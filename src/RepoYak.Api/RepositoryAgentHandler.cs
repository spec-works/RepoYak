using A2A;
using System.Collections.Concurrent;
using System.Text.Json;

namespace RepoYak.Api;

public sealed class RepositoryAgentHandler(IAgentRuntime agent, ILogger<RepositoryAgentHandler> logger) : IAgentHandler
{
    private readonly ConcurrentDictionary<string, PendingInput> _pendingInputs = new();
    private readonly ConcurrentDictionary<string, PendingAuthentication> _pendingAuthentications = new();

    public bool IsAwaitingAuthentication(AgentTask task) =>
        task.Status.State == TaskState.AuthRequired
        && _pendingAuthentications.TryGetValue(task.Id, out var pending)
        && !pending.Request.Completion.IsCompleted
        && task.Status.Message?.Parts.Any(part => part.Data is { } data
            && data.TryGetProperty("requestId", out var id) && id.GetString() == pending.Id) == true;

    public bool IsAwaitingInput(AgentTask task) =>
        task.Status.State == TaskState.InputRequired
        && _pendingInputs.TryGetValue(task.Id, out var pending)
        && !pending.Response.Task.IsCompleted
        && task.Status.Message?.Parts.Any(part => part.Data is { } data
            && data.TryGetProperty("requestId", out var id) && id.GetString() == pending.Id) == true;

    public void ValidateInputReply(Message message)
    {
        if (message.TaskId is not { } taskId || !_pendingInputs.TryGetValue(taskId, out var pending))
        {
            throw new A2AException("This task is not awaiting user input.", A2AErrorCode.InvalidParams);
        }
        if (pending.Response.Task.IsCompleted)
        {
            throw new A2AException("This input request has already been answered.", A2AErrorCode.InvalidParams);
        }
        _ = ParseInputReply(pending, message);
    }

    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        if (context.IsContinuation)
        {
            if (!_pendingInputs.TryGetValue(context.TaskId, out var pending)
                || !pending.Response.TrySetResult(ParseInputReply(pending, context.Message)))
            {
                throw new A2AException("This input request has already been answered or canceled.", A2AErrorCode.InvalidParams);
            }
            await eventQueue.EnqueueMessageAsync(InputMessage(context, [Part.FromText("Input accepted.")]), cancellationToken);
            return;
        }

        var userText = context.UserText;
        if (string.IsNullOrWhiteSpace(userText))
        {
            await new MessageResponder(eventQueue, context.ContextId)
                .ReplyAsync("Send a text question or request for this agent.", cancellationToken: cancellationToken);
            return;
        }

        if (userText.Trim().Equals("/new-session", StringComparison.OrdinalIgnoreCase))
        {
            if (!_pendingInputs.IsEmpty || !_pendingAuthentications.IsEmpty)
            {
                throw new A2AException("Complete or cancel pending input or sign-in before starting a new session.", A2AErrorCode.InvalidParams);
            }
            await agent.StartNewSessionAsync(cancellationToken);
            await new MessageResponder(eventQueue, context.ContextId)
                .ReplyAsync("Started a new agent session. Previous task records are unchanged.", cancellationToken: cancellationToken);
            return;
        }

        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        if (!context.IsContinuation)
        {
            await updater.SubmitAsync(cancellationToken: cancellationToken);
        }
        await updater.StartWorkAsync(cancellationToken: cancellationToken);
        using var inputGate = new SemaphoreSlim(1, 1);
        using var eventGate = new SemaphoreSlim(1, 1);
        string answer;
        try
        {
            answer = await agent.AskAsync(context.TaskId, userText, cancellationToken,
                async (text, token) =>
                {
                    await eventGate.WaitAsync(token);
                    try
                    {
                        if (!_pendingInputs.ContainsKey(context.TaskId) && !_pendingAuthentications.ContainsKey(context.TaskId))
                        {
                            await updater.StartWorkAsync(InputMessage(context, [Part.FromText(text)]), cancellationToken: token);
                        }
                    }
                    finally
                    {
                        eventGate.Release();
                    }
                },
                async (request, token) =>
                {
                    await inputGate.WaitAsync(token);
                    var pending = new PendingInput(Guid.NewGuid().ToString("N"), request);
                    try
                    {
                        await eventGate.WaitAsync(token);
                        try
                        {
                            _pendingInputs[context.TaskId] = pending;
                            // Keep the producer alive so cancellation and timeout still update the task.
                            await eventQueue.EnqueueStatusUpdateAsync(new TaskStatusUpdateEvent
                            {
                                TaskId = context.TaskId,
                                ContextId = context.ContextId,
                                Status = new A2A.TaskStatus
                                {
                                    State = TaskState.InputRequired,
                                    Timestamp = DateTimeOffset.UtcNow,
                                    Message = InputMessage(context,
                                    [
                                        Part.FromText(request.Question),
                                        Part.FromData(JsonSerializer.SerializeToElement(new
                                        {
                                            requestId = pending.Id,
                                            kind = request.Kind,
                                            question = request.Question,
                                            choices = request.Choices,
                                            allowFreeform = request.AllowFreeform,
                                            requestedSchema = request.RequestedSchema,
                                            url = request.Url
                                        }))
                                    ])
                                }
                            }, token);
                        }
                        finally
                        {
                            eventGate.Release();
                        }
                        var response = await pending.Response.Task.WaitAsync(token);
                        await eventGate.WaitAsync(token);
                        try
                        {
                            _pendingInputs.TryRemove(context.TaskId, out _);
                            await updater.StartWorkAsync(cancellationToken: token);
                        }
                        finally
                        {
                            eventGate.Release();
                        }
                        return response;
                    }
                    finally
                    {
                        _pendingInputs.TryRemove(context.TaskId, out _);
                        inputGate.Release();
                    }
                },
                async (request, token) =>
                {
                    await inputGate.WaitAsync(token);
                    var pending = new PendingAuthentication(Guid.NewGuid().ToString("N"), request);
                    try
                    {
                        await eventGate.WaitAsync(token);
                        try
                        {
                            _pendingAuthentications[context.TaskId] = pending;
                            await eventQueue.EnqueueStatusUpdateAsync(new TaskStatusUpdateEvent
                            {
                                TaskId = context.TaskId,
                                ContextId = context.ContextId,
                                Status = new A2A.TaskStatus
                                {
                                    State = TaskState.AuthRequired,
                                    Timestamp = DateTimeOffset.UtcNow,
                                    Message = InputMessage(context,
                                    [
                                        Part.FromText($"Sign in to MCP server '{request.ServerName}' using a browser on the RepoYak machine: "
                                            + request.AuthorizationUrl + "\nThe task resumes automatically after sign-in. Do not send tokens, codes, or callback URLs in chat."),
                                        Part.FromData(JsonSerializer.SerializeToElement(new
                                        {
                                            requestId = pending.Id,
                                            kind = "local_mcp_oauth",
                                            serverName = request.ServerName,
                                            authorizationUrl = request.AuthorizationUrl
                                        }))
                                    ])
                                }
                            }, token);
                        }
                        finally
                        {
                            eventGate.Release();
                        }
                        await request.Completion.WaitAsync(token);
                        await eventGate.WaitAsync(token);
                        try
                        {
                            _pendingAuthentications.TryRemove(context.TaskId, out _);
                            await updater.StartWorkAsync(cancellationToken: token);
                        }
                        finally
                        {
                            eventGate.Release();
                        }
                    }
                    finally
                    {
                        _pendingAuthentications.TryRemove(context.TaskId, out _);
                        inputGate.Release();
                    }
                });
        }
        catch (PersonaSelectionException exception)
        {
            await updater.FailAsync(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Role = Role.Agent,
                Parts = [Part.FromText(exception.Message)]
            }, cancellationToken: cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Agent execution failed for task {TaskId}", context.TaskId);
            await updater.FailAsync(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Role = Role.Agent,
                Parts = [Part.FromText("Agent execution failed. Check the host logs for details.")]
            }, cancellationToken: cancellationToken);
            return;
        }
        await updater.AddArtifactAsync(
            [Part.FromText(answer)], name: "Result", cancellationToken: cancellationToken);
        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }

    private static Message InputMessage(RequestContext context, List<Part> parts) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        TaskId = context.TaskId,
        ContextId = context.ContextId,
        Role = Role.Agent,
        Parts = parts
    };

    private sealed record PendingInput(string Id, AgentInputRequest Request)
    {
        public TaskCompletionSource<AgentInputResponse> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingAuthentication(string Id, AgentAuthenticationRequest Request);

    private static AgentInputResponse ParseInputReply(PendingInput pending, Message message)
    {
        var data = message.Parts.FirstOrDefault(part => part.Data is not null)?.Data;
        var text = message.Parts.FirstOrDefault(part => part.Text is not null)?.Text;
        if (data is null && pending.Request.Kind == "elicitation" && text?.TrimStart().StartsWith('{') == true)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new A2AException("The input response must contain valid JSON.", A2AErrorCode.InvalidParams);
            }
        }
        if (data is { } structured && structured.ValueKind != JsonValueKind.Object)
        {
            throw new A2AException("The input response data must be an object.", A2AErrorCode.InvalidParams);
        }
        if (data is { } identified && identified.TryGetProperty("requestId", out var requestId)
            && (requestId.ValueKind != JsonValueKind.String || requestId.GetString() != pending.Id))
        {
            throw new A2AException("The reply does not match the pending input request.", A2AErrorCode.InvalidParams);
        }
        string? ReadString(string name) => data is { } value && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

        if (pending.Request.Kind == "user_input")
        {
            var answer = ReadString("answer") ?? text;
            if (string.IsNullOrWhiteSpace(answer)
                || (!pending.Request.AllowFreeform && pending.Request.Choices?.Contains(answer, StringComparer.Ordinal) != true))
            {
                throw new A2AException("Provide an answer matching the requested choices or freeform input.", A2AErrorCode.InvalidParams);
            }
            return new AgentInputResponse(Answer: answer);
        }

        var action = ReadString("action") ?? text?.Trim();
        if (action is not ("accept" or "decline" or "cancel"))
        {
            throw new A2AException("Elicitation replies require action accept, decline, or cancel.", A2AErrorCode.InvalidParams);
        }
        JsonElement? content = data is { } form && form.TryGetProperty("content", out var values) ? values.Clone() : null;
        if (action == "accept" && pending.Request.Url is null)
        {
            if (content is not { ValueKind: JsonValueKind.Object })
            {
                throw new A2AException("An accepted form requires a content object.", A2AErrorCode.InvalidParams);
            }
            if (pending.Request.RequestedSchema is { } schema && schema.TryGetProperty("required", out var required))
            {
                foreach (var field in required.EnumerateArray())
                {
                    if (!content.Value.TryGetProperty(field.GetString()!, out _))
                    {
                        throw new A2AException($"The form is missing required field '{field.GetString()}'.", A2AErrorCode.InvalidParams);
                    }
                }
            }
        }
        if (content is { } submitted && submitted.ValueKind != JsonValueKind.Object)
        {
            throw new A2AException("Form content must be an object.", A2AErrorCode.InvalidParams);
        }
        return new AgentInputResponse(Action: action, Content: action == "accept" ? content : null);
    }
}
