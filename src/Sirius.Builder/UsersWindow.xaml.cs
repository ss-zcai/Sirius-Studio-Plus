using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sirius.Core.Projects;
using Sirius.Studio.App;

namespace Sirius.Builder;

/// <summary>Who may log in on the line (F2): the project's users, their levels and PINs.</summary>
public partial class UsersWindow : Window
{
    private readonly UsersViewModel _model;

    private UsersWindow(UsersViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _model = model;
        DataContext = model;
    }

    internal UsersViewModel Model => _model;

    /// <summary>Shows the window, modal: each change is the project's as it is made.</summary>
    public static void Show(Window? owner, UsersViewModel model)
    {
        var window = new UsersWindow(model)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        if (window.Owner is null)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
    }

    /// <summary>For tests: the window, not shown.</summary>
    internal static UsersWindow Unshown(UsersViewModel model) => new(model);

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var level = NewLevelBox.SelectedItem is UserLevel chosen ? chosen : UserLevel.Technician;

        if (_model.Add(NewNameBox.Text, level, NewPinBox.Password, NewPinAgainBox.Password) is null)
        {
            NewNameBox.Clear();
            NewPinBox.Clear();
            NewPinAgainBox.Clear();
        }
    }

    private void OnSetPin(object sender, RoutedEventArgs e)
    {
        if (_model.SetPin(PinBox.Password, PinAgainBox.Password) is null)
        {
            PinBox.Clear();
            PinAgainBox.Clear();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } row)
            return;

        if (Dialog.Confirm($"Remove {row.Name}? They will no longer be able to log in on the line.", "Remove User", this))
            _model.Remove();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (_model.Rename(NameBox.Text) is not null)
            NameBox.Text = _model.Selected?.Name;
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnRename(sender, e);
    }

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        // Following the selection sets the same level, which changes nothing.
        if (LevelBox.SelectedItem is not UserLevel level)
            return;

        _model.SetLevel(level);
    }

    private void OnCommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
