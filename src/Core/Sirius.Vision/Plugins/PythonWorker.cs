using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Sirius.Vision.Plugins;

/// <summary>
/// Something a Python library could not do, already worded for the operator.
/// </summary>
public sealed class PythonException : Exception
{
    public PythonException(string message)
        : base(message)
    {
    }
}

/// <summary>Bytes to send after a request's header - an image's pixels, written straight from where they are.</summary>
internal readonly record struct OutgoingBlob(long Length, Action<Stream> Write);

/// <summary>What a worker answered: its header, and the pixels of any images in it.</summary>
internal sealed record PythonReply(JsonObject Header, IReadOnlyList<byte[]> Blobs);

/// <summary>
/// The Python process one library runs in.
/// </summary>
/// <remarks>
/// <para>
/// Started with the library's own interpreter, so each library has its own
/// packages, and a crash in one - a CUDA fault, a segfault in an extension -
/// ends that process rather than the application. The next run starts it
/// again.
/// </para>
/// <para>
/// It stays running between images, which is what keeps a model loaded: a
/// network that takes ten seconds to load is loaded once, not once a part.
/// One request at a time - Python runs one anyway - so nodes from the same
/// library queue behind each other.
/// </para>
/// </remarks>
internal sealed class PythonWorker : IDisposable
{
    private const int TailLines = 40;

    private static readonly ConcurrentDictionary<PythonWorker, byte> Running = new();

    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _tail = new();
    private Process? _process;
    private Stream? _input;
    private Stream? _output;
    private long _call;
    private long _activeCall;

    static PythonWorker()
    {
        // Workers would end anyway when their input closes with this
        // process, but only after finishing whatever they were doing.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var worker in Running.Keys)
                worker.Dispose();
        };
    }

    public PythonWorker(string script, PythonInterpreter interpreter)
    {
        Script = script;
        Interpreter = interpreter;
    }

    /// <summary>The library file.</summary>
    public string Script { get; }

    public PythonInterpreter Interpreter { get; }

    /// <summary>How long a library has to import before it is given up on - a CUDA stack can take a while.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a cancelled run is given to finish before the process is ended.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The worker script, shipped beside the application.</summary>
    public static string WorkerScript { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "python", "sirius_worker.py");

    /// <summary>The hello the library sent when it last started: its nodes and versions.</summary>
    public JsonObject? Hello { get; private set; }

    /// <summary>"Python 3.12.4", once it has started.</summary>
    public string PythonTitle => Hello?["python"]?.GetValue<string>() is { } version ? $"Python {version}" : "Python";

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    /// <summary>
    /// Starts the process if it is not running and returns its hello.
    /// </summary>
    /// <exception cref="PythonException">It would not start, or the library would not import.</exception>
    public JsonObject Start()
    {
        lock (_gate)
            return EnsureRunning();
    }

    /// <summary>
    /// Sends one request and waits for its answer, starting the process
    /// first if it is not running.
    /// </summary>
    /// <remarks>
    /// Cancelling does not interrupt Python - nothing can, safely - so the
    /// run is given <see cref="CancelGrace"/> to finish, and the process is
    /// ended only if it has not. The answer is then thrown away either way.
    /// </remarks>
    public PythonReply Call(JsonObject request, IReadOnlyList<OutgoingBlob> blobs, string doing, CancellationToken cancel)
    {
        lock (_gate)
        {
            cancel.ThrowIfCancellationRequested();
            EnsureRunning();

            var process = _process!;
            var call = ++_call;
            _activeCall = call;

            using var registration = cancel.Register(() => EndLater(process, call));

            try
            {
                WriteFrame(_input!, request, blobs);
                var reply = ReadFrame(_output!);
                cancel.ThrowIfCancellationRequested();
                return reply;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or InvalidDataException)
            {
                if (cancel.IsCancellationRequested)
                {
                    StopLocked();
                    throw new OperationCanceledException(cancel);
                }

                var message = Stopped(process, $"while {doing}") + " It will be started again on the next run.";
                StopLocked();
                throw new PythonException(message);
            }
            finally
            {
                _activeCall = 0;
            }
        }
    }

    /// <summary>Ends the process; the next call starts a fresh one, importing the library again.</summary>
    public void Restart()
    {
        lock (_gate)
            StopLocked();
    }

    public void Dispose()
    {
        // Not under the gate: at exit, a run may be holding it.
        var process = _process;
        _process = null;
        End(process);
        Running.TryRemove(this, out _);
    }

    /// <summary>The last lines the library wrote to stderr, oldest first.</summary>
    public string Tail(int lines = 6) =>
        string.Join(Environment.NewLine, _tail.ToArray().TakeLast(lines));

    // ---- process ----------------------------------------------------------------

    private JsonObject EnsureRunning()
    {
        if (_process is { HasExited: false } && Hello is not null)
            return Hello;

        StopLocked();

        if (!File.Exists(WorkerScript))
        {
            throw new PythonException(
                $"The Python worker script is missing from {Path.GetDirectoryName(WorkerScript)}. Reinstall the application.");
        }

        var start = new ProcessStartInfo(Interpreter.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Script)!,
        };

        foreach (var argument in Interpreter.Arguments)
            start.ArgumentList.Add(argument);

        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(WorkerScript);
        start.ArgumentList.Add(Script);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUNBUFFERED"] = "1";

        _tail.Clear();
        var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (sender, line) =>
        {
            if (line.Data is null)
                return;

            _tail.Enqueue(line.Data);
            while (_tail.Count > TailLines)
                _tail.TryDequeue(out _);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new PythonException(
                $"Python could not be started ({Interpreter.Executable}, {Interpreter.Source}): {ex.Message}");
        }

        process.BeginErrorReadLine();
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        Running[this] = 0;

        var output = _output;
        var hello = Task.Run(() => ReadFrame(output));

        bool answered;
        try
        {
            answered = hello.Wait(StartTimeout);
        }
        catch (AggregateException)
        {
            answered = true;
        }

        if (!answered)
        {
            StopLocked();
            throw new PythonException(
                $"{Path.GetFileName(Script)} did not finish loading within {StartTimeout.TotalSeconds:0} seconds, " +
                $"so it was stopped.{Said()}");
        }

        if (hello.IsFaulted)
        {
            var message = Stopped(process, $"before it could load {Path.GetFileName(Script)}");
            StopLocked();
            throw new PythonException(message);
        }

        var header = hello.Result.Header;

        if (header["ok"]?.GetValue<bool>() != true)
        {
            var message = ImportFailure(header);
            StopLocked();
            throw new PythonException(message);
        }

        Hello = header;
        return header;
    }

    private void StopLocked()
    {
        var process = _process;
        _process = null;
        _input = null;
        _output = null;
        End(process);
        Running.TryRemove(this, out _);
    }

    private static void End(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }

        process.Dispose();
    }

    /// <summary>After the grace period, ends a run that is still going.</summary>
    private void EndLater(Process process, long call)
    {
        _ = Task.Delay(CancelGrace).ContinueWith(_ =>
        {
            if (Interlocked.Read(ref _activeCall) != call)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Finished in the meantime.
            }
        }, TaskScheduler.Default);
    }

    // ---- messages -----------------------------------------------------------------

    private string Stopped(Process process, string when)
    {
        string code;
        try
        {
            code = process.WaitForExit(2000) ? $" (exit code {process.ExitCode})" : "";
        }
        catch (InvalidOperationException)
        {
            code = "";
        }

        return $"{PythonTitle} ({Interpreter.Executable}) stopped {when}{code}.{Said()}";
    }

    private string Said()
    {
        // Give the reader a moment to collect what the process wrote last.
        Thread.Sleep(50);
        var tail = Tail();
        return tail.Length == 0 ? "" : $" It said:{Environment.NewLine}{tail}";
    }

    private string ImportFailure(JsonObject header)
    {
        var version = header["python"]?.GetValue<string>();
        var who = version is null ? "Python" : $"Python {version}";
        var error = header["error"]?.GetValue<string>() ?? "it did not say why";
        var file = Path.GetFileName(Script);

        var hint = error.StartsWith("ModuleNotFoundError", StringComparison.Ordinal) ||
                   error.StartsWith("ImportError", StringComparison.Ordinal)
            ? $" Install it in that environment, or name the environment that has it in {PythonInterpreter.SettingsFile} " +
              "beside the library."
            : "";

        return $"{file} could not be loaded by {who} ({Interpreter.Executable}): {error}" +
               $"{PythonErrors.Where(header, Script)}.{hint}";
    }

    // ---- frames ---------------------------------------------------------------------

    internal static void WriteFrame(Stream stream, JsonObject header, IReadOnlyList<OutgoingBlob> blobs)
    {
        var text = Encoding.UTF8.GetBytes(header.ToJsonString());
        Span<byte> prefix = stackalloc byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)text.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[4..], (uint)blobs.Count);
        stream.Write(prefix);
        stream.Write(text);

        foreach (var blob in blobs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)blob.Length);
            stream.Write(prefix);
            blob.Write(stream);
        }

        stream.Flush();
    }

    internal static PythonReply ReadFrame(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[8];
        stream.ReadExactly(prefix);

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        var blobCount = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);

        if (headerLength > 256 * 1024 * 1024 || blobCount > 1024)
            throw new InvalidDataException("The Python worker sent something that is not a message.");

        var text = new byte[headerLength];
        stream.ReadExactly(text);

        var header = JsonNode.Parse(text) as JsonObject
                     ?? throw new InvalidDataException("The Python worker sent a message that is not an object.");

        var blobs = new byte[blobCount][];
        for (var i = 0; i < blobCount; i++)
        {
            stream.ReadExactly(prefix);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);

            if (length > (ulong)Array.MaxLength)
                throw new InvalidDataException("The Python worker sent an image too large to receive.");

            blobs[i] = new byte[length];
            stream.ReadExactly(blobs[i]);
        }

        return new PythonReply(header, blobs);
    }
}

/// <summary>Turns a Python failure into one line an operator can act on.</summary>
internal static class PythonErrors
{
    /// <summary>
    /// " (dark_spots.py, line 12, in run)" - the deepest place in the
    /// library's own file the traceback passes through, or the deepest
    /// place of all when it never does.
    /// </summary>
    public static string Where(JsonObject header, string script)
    {
        if (header["trace"]?.GetValue<string>() is not { Length: > 0 } trace)
            return "";

        string? last = null;
        string? own = null;

        foreach (var raw in trace.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("File \"", StringComparison.Ordinal))
                continue;

            var close = line.IndexOf('"', 6);
            if (close < 0)
                continue;

            var file = line[6..close];
            if (file.StartsWith('<') ||
                file.EndsWith("sirius_worker.py", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith("sirius_nodes.py", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var place = $" ({Path.GetFileName(file)}{line[(close + 1)..]})";
            last = place;

            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(script), StringComparison.OrdinalIgnoreCase))
                own = place;
        }

        return own ?? last ?? "";
    }
}
