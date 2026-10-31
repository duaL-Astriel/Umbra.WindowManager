using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Opt-in diagnostic file log. Disabled unless the <c>UMBRA_WM_DIAG</c> environment variable is <c>1</c>.
/// When disabled, <see cref="GameWindowAdapter.DiagLog"/> costs nothing (no formatting, no I/O). When enabled,
/// lines are queued from the calling (game main) thread and written by a background thread, and logging
/// stops once the file reaches <see cref="MaxFileBytes"/>.
/// </summary>
internal static class DiagLogWriter
{
    internal const long MaxFileBytes = 4L * 1024 * 1024;
    private const int QueueCapacity = 4096;
    private const int MaxBatchLines = 256;

    private static readonly BlockingCollection<string> Queue = new(QueueCapacity);
    private static readonly object ThreadLock = new();
    private static Thread? writerThread;
    private static int pending;

    private static volatile bool enabled =
        string.Equals(Environment.GetEnvironmentVariable("UMBRA_WM_DIAG"), "1", StringComparison.Ordinal);

    internal static bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    internal static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "wm-game-diag.log");

    internal static void Enqueue(string message)
    {
        EnsureWriterThread();

        Interlocked.Increment(ref pending);
        if (!Queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff} {message}\n"))
        {
            // Queue full: drop the line rather than block the game thread.
            Interlocked.Decrement(ref pending);
        }
    }

    /// <summary>Blocks until all queued lines were written (or dropped). Intended for tests.</summary>
    internal static bool Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref pending) > 0)
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(5);
        }

        return true;
    }

    private static void EnsureWriterThread()
    {
        if (writerThread != null) return;

        lock (ThreadLock)
        {
            if (writerThread != null) return;

            writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "Umbra.WindowManager.DiagLog" };
            writerThread.Start();
        }
    }

    private static void WriterLoop()
    {
        var batch = new StringBuilder();
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            var count = 1;
            batch.Clear().Append(first);
            while (count < MaxBatchLines && Queue.TryTake(out var next))
            {
                batch.Append(next);
                count++;
            }

            try
            {
                var path = LogPath;
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (length < MaxFileBytes)
                {
                    File.AppendAllText(path, batch.ToString());
                }
            }
            catch
            {
                // Best effort
            }
            finally
            {
                Interlocked.Add(ref pending, -count);
            }
        }
    }
}

/// <summary>
/// Interpolated-string handler for <see cref="GameWindowAdapter.DiagLog"/>. When diagnostics are disabled the
/// compiler skips evaluating and formatting every interpolation hole, so call sites need no explicit guard.
/// </summary>
[InterpolatedStringHandler]
internal ref struct DiagLogHandler
{
    private DefaultInterpolatedStringHandler inner;
    private readonly bool active;

    public DiagLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        this.active = shouldAppend = DiagLogWriter.Enabled;
        this.inner = this.active ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => this.inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => this.inner.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => this.inner.AppendFormatted(value, format);

    internal void Write()
    {
        if (this.active)
        {
            DiagLogWriter.Enqueue(this.inner.ToStringAndClear());
        }
    }
}
