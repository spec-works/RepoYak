#:project ../src/RepoYak.Api/RepoYak.Api.csproj
#:property PublishAot=false

using GitHub.Copilot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using RepoYak.Api;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#pragma warning disable GHCP001

if (args is not [var runtimePath, var stateDirectory])
{
    throw new ArgumentException("Provide the native runtime path and a disposable state directory.");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var builder = WebApplication.CreateBuilder([]);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
var origin = "";
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
string? challenge = null;
var exchanged = false;
var outcome = "success";
app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
{
    issuer = origin,
    authorization_endpoint = origin + "/authorize",
    token_endpoint = origin + "/token",
    response_types_supported = new[] { "code" },
    grant_types_supported = new[] { "authorization_code" },
    token_endpoint_auth_methods_supported = new[] { "none" },
    code_challenge_methods_supported = new[] { "S256" }
}));
app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
{
    resource = origin + "/mcp",
    authorization_servers = new[] { origin },
    scopes_supported = new[] { "tools" }
}));
app.MapGet("/authorize", (HttpContext context) =>
{
    challenge = context.Request.Query["code_challenge"];
    if (string.IsNullOrEmpty(challenge) || context.Request.Query["code_challenge_method"] != "S256")
    {
        return Results.BadRequest();
    }
    return Results.Redirect(QueryHelpers.AddQueryString(context.Request.Query["redirect_uri"].ToString(),
        outcome == "access_denied"
            ? new Dictionary<string, string?> { ["error"] = "access_denied", ["state"] = context.Request.Query["state"] }
            : new Dictionary<string, string?>
            {
                ["code"] = outcome == "invalid_code" ? "invalid-code" : code,
                ["state"] = outcome == "invalid_state" ? "invalid-state" : context.Request.Query["state"]
            }));
});
app.MapPost("/token", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var actual = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
    if (form["code"] != code || actual != challenge)
    {
        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
    }
    exchanged = true;
    return Results.Json(new { access_token = token, token_type = "Bearer", expires_in = 3600, scope = "tools" });
});
app.MapMethods("/mcp", ["GET", "POST", "DELETE"], async (HttpContext context) =>
{
    if (context.Request.Headers.Authorization != "Bearer " + token)
    {
        context.Response.Headers.WWWAuthenticate =
            $"Bearer resource_metadata=\"{origin}/.well-known/oauth-protected-resource\", scope=\"tools\"";
        context.Response.StatusCode = 401;
        return;
    }
    if (context.Request.Method != "POST")
    {
        context.Response.StatusCode = 405;
        return;
    }
    using var body = await JsonDocument.ParseAsync(context.Request.Body);
    if (!body.RootElement.TryGetProperty("id", out var id))
    {
        context.Response.StatusCode = 202;
        return;
    }
    var method = body.RootElement.GetProperty("method").GetString();
    object result = method switch
    {
        "initialize" => new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { tools = new { } },
            serverInfo = new { name = "repoyak-auth-check", version = "1" }
        },
        "tools/list" => new { tools = Array.Empty<object>() },
        _ => new { }
    };
    await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = id.Clone(), result });
});
await app.StartAsync(timeout.Token);
origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var environment = new Dictionary<string, string>();
foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "HOME", "USERPROFILE", "LOCALAPPDATA" })
{
    if (Environment.GetEnvironmentVariable(name) is { } value)
    {
        environment[name] = value;
    }
}
environment["COPILOT_HOME"] = Path.Combine(stateDirectory, "copilot-home");
environment["COPILOT_DISABLE_KEYTAR"] = "1";
await using var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.Empty,
    BaseDirectory = stateDirectory,
    UseLoggedInUser = false,
    Environment = environment,
    Connection = RuntimeConnection.ForStdio(path: runtimePath)
});
await client.StartAsync(timeout.Token);
LocalMcpAuthentication? authentication = null;
SessionConfig Configure() => new()
{
    EnableConfigDiscovery = false,
    AvailableTools = new ToolSet().AddCustom("authenticate_mcp"),
    Tools = [AIFunctionFactory.Create((string? serverName = null) => authentication!.InvokeAsync(serverName), "authenticate_mcp")],
    OnPermissionRequest = PermissionHandler.ApproveAll,
    McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory,
    McpServers = new Dictionary<string, McpServerConfig>
    {
        ["auth-check"] = new McpHttpServerConfig
        {
            Url = origin + "/mcp", OauthClientId = "repoyak-auth-check", OauthPublicClient = true
        }
    },
    OnEvent = evt =>
    {
        if (evt is SessionMcpServerStatusChangedEvent status)
        {
            Console.WriteLine("MCP status: " + status.Data.Status + "; has error: " + !string.IsNullOrEmpty(status.Data.Error));
        }
        else if (evt is McpOauthRequiredEvent)
        {
            Console.WriteLine("MCP OAuth required event.");
        }
    }
};
await using var session = await client.CreateSessionAsync(Configure(), timeout.Token);
Console.WriteLine("Session created.");
await session.Rpc.Mcp.ListAsync(timeout.Token);
Console.WriteLine("MCP inventory loaded.");
await using var localAuthentication = new LocalMcpAuthentication(session, async (request, cancellationToken) =>
{
    using var browser = new HttpClient();
    using var callback = await browser.GetAsync(request.AuthorizationUrl, cancellationToken);
    callback.EnsureSuccessStatusCode();
    await request.Completion.WaitAsync(cancellationToken);
}, timeout.Token);
authentication = localAuthentication;
var inventoryResult = await session.Rpc.Tools.ExecuteAsync("authenticate_mcp",
    JsonSerializer.SerializeToElement(new { }), cancellationToken: timeout.Token);
var inventoryText = JsonSerializer.Serialize(inventoryResult);
if (!inventoryText.Contains("auth-check", StringComparison.Ordinal) || inventoryText.Contains(origin, StringComparison.Ordinal)
    || inventoryText.Contains(token, StringComparison.Ordinal) || inventoryText.Contains(code, StringComparison.Ordinal))
{
    throw new InvalidOperationException("The authentication inventory did not contain only safe connection metadata.");
}
var unknown = await localAuthentication.InvokeAsync("unknown-server");
if (!unknown.StartsWith("Select an enabled configured MCP server", StringComparison.Ordinal))
{
    throw new InvalidOperationException("An unknown server was not rejected.");
}
var result = await session.Rpc.Tools.ExecuteAsync("authenticate_mcp",
    JsonSerializer.SerializeToElement(new { serverName = "auth-check" }), cancellationToken: timeout.Token);
var serializedResult = JsonSerializer.Serialize(result);
if (!serializedResult.Contains("MCP sign-in completed", StringComparison.Ordinal)
    || serializedResult.Contains(token, StringComparison.Ordinal)
    || serializedResult.Contains(code, StringComparison.Ordinal))
{
    throw new InvalidOperationException("The authentication tool did not return a credential-free success result.");
}
if (!exchanged)
{
    throw new InvalidOperationException("The runtime did not exchange the authorization code using PKCE.");
}
Console.WriteLine("Runtime-managed loopback callback and token exchange succeeded.");
var alreadyConnected = await localAuthentication.InvokeAsync("auth-check");
if (!alreadyConnected.Contains("already connected", StringComparison.Ordinal))
{
    throw new InvalidOperationException("A connected server unexpectedly requested another sign-in.");
}

await using var canceledSession = await client.CreateSessionAsync(Configure(), timeout.Token);
await canceledSession.Rpc.Mcp.ListAsync(timeout.Token);
using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
var presented = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var canceledAuthentication = new LocalMcpAuthentication(canceledSession, async (request, cancellationToken) =>
{
    presented.TrySetResult(request.AuthorizationUrl);
    await request.Completion.WaitAsync(cancellationToken);
}, cancel.Token))
{
    var pending = canceledAuthentication.InvokeAsync("auth-check");
    var oldUrl = await presented.Task.WaitAsync(timeout.Token);
    await cancel.CancelAsync();
    try
    {
        await pending;
        throw new InvalidOperationException("Canceled authentication unexpectedly succeeded.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
    {
    }
    var canceledInventory = await canceledSession.Rpc.Mcp.ListConfiguredAsync(timeout.Token);
    if (canceledInventory.Servers.Single().Live?.Status != McpServerStatus.Stopped)
    {
        throw new InvalidOperationException("Canceled authentication did not stop the MCP server.");
    }
    using var lateBrowser = new HttpClient();
    exchanged = false;
    try
    {
        using var lateCallback = await lateBrowser.GetAsync(oldUrl, timeout.Token);
        if (lateCallback.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The canceled OAuth listener accepted a late callback.");
        }
    }
    catch (HttpRequestException)
    {
        Console.WriteLine("Canceled login listener is closed.");
    }
    var afterCallback = await canceledSession.Rpc.Mcp.ListConfiguredAsync(timeout.Token);
    if (afterCallback.Servers.Single().Live?.Status != McpServerStatus.Stopped || exchanged)
    {
        throw new InvalidOperationException("A late callback reconnected the canceled MCP server or exchanged a code.");
    }
}
Console.WriteLine("Canceled sign-in stopped the MCP server and rejected a late callback.");
if ((await session.Rpc.Mcp.ListConfiguredAsync(timeout.Token)).Servers.Single().Live?.Status != McpServerStatus.Connected)
{
    throw new InvalidOperationException("Canceling sign-in affected a different session with the same MCP server name.");
}

foreach (var failure in new[] { "access_denied", "invalid_state", "invalid_code" })
{
    outcome = failure;
    exchanged = false;
    await using var rejectedSession = await client.CreateSessionAsync(Configure(), timeout.Token);
    await rejectedSession.Rpc.Mcp.ListAsync(timeout.Token);
    using var rejectedTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    rejectedTimeout.CancelAfter(TimeSpan.FromSeconds(3));
    await using var rejectedAuthentication = new LocalMcpAuthentication(rejectedSession, async (request, cancellationToken) =>
    {
        using var browser = new HttpClient();
        using var callback = await browser.GetAsync(request.AuthorizationUrl, cancellationToken);
        await request.Completion.WaitAsync(cancellationToken);
    }, rejectedTimeout.Token);
    try
    {
        var rejected = await rejectedAuthentication.InvokeAsync("auth-check");
        if (rejected.Contains("completed", StringComparison.Ordinal) || rejected.Contains(token, StringComparison.Ordinal)
            || rejected.Contains(code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rejected sign-in was accepted or disclosed credentials.");
        }
    }
    catch (OperationCanceledException) when (rejectedTimeout.IsCancellationRequested)
    {
        // The pinned runtime can leave a rejected manual login in needs-auth until task cancellation.
    }
    if (exchanged || (await rejectedSession.Rpc.Mcp.ListConfiguredAsync(timeout.Token)).Servers.Single().Live?.Status
        == McpServerStatus.Connected)
    {
        throw new InvalidOperationException("Rejected OAuth credentials connected an MCP server.");
    }
    Console.WriteLine("Rejected OAuth scenario did not authenticate: " + failure);
}
await app.StopAsync(timeout.Token);
