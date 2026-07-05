using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MarkIasVideoProcessingApi.Data;
using MarkIasVideoProcessingApi.Services;
using MarkIasVideoProcessingApi.Models;
using MarkIasVideoProcessingApi.DTOs;

namespace MarkIasVideoProcessingApi.Controllers;

/// <summary>Video upload, processing status, and playback metadata.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class VideoController : ControllerBase
{
    private readonly MinioService _minio;
    private readonly FfmpegService _ffmpeg;
    private readonly ILogger<VideoController> _logger;
    private readonly MarkIasVideoProcessingDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, int> _progressStore;
    private readonly string _tempPath;
    private readonly string _logsPath;

    private static readonly SemaphoreSlim _transcodeSemaphore = new(3);

    public VideoController(
        MinioService minio,
        FfmpegService ffmpeg,
        ILogger<VideoController> logger,
        IConfiguration config,
        MarkIasVideoProcessingDbContext db,
        IServiceScopeFactory scopeFactory,
        ConcurrentDictionary<string, int> progressStore)
    {
        _minio = minio;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _tempPath = config["VideoProcessing:TempPath"]!;
        _logsPath = "logs/jobs";
        _db = db;
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
    }

    /// <summary>Stream transcode progress as Server-Sent Events.</summary>
    [HttpGet("{id}/progress")]
    [Produces("text/event-stream")]
    public async Task StreamProgress(string id, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var lastHeartbeat = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            var video = await _db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (video == null)
            {
                await Response.WriteAsync(": video not found\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                return;
            }

            var percent = _progressStore.TryGetValue(id, out var p) ? p : (video.Status == VideoStatus.Ready ? 100 : 0);
            var payload = JsonSerializer.Serialize(new { percent, status = video.Status.ToString() });
            await Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);

            if (video.Status is VideoStatus.Ready or VideoStatus.Failed)
                return;

            if (DateTime.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(15))
            {
                await Response.WriteAsync(": heartbeat\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                lastHeartbeat = DateTime.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>List all videos, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<VideoResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var videos = await _db.Videos.OrderByDescending(v => v.CreatedAt).ToListAsync();
        return Ok(videos.Select(ToResponse));
    }

    /// <summary>Get video metadata by id.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(VideoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id)
    {
        var video = await _db.Videos.FindAsync(id);
        return video == null ? NotFound() : Ok(ToResponse(video));
    }

    /// <summary>Get processing status and playback URLs.</summary>
    [HttpGet("{id}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(string id)
    {
        var video = await _db.Videos.FindAsync(id);
        if (video == null) return NotFound();
        return Ok(new
        {
            video.Id,
            Status = video.Status.ToString(),
            video.HlsUrl,
            video.ThumbnailUrl,
            video.ErrorMessage,
            video.TranscodeMs,
            video.HlsUploadMs,
        });
    }

    /// <summary>Upload a video in one request (for testing; production uses TUS at /api/files).</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(4_294_967_295)]
    [ProducesResponseType(typeof(VideoResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(
        IFormFile videoFile,
        [FromForm] string title)
    {
        if (videoFile == null || videoFile.Length == 0)
            return BadRequest("No file provided");

        if (!videoFile.ContentType.StartsWith("video/"))
            return BadRequest("File must be a video");

        var video = new Video
        {
            Title = title,
            FileName = videoFile.FileName,
            FileSizeBytes = videoFile.Length,
            Status = VideoStatus.Pending
        };
        _db.Videos.Add(video);

        var jobLog = new VideoJobLogger(_logsPath, video.Id);
        jobLog.Section($"Job {video.Id}");
        jobLog.Info($"Title: {title}  File: {videoFile.FileName}  Size: {VideoJobLogger.FmtBytes(videoFile.Length)}");

        Directory.CreateDirectory(_tempPath);
        var tempFile = Path.Combine(_tempPath, $"{video.Id}_{videoFile.FileName}");

        var sw = Stopwatch.StartNew();
        await using (var fs = System.IO.File.Create(tempFile))
            await videoFile.CopyToAsync(fs);
        jobLog.Info($"Buffered to disk  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}");

        sw.Restart();
        await using (var fs = System.IO.File.OpenRead(tempFile))
            video.OriginalKey = await _minio.UploadVideoAsync(fs, video.Id, videoFile.FileName, jobLog);
        video.UploadToMinioMs = sw.ElapsedMilliseconds;

        await _db.SaveChangesAsync();

        _ = Task.Run(() => ProcessVideoAsync(video.Id, tempFile, jobLog));

        _logger.LogInformation("Video {Id} queued for processing", video.Id);

        return Accepted(ToResponse(video));
    }

    /// <summary>Get a pre-signed MinIO PUT URL for direct browser upload.</summary>
    [HttpGet("presigned-upload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPresignedUploadUrl([FromQuery] string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return BadRequest("fileName is required");

        var videoId = Guid.NewGuid().ToString();
        var key = $"videos/{videoId}/original/{fileName}";
        var url = await _minio.GeneratePresignedUploadUrlAsync(key);
        return Ok(new { url, key, videoId });
    }

    // ── Private pipeline ──────────────────────────────────────────────────────

    private async Task ProcessVideoAsync(string videoId, string tempFile, VideoJobLogger jobLog)
    {
        var hlsDir = Path.Combine(_tempPath, $"hls_{videoId}");

        await _transcodeSemaphore.WaitAsync();
        using (jobLog)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MarkIasVideoProcessingDbContext>();
            var video = await db.Videos.FindAsync(videoId);

            try
            {
                if (video == null)
                {
                    _logger.LogWarning("Video {Id} not found during background processing", videoId);
                    return;
                }

                video.Status = VideoStatus.Processing;
                _progressStore[videoId] = 0;
                await db.SaveChangesAsync();
                jobLog.Section("Pipeline");

                var sw = Stopwatch.StartNew();
                var progress = new Progress<int>(pct => _progressStore[videoId] = pct);
                await _ffmpeg.TranscodeToHlsAsync(tempFile, videoId, jobLog, progress);
                video.TranscodeMs = sw.ElapsedMilliseconds;

                sw.Restart();
                await _minio.UploadHlsFilesAsync(hlsDir, videoId, jobLog);
                video.HlsUploadMs = sw.ElapsedMilliseconds;

                if (string.IsNullOrEmpty(video.ThumbnailUrl))
                {
                    jobLog.Info("Generating thumbnail…");
                    var thumbPath = await _ffmpeg.ExtractThumbnailAsync(tempFile, videoId, jobLog);
                    if (thumbPath != null)
                    {
                        await using var thumbFs = System.IO.File.OpenRead(thumbPath);
                        video.ThumbnailUrl = await _minio.UploadThumbnailAsync(thumbFs, thumbFs.Length, videoId);
                        System.IO.File.Delete(thumbPath);
                        jobLog.Info("Thumbnail uploaded");
                    }
                }

                video.HlsUrl = _minio.GetHlsUrl(videoId);
                video.Status = VideoStatus.Ready;
                video.ProcessedAt = DateTime.UtcNow;
                _progressStore[videoId] = 100;
                await db.SaveChangesAsync();

                var total = (video.TranscodeMs ?? 0) + (video.HlsUploadMs ?? 0);
                jobLog.Section("Done");
                jobLog.Info($"READY  transcode={VideoJobLogger.FmtMs(video.TranscodeMs ?? 0)}  hls-upload={VideoJobLogger.FmtMs(video.HlsUploadMs ?? 0)}  pipeline={VideoJobLogger.FmtMs(total)}");
                _logger.LogInformation("Video {Id} ready  transcode={T}  hls={H}",
                    video.Id, VideoJobLogger.FmtMs(video.TranscodeMs ?? 0), VideoJobLogger.FmtMs(video.HlsUploadMs ?? 0));
            }
            catch (Exception ex)
            {
                if (video != null)
                {
                    video.Status = VideoStatus.Failed;
                    video.ErrorMessage = ex.Message;
                    await db.SaveChangesAsync();
                }
                _progressStore.TryRemove(videoId, out _);
                jobLog.Section("FAILED");
                jobLog.Error($"{ex.Message}  total={VideoJobLogger.FmtMs((long)jobLog.Elapsed.TotalMilliseconds)}");
                _logger.LogError("Video {Id} failed: {Msg}", videoId, ex.Message);
            }
            finally
            {
                _transcodeSemaphore.Release();
                _progressStore.TryRemove(videoId, out _);
                TryDelete(tempFile, jobLog);
                TryDeleteDir(hlsDir, jobLog);
            }
        }
    }

    private static VideoResponse ToResponse(Video v) => new(
        v.Id, v.Title, v.HlsUrl, v.Status.ToString(),
        v.CreatedAt, v.ProcessedAt, v.ThumbnailUrl,
        v.UploadToMinioMs, v.TranscodeMs, v.HlsUploadMs, v.ErrorMessage);

    private void TryDelete(string path, VideoJobLogger jobLog)
    {
        try { if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); jobLog.Info($"Deleted {Path.GetFileName(path)}"); } }
        catch (Exception ex) { _logger.LogWarning("Could not delete {Path}: {Err}", path, ex.Message); }
    }

    private void TryDeleteDir(string path, VideoJobLogger jobLog)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); jobLog.Info($"Deleted dir {Path.GetFileName(path)}"); } }
        catch (Exception ex) { _logger.LogWarning("Could not delete dir {Path}: {Err}", path, ex.Message); }
    }
}
