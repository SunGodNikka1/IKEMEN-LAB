# X-Ray Phase 4 — Character X-Ray MCP (Observe + Experiment)

```
Claude Code / Codex / Cursor ──stdio (MCP, JSON-RPC 2.0)──▶ ikemenlab-mcp.exe
                                                              │  16 tools: Observe (8, read-only) · Experiment (8)
                                                              ▼
                                         IKEMENLab.Core — the ONE parser/index, names, candidate graph,
                                         Play Ability / State Preview / Sequence Lab planners & verifiers,
                                         PlaybackSession, sandbox + driver + probe, experiment store
                                                              │
                                         RuntimeBroker (machine-wide one-engine lease) ◀── also the desktop app and the CLI
                                                              ▼
                                         disposable IKEMEN sandbox (never the install; never character files)
```

An IDE agent can ask "show me this character's useful moves", "play Kung Fu Palm", "try Kung Fu Palm → chase to 35 → wait 20 → Normal A",
"compare that against waiting 15" and "why did this sequence fail?" and get X-Ray's own semantic answers — names first, ids secondary — instead
of reading CNS/CMD files. There is no X-Ray logic in the MCP project: it only turns tool arguments into Core calls and Core results into small JSON.

## Architecture

| Concern | Where | Shared with |
|---|---|---|
| Character resolution | `Core/XRay/Workbench/CharacterLocator` (moved from the CLI) | CLI |
| Index, names, candidate graph | `CharacterSemanticIndexer`, `CharacterNames`, `CandidateGraph` — exactly what the X-Ray window builds | app, CLI |
| Play Ability / State Preview | `AbilityPlayback.Resolve`, `PlaybackRequest` / `PreviewRequest`, `PlaybackSession.PlayAsync` / `PreviewAsync` | app (Ability Lab) |
| Sequence Lab | `SequenceExperimentRunner.Prepare` (new, shared) → `PlaybackSession.RunSequenceAsync` → `SequenceExperimentRunner.Run` | app (Sequence Lab now uses `Prepare` too), CLI |
| Verdicts | `AbilityVerifier`, `StatePreview.Read`, `SequenceVerifier` — unchanged | app, CLI |
| One engine at a time | `Core/XRay/Runtime/RuntimeBroker` (new) | app (`PlaybackSession`), CLI runtime commands |

`IKEMENLab.Mcp` (executable `ikemenlab-mcp.exe`, no third-party packages):
- `Protocol/McpServer.cs` — MCP over stdio: newline-delimited JSON-RPC 2.0; `initialize` (protocol 2025-06-18, 2025-03-26 or 2024-11-05),
  `ping`, `tools/list`, `tools/call`. Requests are answered concurrently; stdout carries only protocol messages, logs go to stderr.
  Every request gets an answer: an unexpected failure becomes an error result, never silence.
- `Lab/LabContext.cs` — the stores, the app's settings (read, never written), the character cache (rebuilt when a file changes;
  names re-read when the names file changes, so a rename in the app shows up on the next call).
- `Lab/ObserveTools.cs`, `Lab/ExperimentTools.cs` — the tools. `Lab/Semantic.cs` — JSON slices of Core results. `Lab/Resolve.cs` — "Kung Fu Palm" / "1000" / "ability:1000" → id.
- `Lab/JobQueue.cs` — long jobs, one at a time, through one `PlaybackSession`, each under a broker lease.

## Tools

All take `character` = a folder under `chars/` (`"kfm"`), a folder path, or a `.def` path. Moves and states can be named by display name
(the user's Phase-1 names first), id (`ability:1000`, `state:200`) or number.

**Observe (read-only)**

| Tool | Arguments | Returns |
|---|---|---|
| `inspect_character` | character | name/author, ability counts by category, ≤10 useful playable moves, the user's names, playback readiness (and who holds the engine), saved variants/experiments |
| `list_abilities` | character, category?, search?, playable_only?, limit ≤100 (40), offset | rows: name, id, category, command notation, entry state, literal damage/startup/meter, playable |
| `inspect_ability` | character, ability | categories (with confidence + rule), commands, entry state, move facts, how Play Ability presses it (or why not), ≤12 candidate follow-ups, source location |
| `explain_state` | character, state | Statedef settings, owning abilities, entered by / leads to (triggers in plain words + raw text, truncated), HitDefs, spawns, source location |
| `get_source` | character, object **or** file+start_line[, end_line], max_lines ≤200 (60) | numbered lines of ONE object (a state with its controllers; an ability = its entry state) or an explicit range of one of the character's own files; ≤24 000 chars; `nextStartLine` continues. Never a whole file, never a path outside the index's files |
| `list_experiments` | character, sequence_id?, limit ≤30, all_file_versions? | saved Sequence Lab variants + experiments on the current files (count of older-file-version experiments) |
| `get_experiment_results` | experiment_id, include_trials? | headline, exact scope, statistics, plain lines, the last trial step by step |
| `get_runtime_trace` | run_id, character?, from_frame?, to_frame?, max_events ≤200 (60), samples?, max_samples ≤120 (30) | named state changes, inputs, life changes, driver steps; optional sampled frames. Reads MCP runs, experiment trials and (read-only) the app's own playback history |

**Experiment**

| Tool | Arguments | Behaviour |
|---|---|---|
| `play_ability` | character, ability, setup?, wait_seconds ≤50 | job → Performed / Not performed / Could not be tested, what followed (contact, damage, reaction, recovery, situation), runId, provenance. Refused at once (no job) when the move has no command path from neutral |
| `preview_state` | character, state **or** ability, setup?, wait_seconds | job → `proof: false` always, `notProof` text, Forced / ForcedNotSeen / Refused / Could not run |
| `test_sequence` | character, steps **or** spec, trials ≤50 (1), accept_cost (needed above 10), name?, plan_only?, setup?, wait_seconds | job → the Sequence Lab verdict and per-step results; ×N → statistics. Unsaved tests get a stable id (`mcp-<hash of the steps>`) so their experiments stay comparable |
| `create_experiment` | character, name, steps **or** spec, sequence_id?, setup? | saves a named variant in the Sequence Lab's own store (new version when its steps change) and shows the plan. Launches nothing |
| `run_experiment` | character, sequence_id, trials, accept_cost, setup?, wait_seconds | job → runs the saved variant's current version |
| `compare_experiments` | experiment_a, experiment_b | `ExperimentText.Compare` rows + `sameSetup` + the "not directly comparable" warning |
| `get_job` | job_id? (none = recent jobs), wait_seconds ≤50 | queued / running / completed / failed / cancelled, phase, `progress {trial, of}`, `queuedBehind`, `waitingForEngine` (who holds it), result |
| `cancel_job` | job_id | queued → cancelled at once; running → the session's Cancel (engine killed, sandbox deleted, finished trials kept); "too late" once the result is saved |

Typed steps (same actions as the Sequence Lab): `{"action":"ability","ability":"Kung Fu Palm"}`, `walk_forward`/`walk_backward` + `frames`,
`dash`, `jump`, `wait` + `frames`, `chase` + `distance` (+ `stop_if_opponent_recovers`). A string item is a move name or a spec token
(`"wait:20"`); `spec` takes the compact form `"1000 > chase:35 > wait:20 > 200"`. `setup` = `{dummy, stage, approach_distance}` for one call;
it never changes settings.json.

The full JSON Schemas are what `tools/list` returns (`McpApp.BuildTools`); every object schema has `additionalProperties: false`.

## Evidence (unchanged semantics)

- Static answers carry `"basis"`: what the files say, never a runtime fact. Confidences are X-Ray's (`static: literal in the files`, `inferred`, `unknown`).
- Play Ability "Performed" cites only `runtime.transition-observed` (the route verifier's step check); measurements are of that run.
- State Preview is `proof: false` with the not-proof text; it cites no rule and is never a verdict.
- Sequences report exactly the Sequence Lab verdict (`verdict` text + `verdictCode`): True combo, Connected sequence, Did not connect, Could not test.
  A Connected sequence carries `opponentOutOfHitstunFrames`; nothing upgrades it.
- Experiments carry their exact scope (character files hash, engine sha256, dummy, stage, approach distance, sequence id + version, plan fingerprint).

## One engine at a time

`RuntimeBroker` is an exclusive handle on `%LOCALAPPDATA%\IKEMEN Lab\runtime\engine.lock` (readable, so others see who holds it). The OS drops it
when the process ends, so a crash never leaves the engine locked. It ignores `IKEMENLAB_APPDATA_DIR` on purpose (an isolated test run still must not
race the user's app); `IKEMENLAB_RUNTIME_LOCK` moves it for tests.
- **Desktop app**: `PlaybackSession` holds the lease for each Play Combo / Play Ability / Preview / Sequence run. Busy → "Could not play: The playback
  engine is busy: MCP · claude-code — test_sequence … (since 10:42, process 1234). Only one playback runs at a time; wait for it to finish or cancel it
  where it was started." Nothing is launched. Two X-Ray windows can no longer play at once either.
- **CLI**: `runtime-verify`, `runtime-ability`, `runtime-sequence` take the lease or exit 3 with the same message.
- **MCP**: a job waits `queued` with `waitingForEngine` naming the holder, starts as soon as the engine is free, and fails with the reason after 15
  minutes (`--queue-timeout-minutes`).
- Only the real engine runner leases; test runners (which launch nothing) do not.

The engine process gets its own stdin/stdout/stderr pipes (drained and discarded). It used to inherit the caller's: Ikemen GO reads stdin line by
line and runs each line as a command (`src/system.go`), so under MCP it could swallow a client's request, and anything it printed would have
corrupted the protocol stream (and the CLI's JSON output).

## Data safety

- One data folder (default: the app's `%LOCALAPPDATA%\IKEMEN Lab`; `--data-dir` or `IKEMENLAB_APPDATA_DIR` moves all of it):
  - names `xray\names` (read);
  - saved variants `xray\sequences` (`create_experiment` writes);
  - experiments `xray-experiments` (written);
  - MCP's own Play Ability / Preview records `xray-mcp-runs` (keep newest 25 of their own);
  - the app's Play Combo / Ability history `xray-playback` (read for traces, **never written**);
  - `settings.json` (read, **never written**).
- Experiments remember their origin (`app`, `cli`, `mcp`); retention keeps the newest 30 **per origin**, so agent runs never evict the user's Sequence
  Lab experiments (no retention was raised).
- Character files are never written; playbacks run in disposable sandboxes. There is no edit or deploy tool.
- MCP tests run against an isolated data folder, sandbox folder and runtime lock, and assert the real stores are byte-identical afterwards.

## Connecting an IDE

Build once (or `dotnet publish windows\IKEMENLab.Mcp -c Release -o windows\publish\mcp` for a stable copy; `windows/publish/` is git-ignored).
It needs the .NET 8 runtime. The IKEMEN folder, engine, dummy and stage come from IKEMEN Lab's settings (X-Ray → Combos → Playback setup);
`--root`, `--engine`, `--dummy`, `--stage` override them for the server.

Claude Code:
```
claude mcp add ikemenlab --scope user -- "D:\Games 3\Mugen AI Research\IKEMEN-LAB\windows\publish\mcp\ikemenlab-mcp.exe"
```
or a project `.mcp.json` (Cursor: `.cursor/mcp.json`, same shape):
```json
{ "mcpServers": { "ikemenlab": { "command": "D:\\Games 3\\Mugen AI Research\\IKEMEN-LAB\\windows\\publish\\mcp\\ikemenlab-mcp.exe", "args": [] } } }
```
Codex (`%USERPROFILE%\.codex\config.toml`):
```toml
[mcp_servers.ikemenlab]
command = 'D:\Games 3\Mugen AI Research\IKEMEN-LAB\windows\publish\mcp\ikemenlab-mcp.exe'
args = []
tool_timeout_sec = 90
```
`get_job` waits at most 50 s per call, so the default tool timeouts suffice; long experiments are polled.

## Example interaction

```
→ test_sequence {"character":"kfm","name":"Palm, chase 35, wait 20, Normal A","wait_seconds":50,
                 "steps":[{"action":"ability","ability":"Kung Fu Palm"},{"action":"chase","distance":35},
                          {"action":"wait","frames":20},{"action":"ability","ability":"Normal A"}]}
← {"jobId":"job-…","status":"completed","result":{"kind":"sequence","experimentId":"20261004-…",
     "run":{"verdict":"Connected sequence","verdictCode":"ConnectedSequence","summary":"Connected sequence: …",
            "steps":[{"step":1,"label":"Kung Fu Palm","outcome":"Done","result":"Done — connected at frame …"}, …],
            "opponentOutOfHitstunFrames":18, …}, "scope":{…}}}
→ compare_experiments {"experiment_a":"…","experiment_b":"…"}
→ get_experiment_results {"experiment_id":"…"}      → the failing step and its plain reason
→ get_runtime_trace {"run_id":"…","character":"kfm","from_frame":400,"to_frame":440}
```

## Tests

- `IKEMENLab.Mcp.Tests` (17): handshake/negotiation/tool list/errors; the real server process on stdio; the engine never sharing the protocol pipes
  (a stand-in engine that reads stdin, like Ikemen GO); observe slices; renamed abilities everywhere and by name; get_source limits; Play Ability
  through the playback stack into the MCP store; Preview `proof: false`; sequences give exactly Core's verdict on the recorded trace; variants,
  experiments and comparison (incl. different setup); job lifecycle with progress and queueing; cancellation (queued, running, mid-experiment, the
  start/busy race) and cleanup; shutdown; the broker queueing behind another client and timing out; per-origin retention; isolation from the real
  stores and settings.
- `IKEMENLab.Tests/XRayRuntimeBrokerTests.cs` (5): the broker, crash safety, which runner leases, the app session refusing while busy, experiment origins.

## Windows acceptance (2026-10-04)

A Python stdio client stood in for an IDE and drove the Release `ikemenlab-mcp.exe` against the installed IKEMEN (`D:\Games 3\Ikemen_GO-v1.0.0`,
engine sha256 `F68DC970…D0A1`), KFM vs KFM on ASI Fight Island, approach 40 — the Phase 2/3 setup. All app data went to an isolated `--data-dir`
(settings written there by hand; two names set there with the CLI: ability:1000 → "Kung Fu Palm", ability:200 → "Normal A"). The runtime lock was
the real machine-wide one.

| Step | Result |
|---|---|
| initialize / tools/list | protocol 2025-06-18, 16 tools |
| inspect_character kfm | Kung Fu Man, 36 abilities (25 playable), files `6BCCAC7CB89A0E24`, "Kung Fu Palm" among the useful moves (user name, X-Ray name `QCF x (↓↘→ + LP)` secondary) |
| get_source max_lines 500 | refused: "'max_lines' must be from 1 to 200" |
| CLI `runtime-ability` holds the engine, MCP `play_ability "Kung Fu Palm"` | MCP job `queued`: "The engine is busy: ikemenlab CLI — runtime-ability ability:1000 (since 13:23:56, process 15076)…"; the CLI finished (Performed), then the MCP job ran |
| play_ability | **Performed** · connected · knocked down · 90 damage · back in control after 48f; situation at frame 329: launched, 137 away; cites `runtime.transition-observed`; run `20261004-202411-3e0ec0` |
| CLI while MCP previews | exit 3: "The playback engine is busy: MCP · acceptance-ide — State Preview (not proof) · State 3000 …"; nothing launched |
| preview_state 3000 | `proof: false`, Forced (a look, not proof) |
| Kung Fu Palm → chase 35 → wait 20 → Normal A | **Connected sequence**: both attacks connected, opponent out of hitstun 18 frames and able to act for 4; 113 damage (= the Phase 3 app run) — experiment `20261004-202443-8b8937` |
| … wait 15 instead | **Did not connect**: "Normal A did not touch the opponent: when it started they were lying down, 32.47 away … they could act again at frame 430 (4 frames later)" — experiment `20261004-202501-6b4046` |
| compare_experiments | same setup; succeeded 1 (100%) vs 0 (0%); most common failure "Step 4 (Normal A): the attack did not touch the opponent"; damage 113 vs 90 |
| get_runtime_trace (frames 400–440) | 416 opponent 5110 → 5120 (getting up) · 425 x pressed · 426 You: State 0 → Normal A · 430 opponent → State 0 |
| test_sequence ×3, cancel during trial 1 | accepted; job `cancelled`; engine process gone, sandbox deleted, no experiment record |
| close stdin | server exit 0; no IKEMEN process, no sandbox, lock free |

The real `%LOCALAPPDATA%\IKEMEN Lab` stores (settings.json `e3d33f5f…`, xray-playback, xray-experiments, xray\sequences, qa-isolated,
runtime-sandboxes) were byte-identical before and after; xray\names and xray-mcp-runs stayed absent. The only new file is the empty lock file
`runtime\engine.lock`.

**Defect found and fixed.** The first full run stalled after a cancel: a `get_job` sent while IKEMEN was running was never answered, and a stack dump
showed the server idle. IKEMEN had inherited the server's stdin, and Ikemen GO reads its stdin line by line and runs each line as a command (`src/system.go`), so the
request went to the engine. `ProcessEngineRunner` now gives the engine its own pipes. `TheEngineNeverReadsTheProtocolStdinNorWritesIntoTheProtocolStdout`
fails without the fix (the stand-in engine echoes a swallowed request into the protocol stream) and passes with it. The server also now answers
every request, even when a handler fails unexpectedly.

## Deferred to Phase 5 and later

- Teach AI, AI code generation, Behavior Graph, Assistant UI, Autopilot, Strategy.
- Edit / Deploy tools (any write to character files), and renaming through MCP (names stay a Phase-1 UI/CLI action).
- Automatic combo / timing search, save/restore branching, prerequisite solving, engine controller attribution.
- MCP resources/prompts, progress notifications and `listChanged`; a cancelled-request notification does not interrupt a waiting `get_job`
  (it returns within 50 s).
- Jobs live as long as the server process (their results stay in the stores).
