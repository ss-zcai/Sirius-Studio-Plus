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

    /// <summary>
    /// What was chosen before the list was narrowed, held in case the
    /// search hides it and then ends without anything being picked.
    /// </summary>
    private object? _chosen;

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
    /// types. Spaces in the name are ignored too, so "bgr2gray" still finds
    /// it shown as "Bgr 2 Gray".
    /// </remarks>
    public static bool Matches(string? candidate, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (string.IsNullOrEmpty(candidate))
            return false;

        var compact = candidate.Replace(" ", string.Empty, StringComparison.Ordinal);

        foreach (var term in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                compact.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
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
        if (string.IsNullOrWhiteSpace(text))
        {
            Items.Filter = null;
            Restore();
            return;
        }

        // Taken before the list narrows, because narrowing it past the
        // chosen value is what makes the ComboBox drop the selection.
        _chosen ??= SelectedItem;

        Items.Filter = item => Matches(item?.ToString(), text);
    }

    /// <summary>
    /// Puts back the value the search hid, if the search did not end in
    /// a new one being picked.
    /// </summary>
    private void Restore()
    {
        if (_chosen is not null && SelectedItem is null)
            SelectedItem = _chosen;

        _chosen = null;
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        // Choosing from the list also writes to the text box; only what a
        // person types should narrow it.
        if (_settingText || !IsEditable || _editor is null || !_editor.IsKeyboardFocusWithin)
            return;

        Typed(_editor.Text, _editor.CaretIndex);
        IsDropDownOpen = true;
    }

    /// <summary>
    /// Narrows the list to what has been typed, and makes sure that is
    /// still what the box says afterwards.
    /// </summary>
    /// <remarks>
    /// Separate from the event so it can be driven directly. Deciding
    /// that a person typed this means asking whether the text box has
    /// keyboard focus, and a window that is never shown never has it.
    /// </remarks>
    internal void Typed(string typed, int caret)
    {
        _settingText = true;

        try
        {
            Filter(typed);

            if (_editor is null)
                return;

            // Narrowing the list refreshes it, and a ComboBox answers a
            // refresh by writing its selected item back over the box.
            // Left alone, the second keystroke of a search replaced the
            // search with the value already chosen.
            if (!string.Equals(_editor.Text, typed, StringComparison.Ordinal))
                _editor.Text = typed;

            _editor.CaretIndex = Math.Min(caret, typed.Length);
        }
        finally
        {
            _settingText = false;
        }
    }

    /// <summary>A real pick replaces whatever the search was holding.</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        if (SelectedItem is not null)
            _chosen = null;

        base.OnSelectionChanged(e);
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
            Restore();

            if (_editor is not null)
                _editor.Text = SelectedItem?.ToString() ?? string.Empty;
        }
        finally
        {
            _settingText = false;
        }
    }
}
