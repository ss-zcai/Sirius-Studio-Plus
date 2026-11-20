using System.IO;
using System.Text.Json.Nodes;
using Sirius.Core.Graph;
using Sirius.Core.Projects;
using Sirius.Core.Screens;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// User levels (F2): who may log in on the line, their PINs kept only as
/// hashes, and what each screen and widget needs.
/// </summary>
public sealed class UserLevelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-users-" + Guid.NewGuid().ToString("N"));

    public UserLevelTests()
    {
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

    [Theory]
    [InlineData("1234", null)]
    [InlineData("12345678", null)]
    [InlineData("", "A PIN is needed.")]
    [InlineData("123", "A PIN is 4 to 8 digits.")]
    [InlineData("123456789", "A PIN is 4 to 8 digits.")]
    [InlineData("12a4", "A PIN is digits only, so it can be typed on the screen's keypad.")]
    public void A_pin_is_four_to_eight_digits(string pin, string? problem) =>
        Assert.Equal(problem, Pins.Problem(pin));

    [Fact]
    public void A_pin_is_kept_salted_and_hashed_never_itself()
    {
        var kept = Pins.Hash("4821");
        var again = Pins.Hash("4821");

        Assert.StartsWith("pbkdf2$", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("4821", kept, StringComparison.Ordinal);
        Assert.NotEqual(kept, again);

        Assert.True(Pins.Verify("4821", kept));
        Assert.True(Pins.Verify("4821", again));
        Assert.False(Pins.Verify("4822", kept));
        Assert.False(Pins.Verify(null, kept));
        Assert.False(Pins.Verify("4821", "not a kept pin"));
        Assert.False(Pins.Verify("4821", "pbkdf2$1000$!!$!!"));
    }

    [Fact]
    public void Users_are_kept_in_the_project_and_log_in_by_name_and_pin()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
        Assert.False(project.HasUsers);
        Assert.Equal(Project.DefaultLogOutMinutes, project.LogOutMinutes);

        // None: the project is written as it always was.
        var before = JsonNode.Parse(File.ReadAllText(project.FilePath))!;
        Assert.Equal(1, (int)before["version"]!);
        Assert.Null(before["users"]);

        var ann = project.AddUser("Ann", UserLevel.Technician, "1234");
        project.AddUser("Bo", UserLevel.Engineer, "998877");

        Assert.Equal("There is already a user called 'ann'.", project.UserNameProblem("ann"));
        Assert.Throws<ArgumentException>(() => project.AddUser("Cy", UserLevel.Operator, "12"));

        var text = File.ReadAllText(project.FilePath);
        Assert.DoesNotContain("1234", text, StringComparison.Ordinal);
        Assert.DoesNotContain("998877", text, StringComparison.Ordinal);
        Assert.Equal(2, (int)JsonNode.Parse(text)!["version"]!);

        var loaded = Project.Load(project.FilePath);
        Assert.Empty(loaded.Warnings);
        var reopened = loaded.Project;

        Assert.True(reopened.HasUsers);
        Assert.Equal(["Ann", "Bo"], reopened.Users.Select(user => user.Name));
        Assert.Equal(UserLevel.Technician, reopened.LogIn("ann", "1234")!.Level);
        Assert.Null(reopened.LogIn("Ann", "4321"));
        Assert.Null(reopened.LogIn("Nobody", "1234"));

        // Renamed, its level changed, its PIN set again, removed.
        reopened.RenameUser(reopened.FindUser("Ann")!, "Anna");
        reopened.SetUserLevel(reopened.FindUser("Anna")!, UserLevel.Engineer);
        reopened.SetUserPin(reopened.FindUser("Anna")!, "5555");
        reopened.SetLogOutMinutes(2);

        var third = Project.Load(project.FilePath).Project;
        Assert.Equal(UserLevel.Engineer, third.LogIn("Anna", "5555")!.Level);
        Assert.Null(third.LogIn("Anna", "1234"));
        Assert.Equal(2, third.LogOutMinutes);

        third.RemoveUser(third.FindUser("Anna")!);
        third.RemoveUser(third.FindUser("Bo")!);
        third.SetLogOutMinutes(Project.DefaultLogOutMinutes);

        var none = JsonNode.Parse(File.ReadAllText(project.FilePath))!;
        Assert.Equal(1, (int)none["version"]!);
        Assert.Null(none["users"]);
        Assert.Null(none["logOutMinutes"]);
    }

    [Fact]
    public void A_user_whose_pin_cannot_be_checked_is_left_out_and_said()
    {
        var project = Project.Create(_folder, "Line", new VisionGraph(), "Top");
        project.AddUser("Ann", UserLevel.Technician, "1234");

        var document = JsonNode.Parse(File.ReadAllText(project.FilePath))!;
        document["users"]!.AsArray().Add(new JsonObject { ["name"] = "Typed", ["level"] = "Engineer", ["pin"] = "1234" });
        document["users"]!.AsArray().Add(new JsonObject { ["name"] = "Odd", ["level"] = "Wizard", ["pin"] = Pins.Hash("1111") });
        File.WriteAllText(project.FilePath, document.ToJsonString());

        var loaded = Project.Load(project.FilePath);

        Assert.Equal(["Ann", "Odd"], loaded.Project.Users.Select(user => user.Name));
        Assert.Contains(loaded.Warnings, warning => warning.StartsWith("User 'Typed' has no PIN that can be checked", StringComparison.Ordinal));
        Assert.Contains(loaded.Warnings, warning => warning.StartsWith("User 'Odd': the level 'Wizard'", StringComparison.Ordinal));
        Assert.Equal(UserLevel.Operator, loaded.Project.FindUser("Odd")!.Level);
    }

    [Fact]
    public void A_widget_needs_its_own_level_or_its_kinds()
    {
        Assert.Equal(UserLevel.Technician, ScreenLayout.NeedsFor(new Widget { Kind = WidgetKind.Setting }));
        Assert.Equal(UserLevel.Operator, ScreenLayout.NeedsFor(new Widget { Kind = WidgetKind.Setting, ReadOnly = true }));
        Assert.Equal(UserLevel.Technician, ScreenLayout.NeedsFor(new Widget { Kind = WidgetKind.Button, Action = WidgetAction.ResetCounts }));
        Assert.Equal(UserLevel.Operator, ScreenLayout.NeedsFor(new Widget { Kind = WidgetKind.Button, Action = WidgetAction.RunStop }));
        Assert.Equal(UserLevel.Engineer, ScreenLayout.NeedsFor(new Widget { Kind = WidgetKind.Button, Action = WidgetAction.RunStop, Needs = UserLevel.Engineer }));
        Assert.Equal(UserLevel.Operator, ScreenLayout.NeedsFor(new Widget { Kind = WidgetKind.Setting, Needs = UserLevel.Operator }));

        Assert.Equal(UserLevel.Engineer, ScreenLayout.NeedsFor(new Screen { Name = "Setup", Needs = UserLevel.Engineer }));
        Assert.Equal(UserLevel.Operator, ScreenLayout.NeedsFor(new Screen { Name = Screen.StartName, Needs = UserLevel.Engineer }));
        Assert.Equal(UserLevel.Engineer, ScreenLayout.ExitNeeds);
    }

    /// <summary>
    /// A screen asking for a level is format 3, so a build that does not
    /// know levels refuses it rather than show it unlocked.
    /// </summary>
    [Fact]
    public void A_screen_asking_for_a_level_is_one_an_older_build_refuses()
    {
        Assert.Equal(1, new Screen().RequiredVersion);
        Assert.Equal(3, new Screen { Needs = UserLevel.Technician }.RequiredVersion);
        Assert.Equal(3, new Screen { Widgets = [new Widget { Kind = WidgetKind.Button, Action = WidgetAction.LogInOut }] }.RequiredVersion);
        Assert.Equal(3, new Screen { Widgets = [new Widget { Kind = WidgetKind.Setting, Needs = UserLevel.Engineer }] }.RequiredVersion);

        var path = Path.Combine(_folder, "Setup.screen");
        new Screen { Name = "Setup", Needs = UserLevel.Engineer }.Save(path);

        var read = Screen.Load(path);
        Assert.Equal(UserLevel.Engineer, read.Needs);
        Assert.Contains("\"needs\": \"Engineer\"", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
