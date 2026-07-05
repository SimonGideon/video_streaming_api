namespace VideoStreamingApi.Configuration;

/// <summary>Video ingest and transcode settings (bound from the VideoProcessing config section).</summary>
public class VideoProcessingOptions
{
    public const string SectionName = "VideoProcessing";
    private const long Gibibyte = 1024L * 1024 * 1024;

    /// <summary>Local scratch directory for uploads and FFmpeg output.</summary>
    public string TempPath { get; set; } = "/tmp/video-streaming";

    /// <summary>HLS segment length in seconds.</summary>
    public int HlsSegmentDuration { get; set; } = 6;

    /// <summary>Maximum allowed video upload size in gigabytes (binary GB / GiB).</summary>
    public int MaxUploadSizeGb { get; set; } = 4;

    /// <summary>Upload limit in bytes, derived from <see cref="MaxUploadSizeGb"/>.</summary>
    public long MaxUploadSizeBytes => MaxUploadSizeGb * Gibibyte;
}
