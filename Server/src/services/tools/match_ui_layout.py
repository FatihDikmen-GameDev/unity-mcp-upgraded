"""
Defines the match_ui_layout tool: reconstruct a uGUI hierarchy from a reference mockup PNG
plus a set of source sprite PNGs by template-matching each sprite against the reference.

Algorithm (Unity side, normalized cross-correlation on luminance with alpha masking):
- Shape-only match — tinted instances in the mockup still match the source sprite.
- Multi-instance detection via non-max suppression.
- Per match, the tint is recovered from the average reference/sprite RGB ratio and applied
  on the built Image's color.
- v1 limitations: 1:1 scale (no multi-scale search), no text detection (TMP placeholders
  left empty for the user), single sprite per region (no compositing detection).

Typical flow:
  1. Call action='match' with a reference image and a list of sprite paths -> returns matches.
  2. Call action='build_layout' with those matches + reference_width/height -> instantiates
     the matched UI via manage_canvas under the parent of your choice.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    group="ui",
    description=(
        "Reconstruct a uGUI hierarchy from a reference mockup PNG plus source sprite PNGs.\n\n"
        "Actions:\n"
        "- match: find every occurrence of each provided sprite in the reference via "
        "normalized cross-correlation on luminance (shape-only — tinted instances match). "
        "Returns a list of {sprite, x, y, width, height, tint, confidence}.\n"
        "- build_layout: pass the matches back along with reference_width/height and a parent "
        "Canvas; instantiates the matched UI as a manage_canvas hierarchy with each sprite "
        "placed at its detected position and tinted to the recovered color.\n\n"
        "Typical loop:\n"
        "  1. action='match' with reference_image + sprites list -> get matches.\n"
        "  2. (optional) tweak min_confidence or remove duplicates.\n"
        "  3. action='build_layout' with the matches array + parent canvas name -> live UI.\n\n"
        "v1 limitations: 1:1 scale only (no multi-scale search), no text detection (TMP "
        "placeholders left empty), single sprite per region. Best results when the mockup "
        "PNG was captured at the same resolution the sprites render at in-game."
    ),
    annotations=ToolAnnotations(
        title="Match UI Layout",
        destructiveHint=True,
    ),
)
async def match_ui_layout(
    ctx: Context,
    action: Annotated[Literal["match", "build_layout"], "Action to perform."],

    # match
    reference_image: Annotated[str,
        "Path to the reference mockup PNG (Assets-relative or absolute). For action='match'."] | None = None,
    sprites: Annotated[list[str],
        "List of sprite asset paths to search for in the reference. For action='match'."] | None = None,
    min_confidence: Annotated[float,
        "Minimum normalized-cross-correlation score to accept a match (0.0-1.0, default 0.85). For action='match'."] | None = None,
    max_matches_per_sprite: Annotated[int,
        "Cap on number of instances detected for any single sprite (default 32). For action='match'."] | None = None,
    coarse_stride: Annotated[int,
        "Coarse-pass stride in pixels for the two-pass scan (default 4). Lower = slower but finds smaller / off-grid sprites; higher = faster. For action='match'."] | None = None,

    # build_layout
    matches: Annotated[list[dict[str, Any]],
        "List of {sprite, x, y, width, height, tint, confidence} entries (typically the output of action='match'). For action='build_layout'."] | None = None,
    reference_width: Annotated[int,
        "Reference image width in pixels (passed through from action='match' output). For action='build_layout'."] | None = None,
    reference_height: Annotated[int,
        "Reference image height. For action='build_layout'."] | None = None,
    parent: Annotated[str,
        "GameObject name or hierarchy path of the Canvas (or any RectTransform) to attach the rebuilt layout under. For action='build_layout'."] | None = None,
    root_name: Annotated[str,
        "Name for the root GameObject of the rebuilt layout (default 'MatchedLayout'). For action='build_layout'."] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    action_lower = action.lower()

    if action_lower == "match":
        if not reference_image:
            return {"success": False, "message": "'reference_image' is required for action='match'."}
        if not sprites:
            return {"success": False, "message": "'sprites' list is required for action='match'."}
    elif action_lower == "build_layout":
        if not matches:
            return {"success": False, "message": "'matches' is required for action='build_layout' (use action='match' first)."}

    params_dict: dict[str, Any] = {"action": action_lower}
    for k, v in {
        "reference_image": reference_image,
        "sprites": sprites,
        "min_confidence": min_confidence,
        "max_matches_per_sprite": max_matches_per_sprite,
        "coarse_stride": coarse_stride,
        "matches": matches,
        "reference_width": reference_width,
        "reference_height": reference_height,
        "parent": parent,
        "root_name": root_name,
    }.items():
        if v is not None:
            params_dict[k] = v

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "match_ui_layout",
        params_dict,
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
