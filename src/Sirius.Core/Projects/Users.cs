using System.Globalization;
using System.Security.Cryptography;

namespace Sirius.Core.Projects;

/// <summary>
/// Who may do what on the line (F2): an operator runs it, a technician
/// tunes it, an engineer changes how it is set up.
/// </summary>
/// <remarks>Ordered: a higher level may do all a lower one may.</remarks>
public enum UserLevel
{
    /// <summary>Anyone at the machine, logged in or not.</summary>
    Operator = 0,

    /// <summary>Changes settings on the line, resets counts.</summary>
    Technician = 1,

    /// <summary>Everything, and leaving the running program.</summary>
    Engineer = 2,
}

/// <summary>The levels in words.</summary>
public static class UserLevels
{
    /// <summary>"an Engineer", "a Technician", "an Operator".</summary>
    public static string WithArticle(UserLevel level) =>
        level is UserLevel.Engineer or UserLevel.Operator ? $"an {level}" : $"a {level}";
}

/// <summary>
/// A person who logs in on the line: a name, a level, and a PIN kept only
/// as a salted hash.
/// </summary>
/// <remarks>
/// The hash keeps a PIN from being read off the project file; it does not
/// make a four-digit PIN strong against someone holding the file, who
/// could try every one. It is a lock on a cabinet, as user levels on a
/// machine are, not a safe.
/// </remarks>
public sealed class ProjectUser
{
    internal ProjectUser(string name, UserLevel level, string pin)
    {
        Name = name;
        Level = level;
        Pin = pin;
    }

    public string Name { get; internal set; }

    public UserLevel Level { get; internal set; }

    /// <summary>The PIN as kept: "pbkdf2$iterations$salt$hash".</summary>
    internal string Pin { get; set; }

    /// <summary>Whether <paramref name="pin"/> is this user's.</summary>
    public bool Checks(string? pin) => Pins.Verify(pin, Pin);

    public override string ToString() => $"{Name} ({Level})";
}

/// <summary>PINs: what one may be, and how one is kept.</summary>
public static class Pins
{
    public const int MinLength = 4;
    public const int MaxLength = 8;

    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Why <paramref name="pin"/> cannot be a PIN, or null: four to eight digits.</summary>
    public static string? Problem(string? pin)
    {
        if (string.IsNullOrEmpty(pin))
            return "A PIN is needed.";

        if (!pin.All(char.IsAsciiDigit))
            return "A PIN is digits only, so it can be typed on the screen's keypad.";

        return pin.Length is < MinLength or > MaxLength
            ? $"A PIN is {MinLength} to {MaxLength} digits."
            : null;
    }

    /// <summary>The PIN as kept: salted, hashed, never itself.</summary>
    public static string Hash(string pin)
    {
        if (Problem(pin) is { } problem)
            throw new ArgumentException(problem, nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>Whether <paramref name="pin"/> is the one <paramref name="kept"/> was made from.</summary>
    public static bool Verify(string? pin, string? kept)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(kept))
            return false;

        var parts = kept.Split('$');

        if (parts.Length != 4 || parts[0] != "pbkdf2" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations <= 0)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="kept"/> is a PIN as this keeps one.</summary>
    internal static bool IsKept(string? kept) =>
        kept is not null && kept.StartsWith("pbkdf2$", StringComparison.Ordinal) && kept.Split('$').Length == 4;
}
