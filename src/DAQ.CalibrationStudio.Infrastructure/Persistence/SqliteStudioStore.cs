using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using Microsoft.Data.Sqlite;

namespace DAQ.CalibrationStudio.Infrastructure.Persistence;

/// <summary>SQLite-backed append-only run history and versioned profile store.</summary>
public sealed class SqliteStudioStore : IStudioStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    static SqliteStudioStore() => SQLitePCL.Batteries.Init();

    public SqliteStudioStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:") throw new ArgumentException("Für persistente Prüfhistorie ist ein Dateipfad erforderlich.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, ForeignKeys = true, Pooling = false }.ToString();
    }

    public async Task SaveRunAsync(RunResult run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var profileJson = Serialize(run.Profile);
        var snapshot = Serialize(run);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = """
                INSERT INTO Profiles(Id,Version,Name,Kind,SnapshotJson,SavedUtc)
                VALUES($id,$version,$name,$kind,$json,$utc)
                ON CONFLICT(Id,Version) DO NOTHING;
                """;
            profile.Parameters.AddWithValue("$id", run.Profile.Id.ToString("D"));
            profile.Parameters.AddWithValue("$version", run.Profile.Version);
            profile.Parameters.AddWithValue("$name", run.Profile.Name);
            profile.Parameters.AddWithValue("$kind", (int)run.Profile.Kind);
            profile.Parameters.AddWithValue("$json", profileJson);
            profile.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            await profile.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await EnsureSameProfileSnapshotAsync(connection, transaction, run.Profile, profileJson, ct).ConfigureAwait(false);

        await using (var device = connection.CreateCommand())
        {
            device.Transaction = transaction;
            device.CommandText = """
                INSERT INTO Devices(DeviceKey,Model,Serial,Firmware,Revision,LastSeenUtc)
                VALUES($key,$model,$serial,$firmware,$revision,$utc)
                ON CONFLICT(DeviceKey) DO UPDATE SET Model=excluded.Model, Serial=excluded.Serial,
                    Firmware=excluded.Firmware, Revision=excluded.Revision, LastSeenUtc=excluded.LastSeenUtc;
                """;
            device.Parameters.AddWithValue("$key", string.IsNullOrWhiteSpace(run.Daq.Serial) ? run.Daq.Model : run.Daq.Serial);
            device.Parameters.AddWithValue("$model", run.Daq.Model);
            device.Parameters.AddWithValue("$serial", Db(run.Daq.Serial));
            device.Parameters.AddWithValue("$firmware", Db(run.Daq.Firmware));
            device.Parameters.AddWithValue("$revision", Db(run.Daq.Revision));
            device.Parameters.AddWithValue("$utc", run.Finished.ToUniversalTime().ToString("O"));
            await device.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Runs(Id,StartedUtc,FinishedUtc,ProfileId,ProfileVersion,ProfileName,Kind,Outcome,
                    DeviceModel,DeviceSerial,HardwareRevision,FirmwareVersion,Operator,Simulated,SnapshotJson,Sha256)
                VALUES($id,$started,$finished,$profileId,$profileVersion,$profileName,$kind,$outcome,
                    $model,$serial,$revision,$firmware,$operator,$simulated,$snapshot,$hash);
                """;
            command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            command.Parameters.AddWithValue("$started", run.Started.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$finished", run.Finished.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$profileId", run.Profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileVersion", run.Profile.Version);
            command.Parameters.AddWithValue("$profileName", run.Profile.Name);
            command.Parameters.AddWithValue("$kind", (int)run.Profile.Kind);
            command.Parameters.AddWithValue("$outcome", (int)run.Outcome);
            command.Parameters.AddWithValue("$model", run.Daq.Model);
            command.Parameters.AddWithValue("$serial", Db(run.Daq.Serial));
            command.Parameters.AddWithValue("$revision", Db(run.Daq.Revision));
            command.Parameters.AddWithValue("$firmware", Db(run.Daq.Firmware));
            command.Parameters.AddWithValue("$operator", run.Operator);
            command.Parameters.AddWithValue("$simulated", run.Simulated ? 1 : 0);
            command.Parameters.AddWithValue("$snapshot", snapshot);
            command.Parameters.AddWithValue("$hash", hash);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunResult>> ListRunsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,SnapshotJson,Sha256 FROM Runs ORDER BY StartedUtc DESC, Id DESC;";
        var runs = new List<RunResult>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var json = reader.GetString(1);
            VerifyHash(reader.GetString(0), json, reader.GetString(2));
            runs.Add(Deserialize<RunResult>(json));
        }
        return runs;
    }

    public async Task<IReadOnlyList<RunResult>> SearchRunsAsync(string? text = null, DateTimeOffset? from = null,
        DateTimeOffset? to = null, string? serial = null, Guid? profileId = null, RunOutcome? outcome = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,SnapshotJson,Sha256 FROM Runs
            WHERE ($text IS NULL OR ProfileName LIKE $pattern OR DeviceSerial LIKE $pattern OR Operator LIKE $pattern)
              AND ($from IS NULL OR StartedUtc >= $from) AND ($to IS NULL OR StartedUtc <= $to)
              AND ($serial IS NULL OR DeviceSerial = $serial)
              AND ($profileId IS NULL OR ProfileId = $profileId)
              AND ($outcome IS NULL OR Outcome = $outcome)
            ORDER BY StartedUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$text", Db(text));
        command.Parameters.AddWithValue("$pattern", text is null ? DBNull.Value : $"%{text}%");
        command.Parameters.AddWithValue("$from", Db(from?.ToUniversalTime().ToString("O")));
        command.Parameters.AddWithValue("$to", Db(to?.ToUniversalTime().ToString("O")));
        command.Parameters.AddWithValue("$serial", Db(serial));
        command.Parameters.AddWithValue("$profileId", profileId is null ? DBNull.Value : profileId.Value.ToString("D"));
        command.Parameters.AddWithValue("$outcome", outcome is null ? DBNull.Value : (int)outcome.Value);
        var results = new List<RunResult>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var json = reader.GetString(1);
            VerifyHash(reader.GetString(0), json, reader.GetString(2));
            results.Add(Deserialize<RunResult>(json));
        }
        return results;
    }

    public async Task SaveProfileAsync(TestProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var json = Serialize(profile);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Profiles(Id,Version,Name,Kind,SnapshotJson,SavedUtc) VALUES($id,$version,$name,$kind,$json,$utc) ON CONFLICT(Id,Version) DO NOTHING;";
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$kind", (int)profile.Kind);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await EnsureSameProfileSnapshotAsync(connection, null, profile, json, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TestProfile>> ListProfilesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM Profiles ORDER BY Name COLLATE NOCASE, Id, Version DESC;";
        var profiles = new List<TestProfile>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) profiles.Add(Deserialize<TestProfile>(reader.GetString(0)));
        return profiles;
    }

    public async Task AddApprovalAsync(Approval approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Approvals(Id,RunId,TimeUtc,Operator,Target,Verified,SnapshotJson) VALUES($id,$runId,$time,$operator,$target,$verified,$json);";
        command.Parameters.AddWithValue("$id", approval.Id.ToString("D"));
        command.Parameters.AddWithValue("$runId", approval.RunId.ToString("D"));
        command.Parameters.AddWithValue("$time", approval.Time.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$operator", approval.Operator);
        command.Parameters.AddWithValue("$target", approval.Target);
        command.Parameters.AddWithValue("$verified", approval.Verified ? 1 : 0);
        command.Parameters.AddWithValue("$json", Serialize(approval));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Approval>> ListApprovalsAsync(Guid runId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM Approvals WHERE RunId=$id ORDER BY TimeUtc, Id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        var approvals = new List<Approval>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) approvals.Add(Deserialize<Approval>(reader.GetString(0)));
        return approvals;
    }

    public async Task<StudioSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM Settings WHERE Id=1;";
        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return json is null ? new StudioSettings() : Deserialize<StudioSettings>(json);
    }

    public async Task SaveSettingsAsync(StudioSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Id,SnapshotJson,UpdatedUtc) VALUES(1,$json,$utc) ON CONFLICT(Id) DO UPDATE SET SnapshotJson=excluded.SnapshotJson, UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$json", Serialize(settings));
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureSameProfileSnapshotAsync(SqliteConnection connection, SqliteTransaction? transaction,
        TestProfile profile, string expectedJson, CancellationToken ct)
    {
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT SnapshotJson FROM Profiles WHERE Id=$id AND Version=$version;";
        check.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        check.Parameters.AddWithValue("$version", profile.Version);
        var existing = await check.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (existing != expectedJson)
            throw new InvalidOperationException("Eine Profilversion ist unveränderlich. Änderungen benötigen eine neue Versionsnummer.");
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS SchemaMigrations(Version INTEGER PRIMARY KEY, AppliedUtc TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var assembly = typeof(SqliteStudioStore).Assembly;
            foreach (var resourceName in assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("DAQ.CalibrationStudio.Infrastructure.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                var migrationFile = Path.GetFileNameWithoutExtension(resourceName).Split('.').Last();
                var separator = migrationFile.IndexOf('_');
                if (separator < 1 || !int.TryParse(migrationFile.AsSpan(0, separator), out var version))
                    throw new InvalidOperationException($"Ungültiger Migrationsname: {resourceName}");
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM SchemaMigrations WHERE Version=$version);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$version", version);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                    continue;
                using var migration = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Die Datenbankmigration {resourceName} fehlt.");
                using var migrationReader = new StreamReader(migration);
                var sql = await migrationReader.ReadToEndAsync(ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                command.CommandText = "INSERT INTO SchemaMigrations(Version,AppliedUtc) VALUES($version,$utc);";
                command.Parameters.AddWithValue("$version", version);
                command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                command.Transaction = null;
            }
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException($"Gespeicherte Daten konnten nicht als {typeof(T).Name} gelesen werden.");
    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static void VerifyHash(string runId, string json, string expectedHash)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException exception) { throw new InvalidDataException($"Der Prüflauf-Hash ist ungültig: {runId}", exception); }
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException($"Der gespeicherte Prüflauf-Hash stimmt nicht: {runId}");
    }

}
