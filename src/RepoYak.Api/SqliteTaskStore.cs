using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A;
using Microsoft.Data.Sqlite;

namespace RepoYak.Api;

public sealed class TaskHistoryHealth
{
    private readonly CancellationTokenSource _failure = new();
    public bool Failed => _failure.IsCancellationRequested;
    public CancellationToken FailureToken => _failure.Token;
    public void Fail() => _failure.Cancel();
}

public sealed class SqliteTaskStore(string databasePath, string owner, TaskHistoryHealth? health = null) : ITaskStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, AgentTask> _live = new();
    private readonly TaskHistoryHealth _health = health ?? new();
    public CancellationToken FailureToken => _health.FailureToken;

    public static string OwnerKey(string hostId, string agentId, string workspace)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{hostId}\0{agentId}\0{path}")));
    }

    public void EnsureAvailable()
    {
        if (_health.Failed)
        {
            throw new InvalidOperationException("Task history storage is unavailable. Repair storage and restart the host before admitting work.");
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static async Task InitializeAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var create = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString()))
        {
            await create.OpenAsync(cancellationToken);
        }
        using var connection = Open(path);
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version > 1)
        {
            throw new InvalidOperationException("Task history schema is newer than this host supports.");
        }
        using var transaction = connection.BeginTransaction();
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS history_agents (
                owner TEXT PRIMARY KEY, host_id TEXT NOT NULL, agent_id TEXT NOT NULL, workspace TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tasks (
                owner TEXT NOT NULL REFERENCES history_agents(owner), id TEXT NOT NULL,
                context_id TEXT NOT NULL, snapshot TEXT NOT NULL, state TEXT NOT NULL,
                status_ticks INTEGER, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                completed_utc TEXT, revision INTEGER NOT NULL, PRIMARY KEY(owner,id)
            );
            CREATE INDEX IF NOT EXISTS task_queries ON tasks(owner,state,context_id,status_ticks);
            CREATE TABLE IF NOT EXISTS task_transitions (
                owner TEXT NOT NULL, task_id TEXT NOT NULL, revision INTEGER NOT NULL,
                state TEXT NOT NULL, observed_utc TEXT NOT NULL, message TEXT, reason TEXT,
                PRIMARY KEY(owner,task_id,revision),
                FOREIGN KEY(owner,task_id) REFERENCES tasks(owner,id) ON DELETE CASCADE
            );
            PRAGMA user_version=1;
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken);
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT owner,id,snapshot,revision FROM tasks WHERE completed_utc IS NULL;";
        var interrupted = new List<(string Owner, AgentTask Task, long Revision)>();
        using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                interrupted.Add((reader.GetString(0), Deserialize(reader.GetString(2)), reader.GetInt64(3)));
            }
        }
        foreach (var item in interrupted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = item.Task;
            task.Status = new A2A.TaskStatus
            {
                State = TaskState.Failed, Timestamp = DateTimeOffset.UtcNow,
                Message = new Message
                {
                    MessageId = Guid.NewGuid().ToString("N"), TaskId = task.Id, ContextId = task.ContextId,
                    Role = Role.Agent,
                    Parts = [Part.FromText("Host execution was interrupted. Completion and external effects are unknown; work was not replayed.")]
                }
            };
            await WriteAsync(connection, transaction, item.Owner, task, Serialize(task),
                item.Revision + 1, true, "host-interrupted-outcome-unknown", cancellationToken);
        }
        transaction.Commit();
    }

    public static async Task RegisterOwnerAsync(string path, string owner, string hostId,
        AgentRegistration registration, CancellationToken cancellationToken)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO history_agents(owner,host_id,agent_id,workspace)
            VALUES($owner,$host,$agent,$workspace);
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        command.Parameters.AddWithValue("$agent", registration.Id);
        command.Parameters.AddWithValue("$workspace", registration.WorkspacePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return await RunAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot FROM tasks WHERE owner=$owner AND id=$id;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$id", taskId);
            var snapshot = await command.ExecuteScalarAsync(cancellationToken) as string;
            return snapshot is null ? null
                : _live.TryGetValue(taskId, out var live) ? Deserialize(Serialize(live)) : Deserialize(snapshot);
        }, cancellationToken);
    }

    public async Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken = default)
    {
        if (task.Id != taskId)
        {
            throw new ArgumentException("Task ID does not match its storage key.", nameof(taskId));
        }
        var liveSnapshot = Serialize(task);
        var sanitized = Sanitize(task);
        await RunAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT snapshot,revision FROM tasks WHERE owner=$owner AND id=$id;";
            read.Parameters.AddWithValue("$owner", owner);
            read.Parameters.AddWithValue("$id", taskId);
            string? previous = null;
            long revision = 0;
            using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    previous = reader.GetString(0);
                    revision = reader.GetInt64(1);
                }
            }
            var before = previous is null ? null : Deserialize(previous);
            if (before is not null && before.ContextId != task.ContextId)
            {
                throw new InvalidOperationException("A task's context identity cannot change.");
            }
            if (before is not null && (IsTerminal(before.Status.State)
                || before.Status.Timestamp > task.Status.Timestamp))
            {
                if (previous != sanitized)
                {
                    throw new InvalidOperationException("Cannot overwrite a terminal task or apply a stale task snapshot.");
                }
            }
            if (previous != sanitized)
            {
                await WriteAsync(connection, transaction, owner, Deserialize(sanitized), sanitized, revision + 1,
                    before is null || before.Status.State != task.Status.State, null, cancellationToken);
            }
            transaction.Commit();
            // Pending sign-in links remain caller-visible only in process memory, never in the ledger.
            if (!IsTerminal(task.Status.State))
            {
                _live[taskId] = Deserialize(liveSnapshot);
            }
            else
            {
                _live.TryRemove(taskId, out _);
            }
            return true;
        }, cancellationToken);
    }

    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string owner, AgentTask task, string snapshot, long revision, bool transition, string? reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks(owner,id,context_id,snapshot,state,status_ticks,created_utc,updated_utc,completed_utc,revision)
            VALUES($owner,$id,$context,$snapshot,$state,$ticks,$now,$now,$completed,$revision)
            ON CONFLICT(owner,id) DO UPDATE SET snapshot=$snapshot,state=$state,status_ticks=$ticks,
                updated_utc=$now,completed_utc=$completed,revision=$revision;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$context", task.ContextId);
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$state", task.Status.State.ToString());
        command.Parameters.AddWithValue("$ticks", (object?)task.Status.Timestamp?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$completed", IsTerminal(task.Status.State) ? now : DBNull.Value);
        command.Parameters.AddWithValue("$revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (transition)
        {
            command.CommandText = """
                INSERT INTO task_transitions(owner,task_id,revision,state,observed_utc,message,reason)
                VALUES($owner,$id,$revision,$state,$now,$message,$reason);
                """;
            command.Parameters.AddWithValue("$message", task.Status.Message is null ? DBNull.Value
                : JsonSerializer.Serialize(task.Status.Message, A2AJsonUtilities.DefaultOptions));
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tasks WHERE owner=$owner AND id=$id;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$id", taskId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _live.TryRemove(taskId, out _);
            return true;
        }, cancellationToken);
    }

    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PageSize is <= 0 or > 100 || request.HistoryLength is < 0)
        {
            throw new A2AException("Invalid page size or history length.", A2AErrorCode.InvalidParams);
        }
        var offset = 0;
        if (!string.IsNullOrEmpty(request.PageToken)
            && (!int.TryParse(request.PageToken, out offset) || offset < 0))
        {
            throw new A2AException("Invalid pageToken.", A2AErrorCode.InvalidParams);
        }
        return await RunAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var filter = "owner=$owner";
            command.Parameters.AddWithValue("$owner", owner);
            if (!string.IsNullOrEmpty(request.ContextId))
            {
                filter += " AND context_id=$context";
                command.Parameters.AddWithValue("$context", request.ContextId);
            }
            if (request.Status is { } state)
            {
                filter += " AND state=$state";
                command.Parameters.AddWithValue("$state", state.ToString());
            }
            if (request.StatusTimestampAfter is { } after)
            {
                filter += " AND status_ticks>$after";
                command.Parameters.AddWithValue("$after", after.UtcTicks);
            }
            command.CommandText = $"SELECT count(*) FROM tasks WHERE {filter};";
            var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            command.CommandText = $"SELECT id,snapshot FROM tasks WHERE {filter} ORDER BY status_ticks DESC,id LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$limit", request.PageSize ?? 50);
            command.Parameters.AddWithValue("$offset", offset);
            var tasks = new List<AgentTask>();
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var task = _live.TryGetValue(reader.GetString(0), out var live)
                        ? Deserialize(Serialize(live)) : Deserialize(reader.GetString(1));
                    if (request.HistoryLength is { } length)
                    {
                        task.History = length == 0 ? null : task.History?.TakeLast(length).ToList();
                    }
                    if (request.IncludeArtifacts is not true)
                    {
                        task.Artifacts = null;
                    }
                    tasks.Add(task);
                }
            }
            transaction.Commit();
            return new ListTasksResponse
            {
                Tasks = tasks, PageSize = tasks.Count, TotalSize = total,
                NextPageToken = offset + tasks.Count < total
                    ? (offset + tasks.Count).ToString(CultureInfo.InvariantCulture) : ""
            };
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureAvailable();
            using var connection = Open(databasePath);
            return await action(connection);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or JsonException)
        {
            _health.Fail();
            throw new InvalidOperationException("Task history storage failed. No durable success can be acknowledged.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsTerminal(TaskState state) =>
        state == TaskState.Completed || state == TaskState.Failed || state == TaskState.Canceled || state == TaskState.Rejected;
    private static string Serialize(AgentTask task) => JsonSerializer.Serialize(task, A2AJsonUtilities.DefaultOptions);
    private static AgentTask Deserialize(string snapshot) =>
        JsonSerializer.Deserialize<AgentTask>(snapshot, A2AJsonUtilities.DefaultOptions)
        ?? throw new JsonException("Task history snapshot is null.");

    private static string Sanitize(AgentTask task)
    {
        var root = JsonNode.Parse(Serialize(task))!;
        var links = new HashSet<string>(StringComparer.Ordinal);
        void Collect(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    if (pair.Key.Equals("authorizationUrl", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue value
                        && value.TryGetValue<string>(out var link))
                    {
                        links.Add(link);
                    }
                    Collect(pair.Value);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array) Collect(child);
            }
        }
        Collect(root);
        static bool IsAuthentication(JsonObject? data) =>
            data?["kind"] is JsonValue kind && kind.TryGetValue<string>(out var text) && text == "local_mcp_oauth";
        void Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["parts"] is JsonArray parts && parts.OfType<JsonObject>().Any(part =>
                    IsAuthentication(part["data"] as JsonObject)))
                {
                    var server = parts.OfType<JsonObject>().Select(part => part["data"] as JsonObject)
                        .First(IsAuthentication)!["serverName"]?.DeepClone();
                    obj["parts"] = new JsonArray(
                        new JsonObject { ["text"] = "Local MCP sign-in was required; temporary authorization details were not archived." },
                        new JsonObject { ["data"] = new JsonObject { ["kind"] = "local_mcp_oauth", ["serverName"] = server } });
                }
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key.Equals("authorizationUrl", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("accessToken", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("refreshToken", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("clientSecret", StringComparison.OrdinalIgnoreCase)
                        || pair.Key.Equals("authorizationCode", StringComparison.OrdinalIgnoreCase))
                    {
                        obj.Remove(pair.Key);
                    }
                    else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        foreach (var link in links) text = text.Replace(link, "[sign-in link omitted]", StringComparison.Ordinal);
                        obj[pair.Key] = text;
                    }
                    else Clean(pair.Value);
                }
            }
            else if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        foreach (var link in links) text = text.Replace(link, "[sign-in link omitted]", StringComparison.Ordinal);
                        array[i] = text;
                    }
                    else Clean(array[i]);
                }
            }
        }
        Clean(root);
        return root.ToJsonString();
    }
}
