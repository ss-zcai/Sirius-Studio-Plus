using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Projects;

namespace Sirius.Studio.App.Screens;

/// <summary>
/// Who is logged in on the running screens (F2), and since when they last
/// touched it: back to Operator after the project's idle minutes.
/// </summary>
/// <remarks>
/// A project with no users enforces nothing: <see cref="Allows"/> is
/// always true, and no one is asked to log in.
/// </remarks>
public sealed class UserSession : ObservableObject
{
    private readonly Project? _project;
    private ProjectUser? _user;
    private DateTime _touched;

    /// <summary>A session for <paramref name="project"/>'s users; null for none - nothing locked.</summary>
    public UserSession(Project? project = null)
    {
        _project = project;
        _touched = Now();
    }

    /// <summary>Nothing locked: a project without users, or a screen run without one.</summary>
    public static UserSession Open => new();

    /// <summary>The clock - for a test to move on.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Whether levels are in force: the project has someone to log in as.</summary>
    public bool IsEnforced => _project is { HasUsers: true };

    /// <summary>Who can log in, by name.</summary>
    public IReadOnlyList<string> Names => _project?.Users.Select(user => user.Name).ToArray() ?? [];

    /// <summary>
    /// Who can do what needs <paramref name="needed"/>, by name: everyone
    /// for null - or when no one could, so the popup is never empty.
    /// </summary>
    public IReadOnlyList<string> NamesFor(UserLevel? needed)
    {
        if (_project is null)
            return [];

        var able = _project.Users.Where(user => needed is not { } level || user.Level >= level).Select(user => user.Name).ToArray();
        return able.Length > 0 ? able : Names;
    }

    /// <summary>Who logged in last, still known once they log out: the popup offers them first.</summary>
    public string? LastName { get; private set; }

    /// <summary>The user logged in, or null - Operator.</summary>
    public ProjectUser? User
    {
        get => _user;
        private set
        {
            if (SetProperty(ref _user, value))
            {
                OnPropertyChanged(nameof(Level));
                OnPropertyChanged(nameof(IsLoggedIn));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ActionText));
            }
        }
    }

    public bool IsLoggedIn => _user is not null;

    /// <summary>The level in force: the user's, or Operator.</summary>
    public UserLevel Level => _user?.Level ?? UserLevel.Operator;

    /// <summary>"Ann · Technician", or "Operator".</summary>
    public string StatusText => _user is { } user ? $"{user.Name} · {user.Level}" : "Operator";

    /// <summary>What the status button does: "Log In", "Log Out".</summary>
    public string ActionText => _user is null ? "Log In" : "Log Out";

    /// <summary>Whether what needs <paramref name="needed"/> may be done now.</summary>
    public bool Allows(UserLevel needed) => !IsEnforced || Level >= needed;

    /// <summary>Logs <paramref name="name"/> in.</summary>
    /// <returns>Why not - no such user, or not their PIN - or null.</returns>
    public string? TryLogIn(string? name, string? pin)
    {
        if (_project is null)
            return "There is no one to log in as.";

        if (string.IsNullOrWhiteSpace(name))
            return "Choose who you are.";

        if (string.IsNullOrEmpty(pin))
            return "Type your PIN.";

        if (_project.LogIn(name, pin) is not { } user)
            return _project.FindUser(name) is null ? $"There is no user called '{name}'." : "That PIN is not right.";

        User = user;
        LastName = user.Name;
        Touch();
        return null;
    }

    public void LogOut() => User = null;

    /// <summary>Someone used the screen: the idle clock starts again.</summary>
    public void Touch() => _touched = Now();

    /// <summary>Logs out when idle for the project's minutes.</summary>
    /// <returns>Whether it did.</returns>
    public bool CheckIdle()
    {
        if (_user is null || _project is not { LogOutMinutes: > 0 } project)
            return false;

        if (Now() - _touched < TimeSpan.FromMinutes(project.LogOutMinutes))
            return false;

        LogOut();
        return true;
    }
}

/// <summary>
/// The login popup: who, from a list of those who may do what asked; their
/// PIN on a keypad; and - when something asked for it - what needs which
/// level.
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    private readonly UserSession _session;
    private readonly UserLevel? _needed;
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _selected;
    private string _pin = string.Empty;
    private string? _problem;

    /// <param name="needed">The level something needs, or null to just log in.</param>
    /// <param name="what">What needs it: "Reset Counts", "Limit", "Settings".</param>
    public LoginViewModel(UserSession session, UserLevel? needed = null, string? what = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _needed = needed;
        Prompt = needed is { } level
            ? $"{(string.IsNullOrWhiteSpace(what) ? "This" : what)} needs {UserLevels.WithArticle(level)} or above."
            : "Who are you?";

        // Only those who could do it: a short list, and no one picked who
        // would be turned away after typing their PIN.
        Names = session.NamesFor(needed);

        // Whoever is in, else whoever was last, else the only one.
        SelectedName = Listed(session.User?.Name) ?? Listed(session.LastName) ?? (Names.Count == 1 ? Names[0] : null);

        DigitCommand = new RelayCommand<string>(Digit);
        BackCommand = new RelayCommand(Back);
        LogInCommand = new RelayCommand(LogIn);
        CancelCommand = new RelayCommand(Cancel);
    }

    public string Prompt { get; }

    /// <summary>The users offered, in the project's order.</summary>
    public IReadOnlyList<string> Names { get; }

    public string? SelectedName
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                Problem = null;
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    /// <summary>No one chosen yet: the list says "Choose who you are".</summary>
    public bool HasNoSelection => _selected is null;

    private string? Listed(string? name) =>
        name is null ? null : Names.FirstOrDefault(listed => string.Equals(listed, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The PIN typed, as dots.</summary>
    public string PinDots => new('●', _pin.Length);

    public string? Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>True when logged in at a level that allows what asked; false when cancelled.</summary>
    public Task<bool> Result => _done.Task;

    public IRelayCommand<string> DigitCommand { get; }

    public IRelayCommand BackCommand { get; }

    public IRelayCommand LogInCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public void Digit(string? digit)
    {
        if (digit is not { Length: 1 } || !char.IsAsciiDigit(digit[0]) || _pin.Length >= Pins.MaxLength)
            return;

        _pin += digit;
        Problem = null;
        OnPropertyChanged(nameof(PinDots));
    }

    public void Back()
    {
        if (_pin.Length == 0)
            return;

        _pin = _pin[..^1];
        OnPropertyChanged(nameof(PinDots));
    }

    public void LogIn()
    {
        if (_session.TryLogIn(SelectedName, _pin) is { } problem)
        {
            Problem = problem;
            ClearPin();
            return;
        }

        ClearPin();

        // Logged in, but below what asked: another may log in, or cancel.
        if (_needed is { } needed && !_session.Allows(needed))
        {
            Problem = $"{_session.User!.Name} is {UserLevels.WithArticle(_session.Level)}: this needs {UserLevels.WithArticle(needed)} or above.";
            return;
        }

        _done.TrySetResult(true);
    }

    public void Cancel()
    {
        ClearPin();
        _done.TrySetResult(false);
    }

    private void ClearPin()
    {
        _pin = string.Empty;
        OnPropertyChanged(nameof(PinDots));
    }
}
