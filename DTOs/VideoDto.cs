namespace VideoStreamingApi.DTOs;

public record UploadVideoRequest(string Title);

/// <summary>Configured maximum video upload size.</summary>
/// <param name="MaxUploadSizeGb">Maximum upload size in gigabytes (GiB).</param>
/// <param name="MaxUploadSizeBytes">Same limit in bytes for TUS and HTTP clients.</param>
public record UploadLimitsResponse(int MaxUploadSizeGb, long MaxUploadSizeBytes);

public record VideoResponse(
    string Id,
    string Title,
    string? HlsUrl,
    string Status,
    DateTime CreatedAt,
    DateTime? ProcessedAt,
    string? ThumbnailUrl = null,
    long? UploadToMinioMs = null,
    long? TranscodeMs = null,
    long? HlsUploadMs = null,
    string? ErrorMessage = null
);
