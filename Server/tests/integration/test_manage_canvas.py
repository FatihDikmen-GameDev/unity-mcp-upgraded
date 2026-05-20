"""Integration tests for the manage_canvas tool (uGUI builder)."""

import asyncio
import pytest

from .test_helpers import DummyContext
import services.tools.manage_canvas as manage_canvas_mod


def run_async(coro):
    loop = asyncio.new_event_loop()
    try:
        asyncio.set_event_loop(loop)
        return loop.run_until_complete(coro)
    finally:
        loop.close()
        asyncio.set_event_loop(None)


class TestManageCanvasIntegration:
    def test_build_simple_layout(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["cmd"] = cmd
            captured["params"] = params
            return {"success": True, "message": "ok",
                    "data": {"root": {"name": "Plate", "instanceID": 1234, "path": "Canvas/Plate"},
                             "nodes": []}}

        monkeypatch.setattr(manage_canvas_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_canvas_mod.manage_canvas(
            ctx=DummyContext(),
            action="build",
            parent="Canvas",
            layout={
                "name": "Plate",
                "rect": {"anchor": "top-center", "anchored_position": [0, -40], "size_delta": [200, 60]},
                "image": {"sprite": "Assets/UI/Plate.png", "color": [1, 1, 1, 1]},
            },
        ))

        assert resp["success"] is True
        assert captured["cmd"] == "manage_canvas"
        assert captured["params"]["action"] == "build"
        assert captured["params"]["parent"] == "Canvas"
        assert captured["params"]["layout"]["name"] == "Plate"
        assert captured["params"]["layout"]["rect"]["anchor"] == "top-center"

    def test_build_accepts_json_string_layout(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True}

        monkeypatch.setattr(manage_canvas_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_canvas_mod.manage_canvas(
            ctx=DummyContext(),
            action="build",
            layout='{"name": "X", "children": [{"name": "A"}, {"name": "B"}]}',
        ))

        assert resp["success"] is True
        assert captured["params"]["layout"]["name"] == "X"
        assert len(captured["params"]["layout"]["children"]) == 2

    def test_build_rejects_missing_layout(self, monkeypatch):
        async def fake_send(*args, **kwargs):
            return {"success": True}

        monkeypatch.setattr(manage_canvas_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_canvas_mod.manage_canvas(
            ctx=DummyContext(),
            action="build",
        ))

        assert resp["success"] is False
        assert "layout" in resp["message"]

    def test_build_rejects_missing_name(self, monkeypatch):
        async def fake_send(*args, **kwargs):
            return {"success": True}

        monkeypatch.setattr(manage_canvas_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_canvas_mod.manage_canvas(
            ctx=DummyContext(),
            action="build",
            layout={"rect": {"anchor": "center"}},
        ))

        assert resp["success"] is False
        assert "name" in resp["message"]

    def test_build_rejects_unknown_action(self, monkeypatch):
        async def fake_send(*args, **kwargs):
            return {"success": True}

        monkeypatch.setattr(manage_canvas_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_canvas_mod.manage_canvas(
            ctx=DummyContext(),
            action="delete",
            layout={"name": "X"},
        ))

        assert resp["success"] is False
        assert "Unknown action" in resp["message"]

    def test_build_nested_children_forwarded(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True}

        monkeypatch.setattr(manage_canvas_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_canvas_mod.manage_canvas(
            ctx=DummyContext(),
            action="build",
            layout={
                "name": "Toolbar",
                "rect": {"anchor": "bottom-center", "size_delta": [800, 80]},
                "image": {"color": [0.2, 0.2, 0.2, 0.9]},
                "children": [
                    {"name": f"Cell{i}",
                     "rect": {"size_delta": [60, 60], "anchored_position": [i * 70, 0]},
                     "image": {"color": [1, 1, 1, 1]},
                     "children": [
                         {"name": "Label", "text": {"text": str(i), "font_size": 20}}
                     ]}
                    for i in range(3)
                ],
            },
        ))

        assert resp["success"] is True
        children = captured["params"]["layout"]["children"]
        assert len(children) == 3
        assert children[0]["children"][0]["text"]["text"] == "0"
        assert children[2]["children"][0]["text"]["font_size"] == 20
