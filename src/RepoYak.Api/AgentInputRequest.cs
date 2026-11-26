using System.Text.Json;

namespace RepoYak.Api;

public sealed record AgentInputRequest(
    string Kind,
    string Question,
    IList<string>? Choices = null,
    bool AllowFreeform = true,
    JsonElement? RequestedSchema = null,
    string? Url = null);

public sealed record AgentInputResponse(string? Answer = null, string? Action = null, JsonElement? Content = null);
