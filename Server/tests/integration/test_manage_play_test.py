"""Integration tests for the manage_play_test tool (game-testing primitives)."""

import asyncio
import pytest

from .test_helpers import DummyContext
import services.tools.manage_play_test as manage_play_test_mod


def run_async(coro):
    loop = asyncio.new_event_loop()
    try:
        asyncio.set_event_loop(loop)
        return loop.run_until_complete(coro)
    finally:
        loop.close()
        asyncio.set_event_loop(None)


class TestManagePlayTestIntegration:
    def test_click_ui_forwards_path(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["cmd"] = cmd
            captured["params"] = params
            return {"success": True, "message": "clicked"}

        monkeypatch.setattr(manage_play_test_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_play_test_mod.manage_play_test(
            ctx=DummyContext(),
            action="click_ui",
            path="Canvas/PlayButton",
        ))

        assert resp["success"] is True
        assert captured["cmd"] == "manage_play_test"
        assert captured["params"]["action"] == "click_ui"
        assert captured["params"]["path"] == "Canvas/PlayButton"

    def test_press_key_with_focus(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True}

        monkeypatch.setattr(manage_play_test_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_play_test_mod.manage_play_test(
            ctx=DummyContext(),
            action="press_key",
            key="E",
            focus_game_view=True,
        ))

        assert resp["success"] is True
        assert captured["params"]["key"] == "E"
        assert captured["params"]["focus_game_view"] is True

    def test_press_chord_forwards_key_list(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True}

        monkeypatch.setattr(manage_play_test_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_play_test_mod.manage_play_test(
            ctx=DummyContext(),
            action="press_chord",
            keys=["LeftCtrl", "S"],
            hold_ms=50,
        ))

        assert resp["success"] is True
        assert captured["params"]["keys"] == ["LeftCtrl", "S"]
        assert captured["params"]["hold_ms"] == 50

    def test_click_at_forwards_coords(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True}

        monkeypatch.setattr(manage_play_test_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_play_test_mod.manage_play_test(
            ctx=DummyContext(),
            action="click_at",
            x=960.5,
            y=540.0,
            button="left",
        ))

        assert resp["success"] is True
        assert captured["params"]["x"] == 960.5
        assert captured["params"]["y"] == 540.0
        assert captured["params"]["button"] == "left"

    def test_wait_until_text(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True, "elapsedSeconds": 0.3, "polls": 4}

        monkeypatch.setattr(manage_play_test_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_play_test_mod.manage_play_test(
            ctx=DummyContext(),
            action="wait_until_text",
            path="Counter",
            expected_substring="10/10",
            timeout_seconds=10.0,
        ))

        assert resp["success"] is True
        assert captured["params"]["expected_substring"] == "10/10"
        assert captured["params"]["timeout_seconds"] == 10.0

    def test_drag_from_to_coords(self, monkeypatch):
        captured = {}

        async def fake_send(func, instance, cmd, params, **kwargs):
            captured["params"] = params
            return {"success": True}

        monkeypatch.setattr(manage_play_test_mod, "send_with_unity_instance", fake_send)

        resp = run_async(manage_play_test_mod.manage_play_test(
            ctx=DummyContext(),
            action="drag_from_to",
            from_x=100, from_y=200, to_x=300, to_y=400,
            steps=8,
        ))

        assert resp["success"] is True
        assert captured["params"]["from_x"] == 100
        assert captured["params"]["to_y"] == 400
        assert captured["params"]["steps"] == 8
