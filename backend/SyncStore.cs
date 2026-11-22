using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VapeMeter.Backend;

internal sealed class SyncStore(string databasePath)
{
    private const int SchemaVersion = 1;

    public void Initialize()
    {
        using var db = Open(SqliteOpenMode.ReadWriteCreate);
        // For this tiny database, a rollback journal and short transactions are sufficient.
        using var tx = db.BeginTransaction(deferred: false);
        using var version = Command(db, tx, "PRAGMA user_version");
        var current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (current == SchemaVersion)
        {
            return;
        }
        if (current != 0)
        {
            throw new InvalidOperationException($"Unsupported database schema version {current}.");
        }

        using var migration = Command(db, tx, """
            CREATE TABLE sync_state (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                dataset_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision >= 0)
            );
            INSERT INTO sync_state VALUES (1, $dataset, 0);

            CREATE TABLE replacement (
                id TEXT PRIMARY KEY NOT NULL,
                date TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                deleted INTEGER NOT NULL CHECK (deleted IN (0, 1))
            );
            CREATE UNIQUE INDEX replacement_active_date ON replacement(date) WHERE deleted = 0;

            CREATE TABLE operation_receipt (
                operation_id TEXT PRIMARY KEY NOT NULL,
                fingerprint BLOB NOT NULL,
                status INTEGER NOT NULL,
                response TEXT NOT NULL
            );

            PRAGMA user_version = 1;
            """, ("$dataset", Guid.NewGuid().ToString("D")));
        migration.ExecuteNonQuery();
        tx.Commit();
    }

    public SyncState ReadState()
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var (dataset, revision) = ReadHeader(db, tx);
        using var query = Command(db, tx, "SELECT id, date, version, deleted FROM replacement ORDER BY date, id");
        using var rows = query.ExecuteReader();
        var records = new List<Replacement>();
        while (rows.Read())
        {
            records.Add(ReadReplacement(rows));
        }
        return new SyncState(dataset, revision, records);
    }

    public OperationResult Apply(string device, SyncOperation operation)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var (dataset, revision) = ReadHeader(db, tx);
        if (operation.DatasetId != dataset)
        {
            return new(409, new("dataset_changed", dataset, revision));
        }

        var operationId = operation.OperationId.ToString("D");
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { device, operation }));
        using (var receipt = Command(db, tx,
            "SELECT fingerprint, status, response FROM operation_receipt WHERE operation_id = $operation",
            ("$operation", operationId)))
        using (var row = receipt.ExecuteReader())
        {
            if (row.Read())
            {
                if (!CryptographicOperations.FixedTimeEquals(row.GetFieldValue<byte[]>(0), fingerprint))
                {
                    return new(409, new("operation_id_reused", dataset, revision));
                }

                return new(row.GetInt32(1), JsonSerializer.Deserialize<OperationReply>(row.GetString(2))
                    ?? throw new InvalidOperationException("Invalid operation receipt."));
            }
        }

        var record = Find(db, tx, "WHERE id = $value", operation.RecordId.ToString("D"));
        OperationResult result;
        if (operation.ExpectedVersion != (record?.Version ?? 0))
        {
            result = new(409, new("version_conflict", dataset, revision, record));
        }
        else if (operation.Action == "delete" && record is { Deleted: true })
        {
            result = new(200, new("applied", dataset, revision, record));
        }
        else
        {
            var date = operation.Date ?? record!.Date;
            var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var deleted = operation.Action == "delete";
            var sameDay = deleted ? null : Find(db, tx, "WHERE date = $value AND deleted = 0", dateText);
            if (sameDay is not null && sameDay.Id != operation.RecordId)
            {
                result = new(409, new("duplicate_date", dataset, revision, record, sameDay));
            }
            else if (record is { Deleted: false } && !deleted && record.Date == date)
            {
                result = new(200, new("applied", dataset, revision, record));
            }
            else
            {
                revision = checked(revision + 1);
                using var update = Command(db, tx, """
                    INSERT INTO replacement (id, date, version, deleted) VALUES ($id, $date, $revision, $deleted)
                    ON CONFLICT (id) DO UPDATE SET
                        date = excluded.date, version = excluded.version, deleted = excluded.deleted;
                    UPDATE sync_state SET revision = $revision WHERE singleton = 1;
                    """,
                    ("$id", operation.RecordId.ToString("D")), ("$date", dateText),
                    ("$revision", revision), ("$deleted", deleted));

                update.ExecuteNonQuery();
                result = new(200, new("applied", dataset, revision, new(operation.RecordId, date, revision, deleted)));
            }
        }

        using var save = Command(db, tx, """
            INSERT INTO operation_receipt VALUES ($operation, $fingerprint, $status, $response);
            """, ("$operation", operationId), ("$fingerprint", fingerprint), ("$status", result.StatusCode),
            ("$response", JsonSerializer.Serialize(result.Reply)));
        save.ExecuteNonQuery();
        tx.Commit();
        return result;
    }

    public Guid RenewDataset()
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        _ = ReadHeader(db, tx);
        var dataset = Guid.NewGuid();
        using var update = Command(db, tx, """
            UPDATE sync_state SET dataset_id = $dataset WHERE singleton = 1;
            DELETE FROM operation_receipt;
            """, ("$dataset", dataset.ToString("D")));
        update.ExecuteNonQuery();
        tx.Commit();
        return dataset;
    }

    public void Backup(string destination)
    {
        var target = Path.GetFullPath(destination);
        var temporaryPath = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using (var source = Open())
            using (var copy = Connect(temporaryPath, SqliteOpenMode.ReadWriteCreate, pooling: false))
            {
                source.BackupDatabase(copy);
                using var check = Command(copy, null, "PRAGMA quick_check");
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("SQLite backup integrity check failed.");
                }
            }
            File.Move(temporaryPath, target);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadWrite) =>
        Connect(databasePath, mode);

    private static SqliteConnection Connect(string path, SqliteOpenMode mode, bool pooling = true)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = pooling, DefaultTimeout = 4
        }.ToString());
        try
        {
            db.Open();
            using var durability = Command(db, null, "PRAGMA synchronous = FULL");
            durability.ExecuteNonQuery();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static (Guid Dataset, long Revision) ReadHeader(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = Command(db, tx, "SELECT dataset_id, revision FROM sync_state WHERE singleton = 1");
        using var row = query.ExecuteReader();
        if (!row.Read())
        {
            throw new InvalidOperationException("Dataset metadata is missing.");
        }
        return (Guid.Parse(row.GetString(0)), row.GetInt64(1));
    }

    private static Replacement? Find(SqliteConnection db, SqliteTransaction tx, string condition, string value)
    {
        using var query = Command(db, tx, $"SELECT id, date, version, deleted FROM replacement {condition}", ("$value", value));
        using var row = query.ExecuteReader();
        return row.Read() ? ReadReplacement(row) : null;
    }

    private static Replacement ReadReplacement(SqliteDataReader row) =>
        new(Guid.Parse(row.GetString(0)), DateOnly.ParseExact(row.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.GetInt64(2), row.GetBoolean(3));

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return cmd;
    }
}
