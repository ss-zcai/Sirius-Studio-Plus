using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Sirius.Studio.App;
using Sirius.Studio.App.Hosting;

namespace Sirius.Builder;

/// <summary>Sirius Builder's window.</summary>
/// <remarks>
/// Laid out as Studio is - menu, toolbar, docking panels - and the panels
/// are put back where they were left, from a layout file of Builder's own.
/// </remarks>
public partial class BuilderWindow : Window
{
    private readonly BuilderViewModel _model;
    private readonly Dictionary<string, object> _panels = [];
    private string? _defaultLayout;

    private Point _toolDownAt;
    private WidgetTool? _pressedTool;

    public BuilderWindow(BuilderViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;
    }

    internal BuilderViewModel Model => _model;

    /// <summary>Where the panel arrangement is kept between sessions.</summary>
    internal static string LayoutPath => Path.Combine(SiriusHost.SettingsFolder, "builder-layout.xml");

    /// <summary>For tests: the layout is neither read nor written.</summary>
    internal bool KeepsLayout { get; set; } = true;

    // ---- the window ----------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var content in Docking.Layout.Descendents().OfType<LayoutContent>())
        {
            if (!string.IsNullOrEmpty(content.ContentId) && content.Content is { } panel)
                _panels[content.ContentId] = panel;
        }

        _defaultLayout = Serialize();

        if (KeepsLayout && File.Exists(LayoutPath))
            Restore(serializer => serializer.Deserialize(LayoutPath));
    }

    /// <summary>Back in front: a recipe may have been saved in Studio meanwhile.</summary>
    private void OnActivated(object? sender, EventArgs e) => _model.Refresh();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_model.ConfirmScreen("Sirius Builder"))
        {
            e.Cancel = true;
            return;
        }

        if (!KeepsLayout)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            new XmlLayoutSerializer(Docking).Serialize(LayoutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Only a convenience.
        }
    }

    // ---- panels ------------------------------------------------------------

    private void OnShowPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string contentId })
            ShowPanel(contentId);
    }

    /// <summary>Brings a panel back, hidden or not, and to the front.</summary>
    internal void ShowPanel(string contentId)
    {
        var anchorable = Docking.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable is null)
            return;

        if (anchorable.IsHidden)
            anchorable.Show();

        anchorable.IsActive = true;
    }

    /// <summary>The panels as they were first laid out.</summary>
    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        if (_defaultLayout is { } layout)
            Restore(serializer => serializer.Deserialize(new StringReader(layout)));
    }

    private string Serialize()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(Docking).Serialize(writer);
        return writer.ToString();
    }

    /// <summary>
    /// Lays the panels out from a saved arrangement, putting each panel's
    /// content back by its id. One from another build is left out; a broken
    /// arrangement, or one missing a panel, gives way to the first layout.
    /// </summary>
    private void Restore(Action<XmlLayoutSerializer> load)
    {
        try
        {
            Load(load);

            if (Docking.Layout.Descendents().OfType<LayoutContent>().Count(content => _panels.ContainsKey(content.ContentId ?? string.Empty)) == _panels.Count)
                return;
        }
        catch (Exception)
        {
            // Falls back below.
        }

        if (_defaultLayout is { } layout)
            Load(serializer => serializer.Deserialize(new StringReader(layout)));
    }

    private void Load(Action<XmlLayoutSerializer> load)
    {
        var serializer = new XmlLayoutSerializer(Docking);
        serializer.LayoutSerializationCallback += OnRestoring;
        load(serializer);
    }

    private void OnRestoring(object? sender, LayoutSerializationCallbackEventArgs args)
    {
        if (args.Model.ContentId is { } id && _panels.TryGetValue(id, out var content))
            args.Content = content;
        else
            args.Cancel = true;
    }

    // ---- stations ----------------------------------------------------------

    private void OnStationDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: StationViewModel station })
            {
                _model.OpenInStudioCommand.Execute(station);
                return;
            }
        }
    }

    // ---- the Widgets panel ---------------------------------------------------

    /// <summary>
    /// Double-click adds the widget; a press is otherwise only the start of a
    /// possible drag. Nothing is added on a single click, so a drag that
    /// ends on the screen adds exactly one.
    /// </summary>
    private void OnWidgetToolMouseDown(object sender, MouseButtonEventArgs e)
    {
        _toolDownAt = e.GetPosition(this);
        _pressedTool = (sender as FrameworkElement)?.DataContext as WidgetTool;

        if (e.ClickCount == 2 && _pressedTool is { } tool && _model.Designer is { } designer)
        {
            designer.Add(tool.Kind);
            _pressedTool = null;
            e.Handled = true;
        }
    }

    private void OnWidgetToolMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedTool is not { } tool)
            return;

        var moved = e.GetPosition(this) - _toolDownAt;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedTool = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ScreenDesignerView.DragFormat, tool.Kind), DragDropEffects.Copy);
    }
}
