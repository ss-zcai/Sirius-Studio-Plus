using System.Globalization;
using System.Text.Json;

namespace Sirius.Core.Licensing;

/// <summary>How a licence stands.</summary>
public enum LicenceStatus
{
    /// <summary>It runs.</summary>
    Valid,

    /// <summary>No code entered yet.</summary>
    None,

    /// <summary>Its last day has gone.</summary>
    Ended,

    /// <summary>Made for another PC.</summary>
    OtherMachine,

    /// <summary>Made for another Sirius program.</summary>
    OtherProduct,

    /// <summary>Not a code Sirius made: cut short, altered, or made up.</summary>
    NotGenuine,

    /// <summary>The clock is behind when the program last ran, or before the code was made.</summary>
    ClockBehind,
}

/// <summary>A licence's standing, and what to tell the user of it.</summary>
public sealed record LicenceState(LicenceStatus Status, Licence? Licence, string Message)
{
    public bool IsValid => Status == LicenceStatus.Valid;

    /// <summary>Days left of a trial, today counted; null with no end.</summary>
    public int? DaysLeft { get; init; }

    /// <summary>"Trial - 3 days left", for a title; null with no end.</summary>
    public string? TrialNote => IsValid && DaysLeft is { } days
        ? $"Trial - {days} day{(days == 1 ? string.Empty : "s")} left"
        : null;
}

/// <summary>
/// A program's licence on this PC: the code entered, kept in a file, and
/// checked each time - against the public key, this PC's machine ID and
/// the date.
/// </summary>
/// <remarks>
/// The latest time the program was seen running is kept beside the code,
/// and in the registry, so a clock put back to stretch a trial is told and
/// stops it. Nothing on the PC can be made safe from someone set on it;
/// this keeps an honest user honest and asks real work of anyone else.
/// </remarks>
public sealed class LicenceStore
{
    /// <summary>Some slack for the clock being set right by a little.</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromHours(2);

    private readonly string _file;
    private readonly byte[] _publicKey;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _registryKey;

    /// <param name="product">"IV".</param>
    /// <param name="productName">"Sirius IV", for the messages.</param>
    /// <param name="folder">Where the code is kept.</param>
    /// <param name="machineId">This PC's.</param>
    /// <param name="publicKey">Sirius' public key, as SubjectPublicKeyInfo.</param>
    /// <param name="now">The clock.</param>
    /// <param name="registryKey">Under HKEY_CURRENT_USER, a second place for the last time seen; none in tests.</param>
    public LicenceStore(string product, string productName, string folder, string machineId, byte[] publicKey, Func<DateTimeOffset> now, string? registryKey = null)
    {
        Product = product;
        ProductName = productName;
        MachineId = machineId;
        _file = Path.Combine(folder, product + ".licence");
        _publicKey = publicKey;
        _now = now;
        _registryKey = registryKey;
    }

    /// <summary>The store for <paramref name="product"/> on this PC, for this user.</summary>
    public static LicenceStore ForThisPc(string product, string productName) =>
        new(
            product,
            productName,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sirius Vision", "Licences"),
            Licensing.MachineId.Current(),
            SiriusLicensing.PublicKey,
            () => DateTimeOffset.Now,
            @"Software\Sirius Vision\Licences");

    public string Product { get; }

    public string ProductName { get; }

    /// <summary>This PC's, to send for a code.</summary>
    public string MachineId { get; }

    /// <summary>How the licence entered stands now; notes the time, for the clock check.</summary>
    public LicenceState Check()
    {
        var saved = Load();

        if (saved?.Code is not { Length: > 0 } code)
            return new LicenceState(LicenceStatus.None, null, $"{ProductName} needs a licence code for this PC. Send the machine ID below to your Sirius supplier for one, then enter the code.");

        var state = Judge(code, Latest(saved));

        if (state.IsValid)
            Seen(saved);

        return state;
    }

    /// <summary>
    /// Enters a code: kept when it is good on this PC today, and how it
    /// stands either way. A code not kept leaves the one before in place.
    /// </summary>
    public LicenceState Enter(string code)
    {
        var saved = Load();
        var state = Judge(code, Latest(saved));

        if (state.IsValid)
            Seen(new Saved { Code = code.Trim(), LastSeen = saved?.LastSeen });

        return state;
    }

    private LicenceState Judge(string code, DateTimeOffset? lastSeen)
    {
        if (LicenceCode.Read(code, _publicKey) is not { } licence)
            return new LicenceState(LicenceStatus.NotGenuine, null, "This is not a Sirius licence code. Check it was copied whole.");

        if (!string.Equals(licence.Product, Product, StringComparison.OrdinalIgnoreCase))
            return new LicenceState(LicenceStatus.OtherProduct, licence, $"This code is for another Sirius program ({licence.Product}), not {ProductName}.");

        if (!string.Equals(licence.MachineId, MachineId, StringComparison.Ordinal))
            return new LicenceState(LicenceStatus.OtherMachine, licence, $"This code is for another PC ({licence.MachineId}). A code works only on the PC whose machine ID it was made for.");

        var now = _now();
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        // A licence with no end does not mind the clock.
        if (licence.LastDay is not { } last)
            return new LicenceState(LicenceStatus.Valid, licence, licence.To is { } to ? $"Licensed to {to}, with no end." : "Licensed, with no end.");

        if (lastSeen is { } seen && now < seen - ClockSlack)
            return new LicenceState(LicenceStatus.ClockBehind, licence, $"This PC's clock is behind the last time {ProductName} ran ({Day(DateOnly.FromDateTime(seen.LocalDateTime))}). Set it right to go on.");

        if (today < licence.Issued.AddDays(-1))
            return new LicenceState(LicenceStatus.ClockBehind, licence, $"This PC's clock is before the code was made ({Day(licence.Issued)}). Set it right to go on.");

        if (today > last)
            return new LicenceState(LicenceStatus.Ended, licence, $"The trial ended on {Day(last)}. Enter a new licence code to go on.");

        var left = last.DayNumber - today.DayNumber + 1;
        return new LicenceState(LicenceStatus.Valid, licence, $"Trial, until {Day(last)} - {left} day{(left == 1 ? string.Empty : "s")} left.") { DaysLeft = left };
    }

    /// <summary>"4 December 2026".</summary>
    public static string Day(DateOnly day) => day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    // ---- kept ---------------------------------------------------------------------------

    private sealed class Saved
    {
        public string? Code { get; set; }

        public DateTimeOffset? LastSeen { get; set; }
    }

    private Saved? Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The later of the file's last time seen and the registry's.</summary>
    private DateTimeOffset? Latest(Saved? saved)
    {
        var registry = ReadRegistry();

        return (saved?.LastSeen, registry) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, var b) => b,
        };
    }

    /// <summary>Keeps the code, with now as the last time seen - never moved back.</summary>
    private void Seen(Saved saved)
    {
        var now = _now();
        var latest = Latest(saved);
        saved.LastSeen = latest is { } seen && seen > now ? seen : now;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: asked again next time, which is the worst of it.
        }

        WriteRegistry(saved.LastSeen.Value);
    }

    private DateTimeOffset? ReadRegistry()
    {
        if (_registryKey is null || !OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_registryKey);
            return key?.GetValue(Product) is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen)
                ? seen
                : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private void WriteRegistry(DateTimeOffset seen)
    {
        if (_registryKey is null || !OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_registryKey);
            key.SetValue(Product, seen.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }
}
