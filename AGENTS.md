# AGENTS.md

Working notes for an agent (or a human) continuing this project.

## What this is, and what it is for

`widget-chat` is an ASP.NET Core chat that streams LLM-authored widgets — SVG, HTML, charts,
Three.js scenes — into a sandboxed iframe.

**The reason it exists is an article**: how to build agent-to-UI (generative UI) with Microsoft
Agent Framework and .NET. So optimise for *clarity and citable evidence*, not for features. When you
change something, prefer a change that produces a number, a screenshot, or a clear before/after over
a change that adds capability. If you fix a bug, note the symptom and cause in the README's
troubleshooting section; those become the article's most useful paragraphs.

## Commands

```bash
# live model (needs the key, see below)
dotnet run                       # http://localhost:5080

# offline: no key, no network, no spend
Llm__Provider=stub dotnet run

dotnet build
dotnet test tests/WidgetChat.Tests
```

### Kill a stale server before you start one

This wastes more time than anything else in this repo. `dotnet run` leaves `WidgetChat.exe` behind
when a command is interrupted; it keeps holding 5080 and the *next* instance fails to bind while the
old one answers. Symptom: `/api/info` reports a model you just changed away from.

```bash
for pid in $(ps -W | grep -i widgetchat | awk '{print $4}'); do taskkill //F //PID $pid; done
for pid in $(netstat -ano | grep ":5080 " | grep -i listen | awk '{print $NF}' | sort -u); do taskkill //F //PID $pid; done
netstat -ano | grep ":5080 " | grep -i listen || echo "free"
```

Always confirm the log line `Now listening on: http://localhost:5080` after starting. If it says
`address already in use`, you are looking at the wrong process's behaviour, not a code bug.

## Configuration and secrets

`appsettings.json` holds everything except the key:

| Key | Meaning |
|---|---|
| `Llm:Provider` | `openai` (any OpenAI-compatible endpoint) or `stub` |
| `Llm:Model` | model id as the provider spells it, e.g. `deepseek-v4.1-flash` |
| `Llm:BaseUrl` | OpenCode Go is `https://opencode.ai/inference/go/openai/v1` — note the `/go/` segment |
| `Llm:ApiKey` | leave empty here; supply out of band |
| `Llm:MaxOutputTokens` | `0` omits the cap; reasoning models need room, see below |
| `Llm:Temperature` | **optional and unset by default**; some models accept only 1 |
| `Llm:SessionHeader` | defaults to `x-opencode-session` on opencode.ai hosts, `none` elsewhere |
| `Sessions:Capacity` | conversations held in memory; the least recently used idle one is evicted, default 200 |
| `Sessions:MaxTurns` | turns before a conversation starts a fresh session, default 20; this is what bounds history growth and cost |

The key never lives in the repo:

```bash
dotnet user-secrets set "Llm:ApiKey" "oc_sk_..."
```

That writes to `%APPDATA%\Microsoft\UserSecrets\widgetchat-...\secrets.json`, keyed by
`UserSecretsId` in the csproj — so it survives moving or renaming the folder. Overrides for CI or
containers: `Llm__ApiKey`, `LLM_MODEL`, `OPENAI_BASE_URL`, `Llm__Provider=stub`.

## Architecture

```
Browser ──POST /api/chat──▶ ChatEndpoints ──▶ AIAgent (Agent Framework) ──▶ provider
   ▲                             │
   │        NDJSON lines         │  WidgetStreamParser splits the reply
   └─────────────────────────────┘  into text / height / css / html / js
        │
        └─▶ WidgetHost ──postMessage──▶ sandbox iframe ──▶ eval ──▶ window.stage
```

| Path | Role |
|---|---|
| `Program.cs` | DI wiring, static files, endpoints |
| `Chat/WidgetPrompts.cs` | the contract the model follows — half the design |
| `Chat/WidgetStreamParser.cs` | incremental channel split; the only genuinely tricky code |
| `Chat/ChatEndpoints.cs` | NDJSON streaming, retry, truncation and provider error mapping |
| `Agents/WidgetAgent.cs` | `ChatClientAgent` with instructions, no tools |
| `Agents/ConversationSessions.cs` | one `AgentSession` per conversation, leased |
| `Llm/ChatClientFactory.cs` | provider selection, user agent, session header |
| `Llm/OpenCodeSessionPolicy.cs` | adds `x-opencode-session` per request |
| `Llm/StubChatClient.cs` | offline provider; also the deterministic test fixture |
| `Samples/*.widget.txt` | worked examples in the wire format; the stub's replies |
| `wwwroot/widget-host.js` | the sandbox: bootstrap, injected runtime, morph, bridge, export |
| `wwwroot/app.js` | chat UI, NDJSON reader, widget lifecycle |

## Invariants — do not break these

1. **The wire contract lives in two places and they must agree.** The markers are declared as
   constants in `WidgetStreamParser` and described in prose in `WidgetPrompts`. Change one, change
   the other.
2. **The parser must be split-invariant.** `SplittingAnywhereYieldsTheSameResult` feeds the sample in
   at every possible index. A real token stream cuts markers anywhere; if that test fails, streaming
   is broken in a way that only shows up intermittently.
3. **The frame stays `sandbox="allow-scripts"` only.** Adding `allow-same-origin` would let generated
   code reach storage, cookies and your own APIs. The whole security argument rests on this one
   attribute.
4. **One animation loop.** `window.stage` owns it. Generated code must not create a renderer, a
   camera rig or a `requestAnimationFrame` loop of its own.
5. **Injected runtime functions must be self-contained.** They are serialised with `toString()` and
   evaluated in the frame, which has `default-src 'none'`. A closure over `widget-host.js` compiles
   fine and fails silently inside the frame.
6. **No tools on the agent.** See "Rejected approaches" in the README; a reference-widget tool
   tripled input tokens and caused roughly one empty reply in three.

## Known failure modes

| Symptom | Cause and fix |
|---|---|
| `Error: The model returned no text` | Reasoning model spent the budget before writing. Endpoint retries once; raise `Llm:MaxOutputTokens` or set `0`. |
| `400 ... invalid temperature: only 1 is allowed` | Model rejects a crafted temperature. Leave `Llm:Temperature` unset. |
| `402 Insufficient account funds` | Using the pay-as-you-go path with a Go subscription. `BaseUrl` needs `/inference/go/openai/v1`. |
| `400 ... missing x-opencode-session` | Go now requires the session header. It is added by `OpenCodeSessionPolicy`. |
| `/api/info` shows the old model | Stale process holding 5080. See above. |
| Widget renders but is blank / flat | Someone stopped using `window.stage`; the model reverted to hand-written renderer setup. |
| Widget cut off with no scroll | Iframe height clamp, or `.messages` lost `min-height: 0`. |

## Adding a widget shape

Put a complete, working example in `Samples/<kind>.widget.txt` following the channel format: prose,
then `@@HEIGHT@@`, `@@CSS@@`, `@@HTML@@`, `@@JS@@`, `@@END@@`. The stub serves those files, so
`Llm__Provider=stub dotnet run` is a free way to see the shape render. Note that the sample is *not*
injected into the model's context any more (see the rejected tool); it is a worked example for
humans and a fixture for the stub.

## Conventions

- C# with file-scoped namespaces, `TreatWarningsAsErrors` is off. Comments explain *why*, and each
  non-obvious shortcut is marked `shortcut:` with the condition for upgrading it.
- The frontend has **no build step**: plain browser JS, no modules, no bundler, no npm, served
  straight from `wwwroot`. Keep it that way; it is part of the article's argument.
- Keep dependencies to `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, and xunit for tests.
- Don't widen the sandbox bridge beyond `sendPrompt` and `openLink`, and validate both on the host
  side if you touch them.
