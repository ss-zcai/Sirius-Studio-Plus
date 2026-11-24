using System.Buffers.Binary;
using System.IO;

namespace Sirius.IV;

/// <summary>
/// A picture's width and height, read from the start of its file without
/// decoding it - for the file list, which shows them for every picture and
/// cannot afford to open each one.
/// </summary>
/// <remarks>
/// PNG, JPEG, BMP and TIFF: the files the list shows. Each keeps its size
/// near the start - PNG and BMP at a fixed place, JPEG in its frame header
/// after any EXIF block, TIFF in its first directory - so a few reads of a
/// few bytes find it. Anything not understood is null, never an exception.
/// </remarks>
public static class ImageHeader
{
    /// <summary>How far into a JPEG to look for its frame header before giving up.</summary>
    private const long JpegSearchLimit = 4 * 1024 * 1024;

    /// <summary>The picture's size, or null when the file cannot be read or is not understood.</summary>
    public static (int Width, int Height)? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The size of the picture a stream starts with, or null.</summary>
    public static (int Width, int Height)? Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[26];
        var count = Fill(stream, head);
        head = head[..count];

        (int, int)? size = head switch
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Png(head),
            [0xFF, 0xD8, ..] => Jpeg(stream),
            [(byte)'B', (byte)'M', ..] => Bmp(head),
            [(byte)'I', (byte)'I', 42, 0, ..] => Tiff(stream, head, littleEndian: true),
            [(byte)'M', (byte)'M', 0, 42, ..] => Tiff(stream, head, littleEndian: false),
            _ => null,
        };

        return size is (> 0, > 0) ? size : null;
    }

    // ---- PNG: the IHDR chunk comes first ----------------------------------------------

    private static (int, int)? Png(ReadOnlySpan<byte> head) =>
        head.Length >= 24 && head[12..16].SequenceEqual("IHDR"u8)
            ? (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]))
            : null;

    // ---- BMP: the header after the file header ----------------------------------------

    private static (int, int)? Bmp(ReadOnlySpan<byte> head)
    {
        if (head.Length < 26)
            return null;

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(head[14..]);

        // The old OS/2 header has 16-bit sizes; every other kind 32-bit, a
        // negative height meaning the rows run top-down.
        return headerSize == 12
            ? (BinaryPrimitives.ReadUInt16LittleEndian(head[18..]), BinaryPrimitives.ReadUInt16LittleEndian(head[20..]))
            : (BinaryPrimitives.ReadInt32LittleEndian(head[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])));
    }

    // ---- JPEG: segment after segment to the frame header --------------------------------

    private static (int, int)? Jpeg(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[7];
        stream.Position = 2;

        while (stream.Position < JpegSearchLimit)
        {
            // A marker: 0xFF, perhaps padded with more, then its code.
            if (stream.ReadByte() != 0xFF)
                return null;

            int code;

            do
            {
                code = stream.ReadByte();
            }
            while (code == 0xFF);

            switch (code)
            {
                case < 0:
                    return null;

                // Markers with nothing after them.
                case 0x01 or (>= 0xD0 and <= 0xD7):
                    continue;

                // The scan begins, or the picture ends: no frame header before it.
                case 0xDA or 0xD9:
                    return null;
            }

            if (Fill(stream, buffer[..2]) < 2)
                return null;

            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer);

            if (length < 2)
                return null;

            // Start of frame, of any kind - not the tables that share its codes.
            if (code is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (Fill(stream, buffer[..5]) < 5)
                    return null;

                return (BinaryPrimitives.ReadUInt16BigEndian(buffer[3..]), BinaryPrimitives.ReadUInt16BigEndian(buffer[1..]));
            }

            stream.Seek(length - 2, SeekOrigin.Current);
        }

        return null;
    }

    // ---- TIFF: the first directory's ImageWidth and ImageLength ---------------------------------

    private static (int, int)? Tiff(Stream stream, ReadOnlySpan<byte> head, bool littleEndian)
    {
        if (head.Length < 8)
            return null;

        var directory = UInt32(head[4..], littleEndian);

        if (directory < 8 || directory >= stream.Length)
            return null;

        Span<byte> entry = stackalloc byte[12];
        stream.Position = directory;

        if (Fill(stream, entry[..2]) < 2)
            return null;

        var entries = UInt16(entry, littleEndian);
        int width = 0, height = 0;

        for (var i = 0; i < entries && (width == 0 || height == 0); i++)
        {
            if (Fill(stream, entry) < 12)
                return null;

            var tag = UInt16(entry, littleEndian);

            if (tag is not (256 or 257))
                continue;

            // SHORT or LONG, held in the entry itself.
            var value = UInt16(entry[2..], littleEndian) switch
            {
                3 => UInt16(entry[8..], littleEndian),
                4 => (int)Math.Min(UInt32(entry[8..], littleEndian), int.MaxValue),
                _ => 0,
            };

            if (tag == 256)
                width = value;
            else
                height = value;
        }

        return (width, height);
    }

    private static int UInt16(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint UInt32(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    /// <summary>Reads until the span is full or the stream ends; how many bytes were read.</summary>
    private static int Fill(Stream stream, Span<byte> buffer)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }
}
