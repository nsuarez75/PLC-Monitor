using System.Globalization;
using Microsoft.Data.Sqlite;
using PlcMonitor.Abstractions;
using PlcMonitor.Core;
using PlcMonitor.Engine;

namespace PlcMonitor.Historian;

/// <summary>
/// Histórico en SQLite: un fichero por mes (<c>history-AAAA-MM.db</c>) con las tablas <c>tags</c> y <c>samples</c>.
/// Así la retención borra meses enteros y los ficheros se pueden copiar o abrir con cualquier visor de SQLite.
/// </summary>
public sealed class SqliteHistorianStore : IDisposable
{
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        CREATE TABLE IF NOT EXISTS tags (
          id INTEGER PRIMARY KEY,
          device TEXT NOT NULL,
          tag TEXT NOT NULL,
          data_type TEXT,
          UNIQUE (device, tag));
        CREATE TABLE IF NOT EXISTS samples (
          tag_id INTEGER NOT NULL,
          t INTEGER NOT NULL,      -- milisegundos Unix (UTC)
          num REAL,                -- valor numérico (bool = 0/1)
          txt TEXT,                -- valor no numérico
          q INTEGER NOT NULL);     -- QualityCode
        CREATE INDEX IF NOT EXISTS ix_samples_tag_t ON samples (tag_id, t);
        """;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, SqliteConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string File, TagKey Key), long> _tagIds = [];

    public SqliteHistorianStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = System.IO.Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public static string FileName(DateTimeOffset month) => $"history-{month.UtcDateTime:yyyy-MM}.db";

    /// <summary>Guarda las muestras (en una transacción por fichero mensual).</summary>
    public async Task WriteAsync(IReadOnlyList<HistorySample> samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var month in samples.GroupBy(s => FileName(s.Timestamp)))
            {
                var connection = Open(month.Key, create: true)!;
                using var transaction = connection.BeginTransaction();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO samples (tag_id, t, num, txt, q) VALUES ($id, $t, $num, $txt, $q)";
                var id = insert.Parameters.Add("$id", SqliteType.Integer);
                var t = insert.Parameters.Add("$t", SqliteType.Integer);
                var num = insert.Parameters.Add("$num", SqliteType.Real);
                var txt = insert.Parameters.Add("$txt", SqliteType.Text);
                var q = insert.Parameters.Add("$q", SqliteType.Integer);
                foreach (var sample in month)
                {
                    id.Value = TagId(connection, transaction, month.Key, sample.Key, sample.DataType, create: true);
                    t.Value = sample.Timestamp.ToUnixTimeMilliseconds();
                    var number = HistorySampler.ToNumber(sample.Value);
                    num.Value = number is { } n ? n : DBNull.Value;
                    txt.Value = number is null && sample.Value is not null ? ValueFormatter.Format(sample.Value) : DBNull.Value;
                    q.Value = (int)sample.Quality;
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Puntos de un tag entre dos instantes, ordenados. Devuelve como mucho <paramref name="maxPoints"/> (+1 para saber si hay más).</summary>
    public async Task<IReadOnlyList<HistoryPoint>> ReadAsync(TagKey key, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken = default)
    {
        var points = new List<HistoryPoint>();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            for (var month = MonthStart(from); month <= to && points.Count <= maxPoints; month = month.AddMonths(1))
            {
                var file = FileName(month);
                if (Open(file, create: false) is not { } connection || TagId(connection, null, file, key, TagDataType.Unknown, create: false) is not { } id)
                    continue;
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT t, num, txt, q FROM samples WHERE tag_id = $id AND t >= $from AND t <= $to ORDER BY t LIMIT $limit";
                select.Parameters.AddWithValue("$id", id);
                select.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
                select.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
                select.Parameters.AddWithValue("$limit", maxPoints + 1 - points.Count);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    points.Add(new HistoryPoint(
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                        reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        (QualityCode)reader.GetInt32(3)));
                }
            }
        }
        finally
        {
            _lock.Release();
        }
        return points;
    }

    /// <summary>Borra lo anterior a <paramref name="before"/>: meses enteros por fichero y el resto del mes frontera.</summary>
    public async Task PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var boundary = FileName(before);
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "history-*.db"))
            {
                var file = System.IO.Path.GetFileName(path);
                if (string.CompareOrdinal(file, boundary) < 0)
                {
                    Close(file);
                    foreach (var extra in new[] { path, path + "-wal", path + "-shm" })
                        File.Delete(extra);
                }
                else if (file == boundary && Open(file, create: false) is { } connection)
                {
                    using var delete = connection.CreateCommand();
                    delete.CommandText = "DELETE FROM samples WHERE t < $before";
                    delete.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
                    delete.ExecuteNonQuery();
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Tamaño total y número de ficheros mensuales.</summary>
    public (long SizeBytes, int Files) Usage()
    {
        var files = new DirectoryInfo(Directory).EnumerateFiles("history-*.db*").ToArray();
        return (files.Sum(f => f.Length), files.Count(f => f.Name.EndsWith(".db", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values)
            connection.Dispose();
        _connections.Clear();
        _lock.Dispose();
    }

    private SqliteConnection? Open(string file, bool create)
    {
        if (_connections.TryGetValue(file, out var cached))
            return cached;
        var path = System.IO.Path.Combine(Directory, file);
        if (!create && !File.Exists(path))
            return null;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            schema.ExecuteNonQuery();
        }
        _connections[file] = connection;
        return connection;
    }

    private void Close(string file)
    {
        if (_connections.Remove(file, out var connection))
            connection.Dispose();
        foreach (var key in _tagIds.Keys.Where(k => k.File == file).ToArray())
            _tagIds.Remove(key);
    }

    private long? TagId(SqliteConnection connection, SqliteTransaction? transaction, string file, TagKey key, TagDataType type, bool create)
    {
        if (_tagIds.TryGetValue((file, key), out var cached))
            return cached;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM tags WHERE device = $d AND tag = $t";
            select.Parameters.AddWithValue("$d", key.Device);
            select.Parameters.AddWithValue("$t", key.Tag);
            if (select.ExecuteScalar() is long existing)
                return _tagIds[(file, key)] = existing;
        }
        if (!create)
            return null;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO tags (device, tag, data_type) VALUES ($d, $t, $type) RETURNING id";
        insert.Parameters.AddWithValue("$d", key.Device);
        insert.Parameters.AddWithValue("$t", key.Tag);
        insert.Parameters.AddWithValue("$type", type.ToString());
        return _tagIds[(file, key)] = (long)insert.ExecuteScalar()!;
    }

    private static DateTimeOffset MonthStart(DateTimeOffset t) => new(t.UtcDateTime.Year, t.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    internal static string Describe(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
