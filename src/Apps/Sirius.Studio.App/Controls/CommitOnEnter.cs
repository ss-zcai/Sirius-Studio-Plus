using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sirius.Studio.App.Controls;

/// <summary>
/// Makes Enter commit a text box, and Escape put back what was there.
/// </summary>
/// <remarks>
/// <para>
/// Parameter bindings update on lost focus, so that a half-typed number never
/// reaches the graph and re-runs it on nonsense. The cost is that a value
/// typed and left alone does nothing until focus moves somewhere else, which
/// reads as the application ignoring you.
/// </para>
/// <para>
/// Enter is the obvious way to say "that is the value". This attaches to the
/// implicit TextBox style, so every parameter editor gets it without each
/// binding being written twice.
/// </para>
/// <para>
/// It deliberately does <b>not</b> mark the key handled. The node-name editor
/// has its own Enter handling - commit, then close the editor - and a
/// tunnelling handler that swallowed the key would leave the editor open
/// forever. Committing twice is harmless; swallowing is not.
/// </para>
/// </remarks>
public static class CommitOnEnter
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(CommitOnEnter),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.PreviewKeyDown -= OnPreviewKeyDown;

        if (e.NewValue is true)
            box.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox box)
            Apply(box, e.Key);
    }

    /// <summary>
    /// The whole of the behaviour, separated from the event so it can be
    /// tested without a window to raise keystrokes into.
    /// </summary>
    internal static void Apply(TextBox box, Key key)
    {
        // A box that takes newlines means them, so Enter belongs to the text.
        if (box.AcceptsReturn && key == Key.Enter)
            return;

        if (box.GetBindingExpression(TextBox.TextProperty) is not { } binding)
            return;

        switch (key)
        {
            case Key.Enter:
                binding.UpdateSource();
                break;

            case Key.Escape:
                binding.UpdateTarget();
                break;
        }
    }
}
