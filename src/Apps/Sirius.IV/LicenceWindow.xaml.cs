using System.Windows;
using System.Windows.Media;
using Sirius.Core.Licensing;
using Sirius.UI;

namespace Sirius.IV;

/// <summary>
/// Sirius IV's licence: how it stands, this PC's machine ID to send for a
/// code, and the code entered. Shown at the start when there is no good
/// licence - nothing else opens until there is - and from Help.
/// </summary>
public partial class LicenceWindow : Window
{
    private readonly LicenceStore _store;

    internal LicenceWindow(LicenceStore store, LicenceState state)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _store = store;
        MachineBox.Text = store.MachineId;
        Show(state);
    }

    /// <summary>How the licence stands, as the window last showed it.</summary>
    internal LicenceState State { get; private set; } = null!;

    /// <summary>Puts text on the clipboard - a test reads it instead.</summary>
    internal Action<string> CopyText { get; set; } = text =>
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    };

    /// <summary>
    /// Shows the window; true when the licence is good as it closes. Without
    /// one, its button quits rather than closes.
    /// </summary>
    public static bool Ask(Window? owner, LicenceStore store, LicenceState state)
    {
        var window = new LicenceWindow(store, state)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            ShowInTaskbar = owner is null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
        return window.State.IsValid;
    }

    private void Show(LicenceState state)
    {
        State = state;
        StatusText.Text = state.Message;
        StatusText.Foreground = state.IsValid
            ? (Brush)FindResource("Text.Primary")
            : new SolidColorBrush(Color.FromRgb(0xDB, 0x5C, 0x5C));
        CloseButton.Content = state.IsValid ? "Close" : "Quit";
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopyText(_store.MachineId);

    private void OnCodeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        EnterButton.IsEnabled = CodeBox.Text.Trim().Length > 0;
        ProblemText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// A good code is kept and the window closes; any other says why, and the
    /// licence stays as it was.
    /// </summary>
    internal void OnEnter(object sender, RoutedEventArgs e)
    {
        var entered = _store.Enter(CodeBox.Text);

        if (entered.IsValid)
        {
            Show(entered);

            if (IsVisible)
                DialogResult = true;

            return;
        }

        ProblemText.Text = entered.Message;
        ProblemText.Visibility = Visibility.Visible;
    }
}
