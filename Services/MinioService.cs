using System.Diagnostics;
using Minio;
using Minio.DataModel.Args;

namespace VideoStreamingApi.Services;

public class MinioService
{
    private readonly IMinioClient _client;
    private readonly string _bucketName;
    private readonly string _endpoint;
    private readonly int _port;
    private readonly string _publicBaseUrl;
    private readonly ILogger<MinioService> _logger;

    public MinioService(IConfiguration config, ILogger<MinioService> logger)
    {
        _logger = logger;
        var minio = config.GetSection("MinIO");
        _bucketName = minio["BucketName"]!;
        var endpointFull = minio["Endpoint"]!;
        _endpoint = endpointFull.Split(':')[0];
        _port = int.Parse(endpointFull.Split(':')[1]);
        var publicBase = minio["PublicBaseUrl"];
        _publicBaseUrl = string.IsNullOrWhiteSpace(publicBase)
            ? $"http://{_endpoint}:{_port}"
            : publicBase.TrimEnd('/');

        _client = new MinioClient()
            .WithEndpoint(_endpoint, _port)
            .WithCredentials(minio["AccessKey"], minio["SecretKey"])
            .WithSSL(false)
            .Build();
    }

    public async Task EnsureBucketExistsAsync()
    {
        var exists = await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_bucketName));
        if (!exists)
            await _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_bucketName));

        // Allow anonymous GET so the browser can stream HLS segments and thumbnails directly
        var policy = $$"""
            {
              "Version": "2012-10-17",
              "Statement": [{
                "Effect": "Allow",
                "Principal": {"AWS": ["*"]},
                "Action": ["s3:GetObject"],
                "Resource": ["arn:aws:s3:::{{_bucketName}}/*"]
              }]
            }
            """;
        await _client.SetPolicyAsync(new SetPolicyArgs()
            .WithBucket(_bucketName)
            .WithPolicy(policy));
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task<string> UploadVideoAsync(
        Stream fileStream, string videoId, string fileName, VideoJobLogger jobLog)
    {
        var key = $"videos/{videoId}/original/{fileName}";
        var sw = Stopwatch.StartNew();

        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(key)
            .WithStreamData(fileStream)
            .WithObjectSize(fileStream.Length)
            .WithContentType("video/mp4"));

        jobLog.Info($"MinIO original upload complete  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}  speed={VideoJobLogger.FmtSpeed(fileStream.Length, sw.ElapsedMilliseconds)}");
        return key;
    }

    public async Task UploadHlsFilesAsync(string localHlsDir, string videoId, VideoJobLogger jobLog)
    {
        var files = Directory.GetFiles(localHlsDir, "*", SearchOption.AllDirectories);
        var totalBytes = files.Sum(f => new FileInfo(f).Length);
        var sw = Stopwatch.StartNew();

        jobLog.Section("MinIO HLS upload");
        jobLog.Info($"Uploading {files.Length} files  total={VideoJobLogger.FmtBytes(totalBytes)}  concurrency=8");

        var throttle = new SemaphoreSlim(8);
        var tasks = files.Select(async file =>
        {
            await throttle.WaitAsync();
            try
            {
                var relativePath = Path.GetRelativePath(localHlsDir, file).Replace("\\", "/");
                var key = $"videos/{videoId}/hls/{relativePath}";
                var contentType = file.EndsWith(".m3u8") ? "application/x-mpegURL" : "video/MP2T";

                await using var fs = File.OpenRead(file);
                await _client.PutObjectAsync(new PutObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(key)
                    .WithStreamData(fs)
                    .WithObjectSize(fs.Length)
                    .WithContentType(contentType));
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        jobLog.Info($"HLS upload complete  files={files.Length}  elapsed={VideoJobLogger.FmtMs(sw.ElapsedMilliseconds)}  speed={VideoJobLogger.FmtSpeed(totalBytes, sw.ElapsedMilliseconds)}");
    }

    public async Task<string> UploadThumbnailAsync(
        Stream stream, long size, string videoId, string contentType = "image/jpeg")
    {
        var ext = contentType switch
        {
            "image/png"  => "png",
            "image/webp" => "webp",
            _            => "jpg"
        };
        var key = $"videos/{videoId}/thumbnail.{ext}";

        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(key)
            .WithStreamData(stream)
            .WithObjectSize(size)
            .WithContentType(contentType));

        return $"{_publicBaseUrl}/{_bucketName}/{key}";
    }

    // ── URL builders ──────────────────────────────────────────────────────────

    public string GetHlsUrl(string videoId) =>
        $"{_publicBaseUrl}/{_bucketName}/videos/{videoId}/hls/master.m3u8";

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>Downloads an object from MinIO to a local file path.</summary>
    public async Task DownloadObjectAsync(string objectKey, string destinationPath)
    {
        await _client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithFile(destinationPath));
    }

    // ── Presigned URLs ────────────────────────────────────────────────────────

    /// <summary>Generates a pre-signed PUT URL for direct browser-to-MinIO upload.</summary>
    public async Task<string> GeneratePresignedUploadUrlAsync(string objectKey, int expirySeconds = 3600)
    {
        return await _client.PresignedPutObjectAsync(new PresignedPutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithExpiry(expirySeconds));
    }

    /// <summary>Generates a pre-signed GET URL for private object download.</summary>
    public async Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, int expirySeconds = 3600)
    {
        return await _client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithExpiry(expirySeconds));
    }

    /// <summary>Returns true if the object exists in the bucket.</summary>
    public async Task<bool> ObjectExistsAsync(string objectKey)
    {
        try
        {
            await _client.StatObjectAsync(new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey));
            return true;
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return false;
        }
    }
}
