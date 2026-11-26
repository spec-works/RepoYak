using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Json;
using System.Text.Json;

namespace RepoYak.Api.Tests;

public sealed class AgentAuthenticationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignInReturnsAnAuthBoundaryAndAutomaticallyResumesTheSameTask(bool streaming)
    {
        var runtime = new AuthenticationRuntime();
        await using var server = CreateServer(runtime);
        var task = await StartAsync(server, streaming);
        await runtime.ProgressSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        task = await server.GetTaskAsync(new GetTaskRequest { Id = task.Id });
        Assert.Equal(TaskState.AuthRequired, task.Status.State);
        var data = task.Status.Message!.Parts[1].Data!.Value;
        Assert.Equal("local_mcp_oauth", data.GetProperty("kind").GetString());
        Assert.Equal("test-mcp", data.GetProperty("serverName").GetString());
        Assert.Equal("https://example.invalid/authorize?state=opaque", data.GetProperty("authorizationUrl").GetString());
        Assert.Equal(4, data.EnumerateObject().Count());
        Assert.Contains("Do not send tokens", task.Status.Message.Parts[0].Text);

        await Assert.ThrowsAsync<A2AException>(() => server.SendMessageAsync(Request("Signed in.", task.Id)));
        await Assert.ThrowsAsync<A2AException>(() => server.SendMessageAsync(Request("/new-session")));
        Assert.Equal(TaskState.AuthRequired, (await server.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
        Assert.Equal(0, runtime.Rotations);
        runtime.Connections[0].TrySetResult();
        var completed = await WaitForStateAsync(server, task.Id, TaskState.Completed);
        Assert.Equal(1, runtime.Turns);
        Assert.Single(completed.Artifacts!);
    }

    [Fact]
    public async Task HttpClientGetsASignInLinkAndCanPollWithoutSendingCredentials()
    {
        var runtime = new AuthenticationRuntime();
        await using var server = CreateServer(runtime);
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapHttpA2A(server, "/agent");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.PostAsJsonAsync("/agent/message:send", new
            {
                message = new { messageId = "start", role = "ROLE_USER", parts = new[] { new { text = "Begin." } } }
            });
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var task = body.RootElement.GetProperty("task");
            Assert.Equal("TASK_STATE_AUTH_REQUIRED", task.GetProperty("status").GetProperty("state").GetString());
            var id = task.GetProperty("id").GetString()!;
            runtime.Connections[0].TrySetResult();
            await WaitForStateAsync(server, id, TaskState.Completed);
            using var completed = JsonDocument.Parse(await http.GetStringAsync("/agent/tasks/" + id));
            Assert.Equal("TASK_STATE_COMPLETED", completed.RootElement.GetProperty("status").GetProperty("state").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ReturnImmediatelyAndSubscriptionsRespectRepeatedAuthBoundaries()
    {
        var runtime = new AuthenticationRuntime(2);
        await using var server = CreateServer(runtime);
        var request = Request("Begin.");
        request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
        var task = (await server.SendMessageAsync(request)).Task!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in server.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = task.Id }, timeout.Token)) { }
        task = await WaitForStateAsync(server, task.Id, TaskState.AuthRequired);
        var firstRequest = task.Status.Message!.Parts[1].Data!.Value.GetProperty("requestId").GetString();
        runtime.Connections[0].TrySetResult();
        do
        {
            await Task.Delay(10, timeout.Token);
            task = await server.GetTaskAsync(new GetTaskRequest { Id = task.Id }, timeout.Token);
        } while (task.Status.State != TaskState.AuthRequired
            || task.Status.Message!.Parts[1].Data!.Value.GetProperty("requestId").GetString() == firstRequest);
        await foreach (var _ in server.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = task.Id }, timeout.Token)) { }
        runtime.Connections[1].TrySetResult();
        await WaitForStateAsync(server, task.Id, TaskState.Completed);
        Assert.Equal(1, runtime.Turns);
    }

    [Fact]
    public async Task CancelAndShutdownReleasePendingSignInWithoutCompletingIt()
    {
        var runtime = new AuthenticationRuntime();
        await using (var server = CreateServer(runtime))
        {
            var task = await StartAsync(server, false);
            var canceled = await server.CancelTaskAsync(new CancelTaskRequest { Id = task.Id });
            Assert.Equal(TaskState.Canceled, canceled.Status.State);
            await runtime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.Connections[0].TrySetResult();
            Assert.Equal(TaskState.Canceled, (await server.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
        }
        var stoppedRuntime = new AuthenticationRuntime();
        var stopped = CreateServer(stoppedRuntime);
        await StartAsync(stopped, false);
        await stopped.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await stoppedRuntime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailureDoesNotPublishPrivateProviderDiagnostics()
    {
        var runtime = new AuthenticationRuntime();
        await using var server = CreateServer(runtime);
        var task = await StartAsync(server, false);
        runtime.Connections[0].TrySetException(new TimeoutException("private-provider-diagnostic"));
        var failed = await WaitForStateAsync(server, task.Id, TaskState.Failed);
        Assert.DoesNotContain("private-provider-diagnostic", JsonSerializer.Serialize(failed));
        Assert.Equal(1, runtime.Turns);
    }

    [Fact]
    public async Task OneAgentsSignInCannotResumeAnotherAgent()
    {
        var first = new AuthenticationRuntime();
        var second = new AuthenticationRuntime();
        await using var firstServer = CreateServer(first);
        await using var secondServer = CreateServer(second);
        var firstTask = await StartAsync(firstServer, false);
        var secondTask = await StartAsync(secondServer, false);
        first.Connections[0].TrySetResult();
        await WaitForStateAsync(firstServer, firstTask.Id, TaskState.Completed);
        Assert.Equal(TaskState.AuthRequired, (await secondServer.GetTaskAsync(new GetTaskRequest { Id = secondTask.Id })).Status.State);
        second.Connections[0].TrySetResult();
        await WaitForStateAsync(secondServer, secondTask.Id, TaskState.Completed);
    }

    [Fact]
    public async Task AnOrdinaryQuestionWaitsUntilSignInFinishes()
    {
        var runtime = new AuthenticationRuntime { AskQuestionDuringSignIn = true };
        await using var server = CreateServer(runtime);
        var task = await StartAsync(server, false);
        await runtime.ProgressSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskState.AuthRequired, (await server.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
        runtime.Connections[0].TrySetResult();
        await WaitForStateAsync(server, task.Id, TaskState.InputRequired);
        var completed = await server.SendMessageAsync(Request("The answer.", task.Id));
        Assert.Equal(TaskState.Completed, completed.Task!.Status.State);
        Assert.Equal(1, runtime.Turns);
    }

    private static InputAwareA2AServer CreateServer(IAgentRuntime runtime) => new(
        new RepositoryAgentHandler(runtime, NullLogger<RepositoryAgentHandler>.Instance),
        new InMemoryTaskStore(), new ChannelEventNotifier(), NullLogger<A2AServer>.Instance,
        new A2AServerOptions { SupportsStreaming = true });

    private static SendMessageRequest Request(string text, string? taskId = null) => new()
    {
        Message = new Message
        {
            MessageId = Guid.NewGuid().ToString("N"), TaskId = taskId, Role = Role.User, Parts = [Part.FromText(text)]
        }
    };

    private static async Task<AgentTask> StartAsync(InputAwareA2AServer server, bool streaming)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!streaming)
        {
            return (await server.SendMessageAsync(Request("Begin."), timeout.Token)).Task!;
        }
        string? taskId = null;
        await foreach (var update in server.SendStreamingMessageAsync(Request("Begin."), timeout.Token))
        {
            taskId ??= update.Task?.Id ?? update.StatusUpdate?.TaskId;
        }
        return await server.GetTaskAsync(new GetTaskRequest { Id = taskId! }, timeout.Token);
    }

    private static async Task<AgentTask> WaitForStateAsync(InputAwareA2AServer server, string taskId, TaskState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var task = await server.GetTaskAsync(new GetTaskRequest { Id = taskId }, timeout.Token);
            if (task.Status.State == expected)
            {
                return task;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class AuthenticationRuntime(int requests = 1) : IAgentRuntime
    {
        public TaskCompletionSource[] Connections { get; } = Enumerable.Range(0, requests)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        public TaskCompletionSource ProgressSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Turns { get; private set; }
        public int Rotations { get; private set; }
        public bool AskQuestionDuringSignIn { get; init; }

        public async Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task>? reportProgress = null,
            Func<AgentInputRequest, CancellationToken, Task<AgentInputResponse>>? requestInput = null,
            Func<AgentAuthenticationRequest, CancellationToken, Task>? requestAuthentication = null)
        {
            Turns++;
            try
            {
                Task<AgentInputResponse>? questionResponse = null;
                foreach (var connection in Connections)
                {
                    var waiting = requestAuthentication!(new AgentAuthenticationRequest(
                        "test-mcp", "https://example.invalid/authorize?state=opaque", connection.Task), cancellationToken);
                    if (AskQuestionDuringSignIn)
                    {
                        questionResponse = requestInput!(new AgentInputRequest("user_input", "A later question."), cancellationToken);
                    }
                    await reportProgress!("Late progress must not hide sign-in.", cancellationToken);
                    ProgressSent.TrySetResult();
                    await waiting;
                }
                if (questionResponse is not null)
                {
                    await questionResponse;
                }
                return "Finished the original request after sign-in.";
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }

        public Task StartNewSessionAsync(CancellationToken cancellationToken)
        {
            Rotations++;
            return Task.CompletedTask;
        }
    }
}
