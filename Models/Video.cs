namespace MarkIasVideoProcessingApi.Models;

public class Video
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string OriginalKey { get; set; } = string.Empty;
    public string HlsUrl { get; set; } = string.Empty;
    public VideoStatus Status { get; set; } = VideoStatus.Pending;
    public long FileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public long? UploadToMinioMs { get; set; }
    public long? TranscodeMs { get; set; }
    public long? HlsUploadMs { get; set; }
}

public enum VideoStatus
{
    Pending,
    Processing,
    Ready,
    Failed
}
