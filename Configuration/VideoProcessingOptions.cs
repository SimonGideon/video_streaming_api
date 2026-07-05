namespace VideoStreamingApi.Configuration;

/// <summary>Video ingest and transcode settings (bound from the VideoProcessing config section).</summary>
public class VideoProcessingOptions
{
    public const string SectionName = "VideoProcessing";

    /// <summary>Local scratch directory for uploads and FFmpeg output.</summary>
    public string TempPath { get; set; } = "/tmp/video-streaming";

    /// <summary>HLS segment length in seconds.</summary>
    public int HlsSegmentDuration { get; set; } = 6;

    /// <summary>Maximum allowed video upload size in bytes (applies to TUS and multipart uploads).</summary>
    public long MaxUploadSizeBytes { get; set; } = 4_294_967_296; // 4 GiB
}
