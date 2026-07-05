using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using VideoStreamingApi.Configuration;

namespace VideoStreamingApi.Extensions;

public static class SwaggerExtensions
{
    public static IServiceCollection AddApiSwagger(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var videoProcessing = configuration
            .GetSection(VideoProcessingOptions.SectionName)
            .Get<VideoProcessingOptions>() ?? new VideoProcessingOptions();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Video Streaming API",
                Version = "v1",
                Description = $"""
                    Upload course videos via **tus.io** (`POST /api/files`), transcode to adaptive HLS with FFmpeg,
                    and store media in MinIO-compatible object storage.

                    ## Upload limits

                    | Setting | Value |
                    |---------|-------|
                    | Maximum upload size | **{videoProcessing.MaxUploadSizeGb} GB** ({videoProcessing.MaxUploadSizeBytes:N0} bytes) |
                    | Config key | `VideoProcessing:MaxUploadSizeGb` |

                    Use `GET /api/video/upload-limits` for the current limits.

                    **TUS uploads:** `POST`, `PATCH`, `HEAD`, and `OPTIONS` on `/api/files` (not shown as controller routes; handled by tus.io middleware).
                    """
            });

            var xml = Path.Combine(
                AppContext.BaseDirectory,
                $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml))
                options.IncludeXmlComments(xml);
        });

        return services;
    }

    public static WebApplication UseApiSwagger(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Video Streaming API v1");
            options.RoutePrefix = "swagger";
            options.DocumentTitle = "Video Streaming API";
        });

        return app;
    }
}
