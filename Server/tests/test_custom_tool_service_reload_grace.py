"""Tests for the domain-reload grace period in CustomToolService.execute_tool.

A Unity domain reload (entering play mode, recompiling) drops the plugin
session and its registered tools until Unity re-sends register_tools a few
seconds later. execute_tool must poll through that window instead of
hard-failing, while still failing fast for genuinely unknown tool names.
"""

from unittest.mock import AsyncMock, patch

import pytest

import services.custom_tool_service as custom_tool_service_module
from models.models import ToolDefinitionModel
from services.custom_tool_service import CustomToolService


class _DummyMcp:
    def custom_route(self, _path, methods=None):  # noqa: ARG002
        def _decorator(fn):
            return fn

        return _decorator


@pytest.fixture(autouse=True)
def _fast_grace_poll(monkeypatch):
    monkeypatch.setattr(
        custom_tool_service_module, "_DEFINITION_GRACE_POLL_SECONDS", 0.01)


@pytest.mark.asyncio
async def test_execute_tool_waits_out_reload_gap_then_succeeds():
    service = CustomToolService(_DummyMcp())
    definition = ToolDefinitionModel(
        name="my_tool", description="My tool", requires_polling=False)

    # Definition is missing for the first few polls (reload window), then appears.
    lookups = [None, None, None, definition]

    with patch.object(service, "get_tool_definition", new_callable=AsyncMock) as mock_get:
        with patch.object(service, "list_registered_tools", new_callable=AsyncMock) as mock_list:
            with patch("services.custom_tool_service.send_with_unity_instance", new_callable=AsyncMock) as mock_send:
                mock_get.side_effect = lookups
                mock_list.return_value = []
                mock_send.return_value = {"success": True, "message": "ok"}

                result = await service.execute_tool(
                    "project-hash", "my_tool", "Project@project-hash", {})

    assert result.success is True
    mock_send.assert_awaited_once()


@pytest.mark.asyncio
async def test_execute_tool_fails_fast_when_registration_present():
    service = CustomToolService(_DummyMcp())
    other_tool = ToolDefinitionModel(name="other_tool", description="Other")

    with patch.object(service, "get_tool_definition", new_callable=AsyncMock) as mock_get:
        with patch.object(service, "list_registered_tools", new_callable=AsyncMock) as mock_list:
            with patch("services.custom_tool_service.send_with_unity_instance", new_callable=AsyncMock) as mock_send:
                mock_get.return_value = None
                mock_list.return_value = [other_tool]

                result = await service.execute_tool(
                    "project-hash", "missing_tool", "Project@project-hash", {})

    assert result.success is False
    assert "not found" in (result.message or "")
    mock_send.assert_not_awaited()
    # Initial lookup + one in-grace lookup; no extended polling.
    assert mock_get.await_count == 2


@pytest.mark.asyncio
async def test_execute_tool_times_out_when_nothing_registers(monkeypatch):
    monkeypatch.setenv("UNITY_MCP_TOOL_DEFINITION_WAIT_SECONDS", "0.05")
    service = CustomToolService(_DummyMcp())

    with patch.object(service, "get_tool_definition", new_callable=AsyncMock) as mock_get:
        with patch.object(service, "list_registered_tools", new_callable=AsyncMock) as mock_list:
            with patch("services.custom_tool_service.send_with_unity_instance", new_callable=AsyncMock) as mock_send:
                mock_get.return_value = None
                mock_list.return_value = []

                result = await service.execute_tool(
                    "project-hash", "my_tool", "Project@project-hash", {})

    assert result.success is False
    assert "not found" in (result.message or "")
    mock_send.assert_not_awaited()


@pytest.mark.asyncio
async def test_grace_wait_disabled_via_env(monkeypatch):
    monkeypatch.setenv("UNITY_MCP_TOOL_DEFINITION_WAIT_SECONDS", "0")
    service = CustomToolService(_DummyMcp())

    with patch.object(service, "get_tool_definition", new_callable=AsyncMock) as mock_get:
        with patch.object(service, "list_registered_tools", new_callable=AsyncMock) as mock_list:
            mock_get.return_value = None
            mock_list.return_value = []

            result = await service.execute_tool(
                "project-hash", "my_tool", "Project@project-hash", {})

    assert result.success is False
    # Only the initial lookup — the grace loop never ran.
    assert mock_get.await_count == 1
    mock_list.assert_not_awaited()
