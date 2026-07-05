using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoStreamingApi.Services;

public class FfmpegService
{
    private readonly IConfiguration _config;
    private readonly ILogger<FfmpegService> _logger;
    private readonly string _tempPath;

    private static readonly Regex TimeRegex = new(@"time=(\d+):(\d{2}):(\d{2})\.(\d+)", RegexOptions.Compiled);

    private record Rendition(string Name, int Width, int Height, string Crf, string Bitrate, string AudioBitrate);

    private static readonly Rendition[] Renditions =
    [
        new("360p",  640,  360, "26", "800k",  "96k"),
        new("720p",  1280, 720, "23", "2500k", "128k"),
        new("1080p", 1920, 1080, "21", "5000k", "128k"),
    ];

    public FfmpegService(IConfiguration config, ILogger<FfmpegService> logger)
    {
        _config = config;
        _logger = logger;
        _tempPath = config["VideoProcessing:TempPath"]!;
    }

    public async Task<string> TranscodeToHlsAsync(
        string inputFile,
        string jobId,
        VideoJobLogger jobLog,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outputDir = Path.Combine(_tempPath, $"hls_{jobId}");

        foreach (var rendition in Renditions)
            Directory.CreateDirectory(Path.Combine(outputDir, rendition.Name));

        var seg = _config["VideoProcessing:HlsSegmentDuration"] ?? "6";
        var threads = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "0" : "4";

        jobLog.Section("FFmpeg");

        var durationSeconds = await ProbeDurationSecondsAsync(inputFile, jobLog, cancellationToken);
        jobLog.Info($"Transcode starting  renditions: 360p / 720p / 1080p (parallel)  segment: {seg}s  duration: {durationSeconds:F1}s  threads={threads}");

        var sw = Stopwatch.StartNew();
        var renditionProgress = new ConcurrentDictionary<string, int>();

        void ReportAggregate()
        {
            if (progress == null || renditionProgress.IsEmpty) return;
            var avg = (int)renditionProgress.Values.Average();
            progress.Report(Math.Clamp(avg, 0, 100));
        }

        var tasks = Renditions.Select(rendition => RunRenditionAsync(
            inputFile, outputDir, rendition, seg, threads, durationSeconds, jobLog,
            percent =>
            {
                renditionProgress[rendition.Name] = percent;
                ReportAggregate();
            },
            cancellationToken));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            jobLog.Error($"FFmpeg parallel transcode FAILED  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");
            CleanupPartialOutput(outputDir, jobLog);
            throw;
        }

        WriteMasterPlaylist(outputDir);
        progress?.Report(100);

        jobLog.Info($"Transcode complete  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");
        return outputDir;
    }

    private async Task RunRenditionAsync(
        string inputFile,
        string outputDir,
        Rendition rendition,
        string segmentDuration,
        string threads,
        double durationSeconds,
        VideoJobLogger jobLog,
        Action<int> onProgress,
        CancellationToken cancellationToken)
    {
        var renditionDir = Path.Combine(outputDir, rendition.Name);
        var argList = BuildRenditionArgs(inputFile, renditionDir, rendition, segmentDuration, threads);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in argList)
            startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stderrBuf = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderrBuf.AppendLine(e.Data);

            if (durationSeconds > 0)
            {
                var match = TimeRegex.Match(e.Data);
                if (match.Success)
                {
                    var elapsed = TimeSpan.Parse(
                        $"{match.Groups[1].Value}:{match.Groups[2].Value}:{match.Groups[3].Value}.{match.Groups[4].Value}",
                        CultureInfo.InvariantCulture);
                    var percent = (int)Math.Clamp(elapsed.TotalSeconds / durationSeconds * 100, 0, 100);
                    onProgress(percent);
                }
            }
        };

        var sw = Stopwatch.StartNew();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process, jobLog, rendition.Name);
            throw;
        }

        if (process.ExitCode != 0)
        {
            jobLog.Error($"FFmpeg [{rendition.Name}] FAILED  exit={process.ExitCode}  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");
            jobLog.Error($"--- FFmpeg [{rendition.Name}] stderr ---");
            foreach (var line in stderrBuf.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                jobLog.Error(line.TrimEnd());

            throw new Exception($"FFmpeg transcoding failed for rendition {rendition.Name} with exit code {process.ExitCode}");
        }

        onProgress(100);
        jobLog.Info($"FFmpeg [{rendition.Name}] complete  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");
    }

    private static void TryKill(Process process, VideoJobLogger jobLog, string renditionName)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                jobLog.Warn($"FFmpeg [{renditionName}] cancelled — process killed");
            }
        }
        catch
        {
            // process may have already exited between the check and the kill
        }
    }

    private static void CleanupPartialOutput(string outputDir, VideoJobLogger jobLog)
    {
        try
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
            jobLog.Info("Cleaned up partial HLS output");
        }
        catch (Exception ex)
        {
            jobLog.Warn($"Could not clean up partial HLS output: {ex.Message}");
        }
    }

    private static List<string> BuildRenditionArgs(
        string inputFile, string renditionDir, Rendition rendition, string segmentDuration, string threads)
    {
        var scale =
            $"scale=w={rendition.Width}:h={rendition.Height}:force_original_aspect_ratio=decrease," +
            $"pad={rendition.Width}:{rendition.Height}:(ow-iw)/2:(oh-ih)/2";

        return
        [
            "-i", inputFile,
            "-y",

            "-vf", scale,
            "-c:v", "libx264", "-crf", rendition.Crf, "-preset", "faster", "-b:v", rendition.Bitrate,
            "-c:a", "aac", "-ar", "44100", "-b:a", rendition.AudioBitrate,
            "-threads", threads,

            "-f", "hls",
            "-hls_time", segmentDuration,
            "-hls_playlist_type", "vod",
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", $"{renditionDir}/segment_%03d.ts",

            $"{renditionDir}/playlist.m3u8",
        ];
    }

    private static void WriteMasterPlaylist(string outputDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");

        foreach (var rendition in Renditions)
        {
            var bandwidth = int.Parse(rendition.Bitrate.TrimEnd('k')) * 1000;
            sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={rendition.Width}x{rendition.Height}");
            sb.AppendLine($"{rendition.Name}/playlist.m3u8");
        }

        File.WriteAllText(Path.Combine(outputDir, "master.m3u8"), sb.ToString());
    }

    private async Task<double> ProbeDurationSecondsAsync(string inputFile, VideoJobLogger jobLog, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            inputFile
        })
            startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds;

        jobLog.Warn("ffprobe could not determine video duration — progress reporting will be unavailable");
        return 0;
    }

    public async Task<string?> ExtractThumbnailAsync(string inputFile, string videoId, VideoJobLogger jobLog)
    {
        var outputPath = Path.Combine(_tempPath, $"thumb_{videoId}.jpg");
        var sw = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-ss", "5",           // fast-seek to 5 s (before -i for speed)
            "-i", inputFile,
            "-vframes", "1",      // single frame
            "-vf", "scale=640:-1",
            "-q:v", "3",
            "-y", outputPath
        })
            startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            jobLog.Error($"Thumbnail extraction failed  exit={process.ExitCode}");
            return null;
        }

        jobLog.Info($"Thumbnail extracted  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");
        return outputPath;
    }

    public async Task GenerateThumbnailAsync(string inputFile, string jobId)
    {
        var thumbDir = Path.Combine(_tempPath, $"thumbs_{jobId}");
        Directory.CreateDirectory(thumbDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "-i", inputFile,
            "-vf", "fps=1/120,scale=320:-1",
            "-q:v", "3",
            $"{thumbDir}/thumb_%04d.jpg",
            "-y"
        })
            startInfo.ArgumentList.Add(arg);

        var process = new Process { StartInfo = startInfo };
        process.Start();
        await process.WaitForExitAsync();
    }
}
