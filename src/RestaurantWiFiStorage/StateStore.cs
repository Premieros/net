using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RestaurantWiFiStorage;

/// <summary>
/// Shared, transactional state store used by the desktop app and Windows service.
/// Every read-modify-write operation holds a SQLite write transaction.
/// </summary>
public sealed class StateStore
{
    readonly string dbPath;
    readonly string legacyPath;
    readonly string stagedLegacyPath;
    readonly string connectionString;

    public StateStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Restaurant WiFi Control");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "wifi-state.db");
        legacyPath = Path.Combine(dir, "v9-data.json");
        stagedLegacyPath = Path.Combine(dir, "v9-data.import.json");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString();
        Initialize();
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=15000;";
        busy.ExecuteNonQuery();
        return connection;
    }

    void Initialize()
    {
        using var connection = Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = @"CREATE TABLE IF NOT EXISTS State (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            Json TEXT NOT NULL,
            Revision INTEGER NOT NULL DEFAULT 0
        );";
        schema.ExecuteNonQuery();

        using var tx = connection.BeginTransaction();
        using var exists = connection.CreateCommand();
        exists.Transaction = tx;
        exists.CommandText = "SELECT COUNT(*) FROM State WHERE Id=1;";
        if ((long)exists.ExecuteScalar()! == 0)
        {
            // If migration fails, the original file remains untouched.
            string json = "{}";
            if (File.Exists(legacyPath))
            {
                // The legacy application may have created this file with a DACL
                // that excludes LocalSystem. The *elevated installer* may stage
                // a byte-for-byte copy with a protected SYSTEM-readable ACL.
                // Never silently insert {} or discard legacy data on denied access.
                var importPath = legacyPath;
                try
                {
                    json = File.ReadAllText(legacyPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!File.Exists(stagedLegacyPath))
                        throw new InvalidDataException(
                            "Legacy v9-data.json cannot be read by the Gateway service. " +
                            "Install the latest version as Administrator to stage a protected " +
                            "migration copy; the original file remains untouched.", ex);
                    importPath = stagedLegacyPath;
                    json = File.ReadAllText(stagedLegacyPath);
                }
                _ = JsonNode.Parse(json)?.AsObject() ??
                    throw new InvalidDataException("Legacy JSON must be an object.");
                var backup = legacyPath + ".pre-sqlite.bak";
                if (!File.Exists(backup)) File.Copy(importPath, backup);
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO State(Id, Json, Revision) VALUES (1, $json, 0);";
            insert.Parameters.AddWithValue("$json", json);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public string Read()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM State WHERE Id=1;";
        return (string)command.ExecuteScalar()!;
    }

    public T Update<T>(Func<JsonObject, T> mutate)
    {
        using var connection = Open();
        // Serializable transactions acquire SQLite writer coordination, including across processes.
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT Json FROM State WHERE Id=1;";
        var root = JsonNode.Parse((string)read.ExecuteScalar()!)?.AsObject() ??
                   throw new InvalidDataException("Invalid state JSON.");
        var result = mutate(root);
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE State SET Json=$json, Revision=Revision+1 WHERE Id=1;";
        write.Parameters.AddWithValue("$json", root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (write.ExecuteNonQuery() != 1) throw new InvalidOperationException("State row missing.");
        transaction.Commit();
        return result;
    }
}
