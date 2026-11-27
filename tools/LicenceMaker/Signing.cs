using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Sirius.Core.Licensing;

namespace Sirius.Tools.LicenceMaker;

/// <summary>
/// Making codes with the private key: where the key is kept, signing, and
/// the log of codes made - shared by the command line and the window.
/// </summary>
public static class Signing
{
    /// <summary>The programs codes are made for: "IV" is Sirius IV.</summary>
    public static IReadOnlyList<(string Product, string Name)> Products { get; } = [("IV", "Sirius IV")];

    /// <summary>
    /// %LOCALAPPDATA%\Sirius Licensing\signing-key.pem, unless
    /// SIRIUS_LICENCE_KEY names another.
    /// </summary>
    public static string DefaultKeyFile =>
        Environment.GetEnvironmentVariable("SIRIUS_LICENCE_KEY") is { Length: > 0 } file
            ? file
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sirius Licensing", "signing-key.pem");

    /// <summary>The private key in <paramref name="file"/>.</summary>
    public static ECDsa LoadKey(string file)
    {
        if (!File.Exists(file))
            throw new IOException($"No signing key at {file}.");

        var key = ECDsa.Create();

        try
        {
            key.ImportFromPem(File.ReadAllText(file));
        }
        catch (ArgumentException ex)
        {
            key.Dispose();
            throw new IOException($"{file} is not a private key: {ex.Message}", ex);
        }

        return key;
    }

    /// <summary>The licence's code, signed with the key in <paramref name="keyFile"/>, and logged beside it.</summary>
    public static string Make(Licence licence, string keyFile)
    {
        using var key = LoadKey(keyFile);
        var code = LicenceCode.Make(licence, key);
        Log(keyFile, licence, code);
        return code;
    }

    /// <summary>The programs as built take it: the key is the one their public key is half of.</summary>
    public static bool TakenByPrograms(string code) => LicenceCode.Read(code, SiriusLicensing.PublicKey) is not null;

    /// <summary>"IV for KNVY-…, to Line 3 Lab: made 27 November 2026, runs to 3 December 2026 inclusive".</summary>
    public static string Describe(Licence licence) =>
        $"{licence.Product} for {licence.MachineId}"
        + (licence.To is { } to ? $", to {to}" : string.Empty)
        + $": made {LicenceStore.Day(licence.Issued)}, "
        + (licence.LastDay is { } last ? $"runs to {LicenceStore.Day(last)} inclusive" : "no end");

    /// <summary>issued-codes.csv beside the key: every code made, to look up who has which.</summary>
    public static string LogFile(string keyFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(keyFile))!, "issued-codes.csv");

    private static void Log(string keyFile, Licence licence, string code)
    {
        var file = LogFile(keyFile);
        var line = new StringBuilder();

        if (!File.Exists(file))
            line.AppendLine("Made,Product,Machine ID,To,Last Day,Code");

        line.AppendLine(string.Join(",",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            licence.Product,
            licence.MachineId,
            Csv(licence.To ?? string.Empty),
            licence.LastDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "no end",
            code));

        File.AppendAllText(file, line.ToString());
    }

    private static string Csv(string text) =>
        text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
