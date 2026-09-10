# LOCAL PATCHES — read this before merging a new upstream (CoplayDev) version

This fork (`FatihDikmen-GameDev/unity-mcp-upgraded`) carries local fixes that are **not** in the
CoplayDev `unity-mcp` upstream. When you pull/merge a new upstream version, **go through each patch
below and decide keep-or-drop** using its "On the next upstream merge" instructions.

> **AI update agent:** treat this file as the source of truth for local deviations. For every patch:
> 1. Look at the referenced file/function in the *incoming upstream* version.
> 2. If upstream now fixes the same problem itself → **drop** the local patch (take upstream, remove the entry here).
> 3. If not → **re-apply** the patch (`git apply local-patches/<file>.patch`; if line numbers drifted, apply the change by hand as described).
> 4. Never silently discard a patch whose problem is still present upstream.

---

## 0001 — WebSocket bridge wedges domain reloads (two-part fix: receive-loop spin + ForceStop join)

- **Patch file:** `local-patches/0001-fix-receiveloop-null-socket-spin.patch` (now 3 hunks — both parts below)
- **File / functions:** `MCPForUnity/Editor/Services/Transport/Transports/WebSocketTransportClient.cs`
  → `ReceiveLoopAsync` (part 1) **and** `ForceStop` (part 2)
- **Base version when written:** `v10.1.0-18-g4c939496` (part 1 2026-07-18; part 2 added same day)
- **Severity:** high — can hang the Unity Editor indefinitely.
- **Note:** part 1 (the null-socket break) alone did NOT stop the wedge in practice — it narrows the
  spin window but `ForceStop` still abandoned the receive thread. **Part 2 (below) is the actual fix.**

### The bug
`ReceiveLoopAsync` did `if (message == null) { continue; }`. But `ReceiveMessageAsync` returns `null`
**synchronously** (no `await`) when `_socket == null` (socket torn down/disposed). So once the socket
is gone while the loop's cancellation token hasn't flipped yet, the loop becomes a **tight infinite
loop with no yield → 100% of one CPU core, no logging.** During a domain reload,
`HttpBridgeReloadHandler.OnBeforeAssemblyReload` calls `transport.ForceStop(Http)` *synchronously*,
which races the receive loop's cancellation and hits exactly this window. The spinning background
thread then prevents the domain reload from ever completing → the editor is stuck at
"Reload Script Assemblies (busy for NN:NN)" at 100% CPU forever. Reliably triggered by driving many
reloads through the MCP bridge (`refresh_unity` with force/compile).

**Signature to recognize it:** Unity pinned at 100% of one core, `Editor.log` frozen (no growth),
last log lines are `WebSocketTransportClient`, title stuck on "Reload Script Assemblies (busy…)".
(This is distinct from the normal slow-but-progressing reload, where the log keeps growing.)

### The fix — part 1 (`ReceiveLoopAsync`, the null-socket break)
On a `null` message, exit the loop instead of tight-`continue`ing: if `_socket == null` **or** the socket
is not `WebSocketState.Open`, `break`. Deliberately do **not** trigger a reconnect from this path — it would
race the teardown; genuine reconnects are already handled by the Close-frame branch in `ReceiveMessageAsync`
and the `WebSocketException`/`Exception` catches in the loop. Only a live socket (rare empty frame) keeps looping.
This makes the loop *exit promptly* once the socket is gone — a prerequisite for part 2's bounded wait.

### The fix — part 2 (`ForceStop`, synchronously join the background loops) — THE ACTUAL FIX
The real wedge: `ForceStop` cancelled the token + aborted the socket but then just set
`_receiveTask = null` / `_keepAliveTask = null` — it **abandoned the still-running receive thread without
waiting for it to exit** and disposed the lifecycle CTS out from under it. `beforeAssemblyReload` calls
`ForceStop` *synchronously*, so returning while that thread is alive means Unity freezes the world for the
domain reload with a running thread → hang at "Reload Script Assemblies" at 100% CPU forever.

Fix: after cancelling the CTS + aborting the socket (which, with part 1, makes both loops unwind in ~ms),
**`Task.WaitAll(new[]{ _receiveTask, _keepAliveTask }, TimeSpan.FromSeconds(2))` BEFORE disposing the CTS
and nulling the fields.** The 2s cap is a safety net (a loop stuck mid main-thread marshal degrades to the
old abandon behaviour instead of blocking the reload forever). Safe from deadlock because during ForceStop
the lifecycle token is already cancelled, so `HandleSocketClosureAsync` early-returns without a reconnect or
main-thread marshal — the loops just exit.

### On the next upstream merge
- **Check both** `ReceiveLoopAsync`'s `if (message == null)` branch **and** `ForceStop` in the incoming version.
- **Drop this patch** only if upstream now (a) breaks/checks `_socket` in the null branch AND (b) `ForceStop`
  (or the reload handler) **waits for the receive/keepalive tasks to complete** before the synchronous reload
  proceeds. If upstream still nulls the tasks without awaiting them, the wedge is still present — **keep the patch.**
- **Otherwise re-apply:** `git apply local-patches/0001-fix-receiveloop-null-socket-spin.patch`. If context
  drifted, apply by hand: (1) the `_socket == null || State != Open` break in the `message == null` branch;
  (2) the bounded `Task.WaitAll` of `_receiveTask`/`_keepAliveTask` in `ForceStop`, placed AFTER the socket
  abort and BEFORE the `_lifecycleCts?.Dispose()`.

### Upstream contribution (retire this patch permanently)
This is a genuine upstream bug. Consider opening a PR to `CoplayDev/unity-mcp` with this change so it
lands in their next release — then this local patch can be removed entirely. (A related mitigation, not
a fix: `MCPForUnity.AutoStartOnLoad`-off does **not** prevent it, because `HttpBridgeReloadHandler`
resumes the bridge after reloads whenever a session was active, independent of that pref.)

---

## 0002 — Reload STILL wedges when the reload is triggered BY a bridge command (silence the teardown unwind)

- **Patch file:** `local-patches/0002-silence-teardown-logging.patch`
  (generated as full working-tree diff vs upstream base, so it **contains 0001's hunks too** — 0001 was
  never committed; applying 0002 to a clean upstream file yields 0001+0002 together.)
- **File / functions:** `MCPForUnity/Editor/Services/Transport/Transports/WebSocketTransportClient.cs`
  → `HandleSocketClosureAsync`, `HandleExecuteAsync`, `ReceiveLoopAsync` (catches), `KeepAliveLoopAsync` (catch)
- **Base version when written:** `v10.1.0-18-g4c939496` (2026-07-19)
- **Severity:** high — hangs the editor exactly like 0001, ~every other reload when reloads are driven
  through the bridge (`refresh_unity` force/compile, `manage_editor play/stop`).

### The bug
With 0001 in place, the wedge recurred whenever the domain reload was **caused by the in-flight bridge
command itself**. Sequence: `HandleExecuteAsync` awaits the command on the main thread → the command
starts a recompile/play-mode switch → `beforeAssemblyReload` → `ForceStop` cancels tokens + **nulls the
socket** while the receive thread is still inside `HandleExecuteAsync` → its response send hits
`SendJsonAsync`'s `_socket == null` throw ("WebSocket is not initialised") → the unwind runs
`McpLog.Warn` + `HandleSocketClosureAsync`, which captured **`new StackTrace(true)` (loads file info!)
and `McpLog.Debug` BEFORE its `_shuttingDown` guard** — i.e. Unity `Debug.Log` + expensive stack
capture **from a background thread while the main thread is freezing the world for the reload**.
That background-thread logging during domain teardown is what wedges Mono's unload.
**Signature:** identical to 0001 (one core pegged, Editor.log frozen), but the last log entries are the
`SetException` stacks through `HandleExecuteAsync`/`HandleMessageAsync`.

### The fix — make the teardown unwind completely silent
1. `HandleSocketClosureAsync`: move the `_shuttingDown` + lifecycle-cancelled early-returns ABOVE the
   `StackTrace(true)` capture and `McpLog.Debug` (also read `_lifecycleCts` via a local — ForceStop
   nulls it concurrently).
2. `HandleExecuteAsync`: before sending the command response, bail out silently when
   `_shuttingDown || token.IsCancellationRequested || _socket == null` (nobody is listening; the
   server recovers via its own timeout).
3. `ReceiveLoopAsync` both catches + `KeepAliveLoopAsync` catch: when
   `_shuttingDown || token.IsCancellationRequested`, `break` silently — no `McpLog.Warn`, no
   `HandleSocketClosureAsync`.

### On the next upstream merge
- Check whether upstream's `HandleSocketClosureAsync` still logs/captures a stack before its teardown
  guards, and whether `HandleExecuteAsync` still unconditionally sends its response. If both are fixed
  upstream → drop; otherwise re-apply (by hand if drifted — the four spots above, guards first).
- Keep 0001's checks as documented in its own entry; the two patches are independent but ship in the
  same file.
