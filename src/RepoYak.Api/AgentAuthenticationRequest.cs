namespace RepoYak.Api;

public sealed record AgentAuthenticationRequest(
    string ServerName,
    string AuthorizationUrl,
    Task Completion);
