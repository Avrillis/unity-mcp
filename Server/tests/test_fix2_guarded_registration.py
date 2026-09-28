"""C17-R3-FIX2 server-side coverage.

Two areas Sol found under-tested:

  * the guarded registration transition must be serialised, so two concurrent registrations can
    never both observe the binding as "orphaned" and take it over (blocker 5);
  * every guarded dispatch path - including the REST routes - must resolve its target through the
    single guarded-session selector, never through first / active / default / project-name
    selection (blocker 5).

These tests drive the real production orchestration: ``PluginHub._handle_register``,
``PluginHub.on_disconnect`` and ``PluginHub.resolve_guarded_session`` (the function both REST
routes call).
"""

from __future__ import annotations

import asyncio
import os
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from transport.route_guard import (
    ManagedRouteGuard,
    build_guard,
    get_active_guard,
    set_active_guard,
)
from transport.models import RegisterMessage


@pytest.fixture(autouse=True)
def _reset_guard():
    previous = get_active_guard()
    yield
    set_active_guard(previous)


@pytest.fixture(autouse=True)
def _reset_plugin_hub():
    old_registry = PluginHub._registry
    old_lock = PluginHub._lock
    old_loop = PluginHub._loop
    old_transition = PluginHub._guard_transition_lock
    old_connections = PluginHub._connections.copy()
    old_pending = PluginHub._pending.copy()

    yield

    PluginHub._registry = old_registry
    PluginHub._lock = old_lock
    PluginHub._loop = old_loop
    PluginHub._guard_transition_lock = old_transition
    PluginHub._connections = old_connections
    PluginHub._pending = old_pending


def _mock_websocket(connection_id: str | None = None) -> AsyncMock:
    ws = AsyncMock()
    ws.headers = {}
    ws.state = SimpleNamespace()
    if connection_id is not None:
        ws.state.connection_id = connection_id
    ws.close = AsyncMock()
    ws.send_json = AsyncMock()
    return ws


def _make_hub() -> PluginHub:
    return PluginHub({"type": "websocket"}, receive=AsyncMock(), send=AsyncMock())


async def _register(
    hub: PluginHub,
    ws: AsyncMock,
    *,
    project_name: str = "Game",
    project_hash: str = "hash1",
    project_path: str,
    instance_token: str | None = "nonce-1",
):
    payload = RegisterMessage(
        project_name=project_name,
        project_hash=project_hash,
        unity_version="6000.5.6f1",
        project_path=project_path,
        canonical_project_root=project_path,
        instance_token=instance_token,
    )
    return await hub._handle_register(ws, payload)


def _configured(tmp_path: Path, monkeypatch=None) -> PluginRegistry:
    registry = PluginRegistry()
    PluginHub.configure(registry, loop=asyncio.get_running_loop())
    return registry


# ============================================================
# Serialised guarded registration
# ============================================================


@pytest.mark.asyncio
async def test_second_registration_cannot_take_the_binding_while_the_first_is_in_progress(
    tmp_path, monkeypatch
):
    registry = _configured(tmp_path)
    guard = build_guard(str(tmp_path), "nonce-1")
    set_active_guard(guard)

    entered = asyncio.Event()
    release = asyncio.Event()
    original = PluginHub._complete_registration

    async def slow_complete(websocket, payload, reg, lock, guard=None):
        entered.set()
        await release.wait()
        await original(websocket, payload, reg, lock, guard=guard)

    monkeypatch.setattr(PluginHub, "_complete_registration", slow_complete)
    monkeypatch.setattr(PluginHub, "_guard_transition_lock", asyncio.Lock())

    ws_a = _mock_websocket("c1")
    ws_b = _mock_websocket("c2")

    task_a = asyncio.create_task(
        _register(_make_hub(), ws_a, project_path=str(tmp_path))
    )
    await entered.wait()

    # A has reserved the binding and holds the transition lock. B must not be able to proceed,
    # let alone take the binding.
    assert guard.binding_state == ManagedRouteGuard.BINDING_REGISTERING
    task_b = asyncio.create_task(
        _register(_make_hub(), ws_b, project_path=str(tmp_path))
    )
    await asyncio.sleep(0.05)
    assert not task_b.done(), "B must wait for A's transition instead of racing it"
    assert guard.bound_connection_id == "c1"

    release.set()
    await task_a
    await task_b

    assert guard.binding_state == ManagedRouteGuard.BINDING_BOUND
    assert guard.bound_connection_id == "c1"
    ws_b.close.assert_awaited_once_with(code=4403)
    sessions = await registry.list_sessions()
    assert len(sessions) == 1


@pytest.mark.asyncio
async def test_failed_registration_releases_the_reserved_binding(tmp_path, monkeypatch):
    registry = _configured(tmp_path)
    set_active_guard(build_guard(str(tmp_path), "nonce-1"))

    async def failing(websocket, payload, reg, lock, guard=None):
        raise RuntimeError("registration failed after the reservation")

    monkeypatch.setattr(PluginHub, "_complete_registration", failing)
    monkeypatch.setattr(PluginHub, "_guard_transition_lock", asyncio.Lock())

    with pytest.raises(RuntimeError):
        await _register(_make_hub(), _mock_websocket("c1"), project_path=str(tmp_path))

    # The route must not be left permanently reserved.
    assert get_active_guard().binding_state == ManagedRouteGuard.BINDING_UNBOUND


@pytest.mark.asyncio
async def test_disconnect_releases_the_binding_for_a_later_registration(tmp_path):
    registry = _configured(tmp_path)
    guard = build_guard(str(tmp_path), "nonce-1")
    set_active_guard(guard)

    ws_a = _mock_websocket("c1")
    await _register(_make_hub(), ws_a, project_path=str(tmp_path))
    assert guard.binding_state == ManagedRouteGuard.BINDING_BOUND

    await _make_hub().on_disconnect(ws_a, 1000)

    assert guard.binding_state == ManagedRouteGuard.BINDING_UNBOUND

    ws_b = _mock_websocket("c2")
    await _register(_make_hub(), ws_b, project_path=str(tmp_path))

    ws_b.close.assert_not_called()
    assert guard.bound_connection_id == "c2"
    assert len(await registry.list_sessions()) == 1


@pytest.mark.asyncio
async def test_same_root_and_nonce_from_a_different_connection_is_rejected(tmp_path):
    registry = _configured(tmp_path)
    guard = build_guard(str(tmp_path), "nonce-1")
    set_active_guard(guard)

    await _register(_make_hub(), _mock_websocket("c1"), project_path=str(tmp_path))

    ws_b = _mock_websocket("c2")
    await _register(_make_hub(), ws_b, project_path=str(tmp_path))

    ws_b.close.assert_awaited_once_with(code=4403)
    assert guard.bound_connection_id == "c1"
    assert len(await registry.list_sessions()) == 1


# ============================================================
# Central guarded dispatch selection (the REST routes' selector)
# ============================================================


async def _bound_and_foreign(registry: PluginRegistry, tmp_path: Path) -> str:
    """Register a foreign session first, then the guarded one. Returns the bound session id."""
    await registry.register(
        "foreign-session", "Neighbour", "neighbourhash", "6000.5.6f1", str(tmp_path)
    )
    await _register(_make_hub(), _mock_websocket("c1"), project_path=str(tmp_path))
    bound = await registry.get_session_id_by_hash("hash1")
    assert bound is not None
    return bound


@pytest.mark.asyncio
async def test_absent_target_selects_the_bound_session_never_the_first_foreign_session(tmp_path):
    registry = _configured(tmp_path)
    set_active_guard(build_guard(str(tmp_path), "nonce-1"))
    bound = await _bound_and_foreign(registry, tmp_path)

    selection = await PluginHub.resolve_guarded_session(None)

    assert selection.guarded is True
    assert selection.session_id == bound
    assert selection.project_hash == "hash1"


@pytest.mark.asyncio
async def test_explicit_target_for_a_foreign_session_is_rejected(tmp_path):
    registry = _configured(tmp_path)
    set_active_guard(build_guard(str(tmp_path), "nonce-1"))
    await _bound_and_foreign(registry, tmp_path)

    selection = await PluginHub.resolve_guarded_session("Neighbour@neighbourhash")

    assert selection.guarded is True
    assert selection.session_id is None
    assert selection.status_code == 403


@pytest.mark.asyncio
async def test_own_explicit_target_resolves_to_the_bound_session(tmp_path):
    registry = _configured(tmp_path)
    set_active_guard(build_guard(str(tmp_path), "nonce-1"))
    bound = await _bound_and_foreign(registry, tmp_path)

    assert (await PluginHub.resolve_guarded_session("Game@hash1")).session_id == bound
    assert (await PluginHub.resolve_guarded_session("hash1")).session_id == bound


@pytest.mark.asyncio
async def test_dispatch_fails_closed_before_any_binding(tmp_path):
    _configured(tmp_path)
    set_active_guard(build_guard(str(tmp_path), "nonce-1"))

    selection = await PluginHub.resolve_guarded_session(None)

    assert selection.session_id is None
    assert selection.status_code == 503


@pytest.mark.asyncio
async def test_dispatch_fails_closed_when_the_bound_session_is_gone(tmp_path):
    registry = _configured(tmp_path)
    guard = build_guard(str(tmp_path), "nonce-1")
    set_active_guard(guard)
    bound = await _bound_and_foreign(registry, tmp_path)

    # The bound session disappeared underneath the guard (a crash without a clean disconnect).
    await registry.unregister(bound)

    selection = await PluginHub.resolve_guarded_session(None)

    assert selection.session_id is None
    assert selection.status_code == 503


@pytest.mark.asyncio
async def test_unguarded_selector_defers_to_legacy_selection(tmp_path):
    _configured(tmp_path)
    set_active_guard(build_guard(None, None))

    selection = await PluginHub.resolve_guarded_session(None)

    assert selection.guarded is False
    assert selection.session_id is None


def test_both_rest_routes_use_the_central_guarded_selector():
    """A regression that re-implements selection inside a route must fail here."""
    source = Path(__file__).resolve().parents[1] / "src" / "main.py"
    text = source.read_text(encoding="utf-8")

    command_start = text.index('@mcp.custom_route("/api/command"')
    custom_tools_start = text.index('@mcp.custom_route("/api/custom-tools"')
    command_route = text[command_start:custom_tools_start]
    custom_tools_route = text[custom_tools_start:]

    assert "resolve_guarded_session" in command_route
    assert "resolve_guarded_session" in custom_tools_route
