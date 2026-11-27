using System.Security.Cryptography;
using System.Text;

namespace Sirius.Core.Licensing;

/// <summary>
/// This PC's machine ID - "7KQ2-M9XD-4TWA-PB31" - which a licence code is
/// made for. Taken from the ID Windows gives the installation, hashed, so
/// it says nothing of the PC; a reinstalled Windows is a new ID.
/// </summary>
public static class MachineId
{
    /// <summary>Its bytes: 16 letters of base32.</summary>
    internal const int Length = 10;

    /// <summary>This PC's.</summary>
    public static string Current() => From(WindowsId() ?? Environment.MachineName);

    /// <summary>The ID for a PC known by <paramref name="identity"/>.</summary>
    public static string From(string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("Sirius Vision machine/" + identity.Trim().ToLowerInvariant()));
        return Base32.Grouped(Base32.Encode(hash[..Length]), 4);
    }

    /// <summary>
    /// An ID as typed or pasted, written as it is shown - or null when it is
    /// not one.
    /// </summary>
    public static string? Normalise(string? text)
    {
        var letters = new string((text ?? string.Empty).Where(c => c is not ('-' or ' ' or '\r' or '\n' or '\t')).ToArray());

        return letters.Length == 16 && Base32.Decode(letters) is { Length: Length } bytes
            ? Base32.Grouped(Base32.Encode(bytes), 4)
            : null;
    }

    /// <summary>Windows' MachineGuid, from the 64-bit registry whatever the process.</summary>
    private static string? WindowsId()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string is { Length: > 0 } id ? id : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
