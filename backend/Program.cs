using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VapeMeter.Backend;

var dataDirectory = Environment.GetEnvironmentVariable("VAPEMETER_DATA_DIR")
    ?? throw new InvalidOperationException("Set VAPEMETER_DATA_DIR.");
var store = new SyncStore(Path.Combine(dataDirectory, "vapemeter.sqlite"));

if (args is ["--backup", var destination])
{
    store.Backup(destination);
    return;
}

if (args is ["--renew-dataset"])
{
    Console.WriteLine(store.RenewDataset());
    return;
}

var credentialsPath = Environment.GetEnvironmentVariable("VAPEMETER_DEVICE_CREDENTIALS")
    ?? throw new InvalidOperationException("Set VAPEMETER_DEVICE_CREDENTIALS.");
var credentials = new DeviceCredentials(credentialsPath);
Directory.CreateDirectory(dataDirectory);
store.Initialize();

var builder = WebApplication.CreateBuilder(args);
var port = int.Parse(Environment.GetEnvironmentVariable("VAPEMETER_PORT") ?? "18787");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 4096;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
    {
        var device = credentials.Authenticate(context.Request.Headers.Authorization);
        if (device is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_device_token" });
            return;
        }
        context.Items[DeviceCredentials.ContextKey] = device;
    }

    try
    {
        await next(context);
    }
    catch (SqliteException error) when (!context.Response.HasStarted && error.SqliteErrorCode is 5 or 6)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "2";
        await context.Response.WriteAsJsonAsync(new { error = "database_busy" });
    }
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/v1/state", (HttpContext context) =>
{
    var state = store.ReadState();
    var etag = $"\"{state.DatasetId:N}-{state.Revision}\"";
    context.Response.Headers.ETag = etag;

    return context.Request.GetTypedHeaders().IfNoneMatch?.Any(value =>
        value.Tag == "*" || string.Equals(value.Tag.Value, etag, StringComparison.Ordinal)) == true
        ? Results.StatusCode(StatusCodes.Status304NotModified)
        : Results.Ok(state);
});

app.MapPost("/v1/operations", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (!context.Request.HasJsonContentType())
    {
        return Results.Json(new { error = "expected_json" }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    SyncOperation? operation;
    try
    {
        operation = await context.Request.ReadFromJsonAsync<SyncOperation>(cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "invalid_json" });
    }

    if (operation is null || !operation.IsValid())
    {
        return Results.BadRequest(new { error = "invalid_operation" });
    }

    var result = store.Apply((string)context.Items[DeviceCredentials.ContextKey]!, operation);
    return Results.Json(result.Reply, statusCode: result.StatusCode);
});

app.Run();
