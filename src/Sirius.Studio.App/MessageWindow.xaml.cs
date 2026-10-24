using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Sirius.Studio.App;

/// <summary>
/// The shell's message dialog, in the shell's colours.
/// </summary>
/// <remarks>
/// Answers the same questions <see cref="MessageBox"/> does and returns the
/// same <see cref="MessageBoxResult"/>, so call sites read as they did. Use
/// <see cref="Dialog"/> rather than this directly.
/// </remarks>
public partial class MessageWindow : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    /// <summary>What the left-hand button means - OK on an OK dialog, Yes otherwise.</summary>
    private MessageBoxResult _affirmative = MessageBoxResult.OK;

    private MessageWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    /// <summary>
    /// Shows the dialog and returns which button was pressed.
    /// </summary>
    /// <param name="owner">
    /// The window it belongs to, centred on it and blocking it. Null when
    /// there is no window yet - a failure during startup still has to be
    /// reportable.
    /// </param>
    internal static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        var window = new MessageWindow
        {
            Title = title,
            Owner = ReferenceEquals(owner, null) || !owner.IsLoaded ? null : owner,
        };

        // Centring on nothing puts the dialog in the corner of the primary
        // screen, which looks like a different application opening.
        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.MessageText.Text = message;
        window.Paint(icon);
        window.Arrange(buttons);

        window.ShowDialog();

        return window._result;
    }

    /// <summary>
    /// Colours the severity mark. Amber for a question, because a question
    /// is a decision rather than a problem and the red one would read as
    /// something having gone wrong.
    /// </summary>
    private void Paint(MessageBoxImage icon)
    {
        var (glyph, colour) = icon switch
        {
            MessageBoxImage.Error => ("!", "#DB5C5C"),
            MessageBoxImage.Warning => ("!", "#D9A33A"),
            MessageBoxImage.Question => ("?", "#D9A33A"),
            _ => ("i", "#5B8DD6"),
        };

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour)!);
        brush.Freeze();

        GlyphText.Text = glyph;
        GlyphText.Foreground = brush;
        Glyph.BorderBrush = brush;
    }

    /// <summary>
    /// Labels the buttons and decides which answers Enter and which Escape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enter takes the affirmative, Escape the safe way out: Cancel where
    /// there is one, No where there is not, and OK when OK is the only
    /// button there. That is what the stock dialog does, and the muscle
    /// memory is older than this application.
    /// </para>
    /// <para>
    /// Yes/No/Cancel deliberately does <b>not</b> default to Yes. "Save this
    /// project before closing it?" answered by a stray Enter should not pick
    /// the branch that opens a file dialog - but nor should it discard the
    /// work, so Escape cancels and Enter saves, and neither of them loses
    /// anything.
    /// </para>
    /// </remarks>
    private void Arrange(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                _affirmative = MessageBoxResult.OK;
                Set(AffirmButton, "_OK", isDefault: true);
                Hide(DenyButton);
                Set(CancelButton, "_Cancel", isCancel: true);
                break;

            case MessageBoxButton.YesNo:
                _affirmative = MessageBoxResult.Yes;
                Set(AffirmButton, "_Yes", isDefault: true);
                Set(DenyButton, "_No", isCancel: true);
                Hide(CancelButton);
                break;

            case MessageBoxButton.YesNoCancel:
                _affirmative = MessageBoxResult.Yes;
                Set(AffirmButton, "_Yes", isDefault: true);
                Set(DenyButton, "_No");
                Set(CancelButton, "_Cancel", isCancel: true);
                break;

            default:
                _affirmative = MessageBoxResult.OK;
                Set(AffirmButton, "_OK", isDefault: true, isCancel: true);
                Hide(DenyButton);
                Hide(CancelButton);
                break;
        }

        // The default button takes the focus, so Enter works without the
        // operator first having to click somewhere.
        Loaded += (_, _) => AffirmButton.Focus();
    }

    private static void Set(System.Windows.Controls.Button button, string content,
        bool isDefault = false, bool isCancel = false)
    {
        button.Content = content;
        button.IsDefault = isDefault;
        button.IsCancel = isCancel;
        button.Visibility = Visibility.Visible;
    }

    private static void Hide(System.Windows.Controls.Button button) =>
        button.Visibility = Visibility.Collapsed;

    private void OnAffirm(object sender, RoutedEventArgs e) => Answer(_affirmative);

    private void OnDeny(object sender, RoutedEventArgs e) => Answer(MessageBoxResult.No);

    private void OnCancel(object sender, RoutedEventArgs e) => Answer(MessageBoxResult.Cancel);

    /// <summary>
    /// Answers to a bare letter, as the dialog this replaced did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An underscore in a button's caption is an access key, which on
    /// Windows means Alt+Y rather than Y. The stock MessageBox takes the
    /// bare letter as well, and since this stands in its place it would be
    /// a small regression not to - the first thing anybody tries is N.
    /// </para>
    /// <para>
    /// Safe here only because there is nothing on this window to type into.
    /// The same handler on a dialog with a text field would eat the typing.
    /// </para>
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        if (ResolveKey(e.Key, Keyboard.Modifiers) is not { } pressed)
            return;

        pressed.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        e.Handled = true;
    }

    /// <summary>
    /// Which button a bare letter answers, or null for none.
    /// </summary>
    /// <remarks>
    /// Separated from the handler so it can be tested without a shown
    /// window and a live keyboard device. The rules are narrow on purpose:
    /// only a button this dialog actually has, and only the letter that
    /// button's own caption offers - so N does nothing on an OK-only
    /// report, and O does not answer a Yes/No.
    /// </remarks>
    internal System.Windows.Controls.Button? ResolveKey(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.None)
            return null;

        var candidate = key switch
        {
            Key.Y or Key.O => AffirmButton,
            Key.N => DenyButton,
            Key.C => CancelButton,
            _ => null,
        };

        if (candidate is null || candidate.Visibility != Visibility.Visible)
            return null;

        return candidate.Content is string caption &&
               caption.Replace("_", string.Empty)
                   .StartsWith(key.ToString(), StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    /// <summary>
    /// Closing by the title bar's X is a cancel, like dismissing the stock
    /// dialog - never a silent yes.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_result == MessageBoxResult.None)
        {
            _result = CancelButton.Visibility == Visibility.Visible
                ? MessageBoxResult.Cancel
                : DenyButton.Visibility == Visibility.Visible
                    ? MessageBoxResult.No
                    : _affirmative;
        }

        base.OnClosing(e);
    }

    private void Answer(MessageBoxResult result)
    {
        _result = result;
        Close();
    }
}
