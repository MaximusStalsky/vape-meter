using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VapeMeter.Core;

namespace VapeMeter.Android;

internal sealed class TimewebConnection
{
    private static readonly Uri Endpoint = new("https://vapemeter.stalsky.org/");
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 8 * 1024 * 1024,
    };

    public static bool IsAndroidKey(string text)
    {
        const string prefix = "android.";
        return text.StartsWith(prefix, StringComparison.Ordinal)
            && text.Length is > 8 and <= 121
            && text[prefix.Length..].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    public async Task VerifyAsync(string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, "v1/state"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.UserAgent.ParseAdd("VapeMeter/1.0");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException();
        }
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var body = json.RootElement;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("datasetId", out var dataset)
            || dataset.ValueKind != JsonValueKind.String
            || !dataset.TryGetGuid(out var datasetId) || datasetId == Guid.Empty
            || !body.TryGetProperty("revision", out var revision)
            || revision.ValueKind != JsonValueKind.Number
            || !revision.TryGetInt64(out var version) || version < 0
            || !body.TryGetProperty("records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Timeweb вернул неподдерживаемое состояние синхронизации.");
        }
    }

    public MeterSync MakeSync(MeterStore store, string key) => new(store, _client, Endpoint, key);
}
