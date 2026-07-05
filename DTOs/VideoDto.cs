namespace VideoStreamingApi.DTOs;

public record UploadVideoRequest(string Title);

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
