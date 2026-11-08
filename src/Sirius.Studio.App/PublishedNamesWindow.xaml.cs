using System.Windows;
using Sirius.Core.Graph;

namespace Sirius.Studio.App;

/// <summary>Lists, renames and unpublishes a recipe's published names.</summary>
public partial class PublishedNamesWindow : Window
{
    private readonly VisionGraph _graph;

    private PublishedNamesWindow(VisionGraph graph)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _graph = graph;
        Refresh();
    }

    public static void Show(Window? owner, VisionGraph graph)
    {
        var window = new PublishedNamesWindow(graph)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
    }

    /// <summary>One row of the list.</summary>
    public sealed record Row(PublishedItem Item)
    {
        public string Name => Item.Name;

        public string Kind => Item.Kind.ToString();

        public string Source => Item.Source;

        public string State => Item.Found ? string.Empty : "missing";
    }

    private void Refresh(string? select = null)
    {
        var rows = Publishing.In(_graph)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new Row(item))
            .ToArray();

        Items.ItemsSource = rows;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Items.SelectedItem = rows.FirstOrDefault(row => row.Name == select);

        OnSelectionChanged(this, null!);
    }

    private Row? Selected => Items.SelectedItem as Row;

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RenameButton.IsEnabled = Selected is not null;
        UnpublishButton.IsEnabled = Selected is not null;
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected?.Item is not { } item)
            return;

        PublishNameWindow.Ask(this, _graph, item.Node, item.Kind, item.Entry.Member);
        Refresh(Publishing.NameOf(item.Node, item.Kind, item.Entry.Member)?.Name);
    }

    private void OnUnpublish(object sender, RoutedEventArgs e)
    {
        if (Selected?.Item is not { } item)
            return;

        Publishing.Unpublish(item.Node, item.Kind, item.Entry.Member);
        Refresh();
    }
}
