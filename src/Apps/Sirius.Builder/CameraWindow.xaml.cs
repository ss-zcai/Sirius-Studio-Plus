using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sirius.UI;
using Sirius.Vision.Cameras;

namespace Sirius.Builder;

/// <summary>A project camera: find it, set it, look through it.</summary>
/// <remarks>
/// Modal, so a Test Run - which opens the same camera - cannot start while
/// it is up; closing it lets the camera go.
/// </remarks>
public partial class CameraWindow : Window
{
    public CameraWindow(CameraViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        Model = model;
        DataContext = model;

        // The cameras listed as soon as the window is up.
        Loaded += async (_, _) => await model.FindAsync().ConfigureAwait(true);
    }

    internal CameraViewModel Model { get; }

    /// <summary>Shows the window over <paramref name="owner"/>; returns once it is closed.</summary>
    public static void Show(Window? owner, CameraViewModel model)
    {
        var window = new CameraWindow(model)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
    }

    /// <summary>A number typed and Enter pressed: it is the camera's now.</summary>
    private void OnCommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
        }
    }

    /// <summary>A camera in the list double-clicked: use it.</summary>
    private void OnFoundDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FoundList.SelectedItem is CameraInfo camera)
            Model.Use(camera);
    }

    private void OnTakeFromRecipe(object sender, RoutedEventArgs e) => Model.TakeFromRecipe();

    /// <summary>Asks first: it writes the recipe file, which Studio may have open.</summary>
    private void OnGiveToRecipe(object sender, RoutedEventArgs e)
    {
        if (Model.GiveQuestion() is not { } question)
            return;

        if (Dialog.Confirm(question, "Give to Recipe", this))
            Model.GiveToRecipe();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e) => Model.Dispose();
}
