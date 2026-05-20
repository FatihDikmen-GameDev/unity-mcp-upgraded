"""
Defines the manage_play_test tool for driving the running Unity game like a human tester.

Synthesises keyboard + mouse input (Input System + OS SendInput dual-path), fires uGUI
EventSystem clicks, performs drags / scrolls, and polls UI state for assertions. Pair with
manage_camera screenshot to close the observe-act loop for automated test runs or LLM-driven
playthroughs.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    group="testing",
    description=(
        "Drive the running game like a human tester. Use during play mode to automate test "
        "flows or close an observe-act loop with manage_camera screenshot.\n\n"
        "UI events (no GameView focus required, uses EventSystem):\n"
        "- click_ui: fire pointer down/up/click on a UI element by name or hierarchy path.\n"
        "- drag_ui: drag from one UI element to another (pointer down -> dragHandler steps -> up -> drop).\n"
        "- drag_from_to: same but using explicit GameView pixel coords.\n"
        "- scroll_ui: fire IScrollHandler on a UI element with a given scroll delta.\n"
        "- describe_ui: read state of a UI element (active, components, button state, rect).\n\n"
        "Keyboard (dual-path: Input System synthetic + OS SendInput, focuses GameView):\n"
        "- press_key / release_key: synthesise a key down or up. Works for any input API the game reads.\n"
        "- tap_key: press + brief hold + release in one call. For one-shot triggers.\n"
        "- press_chord: press multiple keys simultaneously (e.g. ['LeftCtrl', 'S']).\n"
        "- focus_game_view: bring Unity main window + GameView to foreground for OS input routing.\n\n"
        "Mouse (OS-level, requires GameView focus):\n"
        "- click_at: move OS cursor to GameView pixel (x, y) and click. For world clicks the EventSystem doesn't reach.\n"
        "- move_mouse_to / mouse_down / mouse_up: compose custom drags or held clicks across MCP calls.\n\n"
        "Waits (poll Unity main thread, can pause game in play mode):\n"
        "- wait_until_active / wait_until_inactive: poll until a UI element is active or gone, with timeout.\n"
        "- wait_until_text: poll until a TMP_Text or uGUI Text contains a substring.\n"
        "- wait: blocking sleep on the main thread (use sparingly in play mode).\n\n"
        "Reliability notes:\n"
        "- Holding a key across game frames (e.g. 'hold E for 1s') requires the press in one MCP call "
        "and the release in a later call so Unity can advance frames between them.\n"
        "- Conveyor / drag-based placers need mouse down + intermediate moves + up to span MCP calls "
        "so Unity processes frames between samples; doing it all in one call collapses to a 0-distance drag.\n"
        "- OS SendInput mouse events always reach legacy UnityEngine.Input.GetMouseButton but may not "
        "always reach the new Input System's Mouse.current — for tools that read the new Input System "
        "(typical for InputAction bindings), one physical click in the GameView after entering play mode "
        "can re-seed the device."
    ),
    annotations=ToolAnnotations(
        title="Manage Play Test",
        destructiveHint=True,
    ),
)
async def manage_play_test(
    ctx: Context,
    action: Annotated[Literal[
        "click_ui", "drag_ui", "drag_from_to", "scroll_ui", "describe_ui",
        "press_key", "release_key", "tap_key", "press_chord", "focus_game_view",
        "click_at", "move_mouse_to", "mouse_down", "mouse_up",
        "wait_until_active", "wait_until_inactive", "wait_until_text", "wait",
    ], "Action to perform."],

    # Common UI / wait
    path: Annotated[str, "GameObject name or hierarchy path (e.g. 'Canvas/Panel/Button'). For click_ui, drag_ui (from-side), scroll_ui, describe_ui, wait_until_active/inactive/text."] | None = None,
    to: Annotated[str, "Destination GameObject for drag_ui."] | None = None,

    # Drag/scroll/coords
    from_x: Annotated[float, "Drag start GameView pixel x (drag_from_to)."] | None = None,
    from_y: Annotated[float, "Drag start GameView pixel y."] | None = None,
    to_x: Annotated[float, "Drag end pixel x."] | None = None,
    to_y: Annotated[float, "Drag end pixel y."] | None = None,
    steps: Annotated[int, "Drag interpolation steps (default 10)."] | None = None,
    step_delay_ms: Annotated[int, "Sleep per drag step in ms (default 16)."] | None = None,
    delta_x: Annotated[float, "Scroll delta x (scroll_ui)."] | None = None,
    delta_y: Annotated[float, "Scroll delta y (scroll_ui, default -1 = down)."] | None = None,

    # Mouse positioning
    x: Annotated[float, "GameView pixel x for click_at / move_mouse_to / mouse_down / mouse_up."] | None = None,
    y: Annotated[float, "GameView pixel y."] | None = None,
    button: Annotated[Literal["left", "right", "middle"], "Mouse button (default 'left')."] | None = None,

    # Keys
    key: Annotated[str, "Key name (e.g. 'W', 'Space', 'LeftArrow', 'Escape'). For press_key / release_key / tap_key."] | None = None,
    keys: Annotated[list[str], "List of keys for press_chord (e.g. ['LeftCtrl', 'S'])."] | None = None,
    hold_ms: Annotated[int, "Hold duration in ms for tap_key / press_chord (default 40 / 50)."] | None = None,
    focus_game_view: Annotated[bool, "Focus Unity + GameView before sending the key (default true)."] | None = None,

    # Waits
    expected_substring: Annotated[str, "Substring to match in TMP/Text for wait_until_text."] | None = None,
    timeout_seconds: Annotated[float, "Max poll duration for wait_until_* (default 5)."] | None = None,
    poll_ms: Annotated[int, "Sleep per poll in ms for wait_until_* (default 50 / 100)."] | None = None,
    seconds: Annotated[float, "Sleep duration for action='wait'."] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {"action": action.lower()}
    for k, v in {
        "path": path, "target": path, "to": to,
        "from_x": from_x, "from_y": from_y, "to_x": to_x, "to_y": to_y,
        "steps": steps, "step_delay_ms": step_delay_ms,
        "delta_x": delta_x, "delta_y": delta_y,
        "x": x, "y": y, "button": button,
        "key": key, "keys": keys, "hold_ms": hold_ms, "focus_game_view": focus_game_view,
        "expected_substring": expected_substring,
        "timeout_seconds": timeout_seconds, "poll_ms": poll_ms,
        "seconds": seconds,
    }.items():
        if v is not None:
            params_dict[k] = v

    # 'path' and 'target' map to the same Unity-side field; only one needed.
    if "path" in params_dict and "target" in params_dict and params_dict["path"] == params_dict["target"]:
        params_dict.pop("target", None)

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_play_test",
        params_dict,
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
