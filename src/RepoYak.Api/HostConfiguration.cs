using System.Text.Json;

namespace RepoYak.Api;

public sealed record HostConfiguration(string HostId, AgentRegistration[] Agents,
    Dictionary<string, string[]>? Catalogs = null, int? Port = null,
    bool PersistentTasks = true, string DefaultSessionMode = "single");

public sealed record HostOptions(string[] Arguments, string? ConfigPath, int Port,
    bool? PersistentTasks = null, string? DefaultSessionMode = null)
{
    public string? StateDirectory => ConfigPath is null ? null : ConfigPath + ".state";

    public static HostOptions Parse(string[] args)
    {
        string? config = null;
        int? port = null;
        bool? persistentTasks = null;
        string? sessionMode = null;
        var arguments = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--no-persistent-tasks")
            {
                if (persistentTasks is not null)
                {
                    throw new AgentRegistryException(400, "Repeated --no-persistent-tasks.");
                }
                persistentTasks = false;
            }
            else if (args[index] is "--config" or "--port" or "--default-session-mode")
            {
                var option = args[index];
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith('-'))
                {
                    throw new AgentRegistryException(400, $"A value is required for {option}.");
                }
                if (option == "--default-session-mode" && sessionMode is null)
                {
                    sessionMode = SessionModes.Validate(args[index]);
                }
                else if (option == "--config" && config is null)
                {
                    config = Path.GetFullPath(args[index]);
                }
                else if (option == "--port" && port is null && int.TryParse(args[index], out var parsed)
                    && parsed is >= 1 and <= 65535)
                {
                    port = parsed;
                }
                else
                {
                    throw new AgentRegistryException(400, $"Invalid or repeated {option}.");
                }
            }
            else
            {
                arguments.Add(args[index]);
            }
        }
        if ((persistentTasks is not null || sessionMode is not null)
            && arguments.FirstOrDefault() is "add" or "remove" or "stop" or "list")
        {
            throw new AgentRegistryException(400, "Task persistence and default session mode are host startup options.");
        }
        if (port is null && config is not null && File.Exists(config))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(config));
            if (document.RootElement.TryGetProperty("port", out var configuredPort)
                && configuredPort.ValueKind != JsonValueKind.Null)
            {
                port = configuredPort.GetInt32();
            }
        }
        if (port is null)
        {
            var value = Environment.GetEnvironmentVariable("REPOSITORY_AGENT_PORT") ?? "5080";
            if (!int.TryParse(value, out var parsed))
            {
                throw new AgentRegistryException(400, "REPOSITORY_AGENT_PORT must be a valid TCP port.");
            }
            port = parsed;
        }
        if (port is < 1 or > 65535)
        {
            throw new AgentRegistryException(400, "Port must be between 1 and 65535.");
        }
        return new HostOptions(arguments.ToArray(), config, port.Value, persistentTasks, sessionMode);
    }
}
