namespace VapeMeter.Backend;

internal sealed record SyncOperation(
    Guid DatasetId,
    Guid OperationId,
    Guid RecordId,
    long ExpectedVersion,
    string? Action,
    DateOnly? Date)
{
    public bool IsValid() =>
        DatasetId != Guid.Empty && OperationId != Guid.Empty && RecordId != Guid.Empty
        && ExpectedVersion >= 0
        && (Action == "upsert" && Date.HasValue || Action == "delete" && !Date.HasValue && ExpectedVersion > 0);
}

internal sealed record Replacement(Guid Id, DateOnly Date, long Version, bool Deleted);

internal sealed record SyncState(Guid DatasetId, long Revision, IReadOnlyList<Replacement> Records);

internal sealed record OperationReply(
    string Outcome,
    Guid DatasetId,
    long Revision,
    Replacement? Record = null,
    Replacement? Existing = null);

internal sealed record OperationResult(int StatusCode, OperationReply Reply);
