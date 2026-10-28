using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sirius.Studio.App.ViewModels;

namespace Sirius.Studio.App;

/// <summary>
/// Lists the custom node libraries, what each added, and what went wrong.
/// </summary>
/// <remarks>
/// Problems are listed per library, a sentence each, because the person
/// reading them is often not the person who wrote the library - and "Gamma:
/// needs a TypeKey" is something they can pass on, where a stack trace is not.
/// </remarks>
public partial class CustomNodesWindow : Window
{
    private readonly CustomNodesViewModel _model;

    public CustomNodesWindow(CustomNodesViewModel model)
    {
        _model = model;

        InitializeComponent();
        DarkTitleBar.Apply(this);

        DataContext = model;
        FoldersText.Text =
            $"Plugins folders: {model.ShippedFolder}  and  {model.UserFolder}";

        model.Libraries.CollectionChanged += OnLibrariesChanged;
        Closed += (_, _) => model.Libraries.CollectionChanged -= OnLibrariesChanged;
        ShowEmptyText();
    }

    private void OnLibrariesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ShowEmptyText();

    private void ShowEmptyText() =>
        EmptyText.Visibility = _model.Libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Custom Node Library",
            Filter = "Custom node library (*.dll)|*.dll|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var row = _model.Add(dialog.FileName);

        // The new entry is at the bottom, which may be out of sight; the
        // operator wants to see whether it worked.
        Dispatcher.BeginInvoke(() =>
        {
            if (LibraryList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
                container.BringIntoView();
        });
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CustomNodeLibraryViewModel library })
            _model.Remove(library);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_model.UserFolder);
            Process.Start(new ProcessStartInfo { FileName = _model.UserFolder, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Dialog.Error($"Could not open {_model.UserFolder}: {ex.Message}", "Custom Nodes");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
