using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
#if MCP_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
#endif
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Drives the running game like a human tester: synthesises keyboard + mouse input
    /// (Input System + OS SendInput dual-path), fires uGUI EventSystem clicks, performs
    /// drags / scrolls, and polls UI state for assertions. Use this in play mode to
    /// automate test flows or with an LLM as the vision layer.
    ///
    /// In play mode, all calls run on the Unity main thread. Methods that internally Sleep
    /// (Wait, TapKey with hold) pause the game for the sleep duration — use Press+Release
    /// across separate MCP calls when you need the game to advance during a key hold.
    /// </summary>
    [McpForUnityTool("manage_play_test", AutoRegister = false, Group = "testing")]
    public static class ManagePlayTest
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return new ErrorResponse("Parameters cannot be null.");
            string action = @params["action"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action)) return new ErrorResponse("'action' is required.");

            try
            {
                switch (action)
                {
                    case "click_ui":
                        return ClickUI(@params["path"]?.ToString() ?? @params["target"]?.ToString());
                    case "drag_ui":
                        return DragUI(
                            @params["from"]?.ToString() ?? @params["path"]?.ToString() ?? @params["target"]?.ToString(),
                            @params["to"]?.ToString(),
                            @params["steps"]?.ToObject<int>() ?? 10,
                            @params["step_delay_ms"]?.ToObject<int>() ?? 16);
                    case "drag_from_to":
                        return DragFromTo(
                            @params["from_x"]?.ToObject<float>() ?? 0f,
                            @params["from_y"]?.ToObject<float>() ?? 0f,
                            @params["to_x"]?.ToObject<float>() ?? 0f,
                            @params["to_y"]?.ToObject<float>() ?? 0f,
                            @params["steps"]?.ToObject<int>() ?? 10,
                            @params["step_delay_ms"]?.ToObject<int>() ?? 16);
                    case "scroll_ui":
                        return ScrollUI(
                            @params["path"]?.ToString() ?? @params["target"]?.ToString(),
                            @params["delta_x"]?.ToObject<float>() ?? 0f,
                            @params["delta_y"]?.ToObject<float>() ?? -1f);
                    case "describe_ui":
                        return DescribeUI(@params["path"]?.ToString() ?? @params["target"]?.ToString());

                    case "press_key":
                        return PressKey(@params["key"]?.ToString(), @params["focus_game_view"]?.ToObject<bool>() ?? true);
                    case "release_key":
                        return ReleaseKey(@params["key"]?.ToString(), @params["focus_game_view"]?.ToObject<bool>() ?? true);
                    case "tap_key":
                        return TapKey(
                            @params["key"]?.ToString(),
                            @params["hold_ms"]?.ToObject<int>() ?? 40,
                            @params["focus_game_view"]?.ToObject<bool>() ?? true);
                    case "press_chord":
                        return PressChord(
                            (@params["keys"] as JArray)?.Select(t => t.ToString()).ToArray(),
                            @params["hold_ms"]?.ToObject<int>() ?? 50);
                    case "focus_game_view":
                        return FocusGameView();

                    case "click_at":
                        return ClickAt(
                            @params["x"]?.ToObject<float>() ?? 0f,
                            @params["y"]?.ToObject<float>() ?? 0f,
                            @params["button"]?.ToString() ?? "left");
                    case "move_mouse_to":
                        return MoveMouseTo(
                            @params["x"]?.ToObject<float>() ?? 0f,
                            @params["y"]?.ToObject<float>() ?? 0f);
                    case "mouse_down":
                        return MouseDown(
                            @params["x"]?.ToObject<float>() ?? 0f,
                            @params["y"]?.ToObject<float>() ?? 0f,
                            @params["button"]?.ToString() ?? "left");
                    case "mouse_up":
                        return MouseUp(
                            @params["x"]?.ToObject<float>() ?? 0f,
                            @params["y"]?.ToObject<float>() ?? 0f,
                            @params["button"]?.ToString() ?? "left");

                    case "wait_until_active":
                        return WaitUntilActive(
                            @params["path"]?.ToString() ?? @params["target"]?.ToString(),
                            @params["timeout_seconds"]?.ToObject<float>() ?? 5f,
                            @params["poll_ms"]?.ToObject<int>() ?? 50);
                    case "wait_until_inactive":
                        return WaitUntilInactive(
                            @params["path"]?.ToString() ?? @params["target"]?.ToString(),
                            @params["timeout_seconds"]?.ToObject<float>() ?? 5f,
                            @params["poll_ms"]?.ToObject<int>() ?? 50);
                    case "wait_until_text":
                        return WaitUntilText(
                            @params["path"]?.ToString() ?? @params["target"]?.ToString(),
                            @params["expected_substring"]?.ToString(),
                            @params["timeout_seconds"]?.ToObject<float>() ?? 5f,
                            @params["poll_ms"]?.ToObject<int>() ?? 100);
                    case "wait":
                        return Wait(@params["seconds"]?.ToObject<float>() ?? 0f);

                    default:
                        return new ErrorResponse(
                            $"Unknown action '{action}'. Valid: click_ui, drag_ui, drag_from_to, scroll_ui, describe_ui, " +
                            "press_key, release_key, tap_key, press_chord, focus_game_view, " +
                            "click_at, move_mouse_to, mouse_down, mouse_up, " +
                            "wait_until_active, wait_until_inactive, wait_until_text, wait.");
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[ManagePlayTest] Action '{action}' failed: {e}");
                return new ErrorResponse($"Internal error in '{action}': {e.Message}");
            }
        }

    public static object ClickUI(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
            return Err("pathOrName is required.");

        var go = FindByPath(pathOrName);
        if (go == null)
            return Err($"UI element not found: '{pathOrName}'.");

        if (!go.activeInHierarchy)
            return Err($"UI element '{go.name}' is inactive in hierarchy.");

        var es = EventSystem.current;
        if (es == null)
            return Err("No active EventSystem in the scene. Add an EventSystem (one is created automatically in play mode if any uGUI is present).");

        var canvas = go.GetComponentInParent<Canvas>();
        Camera eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

        Vector2 screenPos;
        var rt = go.transform as RectTransform;
        if (rt != null)
            screenPos = RectTransformUtility.WorldToScreenPoint(eventCamera, rt.position);
        else
            screenPos = new Vector2(Screen.width / 2f, Screen.height / 2f);

        var pointerData = new PointerEventData(es)
        {
            position = screenPos,
            button = PointerEventData.InputButton.Left,
            pressPosition = screenPos,
        };

        bool downHandled = ExecuteEvents.ExecuteHierarchy(go, pointerData, ExecuteEvents.pointerDownHandler) != null;
        bool upHandled = ExecuteEvents.ExecuteHierarchy(go, pointerData, ExecuteEvents.pointerUpHandler) != null;
        bool clickHandled = ExecuteEvents.ExecuteHierarchy(go, pointerData, ExecuteEvents.pointerClickHandler) != null;
        bool submitHandled = false;

        bool fellBackToOnClick = false;
        if (!clickHandled)
        {
            // No IPointerClickHandler — try Button.onClick directly (very common case for plain Buttons
            // that were wired through the inspector without a custom handler component).
            var btn = go.GetComponentInParent<Button>();
            if (btn != null && btn.interactable)
            {
                btn.onClick.Invoke();
                clickHandled = true;
                fellBackToOnClick = true;
            }
            else
            {
                submitHandled = ExecuteEvents.ExecuteHierarchy(go, pointerData, ExecuteEvents.submitHandler) != null;
            }
        }

        return new
        {
            success = true,
            message = $"Clicked '{go.name}'.",
            path = GetPath(go),
            screenPosition = new { x = screenPos.x, y = screenPos.y },
            downHandled,
            upHandled,
            clickHandled,
            submitHandled,
            fellBackToOnClick,
            isPlaying = Application.isPlaying,
        };
    }

    /// <summary>
    /// Press a key. Fires through TWO paths so it works for any game regardless of which input
    /// API the game reads:
    ///   1. Input System synthetic event (for code reading Keyboard.current.xKey or InputActions)
    ///   2. Win32 SendInput (for code reading legacy UnityEngine.Input.GetKey, plus everything else
    ///      since Unity's editor input pipeline ultimately reads OS keyboard messages).
    /// Briefly focuses the Unity main window so SendInput is delivered there. Pass focusGameView=false
    /// to skip focus-stealing if Unity is already the foreground app.
    /// </summary>
    public static object PressKey(string keyName, bool focusGameView = true)
    {
        if (focusGameView) FocusUnityForInput();

        bool osFired = OsKey(keyName, down: true, out string osErr);
        bool isFired = TryGetKeyControl(keyName, out var kc, out string isErr);
        if (isFired) WriteKey(kc, 1f);

        if (!osFired && !isFired)
            return Err($"PressKey('{keyName}') failed both paths. InputSystem: {isErr}. SendInput: {osErr}");

        return new { success = true, key = keyName, state = "pressed", inputSystem = isFired, sendInput = osFired };
    }

    public static object ReleaseKey(string keyName, bool focusGameView = true)
    {
        if (focusGameView) FocusUnityForInput();

        bool osFired = OsKey(keyName, down: false, out string osErr);
        bool isFired = TryGetKeyControl(keyName, out var kc, out string isErr);
        if (isFired) WriteKey(kc, 0f);

        if (!osFired && !isFired)
            return Err($"ReleaseKey('{keyName}') failed both paths. InputSystem: {isErr}. SendInput: {osErr}");

        return new { success = true, key = keyName, state = "released", inputSystem = isFired, sendInput = osFired };
    }

    /// <summary>
    /// Press, brief hold, release in one call. holdMs blocks the main thread, so the game
    /// pauses for that duration. Suitable for one-shot triggers (e.g. fire button). For
    /// movement-style sustained input or holds that need the game to advance (e.g. "Hold E
    /// for 1s to skip greeting"), use PressKey + separate MCP calls + ReleaseKey so frames
    /// can render between press and release.
    /// </summary>
    public static object TapKey(string keyName, int holdMs = 40, bool focusGameView = true)
    {
        if (focusGameView) FocusUnityForInput();

        bool osDown = OsKey(keyName, down: true, out string osErr1);
        bool isOk = TryGetKeyControl(keyName, out var kc, out string isErr);
        if (isOk) WriteKey(kc, 1f);

        if (!osDown && !isOk)
            return Err($"TapKey('{keyName}') failed both paths. InputSystem: {isErr}. SendInput: {osErr1}");

        if (holdMs > 0) Thread.Sleep(Mathf.Clamp(holdMs, 1, 5000));

        OsKey(keyName, down: false, out _);
        if (isOk) WriteKey(kc, 0f);

        return new { success = true, key = keyName, heldMs = holdMs, inputSystem = isOk, sendInput = osDown };
    }

    /// <summary>
    /// Focus the Unity Editor's GameView and main window so OS-level synthetic key events route
    /// to the running game. Briefly steals foreground from whatever was focused before
    /// (typically the Claude Code / MCP client terminal). Focus naturally lingers on Unity until
    /// the user clicks elsewhere; no automatic restore.
    /// </summary>
    public static object FocusGameView()
    {
        FocusUnityForInput();
        return new { success = true, focused = "Unity main window + GameView" };
    }

    /// <summary>
    /// Sleep the Unity main thread. In play mode the game pauses for the duration.
    /// Prefer letting MCP-call latency provide natural pacing instead of using this.
    /// </summary>
    public static object Wait(float seconds)
    {
        int ms = Mathf.Clamp(Mathf.RoundToInt(seconds * 1000f), 0, 30000);
        Thread.Sleep(ms);
        return new { success = true, waitedSeconds = ms / 1000f, isPlaying = Application.isPlaying };
    }

    /// <summary>Diagnostic: report whether a UI element exists, is active, and what handlers it has.</summary>
    public static object DescribeUI(string pathOrName)
    {
        var go = FindByPath(pathOrName);
        if (go == null) return Err($"UI element not found: '{pathOrName}'.");

        var btn = go.GetComponent<Button>();
        var img = go.GetComponent<Image>();
        var rt = go.transform as RectTransform;

        var components = go.GetComponents<Component>()
            .Where(c => c != null)
            .Select(c => c.GetType().Name)
            .ToArray();

        var canvas = go.GetComponentInParent<Canvas>();

        return new
        {
            success = true,
            path = GetPath(go),
            name = go.name,
            activeInHierarchy = go.activeInHierarchy,
            activeSelf = go.activeSelf,
            components,
            hasButton = btn != null,
            buttonInteractable = btn?.interactable,
            buttonOnClickCount = btn?.onClick.GetPersistentEventCount(),
            hasImage = img != null,
            rect = rt != null ? new
            {
                anchoredPos = new { x = rt.anchoredPosition.x, y = rt.anchoredPosition.y },
                sizeDelta = new { x = rt.sizeDelta.x, y = rt.sizeDelta.y },
                worldPos = new { x = rt.position.x, y = rt.position.y, z = rt.position.z },
            } : null,
            canvas = canvas != null ? new { name = canvas.name, renderMode = canvas.renderMode.ToString(), sortingOrder = canvas.sortingOrder } : null,
            isPlaying = Application.isPlaying,
        };
    }

    // ---------- drag ----------

    /// <summary>
    /// Drag from one UI element to another. Fires pointerDown → beginDrag → N dragHandler steps →
    /// endDrag → pointerUp → drop on whatever UI sits at the destination.
    /// </summary>
    public static object DragUI(string fromPath, string toPath, int steps = 10, int stepDelayMs = 16)
    {
        var fromGO = FindByPath(fromPath);
        var toGO = FindByPath(toPath);
        if (fromGO == null) return Err($"From element not found: '{fromPath}'.");
        if (toGO == null) return Err($"To element not found: '{toPath}'.");
        return DragInternal(GetScreenPos(fromGO), GetScreenPos(toGO), steps, stepDelayMs, fromGO);
    }

    /// <summary>Drag between explicit screen positions.</summary>
    public static object DragFromTo(float fromX, float fromY, float toX, float toY, int steps = 10, int stepDelayMs = 16)
    {
        return DragInternal(new Vector2(fromX, fromY), new Vector2(toX, toY), steps, stepDelayMs, null);
    }

    private static object DragInternal(Vector2 from, Vector2 to, int steps, int stepDelayMs, GameObject sourceHint)
    {
        var es = EventSystem.current;
        if (es == null) return Err("No active EventSystem.");

        steps = Mathf.Clamp(steps, 2, 200);
        stepDelayMs = Mathf.Clamp(stepDelayMs, 0, 200);

        var data = new PointerEventData(es)
        {
            position = from,
            pressPosition = from,
            button = PointerEventData.InputButton.Left,
            useDragThreshold = false,
        };

        // Find drag source: prefer caller hint, else raycast at start position.
        GameObject source = sourceHint;
        var firstHits = new List<RaycastResult>();
        if (source == null)
        {
            es.RaycastAll(data, firstHits);
            if (firstHits.Count == 0)
                return Err($"No UI element at source ({from.x:F0},{from.y:F0}).");
            source = firstHits[0].gameObject;
        }
        data.pointerPress = source;
        data.rawPointerPress = source;
        data.pointerDrag = source;

        ExecuteEvents.ExecuteHierarchy(source, data, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(source, data, ExecuteEvents.initializePotentialDrag);
        ExecuteEvents.ExecuteHierarchy(source, data, ExecuteEvents.beginDragHandler);

        Vector2 prev = from;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            Vector2 next = Vector2.LerpUnclamped(from, to, t);
            data.delta = next - prev;
            data.position = next;
            prev = next;
            ExecuteEvents.ExecuteHierarchy(source, data, ExecuteEvents.dragHandler);
            if (stepDelayMs > 0) Thread.Sleep(stepDelayMs);
        }

        ExecuteEvents.ExecuteHierarchy(source, data, ExecuteEvents.endDragHandler);
        ExecuteEvents.ExecuteHierarchy(source, data, ExecuteEvents.pointerUpHandler);

        // Drop on whatever UI is under the destination.
        var dropHits = new List<RaycastResult>();
        data.position = to;
        es.RaycastAll(data, dropHits);
        bool dropFired = false;
        string droppedOn = null;
        if (dropHits.Count > 0)
        {
            droppedOn = dropHits[0].gameObject.name;
            dropFired = ExecuteEvents.ExecuteHierarchy(dropHits[0].gameObject, data, ExecuteEvents.dropHandler) != null;
        }

        return new
        {
            success = true,
            source = source.name,
            from = new { x = from.x, y = from.y },
            to = new { x = to.x, y = to.y },
            steps,
            droppedOn,
            dropFired,
            isPlaying = Application.isPlaying,
        };
    }

    // ---------- scroll ----------

    /// <summary>
    /// Fire a scroll event on a UI element. deltaY is positive = up, negative = down,
    /// matching Unity's PointerEventData.scrollDelta convention (after Input System normalization,
    /// most ScrollRects also treat +y as scrolling content upward).
    /// </summary>
    public static object ScrollUI(string pathOrName, float deltaX = 0f, float deltaY = -1f)
    {
        var go = FindByPath(pathOrName);
        if (go == null) return Err($"Not found: '{pathOrName}'.");
        if (!go.activeInHierarchy) return Err($"'{go.name}' is inactive in hierarchy.");

        var es = EventSystem.current;
        if (es == null) return Err("No active EventSystem.");

        var data = new PointerEventData(es)
        {
            position = GetScreenPos(go),
            scrollDelta = new Vector2(deltaX, deltaY),
        };

        bool handled = ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.scrollHandler) != null;

        return new
        {
            success = true,
            target = go.name,
            delta = new { x = deltaX, y = deltaY },
            handled,
        };
    }

    // ---------- wait-until ----------

    public static object WaitUntilActive(string pathOrName, float timeoutSeconds = 5f, int pollMs = 50)
        => PollWait(timeoutSeconds, pollMs, () => {
            var go = FindByPath(pathOrName);
            return go != null && go.activeInHierarchy;
        }, $"WaitUntilActive('{pathOrName}')");

    public static object WaitUntilInactive(string pathOrName, float timeoutSeconds = 5f, int pollMs = 50)
        => PollWait(timeoutSeconds, pollMs, () => {
            var go = FindByPath(pathOrName);
            return go == null || !go.activeInHierarchy;
        }, $"WaitUntilInactive('{pathOrName}')");

    /// <summary>
    /// Wait until a TMP_Text or uGUI Text on the named GameObject contains <paramref name="expectedSubstring"/>.
    /// Case-sensitive substring match.
    /// </summary>
    public static object WaitUntilText(string pathOrName, string expectedSubstring, float timeoutSeconds = 5f, int pollMs = 100)
    {
        var tmpType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
        return PollWait(timeoutSeconds, pollMs, () => {
            var go = FindByPath(pathOrName);
            if (go == null) return false;
            if (tmpType != null)
            {
                var tmp = go.GetComponent(tmpType);
                if (tmp != null)
                {
                    var t = tmpType.GetProperty("text")?.GetValue(tmp) as string;
                    if (t != null && t.Contains(expectedSubstring)) return true;
                }
            }
            var txt = go.GetComponent<Text>();
            return txt != null && txt.text != null && txt.text.Contains(expectedSubstring);
        }, $"WaitUntilText('{pathOrName}', '{expectedSubstring}')");
    }

    /// <summary>
    /// Polling loop that ticks Unity's player loop between polls so the game can advance during
    /// play mode. Blocks the calling MCP request until the condition is met or the deadline passes.
    /// Use modest timeouts (≤10s); for longer waits, prefer splitting across multiple MCP calls.
    /// </summary>
    private static object PollWait(float timeoutSeconds, int pollMs, Func<bool> condition, string desc)
    {
        pollMs = Mathf.Clamp(pollMs, 5, 2000);
        timeoutSeconds = Mathf.Clamp(timeoutSeconds, 0.05f, 60f);
        float start = Time.realtimeSinceStartup;
        float deadline = start + timeoutSeconds;
        int polls = 0;
        while (Time.realtimeSinceStartup < deadline)
        {
            polls++;
            try
            {
                if (condition())
                    return new { success = true, condition = desc, elapsedSeconds = Time.realtimeSinceStartup - start, polls };
            }
            catch (Exception ex)
            {
                return new { success = false, condition = desc, message = $"Predicate threw: {ex.Message}", polls };
            }

            if (Application.isPlaying)
                EditorApplication.QueuePlayerLoopUpdate();
            Thread.Sleep(pollMs);
        }
        return new { success = false, condition = desc, message = "Timed out.", timeoutSeconds, polls };
    }

    // ---------- richer key sequences ----------

    /// <summary>
    /// Press multiple keys simultaneously (e.g. ["LeftCtrl","S"] for Ctrl+S), hold briefly, release.
    /// </summary>
    public static object PressChord(string[] keys, int holdMs = 50)
    {
#if MCP_INPUT_SYSTEM
        if (keys == null || keys.Length == 0) return Err("keys is required.");
        var controls = new List<KeyControl>();
        foreach (var k in keys)
        {
            if (!TryGetKeyControl(k, out var kc, out string err))
                return Err($"Chord key '{k}': {err}");
            controls.Add(kc);
        }
        foreach (var kc in controls) WriteKey(kc, 1f);
        Thread.Sleep(Mathf.Clamp(holdMs, 0, 5000));
        foreach (var kc in controls) WriteKey(kc, 0f);
        return new { success = true, chord = string.Join("+", keys), heldMs = holdMs };
#else
        return Err("PressChord requires the Input System package (com.unity.inputsystem).");
#endif
    }

    // ---------- internals ----------

#if MCP_INPUT_SYSTEM
    private static bool TryGetKeyControl(string keyName, out KeyControl keyControl, out string error)
    {
        keyControl = null;
        error = null;

        if (string.IsNullOrWhiteSpace(keyName))
        {
            error = "keyName is required.";
            return false;
        }

        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            error = "No active Keyboard device. Make sure the Input System has initialized (usually requires entering play mode at least once).";
            return false;
        }

        string normalized = keyName.Trim();
        if (!Enum.TryParse<Key>(normalized, true, out var keyEnum))
        {
            error = $"Unknown key '{keyName}'. Use Input System Key enum names (e.g. 'W', 'Space', 'LeftArrow', 'Escape', 'Digit1').";
            return false;
        }

        keyControl = keyboard[keyEnum];
        if (keyControl == null)
        {
            error = $"Keyboard has no control for key '{keyName}'.";
            return false;
        }
        return true;
    }

    private static void WriteKey(KeyControl control, float value)
    {
        // The event buffer must stay alive while QueueEvent + Update run, so process inside the using.
        using (StateEvent.From(control.device, out var eventPtr))
        {
            eventPtr.time = InputState.currentTime;
            control.WriteValueIntoEvent(value, eventPtr);
            InputSystem.QueueEvent(eventPtr);
            InputSystem.Update();
        }
    }
#else
    // Input System package not installed: PressKey/ReleaseKey/TapKey fall back to the OS SendInput
    // path alone; these stubs keep the dual-path call sites compiling.
    private static bool TryGetKeyControl(string keyName, out object keyControl, out string error)
    {
        keyControl = null;
        error = "Input System package (com.unity.inputsystem) is not installed.";
        return false;
    }

    private static void WriteKey(object control, float value) { }
#endif

    private static GameObject FindByPath(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName)) return null;

        // Full path: "Canvas/Panel/Button"
        if (pathOrName.Contains('/'))
        {
            var found = GameObject.Find(pathOrName);
            if (found != null) return found;
        }

        // Fallback: search every GameObject (including inactive) for an exact name match.
        var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>();
        return all.FirstOrDefault(g =>
            g.name == pathOrName &&
            g.hideFlags == HideFlags.None &&
            g.scene.IsValid());
    }

    private static Vector2 GetScreenPos(GameObject go)
    {
        var canvas = go.GetComponentInParent<Canvas>();
        Camera eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var rt = go.transform as RectTransform;
        if (rt != null)
            return RectTransformUtility.WorldToScreenPoint(eventCamera, rt.position);
        return new Vector2(Screen.width / 2f, Screen.height / 2f);
    }

    private static string GetPath(GameObject go)
    {
        if (go == null) return null;
        var sb = new StringBuilder(go.name);
        var t = go.transform.parent;
        while (t != null) { sb.Insert(0, t.name + "/"); t = t.parent; }
        return sb.ToString();
    }

    private static object Err(string message) => new { success = false, message };

    // ---------- OS-level mouse (Win32 SendInput) ----------

    /// <summary>
    /// Move the OS cursor to a screen position (in GameView pixel coords — the same coord space
    /// screenshots use) and click. Required for world clicks (machine placement, world picking)
    /// because the game reads Input.mousePosition + raycasts rather than EventSystem UI events.
    /// </summary>
    public static object ClickAt(float gameViewX, float gameViewY, string button = "left")
    {
        FocusUnityForInput();
        if (!TryGameViewToScreen(gameViewX, gameViewY, out int screenX, out int screenY, out string convErr))
            return Err(convErr);

        SetCursorPos(screenX, screenY);
        Thread.Sleep(15);

        OsMouseButton(button, down: true);
        Thread.Sleep(40);
        OsMouseButton(button, down: false);

        return new { success = true, gameView = new { x = gameViewX, y = gameViewY }, screen = new { x = screenX, y = screenY }, button };
    }

    public static object MoveMouseTo(float gameViewX, float gameViewY)
    {
        FocusUnityForInput();
        if (!TryGameViewToScreen(gameViewX, gameViewY, out int sx, out int sy, out string err))
            return Err(err);
        SetCursorPos(sx, sy);
        return new { success = true, screen = new { x = sx, y = sy } };
    }

    public static object MouseDown(float gameViewX, float gameViewY, string button = "left")
    {
        FocusUnityForInput();
        if (!TryGameViewToScreen(gameViewX, gameViewY, out int sx, out int sy, out string err))
            return Err(err);
        SetCursorPos(sx, sy);
        Thread.Sleep(10);
        OsMouseButton(button, down: true);
        return new { success = true, screen = new { x = sx, y = sy }, button, state = "down" };
    }

    public static object MouseUp(float gameViewX, float gameViewY, string button = "left")
    {
        FocusUnityForInput();
        if (!TryGameViewToScreen(gameViewX, gameViewY, out int sx, out int sy, out string err))
            return Err(err);
        SetCursorPos(sx, sy);
        Thread.Sleep(10);
        OsMouseButton(button, down: false);
        return new { success = true, screen = new { x = sx, y = sy }, button, state = "up" };
    }

    private static bool TryGameViewToScreen(float gvX, float gvY, out int screenX, out int screenY, out string error)
    {
        screenX = 0; screenY = 0; error = null;

        var gvType = Type.GetType("UnityEditor.GameView, UnityEditor");
        if (gvType == null) { error = "GameView type not found."; return false; }

        var gv = EditorWindow.GetWindow(gvType, false, "Game", true);
        if (gv == null) { error = "Failed to access GameView."; return false; }

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var viewInParent = (Rect)gvType.GetProperty("viewInParent", flags).GetValue(gv);
        var targetInView = (Rect)gvType.GetProperty("targetInView", flags).GetValue(gv);

        var parentField = typeof(EditorWindow).GetField("m_Parent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        object hostView = parentField.GetValue(gv);
        var screenPos = (Rect)hostView.GetType().GetProperty("screenPosition", flags).GetValue(hostView);

        float ppp = EditorGUIUtility.pixelsPerPoint;
        float gameAreaScreenX = (screenPos.x + viewInParent.x + targetInView.x) * ppp;
        float gameAreaScreenY = (screenPos.y + viewInParent.y + targetInView.y) * ppp;

        screenX = Mathf.RoundToInt(gameAreaScreenX + gvX);
        screenY = Mathf.RoundToInt(gameAreaScreenY + gvY);
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT_MOUSE_STRUCT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public int dx;
        [FieldOffset(12)] public int dy;
        [FieldOffset(16)] public uint mouseData;
        [FieldOffset(20)] public uint dwFlags;
        [FieldOffset(24)] public uint time;
        [FieldOffset(28)] public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT_MOUSE_STRUCT[] pInputs, int cbSize);

    private static void OsMouseButton(string button, bool down)
    {
        uint flag;
        switch (button?.ToLowerInvariant())
        {
            case "right": flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case "middle": flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            default: flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
        }
        var input = new INPUT_MOUSE_STRUCT { type = INPUT_MOUSE, dwFlags = flag };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT_MOUSE_STRUCT)));
    }

    // ---------- OS-level keyboard (Win32 SendInput) ----------

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // INPUT struct layout (Win32) on x64: 40 bytes total = 4 (type) + 4 (align) + 32 (largest union: MOUSEINPUT).
    // For the keyboard case we use the first 24 bytes after the header; remaining 8 bytes are tail padding.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT_KBD
    {
        [FieldOffset(0)] public uint type;
        // KEYBDINPUT fields begin at offset 8 (after 8-byte aligned union start on x64).
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT_KBD[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private static readonly HashSet<ushort> ExtendedVks = new HashSet<ushort>
    {
        0x21, 0x22, // PageUp, PageDown
        0x23, 0x24, // End, Home
        0x25, 0x26, 0x27, 0x28, // Left, Up, Right, Down
        0x2D, 0x2E, // Insert, Delete
        0x6F, // NumDivide
        0x2C, // PrintScreen
        0xA3, 0xA5, // RControl, RAlt
    };

    private static bool OsKey(string keyName, bool down, out string error)
    {
        error = null;
        if (!TryMapVk(keyName, out ushort vk))
        {
            error = $"No Win32 VK mapping for '{keyName}'.";
            return false;
        }

        uint scan = MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        uint flags = 0;
        if (!down) flags |= KEYEVENTF_KEYUP;
        if (ExtendedVks.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT_KBD
        {
            type = INPUT_KEYBOARD,
            wVk = vk,
            wScan = (ushort)scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        };

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT_KBD)));
        if (sent != 1)
        {
            error = $"SendInput returned {sent} (Win32 error {Marshal.GetLastWin32Error()}).";
            return false;
        }
        return true;
    }

    private static bool TryMapVk(string name, out ushort vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string k = name.Trim();

        // Single-letter A-Z
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c >= 'A' && c <= 'Z') { vk = (ushort)c; return true; }
            if (c >= '0' && c <= '9') { vk = (ushort)c; return true; }
            switch (c)
            {
                case ' ': vk = 0x20; return true;
            }
        }

        // Multi-char keys (case-insensitive)
        switch (k.ToLowerInvariant())
        {
            case "space": vk = 0x20; return true;
            case "enter": case "return": vk = 0x0D; return true;
            case "escape": case "esc": vk = 0x1B; return true;
            case "tab": vk = 0x09; return true;
            case "backspace": vk = 0x08; return true;
            case "delete": case "del": vk = 0x2E; return true;
            case "insert": case "ins": vk = 0x2D; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup": case "pgup": vk = 0x21; return true;
            case "pagedown": case "pgdn": vk = 0x22; return true;
            case "leftarrow": case "left": vk = 0x25; return true;
            case "uparrow": case "up": vk = 0x26; return true;
            case "rightarrow": case "right": vk = 0x27; return true;
            case "downarrow": case "down": vk = 0x28; return true;
            case "leftshift": case "lshift": vk = 0xA0; return true;
            case "rightshift": case "rshift": vk = 0xA1; return true;
            case "shift": vk = 0x10; return true;
            case "leftctrl": case "lctrl": vk = 0xA2; return true;
            case "rightctrl": case "rctrl": vk = 0xA3; return true;
            case "ctrl": case "control": vk = 0x11; return true;
            case "leftalt": case "lalt": vk = 0xA4; return true;
            case "rightalt": case "ralt": vk = 0xA5; return true;
            case "alt": vk = 0x12; return true;
            case "capslock": vk = 0x14; return true;
            case "minus": vk = 0xBD; return true;
            case "equals": case "plus": vk = 0xBB; return true;
            case "comma": vk = 0xBC; return true;
            case "period": case "dot": vk = 0xBE; return true;
            case "slash": vk = 0xBF; return true;
            case "backslash": vk = 0xDC; return true;
            case "semicolon": vk = 0xBA; return true;
            case "quote": vk = 0xDE; return true;
            case "leftbracket": case "lbracket": vk = 0xDB; return true;
            case "rightbracket": case "rbracket": vk = 0xDD; return true;
            case "backquote": case "tilde": vk = 0xC0; return true;
        }

        // Function keys F1-F24
        if (k.Length >= 2 && (k[0] == 'F' || k[0] == 'f'))
        {
            if (int.TryParse(k.Substring(1), out int fn) && fn >= 1 && fn <= 24)
            {
                vk = (ushort)(0x70 + (fn - 1));
                return true;
            }
        }

        // Digit<N> aliases from Input System
        if (k.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && k.Length == 6)
        {
            char d = k[5];
            if (d >= '0' && d <= '9') { vk = (ushort)d; return true; }
        }

        return false;
    }

    /// <summary>Focus the Unity main window AND the GameView panel so SendInput reaches the running game.</summary>
    private static void FocusUnityForInput()
    {
        try
        {
            // Focus the GameView panel inside Unity so the game receives messages, not the Hierarchy etc.
            var gvType = Type.GetType("UnityEditor.GameView, UnityEditor");
            if (gvType != null)
            {
                var gv = EditorWindow.GetWindow(gvType, false, "Game", true);
                gv?.Focus();
            }

            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return;

            // Standard trick to bypass Windows' anti-focus-steal protection: temporarily attach
            // our thread's input to the foreground thread's input, then SetForegroundWindow is
            // allowed because we appear to be the foreground process.
            IntPtr fgHwnd = GetForegroundWindow();
            uint fgThread = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out _) : 0;
            uint myThread = GetCurrentThreadId();

            bool attached = false;
            if (fgThread != 0 && fgThread != myThread)
                attached = AttachThreadInput(myThread, fgThread, true);

            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            if (attached)
                AttachThreadInput(myThread, fgThread, false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ManagePlayTest] FocusUnityForInput failed: {ex.Message}");
        }
    }
    }
}
