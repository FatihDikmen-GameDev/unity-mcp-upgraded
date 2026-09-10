using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Maintains a persistent WebSocket connection to the MCP server plugin hub.
    /// Handles registration, keep-alives, and command dispatch back into Unity via
    /// <see cref="TransportCommandDispatcher"/>.
    /// </summary>
    public class WebSocketTransportClient : IMcpTransportClient, IDisposable
    {
        private const string TransportDisplayName = "websocket";
        private static readonly TimeSpan[] ReconnectSchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };
        private static readonly TimeSpan ReconnectTailInterval = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);

        private readonly IToolDiscoveryService _toolDiscoveryService;
        private ClientWebSocket _socket;
        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _connectionCts;
        private Task _receiveTask;
        private Task _keepAliveTask;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private Uri _endpointUri;
        private string _sessionId;
        private string _projectHash;
        private string _projectName;
        private string _projectPath;
        private string _unityVersion;
        private TimeSpan _keepAliveInterval = DefaultKeepAliveInterval;
        private TimeSpan _socketKeepAliveInterval = DefaultKeepAliveInterval;
        private volatile bool _isConnected;
        private int _isReconnectingFlag;
        private TransportState _state = TransportState.Disconnected(TransportDisplayName, "Transport not started");
        private string _apiKey;
        private bool _disposed;
        // Set for the duration of a synchronous ForceStop (domain-reload teardown). While set, NO new
        // background loops or reconnects may start — otherwise a reconnect racing the teardown spawns an
        // untracked receive thread that ForceStop never waits for, and that orphaned thread is what hangs
        // the domain reload at 100% CPU. Volatile: read/written across the main + background threads.
        private volatile bool _shuttingDown;

        public WebSocketTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            _toolDiscoveryService = toolDiscoveryService;
        }

        public bool IsConnected => _isConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;

        private Task<List<ToolMetadata>> GetEnabledToolsOnMainThreadAsync(CancellationToken token)
        {
            return TransportCommandDispatcher.RunOnMainThreadAsync(
                () => _toolDiscoveryService?.GetEnabledTools() ?? new List<ToolMetadata>(),
                token);
        }

        public async Task<bool> StartAsync()
        {
            // Capture identity values on the main thread before any async context switching
            _projectName = ProjectIdentityUtility.GetProjectName();
            _projectHash = ProjectIdentityUtility.GetProjectHash();
            _unityVersion = Application.unityVersion;
            _apiKey = HttpEndpointUtility.IsRemoteScope()
                ? EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty)
                : string.Empty;

            if (HttpEndpointUtility.IsRemoteScope()
                && !HttpEndpointUtility.IsCurrentRemoteUrlAllowed(out string remoteUrlError))
            {
                string message = remoteUrlError ?? "HTTP Remote URL is not allowed by current security settings.";
                _state = TransportState.Disconnected(TransportDisplayName, message);
                McpLog.Error($"[WebSocket] {message}");
                return false;
            }

            // Get project root path (strip /Assets from dataPath) for focus nudging
            string dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string normalized = dataPath.TrimEnd('/', '\\');
                if (string.Equals(System.IO.Path.GetFileName(normalized), "Assets", StringComparison.Ordinal))
                {
                    _projectPath = System.IO.Path.GetDirectoryName(normalized) ?? normalized;
                }
                else
                {
                    _projectPath = normalized;  // Fallback if path doesn't end with Assets
                }
            }

            await StopAsync();

            _lifecycleCts = new CancellationTokenSource();
            _endpointUri = BuildWebSocketUri(HttpEndpointUtility.GetBaseUrl());
            _sessionId = null;

            if (!await EstablishConnectionAsync(_lifecycleCts.Token))
            {
                await StopAsync();
                return false;
            }

            // State is connected but session ID might be pending until 'registered' message
            _state = TransportState.Connected(TransportDisplayName, sessionId: "pending", details: _endpointUri.ToString());
            _isConnected = true;
            return true;
        }

        public async Task StopAsync()
        {
            if (_lifecycleCts == null)
            {
                return;
            }

            try
            {
                _lifecycleCts.Cancel();
            }
            catch { }

            await StopConnectionLoopsAsync().ConfigureAwait(false);

            if (_socket != null)
            {
                try
                {
                    if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { }
                finally
                {
                    _socket.Dispose();
                    _socket = null;
                }
            }

            _isConnected = false;
            _state = TransportState.Disconnected(TransportDisplayName);

            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        /// <summary>
        /// Synchronous teardown for use in beforeAssemblyReload where async is not possible.
        /// Skips the graceful WebSocket close handshake and just disposes resources immediately.
        /// The server handles ungraceful disconnects via its ping timeout.
        /// </summary>
        public void ForceStop()
        {
            // Latch teardown FIRST so no reconnect/loop can start while we unwind (see _shuttingDown).
            _shuttingDown = true;
            McpLog.Info("[WebSocket] ForceStop: begin", false);
            try
            {
                // 1) Cancel the tokens the loops observe. This is what SHOULD unblock a receive parked in
                //    ClientWebSocket.ReceiveAsync (its cancellation callback aborts the socket).
                try { _lifecycleCts?.Cancel(); } catch { }
                try { _connectionCts?.Cancel(); } catch { }

                // 2) Abort AND Dispose the socket, nulling the field first so a looping thread that
                //    re-enters ReceiveMessageAsync sees null and bails. Dispose closes the underlying
                //    connection, forcing an in-progress native read to return even if (2021.3 Mono)
                //    token cancellation alone doesn't interrupt it — this is the belt to the token's braces.
                var sock = _socket;
                _socket = null;
                if (sock != null)
                {
                    try { sock.Abort(); } catch { }
                    try { sock.Dispose(); } catch { }
                }
                McpLog.Info("[WebSocket] ForceStop: tokens cancelled + socket disposed", false);

                // 3) Give the loops a SHORT, bounded chance to observe cancellation and exit. Kept short
                //    on purpose: this runs on the main thread inside beforeAssemblyReload, and a
                //    main-thread-marshalling continuation cannot run while we block here — a long wait
                //    would risk the classic reload deadlock. With the break-on-dead-socket loop exit and
                //    the socket disposal above, both loops complete in ~ms; the cap only smooths timing.
                //    If a loop is genuinely wedged (native read that ignored both cancel + dispose), the
                //    log line below reports done=false so we know the hang is below the managed layer.
                Task receive = _receiveTask, keepAlive = _keepAliveTask;
                try
                {
                    var pending = new List<Task>(2);
                    if (receive != null) pending.Add(receive);
                    if (keepAlive != null) pending.Add(keepAlive);
                    if (pending.Count > 0)
                    {
                        bool done = Task.WaitAll(pending.ToArray(), TimeSpan.FromMilliseconds(800));
                        McpLog.Info($"[WebSocket] ForceStop: loops exited within cap = {done}", false);
                    }
                }
                catch { /* AggregateException(TaskCanceled/faulted) from the unwinding loops is expected. */ }

                try { _connectionCts?.Dispose(); } catch { }
                _connectionCts = null;
                _receiveTask = null;
                _keepAliveTask = null;
                Interlocked.Exchange(ref _isReconnectingFlag, 0);
                _isConnected = false;
                _state = TransportState.Disconnected(TransportDisplayName);

                try { _lifecycleCts?.Dispose(); } catch { }
                _lifecycleCts = null;
                McpLog.Info("[WebSocket] ForceStop: done", false);
            }
            finally
            {
                // Clear the latch so a later (manual or post-reload) reconnect can start loops again.
                _shuttingDown = false;
            }
        }

        public async Task<bool> VerifyAsync()
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                return false;
            }

            if (_lifecycleCts == null)
            {
                return false;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await SendPongAsync(timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Verify ping failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Ensure background loops are stopped before disposing shared resources
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Dispose failed to stop cleanly: {ex.Message}");
            }

            _sendLock?.Dispose();
            _socket?.Dispose();
            _lifecycleCts?.Dispose();
            _disposed = true;
        }

        private async Task<bool> EstablishConnectionAsync(CancellationToken token)
        {
            await StopConnectionLoopsAsync().ConfigureAwait(false);

            _connectionCts?.Dispose();
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken connectionToken = _connectionCts.Token;

            Uri originalEndpoint = _endpointUri;
            Uri connectedEndpoint = null;
            Exception lastConnectError = null;

            foreach (Uri candidate in BuildConnectionCandidateUris(originalEndpoint))
            {
                connectionToken.ThrowIfCancellationRequested();

                _socket?.Dispose();
                _socket = new ClientWebSocket();
                _socket.Options.KeepAliveInterval = _socketKeepAliveInterval;

                // Add API key header if configured (for remote-hosted mode)
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    _socket.Options.SetRequestHeader(AuthConstants.ApiKeyHeader, _apiKey);
                }

                try
                {
                    await _socket.ConnectAsync(candidate, connectionToken).ConfigureAwait(false);
                    connectedEndpoint = candidate;
                    break;
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastConnectError = ex;
                    McpLog.Debug($"[WebSocket] Connect failed for {candidate}: {ex.Message}");
                }
            }

            if (connectedEndpoint == null)
            {
                string errorMsg = "Connection failed. Check that the server URL is correct, the server is running, and your API key (if required) is valid.";
                McpLog.Error($"[WebSocket] {errorMsg} (Detail: {lastConnectError?.Message ?? "Unknown error"})");
                _state = TransportState.Disconnected(TransportDisplayName, errorMsg);
                return false;
            }

            if (!string.Equals(connectedEndpoint.Host, originalEndpoint.Host, StringComparison.OrdinalIgnoreCase))
            {
                McpLog.Warn($"[WebSocket] Connected via fallback host '{connectedEndpoint.Host}' after '{originalEndpoint.Host}' failed.");
                _endpointUri = connectedEndpoint;
            }

            StartBackgroundLoops(connectionToken);

            try
            {
                await SendRegisterAsync(connectionToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string regMsg = $"Registration with server failed: {ex.Message}";
                McpLog.Error($"[WebSocket] {regMsg}");
                _state = TransportState.Disconnected(TransportDisplayName, regMsg);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Stops the connection loops and disposes of the connection CTS.
        /// Particularly useful when reconnecting, we want to ensure that background loops are cancelled correctly before starting new oens
        /// </summary>
        /// <param name="awaitTasks">Whether to await the receive and keep alive tasks before disposing.</param>
        private async Task StopConnectionLoopsAsync(bool awaitTasks = true)
        {
            if (_connectionCts != null && !_connectionCts.IsCancellationRequested)
            {
                try { _connectionCts.Cancel(); } catch { }
            }

            if (_receiveTask != null)
            {
                if (awaitTasks)
                {
                    try { await _receiveTask.ConfigureAwait(false); } catch { }
                    _receiveTask = null;
                }
                else if (_receiveTask.IsCompleted)
                {
                    _receiveTask = null;
                }
            }

            if (_keepAliveTask != null)
            {
                if (awaitTasks)
                {
                    try { await _keepAliveTask.ConfigureAwait(false); } catch { }
                    _keepAliveTask = null;
                }
                else if (_keepAliveTask.IsCompleted)
                {
                    _keepAliveTask = null;
                }
            }

            if (_connectionCts != null)
            {
                _connectionCts.Dispose();
                _connectionCts = null;
            }
        }

        private void StartBackgroundLoops(CancellationToken token)
        {
            // Never spawn loops while a synchronous teardown is in flight (domain-reload ForceStop) — a
            // reconnect racing the teardown would otherwise create an untracked receive thread that hangs
            // the reload.
            if (_shuttingDown) return;
            if ((_receiveTask != null && !_receiveTask.IsCompleted) || (_keepAliveTask != null && !_keepAliveTask.IsCompleted))
            {
                return;
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
            _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token), CancellationToken.None);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string message = await ReceiveMessageAsync(token).ConfigureAwait(false);
                    if (message == null)
                    {
                        // A null message means the socket is gone/closing (or, rarely, an empty frame).
                        // NEVER tight-`continue` on a dead socket: ReceiveMessageAsync returns null
                        // SYNCHRONOUSLY when _socket == null, so `continue` would spin this loop at 100%
                        // CPU with no yield — and during a domain reload's synchronous ForceStop the socket
                        // is torn down mid-loop, hitting exactly that race and wedging the reload. Just EXIT
                        // the loop on any dead/closing socket. We deliberately do NOT trigger a reconnect
                        // from here (that would race the teardown) — genuine reconnects are already handled
                        // by the Close-frame branch in ReceiveMessageAsync and the WebSocketException/
                        // Exception catches below. Only a live socket (rare empty frame) keeps looping.
                        if (_socket == null || _socket.State != WebSocketState.Open)
                        {
                            break;
                        }
                        continue;
                    }
                    await HandleMessageAsync(message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException wse)
                {
                    // Local patch 0002: during teardown exit SILENTLY — background-thread logging
                    // while the main thread freezes the world for a reload wedges the editor.
                    if (_shuttingDown || token.IsCancellationRequested) break;
                    McpLog.Warn($"[WebSocket] Receive loop error: {wse.Message}");
                    await HandleSocketClosureAsync(wse.Message).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    // Local patch 0002: see above — silent exit during teardown.
                    if (_shuttingDown || token.IsCancellationRequested) break;
                    McpLog.Warn($"[WebSocket] Unexpected receive error: {ex.Message}");
                    await HandleSocketClosureAsync(ex.Message).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken token)
        {
            if (_socket == null)
            {
                return null;
            }

            byte[] rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            var buffer = new ArraySegment<byte>(rentedBuffer);
            using var ms = new MemoryStream(8192);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleSocketClosureAsync(result.CloseStatusDescription ?? "Server closed connection").ConfigureAwait(false);
                        return null;
                    }

                    if (result.Count > 0)
                    {
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                if (ms.Length == 0)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(ms.ToArray());
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private async Task HandleMessageAsync(string message, CancellationToken token)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(message);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Invalid JSON payload: {ex.Message}");
                return;
            }

            string messageType = payload.Value<string>("type") ?? string.Empty;

            switch (messageType)
            {
                case "welcome":
                    ApplyWelcome(payload);
                    break;
                case "registered":
                    await HandleRegisteredAsync(payload, token).ConfigureAwait(false);
                    break;
                case "execute":
                    await HandleExecuteAsync(payload, token).ConfigureAwait(false);
                    break;
                case "ping":
                    await SendPongAsync(token).ConfigureAwait(false);
                    break;
                default:
                    // No-op for unrecognised types (keep-alives, telemetry, etc.)
                    break;
            }
        }

        private void ApplyWelcome(JObject payload)
        {
            int? keepAliveSeconds = payload.Value<int?>("keepAliveInterval");
            if (keepAliveSeconds.HasValue && keepAliveSeconds.Value > 0)
            {
                _keepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds.Value);
                _socketKeepAliveInterval = _keepAliveInterval;
            }

            int? serverTimeoutSeconds = payload.Value<int?>("serverTimeout");
            if (serverTimeoutSeconds.HasValue)
            {
                int sourceSeconds = keepAliveSeconds ?? serverTimeoutSeconds.Value;
                int safeSeconds = Math.Max(5, Math.Min(serverTimeoutSeconds.Value, sourceSeconds));
                _socketKeepAliveInterval = TimeSpan.FromSeconds(safeSeconds);
            }
        }

        private async Task HandleRegisteredAsync(JObject payload, CancellationToken token)
        {
            string newSessionId = payload.Value<string>("session_id");
            if (!string.IsNullOrEmpty(newSessionId))
            {
                _sessionId = newSessionId;
                ProjectIdentityUtility.SetSessionId(_sessionId);
                _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                McpLog.Info($"[WebSocket] Registered with session ID: {_sessionId}", false);

                await SendRegisterToolsAsync(token).ConfigureAwait(false);
            }
        }

        private async Task SendRegisterToolsAsync(CancellationToken token)
        {
            if (_toolDiscoveryService == null) return;

            token.ThrowIfCancellationRequested();
            var tools = await GetEnabledToolsOnMainThreadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            McpLog.Info($"[WebSocket] Preparing to register {tools.Count} tool(s) with the bridge.", false);
            var toolsArray = new JArray();

            foreach (var tool in tools)
            {
                var toolObj = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["structured_output"] = tool.StructuredOutput,
                    ["requires_polling"] = tool.RequiresPolling,
                    ["poll_action"] = tool.PollAction ?? "status",
                    ["max_poll_seconds"] = tool.MaxPollSeconds,
                    ["group"] = string.IsNullOrWhiteSpace(tool.Group) ? "core" : tool.Group
                };

                var paramsArray = new JArray();
                if (tool.Parameters != null)
                {
                    foreach (var p in tool.Parameters)
                    {
                        paramsArray.Add(new JObject
                        {
                            ["name"] = p.Name,
                            ["description"] = p.Description,
                            ["type"] = p.Type,
                            ["required"] = p.Required,
                            ["default_value"] = p.DefaultValue
                        });
                    }
                }
                toolObj["parameters"] = paramsArray;
                toolsArray.Add(toolObj);
            }

            var payload = new JObject
            {
                ["type"] = "register_tools",
                ["tools"] = toolsArray
            };

            await SendJsonAsync(payload, token).ConfigureAwait(false);
            McpLog.Info($"[WebSocket] Sent {tools.Count} tools registration", false);
        }

        public async Task ReregisterToolsAsync()
        {
            if (!IsConnected || _lifecycleCts == null)
            {
                McpLog.Warn("[WebSocket] Cannot reregister tools: not connected");
                return;
            }

            try
            {
                await SendRegisterToolsAsync(_lifecycleCts.Token).ConfigureAwait(false);
                McpLog.Info("[WebSocket] Tool reregistration completed", false);
            }
            catch (System.OperationCanceledException)
            {
                McpLog.Warn("[WebSocket] Tool reregistration cancelled");
            }
            catch (System.Exception ex)
            {
                McpLog.Error($"[WebSocket] Tool reregistration failed: {ex.Message}");
            }
        }

        private async Task HandleExecuteAsync(JObject payload, CancellationToken token)
        {
            string commandId = payload.Value<string>("id");
            string commandName = payload.Value<string>("name");
            JObject parameters = payload.Value<JObject>("params") ?? new JObject();
            int timeoutSeconds = payload.Value<int?>("timeout") ?? (int)DefaultCommandTimeout.TotalSeconds;

            if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(commandName))
            {
                McpLog.Warn("[WebSocket] Invalid execute payload (missing id or name)");
                return;
            }

            var commandEnvelope = new JObject
            {
                ["type"] = commandName,
                ["params"] = parameters
            };

            string responseJson;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
                responseJson = await TransportCommandDispatcher.ExecuteCommandJsonAsync(commandEnvelope.ToString(Formatting.None), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"Command '{commandName}' timed out after {timeoutSeconds} seconds"
                });
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message
                });
            }

            JToken resultToken;
            try
            {
                resultToken = JToken.Parse(responseJson);
            }
            catch
            {
                resultToken = new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Invalid response payload"
                };
            }

            var responsePayload = new JObject
            {
                ["type"] = "command_result",
                ["id"] = commandId,
                ["result"] = resultToken
            };

            // Local patch 0002: when the command itself triggered a domain reload (refresh/compile,
            // play mode), ForceStop tears the socket down while we are still here. Nobody is left
            // to answer — and touching the nulled/disposed socket throws an exception whose
            // background-thread unwind (logging during the world-freeze) wedges the reload.
            // Drop the response silently; the server recovers via its own timeout.
            if (_shuttingDown || token.IsCancellationRequested || _socket == null)
            {
                return;
            }

            await SendJsonAsync(responsePayload, token).ConfigureAwait(false);
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_keepAliveInterval, token).ConfigureAwait(false);
                    if (_socket == null || _socket.State != WebSocketState.Open)
                    {
                        break;
                    }
                    await SendPongAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Local patch 0002: silent exit during teardown (see ReceiveLoopAsync).
                    if (_shuttingDown || token.IsCancellationRequested) break;
                    McpLog.Warn($"[WebSocket] Keep-alive failed: {ex.Message}");
                    await HandleSocketClosureAsync(ex.Message).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task SendRegisterAsync(CancellationToken token)
        {
            var registerPayload = new JObject
            {
                ["type"] = "register",
                // session_id is now server-authoritative; omitted here or sent as null
                ["project_name"] = _projectName,
                ["project_hash"] = _projectHash,
                ["unity_version"] = _unityVersion,
                ["project_path"] = _projectPath
            };

            await SendJsonAsync(registerPayload, token).ConfigureAwait(false);
        }

        private Task SendPongAsync(CancellationToken token)
        {
            var payload = new JObject
            {
                ["type"] = "pong",
                ["session_id"] = _sessionId  // Include session ID for server-side tracking
            };
            return SendJsonAsync(payload, token);
        }

        private async Task SendJsonAsync(JObject payload, CancellationToken token)
        {
            if (_socket == null)
            {
                throw new InvalidOperationException("WebSocket is not initialised");
            }

            string json = payload.ToString(Formatting.None);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var buffer = new ArraySegment<byte>(bytes);

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("WebSocket is not open");
                }

                await _socket.SendAsync(buffer, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task HandleSocketClosureAsync(string reason)
        {
            // ORDER MATTERS (local patch 0002): the teardown guards must run BEFORE any logging
            // or stack capture. This method is reached from background threads; during a domain
            // reload the main thread is freezing the world, and a Debug.Log — let alone an
            // expensive StackTrace(true) with file info — from a background thread at that moment
            // is what wedges "Reload Script Assemblies" at 100% CPU (see LOCAL_PATCHES.md 0002).

            // During a synchronous teardown, never kick off a reconnect (it would race ForceStop and
            // spawn an untracked receive thread that wedges the domain reload).
            if (_shuttingDown) return;

            var lifecycle = _lifecycleCts; // local copy: ForceStop nulls the field concurrently
            if (lifecycle == null || lifecycle.IsCancellationRequested)
            {
                return;
            }

            // Capture stack trace for debugging disconnection triggers (safe here: not tearing down)
            var stackTrace = new System.Diagnostics.StackTrace(true);
            McpLog.Debug($"[WebSocket] HandleSocketClosureAsync called. Reason: {reason}\nStack trace:\n{stackTrace}");

            if (Interlocked.CompareExchange(ref _isReconnectingFlag, 1, 0) != 0)
            {
                return;
            }

            _isConnected = false;
            _state = _state.WithError(reason ?? "Connection closed");
            McpLog.Warn($"[WebSocket] Connection closed: {reason}");

            await StopConnectionLoopsAsync(awaitTasks: false).ConfigureAwait(false);

            _ = Task.Run(() => AttemptReconnectAsync(_lifecycleCts.Token), CancellationToken.None);
        }

        private async Task AttemptReconnectAsync(CancellationToken token)
        {
            try
            {
                await StopConnectionLoopsAsync().ConfigureAwait(false);

                foreach (TimeSpan delay in ReconnectSchedule)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }

                    if (await EstablishConnectionAsync(token).ConfigureAwait(false))
                    {
                        _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                        _isConnected = true;
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        return;
                    }
                }

                // Schedule exhausted — keep retrying every 30 s indefinitely so a transient
                // server outage longer than ~49 s doesn't leave the plugin permanently dead.
                McpLog.Warn($"[WebSocket] Initial reconnect schedule exhausted. Retrying every {ReconnectTailInterval.TotalSeconds}s until cancelled.");
                _state = _state.WithError($"Server unreachable – retrying every {ReconnectTailInterval.TotalSeconds} s");
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(ReconnectTailInterval, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    if (await EstablishConnectionAsync(token).ConfigureAwait(false))
                    {
                        _state = TransportState.Connected(TransportDisplayName, sessionId: _sessionId, details: _endpointUri.ToString());
                        _isConnected = true;
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        return;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnectingFlag, 0);
            }
        }

        private static Uri BuildWebSocketUri(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var httpUri))
            {
                throw new InvalidOperationException($"Invalid MCP base URL: {baseUrl}");
            }

            // Replace bind-only addresses for client connections
            // 0.0.0.0 and :: are only valid for server binding, not client connections
            string host = httpUri.Host;
            if (host == "0.0.0.0")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '127.0.0.1' for client connection.");
                host = "127.0.0.1";
            }
            else if (host == "::")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '::1' for client connection.");
                host = "::1";
            }

            var builder = new UriBuilder(httpUri)
            {
                Scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Host = host,
                Path = httpUri.AbsolutePath.TrimEnd('/') + "/hub/plugin"
            };

            return builder.Uri;
        }

        private static List<Uri> BuildConnectionCandidateUris(Uri endpointUri)
        {
            var candidates = new List<Uri>();
            if (endpointUri == null)
            {
                return candidates;
            }

            candidates.Add(endpointUri);

            if (!string.Equals(endpointUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            // Retry localhost using explicit loopback hosts to avoid DNS family ambiguity on some machines.
            TryAddCandidate(candidates, endpointUri, "127.0.0.1");
            TryAddCandidate(candidates, endpointUri, "::1");
            return candidates;
        }

        private static void TryAddCandidate(List<Uri> candidates, Uri template, string host)
        {
            try
            {
                var builder = new UriBuilder(template) { Host = host };
                Uri candidate = builder.Uri;
                foreach (Uri existing in candidates)
                {
                    if (Uri.Compare(existing, candidate, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        return;
                    }
                }
                candidates.Add(candidate);
            }
            catch
            {
                // Ignore malformed fallback candidate and continue with remaining options.
            }
        }
    }
}
