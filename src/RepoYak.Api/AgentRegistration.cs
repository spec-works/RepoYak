using System.Text.RegularExpressions;

namespace RepoYak.Api;

public sealed record AgentRegistration(string Id, string WorkspacePath, string? Persona = null,
    string? SessionMode = null);
public sealed record RegisterAgentRequest(string WorkspacePath, string? Id = null, string? Persona = null,
    string? Catalog = null, string? SessionMode = null);
public sealed record AgentRegistrationView(
    string Id, string WorkspacePath, string Status, string? Error, string CardUrl, string? Persona = null,
    string[]? Catalogs = null, string? SessionMode = null, string EffectiveSessionMode = "single");

public static class SessionModes
{
    public static string Validate(string mode) => mode is "single" or "daily" ? mode
        : throw new AgentRegistryException(400, "Session mode must be 'single' or 'daily'. Daily is reserved; execution currently uses single sessions.");
}

public sealed class AgentRegistryException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class AgentIds
{
    public static string FromWorkspace(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        var id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return Validate(id);
    }

    public static string Validate(string id)
    {
        if (id.Length > 64 || !Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
        {
            throw new AgentRegistryException(400,
                "Agent IDs must be 1-64 lowercase ASCII letters or digits, with single hyphens between words. Supply an explicit ID if the repository folder name cannot produce one.");
        }
        return id;
    }
}

public static class PersonaNames
{
    public static string Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100
            || name != name.Trim() || name.Any(char.IsControl) || name.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new AgentRegistryException(400, "Persona must be an installed custom-agent name, not a file path.");
        }
        return name;
    }
}
