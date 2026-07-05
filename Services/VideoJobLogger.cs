using System.Diagnostics;
using System.Text;

namespace VideoStreamingApi.Services;

public sealed class VideoJobLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Stopwatch _jobTimer = Stopwatch.StartNew();

    public TimeSpan Elapsed => _jobTimer.Elapsed;

    public VideoJobLogger(string logsDir, string jobId)
    {
        Directory.CreateDirectory(logsDir);
        var path = Path.Combine(logsDir, $"video_{jobId}.log");
        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public void Info(string msg)  => Write("INF", msg);
    public void Error(string msg) => Write("ERR", msg);
    public void Warn(string msg)  => Write("WRN", msg);

    public void Section(string title) => Write("---", title);

    private void Write(string level, string msg)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}";
        lock (_writer) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();

    // ── Formatting helpers ────────────────────────────────────────────────────

    public static string FmtMs(long ms)
    {
        if (ms < 1_000) return $"{ms}ms";
        var s = ms / 1_000.0;
        if (s < 60) return $"{s:F1}s";
        var m = (int)(s / 60);
        return $"{m}m {s % 60:F0}s";
    }

    public static string FmtBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1_024.0:F1} KB";
    }

    public static string FmtSpeed(long bytes, long ms) =>
        ms > 0 ? $"{bytes / 1_048_576.0 / (ms / 1_000.0):F1} MB/s" : "—";
}
