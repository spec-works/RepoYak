using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Text.Json;

namespace RepoYak.Api;

#pragma warning disable GHCP001

public sealed class LocalMcpAuthentication(
    CopilotSession session,
    Func<AgentAuthenticationRequest, CancellationToken, Task> requestAuthentication,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> InvokeAsync(string? serverName = null)
    {
        var token = _lifetime.Token;
        await _gate.WaitAsync(token);
        var loginStarted = false;
        var connected = false;
        try
        {
            var inventory = await session.Rpc.Mcp.ListConfiguredAsync(token);
            if (string.IsNullOrWhiteSpace(serverName))
            {
                return JsonSerializer.Serialize(inventory.Servers.Select(server => new
                {
                    name = server.Name,
                    enabled = server.Enabled,
                    status = server.Live?.Status.Value ?? "not_configured"
                }));
            }
            var server = inventory.Servers.SingleOrDefault(server => server.Name == serverName);
            if (server is null || !server.Enabled)
            {
                return "Select an enabled configured MCP server using its exact name. Omit serverName to list servers.";
            }
            if (server.Live?.Status == McpServerStatus.Connected)
            {
                return "The MCP server is already connected.";
            }
            if (server.Live?.Status != McpServerStatus.NeedsAuth)
            {
                await session.Rpc.Mcp.StartServerAsync(serverName, cancellationToken: token);
                inventory = await session.Rpc.Mcp.ListConfiguredAsync(token);
                server = inventory.Servers.Single(item => item.Name == serverName);
                if (server.Live?.Status == McpServerStatus.Connected)
                {
                    return "The MCP server is connected.";
                }
                if (server.Live?.Status != McpServerStatus.NeedsAuth)
                {
                    return "The MCP server is not awaiting OAuth sign-in. Check its local configuration.";
                }
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = session.On<SessionMcpServerStatusChangedEvent>(status =>
            {
                if (status.Data.ServerName != serverName)
                {
                    return;
                }
                if (status.Data.Status == McpServerStatus.Connected)
                {
                    completion.TrySetResult();
                }
                else if (status.Data.Status == McpServerStatus.Failed
                    || status.Data.Status == McpServerStatus.Stopped
                    || status.Data.Status == McpServerStatus.Disabled)
                {
                    completion.TrySetException(new InvalidOperationException("The MCP sign-in connection failed."));
                }
            });
            loginStarted = true;
            var login = await session.Rpc.Mcp.Oauth.LoginAsync(
                serverName, clientName: "RepoYak",
                callbackSuccessMessage: "Sign-in received. Return to your RepoYak caller; the task resumes when the MCP server connects.",
                grantType: McpOauthLoginGrantType.AuthorizationCode, cancellationToken: token);
            if (login.AuthorizationUrl is { Length: > 0 } url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var authorizationUri)
                    || (authorizationUri.Scheme != Uri.UriSchemeHttps
                        && !(authorizationUri.Scheme == Uri.UriSchemeHttp && authorizationUri.IsLoopback))
                    || !string.IsNullOrEmpty(authorizationUri.UserInfo))
                {
                    return "The MCP provider did not supply a safe browser sign-in URL.";
                }
                await requestAuthentication(new AgentAuthenticationRequest(serverName, url, completion.Task), token);
            }
            // A cached login may have connected before the RPC returned; the event subscription is already active.
            await completion.Task.WaitAsync(token);
            connected = true;
            return "MCP sign-in completed and the server is connected. Continue the original request.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // SDK errors can include provider responses. Neither the model nor task history needs those details.
            return "MCP sign-in failed. Check the MCP server's local configuration and try again.";
        }
        finally
        {
            if (loginStarted && !connected)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await session.Rpc.Mcp.StopServerAsync(new McpStopServerRequest { ServerName = serverName! }, cleanup.Token);
                }
                catch
                {
                    // Best effort: never let a stalled cleanup retain the task.
                }
            }
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        _gate.Release();
        _lifetime.Dispose();
    }
}
