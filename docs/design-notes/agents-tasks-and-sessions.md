# Design Note: Agents, Tasks, and Sessions

## Problem

Tools like Claude Code and GitHub Copilot CLI organize everything around a "session", and the user ends up managing sessions by hand. The word is overloaded: it means a slice of conversation history, a unit of work, and a bucket of memory all at once. As usage grows, that single concept becomes painful to manage. The goal is a model where users reason about things that match how they think, and sessions become internal plumbing.

## Core Model: Agent plus Task

Users think in terms of work to be done and who they are asking to do it, not in terms of sessions.

- **Agent**: has an identity, a role, and awareness of certain topics. An agent can be a proxy for a topic (for example, a marketing agent) or a horizontal role (for example, a research agent).
- **Task**: the unit of work, in the A2A sense. Every task has exactly one owning agent, and that agent alone decides whether it has succeeded, failed, or been cancelled.

A task never spans agents. If two agents could both decide a task's state, there would be no single owner, and state management becomes a distributed consensus problem. Instead, coordination between agents happens through **related tasks**: when work is handed to another agent, that agent creates its own task and links it to the originating one.

## The Entry Agent as Saga Owner

When work fans out across agents, the task of the first agent that receives the user's request is the governing task, in domain-driven design terms the **saga**. The user only ever talks to that agent, and the graph of related tasks is available for inspection, like a trace, but is otherwise an implementation detail.

There is an important exception. Sometimes the entry agent is purely a router and adds nothing after handing off. In that case ownership should transfer: the specialist's task becomes the primary task and the router's task simply completes. The deciding question is **who is accountable to the user for the outcome**. If the entry agent will synthesize, verify, or answer for the result, it delegates and remains saga owner. If it adds nothing after routing, it transfers.

## Mapping the Five Interaction Patterns to Tasks

The IETF draft *Framework, Use Cases and Requirements for AI Agent Protocols* (Rosenberg and Jennings, draft-rosenberg-aiproto-framework-00) describes five ways an invoking agent can involve an invoked agent. These are defined in telephony terms rather than A2A terms. The mapping below is our interpretation.

| Pattern | What the draft describes | Task interpretation |
| --- | --- | --- |
| Blind transfer | The user is redirected to the new agent; the original agent has no access afterward, though context can be passed along. | Ownership transfers. The specialist creates its own task, linked to its origin. The router's task completes. |
| Supervised transfer | The original agent conferences in the new agent, introduces it, then drops. | Same as blind transfer, with a visible introduction phase before the router's task closes. |
| Sidebar | The original agent talks to the other agent privately, to get information it needs to keep handling the user. | Delegation. A related sub-task is created; the entry agent remains saga owner and the user never sees it directly. |
| Conference | Both agents and the user converse together, and both agents see all messages. | Delegation with shared context. The entry agent keeps the owning task, the other agent has a related task, and they share a conversation. This stresses the single-owner rule and needs care. |
| Passthrough | The original agent relays content in both directions without acting on it, though it may store it for history. | Delegation with the router as a relay. The router's task stays open but adds no judgment. |

The patterns differ along two axes: **who owns the outcome**, and **what the user can see**. This also defines the user's view. In the two transfer patterns, the user's thread of work re-points to the new owner. In the other three, it stays anchored on the entry agent's task.

## Sessions as Internal Plumbing

Sessions still matter, because an agent doing several tasks in a row benefits from being aware of the earlier ones. But users should not manage them.

- **A session is keyed by agent identity and day.** The address is predictable: anything that wants Wednesday's work with a given agent can construct the key without a lookup.
- **The day is the floor.** A session never spans more than one day.
- **The agent decides whether to split further within a day.** The agent already has to decide how work breaks into tasks, so deciding where to cut sessions is no harder. A cut is a real cut: nothing is automatically carried into the new session. Reasons to cut include a long idle gap, a clear topic switch, or context growing large enough to degrade quality.
- **Users refer to days, not sessions.** "That thing we were doing on Wednesday" resolves to a search across that agent's sessions for that day.
- **The day means the user's local day**, not the server's. A cutoff such as 4 a.m. rather than midnight keeps late-night work attached to the evening it belongs to.

## Agents as Topic Boundaries

The agent is the topic boundary, and the user chooses it deliberately by choosing who to talk to. That makes partitioning free: each agent's role and knowledge scope define what belongs in its history, and recall stays naturally scoped. Asking the marketing agent about Wednesday never means wading through unrelated coding conversations.

### Creation must be nearly free

If a new topic means a new agent, then creating an agent has to cost about as much as opening a new browser tab. If it requires configuring prompts, tools, and permissions, people will cram unrelated topics into one agent and the bloated-session problem returns under a different name.

- **Minimum viable agent**: a name and a one-line purpose, with everything else inherited from sensible defaults.
- **Identity accumulates**: the agent starts as a blank topic and becomes a specialist through its own memory and daily summaries.
- **Cost of success**: sprawl. Heavy users will end up with dozens of agents, many dormant, so organization matters as much as creation.

### Organizing agents with AI Catalog

Physical storage and navigation are kept separate. Each agent's workspace, memory, and summaries live in its own folder on disk. An AI Catalog sits on top as an index, so reorganizing never means moving anything.

The AI Catalog specification supports this shape. Catalog entries can point to other catalogs, which gives hierarchical organization. An entry inside a nested catalog may reuse the same identifier as an entry elsewhere to indicate the same logical artifact, which allows a graph rather than a strict tree. A pricing agent could therefore be reachable under both marketing and finance without being duplicated. A minimal catalog needs only a spec version and an array of entries, and catalogs can be plain local files, which fits the lightweight requirement.

This also helps with sprawl. Dormant agents can be archived or collapsed in the catalog without being deleted, and a parent entry could itself act as a router, which ties back to the transfer patterns above.

Note: the AI Catalog spec is still evolving and appears to be in draft, so the nesting details should be verified against the current version before building on them.

Sources: [AI Catalog specification](https://ai-catalog.io/spec/), [AI Catalog project site](https://agent-card.github.io/ai-catalog/).

## Memory

Memory is a separate layer from sessions and tasks, not something a session owns.

- At the end of each day, a scheduled job summarizes the day: key learnings, completed work, and items to carry over.
- That distilled summary is what persists forward. Raw session history stays retrievable but is the second place to look.
- Recall for a request like "the thing from Wednesday" therefore searches summaries first and raw history second.

## Open Questions

- Should the choice between delegation and transfer be decided by the agent at runtime, or declared up front in the agent's configuration?
- How should the saga handle failure of a sub-task: retry, compensate, or escalate to the user?
- If the user sends a follow-up while sub-tasks are still running, how does the entry agent decide whether it amends the running saga or starts a new one?
- How should the conference pattern be implemented without breaking single ownership of task state?
- Is there a risk of the entry agent becoming a coordination bottleneck, and should entry agents be mostly routers or real specialists?
- Should a parent entry in the agent catalog be able to route requests to its children, or create a new child agent when nothing fits? This is an extension beyond what the AI Catalog spec describes, and the parent would need to be a real agent rather than a folder.
- How should an agent be archived and later revived without losing its memory and summaries?
- How do topics that span several agents (for example marketing, legal, and engineering) get a coherent memory when the knowledge is scattered across them?
