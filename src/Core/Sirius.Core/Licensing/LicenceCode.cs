using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sirius.Core.Licensing;

/// <summary>
/// What a licence code says: the product, the PC it is for, when it was made
/// and the last day it runs - none, for a licence with no end.
/// </summary>
/// <param name="Product">"IV" for Sirius IV.</param>
/// <param name="MachineId">The PC's machine ID, as <see cref="Licensing.MachineId"/> shows it.</param>
/// <param name="Issued">The day it was made.</param>
/// <param name="LastDay">The last day it runs, that day included; null for no end.</param>
/// <param name="To">Who it is for, when said; shown, not checked.</param>
public sealed record Licence(string Product, string MachineId, DateOnly Issued, DateOnly? LastDay, string? To = null)
{
    /// <summary>A trial, or anything else that ends.</summary>
    public bool IsTrial => LastDay is not null;
}

/// <summary>
/// A licence written as a code to paste: what it says, signed with Sirius'
/// private key (ECDSA P-256), in Crockford base32 in groups of five. The
/// program holds only the public key, so it can tell a code is genuine but
/// cannot make one; codes are made with <c>tools/LicenceMaker</c>.
/// </summary>
/// <remarks>
/// The bytes: a version (1); the product, its length first; the machine ID's
/// 10 bytes; the day made and the last day, each days since 1 January 2024
/// (0 for no end); who it is for, its length first; then the 64-byte
/// signature of all that.
/// </remarks>
public static class LicenceCode
{
    private const byte Version = 1;
    private const int SignatureLength = 64;
    private const int MaxToBytes = 64;
    private static readonly DateOnly Epoch = new(2024, 1, 1);

    /// <summary>Makes a code for <paramref name="licence"/>, signed with <paramref name="privateKey"/>.</summary>
    public static string Make(Licence licence, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(licence);
        ArgumentNullException.ThrowIfNull(privateKey);

        var payload = Payload(licence);
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Base32.Grouped(Base32.Encode([.. payload, .. signature]), 5);
    }

    /// <summary>
    /// Reads a code, as pasted - case, spaces, dashes and line breaks do not
    /// matter. Null when it is not a genuine Sirius code: cut short, altered,
    /// or signed with another key.
    /// </summary>
    public static Licence? Read(string code, ReadOnlySpan<byte> publicKey)
    {
        if (Base32.Decode(code) is not { Length: > SignatureLength } bytes)
            return null;

        var payload = bytes.AsSpan(0, bytes.Length - SignatureLength);
        var signature = bytes.AsSpan(bytes.Length - SignatureLength);

        using var key = ECDsa.Create();

        try
        {
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return null;

        return Parse(payload);
    }

    private static byte[] Payload(Licence licence)
    {
        var product = Encoding.ASCII.GetBytes(licence.Product);
        var machine = Licensing.MachineId.Normalise(licence.MachineId) is { } id ? Base32.Decode(id) : null;
        var to = Encoding.UTF8.GetBytes(licence.To ?? string.Empty);

        if (product.Length is 0 or > 16)
            throw new ArgumentException("A product is 1 to 16 letters.", nameof(licence));

        if (machine is not { Length: Licensing.MachineId.Length })
            throw new ArgumentException($"\"{licence.MachineId}\" is not a machine ID.", nameof(licence));

        if (to.Length > MaxToBytes)
            throw new ArgumentException($"Who it is for is at most {MaxToBytes} bytes.", nameof(licence));

        if (licence.LastDay is { } last && last < licence.Issued)
            throw new ArgumentException("The last day is before the day it is made.", nameof(licence));

        var bytes = new List<byte> { Version, (byte)product.Length };
        bytes.AddRange(product);
        bytes.AddRange(machine);
        bytes.AddRange(Day(licence.Issued));
        bytes.AddRange(licence.LastDay is { } day ? Day(day) : [0, 0]);
        bytes.Add((byte)to.Length);
        bytes.AddRange(to);
        return [.. bytes];
    }

    private static byte[] Day(DateOnly day)
    {
        var days = day.DayNumber - Epoch.DayNumber;

        if (days is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(day), day, "A day from 2 January 2024 on.");

        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)days);
        return bytes;
    }

    private static Licence? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != Version)
            return null;

        var at = 1;
        int productLength = payload[at++];

        if (payload.Length < at + productLength + Licensing.MachineId.Length + 5)
            return null;

        var product = Encoding.ASCII.GetString(payload.Slice(at, productLength));
        at += productLength;

        var machine = Base32.Grouped(Base32.Encode(payload.Slice(at, Licensing.MachineId.Length).ToArray()), 4);
        at += Licensing.MachineId.Length;

        var issued = Epoch.AddDays(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2)));
        at += 2;

        var last = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(at, 2));
        at += 2;

        int toLength = payload[at++];

        if (payload.Length != at + toLength)
            return null;

        var to = toLength == 0 ? null : Encoding.UTF8.GetString(payload.Slice(at, toLength));

        return new Licence(product, machine, issued, last == 0 ? null : Epoch.AddDays(last), to);
    }
}

/// <summary>
/// Crockford's base32: digits and capitals without I, L, O and U, so a code
/// read aloud or typed again has nothing to mistake - and read back, O is
/// taken for 0 and I and L for 1.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(byte[] bytes)
    {
        var text = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return text.ToString();
    }

    /// <summary>The bytes; null when the text holds a letter base32 has not.</summary>
    public static byte[]? Decode(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;

        foreach (var raw in text)
        {
            if (raw is '-' or ' ' or '\r' or '\n' or '\t')
                continue;

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };

            var value = Alphabet.IndexOf(c);

            if (value < 0)
                return null;

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }

            buffer &= (1 << bits) - 1;
        }

        return [.. bytes];
    }

    /// <summary>"ABCDE-FGHJK-…".</summary>
    public static string Grouped(string text, int size) =>
        string.Join("-", Enumerable.Range(0, (text.Length + size - 1) / size)
            .Select(group => text.Substring(group * size, Math.Min(size, text.Length - group * size))));
}
