using Sirius.Core.Nodes;

namespace Sirius.Core.Graph;

/// <summary>
/// The files a node's settings name, as they were when it last ran - so a
/// file changed under the same name makes the node run again.
/// </summary>
/// <remarks>
/// <para>
/// A node is re-run when a setting changes or something upstream does; a
/// file on disk is neither. A template taught again, a model retrained, a
/// calibration redone - saved over the file a node reads, under the same
/// name - changed nothing the scheduler could see, and the node kept its
/// old result until somebody renamed the file. Asked for by the test team,
/// 16 November 2026, writing a template-matching custom node.
/// </para>
/// <para>
/// The files are the settings edited as a file path
/// (<see cref="ParamEditor.FilePath"/>) - built-in nodes', and custom
/// nodes' in C# or Python - other than those a node writes
/// (<see cref="ParamAttribute.SaveFile"/>: a log, saved pictures).
/// Each is known by its last-write time and its length, read after the
/// node runs, whether it succeeded or not: a node that writes the file it
/// names does not set itself off again, and a file missing then and there
/// now counts as changed.
/// </para>
/// </remarks>
internal static class FileStamps
{
    /// <summary>The files a node's settings name, and how each was when it was last read; empty for a node with none.</summary>
    public static IReadOnlyDictionary<string, (DateTime Written, long Length)> Take(VisionNode node)
    {
        Dictionary<string, (DateTime, long)>? stamps = null;

        foreach (var path in Paths(node))
        {
            stamps ??= new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
            stamps[path] = Stamp(path);
        }

        return stamps ?? Empty;
    }

    /// <summary>Whether any file differs from when the node last ran - written since, replaced, made, or removed.</summary>
    public static bool Changed(VisionNode node, IReadOnlyDictionary<string, (DateTime Written, long Length)> taken)
    {
        if (taken.Count == 0)
            return false;

        foreach (var path in Paths(node))
        {
            // A path changed is a setting changed, which invalidates by itself.
            if (taken.TryGetValue(path, out var then) && then != Stamp(path))
                return true;
        }

        return false;
    }

    private static readonly IReadOnlyDictionary<string, (DateTime, long)> Empty =
        new Dictionary<string, (DateTime, long)>();

    private static IEnumerable<string> Paths(VisionNode node)
    {
        foreach (var parameter in node.Descriptor.Parameters)
        {
            if (parameter.Attribute.Editor != ParamEditor.FilePath || parameter.Attribute.SaveFile)
                continue;

            string? path;

            try
            {
                path = parameter.GetValue(node) as string;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(path))
                yield return path.Trim();
        }
    }

    /// <summary>A file's last-write time and length; for a folder its time alone; for nothing there, nothing.</summary>
    private static (DateTime, long) Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);

            if (file.Exists)
                return (file.LastWriteTimeUtc, file.Length);

            var folder = new DirectoryInfo(path);
            return folder.Exists ? (folder.LastWriteTimeUtc, -1) : (DateTime.MinValue, -2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (DateTime.MinValue, -3);
        }
    }
}
