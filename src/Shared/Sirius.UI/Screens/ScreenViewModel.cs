using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Projects;
using Sirius.Core.Runtime;
using Sirius.Core.Screens;
using Sirius.Plc.Workflows;
using Sirius.UI.ViewModels;

namespace Sirius.UI.Screens;

/// <summary>Opens and closes a running project's screens - what an Open Screen or Close button asks.</summary>
public interface IScreenNavigator
{
    /// <summary>Shows the screen called <paramref name="name"/>: a page in place of the one shown, a popup over it.</summary>
    /// <returns>Whether there is such a screen.</returns>
    bool Open(string? name);

    /// <summary>Closes the popup, or - with none open - goes back to the page shown before.</summary>
    void Close();
}

/// <summary>Who is logged in, and asking for a login: what a widget needs of the screen it is on (F2).</summary>
public interface IScreenAccess
{
    UserSession Session { get; }

    /// <summary>
    /// Whether what needs <paramref name="needed"/> may be done - asking for
    /// a login first when it may not.
    /// </summary>
    /// <param name="what">What asks: a button's caption, a setting's label.</param>
    Task<bool> EnsureAsync(UserLevel needed, string? what);

    /// <summary>Logs out, or - no one logged in - asks who.</summary>
    Task LogInOutAsync();

    /// <summary>The level needed to open the screen called <paramref name="name"/>, or null when there is none.</summary>
    UserLevel? NeedsToOpen(string? name);
}

/// <summary>One of a running project's screens: its widgets, and whether it is on view.</summary>
public sealed class ScreenPageViewModel : ObservableObject
{
    private bool _isShown;

    internal ScreenPageViewModel(Screen screen, Func<ScreenPageViewModel, IReadOnlyList<WidgetViewModel>> widgets)
    {
        Screen = screen;
        Widgets = widgets(this);
    }

    public Screen Screen { get; }

    public string Name => Screen.Name;

    /// <summary>A page or a popup; the start screen is a page whatever its file says.</summary>
    public ScreenKind Kind => ScreenSet.IsStart(Screen.Name) ? ScreenKind.Page : Screen.Kind;

    public bool IsPopup => Kind == ScreenKind.Popup;

    public double Width => Screen.Width;

    public double Height => Screen.Height;

    public IReadOnlyList<WidgetViewModel> Widgets { get; }

    /// <summary>
    /// A popup with no Close button of its own is given one in its corner:
    /// on a touch screen with no keyboard it could not be closed otherwise.
    /// </summary>
    public bool NeedsCloseCorner => IsPopup && !Widgets.OfType<ButtonWidgetViewModel>().Any(button => button.Widget.Action == WidgetAction.Close);

    /// <summary>On view: the page shown, or the popup open over it. Only these draw pictures.</summary>
    public bool IsShown
    {
        get => _isShown;
        internal set => SetProperty(ref _isShown, value);
    }

    public override string ToString() => Name;
}

/// <summary>
/// A project's screens, running: every widget bound to the stations as they
/// inspect, one page on view and perhaps a popup over it (runtime MVP step
/// 4; several screens, decision 79).
/// </summary>
/// <remarks>
/// <para>
/// Values follow every inspection, on every screen, so a page brought up
/// shows the latest at once. Pictures follow only on the screens on view,
/// at up to the screen's rate, paced per station as Studio's live display
/// is (<see cref="LiveDisplay"/>): a station inspecting four hundred parts
/// a second must not spend its time drawing them. They are drawn in the
/// station's <see cref="StationRunner.Inspected"/> handler, the one moment
/// its pictures are alive; one skipped while running is drawn when the
/// station stops, from the inspection it stopped on - as are a screen's
/// pictures when it comes on view with its station stopped.
/// </para>
/// <para>
/// Opening a page puts the one shown on a history, which Close on a page
/// goes back through; opening the start screen starts the history again.
/// One popup at a time: a popup opened from a popup takes its place.
/// </para>
/// </remarks>
public sealed class ScreenViewModel : ObservableObject, IDisposable, IScreenNavigator, IScreenAccess
{
    private UserSession _session = UserSession.Open;
    private LoginViewModel? _login;

    private readonly ProjectRuntime _runtime;
    private readonly Dictionary<StationRunner, (LiveDisplay Pacing, bool Undrawn)> _pacing = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _problems = [];
    private readonly Stack<ScreenPageViewModel> _history = new();
    private readonly List<(ScreenPageViewModel Screen, WidgetViewModel Widget)> _all = [];
    private ScreenPageViewModel _page;
    private ScreenPageViewModel? _popup;
    private IReadOnlyList<WorkflowRunner> _controllers = [];

    /// <summary>A screen alone, as a project had before there were several.</summary>
    public ScreenViewModel(Screen screen, ProjectRuntime runtime, string projectFolder, IEnumerable<string>? problems = null)
        : this([screen ?? throw new ArgumentNullException(nameof(screen))], runtime, projectFolder, problems)
    {
    }

    /// <param name="screens">Every screen; the first is shown first.</param>
    public ScreenViewModel(IReadOnlyList<Screen> screens, ProjectRuntime runtime, string projectFolder, IEnumerable<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (screens.Count == 0)
            throw new ArgumentException("A project has at least its start screen.", nameof(screens));

        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        Screens = screens
            .Select(screen => new ScreenPageViewModel(screen, page => screen.Widgets.Select(widget => Create(widget, page, projectFolder)).ToArray()))
            .ToArray();

        foreach (var page in Screens)
            _all.AddRange(page.Widgets.Select(widget => (page, widget)));

        foreach (var (_, widget) in _all)
            widget.Access = this;

        LogInOutCommand = new AsyncRelayCommand(LogInOutAsync);

        _page = Screens[0];
        _page.IsShown = true;

        ClosePopupCommand = new RelayCommand(() => Popup = null, () => _popup is not null);

        _problems.AddRange(problems ?? []);
        _problems.AddRange(ScreenSet.Problems(screens, runtime.Stations));
        _problems.AddRange(runtime.Problems);

        foreach (var station in runtime.Stations)
        {
            _pacing[station] = (new LiveDisplay(0), false);
            station.Inspected = snapshot => OnInspected(station, snapshot);
            station.Breathe = Breathe;
            station.PropertyChanged += OnStationChanged;
            Refresh(station);
        }
    }

    /// <summary>Every screen of the project, the start screen first.</summary>
    public IReadOnlyList<ScreenPageViewModel> Screens { get; }

    /// <summary>The page on view.</summary>
    public ScreenPageViewModel Page
    {
        get => _page;
        private set
        {
            if (ReferenceEquals(_page, value))
                return;

            _page.IsShown = false;
            _page = value;
            _page.IsShown = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(Screen));
            OnPropertyChanged(nameof(Widgets));
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
            OnPropertyChanged(nameof(CanGoBack));
            DrawStopped(value);
        }
    }

    /// <summary>The popup open over the page, or null.</summary>
    public ScreenPageViewModel? Popup
    {
        get => _popup;
        private set
        {
            if (ReferenceEquals(_popup, value))
                return;

            if (_popup is not null)
                _popup.IsShown = false;

            _popup = value;

            if (_popup is not null)
                _popup.IsShown = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPopup));
            OnPropertyChanged(nameof(IsPageEnabled));
            ClosePopupCommand.NotifyCanExecuteChanged();

            if (value is not null)
                DrawStopped(value);
        }
    }

    public bool HasPopup => _popup is not null;

    /// <summary>The page takes input only with no popup over it.</summary>
    public bool IsPageEnabled => _popup is null;

    /// <summary>A page was opened from another, which Close goes back to.</summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>The × in the corner of a popup with no Close button of its own, and Escape.</summary>
    public IRelayCommand ClosePopupCommand { get; }

    /// <summary>The page on view's screen.</summary>
    public Screen Screen => _page.Screen;

    public double Width => _page.Width;

    public double Height => _page.Height;

    /// <summary>The page on view's widgets.</summary>
    public IReadOnlyList<WidgetViewModel> Widgets => _page.Widgets;

    /// <summary>Every widget of every screen.</summary>
    internal IEnumerable<WidgetViewModel> AllWidgets => _all.Select(entry => entry.Widget);

    /// <summary>The screen called <paramref name="name"/>, or null.</summary>
    public ScreenPageViewModel? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Screens.FirstOrDefault(screen => string.Equals(screen.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool Open(string? name)
    {
        if (Find(name) is not { } screen)
            return false;

        // Locked: the button asked for a login first; anything else is refused.
        if (!_session.Allows(ScreenLayout.NeedsFor(screen.Screen)))
            return false;

        if (screen.IsPopup)
        {
            Popup = screen;
            return true;
        }

        // A page: over any popup, and onto the history unless it is home.
        Popup = null;

        if (ReferenceEquals(screen, _page))
            return true;

        if (ReferenceEquals(screen, Screens[0]))
            _history.Clear();
        else
            _history.Push(_page);

        Page = screen;
        OnPropertyChanged(nameof(CanGoBack));
        return true;
    }

    public void Close()
    {
        if (_popup is not null)
        {
            Popup = null;
            return;
        }

        if (_history.Count > 0)
            Page = _history.Pop();
    }

    /// <summary>
    /// Stations that would not load, names a screen cannot find, and
    /// pictures not being saved - shown along the bottom.
    /// </summary>
    public IReadOnlyList<string> Problems =>
    [
        .. _problems,
        .. _runtime.Stations.Where(station => station.SavingProblem is not null)
            .Select(station => $"{station.Name}: {station.SavingProblem}"),
    ];

    public bool HasProblems => Problems.Count > 0;

    public string ProblemText => string.Join("   ·   ", Problems);

    private WidgetViewModel Create(Widget widget, ScreenPageViewModel page, string projectFolder) => widget.Kind switch
    {
        WidgetKind.Image => new ImageWidgetViewModel(widget),
        WidgetKind.Verdict => new VerdictWidgetViewModel(widget),
        WidgetKind.Value => new ValueWidgetViewModel(widget),
        WidgetKind.Setting => new SettingWidgetViewModel(widget, _runtime),
        WidgetKind.Counter => new CounterWidgetViewModel(widget),
        WidgetKind.Button => new ButtonWidgetViewModel(widget, _runtime, this, page.Kind),
        WidgetKind.Logo => new LogoWidgetViewModel(widget, projectFolder),
        WidgetKind.Controller => new ControllerWidgetViewModel(widget),
        _ => new TextWidgetViewModel(widget),
    };

    /// <summary>
    /// Between inspections: back once the window has drawn, answered input
    /// and done its background work - so a running station never freezes
    /// the screen, however fast its recipe.
    /// </summary>
    private static async Task Breathe() =>
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

    private Task OnInspected(StationRunner station, InspectionSnapshot snapshot)
    {
        var (pacing, _) = _pacing[station];
        var now = _clock.Elapsed.TotalSeconds;

        pacing.Inspected();

        // A single trigger is always drawn; a run, at the screen's pace.
        var draw = station.State != StationState.Running || pacing.IsDue(now);

        foreach (var (screen, widget) in _all)
        {
            if (widget.Follows(station))
                widget.Inspected(station, snapshot, draw && screen.IsShown);
        }

        if (draw)
            pacing.Shown(now, _clock.Elapsed.TotalSeconds);

        _pacing[station] = (pacing, !draw);
        return Task.CompletedTask;
    }

    private void OnStationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StationRunner station)
            return;

        Refresh(station);

        if (e.PropertyName == nameof(StationRunner.SavingProblem))
        {
            OnPropertyChanged(nameof(Problems));
            OnPropertyChanged(nameof(HasProblems));
            OnPropertyChanged(nameof(ProblemText));
        }

        // Stopped on a frame that was not drawn: draw it now. It is still
        // the station's latest, so still alive.
        if (e.PropertyName == nameof(StationRunner.State) &&
            station.State == StationState.Ready &&
            _pacing[station] is { Undrawn: true } paced &&
            station.Last is { } last)
        {
            foreach (var (screen, widget) in _all)
            {
                if (screen.IsShown && widget is ImageWidgetViewModel && widget.Follows(station))
                    widget.Inspected(station, last, drawPictures: true);
            }

            _pacing[station] = (paced.Pacing, false);
        }
    }

    /// <summary>
    /// A screen come on view: its pictures from each stopped station's last
    /// inspection, which is alive until the next. A running station's come
    /// with its next drawn inspection, a moment later.
    /// </summary>
    private void DrawStopped(ScreenPageViewModel screen)
    {
        foreach (var widget in screen.Widgets.OfType<ImageWidgetViewModel>())
        {
            if (widget.Station is { } name && _runtime.Find(name) is { State: StationState.Ready, Last: { } last } station)
                widget.Inspected(station, last, drawPictures: true);
        }
    }

    private void Refresh(StationRunner station)
    {
        foreach (var (_, widget) in _all)
        {
            if (widget.Follows(station))
                widget.StationChanged(station);
        }

        foreach (var button in AllWidgets.OfType<ButtonWidgetViewModel>())
            button.Refresh();
    }

    // ---- who is logged in (F2) ----------------------------------------------

    /// <summary>Who is logged in. Nothing locked until <see cref="UseSession"/> gives the project's.</summary>
    public UserSession Session => _session;

    /// <summary>The login popup on view, or null.</summary>
    public LoginViewModel? Login
    {
        get => _login;
        private set
        {
            if (SetProperty(ref _login, value))
                OnPropertyChanged(nameof(HasLogin));
        }
    }

    public bool HasLogin => _login is not null;

    /// <summary>The status line's button: log in, or out.</summary>
    public IAsyncRelayCommand LogInOutCommand { get; }

    /// <summary>The project's users and their levels, in force from now.</summary>
    public void UseSession(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        _session.PropertyChanged += OnSessionChanged;

        OnPropertyChanged(nameof(Session));
        SessionChanged();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserSession.User))
            SessionChanged();
    }

    /// <summary>
    /// What is locked follows; and a screen the level in force may not open
    /// - an engineer's popup, left as they logged out - is closed.
    /// </summary>
    private void SessionChanged()
    {
        foreach (var (_, widget) in _all)
            widget.AccessChanged();

        if (_popup is { } popup && !_session.Allows(ScreenLayout.NeedsFor(popup.Screen)))
            Popup = null;

        if (!_session.Allows(ScreenLayout.NeedsFor(_page.Screen)))
        {
            _history.Clear();
            Page = Screens[0];
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    public async Task<bool> EnsureAsync(UserLevel needed, string? what)
    {
        if (_session.Allows(needed))
            return true;

        return await AskAsync(new LoginViewModel(_session, needed, what)).ConfigureAwait(true) && _session.Allows(needed);
    }

    public async Task LogInOutAsync()
    {
        if (_session.IsLoggedIn)
        {
            _session.LogOut();
            return;
        }

        if (_session.IsEnforced)
            await AskAsync(new LoginViewModel(_session)).ConfigureAwait(true);
    }

    public UserLevel? NeedsToOpen(string? name) => Find(name) is { } screen ? ScreenLayout.NeedsFor(screen.Screen) : null;

    /// <summary>Shows the login popup until it is answered; one at a time.</summary>
    private async Task<bool> AskAsync(LoginViewModel login)
    {
        if (_login is { } asking)
            return await asking.Result.ConfigureAwait(true);

        Login = login;

        try
        {
            return await login.Result.ConfigureAwait(true);
        }
        finally
        {
            Login = null;
        }
    }

    // ---- controllers --------------------------------------------------------

    /// <summary>
    /// The project's controllers, whose parts its counters count; a counter
    /// naming one that is not running is said as a problem.
    /// </summary>
    public void UseControllers(IReadOnlyList<WorkflowRunner> controllers)
    {
        _controllers = controllers ?? throw new ArgumentNullException(nameof(controllers));

        var counted = _all
            .Select(entry => entry.Widget)
            .OfType<CounterWidgetViewModel>()
            .Select(counter => counter.Controller)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => Controller(name) is null)
            .Select(name => $"Counter: there is no controller '{name}' running.");

        var shown = _all
            .Select(entry => entry.Widget)
            .OfType<ControllerWidgetViewModel>()
            .Select(widget => widget.Controller)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => Controller(name) is null)
            .Select(name => $"Controller widget: there is no controller '{name}' running.");

        var missing = counted.Concat(shown).ToArray();

        if (missing.Length > 0)
        {
            _problems.AddRange(missing);
            OnPropertyChanged(nameof(HasProblems));
            OnPropertyChanged(nameof(ProblemText));
        }

        ShowControllers();
    }

    /// <summary>Brings each counter of a controller's parts, and each Controller widget, up to date - every half second while running.</summary>
    public void ShowControllers()
    {
        foreach (var counter in _all.Select(entry => entry.Widget).OfType<CounterWidgetViewModel>())
        {
            if (counter.Controller is { } name && Controller(name) is { } runner)
            {
                var lists = runner.Stations is { } stations ? stations.Stations.Select(stations.ResultsOf).ToArray() : [];
                counter.PartsChanged(runner.PartCounts, runner.LastPart, runner.PartResultCounts, lists);
            }
        }

        foreach (var widget in _all.Select(entry => entry.Widget).OfType<ControllerWidgetViewModel>())
            widget.Show(widget.Controller is { } name ? Controller(name) : null);
    }

    /// <summary>Starts every controller's part counts again - Reset Counts for every station.</summary>
    internal void ResetPartCounts()
    {
        foreach (var runner in _controllers)
            runner.ResetPartCounts();

        ShowControllers();
    }

    private WorkflowRunner? Controller(string name) =>
        _controllers.FirstOrDefault(runner => string.Equals(runner.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;

        foreach (var (_, widget) in _all)
            widget.Detach();

        foreach (var station in _runtime.Stations)
        {
            station.PropertyChanged -= OnStationChanged;
            station.Inspected = null;
            station.Breathe = null;
        }
    }
}
