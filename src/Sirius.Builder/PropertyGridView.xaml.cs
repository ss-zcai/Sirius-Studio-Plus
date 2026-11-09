using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sirius.Builder;

/// <summary>Picks a property row's editor by its kind.</summary>
public sealed class PropertyEditorSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }

    public DataTemplate? Choice { get; set; }

    public DataTemplate? Check { get; set; }

    public DataTemplate? File { get; set; }

    public DataTemplate? ReadOnly { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (item as PropertyRow)?.Editor switch
        {
            PropertyEditor.Text => Text,
            PropertyEditor.Choice => Choice,
            PropertyEditor.Check => Check,
            PropertyEditor.File => File,
            _ => ReadOnly,
        };
}

/// <summary>
/// A property grid for <see cref="PropertyCategory"/> rows: names on the
/// left, editors on the right, the row in hand described underneath.
/// </summary>
public partial class PropertyGridView : UserControl
{
    public static readonly DependencyProperty CategoriesProperty =
        DependencyProperty.Register(nameof(Categories), typeof(IEnumerable), typeof(PropertyGridView), new PropertyMetadata(null, OnCategoriesChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PropertyGridView));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PropertyGridView));

    public PropertyGridView()
    {
        InitializeComponent();
    }

    public IEnumerable? Categories
    {
        get => (IEnumerable?)GetValue(CategoriesProperty);
        set => SetValue(CategoriesProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    private static void OnCategoriesChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) =>
        ((PropertyGridView)owner).Describe(null);

    private void OnRowFocused(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PropertyRow row })
            Describe(row);
    }

    private void Describe(PropertyRow? row)
    {
        DescriptionName.Text = row?.Name ?? string.Empty;
        DescriptionText.Text = row?.Description ?? string.Empty;
    }

    /// <summary>Enter commits the box; Escape puts back what the property holds.</summary>
    private void OnTextKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        var binding = box.GetBindingExpression(TextBox.TextProperty);

        if (e.Key == Key.Enter)
        {
            binding?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            binding?.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }
}
