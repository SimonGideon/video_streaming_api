using System.Collections.Concurrent;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using VideoStreamingApi.Data;
using VideoStreamingApi.Extensions;
using VideoStreamingApi.Jobs;
using VideoStreamingApi.Models;
using VideoStreamingApi.Services;

LoadDotEnv();

static void LoadDotEnv()
{
    var path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    if (!File.Exists(path)) return;
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
        var eq = trimmed.IndexOf('=');
        if (eq < 0) continue;
        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();
        Environment.SetEnvironmentVariable(key, value);
    }
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft",                     LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore",          LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting",  LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Routing",  LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http",               LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        "logs/video-streaming-api.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Serilog
    builder.Host.UseSerilog();

    // Services
    builder.Services.AddDbContext<VideoStreamingDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddApiSwagger();
    builder.Services.AddSingleton<MinioService>();
    builder.Services.AddSingleton<FfmpegService>();

    // Shared progress tracker — TranscodeJob writes, the SSE endpoint reads
    builder.Services.AddSingleton<ConcurrentDictionary<string, int>>();
    builder.Services.AddScoped<TranscodeJob>();

    // Hangfire — background transcode job queue
    builder.Services.AddHangfire(config => config.UseInMemoryStorage());
    builder.Services.AddHangfireServer(opts => { opts.WorkerCount = 3; });

    // CORS — allow Next.js frontend
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("VideoStreamingCorsPolicy", policy =>
        {
            var origins = builder.Configuration["Cors:AllowedOrigins"]!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                // tus-js-client needs to read these cross-origin response headers —
                // browsers hide them by default unless explicitly exposed via CORS
                .WithExposedHeaders(
                    "Location",
                    "Tus-Resumable",
                    "Tus-Version",
                    "Tus-Extension",
                    "Tus-Max-Size",
                    "Upload-Offset",
                    "Upload-Length",
                    "Upload-Metadata",
                    "Upload-Expires",
                    "Upload-Concat");
        });
    });

    // Increase upload size limit
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = 4_294_967_295;
    });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<VideoStreamingDbContext>();
        await db.Database.EnsureCreatedAsync();

        var minio = scope.ServiceProvider.GetRequiredService<MinioService>();
        await minio.EnsureBucketExistsAsync();
    }

    app.UseApiSwagger();

    if (app.Environment.IsDevelopment())
        app.UseHangfireDashboard("/hangfire");

    app.UseCors("VideoStreamingCorsPolicy");
    app.UseAuthorization();
    app.MapControllers();

    // TUS resumable upload endpoint — browser uploads chunks here via tus-js-client
    var tempPath = builder.Configuration["VideoProcessing:TempPath"]!;
    Directory.CreateDirectory(tempPath);
    var tusStore = new TusDiskStore(tempPath);

    app.MapTus("/api/files", httpContext => Task.FromResult(new DefaultTusConfiguration
    {
        Store = tusStore,
        MaxAllowedUploadSizeInBytesLong = 4_294_967_295, // 4 GB
        Events = new Events
        {
            OnFileCompleteAsync = async eventContext =>
            {
                var file = await eventContext.GetFileAsync();
                var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);

                string Meta(string key) =>
                    metadata.TryGetValue(key, out var v) ? v.GetString(System.Text.Encoding.UTF8) : "";

                var title = Meta("title");
                var fileName = Meta("filename");
                if (string.IsNullOrEmpty(fileName))
                    fileName = file.Id;

                using var scope = eventContext.HttpContext.RequestServices.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VideoStreamingDbContext>();
                var minio = scope.ServiceProvider.GetRequiredService<MinioService>();

                var video = new Video
                {
                    Id = file.Id, // re-use the TUS file id so the client can derive the video id from the upload URL
                    Title = title,
                    FileName = fileName,
                    Status = VideoStatus.Pending
                };

                var jobLog = new VideoJobLogger("logs/jobs", video.Id);
                using (jobLog)
                {
                    jobLog.Section($"TUS upload complete — {video.Id}");
                    jobLog.Info($"Title: {title}  File: {fileName}");

                    await using (var content = await file.GetContentAsync(eventContext.CancellationToken))
                    {
                        video.FileSizeBytes = content.Length;
                        video.OriginalKey = await minio.UploadVideoAsync(content, video.Id, fileName, jobLog);
                    }

                    db.Videos.Add(video);
                    await db.SaveChangesAsync(eventContext.CancellationToken);

                    BackgroundJob.Enqueue<TranscodeJob>(j => j.ExecuteAsync(video.Id, CancellationToken.None));
                    jobLog.Info($"Enqueued TranscodeJob for video {video.Id}");
                }

                // Delete the completed TUS file from local disk — it now lives in MinIO
                await tusStore.DeleteFileAsync(file.Id, eventContext.CancellationToken);
            }
        }
    }));

    Log.Information("Mark IAS Video Processing API starting on {Env}", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API failed to start");
}
finally
{
    Log.CloseAndFlush();
}