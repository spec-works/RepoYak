using A2A;
using System.Runtime.CompilerServices;

namespace RepoYak.Api;

public sealed class InputAwareA2AServer(
    RepositoryAgentHandler handler,
    ITaskStore taskStore,
    ChannelEventNotifier notifier,
    ILogger<A2AServer> logger,
    A2AServerOptions options) : A2AServer(handler, taskStore, notifier, logger, options), IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public new async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            await _shutdown.CancelAsync();
            await base.DisposeAsync();
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    public override async Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        if (request.Message.TaskId is not null)
        {
            handler.ValidateInputReply(request.Message);
        }
        var response = await base.SendMessageAsync(CopyRequest(request,
            returnImmediately: request.Message.TaskId is null), cancellationToken);
        var taskId = response.Task?.Id ?? request.Message.TaskId;
        if (taskId is null)
        {
            return response;
        }
        if (request.Configuration?.ReturnImmediately == true)
        {
            return new SendMessageResponse
            {
                Task = await GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = request.Configuration.HistoryLength }, cancellationToken)
            };
        }
        try
        {
            await foreach (var update in ObserveTaskAsync(taskId, observation.Token))
            {
                if (update.Task is { } task && IsBoundary(task))
                {
                    return new SendMessageResponse
                    {
                        Task = await GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = request.Configuration?.HistoryLength }, observation.Token)
                    };
                }
                if (update.StatusUpdate is { } status && IsBoundary(status.Status.State))
                {
                    var current = await GetTaskAsync(new GetTaskRequest { Id = taskId, HistoryLength = request.Configuration?.HistoryLength }, observation.Token);
                    if (IsBoundary(current))
                    {
                        return new SendMessageResponse { Task = current };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            try
            {
                await CancelTaskAsync(new CancelTaskRequest { Id = taskId }, CancellationToken.None);
            }
            catch (A2AException exception) when (exception.ErrorCode == A2AErrorCode.TaskNotCancelable)
            {
            }
            throw;
        }
        throw new A2AException("Task observation ended without a response.", A2AErrorCode.InvalidAgentResponse);
    }

    public override async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(
        SendMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = observation.Token;
        if (request.Message.TaskId is { } taskId)
        {
            handler.ValidateInputReply(request.Message);
            await base.SendMessageAsync(CopyRequest(request, returnImmediately: false), cancellationToken);
            await foreach (var update in ObserveTaskAsync(taskId, cancellationToken))
            {
                yield return update;
                if (update.Task is { } task && IsBoundary(task)
                    || update.StatusUpdate is { } status && IsBoundary(status.Status.State))
                {
                    yield break;
                }
            }
        }
        else
        {
            await foreach (var update in base.SendStreamingMessageAsync(request, cancellationToken))
            {
                yield return update;
                if (update.StatusUpdate is { } status && IsBoundary(status.Status.State))
                {
                    yield break;
                }
            }
        }
    }

    public override async IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(
        SubscribeToTaskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await foreach (var update in ObserveTaskAsync(request.Id, observation.Token))
        {
            yield return update;
            if (update.Task is { } task && IsBoundary(task)
                || update.StatusUpdate is { } status && IsBoundary(status.Status.State))
            {
                yield break;
            }
        }
    }

    private async IAsyncEnumerable<StreamResponse> ObserveTaskAsync(
        string taskId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var current = await GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
        if (IsBoundary(current))
        {
            yield return new StreamResponse { Task = current };
            yield break;
        }
        await using var updates = base.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await updates.MoveNextAsync();
            }
            catch (A2AException exception) when (exception.ErrorCode == A2AErrorCode.UnsupportedOperation)
            {
                current = await GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
                if (!IsBoundary(current))
                {
                    throw;
                }
                hasNext = false;
            }
            if (!hasNext)
            {
                current = await GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
                yield return new StreamResponse { Task = current };
                yield break;
            }
            var update = updates.Current;
            if (update.Task is { } snapshot && IsInterrupted(snapshot.Status.State) && !IsBoundary(snapshot))
            {
                continue;
            }
            if (update.StatusUpdate is { } status && IsInterrupted(status.Status.State)
                && !IsBoundary(new AgentTask { Id = taskId, ContextId = status.ContextId, Status = status.Status }))
            {
                continue;
            }
            yield return update;
        }
    }

    private bool IsBoundary(AgentTask task) =>
        IsTerminal(task.Status.State) || handler.IsAwaitingInput(task) || handler.IsAwaitingAuthentication(task);
    private static bool IsBoundary(TaskState state) => IsTerminal(state) || IsInterrupted(state);
    private static bool IsInterrupted(TaskState state) => state == TaskState.InputRequired || state == TaskState.AuthRequired;
    private static bool IsTerminal(TaskState state) =>
        state == TaskState.Completed || state == TaskState.Failed || state == TaskState.Canceled || state == TaskState.Rejected;

    private static SendMessageRequest CopyRequest(SendMessageRequest request, bool returnImmediately) => new()
    {
        Message = request.Message,
        Metadata = request.Metadata,
        Tenant = request.Tenant,
        Configuration = new SendMessageConfiguration
        {
            ReturnImmediately = returnImmediately,
            HistoryLength = request.Configuration?.HistoryLength,
            AcceptedOutputModes = request.Configuration?.AcceptedOutputModes,
            TaskPushNotificationConfig = request.Configuration?.TaskPushNotificationConfig
        }
    };
}
