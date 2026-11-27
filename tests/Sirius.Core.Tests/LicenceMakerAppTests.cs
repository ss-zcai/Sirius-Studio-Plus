using System.IO;
using System.Security.Cryptography;
using Sirius.Core.Licensing;
using Sirius.Tools.LicenceMakerApp;
using Xunit;

namespace Sirius.Core.Tests;

/// <summary>
/// Sirius Licence Maker's window: a code made for a PC, as long as chosen,
/// logged beside the key. With a key of the test's own, never Sirius'.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LicenceMakerAppTests : IDisposable
{
    private static readonly string Pc = MachineId.From("a tester's pc");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sirius-licence-maker-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyFile;

    public LicenceMakerAppTests(WpfTestHost host)
    {
        _ = host;
        Directory.CreateDirectory(_folder);
        _keyFile = Path.Combine(_folder, "test-key.pem");
        File.WriteAllText(_keyFile, _key.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        _key.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LicenceMakerViewModel Maker() => new(_keyFile, () => new DateOnly(2026, 11, 27));

    [Fact]
    public void A_trial_of_days_is_made_for_the_machine_and_logged()
    {
        var maker = Maker();
        Assert.True(maker.HasKey);
        Assert.False(maker.MakeCommand.CanExecute(null));

        // Pasted as the program shows it, or near enough.
        maker.MachineText = " " + Pc.ToLowerInvariant() + " ";
        Assert.Null(maker.MachineProblem);
        maker.To = "Wang, QA";
        maker.DaysText = "14";
        Assert.Equal("Runs to 10 December 2026, that day included.", maker.LastDayText);

        Assert.True(maker.MakeCommand.CanExecute(null));
        maker.MakeCommand.Execute(null);

        var licence = LicenceCode.Read(maker.Code, _key.ExportSubjectPublicKeyInfo());
        Assert.Equal(new Licence("IV", Pc, new DateOnly(2026, 11, 27), new DateOnly(2026, 12, 10), "Wang, QA"), licence);
        Assert.StartsWith($"IV for {Pc}, to Wang, QA", maker.Result);

        // A test key is not the one the programs were built for: said.
        Assert.Contains("will not take this code", maker.Problem, StringComparison.Ordinal);

        // Logged beside the key, the name with its comma quoted.
        var log = File.ReadAllLines(Path.Combine(_folder, "issued-codes.csv"));
        Assert.Equal("Made,Product,Machine ID,To,Last Day,Code", log[0]);
        Assert.Contains($",IV,{Pc},\"Wang, QA\",2026-12-10,{maker.Code}", log[1], StringComparison.Ordinal);

        // Copied.
        var copied = new List<string>();
        maker.CopyText = copied.Add;
        maker.CopyCommand.Execute(null);
        Assert.Equal([maker.Code], copied);
    }

    [Fact]
    public void A_trial_to_a_day_or_no_end()
    {
        var maker = Maker();
        maker.MachineText = Pc;

        maker.IsUntil = true;
        maker.Until = new DateTime(2026, 12, 31);
        maker.MakeCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 12, 31), LicenceCode.Read(maker.Code, _key.ExportSubjectPublicKeyInfo())!.LastDay);

        // Not a day gone.
        maker.Until = new DateTime(2026, 11, 1);
        Assert.False(maker.MakeCommand.CanExecute(null));

        maker.IsNoEnd = true;
        Assert.Equal("A full licence: it runs for good on that PC.", maker.LastDayText);
        maker.MakeCommand.Execute(null);
        Assert.Null(LicenceCode.Read(maker.Code, _key.ExportSubjectPublicKeyInfo())!.LastDay);

        Assert.Equal(3, File.ReadAllLines(Path.Combine(_folder, "issued-codes.csv")).Length);
    }

    [Fact]
    public void Nothing_is_made_without_a_machine_id_days_or_a_key()
    {
        var maker = Maker();

        maker.MachineText = "KNVY-PVC1";
        Assert.NotNull(maker.MachineProblem);
        Assert.False(maker.MakeCommand.CanExecute(null));

        maker.MachineText = Pc;
        maker.DaysText = "0";
        Assert.Equal("Days: 1 to 3650.", maker.LastDayText);
        Assert.False(maker.MakeCommand.CanExecute(null));

        maker.DaysText = "7";
        Assert.True(maker.MakeCommand.CanExecute(null));

        maker.KeyFile = Path.Combine(_folder, "no such key.pem");
        Assert.False(maker.HasKey);
        Assert.Contains("not there", maker.KeyText, StringComparison.Ordinal);
        Assert.False(maker.MakeCommand.CanExecute(null));
    }

    [Fact]
    public void A_pasted_code_is_read_back_against_the_programs_key()
    {
        var maker = Maker();

        maker.CodeToRead = "ABCDE-FGHJK";
        Assert.StartsWith("Not a genuine", maker.ReadText, StringComparison.Ordinal);

        maker.CodeToRead = string.Empty;
        Assert.Equal(string.Empty, maker.ReadText);
    }

    [Fact]
    public void The_window_opens_on_the_maker()
    {
        WpfTestHost.OnStaThread(() =>
        {
            var window = new MainWindow(Maker());

            try
            {
                Assert.Equal("Sirius Licence Maker", window.Title);
                Assert.IsType<LicenceMakerViewModel>(window.DataContext);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
