using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace NodeGraphKit.Controls;

/// <summary>
/// Makes Enter commit a text box, and Escape put back what was there.
/// </summary>
/// <remarks>
/// Bindings in the kit update on lost focus, so a half-typed number never
/// reaches the graph. Enter is how you say the value is finished. The theme
/// switches this on for every TextBox. It does not mark the key handled, so
/// a box with its own Enter handling still sees it.
/// </remarks>
public static class CommitOnEnter
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(CommitOnEnter), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

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
        if (sender is not TextBox box || box.AcceptsReturn && e.Key == Key.Enter)
            return;

        var binding = box.GetBindingExpression(TextBox.TextProperty);

        if (e.Key == Key.Enter)
            binding?.UpdateSource();
        else if (e.Key == Key.Escape)
            binding?.UpdateTarget();
    }
}

/// <summary>
/// Asks Windows to draw a window's title bar dark - the one strip WPF styling
/// cannot reach. Call it from every window's constructor.
/// </summary>
public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!TrySet(window))
            window.SourceInitialized += (_, _) => TrySet(window);
    }

    private static bool TrySet(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
            return false;

        var enabled = 1;

        // The attribute's id changed in Windows 10 20H1; older builds ignore the new one.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

        return true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

internal static class VisualTreeSearch
{
    /// <summary>The nearest element at or above <paramref name="source"/> whose DataContext is a <typeparamref name="T"/>.</summary>
    public static T? DataContextOf<T>(DependencyObject? source) where T : class
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is FrameworkElement { DataContext: T found })
                return found;
        }

        return null;
    }

    /// <summary>Whether an element at or above <paramref name="source"/> carries this Tag.</summary>
    public static bool HasTaggedAncestor(DependencyObject? source, string tag, Type stopAt)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is FrameworkElement element && Equals(element.Tag, tag))
                return true;

            if (stopAt.IsInstanceOfType(current))
                return false;
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
