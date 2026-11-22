using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VapeMeter.Core;

public sealed partial class MeterStore
{
    public SyncStatus GetSyncStatus() => Read((db, tx) =>
    {
        var changes = ReadChanges(db, tx);
        var records = ReadServerRecords(db, tx);
        var conflicts = changes.Where(change => change.ConflictCode is not null)
            .Select(change => DescribeConflict(change, records, changes))
            .ToArray();
        return new SyncStatus(changes.Count - conflicts.Length, ReadHeader(db, tx).LastSynced, conflicts);
    });

    public void ResolveConflict(Guid operationId, bool keepLocal) => ResolveConflict(operationId, null, keepLocal);

    public void ResolveConflict(SyncConflict expected, bool keepLocal)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ResolveConflict(expected.OperationId, expected, keepLocal);
    }

    private void ResolveConflict(Guid operationId, SyncConflict? expected, bool keepLocal) => Write((db, tx) =>
    {
        var changes = ReadChanges(db, tx);
        var conflicted = changes.FirstOrDefault(change => change.OperationId == operationId && change.ConflictCode is not null)
            ?? throw new InvalidOperationException("Этот конфликт уже разрешён или изменился.");
        var records = ReadServerRecords(db, tx);
        var context = DescribeConflict(conflicted, records, changes);
        if (expected is not null && context != expected)
        {
            throw new InvalidOperationException("Данные этого конфликта обновились. Проверьте оба варианта ещё раз.");
        }

        var source = records.FirstOrDefault(record => record.Id == conflicted.RecordId);
        var occupant = FindOccupant(records, conflicted);
        var targetChange = occupant is null ? null : changes.FirstOrDefault(change => change.RecordId == occupant.Id);
        if (!keepLocal || HasSameHistory(conflicted, source, occupant, targetChange is not null))
        {
            RemoveChange(db, tx, conflicted.RecordId);
            return;
        }

        if (conflicted.Date is { } date)
        {
            if (targetChange?.ConflictCode is not null)
            {
                throw new InvalidOperationException("Сначала разрешите конфликт другой записи на эту дату.");
            }

            EnsureDateAvailable(ReadEffective(db, tx), date, conflicted.RecordId);
        }

        SaveChange(db, tx, new(conflicted.RecordId, Guid.NewGuid(), source?.Version ?? 0, conflicted.Date,
            PreviousDate: ActiveDate(source), MergeOnDate: context.KeepLocalRemovesSource));
    });

    private static SyncConflict DescribeConflict(LocalChange change, IReadOnlyList<ServerRecord> records,
        IReadOnlyList<LocalChange> changes)
    {
        var source = records.FirstOrDefault(record => record.Id == change.RecordId);
        var occupant = FindOccupant(records, change);
        var sourceDate = ActiveDate(source);
        var merge = sourceDate.HasValue && sourceDate != change.Date && occupant is not null
            && !changes.Any(pending => pending.RecordId == occupant.Id);
        return new(change.OperationId, change.ConflictCode!, change.Date, occupant?.Date ?? sourceDate)
        {
            RecordId = change.RecordId,
            PreviousDate = change.PreviousDate,
            TimewebRecord = Describe(source),
            OccupyingRecord = Describe(occupant),
            KeepLocalRemovesSource = merge,
        };
    }

    private static SyncConflictServerRecord? Describe(ServerRecord? record) =>
        record is null ? null : new(record.Id, record.Date, record.Version, record.Deleted);

    private static DateOnly? ActiveDate(ServerRecord? record) => record is { Deleted: false } ? record.Date : null;

    private static ServerRecord? FindOccupant(IEnumerable<ServerRecord> records, LocalChange change) =>
        change.Date is { } date
            ? records.Where(record => record.Id != change.RecordId && !record.Deleted && record.Date == date)
                .MaxBy(record => record.Version)
            : null;

    private static bool HasSameHistory(LocalChange change, ServerRecord? source, ServerRecord? occupant, bool occupantPending) =>
        ActiveDate(source) == change.Date
        || (ActiveDate(source) is null && occupant is not null && !occupantPending);

    internal SyncHeader GetSyncHeader() => Read(ReadHeader);

    internal void MarkSynced() => Write((db, tx) =>
    {
        using var update = Command(db, tx, "UPDATE sync_state SET last_synced = $last WHERE singleton = 1",
            ("$last", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        update.ExecuteNonQuery();
    });

    internal void ApplySnapshot(ServerState state, string? etag) => Write((db, tx) =>
    {
        var header = ReadHeader(db, tx);
        var records = ReadServerRecords(db, tx);
        if (header.DatasetId == state.DatasetId && !IsCompatibleSnapshot(header, records, state))
        {
            throw new InvalidDataException(
                "Timeweb вернул неполную или более старую историю. Локальные записи и очередь синхронизации сохранены.");
        }

        if (header.DatasetId is { } previous && previous != state.DatasetId)
        {
            PreserveDatasetChanges(db, tx, records);
        }

        using (var clear = Command(db, tx, "DELETE FROM server_records"))
        {
            clear.ExecuteNonQuery();
        }

        foreach (var record in state.Records)
        {
            SaveServerRecord(db, tx, record);
        }

        using var update = Command(db, tx, """
            UPDATE sync_state SET dataset_id = $dataset, revision = $revision, etag = $etag
            WHERE singleton = 1
            """, ("$dataset", state.DatasetId.ToString("D")), ("$revision", state.Revision), ("$etag", etag));
        update.ExecuteNonQuery();
    });

    private static bool IsCompatibleSnapshot(SyncHeader header, IReadOnlyList<ServerRecord> known, ServerState state)
    {
        if (state.Revision < header.Revision)
        {
            return false;
        }

        // A dataset retains tombstones for its entire lifetime. Loss of a known ID without a new
        // dataset ID is an incomplete snapshot or an unsupported rollback, even at the same revision.
        var incoming = state.Records.ToDictionary(record => record.Id);
        return known.All(record => incoming.TryGetValue(record.Id, out var next)
            && (next.Version > record.Version || next == record));
    }

    internal WireOperation? GetOrStageOperation() => InTransaction<WireOperation?>(deferred: false, (db, tx) =>
    {
        var current = ReadInflight(db, tx);
        if (current is not null)
        {
            return current;
        }

        if (ReadHeader(db, tx).DatasetId is not { } dataset)
        {
            return null;
        }

        var records = ReadServerRecords(db, tx);
        var changes = ReadChanges(db, tx);
        RemoveEquivalentChanges(db, tx, records, changes);
        var sendable = changes.Where(change => change.ConflictCode is null).ToArray();
        if (sendable.Length == 0)
        {
            return null;
        }

        // If two pending changes depend on freeing a date, send the change that frees it first.
        var activeByDate = records.Where(record => !record.Deleted);
        var chosen = sendable.FirstOrDefault(change => !activeByDate.Any(record =>
            record.Id != change.RecordId && record.Date == change.Date
            && sendable.Any(other => other.RecordId == record.Id && other.Date != record.Date))) ?? sendable[0];

        var occupant = FindOccupant(records, chosen);
        var mergeIntoExisting = chosen.MergeOnDate && occupant is not null
            && !changes.Any(change => change.RecordId == occupant.Id)
            && chosen.ExpectedVersion > 0;
        var target = mergeIntoExisting ? null : chosen.Date;
        var operation = new WireOperation(dataset, chosen.OperationId, chosen.RecordId, chosen.ExpectedVersion,
            target.HasValue ? "upsert" : "delete", target);

        using var stage = Command(db, tx, "INSERT INTO inflight VALUES (1, $payload)",
            ("$payload", JsonSerializer.Serialize(operation, MeterJson.Options)));
        stage.ExecuteNonQuery();
        return operation;
    });

    private static void RemoveEquivalentChanges(SqliteConnection db, SqliteTransaction tx,
        IReadOnlyList<ServerRecord> records, List<LocalChange> changes)
    {
        var byId = records.ToDictionary(record => record.Id);
        var pendingIds = changes.Select(change => change.RecordId).ToHashSet();
        changes.RemoveAll(change =>
        {
            var source = byId.GetValueOrDefault(change.RecordId);
            var occupant = FindOccupant(records, change);
            if (!HasSameHistory(change, source, occupant, occupant is not null && pendingIds.Contains(occupant.Id)))
            {
                return false;
            }

            RemoveChange(db, tx, change.RecordId);
            pendingIds.Remove(change.RecordId);
            return true;
        });
    }

    internal bool ApplyReply(WireOperation sent, OperationReply reply) => InTransaction(deferred: false, (db, tx) =>
    {
        if (ReadInflight(db, tx)?.OperationId != sent.OperationId)
        {
            return false;
        }

        var current = ReadChange(db, tx, sent.RecordId)
            ?? throw new InvalidDataException("Не найдена отправленная операция.");
        if (reply.Record is not null)
        {
            SaveServerRecord(db, tx, reply.Record);
        }

        if (reply.Existing is not null)
        {
            SaveServerRecord(db, tx, reply.Existing);
        }

        var refresh = false;
        if (reply.Outcome == "applied")
        {
            if (current.OperationId != sent.OperationId)
            {
                // A successor edit compares against this immutable receipt, not a newer pull.
                SaveChange(db, tx, current with
                {
                    ExpectedVersion = reply.Record!.Version,
                    PreviousDate = ActiveDate(reply.Record),
                });
            }
            else if (current.MergeOnDate && current.Date.HasValue && sent.Action == "delete")
            {
                // Keep the requested date durable until a fresh pull confirms the occupying row.
                // If that row disappeared concurrently, the next request restores the source at the requested date.
                SaveChange(db, tx, current with { OperationId = Guid.NewGuid(), ExpectedVersion = reply.Record!.Version });
                refresh = true;
            }
            else
            {
                RemoveChange(db, tx, sent.RecordId);
            }
        }
        else if (!IsOutdatedCollision(db, tx, current, sent, reply))
        {
            SaveChange(db, tx, current with
            {
                ConflictCode = reply.Outcome,
                MergeOnDate = false,
            });
        }

        ClearInflight(db, tx);
        return refresh;
    });

    private static bool IsOutdatedCollision(SqliteConnection db, SqliteTransaction tx,
        LocalChange current, WireOperation sent, OperationReply reply) =>
        reply.Outcome == "duplicate_date" && current.OperationId != sent.OperationId && current.Date != sent.Date
        && (ReadServerRecord(db, tx, current.RecordId)?.Version ?? 0) == sent.ExpectedVersion;

    private static void PreserveDatasetChanges(SqliteConnection db, SqliteTransaction tx,
        IReadOnlyList<ServerRecord> previous)
    {
        var changes = ReadChanges(db, tx);
        var changedIds = changes.Select(change => change.RecordId).ToHashSet();
        foreach (var change in changes)
        {
            SaveChange(db, tx, change with { ConflictCode = "dataset_changed", MergeOnDate = false });
        }

        foreach (var record in previous.Where(record => !changedIds.Contains(record.Id)))
        {
            var date = ActiveDate(record);
            SaveChange(db, tx, new(record.Id, Guid.NewGuid(), record.Version, date,
                ConflictCode: "dataset_changed", PreviousDate: date));
        }

        ClearInflight(db, tx);
    }

    private static SyncHeader ReadHeader(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = Command(db, tx, "SELECT dataset_id, revision, etag, last_synced FROM sync_state WHERE singleton = 1");
        using var row = query.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidDataException("Не найдены параметры синхронизации.");
        }

        return new(row.IsDBNull(0) ? null : Guid.Parse(row.GetString(0)), row.GetInt64(1),
            row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : DateTimeOffset.ParseExact(row.GetString(3), "O", CultureInfo.InvariantCulture));
    }

    private static WireOperation? ReadInflight(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = Command(db, tx, "SELECT payload FROM inflight WHERE singleton = 1");
        return query.ExecuteScalar() is string payload
            ? JsonSerializer.Deserialize<WireOperation>(payload, MeterJson.Options)
                ?? throw new InvalidDataException("Очередь синхронизации повреждена.")
            : null;
    }

    private static void ClearInflight(SqliteConnection db, SqliteTransaction tx)
    {
        using var clear = Command(db, tx, "DELETE FROM inflight WHERE singleton = 1");
        clear.ExecuteNonQuery();
    }

    private const string ServerColumns = "SELECT record_id, date, version, deleted FROM server_records";

    private static List<ServerRecord> ReadServerRecords(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = Command(db, tx, ServerColumns);
        using var rows = query.ExecuteReader();
        var records = new List<ServerRecord>();
        while (rows.Read())
        {
            records.Add(ReadServerRecord(rows));
        }
        return records;
    }

    private static ServerRecord? ReadServerRecord(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var query = Command(db, tx, ServerColumns + " WHERE record_id = $id", ("$id", id.ToString("D")));
        using var row = query.ExecuteReader();
        return row.Read() ? ReadServerRecord(row) : null;
    }

    private static ServerRecord ReadServerRecord(SqliteDataReader row) =>
        new(Guid.Parse(row.GetString(0)), ParseDate(row.GetString(1)), row.GetInt64(2), row.GetBoolean(3));

    private static void SaveServerRecord(SqliteConnection db, SqliteTransaction tx, ServerRecord record)
    {
        using var update = Command(db, tx, """
            INSERT INTO server_records VALUES ($id, $date, $version, $deleted)
            ON CONFLICT (record_id) DO UPDATE SET date = excluded.date, version = excluded.version, deleted = excluded.deleted
            WHERE excluded.version > server_records.version
            """, ("$id", record.Id.ToString("D")), ("$date", ToDateText(record.Date)), ("$version", record.Version),
            ("$deleted", record.Deleted));
        update.ExecuteNonQuery();
    }

    private const string ChangeColumns =
        "SELECT record_id, operation_id, expected_version, date, conflict_code, previous_date, merge_on_date FROM local_changes";

    private static List<LocalChange> ReadChanges(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = Command(db, tx, ChangeColumns + " ORDER BY rowid");
        using var rows = query.ExecuteReader();
        var changes = new List<LocalChange>();
        while (rows.Read())
        {
            changes.Add(ReadChange(rows));
        }
        return changes;
    }

    private static LocalChange? ReadChange(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var query = Command(db, tx, ChangeColumns + " WHERE record_id = $id", ("$id", id.ToString("D")));
        using var row = query.ExecuteReader();
        return row.Read() ? ReadChange(row) : null;
    }

    private static LocalChange ReadChange(SqliteDataReader row) =>
        new(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), row.GetInt64(2),
            ReadNullableDate(row, 3), row.IsDBNull(4) ? null : row.GetString(4), ReadNullableDate(row, 5), row.GetBoolean(6));

    private static void SaveChange(SqliteConnection db, SqliteTransaction tx, LocalChange change)
    {
        using var write = Command(db, tx, """
            INSERT INTO local_changes (record_id, operation_id, expected_version, date, conflict_code,
                previous_date, merge_on_date)
            VALUES ($id, $operation, $expected, $date, $conflict, $previous_date, $merge_on_date)
            ON CONFLICT (record_id) DO UPDATE SET operation_id = excluded.operation_id,
                expected_version = excluded.expected_version, date = excluded.date,
                conflict_code = excluded.conflict_code,
                previous_date = excluded.previous_date, merge_on_date = excluded.merge_on_date
            """, ("$id", change.RecordId.ToString("D")), ("$operation", change.OperationId.ToString("D")),
            ("$expected", change.ExpectedVersion), ("$date", ToDateText(change.Date)),
            ("$conflict", change.ConflictCode),
            ("$previous_date", ToDateText(change.PreviousDate)), ("$merge_on_date", change.MergeOnDate));
        write.ExecuteNonQuery();
    }

    private static void RemoveChange(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var delete = Command(db, tx, "DELETE FROM local_changes WHERE record_id = $id", ("$id", id.ToString("D")));
        delete.ExecuteNonQuery();
    }
}
