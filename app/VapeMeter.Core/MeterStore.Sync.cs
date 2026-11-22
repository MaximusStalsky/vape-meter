using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VapeMeter.Core;

public sealed partial class MeterStore
{
    public SyncStatus GetSyncStatus() => Read((db, tx) =>
    {
        var changes = ReadChanges(db, tx);
        var conflicts = changes.Where(change => change.ConflictCode is not null)
            .Select(change => new SyncConflict(change.OperationId, change.ConflictCode!, change.Date, change.ServerDate))
            .ToArray();
        return new SyncStatus(changes.Count - conflicts.Length, ReadHeader(db, tx).LastSynced, conflicts);
    });

    public void ResolveConflict(Guid operationId, bool keepLocal) => Write((db, tx) =>
    {
        var changes = ReadChanges(db, tx);
        var conflicted = changes.FirstOrDefault(change => change.OperationId == operationId && change.ConflictCode is not null)
            ?? throw new InvalidOperationException("Этот конфликт уже разрешён или изменился.");

        if (!keepLocal)
        {
            RemoveChange(db, tx, conflicted.RecordId);
            return;
        }

        var current = ReadServerRecord(db, tx, conflicted.RecordId);
        if (!conflicted.Date.HasValue && (current is null || current.Deleted)
            || current is { Deleted: false } && current.Date == conflicted.Date)
        {
            RemoveChange(db, tx, conflicted.RecordId);
            return;
        }

        if (conflicted.Date is { } date)
        {
            var remote = ReadServerRecords(db, tx);
            var sameDay = remote.FirstOrDefault(record =>
                !record.Deleted && record.Date == date && record.Id != conflicted.RecordId);
            if (sameDay is not null)
            {
                if (current is null && conflicted.ExpectedVersion == 0
                    && !changes.Any(change => change.RecordId == sameDay.Id))
                {
                    // Two offline devices independently logged the same day. Adopt the server ID.
                    RemoveChange(db, tx, conflicted.RecordId);
                    return;
                }

                throw new InvalidOperationException("Дата уже занята другой записью. Примите её или освободите дату.");
            }

            EnsureDateAvailable(ReadEffective(db, tx), date, conflicted.RecordId);
        }

        SaveChange(db, tx, new(conflicted.RecordId, Guid.NewGuid(), current?.Version ?? 0, conflicted.Date));
    });

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
        using var observed = Command(db, tx, "SELECT COALESCE(MAX(version), 0) FROM server_records");
        var minimumRevision = Math.Max(header.Revision, Convert.ToInt64(observed.ExecuteScalar(), CultureInfo.InvariantCulture));
        if (header.DatasetId == state.DatasetId && state.Revision < minimumRevision)
        {
            throw new InvalidDataException("Сервер вернул более старую версию истории.");
        }

        if (header.DatasetId is { } previous && previous != state.DatasetId)
        {
            PreserveDatasetChanges(db, tx, state.Records);
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

    internal WireOperation? GetOrStageOperation() => InTransaction<WireOperation?>(deferred: false, (db, tx) =>
    {
        var current = ReadInflight(db, tx);
        if (current is not null)
        {
            return current;
        }

        var header = ReadHeader(db, tx);
        var changes = ReadChanges(db, tx).Where(change => change.ConflictCode is null).ToArray();
        if (header.DatasetId is not { } dataset || changes.Length == 0)
        {
            return null;
        }

        // If two pending changes depend on freeing a date, send the change that frees it first.
        var busyDates = ReadServerRecords(db, tx).Where(record => !record.Deleted);
        var chosen = changes.FirstOrDefault(change => !busyDates.Any(record =>
            record.Id != change.RecordId && record.Date == change.Date
            && changes.Any(other => other.RecordId == record.Id && other.Date != record.Date))) ?? changes[0];
        var operation = new WireOperation(dataset, chosen.OperationId, chosen.RecordId, chosen.ExpectedVersion,
            chosen.Date.HasValue ? "upsert" : "delete", chosen.Date);

        using var stage = Command(db, tx, "INSERT INTO inflight VALUES (1, $payload)",
            ("$payload", JsonSerializer.Serialize(operation, MeterJson.Options)));
        stage.ExecuteNonQuery();
        return operation;
    });

    internal void ApplyReply(WireOperation sent, OperationReply reply) => Write((db, tx) =>
    {
        if (ReadInflight(db, tx)?.OperationId != sent.OperationId)
        {
            return;
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

        if (reply.Outcome == "applied")
        {
            if (current.OperationId == sent.OperationId)
            {
                RemoveChange(db, tx, sent.RecordId);
            }
            else
            {
                // An edit after this request was staged must compare against the actual receipt,
                // even if a newer server update has already been downloaded.
                SaveChange(db, tx, current with { ExpectedVersion = reply.Record!.Version });
            }
        }
        else
        {
            var remote = reply.Existing ?? reply.Record;
            SaveChange(db, tx, current with
            {
                ConflictCode = reply.Outcome,
                ServerDate = remote?.Deleted == false ? remote.Date : null,
            });
        }

        ClearInflight(db, tx);
    });

    private static void PreserveDatasetChanges(SqliteConnection db, SqliteTransaction tx,
        IReadOnlyList<ServerRecord> incoming)
    {
        var byId = incoming.ToDictionary(record => record.Id);
        var activeByDate = incoming.Where(record => !record.Deleted).ToDictionary(record => record.Date);
        var changes = ReadChanges(db, tx);
        var changedIds = changes.Select(change => change.RecordId).ToHashSet();
        foreach (var change in changes)
        {
            SaveChange(db, tx, OnDatasetChange(change, byId, activeByDate));
        }

        foreach (var previous in ReadServerRecords(db, tx))
        {
            var before = previous.Deleted ? (DateOnly?)null : previous.Date;
            var now = byId.GetValueOrDefault(previous.Id);
            var after = now?.Deleted == false ? now.Date : (DateOnly?)null;
            if (before == after || changedIds.Contains(previous.Id))
            {
                continue;
            }

            var change = new LocalChange(previous.Id, Guid.NewGuid(), previous.Version, before);
            SaveChange(db, tx, OnDatasetChange(change, byId, activeByDate));
        }

        ClearInflight(db, tx);
    }

    private static LocalChange OnDatasetChange(LocalChange change, IReadOnlyDictionary<Guid, ServerRecord> byId,
        IReadOnlyDictionary<DateOnly, ServerRecord> activeByDate)
    {
        var current = byId.GetValueOrDefault(change.RecordId);
        var visible = current?.Deleted == false ? current.Date : (DateOnly?)null;
        if (visible is null && change.Date is { } date && activeByDate.ContainsKey(date))
        {
            visible = date;
        }

        return change with { ConflictCode = "dataset_changed", ServerDate = visible };
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
            WHERE excluded.version >= server_records.version
            """, ("$id", record.Id.ToString("D")), ("$date", ToDateText(record.Date)), ("$version", record.Version),
            ("$deleted", record.Deleted));
        update.ExecuteNonQuery();
    }

    private const string ChangeColumns =
        "SELECT record_id, operation_id, expected_version, date, conflict_code, server_date FROM local_changes";

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
            ReadNullableDate(row, 3), row.IsDBNull(4) ? null : row.GetString(4), ReadNullableDate(row, 5));

    private static void SaveChange(SqliteConnection db, SqliteTransaction tx, LocalChange change)
    {
        using var write = Command(db, tx, """
            INSERT INTO local_changes VALUES ($id, $operation, $expected, $date, $conflict, $server_date)
            ON CONFLICT (record_id) DO UPDATE SET operation_id = excluded.operation_id,
                expected_version = excluded.expected_version, date = excluded.date,
                conflict_code = excluded.conflict_code, server_date = excluded.server_date
            """, ("$id", change.RecordId.ToString("D")), ("$operation", change.OperationId.ToString("D")),
            ("$expected", change.ExpectedVersion), ("$date", ToDateText(change.Date)),
            ("$conflict", change.ConflictCode), ("$server_date", ToDateText(change.ServerDate)));
        write.ExecuteNonQuery();
    }

    private static void RemoveChange(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var delete = Command(db, tx, "DELETE FROM local_changes WHERE record_id = $id", ("$id", id.ToString("D")));
        delete.ExecuteNonQuery();
    }
}
