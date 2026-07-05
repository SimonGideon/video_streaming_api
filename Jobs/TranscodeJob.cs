using System.Collections.Concurrent;
using System.Diagnostics;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MarkIasVideoProcessingApi.Data;
using MarkIasVideoProcessingApi.Models;
using MarkIasVideoProcessingApi.Services;

namespace MarkIasVideoProcessingApi.Jobs;

/// <summary>
/// Hangfire background job that transcodes a video to HLS and uploads the
/// result to MinIO. Enqueued after a successful TUS upload.
/// </summary>
public class TranscodeJob
{
    private readonly MinioService _minio;
    private readonly FfmpegService _ffmpeg;
    private readonly ILogger<TranscodeJob> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, int> _progressStore;
    private readonly string _tempPath;
    private const string LogsPath = "logs/jobs";

    public TranscodeJob(
        MinioService minio,
        FfmpegService ffmpeg,
        ILogger<TranscodeJob> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        ConcurrentDictionary<string, int> progressStore)
    {
        _minio = minio;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _tempPath = config["VideoProcessing:TempPath"]!;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 900 })]
    public async Task ExecuteAsync(string videoId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarkIasVideoProcessingDbContext>();

        var video = await db.Videos.FindAsync([videoId], cancellationToken);
        if (video == null)
        {
            _logger.LogWarning("Video {Id} not found — skipping transcode job", videoId);
            return;
        }

        var jobLog = new VideoJobLogger(LogsPath, videoId);
        var tempFile = Path.Combine(_tempPath, $"{videoId}_{video.FileName}");
        var hlsDir = Path.Combine(_tempPath, $"hls_{videoId}");

        using (jobLog)
        {
            try
            {
                jobLog.Section($"TranscodeJob {videoId}");
                video.Status = VideoStatus.Processing;
                _progressStore[videoId] = 0;
                await db.SaveChangesAsync(cancellationToken);

                // Download original from MinIO to temp disk
                Directory.CreateDirectory(_tempPath);
                var sw = Stopwatch.StartNew();
                await _minio.DownloadObjectAsync(video.OriginalKey, tempFile);
                jobLog.Info($"Downloaded from MinIO  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");

                // Transcode to HLS (360p / 720p / 1080p in parallel)
                sw.Restart();
                var progress = new Progress<int>(pct => _progressStore[videoId] = pct);
                await _ffmpeg.TranscodeToHlsAsync(tempFile, videoId, jobLog, progress, cancellationToken);
                video.TranscodeMs = sw.ElapsedMilliseconds;

                // Upload HLS segments to MinIO
                sw.Restart();
                await _minio.UploadHlsFilesAsync(hlsDir, videoId, jobLog);
                video.HlsUploadMs = sw.ElapsedMilliseconds;

                // Auto-generate thumbnail
                if (string.IsNullOrEmpty(video.ThumbnailUrl))
                {
                    jobLog.Info("Generating thumbnail…");
                    var thumbPath = await _ffmpeg.ExtractThumbnailAsync(tempFile, videoId, jobLog);
                    if (thumbPath != null)
                    {
                        await using var thumbFs = File.OpenRead(thumbPath);
                        video.ThumbnailUrl = await _minio.UploadThumbnailAsync(thumbFs, thumbFs.Length, videoId);
                        File.Delete(thumbPath);
                        jobLog.Info("Thumbnail uploaded");
                    }
                }

                video.HlsUrl = _minio.GetHlsUrl(videoId);
                video.Status = VideoStatus.Ready;
                video.ProcessedAt = DateTime.UtcNow;
                _progressStore[videoId] = 100;
                await db.SaveChangesAsync(cancellationToken);

                var total = (video.TranscodeMs ?? 0) + (video.HlsUploadMs ?? 0);
                jobLog.Section("Done");
                jobLog.Info($"READY  transcode={VideoJobLogger.FmtMs(video.TranscodeMs ?? 0)}  hls-upload={VideoJobLogger.FmtMs(video.HlsUploadMs ?? 0)}  pipeline={VideoJobLogger.FmtMs(total)}");
                _logger.LogInformation("Video {Id} ready  transcode={T}  hls={H}",
                    video.Id, VideoJobLogger.FmtMs(video.TranscodeMs ?? 0), VideoJobLogger.FmtMs(video.HlsUploadMs ?? 0));
            }
            catch (Exception ex)
            {
                video.Status = VideoStatus.Failed;
                video.ErrorMessage = ex.Message;
                await db.SaveChangesAsync(cancellationToken);
                jobLog.Section("FAILED");
                jobLog.Error(ex.Message);
                _logger.LogError(ex, "TranscodeJob failed for video {Id}", videoId);
                throw; // rethrow so Hangfire retries
            }
            finally
            {
                _progressStore.TryRemove(videoId, out _);
                TryDelete(tempFile, jobLog);
                TryDeleteDir(hlsDir, jobLog);
            }
        }
    }

    private static void TryDelete(string path, VideoJobLogger jobLog)
    {
        try { if (File.Exists(path)) { File.Delete(path); jobLog.Info($"Deleted {Path.GetFileName(path)}"); } }
        catch (Exception ex) { jobLog.Warn($"Could not delete {path}: {ex.Message}"); }
    }

    private static void TryDeleteDir(string path, VideoJobLogger jobLog)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); jobLog.Info($"Deleted dir {Path.GetFileName(path)}"); } }
        catch (Exception ex) { jobLog.Warn($"Could not delete dir {path}: {ex.Message}"); }
    }
}
