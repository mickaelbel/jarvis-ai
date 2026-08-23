using System.Data;
using System.Text.Json;
using JarvisAI.Application.Agents;
using JarvisAI.Application.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JarvisAI.Infrastructure.Persistence;

public sealed class SqliteRunDataStore : IRunDataStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteRunDataStore> _logger;
    private SqliteConnection? _connection;
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public SqliteRunDataStore(string databasePath, ILogger<SqliteRunDataStore> logger)
    {
        _logger = logger;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS RunRecords (
                RunId TEXT PRIMARY KEY,
                Goal TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT,
                Status INTEGER NOT NULL DEFAULT 0,
                Model TEXT,
                PlanJson TEXT,
                FinalResponse TEXT,
                Error TEXT,
                Reason TEXT,
                Iterations INTEGER NOT NULL DEFAULT 0,
                Retries INTEGER NOT NULL DEFAULT 0,
                ToolsExecuted INTEGER NOT NULL DEFAULT 0,
                PromptTokens INTEGER NOT NULL DEFAULT 0,
                CompletionTokens INTEGER NOT NULL DEFAULT 0,
                PluginsJson TEXT,
                ReasoningTraceJson TEXT,
                MemoryNotesJson TEXT
            );

            CREATE TABLE IF NOT EXISTS RunSteps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId TEXT NOT NULL,
                StepIndex INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                Content TEXT NOT NULL,
                ToolName TEXT,
                Success INTEGER,
                DurationMs INTEGER,
                OccurredAt TEXT NOT NULL,
                FOREIGN KEY (RunId) REFERENCES RunRecords(RunId) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_RunSteps_RunId ON RunSteps(RunId);
            CREATE INDEX IF NOT EXISTS IX_RunRecords_StartedAt ON RunRecords(StartedAt DESC);
        ";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _initialized = true;
        _logger.LogInformation("SQLite run database initialized at {DataSource}", _connectionString.Split(';')[0]);
    }

    private SqliteConnection EnsureConnection()
    {
        if (!_initialized || _connection is null)
            throw new InvalidOperationException("SqliteRunDataStore is not initialized. Call InitializeAsync first.");
        return _connection;
    }

    public async Task UpsertAsync(RunRecord r, CancellationToken cancellationToken = default)
    {
        var conn = EnsureConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO RunRecords (
                    RunId, Goal, StartedAt, FinishedAt, Status, Model, PlanJson,
                    FinalResponse, Error, Reason, Iterations, Retries, ToolsExecuted,
                    PromptTokens, CompletionTokens, PluginsJson, ReasoningTraceJson, MemoryNotesJson
                ) VALUES (
                    @RunId, @Goal, @StartedAt, @FinishedAt, @Status, @Model, @PlanJson,
                    @FinalResponse, @Error, @Reason, @Iterations, @Retries, @ToolsExecuted,
                    @PromptTokens, @CompletionTokens, @PluginsJson, @ReasoningTraceJson, @MemoryNotesJson
                )";

            cmd.Parameters.AddWithValue("@RunId", r.RunId.ToString());
            cmd.Parameters.AddWithValue("@Goal", r.Goal);
            cmd.Parameters.AddWithValue("@StartedAt", r.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@FinishedAt", (object?)r.FinishedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", r.Status.ToString());
            cmd.Parameters.AddWithValue("@Model", (object?)r.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PlanJson", (object?)SerializePlan(r.Plan) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FinalResponse", (object?)r.FinalResponse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Error", (object?)r.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Reason", (object?)r.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Iterations", r.Iterations);
            cmd.Parameters.AddWithValue("@Retries", r.Retries);
            cmd.Parameters.AddWithValue("@ToolsExecuted", r.ToolsExecuted);
            cmd.Parameters.AddWithValue("@PromptTokens", r.PromptTokens);
            cmd.Parameters.AddWithValue("@CompletionTokens", r.CompletionTokens);
            cmd.Parameters.AddWithValue("@PluginsJson", SerializeList(r.Plugins));
            cmd.Parameters.AddWithValue("@ReasoningTraceJson", SerializeList(r.ReasoningTrace));
            cmd.Parameters.AddWithValue("@MemoryNotesJson", SerializeList(r.MemoryNotes));

            await cmd.ExecuteNonQueryAsync(cancellationToken);

            await UpsertStepsAsync(conn, tx, r.RunId, r.Steps, cancellationToken);

            tx.Commit();
            _logger.LogDebug("Upserted run {RunId}", r.RunId);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task UpsertStepsAsync(SqliteConnection conn, SqliteTransaction tx,
        Guid runId, IReadOnlyList<RunStep> steps, CancellationToken ct)
    {
        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM RunSteps WHERE RunId = @RunId";
        del.Parameters.AddWithValue("@RunId", runId.ToString());
        await del.ExecuteNonQueryAsync(ct);

        if (steps.Count == 0) return;

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT INTO RunSteps (RunId, StepIndex, Kind, Content, ToolName, Success, DurationMs, OccurredAt)
            VALUES (@RunId, @StepIndex, @Kind, @Content, @ToolName, @Success, @DurationMs, @OccurredAt)";

        var pRunId = ins.Parameters.Add("@RunId", SqliteType.Text);
        var pIdx = ins.Parameters.Add("@StepIndex", SqliteType.Integer);
        var pKind = ins.Parameters.Add("@Kind", SqliteType.Text);
        var pContent = ins.Parameters.Add("@Content", SqliteType.Text);
        var pTool = ins.Parameters.Add("@ToolName", SqliteType.Text);
        var pSuccess = ins.Parameters.Add("@Success", SqliteType.Integer);
        var pDur = ins.Parameters.Add("@DurationMs", SqliteType.Integer);
        var pOccurred = ins.Parameters.Add("@OccurredAt", SqliteType.Text);

        foreach (var s in steps)
        {
            pRunId.Value = runId.ToString();
            pIdx.Value = s.Index;
            pKind.Value = s.Kind.ToString();
            pContent.Value = s.Content;
            pTool.Value = (object?)s.ToolName ?? DBNull.Value;
            pSuccess.Value = s.Success.HasValue ? (object)(s.Success.Value ? 1L : 0L) : DBNull.Value;
            pDur.Value = s.Duration.HasValue ? (object)(long)s.Duration.Value.TotalMilliseconds : DBNull.Value;
            pOccurred.Value = s.OccurredAt.ToString("O");
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<RunRecord?> GetByIdAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var conn = EnsureConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM RunRecords WHERE RunId = @R";
        cmd.Parameters.AddWithValue("@R", runId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var record = MapRecord(reader);
        await LoadStepsAsync(record, cancellationToken);
        return record;
    }

    public async Task<IReadOnlyList<RunRecord>> GetAllAsync(CancellationToken cancellationToken = default)
        => await QueryRunsAsync("SELECT * FROM RunRecords ORDER BY StartedAt DESC", cancellationToken);

    public async Task<IReadOnlyList<RunRecord>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default)
        => await QueryRunsAsync(
            $"SELECT * FROM RunRecords ORDER BY StartedAt DESC LIMIT {Math.Max(1, count)}",
            cancellationToken);

    private async Task<IReadOnlyList<RunRecord>> QueryRunsAsync(string query, CancellationToken ct)
    {
        var conn = EnsureConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        using var reader = await cmd.ExecuteReaderAsync(ct);

        var runs = new List<RunRecord>();
        while (await reader.ReadAsync(ct))
        {
            var record = MapRecord(reader);
            await LoadStepsAsync(record, ct);
            runs.Add(record);
        }
        return runs.AsReadOnly();
    }

    private async Task LoadStepsAsync(RunRecord r, CancellationToken ct)
    {
        var conn = EnsureConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM RunSteps WHERE RunId = @R ORDER BY StepIndex";
        cmd.Parameters.AddWithValue("@R", r.RunId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idx = reader.GetInt32("StepIndex"!);
            var kind = Enum.Parse<RunStepKind>(reader.GetString("Kind"!));
            var content = reader.GetString("Content"!);
            var toolName = reader.IsDBNull("ToolName") ? null : reader.GetString("ToolName");
            var success = reader.IsDBNull("Success") ? null : (bool?)(reader.GetInt64("Success") == 1);
            var durMs = reader.IsDBNull("DurationMs") ? null : (TimeSpan?)TimeSpan.FromMilliseconds(reader.GetInt64("DurationMs"));
            var occurred = DateTimeOffset.Parse(reader.GetString("OccurredAt"));
            var step = new RunStep(idx, kind, content, toolName, success, durMs, occurred);
            r.LoadStepFromStorage(step);
        }
    }

    private static RunRecord MapRecord(SqliteDataReader reader)
    {
        var runId = Guid.Parse(reader.GetString("RunId"));
        var goal = reader.GetString("Goal");
        var startedAt = DateTimeOffset.Parse(reader.GetString("StartedAt"));
        var record = new RunRecord(runId, goal, startedAt);

        if (!reader.IsDBNull("FinishedAt"))
            record.ForceFinishedAt(DateTimeOffset.Parse(reader.GetString("FinishedAt")));

        record.ForceStatus(Enum.Parse<RunStatus>(reader.GetString("Status")));
        record.ForceModel(reader.IsDBNull("Model") ? null : reader.GetString("Model"));
        record.ForcePlan(DeserializePlan(reader.IsDBNull("PlanJson") ? null : reader.GetString("PlanJson")));
        record.ForceFinalResponse(reader.IsDBNull("FinalResponse") ? null : reader.GetString("FinalResponse"));
        record.ForceError(reader.IsDBNull("Error") ? null : reader.GetString("Error"));
        record.ForceReason(reader.IsDBNull("Reason") ? null : reader.GetString("Reason"));
        record.ForceIterations(reader.GetInt32("Iterations"));
        record.ForceRetries(reader.GetInt32("Retries"));
        record.ForceToolsExecuted(reader.GetInt32("ToolsExecuted"));
        record.ForceTokens(reader.GetInt64("PromptTokens"), reader.GetInt64("CompletionTokens"));
        record.ForcePlugins(DeserializeList(reader.GetString("PluginsJson")));
        record.ForceReasoningTrace(DeserializeList(reader.GetString("ReasoningTraceJson")));
        record.ForceMemoryNotes(DeserializeList(reader.GetString("MemoryNotesJson")));

        return record;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var conn = EnsureConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM RunRecords";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> DeleteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var conn = EnsureConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM RunRecords WHERE RunId = @R";
        cmd.Parameters.AddWithValue("@R", runId.ToString());
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var conn = EnsureConnection();
        using var t = conn.BeginTransaction();
        try
        {
            using var c1 = conn.CreateCommand();
            c1.Transaction = t;
            c1.CommandText = "DELETE FROM RunRecords";
            await c1.ExecuteNonQueryAsync(cancellationToken);

            using var c2 = conn.CreateCommand();
            c2.Transaction = t;
            c2.CommandText = "DELETE FROM RunSteps";
            await c2.ExecuteNonQueryAsync(cancellationToken);

            t.Commit();
        }
        catch
        {
            t.Rollback();
            throw;
        }
    }

    private static string SerializeList(IReadOnlyList<string> list)
        => JsonSerializer.Serialize(list, JsonOptions);

    private static List<string> DeserializeList(string json)
        => string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new();

    private static string? SerializePlan(Plan? plan)
        => plan is null ? null : JsonSerializer.Serialize(plan, JsonOptions);

    private static Plan? DeserializePlan(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Plan>(json, JsonOptions);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _initialized = false;
    }
}