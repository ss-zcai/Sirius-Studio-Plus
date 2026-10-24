using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sirius.Studio.App;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// The shell's own message dialog.
/// </summary>
/// <remarks>
/// The stock <see cref="MessageBox"/> survived this long because it gets the
/// keyboard right for free - a default button, Escape to cancel, access keys
/// - and replacing it meant taking that on. So that is what these check:
/// not that it is the right colour, which is visible, but that it can still
/// be answered without a mouse, which is not.
/// </remarks>
[Collection(WpfCollection.Name)]
public class MessageDialogTests
{
    public MessageDialogTests(WpfTestHost host) => _ = host;

    /// <summary>
    /// Builds a dialog without showing it, and hands back its buttons.
    /// </summary>
    /// <remarks>
    /// Not shown, because a modal in a test is the thing this whole area is
    /// trying to stop. The arrangement is what is being checked, and it is
    /// decided before the window opens.
    /// </remarks>
    private static (Button Affirm, Button Deny, Button Cancel) Arrange(MessageBoxButton buttons)
    {
        var window = Create(buttons);

        return (
            (Button)window.FindName("AffirmButton")!,
            (Button)window.FindName("DenyButton")!,
            (Button)window.FindName("CancelButton")!);
    }

    private static Window Create(MessageBoxButton buttons)
    {
        WpfTestHost.EnsureApplicationResources();

        var type = typeof(MessageWindow);
        var window = (Window)Activator.CreateInstance(type, nonPublic: true)!;

        type.GetMethod("Paint", Private)!.Invoke(window, [MessageBoxImage.Question]);
        type.GetMethod("Arrange", Private)!.Invoke(window, [buttons]);

        return window;
    }

    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    [Fact]
    public void A_question_offers_yes_no_and_cancel() =>
        WpfTestHost.OnStaThread(() =>
        {
            var (affirm, deny, cancel) = Arrange(MessageBoxButton.YesNoCancel);

            Assert.Equal("_Yes", affirm.Content);
            Assert.Equal("_No", deny.Content);
            Assert.Equal("_Cancel", cancel.Content);

            Assert.All(
                new[] { affirm, deny, cancel },
                button => Assert.Equal(Visibility.Visible, button.Visibility));
        });

    /// <summary>
    /// A report has one button, and that button answers both Enter and
    /// Escape - there is nothing else it could mean.
    /// </summary>
    [Fact]
    public void A_report_has_one_button_that_answers_everything() =>
        WpfTestHost.OnStaThread(() =>
        {
            var (affirm, deny, cancel) = Arrange(MessageBoxButton.OK);

            Assert.Equal("_OK", affirm.Content);
            Assert.True(affirm.IsDefault);
            Assert.True(affirm.IsCancel);

            Assert.Equal(Visibility.Collapsed, deny.Visibility);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
        });

    /// <summary>
    /// Escape takes the way out that changes nothing: Cancel where there is
    /// one, No where there is not.
    /// </summary>
    [Theory]
    [InlineData(MessageBoxButton.YesNoCancel, "_Cancel")]
    [InlineData(MessageBoxButton.OKCancel, "_Cancel")]
    [InlineData(MessageBoxButton.YesNo, "_No")]
    [InlineData(MessageBoxButton.OK, "_OK")]
    public void Escape_takes_the_safe_way_out(MessageBoxButton buttons, string expected) =>
        WpfTestHost.OnStaThread(() =>
        {
            var (affirm, deny, cancel) = Arrange(buttons);

            var escape = new[] { affirm, deny, cancel }
                .Where(button => button.Visibility == Visibility.Visible && button.IsCancel)
                .ToArray();

            Assert.Equal(expected, Assert.Single(escape).Content);
        });

    /// <summary>
    /// Enter takes the affirmative, and there is exactly one of those -
    /// two default buttons is ambiguous and WPF picks by tree order.
    /// </summary>
    [Theory]
    [InlineData(MessageBoxButton.YesNoCancel)]
    [InlineData(MessageBoxButton.OKCancel)]
    [InlineData(MessageBoxButton.YesNo)]
    [InlineData(MessageBoxButton.OK)]
    public void There_is_exactly_one_default_button(MessageBoxButton buttons) =>
        WpfTestHost.OnStaThread(() =>
        {
            var (affirm, deny, cancel) = Arrange(buttons);

            var defaults = new[] { affirm, deny, cancel }
                .Count(button => button.Visibility == Visibility.Visible && button.IsDefault);

            Assert.Equal(1, defaults);
        });

    /// <summary>
    /// Every visible button offers an access key in its caption.
    /// </summary>
    [Theory]
    [InlineData(MessageBoxButton.YesNoCancel)]
    [InlineData(MessageBoxButton.OKCancel)]
    [InlineData(MessageBoxButton.YesNo)]
    [InlineData(MessageBoxButton.OK)]
    public void Every_button_offers_an_access_key(MessageBoxButton buttons) =>
        WpfTestHost.OnStaThread(() =>
        {
            var (affirm, deny, cancel) = Arrange(buttons);

            foreach (var button in new[] { affirm, deny, cancel })
            {
                if (button.Visibility != Visibility.Visible)
                    continue;

                Assert.Contains("_", (string)button.Content, StringComparison.Ordinal);
            }
        });

    /// <summary>
    /// And the theme actually honours it.
    /// </summary>
    /// <remarks>
    /// The check above passed while the feature was entirely absent, which
    /// is the more useful lesson here: the caption said "_Yes", the button
    /// drew "_Yes" with the underscore visible, and Alt+Y did nothing. A
    /// hand-written Button template defaults <c>RecognizesAccessKey</c> to
    /// false where the stock one sets it, so asserting on the caption was
    /// asserting on the half that was never broken.
    /// </remarks>
    [Fact]
    public void The_button_template_honours_access_keys()
    {
        var theme = File.ReadAllText(Path.Combine(Repository.AppSource, "Themes", "Dark.xaml"));
        var style = theme.IndexOf("<Style TargetType=\"Button\">", StringComparison.Ordinal);

        Assert.True(style >= 0, "the Button style has gone");

        var template = theme[style..theme.IndexOf("</Style>", style, StringComparison.Ordinal)];

        Assert.Contains("RecognizesAccessKey=\"True\"", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare letter answers too, as the stock dialog does - the first
    /// thing anybody tries is N, without reaching for Alt.
    /// </summary>
    [Theory]
    [InlineData(MessageBoxButton.YesNoCancel, Key.Y, "_Yes")]
    [InlineData(MessageBoxButton.YesNoCancel, Key.N, "_No")]
    [InlineData(MessageBoxButton.YesNoCancel, Key.C, "_Cancel")]
    [InlineData(MessageBoxButton.YesNo, Key.N, "_No")]
    [InlineData(MessageBoxButton.OKCancel, Key.O, "_OK")]
    [InlineData(MessageBoxButton.OKCancel, Key.C, "_Cancel")]
    [InlineData(MessageBoxButton.OK, Key.O, "_OK")]
    public void A_bare_letter_answers(MessageBoxButton buttons, Key key, string expected) =>
        WpfTestHost.OnStaThread(() =>
        {
            var window = (MessageWindow)Create(buttons);

            var pressed = window.ResolveKey(key, ModifierKeys.None);

            Assert.NotNull(pressed);
            Assert.Equal(expected, pressed!.Content);
        });

    /// <summary>
    /// And only for a button this dialog actually has. N on an OK-only
    /// report must do nothing rather than quietly answering something that
    /// is not on screen.
    /// </summary>
    [Theory]
    [InlineData(MessageBoxButton.OK, Key.N)]
    [InlineData(MessageBoxButton.OK, Key.C)]
    [InlineData(MessageBoxButton.OK, Key.Y)]
    [InlineData(MessageBoxButton.YesNo, Key.C)]
    [InlineData(MessageBoxButton.YesNo, Key.O)]
    [InlineData(MessageBoxButton.OKCancel, Key.Y)]
    [InlineData(MessageBoxButton.YesNoCancel, Key.O)]
    [InlineData(MessageBoxButton.YesNoCancel, Key.Z)]
    public void A_letter_that_is_not_offered_does_nothing(MessageBoxButton buttons, Key key) =>
        WpfTestHost.OnStaThread(() =>
        {
            var window = (MessageWindow)Create(buttons);

            Assert.Null(window.ResolveKey(key, ModifierKeys.None));
        });

    /// <summary>
    /// A modifier means it is a shortcut for something else - Ctrl+C is a
    /// copy, not a cancel.
    /// </summary>
    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Shift)]
    public void A_modified_letter_does_nothing(ModifierKeys modifiers) =>
        WpfTestHost.OnStaThread(() =>
        {
            var window = (MessageWindow)Create(MessageBoxButton.YesNoCancel);

            Assert.Null(window.ResolveKey(Key.C, modifiers));
        });

    // ---- the stock dialog is gone -------------------------------------------

    /// <summary>
    /// Nothing opens a stock MessageBox any more, with one deliberate
    /// exception.
    /// </summary>
    /// <remarks>
    /// The unhandled-exception handler keeps it as a fallback behind the
    /// themed one: it runs when something has already gone wrong, and a
    /// themed window needs the theme's resources, which may be exactly what
    /// failed. Losing the crash report would be worse than an ugly one.
    /// </remarks>
    [Fact]
    public void Only_the_crash_handler_still_has_a_stock_dialog()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Repository.AppSource, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) is "App.xaml.cs" or "Dialog.cs" or "MessageWindow.xaml.cs")
                continue;

            var text = File.ReadAllText(file);

            // Ignore prose: several files explain why the stock one went.
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains("MessageBox.Show", StringComparison.Ordinal) &&
                    !line.TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                    !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These open an unthemed dialog:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// And the prompts still exist - replacing them with nothing would also
    /// pass the test above, while quietly discarding people's work.
    /// </summary>
    [Fact]
    public void The_unsaved_changes_prompt_is_still_asked()
    {
        var text = File.ReadAllText(Path.Combine(Repository.AppSource, "ViewModels", "MainViewModel.cs"));

        Assert.Contains("Dialog.Ask", text, StringComparison.Ordinal);
        Assert.Contains("ConfirmDiscard", text, StringComparison.Ordinal);
    }
}
