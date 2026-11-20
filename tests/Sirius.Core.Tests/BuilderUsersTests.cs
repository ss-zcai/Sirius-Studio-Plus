using System.IO;
using System.Windows;
using Sirius.Builder;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Screens;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// User levels in Builder (F2): the Users window, and what a widget or a
/// screen needs, chosen in the designer.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BuilderUsersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-busers-" + Guid.NewGuid().ToString("N"));

    public BuilderUsersTests(WpfTestHost host)
    {
        _ = host;
        TestCatalog.Ensure();
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a run over.
        }
    }

    [Fact]
    public void The_users_window_adds_renames_levels_pins_and_removes_users()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
        var model = new UsersViewModel(project);

        Assert.True(model.HasNoUsers);
        Assert.False(model.HasSelection);

        Assert.Equal("A name is needed.", model.Add(" ", UserLevel.Technician, "1234", "1234"));
        Assert.Equal("The two PINs are not the same.", model.Add("Ann", UserLevel.Technician, "1234", "1235"));
        Assert.Equal("A PIN is 4 to 8 digits.", model.Add("Ann", UserLevel.Technician, "12", "12"));
        Assert.Empty(project.Users);

        Assert.Null(model.Add("Ann", UserLevel.Technician, "1234", "1234"));
        Assert.StartsWith("Added Ann. From now on", model.Status, StringComparison.Ordinal);
        Assert.Null(model.Add("Bo", UserLevel.Engineer, "5678", "5678"));
        Assert.Equal("There is already a user called 'ann'.", model.Add("ann", UserLevel.Operator, "1111", "1111"));

        Assert.Equal(["Ann · Technician", "Bo · Engineer"], model.Users.Select(row => row.Text));
        Assert.Equal("Bo", model.Selected!.Name);

        // The one chosen: renamed, its level, its PIN.
        model.Selected = model.Users[0];
        Assert.Null(model.Rename("Anna"));
        Assert.Equal("There is already a user called 'Bo'.", model.Rename("Bo"));
        model.SetLevel(UserLevel.Engineer);
        Assert.Equal("Anna · Engineer", model.Users[0].Text);
        Assert.Equal("The two PINs are not the same.", model.SetPin("4444", "4445"));
        Assert.Null(model.SetPin("4444", "4444"));

        var reopened = Project.Load(project.FilePath).Project;
        Assert.Equal(UserLevel.Engineer, reopened.LogIn("Anna", "4444")!.Level);

        // Idle minutes.
        model.LogOutText = "abc";
        Assert.StartsWith("Log out after is a whole number", model.Problem, StringComparison.Ordinal);
        model.LogOutText = "0";
        Assert.Equal(0, project.LogOutMinutes);
        Assert.Equal("Users stay logged in until they log out.", model.Status);

        // Removed: the last leaves nothing locked.
        model.Remove();
        Assert.Equal("Bo", model.Selected!.Name);
        model.Remove();
        Assert.True(model.HasNoUsers);
        Assert.False(project.HasUsers);
        Assert.Contains("nothing on the line is locked", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_window_is_made_and_run_users_opens_it()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();

            var window = UsersWindow.Unshown(new UsersViewModel(project));
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;

            try
            {
                window.Show();
                WpfTestHost.Settle(window.Dispatcher);

                // At the size it opens at, both halves of the form fit: nothing to scroll.
                var form = FindChild<System.Windows.Controls.ScrollViewer>(window);
                Assert.NotNull(form);
                Assert.Equal(0, form!.ScrollableHeight);

                // The PIN boxes are the theme's, not the stock white.
                var pin = (System.Windows.Controls.PasswordBox)window.FindName("PinBox");
                Assert.NotEqual(System.Windows.Media.Colors.White, ((System.Windows.Media.SolidColorBrush)pin.Background).Color);
            }
            finally
            {
                window.Close();
            }

            var builder = new BuilderViewModel { RememberProjects = false, Ask = (_, _) => MessageBoxResult.No };
            var opened = new List<UsersViewModel>();
            builder.OpenUsersWindow = (_, model) => opened.Add(model);

            Assert.False(builder.UsersCommand.CanExecute(null));
            builder.Show(project);
            Assert.True(builder.UsersCommand.CanExecute(null));

            builder.UsersCommand.Execute(null);
            Assert.Single(opened);
        });
    }

    private static T? FindChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T found)
                return found;

            if (FindChild<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    [Fact]
    public void The_designer_chooses_what_a_widget_and_a_screen_need()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");

        WpfTestHost.OnStaThread(() =>
        {
            var designer = new ScreenDesignerViewModel(new StationRecipes(project), "Setup");

            // A screen: anyone, or a level; saved with it, and undone.
            Assert.Null(designer.Needs);
            var needs = designer.ScreenCategories.SelectMany(category => category.Rows).Single(row => row.Name == "Needs");
            Assert.Equal(["Anyone", "Technician", "Engineer"], needs.Options!.Cast<PropertyOption>().Select(option => option.Label));

            needs.Value = nameof(UserLevel.Engineer);
            Assert.Equal(UserLevel.Engineer, designer.ToScreen().Needs);
            designer.UndoCommand.Execute(null);
            Assert.Null(designer.ToScreen().Needs);

            // A button: its kind's, or a level.
            var reset = designer.Add(WidgetKind.Button);
            reset.Action = WidgetAction.ResetCounts;
            var row = reset.Categories.SelectMany(category => category.Rows).Single(r => r.Name == "Needs");
            Assert.Equal("As its kind (Technician)", row.Options!.Cast<PropertyOption>().First().Label);

            row.Value = nameof(UserLevel.Engineer);
            Assert.Equal(UserLevel.Engineer, designer.ToScreen().Widgets.Last().Needs);
            Assert.Equal(UserLevel.Engineer, ScreenLayout.NeedsFor(designer.ToScreen().Widgets.Last()));

            row.Value = string.Empty;
            Assert.Null(designer.ToScreen().Widgets.Last().Needs);

            // Log In / Out: no station, and no level - it is how one is had.
            reset.Action = WidgetAction.LogInOut;
            Assert.Equal("Log In / Out", reset.Caption);
            Assert.DoesNotContain(reset.Categories.SelectMany(category => category.Rows), r => r.Name == "Needs" && r.IsVisible);

            // The start screen is always open: no Needs on it.
            var main = new ScreenDesignerViewModel(new StationRecipes(project));
            Assert.DoesNotContain(main.ScreenCategories.SelectMany(category => category.Rows), r => r.Name == "Needs" && r.IsVisible);
        });
    }
}
