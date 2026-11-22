using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VapeMeter.Core;

public sealed partial class MeterStore
{
    private const int SchemaVersion = 2;
    private const int ApplicationId = 0x564D4554;
    private readonly string databasePath;

    internal SemaphoreSlim SyncLock { get; } = new(1, 1);

    public MeterStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(this.databasePath)!);
        using var db = Connect(this.databasePath, SqliteOpenMode.ReadWriteCreate);
        using var tx = db.BeginTransaction(deferred: false);
        var version = ReadPragma(db, tx, "PRAGMA user_version");
        var application = ReadPragma(db, tx, "PRAGMA application_id");
        if (version != SchemaVersion || application != ApplicationId)
        {
            if (application == ApplicationId && version == 1)
            {
                MigrateFrom1(db, tx);
            }
            else
            {
                Initialize(db, tx, version, application);
            }
        }

        tx.Commit();
    }

    public string Theme
    {
        get => Read((db, tx) =>
        {
            using var query = Command(db, tx, "SELECT theme FROM settings WHERE singleton = 1");
            return (string?)query.ExecuteScalar() == "Light" ? "Light" : "Dark";
        });
        set
        {
            if (value is not ("Dark" or "Light"))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Write((db, tx) =>
            {
                using var update = Command(db, tx, "UPDATE settings SET theme = $theme WHERE singleton = 1",
                    ("$theme", value));
                update.ExecuteNonQuery();
            });
        }
    }

    // Call this on a separate demo database. The marker also survives deletion of all demo rows.
    public void SeedIfEmpty(IEnumerable<DateOnly> dates) => Write((db, tx) =>
    {
        using var mark = Command(db, tx, """
            UPDATE settings SET demo_seeded = 1
            WHERE singleton = 1 AND demo_seeded = 0
                AND NOT EXISTS (SELECT 1 FROM local_changes)
                AND NOT EXISTS (SELECT 1 FROM server_records)
                AND EXISTS (SELECT 1 FROM sync_state WHERE dataset_id IS NULL AND last_synced IS NULL)
            """);
        if (mark.ExecuteNonQuery() != 0)
        {
            SeedDates(db, tx, dates);
        }
    });

    public void ResetDemo(IEnumerable<DateOnly> dates) => Write((db, tx) =>
    {
        using var check = Command(db, tx, "SELECT demo_seeded FROM settings WHERE singleton = 1");
        var seeded = Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        var state = ReadHeader(db, tx);
        if (!seeded || state.DatasetId.HasValue || state.LastSynced.HasValue || ReadInflight(db, tx) is not null)
        {
            throw new InvalidOperationException("Сброс доступен только для демонстрационной истории.");
        }

        using var clear = Command(db, tx, "DELETE FROM local_changes");
        clear.ExecuteNonQuery();
        SeedDates(db, tx, dates);
    });

    private static void SeedDates(SqliteConnection db, SqliteTransaction tx, IEnumerable<DateOnly> dates)
    {
        foreach (var date in dates.Distinct())
        {
            SaveChange(db, tx, new(Guid.NewGuid(), Guid.NewGuid(), 0, date));
        }
    }

    public IReadOnlyList<Replacement> GetReplacements() => Read((db, tx) => ReadEffective(db, tx));

    public Replacement AddReplacement(DateOnly date) => InTransaction(deferred: false, (db, tx) =>
    {
        EnsureDateAvailable(ReadEffective(db, tx), date);
        var record = new Replacement(Guid.NewGuid(), date);
        SaveChange(db, tx, new(record.Id, Guid.NewGuid(), 0, date));
        return record;
    });

    public void ChangeReplacement(Guid id, DateOnly date) => Write((db, tx) =>
    {
        var records = ReadEffective(db, tx);
        var current = RequireVisible(records, id);
        var prior = ReadChange(db, tx, id);
        EnsureNoConflict(prior);
        if (current.Date == date)
        {
            return;
        }

        EnsureDateAvailable(records, date, id);
        var remote = ReadServerRecord(db, tx, id);
        var expected = prior?.ExpectedVersion ?? remote?.Version ?? 0;
        var previousDate = prior is null ? ActiveDate(remote) : prior.PreviousDate;
        SaveChange(db, tx, new(id, Guid.NewGuid(), expected, date, PreviousDate: previousDate));
    });

    public void DeleteReplacement(Guid id) => Write((db, tx) =>
    {
        RequireVisible(ReadEffective(db, tx), id);
        var prior = ReadChange(db, tx, id);
        EnsureNoConflict(prior);
        var remote = ReadServerRecord(db, tx, id);
        var expected = prior?.ExpectedVersion ?? remote?.Version ?? 0;
        if (expected == 0 && ReadInflight(db, tx)?.RecordId != id)
        {
            RemoveChange(db, tx, id);
            return;
        }

        var previousDate = prior is null ? ActiveDate(remote) : prior.PreviousDate;
        SaveChange(db, tx, new(id, Guid.NewGuid(), expected, null, PreviousDate: previousDate));
    });

    public void Export(string path)
    {
        var file = new HistoryFile(1, GetReplacements());
        WriteNewFile(path, temp => File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, MeterJson.Options)));
    }

    public int Import(string path)
    {
        using var input = File.OpenRead(path);
        var history = JsonSerializer.Deserialize<HistoryFile>(input, MeterJson.Options);
        if (history is not { Version: 1, Records: not null }
            || history.Records.Any(record => record is null || record.Id == Guid.Empty))
        {
            throw new InvalidDataException("Неподдерживаемый формат истории Vape Meter.");
        }

        return MergeHistory(history.Records);
    }

    public void Backup(string path) => WriteNewFile(path, temp =>
    {
        using var source = Connect(databasePath, SqliteOpenMode.ReadOnly);
        using var copy = Connect(temp, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(copy);
        VerifyIntegrity(copy);
    });

    public void Restore(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Резервная копия не найдена.", path);
        }

        using var backup = Connect(Path.GetFullPath(path), SqliteOpenMode.ReadOnly);
        VerifyIntegrity(backup);
        using var tx = backup.BeginTransaction(deferred: true);
        if (ReadPragma(backup, tx, "PRAGMA application_id") != ApplicationId
            || ReadPragma(backup, tx, "PRAGMA user_version") is < 1 or > SchemaVersion)
        {
            throw new InvalidDataException("Этот файл не является совместимой резервной копией Vape Meter.");
        }

        var records = ReadEffective(backup, tx);
        tx.Commit();
        MergeHistory(records);
    }

    private int MergeHistory(IEnumerable<Replacement> records) => InTransaction(deferred: false, (db, tx) =>
    {
        var knownIds = ReadServerRecords(db, tx).Select(record => record.Id)
            .Concat(ReadChanges(db, tx).Select(change => change.RecordId)).ToHashSet();
        var activeDates = ReadEffective(db, tx).Select(record => record.Date).ToHashSet();
        var added = 0;
        foreach (var record in records.OrderBy(record => record.Date))
        {
            if (knownIds.Contains(record.Id) || activeDates.Contains(record.Date))
            {
                continue;
            }

            knownIds.Add(record.Id);
            activeDates.Add(record.Date);
            SaveChange(db, tx, new(record.Id, Guid.NewGuid(), 0, record.Date));
            added++;
        }

        return added;
    });

    private static void Initialize(SqliteConnection db, SqliteTransaction tx, int version, int application)
    {
        using var existing = Command(db, tx, "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%')");
        if (version != 0 || application != 0 || Convert.ToInt64(existing.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException("Формат базы Vape Meter не поддерживается.");
        }

        using var schema = Command(db, tx, """
            CREATE TABLE settings (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                theme TEXT NOT NULL CHECK (theme IN ('Dark', 'Light')),
                demo_seeded INTEGER NOT NULL CHECK (demo_seeded IN (0, 1))
            );
            INSERT INTO settings VALUES (1, 'Dark', 0);

            CREATE TABLE sync_state (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                dataset_id TEXT,
                revision INTEGER NOT NULL CHECK (revision >= 0),
                etag TEXT,
                last_synced TEXT
            );
            INSERT INTO sync_state VALUES (1, NULL, 0, NULL, NULL);

            CREATE TABLE server_records (
                record_id TEXT PRIMARY KEY NOT NULL,
                date TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                deleted INTEGER NOT NULL CHECK (deleted IN (0, 1))
            );

            CREATE TABLE local_changes (
                record_id TEXT PRIMARY KEY NOT NULL,
                operation_id TEXT NOT NULL UNIQUE,
                expected_version INTEGER NOT NULL CHECK (expected_version >= 0),
                date TEXT,
                conflict_code TEXT,
                previous_date TEXT,
                merge_on_date INTEGER NOT NULL DEFAULT 0 CHECK (merge_on_date IN (0, 1))
            );

            CREATE TABLE inflight (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                payload TEXT NOT NULL
            );
            """);
        schema.ExecuteNonQuery();
        using var mark = Command(db, tx, $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {SchemaVersion};");
        mark.ExecuteNonQuery();
    }

    private static void MigrateFrom1(SqliteConnection db, SqliteTransaction tx)
    {
        using var migrate = Command(db, tx, """
            ALTER TABLE local_changes ADD COLUMN previous_date TEXT;
            ALTER TABLE local_changes ADD COLUMN merge_on_date INTEGER NOT NULL DEFAULT 0 CHECK (merge_on_date IN (0, 1));
            UPDATE local_changes SET previous_date = (
                SELECT date FROM server_records
                WHERE server_records.record_id = local_changes.record_id
                    AND server_records.version = local_changes.expected_version AND deleted = 0
            );
            PRAGMA user_version = 2;
            """);
        migrate.ExecuteNonQuery();
    }

    private static IReadOnlyList<Replacement> ReadEffective(SqliteConnection db, SqliteTransaction tx)
    {
        // Keep both IDs in the database when a pending local change and the server use the same date.
        // The pending intent owns the visible date until the user resolves the conflict.
        var changedIds = new HashSet<Guid>();
        var local = new List<Replacement>();
        using (var query = Command(db, tx, "SELECT record_id, date FROM local_changes ORDER BY rowid"))
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                var id = Guid.Parse(rows.GetString(0));
                changedIds.Add(id);
                if (ReadNullableDate(rows, 1) is { } date)
                {
                    local.Add(new(id, date));
                }
            }
        }
        var remote = ReadServerRecords(db, tx)
            .Where(record => !record.Deleted && !changedIds.Contains(record.Id))
            .Select(record => new Replacement(record.Id, record.Date));
        return local.Concat(remote).DistinctBy(record => record.Date).OrderBy(record => record.Date).ToArray();
    }

    private static Replacement RequireVisible(IEnumerable<Replacement> records, Guid id) =>
        records.FirstOrDefault(record => record.Id == id)
        ?? throw new InvalidOperationException("Запись уже удалена или изменилась при синхронизации.");

    private static void EnsureDateAvailable(IEnumerable<Replacement> records, DateOnly date, Guid? except = null)
    {
        if (records.Any(record => record.Date == date && record.Id != except))
        {
            throw new InvalidOperationException("На эту дату уже есть замена.");
        }
    }

    private static void EnsureNoConflict(LocalChange? pending)
    {
        if (pending?.ConflictCode is not null)
        {
            throw new InvalidOperationException("Сначала разрешите конфликт этой записи в разделе синхронизации.");
        }
    }

    private static void WriteNewFile(string path, Action<string> write)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            write(temp);
            File.Move(temp, fullPath);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void VerifyIntegrity(SqliteConnection db)
    {
        using var query = db.CreateCommand();
        query.CommandText = "PRAGMA quick_check";
        if ((string?)query.ExecuteScalar() != "ok")
        {
            throw new InvalidDataException("Проверка целостности базы завершилась ошибкой.");
        }
    }

    private T Read<T>(Func<SqliteConnection, SqliteTransaction, T> query) => InTransaction(deferred: true, query);

    private void Write(Action<SqliteConnection, SqliteTransaction> update) => InTransaction(deferred: false, (db, tx) =>
    {
        update(db, tx);
        return true;
    });

    private T InTransaction<T>(bool deferred, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var db = Connect(databasePath, SqliteOpenMode.ReadWrite);
        using var tx = db.BeginTransaction(deferred);
        var result = action(db, tx);
        tx.Commit();
        return result;
    }

    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 4,
        }.ToString());
        try
        {
            db.Open();
            if (mode != SqliteOpenMode.ReadOnly)
            {
                using var durability = db.CreateCommand();
                durability.CommandText = "PRAGMA synchronous = FULL";
                durability.ExecuteNonQuery();
            }

            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static int ReadPragma(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var pragma = Command(db, tx, sql);
        return Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? ToDateText(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ReadNullableDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : ParseDate(reader.GetString(index));
}
