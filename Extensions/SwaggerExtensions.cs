using System.Reflection;
using Microsoft.OpenApi;

namespace MarkIasVideoProcessingApi.Extensions;

public static class SwaggerExtensions
{
    public static IServiceCollection AddApiSwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Video Streaming API",
                Version = "v1",
                Description =
                    "Upload course videos via TUS, transcode to adaptive HLS with FFmpeg, " +
                    "and store media in MinIO-compatible object storage."
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
