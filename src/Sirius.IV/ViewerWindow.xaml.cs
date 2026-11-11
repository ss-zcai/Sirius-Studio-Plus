using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Sirius.Studio.App;

namespace Sirius.IV;

/// <summary>Sirius IV's window.</summary>
public partial class ViewerWindow : Window
{
    public static readonly RoutedUICommand OpenFolderCommand = new("Open Folder", nameof(OpenFolderCommand), typeof(ViewerWindow));

    private readonly BrowserViewModel _model;
    private bool _choosing;

    public ViewerWindow(BrowserViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;

        if (TryFindResource("Icon.SiriusImageViewer") is ImageSource icon)
            Icon = icon;

        // The list's selection is the window's to set; the model says what.
        model.ChooseRequested += (_, chosen) =>
        {
            _choosing = true;

            try
            {
                FileList.SelectedItems.Clear();

                foreach (var item in chosen)
                    FileList.SelectedItems.Add(item);

                if (chosen.Count > 0)
                    FileList.ScrollIntoView(chosen[0]);
            }
            finally
            {
                _choosing = false;
            }
        };
    }

    /// <summary>A folder or picture to open as the window appears - from the command line.</summary>
    public string? StartWith { get; init; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var start = StartWith ?? _model.LastFolder;

        if (start is not null && (Directory.Exists(start) || File.Exists(start)))
            await _model.OpenAsync(start);

        FileList.Focus();
    }

    private void OnFilesSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_choosing)
            _model.Choose(FileList.SelectedItems.OfType<ImageFileItem>());
    }

    private async void OnOpenFolder(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open a Folder of Pictures",
            InitialDirectory = _model.Folder ?? _model.LastFolder ?? string.Empty,
        };

        if (dialog.ShowDialog(this) == true)
            await _model.OpenAsync(dialog.FolderName);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
            await _model.OpenAsync(dropped[0]);
    }

    private void OnShowFiles(object sender, RoutedEventArgs e)
    {
        FilesPanel.Show();
        FilesPanel.IsActive = true;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>Measurements not saved are asked about before they are lost.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_model.ConfirmLeaving())
            e.Cancel = true;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) =>
        new AboutWindow(
            BrowserViewModel.AppTitle,
            TryFindResource("Icon.SiriusImageViewer") as ImageSource,
            "Sirius Image Viewer: the pictures a station saved, with what was found on each, one at a time or side by side.",
            typeof(ViewerWindow).Assembly)
        {
            Owner = this,
        }.ShowDialog();
}
