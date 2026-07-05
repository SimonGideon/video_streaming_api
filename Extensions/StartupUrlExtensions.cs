namespace VideoStreamingApi.Extensions;

public static class StartupUrlExtensions
{
    /// <summary>Logs API and Swagger URLs when the application has started listening.</summary>
    public static void LogStartupUrls(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var urls = app.Urls.Count > 0
                ? app.Urls.ToArray()
                : (app.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:8080")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var url in urls)
            {
                var display = ToDisplayUrl(url);
                var root = display.TrimEnd('/');

                Serilog.Log.Information("API listening at {Url}", root);
                Serilog.Log.Information("Swagger UI: {Url}", $"{root}/swagger");
                Serilog.Log.Information("OpenAPI spec: {Url}", $"{root}/swagger/v1/swagger.json");

                var publicBase = app.Configuration["MinIO:PublicBaseUrl"];
                if (!string.IsNullOrWhiteSpace(publicBase))
                {
                    var publicRoot = publicBase.TrimEnd('/');
                    Serilog.Log.Information("Public Swagger UI: {Url}", $"{publicRoot}/swagger");
                }
            }
        });
    }

    /// <summary>Maps wildcard bind addresses to localhost for readable console output.</summary>
    private static string ToDisplayUrl(string url) =>
        url.Replace("+", "localhost", StringComparison.Ordinal)
           .Replace("[::]", "localhost", StringComparison.Ordinal);
}
