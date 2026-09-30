# Character X-Ray — Milestone 3 runtime verifier: BLOCKED on the input adapter

Status after independent Windows acceptance of `5b756b2`. **Milestone 3 is not accepted.** The architecture is in place and
one of the three required behaviours is proven; the other two are blocked by the engine build, not by the code.

## The blocker: there is no way to inject input into this engine from the adapter

Claude deliberately left `_G.__ikemenlab_xray_inject(player, keys)` as a template. Implementing it requires a mechanism to
write live per-player held-key state. Traced from the actual source of `v1.0.0-jg-policy-5 - ffa-build`
(`D:\Games 3\Mugen AI Research\Ikemen-GO-ffa`, branch `ffa-experimental`, HEAD `00418e8ec3a2f99caf4acd54561b2db03daa6a64`):

**1. The Lua API has no input setter.** `src/script.go` registers 558 globals. The input-related ones are all configuration
or read-only:

| Binding | What it actually does |
|---|---|
| `getKey` / `getInput` / `getInputTime` | read-only queries |
| `resetKey` | clears the last captured *UI* key (`sys.keyInput`), not player state |
| `remapInput` | remaps which player's settings a slot reads |
| `setKeyConfig` / `getRemapInput` / `resetRemapInput` | change key *bindings*, not held state |

**2. Live input never passes through Lua.** Per frame, `InputReader.LocalInput(in int) [14]bool`
(`src/input.go:440`) merges SDL keyboard and joystick state and packs it into `InputBits`:

```
0=U 1=D 2=L 3=R 4=a 5=b 6=c 7=x 8=y 9=z 10=s 11=d 12=w 13=m
```

`sys` is not exposed to Lua as a table, so there is no back door either.

**3. There is no replay path.** The `demo` code in this engine is Motif presentation, not input playback. The build's
own `-h` output lists no `-playdemo`/`-record` flag.

**4. The sanctioned fallback cannot be built here.** The task allows adding a minimal engine hook. That needs Go, and
**there is no Go toolchain on this machine** (no `go` on PATH, not in Program Files, Go, or the user profile).

**5. The Lua adapter cannot reach the OS either.** `gopher-lua` has no Win32 bindings, so `xray_inject.lua` cannot call
`SendInput`. The adapter contract itself makes OS-level injection unreachable from inside the engine's VM.

## What was proven

**Inconclusive without an adapter (required, passes).** `runtime-verify` on the real install, no adapter supplied:

```json
{"status":"Inconclusive","reason":"InputInjectionUnavailable",
 "continuity":{"checked":false,"continuous":false,"firstHitFrame":null},
 "engineVersion":"v1.0.0-jg-policy-5 - ffa-build",
 "notes":["no injector is installed", "…a verify run will end Inconclusive (InputInjectionUnavailable)."]}
```

No `RuntimeVerified` evidence is produced, the sandbox is disposable, and all sandboxes were cleaned up.

**Determinism controls that genuinely exist in this build** (from `-h` and `src/script.go`, nothing invented):

- `-p1.ai 0` — disable P1's native AI; `-p2.ai <n>`; `-p<n>.power`, `-p<n>.life`; `-maxpowermode` (auto meter refill)
- `-speed <n>` (−9..9), `-time <num>` (`-1` disables), `-rounds <num>`, `-nosound`, `-nojoy`, `-nomusic`, `-windowed`
- From Lua: `frameStep()` (sets `sys.frameStepFlag`), `togglePause()`, `paused`, `getFrameCount`, `ticksPerSecond`

`-speed 1`, `-p1.ai 0`, `-rounds 1` and `frameStep()` together are enough to make a run frame-deterministic once input
can be fed.

## Chosen subject (from the ranker, not by reputation)

`rank-subjects --root "D:\Games 3\Ikemen_GO-v1.0.0"` scored 15 characters (80 skipped). Top five:

| Subject | Score | Edges | Clean | Clean hit-confirm cancels | Scriptable | Dynamic | AI entries |
|---|---|---|---|---|---|---|---|
| **bangirasu** | 159.2 | 142 | 84 | **71** | 60 | **0** | **0** |
| AVulpix | 154.4 | 390 | 204 | 199 | 60 | 5 | 0 |
| amaruruga | 152.9 | 119 | 63 | 62 | 26 | 0 | 0 |
| ChanseyJS | 150.6 | 172 | 87 | 82 | 60 | 0 | 0 |
| g-kuro | 121.8 | 185 | 44 | 38 | 48 | 3 | 0 |

**`bangirasu` is the first M3 subject**: highest score, 59% of edges clean (against ~0.6% for Funny Valentine), 71 clean
hit-confirm cancels, 60 scriptable neutral-start routes, and zero dynamic targets and zero AI entry points. Funny Valentine
would have been the worst possible choice — its `enemynear(var(59))` gating is exactly what the ranker is there to avoid.

## Verifier P2-continuity rule: no defect found on this subject

Claude's rule is that P2 must stay in a hit state from the first hit through the final step. That risks false "dropped"
verdicts on characters with custom victim states. `bangirasu` has **0 `TargetState` declarations and 4 `p2stateno` uses**,
and no throw/grab-driven victim routing, so standard hitstun applies and the rule should hold. The rule was **not** weakened,
because no real evidence justified it. It still needs checking against a character that does use `TargetState`.

## What is needed to unblock

Any **one** of these is sufficient:

1. **A Go toolchain**, so the sanctioned minimal hook can be built: an override consulted by `InputReader.LocalInput`,
   set through one `luaRegister` in `script.go`, inert unless the adapter calls it. ~20 lines, no gameplay change, and the
   patched binary would live only in the disposable sandbox.
2. **A Go toolchain plus `SDL_PushEvent`**, which would let the adapter feed synthetic key events without touching
   `LocalInput`.
3. **An upstream input-injection API** in a newer IKEMEN GO build, which would remove the need for a fork.

Option 1 is the smallest and is the one to take. It also keeps the patched binary strictly out of the production install,
so `D:\Games 3\Ikemen_GO-v1.0.0` stays byte-identical.

## Safety during this acceptance

Disposable sandboxes only, all cleaned up (0 remaining); the real install was only ever read and is byte-identical; no
character package was modified; no engine source was modified; the working tree is clean.