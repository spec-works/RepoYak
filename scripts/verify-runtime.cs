#:project ../src/RepoYak.Api/RepoYak.Api.csproj
#:property PublishAot=false

using GitHub.Copilot;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using SpecWorks.AiCatalog.Parsing;
using SpecWorks.AiCatalog.Validation;

if (args is not [var runtimePath, var stateDirectory, var hostPath])
{
    throw new ArgumentException("Provide the installed runtime path, disposable state directory, and host path.");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
await using (var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.Empty,
    BaseDirectory = stateDirectory,
    UseLoggedInUser = false,
    Connection = RuntimeConnection.ForStdio(path: runtimePath)
}))
{
    await client.StartAsync(timeout.Token);
    var response = await client.PingAsync("repoyak-package-check", timeout.Token);
    if (response.Message != "pong: repoyak-package-check")
    {
        throw new InvalidOperationException($"Unexpected runtime ping response: '{response.Message}', protocol {response.ProtocolVersion}.");
    }
    Console.WriteLine($"Packaged runtime SDK handshake succeeded (protocol {response.ProtocolVersion}).");
    var inputReceived = false;
    await using var inputSession = await client.CreateSessionAsync(new SessionConfig
    {
        AvailableTools = ["ask_user"],
        OnPermissionRequest = PermissionHandler.ApproveAll,
        OnUserInputRequest = (request, _) =>
        {
            if (request.Question != "Choose a color." || request.Choices?.Contains("blue") != true)
            {
                throw new InvalidOperationException("Unexpected packaged SDK user-input request.");
            }
            inputReceived = true;
            return Task.FromResult(new UserInputResponse { Answer = "blue", WasFreeform = false });
        }
    }, timeout.Token);
#pragma warning disable GHCP001
    await inputSession.Rpc.Tools.ExecuteAsync("ask_user", JsonSerializer.SerializeToElement(new
    {
        question = "Choose a color.",
        choices = new[] { "red", "blue" },
        allowFreeform = false
    }), cancellationToken: timeout.Token);
#pragma warning restore GHCP001
    if (!inputReceived)
    {
        throw new InvalidOperationException("The packaged SDK did not invoke its user-input callback.");
    }
    Console.WriteLine("Packaged SDK user-input callback succeeded without inference.");
    var formReceived = false;
    await using var formSession = await client.CreateSessionAsync(new SessionConfig
    {
        AvailableTools = ["ask_user"],
        OnPermissionRequest = PermissionHandler.ApproveAll,
        AskUserVariant = AskUserVariant.Elicitation,
        OnElicitationRequest = request =>
        {
            if (request.Message != "Choose a color." || request.RequestedSchema is null)
            {
                throw new InvalidOperationException("Unexpected packaged SDK elicitation request.");
            }
            formReceived = true;
            return Task.FromResult(new ElicitationResult
            {
#pragma warning disable GHCP001
                Action = GitHub.Copilot.Rpc.UIElicitationResponseAction.Accept,
#pragma warning restore GHCP001
                Content = new Dictionary<string, object> { ["color"] = "blue" }
            });
        }
    }, timeout.Token);
#pragma warning disable GHCP001
    await formSession.Rpc.Tools.ExecuteAsync("ask_user", JsonSerializer.SerializeToElement(new
    {
        message = "Choose a color.",
        requestedSchema = new
        {
            type = "object",
            properties = new { color = new { type = "string" } },
            required = new[] { "color" }
        }
    }), cancellationToken: timeout.Token);
#pragma warning restore GHCP001
    if (!formReceived)
    {
        throw new InvalidOperationException("The packaged SDK did not invoke its elicitation callback.");
    }
    Console.WriteLine("Packaged SDK elicitation callback succeeded without inference.");
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

var start = new ProcessStartInfo(hostPath)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
start.Environment.Clear();
foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LOCALAPPDATA" })
{
    if (Environment.GetEnvironmentVariable(name) is { } value)
    {
        start.Environment[name] = value;
    }
}
start.ArgumentList.Add("start");
var configPath = Path.Combine(stateDirectory, "host.json");
start.ArgumentList.Add("--config");
start.ArgumentList.Add(configPath);
start.ArgumentList.Add("--port");
start.ArgumentList.Add(port.ToString());
start.Environment["REPOSITORY_AGENT_PORT"] = port.ToString();
start.Environment["REPOAGENT_STATE_DIRECTORY"] = Path.Combine(stateDirectory, "host");
var copilotHome = Path.Combine(stateDirectory, "copilot");
Directory.CreateDirectory(copilotHome);
await File.WriteAllTextAsync(Path.Combine(copilotHome, "settings.json"), """{"experimental":true}""");
await File.WriteAllTextAsync(Path.Combine(copilotHome, "config.json"), "{}");
start.Environment["COPILOT_HOME"] = copilotHome;
start.Environment["COPILOT_DISABLE_KEYTAR"] = "1";
using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installed host.");
var stdout = host.StandardOutput.ReadToEndAsync();
var stderr = host.StandardError.ReadToEndAsync();
try
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(1) };
    while (true)
    {
        timeout.Token.ThrowIfCancellationRequested();
        if (host.HasExited)
        {
            throw new InvalidOperationException($"Installed host exited ({host.ExitCode}): {await stdout}\n{await stderr}");
        }
        try
        {
            using var health = JsonDocument.Parse(await http.GetStringAsync("/healthz", timeout.Token));
            if (health.RootElement.GetProperty("status").GetString() != "ok")
            {
                throw new InvalidOperationException("Installed host reported an unhealthy status.");
            }
            break;
        }
        catch (HttpRequestException) when (!timeout.IsCancellationRequested)
        {
            await Task.Delay(200, timeout.Token);
        }
        catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
        {
            await Task.Delay(200, timeout.Token);
        }
    }
    using var catalog = await http.GetAsync("/catalog", timeout.Token);
    catalog.EnsureSuccessStatusCode();
    if (catalog.Content.Headers.ContentType?.MediaType != "application/ai-catalog+json")
    {
        throw new InvalidOperationException("Installed host did not expose the AI Catalog media type.");
    }
    var catalogDocument = AiCatalogParser.Parse(await catalog.Content.ReadAsStringAsync(timeout.Token));
    if (catalogDocument.SpecVersion != "0.9"
        || !AiCatalogValidator.Validate(catalogDocument, ConformanceLevel.Discoverable).IsValid)
    {
        throw new InvalidOperationException("Installed host did not expose a structurally valid AI Catalog v0.9 document.");
    }
    Console.WriteLine("Installed RepoYak host health and catalog checks succeeded.");

    var workspace = Path.Combine(stateDirectory, "workspace with spaces");
    Directory.CreateDirectory(workspace);
    await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "# Package check\n\nDisposable workspace.");
    await RunCommandAsync("add", workspace, "--id", "package-check");
    await VerifyRegistrationAsync(present: true);
    var populatedCatalog = AiCatalogParser.Parse(await http.GetStringAsync("/catalog", timeout.Token));
    if (!AiCatalogValidator.Validate(populatedCatalog, ConformanceLevel.Discoverable).IsValid
        || populatedCatalog.Entries.Count != 1
        || populatedCatalog.Entries[0].Type != "application/a2a-agent-card+json")
    {
        throw new InvalidOperationException("Installed host did not advertise the registered agent in a valid AI Catalog.");
    }
    using (var sessionHttp = new HttpClient { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(45) })
    {
        using var sessionResponse = await sessionHttp.PostAsJsonAsync("/agents/package-check/message:send", new
        {
            message = new
            {
                messageId = Guid.NewGuid().ToString("N"),
                role = "ROLE_USER",
                parts = new[] { new { text = "/new-session" } }
            }
        }, timeout.Token);
        var responseBody = await sessionResponse.Content.ReadAsStringAsync(timeout.Token);
        if (!sessionResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Installed host session initialization failed ({sessionResponse.StatusCode}): {responseBody}");
        }
        using var reply = JsonDocument.Parse(responseBody);
        if (!reply.RootElement.TryGetProperty("message", out var message)
            || message.GetProperty("parts")[0].GetProperty("text").GetString()
                != "Started a new agent session. Previous task records are unchanged.")
        {
            throw new InvalidOperationException($"Installed host returned an unexpected session initialization response: {responseBody}");
        }
    }
    Console.WriteLine("Installed host session initialization succeeded with experimental mode enabled, without inference.");
    await RunCommandAsync("stop");
    await host.WaitForExitAsync(timeout.Token);
    if (host.ExitCode != 0)
    {
        throw new InvalidOperationException($"Host shutdown failed ({host.ExitCode}): {await stderr}");
    }

    using var restarted = Process.Start(start) ?? throw new InvalidOperationException("Could not restart the installed host.");
    var restartedStdout = restarted.StandardOutput.ReadToEndAsync();
    var restartedStderr = restarted.StandardError.ReadToEndAsync();
    try
    {
        await VerifyRegistrationAsync(present: true);
        await RunCommandAsync("remove", "package-check");
        await VerifyRegistrationAsync(present: false);
        await RunCommandAsync("stop");
        await restarted.WaitForExitAsync(timeout.Token);
        if (restarted.ExitCode != 0)
        {
            throw new InvalidOperationException($"Restarted host shutdown failed ({restarted.ExitCode}): {await restartedStderr}");
        }
    }
    finally
    {
        if (!restarted.HasExited)
        {
            restarted.Kill(entireProcessTree: true);
        }
        await restarted.WaitForExitAsync();
        await Task.WhenAll(restartedStdout, restartedStderr);
    }
    Console.WriteLine("Installed CLI registration, persistence, removal, and graceful shutdown checks succeeded.");
}
finally
{
    if (!host.HasExited)
    {
        host.Kill(entireProcessTree: true);
    }
    await host.WaitForExitAsync();
    await Task.WhenAll(stdout, stderr);
    if (host.ExitCode != 0)
    {
        Console.Error.WriteLine(await stdout);
        Console.Error.WriteLine(await stderr);
    }
}

async Task<string> RunCommandAsync(params string[] arguments)
{
    var command = new ProcessStartInfo(hostPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        command.ArgumentList.Add(argument);
    }
    command.ArgumentList.Add("--config");
    command.ArgumentList.Add(configPath);
    command.ArgumentList.Add("--port");
    command.ArgumentList.Add(port.ToString());
    using var process = Process.Start(command) ?? throw new InvalidOperationException("Could not run an installed CLI command.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Installed CLI '{arguments[0]}' failed ({process.ExitCode}): {await error}");
        }
        return await output;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
    }
}

async Task VerifyRegistrationAsync(bool present)
{
    using var agents = JsonDocument.Parse(await RunCommandAsync("list"));
    var registrations = agents.RootElement.EnumerateArray().ToArray();
    if (registrations.Length != (present ? 1 : 0)
        || (present && (registrations[0].GetProperty("id").GetString() != "package-check"
            || registrations[0].GetProperty("status").GetString() != "ready")))
    {
        throw new InvalidOperationException("Installed CLI returned unexpected registrations.");
    }
}
