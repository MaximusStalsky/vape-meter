using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VapeMeter.Core;

public sealed class MeterSync(MeterStore store, HttpClient client, Uri endpoint, string deviceToken)
{
    public async Task<SyncStatus> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps
            && !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp))
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Для синхронизации требуется HTTPS.", nameof(endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        await store.SyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadStateAsync(force: false, cancellationToken).ConfigureAwait(false);
            var posted = false;
            while (store.GetOrStageOperation() is { } operation)
            {
                posted = true;
                var reply = await PostAsync(operation, cancellationToken).ConfigureAwait(false);
                if (reply.Outcome == "dataset_changed")
                {
                    await ReadStateAsync(force: true, cancellationToken).ConfigureAwait(false);
                    if (store.GetSyncHeader().DatasetId == operation.DatasetId)
                    {
                        throw new InvalidDataException("Сервер не подтвердил смену набора данных.");
                    }
                }
                else
                {
                    store.ApplyReply(operation, reply);
                }
            }

            if (posted)
            {
                await ReadStateAsync(force: false, cancellationToken).ConfigureAwait(false);
            }

            store.MarkSynced();
            return store.GetSyncStatus();
        }
        finally
        {
            store.SyncLock.Release();
        }
    }

    private async Task ReadStateAsync(bool force, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/v1/state");
        var header = store.GetSyncHeader();
        if (!force && header.DatasetId.HasValue && EntityTagHeaderValue.TryParse(header.ETag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && request.Headers.IfNoneMatch.Count > 0)
        {
            return;
        }

        EnsureSuccess(response);
        var state = await response.Content.ReadFromJsonAsync<ServerState>(MeterJson.Options, cancellationToken)
            .ConfigureAwait(false);
        if (state is not { Records: not null } || state.DatasetId == Guid.Empty || state.Revision < 0
            || state.Records.Any(record => !IsValidRecord(record, state.Revision))
            || state.Records.Select(record => record.Id).Distinct().Count() != state.Records.Count
            || state.Records.Where(record => !record.Deleted).GroupBy(record => record.Date).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Сервер вернул некорректный снимок истории.");
        }

        store.ApplySnapshot(state, response.Headers.ETag?.ToString());
    }

    private async Task<OperationReply> PostAsync(WireOperation operation, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "/v1/operations");
        request.Content = JsonContent.Create(operation, options: MeterJson.Options);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            EnsureSuccess(response);
        }

        var reply = await response.Content.ReadFromJsonAsync<OperationReply>(MeterJson.Options, cancellationToken)
            .ConfigureAwait(false);
        if (reply is null || reply.DatasetId == Guid.Empty || reply.Revision < 0)
        {
            throw new InvalidDataException("Сервер не подтвердил результат операции.");
        }

        var valid = response.StatusCode == HttpStatusCode.OK
            ? reply.Outcome == "applied" && reply.Record is not null
            : reply.Outcome is "version_conflict" or "duplicate_date" or "operation_id_reused" or "dataset_changed";
        var matchesDataset = reply.Outcome == "dataset_changed"
            ? reply.DatasetId != operation.DatasetId
            : reply.DatasetId == operation.DatasetId;
        if (!valid || !matchesDataset
            || reply.Record is { } record && (record.Id != operation.RecordId || !IsValidRecord(record, reply.Revision))
            || reply.Existing is { } existing && !IsValidRecord(existing, reply.Revision))
        {
            throw new InvalidDataException("Сервер вернул некорректный результат операции.");
        }

        return reply;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        request.Headers.UserAgent.ParseAdd("VapeMeter/1.0");
        return request;
    }

    private static bool IsValidRecord(ServerRecord? record, long revision) =>
        record is not null && record.Id != Guid.Empty && record.Version > 0 && record.Version <= revision;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode == HttpStatusCode.Unauthorized
                ? "Ключ доступа к синхронизации отклонён сервером."
                : $"Сервер синхронизации вернул HTTP {(int)response.StatusCode}.";
            throw new HttpRequestException(message, null, response.StatusCode);
        }
    }
}
