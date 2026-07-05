using Microsoft.AspNetCore.Mvc;
using VideoStreamingApi.Services;

namespace VideoStreamingApi.Controllers;

/// <summary>Proxies public media from object storage through the API (HLS, thumbnails).</summary>
[ApiController]
[Route("api/media")]
public class MediaController : ControllerBase
{
    private readonly MinioService _minio;

    public MediaController(MinioService minio) => _minio = minio;

    /// <summary>Stream an object from storage, e.g. videos/{id}/hls/master.m3u8.</summary>
    [HttpGet("{**objectPath}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetObject(string objectPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
            return NotFound();

        if (!await _minio.ObjectExistsAsync(objectPath))
            return NotFound();

        Response.ContentType = MinioService.GetContentType(objectPath);
        Response.Headers.CacheControl = "public, max-age=3600";
        await _minio.CopyObjectToAsync(objectPath, Response.Body, cancellationToken);
        return new EmptyResult();
    }
}
