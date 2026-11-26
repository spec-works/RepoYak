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

public sealed class AgentInputTests
{
    [Fact]
    public async Task HttpRepliesResumeTheSameTask()
    {
        var runtime = new InputRuntime(new AgentInputRequest("user_input", "Choose.", ["yes", "no"], AllowFreeform: false));
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
            using var start = await http.PostAsJsonAsync("/agent/message:send", new
            {
                message = new { messageId = "start", role = "ROLE_USER", parts = new[] { new { text = "Begin." } } }
            });
            start.EnsureSuccessStatusCode();
            using var initial = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
            var task = initial.RootElement.GetProperty("task");
            Assert.Equal("TASK_STATE_INPUT_REQUIRED", task.GetProperty("status").GetProperty("state").GetString());
            var taskId = task.GetProperty("id").GetString();
            var requestId = task.GetProperty("status").GetProperty("message").GetProperty("parts")[1]
                .GetProperty("data").GetProperty("requestId").GetString();
            using var reply = await http.PostAsJsonAsync("/agent/message:send", new
            {
                message = new
                {
                    messageId = "reply", role = "ROLE_USER", taskId,
                    parts = new[] { new { data = new { requestId, answer = "yes" } } }
                }
            });
            reply.EnsureSuccessStatusCode();
            using var completed = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.Equal(taskId, completed.RootElement.GetProperty("task").GetProperty("id").GetString());
            Assert.Equal("TASK_STATE_COMPLETED", completed.RootElement.GetProperty("task").GetProperty("status").GetProperty("state").GetString());
            Assert.Equal(1, runtime.Turns);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task ReturnImmediatelyCanBeFollowedThroughAnInputBoundary()
    {
        var runtime = new InputRuntime(new AgentInputRequest("user_input", "Waiting."));
        await using var server = CreateServer(runtime);
        var request = Request("Begin.");
        request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
        var task = Assert.IsType<AgentTask>((await server.SendMessageAsync(request)).Task);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in server.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = task.Id }, timeout.Token))
        {
        }
        Assert.Equal(TaskState.InputRequired, (await server.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
        Assert.Equal(TaskState.Completed, (await SendAsync(server, Request("An answer.", task.Id), false)).Status.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswersResumeTheOriginalTurnAndSupportRepeatedQuestions(bool streaming)
    {
        var runtime = new InputRuntime(
            new("user_input", "Choose a color.", ["red", "blue"], AllowFreeform: false),
            new("user_input", "Why that color?"));
        await using var server = CreateServer(runtime);
        var first = await SendAsync(server, Request("Help me choose."), streaming);
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        Assert.Equal("Choose a color.", first.Status.Message!.Parts[0].Text);
        Assert.Equal("red", first.Status.Message.Parts[1].Data!.Value.GetProperty("choices")[0].GetString());
        var originalRequestId = first.Status.Message.Parts[1].Data!.Value.GetProperty("requestId").GetString();

        var second = await SendAsync(server, Request("red", first.Id), streaming);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TaskState.InputRequired, second.Status.State);
        Assert.Equal("Why that color?", second.Status.Message!.Parts[0].Text);
        await Assert.ThrowsAsync<A2AException>(() => server.SendMessageAsync(DataRequest(first.Id,
            JsonSerializer.Serialize(new { requestId = originalRequestId, answer = "Stale reply." }))));

        var completed = await SendAsync(server, Request("It is easy to spot.", first.Id), streaming);
        Assert.Equal(first.Id, completed.Id);
        Assert.Equal(TaskState.Completed, completed.Status.State);
        Assert.Equal(1, runtime.Turns);
        Assert.Equal(new[] { "red", "It is easy to spot." }, runtime.Responses.Select(response => response.Answer));
        Assert.Single(completed.Artifacts!);
    }

    [Fact]
    public async Task InvalidOrStaleRepliesLeaveTheTaskWaiting()
    {
        var runtime = new InputRuntime(new AgentInputRequest("user_input", "Choose.", ["yes", "no"], AllowFreeform: false));
        await using var server = CreateServer(runtime);
        var task = await SendAsync(server, Request("Begin."), false);
        await Assert.ThrowsAsync<A2AException>(() => server.SendMessageAsync(Request("maybe", task.Id)));
        await Assert.ThrowsAsync<A2AException>(() => server.SendMessageAsync(DataRequest(task.Id,
            """{"requestId":"stale","answer":"yes"}""")));
        Assert.Equal(TaskState.InputRequired, (await server.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
        Assert.Empty(runtime.Responses);
        var completed = await SendAsync(server, Request("yes", task.Id), false);
        Assert.Equal(TaskState.Completed, completed.Status.State);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task ElicitationSupportsStructuredAcceptanceAndDeclineOrCancel(string action)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { project = new { type = "string" } },
            required = new[] { "project" }
        });
        var runtime = new InputRuntime(new AgentInputRequest("elicitation", "Select a project.", RequestedSchema: schema));
        await using var server = CreateServer(runtime);
        var task = await SendAsync(server, Request("Begin."), false);
        Assert.Equal("project", task.Status.Message!.Parts[1].Data!.Value
            .GetProperty("requestedSchema").GetProperty("required")[0].GetString());
        if (action == "accept")
        {
            await Assert.ThrowsAsync<A2AException>(() => server.SendMessageAsync(DataRequest(task.Id,
                """{"action":"accept","content":{}}""")));
        }
        var reply = action == "accept"
            ? DataRequest(task.Id, """{"action":"accept","content":{"project":"RepoYak"}}""")
            : Request(action, task.Id);
        var completed = await SendAsync(server, reply, false);
        Assert.Equal(TaskState.Completed, completed.Status.State);
        var response = Assert.Single(runtime.Responses);
        Assert.Equal(action, response.Action);
        if (action == "accept")
        {
            Assert.Equal("RepoYak", response.Content!.Value.GetProperty("project").GetString());
        }
        else
        {
            Assert.Null(response.Content);
        }
    }

    [Fact]
    public async Task UrlElicitationReturnsTheUrlWithoutOpeningIt()
    {
        var runtime = new InputRuntime(new AgentInputRequest("elicitation", "Complete browser interaction.", Url: "https://example.invalid/consent"));
        await using var server = CreateServer(runtime);
        var task = await SendAsync(server, Request("Begin."), false);
        Assert.Equal("https://example.invalid/consent", task.Status.Message!.Parts[1].Data!.Value.GetProperty("url").GetString());
        Assert.Equal(TaskState.Completed, (await SendAsync(server, Request("accept", task.Id), false)).Status.State);
    }

    [Fact]
    public async Task CancellationAndShutdownReleaseTheWaitingCallback()
    {
        var runtime = new InputRuntime(new AgentInputRequest("user_input", "Waiting."));
        await using (var server = CreateServer(runtime))
        {
            var task = await SendAsync(server, Request("Begin."), false);
            var canceled = await server.CancelTaskAsync(new CancelTaskRequest { Id = task.Id })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TaskState.Canceled, canceled.Status.State);
            await runtime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(runtime.Responses);
        }
        var stoppedRuntime = new InputRuntime(new AgentInputRequest("user_input", "Waiting."));
        var stopped = CreateServer(stoppedRuntime);
        await SendAsync(stopped, Request("Begin."), false);
        await ((IAsyncDisposable)stopped).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await stopped.DisposeAsync();
        await stoppedRuntime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelingASynchronousCallerAbortsItsActiveTurn()
    {
        var runtime = new InputRuntime { HoldForCancellation = true };
        await using var server = CreateServer(runtime);
        using var cancellation = new CancellationTokenSource();
        var pending = server.SendMessageAsync(Request("Begin."), cancellation.Token);
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await runtime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ATimeoutWhileWaitingStillFailsTheTask()
    {
        var runtime = new InputRuntime(new AgentInputRequest("user_input", "Waiting."));
        await using var server = CreateServer(runtime);
        var task = await SendAsync(server, Request("Begin."), false);
        runtime.Timeout.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await server.GetTaskAsync(new GetTaskRequest { Id = task.Id }, timeout.Token)).Status.State != TaskState.Failed)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task RepliesCannotAnswerAnotherAgentsTask()
    {
        var runtime = new InputRuntime(new AgentInputRequest("user_input", "Waiting."));
        await using var first = CreateServer(runtime);
        await using var second = CreateServer(new InputRuntime(new AgentInputRequest("user_input", "Other.")));
        var task = await SendAsync(first, Request("Begin."), false);
        await Assert.ThrowsAsync<A2AException>(() => second.SendMessageAsync(Request("Wrong agent.", task.Id)));
        Assert.Empty(runtime.Responses);
        Assert.Equal(TaskState.InputRequired, (await first.GetTaskAsync(new GetTaskRequest { Id = task.Id })).Status.State);
    }

    private static InputAwareA2AServer CreateServer(InputRuntime runtime) => new(
        new RepositoryAgentHandler(runtime, NullLogger<RepositoryAgentHandler>.Instance),
        new InMemoryTaskStore(), new ChannelEventNotifier(), NullLogger<A2AServer>.Instance,
        new A2AServerOptions { SupportsStreaming = true });

    private static SendMessageRequest Request(string text, string? taskId = null) => new()
    {
        Message = new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            Role = Role.User,
            Parts = [Part.FromText(text)]
        }
    };

    private static SendMessageRequest DataRequest(string taskId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var request = Request("", taskId);
        request.Message.Parts = [Part.FromData(document.RootElement.Clone())];
        return request;
    }

    private static async Task<AgentTask> SendAsync(InputAwareA2AServer server, SendMessageRequest request, bool streaming)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!streaming)
        {
            return Assert.IsType<AgentTask>((await server.SendMessageAsync(request, timeout.Token)).Task);
        }
        await foreach (var update in server.SendStreamingMessageAsync(request, timeout.Token))
        {
            if (update.Task?.Status.State == TaskState.InputRequired || update.StatusUpdate?.Status.State == TaskState.InputRequired)
            {
                break;
            }
        }
        return await server.GetTaskAsync(new GetTaskRequest { Id = request.Message.TaskId
            ?? (await server.ListTasksAsync(new ListTasksRequest())).Tasks.Single().Id }, timeout.Token);
    }

    private sealed class InputRuntime(params AgentInputRequest[] requests) : IAgentRuntime
    {
        public int Turns { get; private set; }
        public List<AgentInputResponse> Responses { get; } = [];
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Timeout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldForCancellation { get; init; }

        public async Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task>? reportProgress = null,
            Func<AgentInputRequest, CancellationToken, Task<AgentInputResponse>>? requestInput = null,
            Func<AgentAuthenticationRequest, CancellationToken, Task>? requestAuthentication = null)
        {
            Turns++;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Entered.TrySetResult();
                if (HoldForCancellation)
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, lifetime.Token);
                }
                foreach (var request in requests)
                {
                    var answer = requestInput!(request, lifetime.Token);
                    await reportProgress!("Waiting for the answer.", lifetime.Token);
                    if (await Task.WhenAny(answer, Timeout.Task) == Timeout.Task)
                    {
                        await lifetime.CancelAsync();
                        try { await answer; } catch (OperationCanceledException) { }
                        throw new TimeoutException("Private timeout diagnostic.");
                    }
                    Responses.Add(await answer);
                }
                return "Completed using the supplied answers.";
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }

        public Task StartNewSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
