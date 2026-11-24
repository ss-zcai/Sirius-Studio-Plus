using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Sirius.Core.Projects;

namespace Sirius.Builder;

/// <summary>A user as the Users window lists them.</summary>
public sealed class UserRow(ProjectUser user) : ObservableObject
{
    public ProjectUser User { get; } = user;

    public string Name => User.Name;

    public UserLevel Level => User.Level;

    /// <summary>"Ann · Technician".</summary>
    public string Text => $"{User.Name} · {User.Level}";

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// Who may log in on the line (F2): the project's users, their levels and
/// PINs, and how long one stays logged in idle. Every change is the
/// project's at once, as a camera's is.
/// </summary>
public sealed partial class UsersViewModel : ObservableObject
{
    private readonly Project _project;

    [ObservableProperty]
    private UserRow? _selected;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private string? _status;

    public UsersViewModel(Project project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));

        foreach (var user in project.Users)
            Users.Add(new UserRow(user));

        Selected = Users.FirstOrDefault();
        _logOutText = project.LogOutMinutes.ToString(CultureInfo.CurrentCulture);
    }

    public ObservableCollection<UserRow> Users { get; } = [];

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(UserRow? value) => OnPropertyChanged(nameof(HasSelection));

    public static IReadOnlyList<UserLevel> Levels { get; } = Enum.GetValues<UserLevel>();

    /// <summary>What a project without users is told: nothing is locked until there is one.</summary>
    public bool HasNoUsers => Users.Count == 0;

    private string _logOutText;

    /// <summary>Minutes idle before a log-out, typed; 0 for never.</summary>
    public string LogOutText
    {
        get => _logOutText;
        set
        {
            if (!SetProperty(ref _logOutText, value))
                return;

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var minutes) || minutes < 0 || minutes > Project.MaxLogOutMinutes)
            {
                Problem = $"Log out after is a whole number of minutes, 0 to {Project.MaxLogOutMinutes}: 0 is never.";
                return;
            }

            Guard(() => _project.SetLogOutMinutes(minutes));
            Status = minutes == 0 ? "Users stay logged in until they log out." : $"Logged out after {minutes} minutes idle.";
        }
    }

    /// <summary>Adds a user; returns why not, or null.</summary>
    public string? Add(string? name, UserLevel level, string? pin, string? again)
    {
        if (_project.UserNameProblem(name) is { } nameProblem)
            return Said(nameProblem);

        if (PinProblem(pin, again) is { } pinProblem)
            return Said(pinProblem);

        var row = new UserRow(_project.AddUser(name!, level, pin!));
        Users.Add(row);
        Selected = row;
        OnPropertyChanged(nameof(HasNoUsers));

        Status = Users.Count == 1
            ? $"Added {row.Name}. From now on, what needs a level on the line asks for a login."
            : $"Added {row.Name}.";

        return Said(null);
    }

    /// <summary>Renames the selected user; returns why not, or null.</summary>
    public string? Rename(string? name)
    {
        if (Selected is not { } row)
            return Said("Choose a user.");

        if (string.Equals(row.Name, name?.Trim(), StringComparison.Ordinal))
            return Said(null);

        if (_project.UserNameProblem(name, row.User) is { } problem)
            return Said(problem);

        var old = row.Name;
        Guard(() => _project.RenameUser(row.User, name!));
        row.Refresh();
        Status = $"Renamed {old} to {row.Name}.";
        return Said(null);
    }

    public void SetLevel(UserLevel level)
    {
        if (Selected is not { } row || row.Level == level)
            return;

        Guard(() => _project.SetUserLevel(row.User, level));
        row.Refresh();
        Status = $"{row.Name} is {UserLevels.WithArticle(level)} now.";
    }

    /// <summary>Sets the selected user's PIN; returns why not, or null.</summary>
    public string? SetPin(string? pin, string? again)
    {
        if (Selected is not { } row)
            return Said("Choose a user.");

        if (PinProblem(pin, again) is { } problem)
            return Said(problem);

        Guard(() => _project.SetUserPin(row.User, pin!));
        Status = $"{row.Name}'s PIN is set.";
        return Said(null);
    }

    public void Remove()
    {
        if (Selected is not { } row)
            return;

        var at = Users.IndexOf(row);
        Guard(() => _project.RemoveUser(row.User));
        Users.Remove(row);
        Selected = Users.Count == 0 ? null : Users[Math.Min(at, Users.Count - 1)];
        OnPropertyChanged(nameof(HasNoUsers));

        Status = Users.Count == 0
            ? $"Removed {row.Name}. With no users, nothing on the line is locked."
            : $"Removed {row.Name}.";
    }

    private static string? PinProblem(string? pin, string? again)
    {
        if (Pins.Problem(pin) is { } problem)
            return problem;

        return pin != again ? "The two PINs are not the same." : null;
    }

    private string? Said(string? problem)
    {
        Problem = problem;
        return problem;
    }

    private void Guard(Action change)
    {
        try
        {
            change();
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Problem = ex.Message;
        }
    }
}
