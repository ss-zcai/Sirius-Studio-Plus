using System.IO;
using System.Security.Cryptography;
using Sirius.Core.Licensing;
using Sirius.IV;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Licence codes (decision 97): made with a private key, read with the
/// public one, for one program on one PC, for some days or with no end.
/// Tests sign with a key of their own, never Sirius' private one.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LicensingTests : IDisposable
{
    private static readonly string ThisPc = MachineId.From("test pc");
    private static readonly string OtherPc = MachineId.From("another pc");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-licence-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    public LicensingTests(WpfTestHost host) => _ = host;

    public void Dispose()
    {
        _key.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static DateOnly Day(int day) => new(2026, 11, day);

    private string Code(DateOnly? last, string machine = "", string product = "IV", string? to = null) =>
        LicenceCode.Make(new Licence(product, machine.Length == 0 ? ThisPc : machine, Day(27), last, to), _key);

    private LicenceStore Store() =>
        new("IV", "Sirius IV", _folder, ThisPc, _key.ExportSubjectPublicKeyInfo(), () => _now);

    [Fact]
    public void A_code_reads_back_as_made_however_it_is_pasted()
    {
        var licence = new Licence("IV", ThisPc, Day(27), Day(30), "Line 3 Lab");
        var code = LicenceCode.Make(licence, _key);

        Assert.Matches("^[0-9A-Z]{5}(-[0-9A-Z]{1,5})+$", code);
        Assert.Equal(licence, LicenceCode.Read(code, _key.ExportSubjectPublicKeyInfo()));

        // Lower case, broken over lines, the dashes gone: the same.
        var pasted = "  " + code.Replace("-", string.Empty).ToLowerInvariant().Insert(40, "\r\n") + "\n";
        Assert.Equal(licence, LicenceCode.Read(pasted, _key.ExportSubjectPublicKeyInfo()));

        // No end.
        Assert.Null(LicenceCode.Read(Code(null), _key.ExportSubjectPublicKeyInfo())!.LastDay);
    }

    [Fact]
    public void A_code_altered_cut_short_or_signed_with_another_key_is_not_genuine()
    {
        var code = Code(Day(30));
        var publicKey = _key.ExportSubjectPublicKeyInfo();

        var altered = code[..10] + (code[10] == 'A' ? 'B' : 'A') + code[11..];
        Assert.Null(LicenceCode.Read(altered, publicKey));
        Assert.Null(LicenceCode.Read(code[..^5], publicKey));
        Assert.Null(LicenceCode.Read("not a code at all", publicKey));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(LicenceCode.Read(code, other.ExportSubjectPublicKeyInfo()));

        // Nor does Sirius' own public key take a code from a test key.
        Assert.Null(LicenceCode.Read(code, SiriusLicensing.PublicKey));
    }

    [Fact]
    public void A_machine_id_is_sixteen_letters_in_fours_and_reads_back_as_typed()
    {
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", ThisPc);
        Assert.Equal(ThisPc, MachineId.From("TEST PC "));
        Assert.NotEqual(ThisPc, OtherPc);

        Assert.Equal(ThisPc, MachineId.Normalise(ThisPc.Replace("-", " ").ToLowerInvariant()));
        Assert.Null(MachineId.Normalise(ThisPc + "7"));
        Assert.Null(MachineId.Normalise("ABCD"));

        // This PC has one, the same each time.
        Assert.Equal(MachineId.Current(), MachineId.Current());
    }

    [Fact]
    public void Without_a_code_it_says_to_send_the_machine_id()
    {
        var state = Store().Check();

        Assert.Equal(LicenceStatus.None, state.Status);
        Assert.False(state.IsValid);
        Assert.Contains("machine ID", state.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trial_runs_to_its_last_day_and_not_after()
    {
        var store = Store();
        var entered = store.Enter(Code(Day(29)));

        Assert.True(entered.IsValid);
        Assert.Equal(3, entered.DaysLeft);
        Assert.Equal("Trial - 3 days left", entered.TrialNote);

        // Kept: the next start finds it.
        _now = _now.AddDays(2);
        var last = Store().Check();
        Assert.True(last.IsValid);
        Assert.Equal("Trial - 1 day left", last.TrialNote);
        Assert.Contains("29 November 2026", last.Message, StringComparison.Ordinal);

        _now = _now.AddDays(1);
        var ended = Store().Check();
        Assert.Equal(LicenceStatus.Ended, ended.Status);
        Assert.Contains("ended on 29 November 2026", ended.Message, StringComparison.Ordinal);

        // A new code, and it runs again.
        var extended = LicenceCode.Make(new Licence("IV", ThisPc, Day(30), new DateOnly(2026, 12, 6)), _key);
        Assert.True(Store().Enter(extended).IsValid);
        Assert.Equal(7, Store().Check().DaysLeft);
    }

    [Fact]
    public void A_licence_with_no_end_runs_whatever_the_date()
    {
        Assert.True(Store().Enter(Code(null, to: "Line 3 Lab")).IsValid);

        _now = _now.AddYears(10);
        var state = Store().Check();

        Assert.True(state.IsValid);
        Assert.Null(state.TrialNote);
        Assert.Equal("Licensed to Line 3 Lab, with no end.", state.Message);
    }

    [Fact]
    public void A_code_for_another_pc_or_program_is_not_kept()
    {
        var store = Store();

        var other = store.Enter(Code(Day(30), machine: OtherPc));
        Assert.Equal(LicenceStatus.OtherMachine, other.Status);
        Assert.Contains(OtherPc, other.Message, StringComparison.Ordinal);

        Assert.Equal(LicenceStatus.OtherProduct, store.Enter(Code(Day(30), product: "STUDIO")).Status);
        Assert.Equal(LicenceStatus.NotGenuine, store.Enter("ABCDE-FGHJK").Status);

        // None of them taken.
        Assert.Equal(LicenceStatus.None, store.Check().Status);

        // Nor does a bad code put out a good one.
        Assert.True(store.Enter(Code(Day(30))).IsValid);
        Assert.False(store.Enter(Code(Day(30), machine: OtherPc)).IsValid);
        Assert.True(store.Check().IsValid);
    }

    [Fact]
    public void A_clock_put_back_stops_a_trial()
    {
        Assert.True(Store().Enter(Code(new DateOnly(2026, 12, 3))).IsValid);

        _now = _now.AddDays(5);
        Assert.True(Store().Check().IsValid);

        // Back three days: the last time seen is later.
        _now = _now.AddDays(-3);
        var behind = Store().Check();
        Assert.Equal(LicenceStatus.ClockBehind, behind.Status);
        Assert.Contains("2 December 2026", behind.Message, StringComparison.Ordinal);

        // An hour back - the clock set right - is no matter.
        _now = _now.AddDays(3).AddHours(-1);
        Assert.True(Store().Check().IsValid);
    }

    [Fact]
    public void A_clock_before_the_code_was_made_will_not_take_it()
    {
        _now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(LicenceStatus.ClockBehind, Store().Enter(Code(Day(30))).Status);
    }

    [Fact]
    public void Sirius_public_key_is_one()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(SiriusLicensing.PublicKey, out _);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void The_licence_window_shows_the_machine_id_and_takes_a_code()
    {
        WpfTestHost.OnStaThread(() =>
        {
            WpfTestHost.EnsureApplicationResources();
            var store = Store();
            var window = new LicenceWindow(store, store.Check());

            try
            {
                Assert.Equal(ThisPc, window.MachineBox.Text);
                Assert.Equal("Quit", window.CloseButton.Content);

                var copied = new List<string>();
                window.CopyText = copied.Add;
                window.CopyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal([ThisPc], copied);

                // Another PC's code: said why, not taken.
                window.CodeBox.Text = Code(Day(30), machine: OtherPc);
                Assert.True(window.EnterButton.IsEnabled);
                window.OnEnter(window, new System.Windows.RoutedEventArgs());
                Assert.Equal(System.Windows.Visibility.Visible, window.ProblemText.Visibility);
                Assert.Contains("another PC", window.ProblemText.Text, StringComparison.Ordinal);
                Assert.False(window.State.IsValid);

                // This PC's: taken.
                window.CodeBox.Text = Code(Day(30));
                window.OnEnter(window, new System.Windows.RoutedEventArgs());
                Assert.True(window.State.IsValid);
                Assert.Equal("Close", window.CloseButton.Content);
                Assert.True(store.Check().IsValid);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
