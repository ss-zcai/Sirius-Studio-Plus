using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sirius.Core.Licensing;
using Sirius.Tools.LicenceMaker;

namespace Sirius.Tools.LicenceMakerApp;

/// <summary>How long a code runs.</summary>
public enum LicenceLength
{
    /// <summary>A trial of some days, today the first.</summary>
    Days,

    /// <summary>A trial to a day chosen.</summary>
    Until,

    /// <summary>No end.</summary>
    NoEnd,
}

/// <summary>
/// The licence maker's window: who and what a code is for, how long it
/// runs, and the code; a code pasted, read back.
/// </summary>
public sealed partial class LicenceMakerViewModel : ObservableObject
{
    private readonly Func<DateOnly> _today;

    public LicenceMakerViewModel(string keyFile, Func<DateOnly>? today = null)
    {
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _keyFile = keyFile;
        _until = _today().AddDays(13).ToDateTime(TimeOnly.MinValue);
    }

    public IReadOnlyList<(string Product, string Name)> Products => Signing.Products;

    /// <summary>"Sirius IV" and the rest, for the list.</summary>
    public IReadOnlyList<string> ProductNames => Signing.Products.Select(product => product.Name).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText), nameof(HasKey), nameof(LogText))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _keyFile;

    [ObservableProperty]
    private int _productIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MachineProblem))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _machineText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToProblem))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _to = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDays), nameof(IsUntil), nameof(IsNoEnd), nameof(LastDayText))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private LicenceLength _length = LicenceLength.Days;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastDayText))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _daysText = "7";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastDayText))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private DateTime? _until;

    /// <summary>The code made last; empty before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string _code = string.Empty;

    /// <summary>What the code made says, or why none was.</summary>
    [ObservableProperty]
    private string _result = string.Empty;

    /// <summary>Said in red: the key not the programs', a code not made.</summary>
    [ObservableProperty]
    private string? _problem;

    /// <summary>A code pasted to read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    private string _codeToRead = string.Empty;

    public bool IsDays
    {
        get => Length == LicenceLength.Days;
        set { if (value) Length = LicenceLength.Days; }
    }

    public bool IsUntil
    {
        get => Length == LicenceLength.Until;
        set { if (value) Length = LicenceLength.Until; }
    }

    public bool IsNoEnd
    {
        get => Length == LicenceLength.NoEnd;
        set { if (value) Length = LicenceLength.NoEnd; }
    }

    public bool HasKey => File.Exists(KeyFile);

    public bool HasCode => Code.Length > 0;

    public string KeyText => HasKey ? KeyFile : $"{KeyFile} - not there. Choose the key file, or make one with LicenceMaker keygen.";

    public string LogText => $"Every code made is logged in {Signing.LogFile(KeyFile)}.";

    /// <summary>The machine ID as written, or null when it is not one.</summary>
    public string? Machine => MachineId.Normalise(MachineText);

    public string? MachineProblem =>
        MachineText.Trim().Length == 0 || Machine is not null ? null : "Not a machine ID: 16 letters and digits, as 7KQ2-M9XD-4TWA-PB31.";

    public string? ToProblem =>
        System.Text.Encoding.UTF8.GetByteCount(To.Trim()) > 64 ? "At most 64 bytes - about 60 letters." : null;

    /// <summary>The last day the code runs, that day included; null for no end, or when the length is not one.</summary>
    public DateOnly? LastDay => Length switch
    {
        LicenceLength.Days when int.TryParse(DaysText.Trim(), out var days) && days is >= 1 and <= 3650 => _today().AddDays(days - 1),
        LicenceLength.Until when Until is { } until && DateOnly.FromDateTime(until) >= _today() => DateOnly.FromDateTime(until),
        _ => null,
    };

    private bool LengthIsGood => Length == LicenceLength.NoEnd || LastDay is not null;

    public string LastDayText => Length switch
    {
        LicenceLength.NoEnd => "A full licence: it runs for good on that PC.",
        _ when LastDay is { } last => $"Runs to {LicenceStore.Day(last)}, that day included.",
        LicenceLength.Days => "Days: 1 to 3650.",
        _ => "Choose a day from today on.",
    };

    /// <summary>What the pasted code says.</summary>
    public string ReadText =>
        CodeToRead.Trim().Length == 0 ? string.Empty
        : LicenceCode.Read(CodeToRead, SiriusLicensing.PublicKey) is { } licence ? Signing.Describe(licence) + "."
        : "Not a genuine Sirius licence code for the programs as built.";

    /// <summary>Puts text on the clipboard - a test reads it instead.</summary>
    internal Action<string> CopyText { get; set; } = _ => { };

    /// <summary>This PC's machine ID, for a code for this PC.</summary>
    [RelayCommand]
    private void ThisPc() => MachineText = MachineId.Current();

    private bool CanMake() => HasKey && Machine is not null && ToProblem is null && LengthIsGood;

    [RelayCommand(CanExecute = nameof(CanMake))]
    private void Make()
    {
        var to = To.Trim();
        var licence = new Licence(Products[ProductIndex].Product, Machine!, _today(), Length == LicenceLength.NoEnd ? null : LastDay, to.Length == 0 ? null : to);

        try
        {
            Code = Signing.Make(licence, KeyFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            Code = string.Empty;
            Result = string.Empty;
            Problem = ex.Message;
            return;
        }

        Result = Signing.Describe(licence) + ".";
        Problem = Signing.TakenByPrograms(Code)
            ? null
            : "The programs as built will not take this code: the key file is not the one they were built for.";
    }

    [RelayCommand(CanExecute = nameof(HasCode))]
    private void Copy() => CopyText(Code);
}
