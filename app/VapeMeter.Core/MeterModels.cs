using System.Text.Json;

namespace VapeMeter.Core;

public sealed record Replacement(Guid Id, DateOnly Date);

public sealed record SyncConflict(Guid OperationId, string Code, DateOnly? LocalDate, DateOnly? ServerDate)
{
    public Guid RecordId { get; init; }

    public DateOnly? PreviousDate { get; init; }

    public SyncConflictServerRecord? TimewebRecord { get; init; }

    public SyncConflictServerRecord? OccupyingRecord { get; init; }

    public bool KeepLocalRemovesSource { get; init; }
}

public sealed record SyncConflictServerRecord(Guid Id, DateOnly Date, long Version, bool Deleted);

public sealed record SyncStatus(int PendingCount, DateTimeOffset? LastSynced, IReadOnlyList<SyncConflict> Conflicts);

internal sealed record ServerRecord(Guid Id, DateOnly Date, long Version, bool Deleted);

internal sealed record ServerState(Guid DatasetId, long Revision, IReadOnlyList<ServerRecord> Records);

internal sealed record WireOperation(
    Guid DatasetId, Guid OperationId, Guid RecordId, long ExpectedVersion, string Action, DateOnly? Date);

internal sealed record OperationReply(
    string Outcome, Guid DatasetId, long Revision, ServerRecord? Record, ServerRecord? Existing);

internal sealed record LocalChange(
    Guid RecordId, Guid OperationId, long ExpectedVersion, DateOnly? Date,
    string? ConflictCode = null, DateOnly? PreviousDate = null, bool MergeOnDate = false);

internal sealed record SyncHeader(Guid? DatasetId, long Revision, string? ETag, DateTimeOffset? LastSynced);

internal sealed record HistoryFile(int Version, IReadOnlyList<Replacement> Records);

internal static class MeterJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
