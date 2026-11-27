# RepoYak host contract

RepoYak is a Spec-Works project providing an agent host over workspace knowledge, skills,
instructions, and the Microsoft Copilot SDK (currently packaged as `GitHub.Copilot.SDK`).
This document describes the current 0.2 implementation,
not an independent replacement for the underlying protocol specifications.

## Specification boundaries

[A2A 1.0](https://a2a-protocol.org/v1.0.0/specification/) defines agent cards, messages, tasks,
artifacts, and streaming. [AI Catalog](https://ai-catalog.io/spec/) defines the discovery document.
[Agent Skills](https://agentskills.io/specification) defines workspace skill artifacts.
[specs.json](../specs.json) is the project's specification linkset.

RepoYak-specific contracts are registration, runtime ownership, workspace binding, and
receiving-session continuity. Git is the current workspace provider, not the public agent identity.

## Registration and host lifetime

`repoyak start` restores the persisted registry or initializes an empty one. An optional workspace
argument seeds a new registry only. The process runs in the foreground by default; `--background`
detaches it on Windows and writes `host.log` in the state directory. No OS service is installed.
`serve` remains a foreground alias. `stop` shuts down the host and all agents without removing
their saved registrations or sessions.

`add <repository-path> [--id <id>] [--persona <name>]`, `list`, `remove <id>`, and `stop` control the running host
through a same-user named pipe. Registrations persist independently of process lifetime.
The workspace basename supplies a default lowercase hyphen-separated ID. Duplicate IDs or workspace
paths are rejected. Explicit IDs contain 1-64 lowercase ASCII letters/digits separated by single
hyphens. A persona name is optional and selects a native SDK custom agent; availability is checked
before inference on first work. Missing personas fail explicitly.

`remove` cancels and joins outstanding A2A work, unregisters the agent, and disposes its session.
It does not delete workspace files or saved session state. Restart does not restore removed
registrations. Failed registrations are visible to management but excluded from the public catalog.

Agents are host-wide and catalogs are membership lists, not runtime owners. `add ... --catalog <name>`
creates a catalog or adds the matching existing agent to it without replacing its session.
`remove <id> --catalog <name>` removes only membership; the agent remains registered even with no
memberships. `remove <id>` unregisters it from all catalogs. `list --catalog <name>` filters agents.
Without a catalog option, new registrations join `default` and `list` returns all agents.

All commands accept `--config <json-path>` and `--port <port>`. The config persists host identity,
port, agents, and catalog memberships. Startup creates a missing file; legacy registrations migrate
to `default`. The host exclusively locks the config and updates it atomically for management changes.
Manual edits require a restart. An explicit config stores runtime state in `<config-path>.state`;
without one the legacy state directory remains in use. Management targets both port and config
identity. Background startup propagates those choices to its child process.
Windows retains the legacy pipe name. On macOS, the account, port, and config identity are
hashed into a compact same-user pipe name to fit Unix-domain socket path limits in the
default temporary directory; this does not change the management commands or trust boundary.

## Discovery and transport

| Surface | Contract |
|---|---|
| `GET /catalog` | Default catalog alias |
| `GET /catalogs/{name}` | Live `application/ai-catalog+json` listing ready members; unknown names return 404 |
| `GET /agents/{id}` | Direct agent card |
| `GET /agents/{id}/.well-known/agent-card.json` | Agent card discovery |
| `/agents/{id}` | Preferred HTTP+JSON A2A binding, including SSE |
| `/agents/{id}/a2a` | JSON-RPC compatibility binding |
| `GET /healthz` | Host responsiveness, not inference or persona readiness |

Catalog entries point to agent cards using `application/a2a-agent-card+json`. Agent identifiers use
`urn:air:{hostId}.localhost:{agentId}`, with no kind suffix. The publisher segment uses the persisted
host ID, independent of the serving address or port. It is a local discovery identity, not verified
publisher ownership. Cards take descriptive identity from workspace
`.agent/agent-profile.json`, then README fallback. Descriptive identity, registration ID, selected
persona, and session ID are distinct.
An agent in several catalogs has the same identifier, card URL, receiving session, and task store
in each. Membership is discovery configuration, not an authorization boundary.

Catalog construction uses `SpecWorks.AiCatalog` 0.7.0 models, its Discoverable-level structural
validator, and its serializer. Responses declare `specVersion: "0.9"`, not the previous `"1.0"`;
the entry wire field remains `type`, and optional null fields are omitted. Invalid generated
documents are not served. Routes, membership persistence, and ordering
are preserved. Structural validation does not perform signature/trust verification or add an
ASP.NET hosting dependency.

Agent entry identifiers replace the former `urn:agentdock:{hostId}:agent:{agentId}` format;
clients must refresh cached catalogs and entry references. The catalog host's
`urn:agentdock:host:{hostId}` identifier, registration IDs, configuration, and saved runtime sessions
remain unchanged. Agent identifiers stay the same across catalogs and host restarts.

Root A2A aliases exist only when an initial workspace is explicitly provided to `start` or `serve`.
Without one, callers discover through the catalog; no default is inferred from list order.

## Message, task, and session semantics

Each work message creates a tracked task, or continues an addressed nonterminal task. Task stores
are isolated per agent. A caller context ID is a correlation handle, not a receiving-session
selector. A completed-task revision is new work, not a continuation of a terminal task.

Execution emits Submitted, Working, public assistant updates as Working status messages, a final
result artifact, and Completed. Completion means execution finished, not caller acceptance.
Reasoning and raw tool data are not forwarded. Token-by-token artifact deltas are not implemented.

SDK user-input and elicitation callbacks emit `InputRequired` on the original task. Its status
message includes human-readable question text and a JSON data part with `requestId`, `kind`,
`question`, `choices`, `allowFreeform`, `requestedSchema`, and `url`. Synchronous sends and streams
return at this boundary. The SDK turn remains alive; a reply addressed to the same agent and task
resolves its pending callback rather than starting another turn. The task then returns to Working
and can request more input before completing. Replies to tasks not awaiting input are rejected.

Question-and-answer replies can be plain text or a data part containing `answer`. Form replies
use `{"action":"accept","content":{...}}`, `{"action":"decline"}`, or `{"action":"cancel"}`.
The same JSON can be sent as text for CLI form replies; plain `decline` and `cancel` also work.
Accepted forms require an object with the requested required fields. The schema is forwarded to
clients, not replaced with a full host-side JSON Schema validator. Include `requestId` in data
replies to guard against answering a superseded question. URLs are surfaced, never opened by the
host; URL elicitation can be acknowledged with plain `accept`, `decline`, or `cancel`.

Cancellation and host shutdown abort the waiting SDK turn. The existing ten-minute turn timeout
includes time waiting for input, and a timeout still transitions the task to Failed. Pending input
is in memory and does not survive restart. Answer or cancel pending input before `/new-session`.
This does not add caller authentication or change automatic tool permission approval. Do not send
credentials or secrets in input replies, which are part of the task's history.

### Local MCP authentication

RepoYak currently implements the **local personal-agent** deployment profile: a trusted local
caller invokes a user-operated agent, which obtains delegated MCP credentials through an interactive
browser sign-in. It does not need an inbound OAuth/OBO chain. A **hosted delegated-agent** profile
would require authenticated/authorized callers, user/tenant isolation, and delegated token exchange
such as OBO with the required registration and tenant consent. That profile is not implemented.

Each SDK session includes `authenticate_mcp`. With no `serverName` it passively lists configured
names, enablement, and observed connection states, without exposing configuration secrets. With
an exact enabled server name, it starts that configured server if necessary and, when `needs-auth`,
calls the runtime's OAuth login API using the authorization-code grant and default loopback callback.
It does not register a host-managed-token `OnMcpAuthRequest` handler, receive tokens, or prompt for
unrelated MCPs before each task. Agent instructions direct use of this tool when a needed MCP
requires sign-in.

While the selected server awaits sign-in, the original task emits `AuthRequired`. Synchronous sends,
streams, and subscriptions return at this boundary, leaving execution alive. The status includes
human-readable instructions and exactly this data shape:

```json
{
  "requestId": "<opaque-pending-request-id>",
  "kind": "local_mcp_oauth",
  "serverName": "<configured-MCP-name>",
  "authorizationUrl": "<runtime-provided-browser-sign-in-link>"
}
```

The caller presents the URL without opening it automatically. The user must use a browser on the
RepoYak machine. The runtime owns OAuth discovery, PKCE, the loopback listener, code exchange, and
MCP reconnection. Its matching session/server `connected` event, not a chat acknowledgement,
confirms completion. Then the original task returns to Working and continues the same SDK turn.
Poll or resubscribe to that task; do not send a work-message continuation. Tokens, authorization
codes, callback URLs, raw challenges, and client secrets are not requested in A2A messages or
returned by the authentication tool to the model.

Sign-in and ordinary input are serialized, late progress cannot obscure the pending state, and
`/new-session` is blocked while either is pending. Cancellation, host shutdown, and the ten-minute
turn timeout release the wait and attempt to stop the selected MCP server, closing its login listener
in the bundled runtime. Cleanup is bounded to five seconds. A late callback cannot complete a
canceled A2A task. If the provider rejects sign-in without a terminal runtime failure event, the
task can remain `AuthRequired` until canceled or timed out; do not interpret a browser callback
page as proof that the MCP connected. Other authentication-tool failures return a generic diagnostic
to the model, which can explain the limitation without disclosing provider responses.

OAuth tokens use explicit in-memory SDK storage. They can be reused by subsequent tasks in the
same loaded session, not by a new session after restart/rotation. This is not caller credential
isolation: every trusted local caller can use the agent's connected MCPs. The configured OAuth
client must support the runtime's loopback redirect; fixed incompatible redirects are not bypassed.
Arbitrary interactive authentication performed internally by a stdio MCP is not intercepted.
Hosted callbacks, inbound A2A authentication, OBO, and tenant approval remain outside this implementation.

Each agent owns one receiving Copilot session with serialized turns. Separate agents can run
concurrently on a single host-owned runtime. The runtime starts lazily on first work or explicit
`/new-session`; registration starts no Copilot process. Calls target the agent's session object,
not a process-wide current-session selector. Persona selection is session-scoped and reapplied
before work and rotation.

`/new-session` explicitly creates fresh continuity and returns a Message. Empty requests also
return Messages. Session IDs persist and are resumed; resume failures do not silently create new
conversations. A2A task records persist by default in `<host-state>\history.db`, independently of SDK
transcripts. They survive restart, rotation, and re-registration of the same host/agent/workspace binding.
Loaded sessions have no idle eviction; shared-runtime failure affects all hosted agents.

### Durable tasks and policy controls

The SQLite task store saves sanitized complete snapshots and state transitions in one transaction,
with observed UTC timestamps and monotonic revisions. Duplicate saves do not duplicate transitions;
terminal and stale snapshots cannot overwrite accepted results. Existing task-get/list filtering,
pagination, history trimming, artifacts, and metadata remain supported. Durable mode explicitly
records initial caller messages and input replies in task history.

The owner scope hashes durable host identity, agent ID, and canonical workspace path; catalog
membership and AIR entry identifiers do not select history. Unregistering retains records.
Re-registering the same binding restores access; reusing the ID for another workspace does not.
Startup reconciles unfinished records to Failed with an outcome-unknown explanation and preserves
partial results. It does not reconstruct callbacks or replay work. Pre-upgrade lost tasks are not imported.

`start --no-persistent-tasks` persists `persistentTasks: false`. That mode uses a separate in-memory
store and never touches the existing ledger. To re-enable, stop, edit the JSON value to true, and
restart. Defaults for absent fields are persistence enabled and `defaultSessionMode: "single"`.
Startup CLI settings override saved settings and are forwarded to the background child.
`--default-session-mode <single|daily>` selects the host default; `add --session-mode <single|daily>`
stores an agent override. Daily is reserved and warns at startup; effective behavior remains single.
Management views distinguish requested `sessionMode` from `effectiveSessionMode`.

The pinned A2A implementation awaits storage before event notification. Storage failures terminate
waiting observers and stop new admissions across the host; they never acknowledge a durable completion.
Recovery requires storage repair and host restart. The ledger uses FULL synchronous transactions
with a five-second busy timeout, schema version checks, and no connection pooling or automatic expiry.
Keep it on local disk. It is not encrypted; inherited host-state filesystem permissions apply.

Local MCP sign-in messages are archived as a generic marker and nonsecret server name, without
temporary authorization links, codes, or token fields. Active callback snapshots remain in process
memory for callers and do not survive restart. This is not reliable redaction of arbitrary pasted
secrets. Public deletion/export controls, session indexing, and model history-recall tools remain
future work; SDK transcript retention is separate.

## Trust boundary

The server binds to loopback. A2A is unauthenticated and editing/command permissions are automatically
approved under the host account. Agent/session separation is not an OS sandbox. All callers of
one agent share its history. Use only trusted local callers and workspaces.

Optional allowed roots restrict registration. Paths through junctions/symlinks are rejected.
An existing local folder is sufficient for registration. A `.git` file or directory at its root
enables Git support; folders without Git metadata remain supported.
Runtime discovery of supported Copilot configuration is enabled, including MCP server definitions;
explicit host settings take precedence. The host does not exclude specific MCP server names.
Repository MCP configuration is subject to Copilot folder trust; registration does not bypass it.
Repository file hooks remain disabled. Discovered servers may launch commands or contact external
services under the host account, subject to runtime authentication and managed policies. Their tool
calls use the host's automatic permission approval without per-call confirmation.
Selecting a persona does not grant access to app conversation history, private memory, or external
services. Retrieved workspace material goes to
the configured inference runtime; only approved content should be hosted.

## Verification and publication

The host prompt instructs agents to check repository freshness before relying on content and
permits `git pull --ff-only` only for workspaces with Git metadata, when the working tree is clean
and the current branch has an upstream. Without Git metadata, agents use current local files and
do not assume a branch or upstream, or initialize Git unless explicitly requested.
It does not authorize forced synchronization, branch switching, merges, rebases, or hiding local
changes. Update blockers and failures must be reported. This is agent guidance, not an automatic
host startup pull or an enforced sandbox. External service calls are not categorically prohibited
by the host prompt; credentials and private data remain protected.

The test suite covers registry persistence, ID/CLI validation, per-agent task isolation, HTTP+JSON
streaming, progress events, cancellation, and optional personas. Tests do not claim full A2A
conformance or validate every model/runtime capability. Real runtime checks require an authenticated
Copilot account and disposable workspaces; normal CI does not invoke inference.

The source repository is public at
[spec-works/RepoYak](https://github.com/spec-works/RepoYak). Hosting under Spec-Works
does not imply a published registry entry or public release.

[NuGet-tool packaging](nuget-tool.md) builds `SpecWorks.RepoYak.Cli` with command `repoyak`,
using self-contained Windows and macOS x64/ARM64 packages and a .NET 10 manifest. Disposable installation,
runtime handshake, and host HTTP checks gate the manually requested publication job. The
mechanism does not establish a NuGet.org release. macOS runs in the foreground; `--background`
remains Windows-only. macOS packaging includes the matching SDK-bundled Copilot runtime and
requires installed-tool verification on the advertised architecture. It does not install a
launch agent, grant macOS privacy permissions, or notarize a public binary distribution.
