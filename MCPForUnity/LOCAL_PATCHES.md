# Local patches (fork: FatihDikmen-GameDev/unity-mcp-upgraded)

Machine-local changes applied on top of upstream (CoplayDev/unity-mcp). **Re-apply / re-verify these on
every upstream merge or plugin update.**

---

## Patch 1 — Domain-reload wedge hardening in `WebSocketTransportClient`

**File:** `Editor/Services/Transport/Transports/WebSocketTransportClient.cs`

**Symptom:** with the HTTP/WebSocket MCP bridge *connected*, a script change triggers a domain reload that
hangs at "Reload Script Assemblies (busy for NN:NN)" — Editor.log stops growing, one CPU core pegged at
100%. `beforeAssemblyReload` → `HttpBridgeReloadHandler` → `TransportManager.ForceStop` →
`WebSocketTransportClient.ForceStop` cannot get the background receive thread to exit, so Mono's domain
unload blocks on it forever.

**History:** an earlier 2-part patch (break-on-dead-socket in `ReceiveLoopAsync` + `Task.WaitAll` on the
loops in `ForceStop`) reduced but did **not** eliminate it — it still wedged reliably when connected.

**This patch (2026-07-18) adds, on top of the earlier two hunks:**

1. **`_shuttingDown` volatile flag** (new field). Latched `true` for the duration of `ForceStop`.
   - `StartBackgroundLoops` returns early when set — stops a reconnect racing the teardown from spawning a
     NEW, untracked receive thread that `ForceStop` never waits for (the prime "always wedges" cause).
   - `HandleSocketClosureAsync` returns early when set — no reconnect is kicked off during teardown.
2. **`ForceStop` rewrite:** cancel tokens → abort **and Dispose** the socket (null-first) to force an
   in-progress native `ReceiveAsync` to return even if token cancellation alone doesn't interrupt it on
   2021.3 Mono → bounded `Task.WaitAll(..., 800ms)` (was 2s; a long main-thread block risks the
   main-thread↔background-marshal deadlock) → `finally { _shuttingDown = false; }`.
3. **Instrumentation:** `McpLog.Info("[WebSocket] ForceStop: …")` at begin / after socket dispose / loops
   exited within cap = {bool} / done. If it EVER wedges again with the bridge connected, the Editor.log
   shows how far `ForceStop` got — "done" present + still hung ⇒ the hang is BELOW the managed layer (a
   native read that ignored cancel+dispose), which needs a different fix (bounded reads / raw socket).

**Merge guidance:** if upstream refactors `ForceStop`/`ReceiveLoopAsync`/`StartBackgroundLoops`/
`HandleSocketClosureAsync`, re-apply the `_shuttingDown` guard in all four spots and keep the bounded
(≤~1s) WaitAll. Retire this only once upstream ships an equivalent (track CoplayDev/unity-mcp #657).

---

## Patch 2 (2026-07-19) — silence the teardown unwind (wedge when the reload is caused BY a bridge command)

**File:** `Editor/Services/Transport/Transports/WebSocketTransportClient.cs`
**Patch file:** `local-patches/0002-silence-teardown-logging.patch` (full diff vs upstream — includes Patch 1's hunks)

**Symptom:** with Patch 1 in place, reloads *driven through the bridge itself* (`refresh_unity`
force/compile, `manage_editor play`/`stop`) still wedged ~every other time. Same signature; Editor.log's
last entries are `SetException` stacks through `HandleExecuteAsync`/`HandleMessageAsync`.

**Cause:** the reload starts while the receive thread is still inside `HandleExecuteAsync` (the command
caused the reload). `ForceStop` nulls the socket → the response send throws ("WebSocket is not
initialised") → the unwind ran `McpLog.Warn` + `HandleSocketClosureAsync`, which captured
`new StackTrace(true)` + `McpLog.Debug` **before** its `_shuttingDown` guard — i.e. Unity logging + an
expensive stack capture from a **background thread while the main thread freezes the world**. That is
what wedges Mono's domain unload.

**Fix (4 spots, all in this file):**
1. `HandleSocketClosureAsync` — teardown guards moved ABOVE the stack capture / Debug log; `_lifecycleCts`
   read via a local (ForceStop nulls it concurrently).
2. `HandleExecuteAsync` — drop the response silently when `_shuttingDown || token cancelled || _socket == null`.
3. `ReceiveLoopAsync` both catches — silent `break` during teardown (no Warn, no closure call).
4. `KeepAliveLoopAsync` catch — same silent `break`.

**Merge guidance:** guards-before-logging is the invariant. If upstream touches these methods, re-apply:
no background-thread `Debug.Log`/`StackTrace(true)` may run once `_shuttingDown` is set or the lifecycle
token is cancelled, and no response send may touch a nulled socket.

---

## Environment note — the bigger Windows cause: Defender scanning (NOT the plugin)

Independently of the bridge, slow/stuck domain reloads on Windows are commonly **Windows Defender
real-time-scanning the freshly-compiled assemblies** (100% of one core, Editor.log quiet — same signature).
Fix once per machine (admin PowerShell):

```powershell
Add-MpPreference -ExclusionPath  "C:\Users\fatih\UnityProjects"      # covers each project's Library/ScriptAssemblies
Add-MpPreference -ExclusionProcess "Unity.exe"
Add-MpPreference -ExclusionProcess "bee_backend.exe"
Add-MpPreference -ExclusionProcess "UnityShaderCompiler.exe"
```

Applied 2026-07-18 (whole `UnityProjects` folder excluded). This is the primary fix for reloads that wedged
**even with the MCP bridge off**; Patch 1 hardens the separate bridge-connected case on top.

---

## 0003 — reconnect-task teardown race + pre-freeze early teardown (2026-08-12)

**Symptom:** the reload wedge RETURNED despite 0001+0002 — 7 kills in one day. Signature unchanged
(stuck at "Begin MonoManager ReloadAssembly", 0 Editor.log growth, 100% of one core), often preceded in
Editor.log by "Attempted to call .Dispose on an already disposed CancellationTokenSource".
Trigger profile: a FLAPPING server (frequent plugin-hub reconnect cycles, e.g. the standalone HTTP
server driven per-call) so that domain reloads landed while a reconnect was in flight. A stably
connected session rarely hits it — which is why a month passed clean.

**Root causes (all in `WebSocketTransportClient.cs`):**
1. `HandleSocketClosureAsync` spawned `AttemptReconnectAsync` **fire-and-forget** (`_ = Task.Run`),
   so `ForceStop` never waited for it, `_shuttingDown` didn't stop it mid-cycle, and
   `EstablishConnectionAsync` had no teardown gate — a reconnect could re-create `_socket` AFTER
   ForceStop nulled it and enter the domain unload as a live untracked worker (the wedge).
2. Unguarded `_connectionCts.Dispose()` in `EstablishConnectionAsync`/`StopConnectionLoopsAsync`
   raced ForceStop's disposal of the same CTS (the double-dispose fingerprint), and
   `HandleSocketClosureAsync` read the `_lifecycleCts` FIELD after taking a null-safe local (NRE race).
3. `ForceStop`'s `finally { _shuttingDown = false; }` re-opened the reconnect gate even when a worker
   had MISSED the 800ms exit cap — letting the straggler resurrect the connection inside the freeze.

**Fix (client):** `_reconnectTask` field — tracked, gated (`_shuttingDown` checks at reconnect loop
steps + `EstablishConnectionAsync` entry), waited on in ForceStop alongside receive/keep-alive, with a
which-path-hung log line; swap-then-dispose guarded CTS disposal everywhere; lifecycle-local token for
the reconnect spawn; the teardown latch now lifts ONLY when all workers provably exited (or on the next
deliberate `StartAsync`, which clears it after a real awaited stop).

**Fix (structural, `HttpBridgeReloadHandler.cs`):** EARLY teardown — `CompilationPipeline
.compilationStarted` and `playModeStateChanged(ExitingEditMode)` now set the resume flag + ForceStop
SECONDS before the freeze window, so workers unwind on a live editor; `beforeAssemblyReload` remains as
a no-op belt. The early path also arms `ResumeTick` in the current domain (guarded by
`_resumeTickArmed`) so a compile that never reloads still gets its bridge back; resume is idempotent
(flag + IsRunning + coalesced StartAsync).

**Merge guidance:** on plugin update, preserve (a) the `_reconnectTask` tracking + `_shuttingDown`
gates, (b) guarded CTS disposal, (c) the early-teardown subscriptions in the reload handler. If
upstream reworks the transport, re-derive from first principles: NO bridge worker may be alive or
able to (re)connect once a domain reload is possible, and teardown must happen before the freeze.
Patch file: `local-patches/0003-teardown-race-early-teardown.patch` (diff vs `local-patches/backup-0003/`).

**0003 round 2 (same day):** live verification caught a flaw in round 1 — the early-armed resume tick
fired in the "not busy" GAP between compile-finish and reload-begin and RESURRECTED the bridge right
before the freeze (Editor.log showed `StartAsync` frames at teardown time). Fixes: (1) the early path
arms a **GracedResumeTick** requiring 5s of SUSTAINED idle (an imminent reload always wins and wipes the
delegate; only genuinely reload-less compiles — e.g. compile errors — resume); (2) the
`beforeAssemblyReload` belt calls ForceStop **unconditionally** (a mid-connect resume StartAsync is not
"IsRunning" yet but must still be latched out of the freeze); (3) the last unguarded CTS dispose
(`StopAsync`) got the swap-then-dispose guard.

**0003 round 3 (same day):** the 8-cycle hot stress passed clean, then the FIRST reload after ~15 min
of idle wedged — idle = the connection flaps, and round 1's `if (!IsRunning) return;` in EarlyTeardown
skipped the latch for a client that was MID-RECONNECT (not "running", but very much alive), letting it
spawn fresh loops into the freeze. Fixes: (1) `ForceStop` (client + TransportManager) now returns
whether ANY live work existed at entry (socket/loops/reconnect) and EarlyTeardown latches
unconditionally, scheduling a resume iff `wasRunning || hadWork`; (2) the ForceStop which-path-hung
summary + EarlyTeardown line now log UNCONDITIONALLY (main-thread only, so 0002's no-background-logging
rule holds) — the round-2 diagnostics were invisible behind debug verbosity.

**0003 residual observation (2026-08-12, post-round-3):** one further reload stall with a DIFFERENT
signature — **0% CPU** (not the 100% spin), stuck after "Begin MonoManager ReloadAssembly" while the
new always-on summary proved the plugin teardown fully clean ("loops exited within cap = True,
receive=done, keepAlive=done, reconnect=-"). Provoked by deleting a .cs mid-compile (stress-rig abort);
unusual Licensing::Client entitlement chatter right before the reload. NOT the plugin's threads — if
this recurs in normal use, investigate Unity-internal waits / modal dialogs, not this transport.
