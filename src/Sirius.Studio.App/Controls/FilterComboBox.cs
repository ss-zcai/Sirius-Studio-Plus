using System.Windows;
using System.Windows.Controls;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// A combo box whose list narrows to what has been typed into it.
/// </summary>
/// <remarks>
/// <para>
/// For the lists that are too long to read. OpenCV's colour conversions
/// run to about a hundred entries, and finding Bgr2Hsv in them means
/// scrolling past every other pair - the name is known, so typing it
/// should be enough.
/// </para>
/// <para>
/// WPF's own type-ahead matches a prefix and only moves the highlight, so
/// "hsv" finds nothing in a list where every name begins with a source
/// space. This matches anywhere in the name, and hides what does not
/// match rather than scrolling to it.
/// </para>
/// </remarks>
public sealed class FilterComboBox : ComboBox
{
    private TextBox? _editor;

    /// <summary>Set while the control is editing its own text.</summary>
    private bool _settingText;

    static FilterComboBox() =>
        // Implicit styles are keyed on the exact type, so without this the
        // control would be themed as a plain ComboBox is not - that is, not
        // at all, and it would arrive in Windows' own grey.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FilterComboBox), new FrameworkPropertyMetadata(typeof(ComboBox)));

    /// <summary>
    /// Whether <paramref name="text"/> is what somebody looking for
    /// <paramref name="candidate"/> might have typed.
    /// </summary>
    /// <remarks>
    /// Every whitespace-separated term has to appear somewhere in the
    /// name, in any order: "gray bgr" finds Bgr2Gray, and so does "2gray".
    /// Case is ignored, since the names are written in a casing nobody
    /// types.
    /// </remarks>
    public static bool Matches(string? candidate, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (string.IsNullOrEmpty(candidate))
            return false;

        foreach (var term in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        return true;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_editor is not null)
            _editor.TextChanged -= OnEditorTextChanged;

        _editor = GetTemplateChild("PART_EditableTextBox") as TextBox;

        if (_editor is not null)
            _editor.TextChanged += OnEditorTextChanged;
    }

    /// <summary>Narrows the list, or restores it when the text is cleared.</summary>
    internal void Filter(string? text)
    {
        // The chosen value always stays in the list. Filtering it out makes
        // the ComboBox drop the selection, so a search that happened to
        // exclude the current value would quietly clear the parameter.
        var selected = SelectedItem;

        Items.Filter = string.IsNullOrWhiteSpace(text)
            ? null
            : item => ReferenceEquals(item, selected) ||
                      Equals(item, selected) ||
                      Matches(item?.ToString(), text);
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        // Choosing from the list also writes to the text box; only what a
        // person types should narrow it.
        if (_settingText || !IsEditable || _editor is null || !_editor.IsKeyboardFocusWithin)
            return;

        Filter(_editor.Text);
        IsDropDownOpen = true;
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        ShowEverything();
        base.OnDropDownClosed(e);
    }

    protected override void OnLostKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        // Half-typed text left in the box would read as the value, and is
        // not one. What the parameter actually holds goes back in.
        ShowEverything();
        base.OnLostKeyboardFocus(e);
    }

    private void ShowEverything()
    {
        _settingText = true;

        try
        {
            Items.Filter = null;

            if (_editor is not null)
                _editor.Text = SelectedItem?.ToString() ?? string.Empty;
        }
        finally
        {
            _settingText = false;
        }
    }
}
