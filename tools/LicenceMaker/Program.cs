using System.Globalization;
using System.Security.Cryptography;
using Sirius.Core.Licensing;
using Sirius.Tools.LicenceMaker;

// Makes Sirius licence codes.
//
//   LicenceMaker keygen [--key <file>]
//       A new signing key pair: the private key written to the file, the
//       public key printed for SiriusLicensing.PublicKey. Once - a new pair
//       makes every code made before worthless.
//
//   LicenceMaker make --machine <ID> (--days <n> | --until <yyyy-MM-dd> | --forever)
//                     [--product IV] [--to <name>] [--key <file>]
//       A code for the PC with that machine ID. --days 7 runs today and the
//       six days after.
//
//   LicenceMaker read <code>
//       What a code says, and whether this build's public key takes it.
//
//   LicenceMaker machine
//       This PC's machine ID, as the programs show it.
//
// The key file is %LOCALAPPDATA%\Sirius Licensing\signing-key.pem unless
// --key or SIRIUS_LICENCE_KEY says otherwise. It must never be in the
// repository or a package: anyone with it can make codes. Every code made
// is logged in issued-codes.csv beside it. LicenceMakerApp is the same in
// a window.

return Run(args);

static int Run(string[] args)
{
    try
    {
        return args.FirstOrDefault() switch
        {
            "keygen" => KeyGen(Options(args)),
            "make" => Make(Options(args)),
            "read" => Read(string.Join(' ', args.Skip(1))),
            "machine" => Machine(),
            _ => Usage(),
        };
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or FormatException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine("""
        LicenceMaker keygen [--key <file>]
        LicenceMaker make --machine <ID> (--days <n> | --until <yyyy-MM-dd> | --forever) [--product IV] [--to <name>] [--key <file>]
        LicenceMaker read <code>
        LicenceMaker machine
        """);
    return 2;
}

static Dictionary<string, string?> Options(string[] args)
{
    var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Not understood: {args[i]}");

        var name = args[i][2..];
        options[name] = name is "forever" ? null : i + 1 < args.Length ? args[++i] : throw new ArgumentException($"--{name} needs a value.");
    }

    return options;
}

static string KeyFile(Dictionary<string, string?> options) =>
    options.GetValueOrDefault("key") ?? Signing.DefaultKeyFile;

static int KeyGen(Dictionary<string, string?> options)
{
    var file = Path.GetFullPath(KeyFile(options));

    if (File.Exists(file))
        throw new IOException($"{file} is there already. A new key makes every code made with it worthless; move it away first if that is meant.");

    if (InsideRepository(file))
        throw new IOException($"{file} is inside a git repository. The private key must be kept out of it.");

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.WriteAllText(file, key.ExportPkcs8PrivateKeyPem());

    Console.WriteLine($"Private key: {file}");
    Console.WriteLine("Keep a copy somewhere safe and private. Lost, no more codes can be made; shared, anyone can make them.");
    Console.WriteLine();
    Console.WriteLine("Public key, for SiriusLicensing.PublicKey:");
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Make(Dictionary<string, string?> options)
{
    var machine = MachineId.Normalise(options.GetValueOrDefault("machine"))
        ?? throw new ArgumentException("--machine: the machine ID the program shows, as 7KQ2-M9XD-4TWA-PB31.");

    var today = DateOnly.FromDateTime(DateTime.Now);
    DateOnly? last =
        options.ContainsKey("forever") ? null
        : options.GetValueOrDefault("days") is { } days ? today.AddDays(int.Parse(days, CultureInfo.InvariantCulture) - 1)
        : options.GetValueOrDefault("until") is { } until ? DateOnly.ParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : throw new ArgumentException("Say how long: --days <n>, --until <yyyy-MM-dd> or --forever.");

    var file = KeyFile(options);

    if (!File.Exists(file))
        throw new IOException($"No signing key at {file}. Give it with --key, or make one with keygen.");

    var licence = new Licence(options.GetValueOrDefault("product") ?? "IV", machine, today, last, options.GetValueOrDefault("to"));
    var code = Signing.Make(licence, file);

    Console.WriteLine(Signing.Describe(licence));
    Console.WriteLine();
    Console.WriteLine(code);

    if (!Signing.TakenByPrograms(code))
    {
        Console.WriteLine();
        Console.WriteLine("Warning: this build's public key does not take the code - the key file is not the one the programs were built for.");
    }

    return 0;
}

static int Read(string code)
{
    if (LicenceCode.Read(code, SiriusLicensing.PublicKey) is { } licence)
    {
        Console.WriteLine(Signing.Describe(licence));
        return 0;
    }

    Console.WriteLine("Not a genuine Sirius licence code for this build's public key.");
    return 1;
}

static int Machine()
{
    Console.WriteLine(MachineId.Current());
    return 0;
}

static bool InsideRepository(string file)
{
    for (var folder = Path.GetDirectoryName(file); folder is not null; folder = Path.GetDirectoryName(folder))
    {
        if (Directory.Exists(Path.Combine(folder, ".git")))
            return true;
    }

    return false;
}
