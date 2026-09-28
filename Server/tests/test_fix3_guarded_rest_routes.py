"""C17-R3-FIX3 executable REST-route coverage for the guarded dispatch selector.

Sol's FIX2 review found that the guarded REST coverage only inspected the *source text* of
``/api/command`` and ``/api/custom-tools`` instead of executing them. These tests build a real
Starlette ASGI app from the production server's own custom routes and drive both endpoints over
HTTP, using a registry that already contains a foreign session (inserted first) plus the guarded
session (inserted second).

They also pin the FIX3 guard repair: a bound identity whose exact session is no longer present in
the registry must fail closed instead of returning its stale session id.
"""

from __future__ import annotations

import asyncio
import logging
import os
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import httpx
import pytest
from starlette.applications import Starlette
from starlette.routing import Route

from transport.models import RegisterMessage
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from transport.route_guard import build_guard, get_active_guard, set_active_guard


def _import_production_server():
    """Import ``main`` without leaking its module-level side effects.

    Importing the production entry point reconfigures process-wide state: ``logging.basicConfig(
    force=True)`` replaces the root handlers and the module then disables propagation on several
    loggers, and it stamps ``UNITY_MCP_TELEMETRY_TIMEOUT``. Other characterization tests assert on
    exactly that state. The routes under test are the production ones, but those unrelated
    import-time side effects are snapshotted and restored so they cannot escape this module.
    """
    watched = (
        "UNITY_MCP_TELEMETRY_TIMEOUT",
        "UNITY_MCP_HTTP_URL",
        "UNITY_MCP_HTTP_HOST",
        "UNITY_MCP_HTTP_PORT",
        "UNITY_MCP_TRANSPORT",
        "UNITY_MCP_AUTOSTART",
    )
    saved_env = {name: os.environ.get(name) for name in watched}

    manager = logging.Logger.manager
    root = logging.getLogger()
    saved_root_level = root.level
    saved_root_handlers = list(root.handlers)
    saved_disable = manager.disable
    saved_loggers = {
        name: (obj.level, obj.propagate, list(obj.handlers))
        for name, obj in list(manager.loggerDict.items())
        if isinstance(obj, logging.Logger)
    }

    try:
        import main as production_main
    finally:
        root.setLevel(saved_root_level)
        root.handlers[:] = saved_root_handlers
        manager.disable = saved_disable
        for name, (level, propagate, handlers) in saved_loggers.items():
            existing = manager.loggerDict.get(name)
            if isinstance(existing, logging.Logger):
                existing.setLevel(level)
                existing.propagate = propagate
                existing.handlers[:] = handlers
        for name, value in saved_env.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
    return production_main


server_main = _import_production_server()


class _RouteRecordingMcp:
    """The minimal FastMCP surface ``create_mcp_server`` needs, recording its custom routes.

    The suite that runs alongside this one replaces ``fastmcp.FastMCP`` with a no-argument stub,
    so the real framework object is not reliably available. Only the decorator surface is
    substituted here; the route *bodies* under test are still the production closures inside
    ``create_mcp_server``, and they execute over a real Starlette ASGI transport.
    """

    last: "_RouteRecordingMcp | None" = None

    def __init__(self, *args, **kwargs):  # noqa: ARG002
        self.custom_routes: dict[str, tuple] = {}
        self._additional_http_routes: list = []
        _RouteRecordingMcp.last = self

    def custom_route(self, path, methods=None):
        def decorator(fn):
            self.custom_routes[path] = (fn, list(methods or ["GET"]))
            return fn

        return decorator

    def tool(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def resource(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def prompt(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def add_middleware(self, *args, **kwargs):  # noqa: ARG002
        return None

    def disable(self, *args, **kwargs):  # noqa: ARG002
        return None

    def _get_additional_http_routes(self):
        return self._additional_http_routes

FOREIGN_SESSION = "foreign-session"
FOREIGN_HASH = "neighbourhash"
BOUND_HASH = "hash1"
NONCE = "nonce-1"

COMMAND_PATH = "/api/command"
CUSTOM_TOOLS_PATH = "/api/custom-tools"


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
    ping_tasks = PluginHub._ping_tasks.copy()
    last_pong = PluginHub._last_pong.copy()

    yield

    for task in list(PluginHub._ping_tasks.values()):
        if task is not None and not task.done():
            task.cancel()

    PluginHub._registry = old_registry
    PluginHub._lock = old_lock
    PluginHub._loop = old_loop
    PluginHub._guard_transition_lock = old_transition
    PluginHub._connections = old_connections
    PluginHub._pending = old_pending
    PluginHub._ping_tasks = ping_tasks
    PluginHub._last_pong = last_pong


_REST_APP: Starlette | None = None


def _rest_app() -> Starlette:
    """The production server's own REST route bodies, as a real ASGI app."""
    global _REST_APP
    if _REST_APP is not None:
        return _REST_APP

    # Only the route bodies are wanted here. Registering the full tool/resource catalogue would
    # mutate the shared registry that other characterization tests read, so those side effects are
    # suppressed for this harness.
    with patch.object(server_main, "FastMCP", _RouteRecordingMcp), patch.object(
        server_main, "register_all_tools", lambda *a, **k: None
    ), patch.object(
        server_main, "register_all_resources", lambda *a, **k: None
    ), patch.object(
        server_main, "get_unity_instance_middleware", lambda *a, **k: None
    ):
        server_main.create_mcp_server(project_scoped_tools=False)

    recording = _RouteRecordingMcp.last
    assert recording is not None

    wanted = {COMMAND_PATH, CUSTOM_TOOLS_PATH}
    assert wanted.issubset(recording.custom_routes), recording.custom_routes.keys()

    _REST_APP = Starlette(
        routes=[
            Route(path, endpoint=endpoint, methods=methods)
            for path, (endpoint, methods) in recording.custom_routes.items()
            if path in wanted
        ]
    )
    return _REST_APP


async def _call(path: str, method: str = "POST", **kwargs) -> httpx.Response:
    transport = httpx.ASGITransport(app=_rest_app())
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        return await client.request(method, path, **kwargs)


def _mock_websocket(connection_id: str | None = None) -> AsyncMock:
    ws = AsyncMock()
    ws.headers = {}
    ws.state = SimpleNamespace()
    if connection_id is not None:
        ws.state.connection_id = connection_id
    ws.close = AsyncMock()
    ws.send_json = AsyncMock()
    return ws


async def _bound_registry(tmp_path) -> tuple[PluginRegistry, str]:
    """Foreign session FIRST, guarded session SECOND. Returns (registry, bound session id)."""
    registry = PluginRegistry()
    PluginHub.configure(registry, loop=asyncio.get_running_loop())
    set_active_guard(build_guard(str(tmp_path), NONCE))

    await registry.register(
        FOREIGN_SESSION, "Neighbour", FOREIGN_HASH, "6000.5.6f1", str(tmp_path)
    )

    payload = RegisterMessage(
        project_name="Game",
        project_hash=BOUND_HASH,
        unity_version="6000.5.6f1",
        project_path=str(tmp_path),
        canonical_project_root=str(tmp_path),
        instance_token=NONCE,
    )
    await PluginHub(
        {"type": "websocket"}, receive=AsyncMock(), send=AsyncMock()
    )._handle_register(_mock_websocket("c1"), payload)

    bound = await registry.get_session_id_by_hash(BOUND_HASH)
    assert bound is not None
    assert bound != FOREIGN_SESSION
    return registry, bound


def _command_body(unity_instance: str | None = None) -> dict:
    body: dict = {"type": "manage_editor", "params": {}}
    if unity_instance is not None:
        body["unity_instance"] = unity_instance
    return body


class _DispatchRecorder:
    """Records which session each guarded route actually reached."""

    def __init__(self) -> None:
        self.command_sessions: list[str] = []
        self.custom_tool_hints: list[str] = []

    async def send_command(self, session_id, command_type, params):  # noqa: ARG002
        self.command_sessions.append(session_id)
        return {"success": True, "session_id": session_id}

    def resolve_project_id(self, unity_instance_hint):  # noqa: ARG002
        self.custom_tool_hints.append(unity_instance_hint)
        return f"project-of-{unity_instance_hint}"


async def _run_command(recorder: _DispatchRecorder, unity_instance: str | None = None):
    with patch.object(PluginHub, "send_command", new=recorder.send_command):
        return await _call(
            COMMAND_PATH, "POST", json=_command_body(unity_instance)
        )


async def _run_custom_tools(recorder: _DispatchRecorder, unity_instance: str | None = None):
    service = SimpleNamespace(list_registered_tools=AsyncMock(return_value=[]))
    params = {"instance": unity_instance} if unity_instance is not None else None
    with patch.object(
        server_main.CustomToolService, "get_instance", return_value=service
    ), patch.object(
        server_main,
        "resolve_project_id_for_unity_instance",
        side_effect=recorder.resolve_project_id,
    ):
        response = await _call(CUSTOM_TOOLS_PATH, "GET", params=params)
    return response, service


# ============================================================
# No target: each route may only reach the bound session
# ============================================================


@pytest.mark.asyncio
async def test_command_route_with_no_target_dispatches_only_to_the_bound_session(tmp_path):
    _, bound = await _bound_registry(tmp_path)
    recorder = _DispatchRecorder()

    response = await _run_command(recorder)

    assert response.status_code == 200, response.text
    assert recorder.command_sessions == [bound], "the foreign first session must be ignored"


@pytest.mark.asyncio
async def test_custom_tools_route_with_no_target_uses_only_the_bound_session(tmp_path):
    _, _ = await _bound_registry(tmp_path)
    recorder = _DispatchRecorder()

    response, service = await _run_custom_tools(recorder)

    assert response.status_code == 200, response.text
    assert response.json()["project_id"] == f"project-of-{BOUND_HASH}"
    assert recorder.custom_tool_hints == [BOUND_HASH]
    service.list_registered_tools.assert_awaited_once_with(f"project-of-{BOUND_HASH}")


@pytest.mark.asyncio
async def test_explicit_foreign_target_is_refused_on_both_routes(tmp_path):
    _, _ = await _bound_registry(tmp_path)
    recorder = _DispatchRecorder()

    command = await _run_command(recorder, f"Neighbour@{FOREIGN_HASH}")
    tools, _ = await _run_custom_tools(recorder, f"Neighbour@{FOREIGN_HASH}")

    assert command.status_code == 403, command.text
    assert tools.status_code == 403, tools.text
    assert recorder.command_sessions == []
    assert recorder.custom_tool_hints == []


@pytest.mark.asyncio
async def test_explicit_bound_target_succeeds_on_both_routes(tmp_path):
    _, bound = await _bound_registry(tmp_path)
    recorder = _DispatchRecorder()

    command = await _run_command(recorder, f"Game@{BOUND_HASH}")
    tools, _ = await _run_custom_tools(recorder, f"Game@{BOUND_HASH}")

    assert command.status_code == 200, command.text
    assert tools.status_code == 200, tools.text
    assert recorder.command_sessions == [bound]
    assert recorder.custom_tool_hints == [BOUND_HASH]


# ============================================================
# Fail-closed states
# ============================================================


@pytest.mark.asyncio
async def test_bound_session_removed_while_a_foreign_session_remains_fails_both_routes(tmp_path):
    registry, bound = await _bound_registry(tmp_path)
    recorder = _DispatchRecorder()
    await registry.unregister(bound)

    command = await _run_command(recorder)
    tools, _ = await _run_custom_tools(recorder)

    assert command.status_code == 503, command.text
    assert tools.status_code == 503, tools.text
    assert recorder.command_sessions == [], "the foreign session must never be used as a fallback"
    assert recorder.custom_tool_hints == []


@pytest.mark.asyncio
async def test_bound_hash_mapping_to_a_missing_session_fails_both_routes(tmp_path):
    registry, bound = await _bound_registry(tmp_path)
    recorder = _DispatchRecorder()

    # The hash -> id mapping survives, but the exact session the registration published is gone.
    # This is the stale-id case: the selector must not hand the id back to the routes.
    real_get_session = registry.get_session

    async def missing_session(session_id):
        if session_id == bound:
            return None
        return await real_get_session(session_id)

    with patch.object(registry, "get_session", side_effect=missing_session):
        command = await _run_command(recorder)
        tools, _ = await _run_custom_tools(recorder)

    assert command.status_code == 503, command.text
    assert tools.status_code == 503, tools.text
    assert recorder.command_sessions == []
    assert recorder.custom_tool_hints == []


@pytest.mark.asyncio
async def test_no_binding_fails_both_routes(tmp_path):
    registry = PluginRegistry()
    PluginHub.configure(registry, loop=asyncio.get_running_loop())
    set_active_guard(build_guard(str(tmp_path), NONCE))
    await registry.register(
        FOREIGN_SESSION, "Neighbour", FOREIGN_HASH, "6000.5.6f1", str(tmp_path)
    )
    recorder = _DispatchRecorder()

    command = await _run_command(recorder)
    tools, _ = await _run_custom_tools(recorder)

    assert command.status_code == 503, command.text
    assert tools.status_code == 503, tools.text
    assert recorder.command_sessions == []
    assert recorder.custom_tool_hints == []


@pytest.mark.asyncio
async def test_registering_state_fails_both_routes(tmp_path):
    registry = PluginRegistry()
    PluginHub.configure(registry, loop=asyncio.get_running_loop())
    guard = build_guard(str(tmp_path), NONCE)
    set_active_guard(guard)
    await registry.register(
        FOREIGN_SESSION, "Neighbour", FOREIGN_HASH, "6000.5.6f1", str(tmp_path)
    )

    # Reserve the binding without publishing the registry entry.
    decision = guard.authorize_registration(
        project_name="Game",
        project_hash=BOUND_HASH,
        project_path=str(tmp_path),
        canonical_root=str(tmp_path),
        instance_token=NONCE,
        connection_id="c1",
    )
    assert decision.allowed
    assert guard.is_registering

    recorder = _DispatchRecorder()
    command = await _run_command(recorder)
    tools, _ = await _run_custom_tools(recorder)

    assert command.status_code == 503, command.text
    assert tools.status_code == 503, tools.text
    assert recorder.command_sessions == []
    assert recorder.custom_tool_hints == []


@pytest.mark.asyncio
async def test_foreign_session_is_never_a_fallback_when_the_bound_one_is_absent(tmp_path):
    """A foreign session that happens to be the only (or default) session is still ignored."""
    registry, bound = await _bound_registry(tmp_path)
    await registry.unregister(bound)

    # Only the foreign session is left, and it is the "first"/default by every legacy rule.
    sessions = await registry.list_sessions()
    assert list(sessions.keys()) == [FOREIGN_SESSION]

    recorder = _DispatchRecorder()
    response = await _run_command(recorder)

    assert response.status_code == 503, response.text
    assert recorder.command_sessions == []
