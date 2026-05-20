"""Integration tests for match_ui_layout — template-match a mockup against source sprites."""

import asyncio

from .test_helpers import DummyContext
import services.tools.match_ui_layout as match_ui_layout_mod


def run_async(coro):
    loop = asyncio.new_event_loop()
    try:
        asyncio.set_event_loop(loop)
        return loop.run_until_complete(coro)
    finally:
        loop.close()
        asyncio.set_event_loop(None)


class TestMatchUiLayout:
    def test_match_forwards_params(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["cmd"] = cmd
            captured["params"] = params
            return {"success": True, "data": {"matches": [], "reference_width": 1920, "reference_height": 1080}}

        monkeypatch.setattr(match_ui_layout_mod, "send_with_unity_instance", fake_send)

        resp = run_async(match_ui_layout_mod.match_ui_layout(
            ctx=DummyContext(),
            action="match",
            reference_image="Assets/Mockup.png",
            sprites=["Assets/UI/A.png", "Assets/UI/B.png"],
            min_confidence=0.8,
            coarse_stride=8,
        ))

        assert resp["success"] is True
        assert captured["cmd"] == "match_ui_layout"
        assert captured["params"]["action"] == "match"
        assert captured["params"]["reference_image"] == "Assets/Mockup.png"
        assert captured["params"]["sprites"] == ["Assets/UI/A.png", "Assets/UI/B.png"]
        assert captured["params"]["min_confidence"] == 0.8
        assert captured["params"]["coarse_stride"] == 8

    def test_match_requires_reference(self, monkeypatch):
        async def fake_send(*args, **kwargs):
            return {"success": True}

        monkeypatch.setattr(match_ui_layout_mod, "send_with_unity_instance", fake_send)

        resp = run_async(match_ui_layout_mod.match_ui_layout(
            ctx=DummyContext(),
            action="match",
            sprites=["Assets/A.png"],
        ))
        assert resp["success"] is False
        assert "reference_image" in resp["message"]

    def test_match_requires_sprites(self, monkeypatch):
        async def fake_send(*args, **kwargs):
            return {"success": True}

        monkeypatch.setattr(match_ui_layout_mod, "send_with_unity_instance", fake_send)

        resp = run_async(match_ui_layout_mod.match_ui_layout(
            ctx=DummyContext(),
            action="match",
            reference_image="Assets/Mockup.png",
        ))
        assert resp["success"] is False
        assert "sprites" in resp["message"]

    def test_build_layout_forwards_matches(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True, "data": {"root": {"name": "MatchedLayout", "instanceID": 123}, "nodes": []}}

        monkeypatch.setattr(match_ui_layout_mod, "send_with_unity_instance", fake_send)

        sample_matches = [
            {"sprite": "Assets/UI/A.png", "x": 100, "y": 200, "width": 64, "height": 64,
             "tint": [1, 1, 1, 1], "confidence": 0.95},
            {"sprite": "Assets/UI/B.png", "x": 300, "y": 200, "width": 128, "height": 64,
             "tint": [1, 0.5, 0.5, 1], "confidence": 0.88},
        ]

        resp = run_async(match_ui_layout_mod.match_ui_layout(
            ctx=DummyContext(),
            action="build_layout",
            matches=sample_matches,
            reference_width=1920,
            reference_height=1080,
            parent="Canvas",
            root_name="ReconstructedUI",
        ))

        assert resp["success"] is True
        assert captured["params"]["action"] == "build_layout"
        assert captured["params"]["matches"] == sample_matches
        assert captured["params"]["reference_width"] == 1920
        assert captured["params"]["parent"] == "Canvas"
        assert captured["params"]["root_name"] == "ReconstructedUI"

    def test_build_layout_requires_matches(self, monkeypatch):
        async def fake_send(*args, **kwargs):
            return {"success": True}

        monkeypatch.setattr(match_ui_layout_mod, "send_with_unity_instance", fake_send)

        resp = run_async(match_ui_layout_mod.match_ui_layout(
            ctx=DummyContext(),
            action="build_layout",
            parent="Canvas",
        ))
        assert resp["success"] is False
        assert "matches" in resp["message"]
