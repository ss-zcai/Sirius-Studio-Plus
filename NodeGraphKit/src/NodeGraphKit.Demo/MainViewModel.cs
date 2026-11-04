using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NodeGraphKit.Model;
using NodeGraphKit.ViewModels;

namespace NodeGraphKit.Demo;

/// <summary>
/// The demo shell: files, the toolbar's colour picker, and "run". The
/// editing itself is all in <see cref="GraphEditorViewModel"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const string FileFilter = "Node graphs (*.graph.json)|*.graph.json|All files (*.*)|*.*";

    private string? _path;
    private string _statusText = "Ready";

    public MainViewModel()
    {
        var catalog = SampleNodes.Catalog();

        Editor = new GraphEditorViewModel(catalog);
        Palette = new PaletteViewModel(catalog);

        NewCommand = new RelayCommand(() => { if (ConfirmDiscard()) New(); });
        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(() => Save(_path));
        SaveAsCommand = new RelayCommand(() => Save(null));
        RunCommand = new RelayCommand(Run);

        // Re-run whenever the graph changes, so the status dots are live -
        // the way the vision application re-inspects on every edit.
        Editor.GraphChanged += (_, _) =>
        {
            Run();
            OnPropertyChanged(nameof(SelectedAccentOption));
        };
        Editor.PropertyChanged += OnEditorPropertyChanged;

        Editor.Load(SampleNodes.StarterGraph(catalog));
    }

    public GraphEditorViewModel Editor { get; }

    public PaletteViewModel Palette { get; }

    public IRelayCommand NewCommand { get; }

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand SaveAsCommand { get; }

    public IRelayCommand RunCommand { get; }

    public string Title =>
        $"{(_path is null ? "Untitled" : Path.GetFileName(_path))}{(Editor.IsDirty ? " *" : "")} - Node Graph Demo";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IReadOnlyList<NodeAccentOption> AccentOptions => NodeAccents.All;

    /// <summary>
    /// The toolbar's colour picker: shows the selection's colour, and
    /// colours the whole selection when changed.
    /// </summary>
    public NodeAccentOption? SelectedAccentOption
    {
        get => Editor.SelectedNode is { } node ? NodeAccents.Of(node.Accent) : null;
        set
        {
            if (value is not null)
                Editor.SetAccentCommand.Execute(value.Accent);
        }
    }

    /// <summary>Asks before throwing away unsaved changes.</summary>
    public bool ConfirmDiscard()
    {
        if (!Editor.IsDirty)
            return true;

        var answer = MessageBox.Show(
            "Save the changes to this graph first?", "Node Graph Demo",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => Save(_path),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void New()
    {
        Editor.New();
        _path = null;
        OnPropertyChanged(nameof(Title));
        StatusText = "New graph";
    }

    private void Open()
    {
        if (!ConfirmDiscard())
            return;

        var dialog = new OpenFileDialog { Filter = FileFilter };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var warnings = Editor.Load(File.ReadAllText(dialog.FileName));
            _path = dialog.FileName;
            OnPropertyChanged(nameof(Title));

            if (warnings.Count > 0)
                MessageBox.Show(string.Join(Environment.NewLine, warnings), "Opened with changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Could not open the graph", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool Save(string? path)
    {
        if (path is null)
        {
            var dialog = new SaveFileDialog { Filter = FileFilter, FileName = "graph.graph.json" };
            if (dialog.ShowDialog() != true)
                return false;

            path = dialog.FileName;
        }

        try
        {
            File.WriteAllText(path, Editor.Save());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Could not save the graph", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _path = path;
        Editor.History.MarkSaved();
        OnPropertyChanged(nameof(Title));
        StatusText = $"Saved {Path.GetFileName(path)}";
        return true;
    }

    /// <summary>Where the real evaluation would go; see <see cref="SampleRunner"/>.</summary>
    private void Run()
    {
        var (ready, warnings) = SampleRunner.Run(Editor.Graph);
        StatusText = $"{Editor.Graph.Nodes.Count} nodes, {Editor.Graph.Connections.Count} wires  ·  " +
                     $"{ready} ready, {warnings} with unconnected inputs";
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GraphEditorViewModel.IsDirty):
                OnPropertyChanged(nameof(Title));
                break;

            case nameof(GraphEditorViewModel.SelectedNode):
                OnPropertyChanged(nameof(SelectedAccentOption));
                break;
        }
    }
}
