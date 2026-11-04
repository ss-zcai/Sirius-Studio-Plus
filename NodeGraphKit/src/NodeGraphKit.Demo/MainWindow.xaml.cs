using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Layout;
using CommunityToolkit.Mvvm.Input;
using NodeGraphKit.Controls;
using NodeGraphKit.Model;
using NodeGraphKit.ViewModels;

namespace NodeGraphKit.Demo;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        DataContext = _model;
        Toolbox.DataContext = _model.Palette;
        Graph.DataContext = _model.Editor;
        Properties.DataContext = _model.Editor;

        // A double click or Enter in the toolbox: into the middle of the canvas.
        _model.Palette.AddRequested += (_, definition) => Graph.AddAtCentre(definition);

        // Where a host would open a node's own window or panel; the demo
        // brings the properties forward.
        Graph.NodeDoubleClick += (_, node) =>
        {
            _model.Editor.SelectOnly([node]);
            ShowPanel("properties");
        };

        // A host can add to a node's right-click menu.
        Graph.NodeMenuOpening += (_, e) =>
        {
            e.Menu.Items.Add(new System.Windows.Controls.Separator());

            var bypass = new System.Windows.Controls.MenuItem
            {
                Header = e.Node.Enabled ? "_Bypass" : "_Enable",
            };
            bypass.Click += (_, _) => e.Node.Enabled = !e.Node.Enabled;
            e.Menu.Items.Add(bypass);
        };

        Loaded += (_, _) => Graph.FitToScreen();
    }

    private void OnClosing(object? sender, CancelEventArgs e) =>
        e.Cancel = !_model.ConfirmDiscard();

    /// <summary>A copy made in another window arrives on the clipboard while this one is in the background.</summary>
    private void OnActivated(object? sender, EventArgs e) => _model.Editor.RefreshClipboardState();

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Graph.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Graph.ZoomOut();

    private void OnFitClick(object sender, RoutedEventArgs e) => Graph.FitToScreen();

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "Node Graph UI Kit - demo\n\nThe node editor of Sirius Studio Plus, without the vision algorithms.\n" +
            "Built on Nodify (MIT), CommunityToolkit.Mvvm (MIT) and AvalonDock (Ms-PL); icons from Phosphor (MIT).",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    /// <summary>Ctrl+F: the toolbox's search box.</summary>
    private void OnFindCommand(object sender, ExecutedRoutedEventArgs e)
    {
        ShowPanel("toolbox");
        Toolbox.FocusSearch();
        e.Handled = true;
    }

    private void OnShowPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string contentId })
            ShowPanel(contentId);
    }

    private void ShowPanel(string contentId)
    {
        var anchorable = Docking.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId);

        if (anchorable is null)
            return;

        if (anchorable.IsHidden)
            anchorable.Show();

        anchorable.IsActive = true;
    }

    private void OnEditCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (Resolve(e.Command) is { } command && command.CanExecute(null))
            command.Execute(null);

        e.Handled = true;
    }

    private void OnEditCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = Resolve(e.Command)?.CanExecute(null) == true;
        e.Handled = true;
    }

    private IRelayCommand? Resolve(ICommand command)
    {
        var editor = _model.Editor;

        return command switch
        {
            _ when command == ApplicationCommands.Undo => editor.UndoCommand,
            _ when command == ApplicationCommands.Redo => editor.RedoCommand,
            _ when command == ApplicationCommands.Cut => editor.CutCommand,
            _ when command == ApplicationCommands.Copy => editor.CopyCommand,
            _ when command == ApplicationCommands.Paste => editor.PasteCommand,
            _ when command == ApplicationCommands.SelectAll => editor.SelectAllCommand,
            _ => null,
        };
    }
}
