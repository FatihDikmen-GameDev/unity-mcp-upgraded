"""
Defines the manage_canvas tool for building runtime uGUI hierarchies
(Canvas / RectTransform / Image / TextMeshProUGUI) from a declarative spec.

Distinct from manage_ui, which targets UI Toolkit (UXML/USS/VisualElement).
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import parse_json_payload
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    group="ui",
    description=(
        "Builds a runtime uGUI hierarchy (Canvas / RectTransform / Image / TextMeshProUGUI) from a "
        "declarative spec in one call. Use this instead of stitching together manage_gameobject + "
        "manage_components calls for layered UI like a plate-with-icon-and-text or a toolbar with cells. "
        "Distinct from manage_ui (UI Toolkit / UXML / USS).\n\n"
        "Action: build.\n\n"
        "Layout spec (nested dict):\n"
        "  name (required): GameObject name.\n"
        "  rect: dict with optional keys:\n"
        "    anchor: anchor preset string. One of: top-left, top-center, top-right, "
        "middle-left, middle-center (or 'center'), middle-right, bottom-left, bottom-center, "
        "bottom-right, stretch-top, stretch-bottom, stretch-left, stretch-right, "
        "stretch-horizontal, stretch-vertical, stretch (or 'stretch-all').\n"
        "    anchor_min, anchor_max, pivot: [x, y] 0-1 floats (override preset when present).\n"
        "    anchored_position: [x, y] in pixels.\n"
        "    size_delta: [w, h] in pixels (or [horiz_pad, vert_pad] when an axis is stretched).\n"
        "    rotation: z-axis euler degrees.\n"
        "    scale: [x, y] (z stays 1).\n"
        "  image: dict, adds a UnityEngine.UI.Image:\n"
        "    sprite: Assets-relative path to a Sprite.\n"
        "    color: [r, g, b] or [r, g, b, a], 0-1 floats.\n"
        "    type: 'Simple' | 'Sliced' | 'Tiled' | 'Filled'.\n"
        "    raycast_target (bool), preserve_aspect (bool).\n"
        "  text: dict, adds a TMPro.TextMeshProUGUI (requires the TextMeshPro package):\n"
        "    text: string content.\n"
        "    font_size: number.\n"
        "    color: [r, g, b, a] 0-1.\n"
        "    alignment: TextAlignmentOptions name (Center, Left, Right, MidlineCenter, etc.).\n"
        "    font_style: FontStyles name (Normal, Bold, Italic, ...).\n"
        "    font_asset: Assets-relative path to a TMP_FontAsset.\n"
        "    raycast_target (bool).\n"
        "  children: list of nested layout specs.\n\n"
        "Returns: root {name, instanceID, path} plus a flat 'nodes' list of every created GameObject."
    ),
    annotations=ToolAnnotations(
        title="Manage Canvas (uGUI Builder)",
        destructiveHint=True,
    ),
)
async def manage_canvas(
    ctx: Context,
    action: Annotated[Literal["build"], "Action to perform."],

    layout: Annotated[dict[str, Any] | str,
                      "Nested layout spec dict (see tool description). Accepts a JSON string for clients "
                      "that cannot send nested objects directly."] | None = None,

    parent: Annotated[str,
                      "Where to attach the root. GameObject name, hierarchy path (e.g. 'Canvas/HUD'), or "
                      "instance ID as string. Omit to attach to the scene root."] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    if action.lower() != "build":
        return {"success": False, "message": f"Unknown action '{action}'. Valid: build."}

    if isinstance(layout, str):
        parsed = parse_json_payload(layout)
        if not isinstance(parsed, dict):
            return {"success": False, "message": "'layout' string must parse to a JSON object."}
        layout = parsed

    if not isinstance(layout, dict):
        return {"success": False, "message": "'layout' is required and must be an object."}

    if not layout.get("name") and not layout.get("children"):
        return {"success": False, "message": "'layout' must have a 'name' (or at minimum 'children')."}

    params_dict: dict[str, Any] = {
        "action": "build",
        "layout": layout,
    }
    if parent is not None:
        params_dict["parent"] = parent

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_canvas",
        params_dict,
    )

    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
