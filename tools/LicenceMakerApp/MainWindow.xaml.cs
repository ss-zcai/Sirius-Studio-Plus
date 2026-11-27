using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Sirius.Tools.LicenceMakerApp;

public partial class MainWindow : Window
{
    private readonly LicenceMakerViewModel _model;

    internal MainWindow(LicenceMakerViewModel model)
    {
        InitializeComponent();

        _model = model;
        DataContext = model;

        model.CopyText = text =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        };

        Loaded += (_, _) => MachineBox.Focus();
    }

    private void OnChooseKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Signing Key",
            Filter = "Private key (*.pem)|*.pem|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_model.KeyFile) is { } folder && Directory.Exists(folder) ? folder : null,
        };

        if (dialog.ShowDialog(this) == true)
            _model.KeyFile = dialog.FileName;
    }
}
