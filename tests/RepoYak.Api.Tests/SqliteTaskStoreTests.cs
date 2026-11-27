using System.Text.Json;
using A2A;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoYak.Api.Tests;

public sealed class SqliteTaskStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RepoYakHistory-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "history.db");
    private const string Owner = "owner-one";

    public async Task InitializeAsync()
    {
        await SqliteTaskStore.InitializeAsync(Database, CancellationToken.None);
        await RegisterAsync(Owner);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private Task RegisterAsync(string owner) => SqliteTaskStore.RegisterOwnerAsync(Database, owner, "host",
        new AgentRegistration(owner, _root), CancellationToken.None);

    private static AgentTask TaskSnapshot(string id = "task", TaskState? state = null, int minute = 0) => new()
    {
        Id = id, ContextId = "context",
        Status = new A2A.TaskStatus
        {
            State = state ?? TaskState.Working,
            Timestamp = new DateTimeOffset(2026, 11, 27, 12, minute, 0, TimeSpan.Zero)
        },
        History = [new Message
        {
            MessageId = "request", Role = Role.User, Parts = [Part.FromText("Do the work.")],
            Metadata = new() { ["source"] = JsonSerializer.SerializeToElement("test") }
        }],
        Artifacts = [new Artifact { ArtifactId = "result", Parts = [Part.FromText("partial result")] }],
        Metadata = new() { ["label"] = JsonSerializer.SerializeToElement("retained") }
    };

    private async Task<long> ScalarAsync(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SnapshotsAndTransitionsSurviveReopenAndDuplicateSavesAreIdempotent()
    {
        var store = new SqliteTaskStore(Database, Owner);
        var task = TaskSnapshot();
        await store.SaveTaskAsync(task.Id, task);
        await store.SaveTaskAsync(task.Id, task);
        task.Artifacts![0].Parts = [Part.FromText("complete result")];
        await store.SaveTaskAsync(task.Id, task);
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM task_transitions"));
        task.Status.State = TaskState.Completed;
        await store.SaveTaskAsync(task.Id, task);
        await SqliteTaskStore.InitializeAsync(Database, CancellationToken.None);
        var loaded = await new SqliteTaskStore(Database, Owner).GetTaskAsync(task.Id);
        Assert.Equal(TaskState.Completed, loaded!.Status.State);
        Assert.Equal("Do the work.", loaded.History![0].Parts[0].Text);
        Assert.Equal("complete result", loaded.Artifacts![0].Parts[0].Text);
        Assert.Equal("retained", loaded.Metadata!["label"].GetString());
        Assert.Equal(2, await ScalarAsync("SELECT count(*) FROM task_transitions"));
        loaded.History.Clear();
        Assert.Single((await store.GetTaskAsync(task.Id))!.History!);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("working")]
    [InlineData("input")]
    [InlineData("auth")]
    public async Task RestartFailsInterruptedWorkWithoutReplayingOrDiscardingPartialResults(string state)
    {
        var task = TaskSnapshot(state: state switch
        {
            "submitted" => TaskState.Submitted, "input" => TaskState.InputRequired,
            "auth" => TaskState.AuthRequired, _ => TaskState.Working
        });
        await new SqliteTaskStore(Database, Owner).SaveTaskAsync(task.Id, task);
        await SqliteTaskStore.InitializeAsync(Database, CancellationToken.None);
        var loaded = await new SqliteTaskStore(Database, Owner).GetTaskAsync(task.Id);
        Assert.Equal(TaskState.Failed, loaded!.Status.State);
        Assert.Contains("unknown", loaded.Status.Message!.Parts[0].Text);
        Assert.Single(loaded.History!);
        Assert.Single(loaded.Artifacts!);
        await SqliteTaskStore.InitializeAsync(Database, CancellationToken.None);
        Assert.Equal(2, await ScalarAsync("SELECT count(*) FROM task_transitions"));
    }

    [Fact]
    public async Task TerminalAndStaleWritesCannotUndoResultsAndDeletionRemovesTransitions()
    {
        var store = new SqliteTaskStore(Database, Owner);
        await store.SaveTaskAsync("task", TaskSnapshot(minute: 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTaskAsync("task", TaskSnapshot(minute: 1)));
        var canceled = TaskSnapshot(state: TaskState.Canceled, minute: 3);
        await store.SaveTaskAsync("task", canceled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTaskAsync("task",
            TaskSnapshot(state: TaskState.Completed, minute: 4)));
        Assert.Equal(TaskState.Canceled, (await store.GetTaskAsync("task"))!.Status.State);
        await store.DeleteTaskAsync("task");
        Assert.Null(await store.GetTaskAsync("task"));
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM task_transitions"));
    }

    [Fact]
    public async Task OwnerIsolationAndListFiltersMatchThePinnedMemoryStore()
    {
        var store = new SqliteTaskStore(Database, Owner);
        var baseline = new InMemoryTaskStore();
        for (var i = 0; i < 4; i++)
        {
            var task = TaskSnapshot($"task-{i}", i % 2 == 0 ? TaskState.Completed : TaskState.Working, i);
            if (i == 3) task.ContextId = "other";
            await store.SaveTaskAsync(task.Id, task);
            await baseline.SaveTaskAsync(task.Id, task);
        }
        await RegisterAsync("other");
        var other = new SqliteTaskStore(Database, "other");
        await other.SaveTaskAsync("task-0", TaskSnapshot("task-0", TaskState.Rejected));
        Assert.Equal(TaskState.Completed, (await store.GetTaskAsync("task-0"))!.Status.State);
        foreach (var request in new[]
        {
            new ListTasksRequest { PageSize = 2, IncludeArtifacts = true },
            new ListTasksRequest { PageSize = 2, PageToken = "2", HistoryLength = 0 },
            new ListTasksRequest { ContextId = "context", Status = TaskState.Completed, HistoryLength = 1 },
            new ListTasksRequest { StatusTimestampAfter = TaskSnapshot(minute: 1).Status.Timestamp },
            new ListTasksRequest { PageToken = "99" }
        })
        {
            var actual = await store.ListTasksAsync(request);
            var expected = await baseline.ListTasksAsync(request);
            Assert.Equal(JsonSerializer.Serialize(expected, A2AJsonUtilities.DefaultOptions),
                JsonSerializer.Serialize(actual, A2AJsonUtilities.DefaultOptions));
        }
        await Assert.ThrowsAsync<A2AException>(() => store.ListTasksAsync(new ListTasksRequest { PageToken = "-1" }));
    }

    [Fact]
    public async Task AuthorizationDetailsAreLiveOnlyAndNeverWrittenToSnapshotsOrTransitions()
    {
        const string url = "https://provider.example/authorize?state=private-state";
        var task = TaskSnapshot(state: TaskState.AuthRequired);
        task.Status.Message = new Message
        {
            MessageId = "auth", Role = Role.Agent,
            Parts =
            [
                Part.FromText("Sign in here: " + url),
                Part.FromData(JsonSerializer.SerializeToElement(new
                {
                    kind = "local_mcp_oauth", serverName = "calendar", authorizationUrl = url,
                    accessToken = "private-token", authorizationCode = "private-code"
                }))
            ]
        };
        task.History!.Add(task.Status.Message);
        var store = new SqliteTaskStore(Database, Owner);
        await store.SaveTaskAsync(task.Id, task);
        Assert.Contains(url, (await store.GetTaskAsync(task.Id))!.Status.Message!.Parts[0].Text);
        using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot FROM tasks UNION ALL SELECT message FROM task_transitions WHERE message IS NOT NULL";
        using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var stored = reader.GetString(0);
                Assert.DoesNotContain(url, stored);
                Assert.DoesNotContain("private-state", stored);
                Assert.DoesNotContain("private-token", stored);
                Assert.DoesNotContain("private-code", stored);
                Assert.Contains("calendar", stored);
            }
        }
        task.Status = new A2A.TaskStatus { State = TaskState.Completed, Timestamp = DateTimeOffset.UtcNow };
        await store.SaveTaskAsync(task.Id, task);
        Assert.DoesNotContain(url, JsonSerializer.Serialize(await store.GetTaskAsync(task.Id), A2AJsonUtilities.DefaultOptions));
    }

    [Fact]
    public async Task CorruptStorageBlocksAcknowledgementAndFurtherAdmissionAcrossOwners()
    {
        var health = new TaskHistoryHealth();
        var store = new SqliteTaskStore(Database, Owner, health);
        File.WriteAllText(Database, "not a database");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTaskAsync("task", TaskSnapshot()));
        Assert.Throws<InvalidOperationException>(() => new SqliteTaskStore(Database, "other", health).EnsureAvailable());
        var runtime = new CountingRuntime();
        await using var server = new InputAwareA2AServer(new RepositoryAgentHandler(runtime,
            NullLogger<RepositoryAgentHandler>.Instance), store, new ChannelEventNotifier(),
            NullLogger<A2AServer>.Instance, new A2AServerOptions { SupportsStreaming = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendMessageAsync(new SendMessageRequest
        {
            Message = new Message { MessageId = "new", Role = Role.User, Parts = [Part.FromText("work")] }
        }));
        Assert.Equal(0, runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCompletionWriteNeverPublishesDurableSuccess(bool streaming)
    {
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER fail_completion BEFORE UPDATE ON tasks
                WHEN NEW.state='{TaskState.Completed}'
                BEGIN SELECT RAISE(ABORT,'simulated disk write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteTaskStore(Database, Owner);
        var runtime = new CountingRuntime();
        await using var server = new InputAwareA2AServer(new RepositoryAgentHandler(runtime,
            NullLogger<RepositoryAgentHandler>.Instance, recordRequests: true), store,
            new ChannelEventNotifier(), NullLogger<A2AServer>.Instance,
            new A2AServerOptions { SupportsStreaming = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new SendMessageRequest
        {
            Message = new Message { MessageId = "request", Role = Role.User, Parts = [Part.FromText("work")] }
        };
        var completed = false;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (streaming)
            {
                await foreach (var update in server.SendStreamingMessageAsync(request, timeout.Token))
                {
                    completed |= update.StatusUpdate?.Status.State == TaskState.Completed
                        || update.Task?.Status.State == TaskState.Completed;
                }
            }
            else
            {
                var response = await server.SendMessageAsync(request, timeout.Token);
                completed = response.Task?.Status.State == TaskState.Completed;
            }
        });
        Assert.False(completed);
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(1, runtime.Calls);
        Assert.Throws<InvalidOperationException>(store.EnsureAvailable);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM tasks WHERE completed_utc IS NOT NULL"));
    }

    [Fact]
    public async Task InitializationRejectsCorruptAndNewerSchemasWithoutReplacingThem()
    {
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteTaskStore.InitializeAsync(Database, CancellationToken.None));
        Assert.Equal(99, await ScalarAsync("PRAGMA user_version"));
        File.WriteAllText(Database, "corrupt history");
        await Assert.ThrowsAsync<SqliteException>(() => SqliteTaskStore.InitializeAsync(Database, CancellationToken.None));
        Assert.Equal("corrupt history", File.ReadAllText(Database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentCallbacksResumeTheOriginalTaskAndArchiveSanitizedHistory(bool authentication)
    {
        var store = new SqliteTaskStore(Database, Owner);
        var runtime = new CallbackRuntime(authentication);
        await using var server = new InputAwareA2AServer(new RepositoryAgentHandler(runtime,
            NullLogger<RepositoryAgentHandler>.Instance, recordRequests: true), store,
            new ChannelEventNotifier(), NullLogger<A2AServer>.Instance,
            new A2AServerOptions { SupportsStreaming = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = (await server.SendMessageAsync(new SendMessageRequest
        {
            Message = new Message { MessageId = "begin", Role = Role.User, Parts = [Part.FromText("Begin")] }
        }, timeout.Token)).Task!;
        Assert.Equal(authentication ? TaskState.AuthRequired : TaskState.InputRequired, task.Status.State);
        if (authentication)
        {
            Assert.Contains("private-state", task.Status.Message!.Parts[0].Text);
            runtime.SignedIn.TrySetResult();
        }
        else
        {
            await server.SendMessageAsync(new SendMessageRequest
            {
                Message = new Message
                {
                    MessageId = "answer", TaskId = task.Id, Role = Role.User, Parts = [Part.FromText("Yes")]
                }
            }, timeout.Token);
        }
        while ((await server.GetTaskAsync(new GetTaskRequest { Id = task.Id }, timeout.Token)).Status.State != TaskState.Completed)
        {
            await Task.Delay(10, timeout.Token);
        }
        var archived = (await new SqliteTaskStore(Database, Owner).GetTaskAsync(task.Id))!;
        Assert.Equal(1, runtime.Calls);
        Assert.Contains(archived.History!, message => message.Parts.Any(part => part.Text == "Begin"));
        if (authentication)
        {
            Assert.DoesNotContain("private-state", JsonSerializer.Serialize(archived, A2AJsonUtilities.DefaultOptions));
            Assert.Contains(archived.History!, message => message.Parts.Any(part =>
                part.Text?.Contains("temporary authorization details were not archived") == true));
        }
        else
        {
            Assert.Equal("Yes", runtime.Answer);
            Assert.Contains(archived.History!, message => message.Parts.Any(part => part.Text == "Yes"));
        }
    }

    private sealed class CallbackRuntime(bool authentication) : IAgentRuntime
    {
        public TaskCompletionSource SignedIn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string? Answer;
        public Task StartNewSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task>? reportProgress = null,
            Func<AgentInputRequest, CancellationToken, Task<AgentInputResponse>>? requestInput = null,
            Func<AgentAuthenticationRequest, CancellationToken, Task>? requestAuthentication = null)
        {
            Calls++;
            if (authentication)
            {
                await requestAuthentication!(new AgentAuthenticationRequest("calendar",
                    "https://provider.example/authorize?state=private-state", SignedIn.Task), cancellationToken);
            }
            else
            {
                Answer = (await requestInput!(new AgentInputRequest("user_input", "Continue?"), cancellationToken)).Answer;
            }
            return "done";
        }
    }

    private sealed class CountingRuntime : IAgentRuntime
    {
        public int Calls;
        public Task StartNewSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task>? reportProgress = null,
            Func<AgentInputRequest, CancellationToken, Task<AgentInputResponse>>? requestInput = null,
            Func<AgentAuthenticationRequest, CancellationToken, Task>? requestAuthentication = null)
        {
            Calls++;
            return Task.FromResult("done");
        }
    }
}
