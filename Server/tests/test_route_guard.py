"""Tests for dedicated-server route/ownership isolation (transport/route_guard.py).

These cover the narrow server-side guard required by the TLN multi-worker isolation
architecture: bind one dedicated server to one Unity project, require the launch nonce to be
echoed, and refuse cross-target dispatch. They are hermetic and never start Unity.
"""

import asyncio
import os
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from transport.plugin_hub import CrossTargetDispatchError, PluginHub
from transport.plugin_registry import PluginRegistry
from transport.route_guard import (
    ManagedRouteGuard,
    build_guard,
    canonical_project_root,
    get_active_guard,
    project_roots_match,
    set_active_guard,
)
from transport.models import RegisterMessage


@pytest.fixture(autouse=True)
def _reset_guard():
    """Keep the process-wide guard from leaking between tests."""
    previous = get_active_guard()
    yield
    set_active_guard(previous)


@pytest.fixture(autouse=True)
def _reset_plugin_hub():
    old_registry = PluginHub._registry
    old_lock = PluginHub._lock
    old_loop = PluginHub._loop
    old_connections = PluginHub._connections.copy()
    old_pending = PluginHub._pending.copy()

    yield

    PluginHub._registry = old_registry
    PluginHub._lock = old_lock
    PluginHub._loop = old_loop
    PluginHub._connections = old_connections
    PluginHub._pending = old_pending


def _make_mock_websocket():
    ws = AsyncMock()
    ws.headers = {}
    ws.state = SimpleNamespace()
    ws.close = AsyncMock()
    ws.send_json = AsyncMock()
    return ws


def _make_hub():
    scope = {"type": "websocket"}
    return PluginHub(scope, receive=AsyncMock(), send=AsyncMock())


def _register(hub, ws, *, project_name, project_hash, project_path=None,
              canonical_root=None, instance_token=None):
    payload = RegisterMessage(
        project_name=project_name,
        project_hash=project_hash,
        unity_version="6000.5.6f1",
        project_path=project_path,
        canonical_project_root=canonical_root,
        instance_token=instance_token,
    )
    return hub._handle_register(ws, payload)


class TestGuardConstruction:
    def test_absent_project_root_leaves_guard_disabled(self):
        """No --unity-project-root must preserve the existing multi-instance behaviour."""
        guard = build_guard(None, None)

        assert guard.enabled is False
        assert guard.usable is False
        assert guard.authorize_registration(
            project_name="Anything",
            project_hash="abc123",
            project_path=os.getcwd(),
            canonical_root=None,
            instance_token=None,
        ).allowed is True
        assert guard.authorize_target("SomeOther@deadbeef").allowed is True

    def test_correct_project_accepted(self, tmp_path):
        guard = build_guard(str(tmp_path), None)

        assert guard.enabled is True
        assert guard.usable is True

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="abc123",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token=None,
        )
        assert decision.allowed is True

    def test_correct_project_accepted_via_canonical_field_only(self, tmp_path):
        """A package build that sends only canonical_project_root must still register."""
        guard = build_guard(str(tmp_path), None)

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="abc123",
            project_path=None,
            canonical_root=str(tmp_path),
            instance_token=None,
        )
        assert decision.allowed is True

    def test_foreign_project_rejected(self, tmp_path):
        own = tmp_path / "OwnProject"
        other = tmp_path / "OtherProject"
        own.mkdir()
        other.mkdir()

        guard = build_guard(str(own), None)
        decision = guard.authorize_registration(
            project_name="Other",
            project_hash="deadbeef",
            project_path=str(other),
            canonical_root=None,
            instance_token=None,
        )

        assert decision.allowed is False
        assert "different Unity project" in decision.reason

    def test_registration_without_project_identity_rejected(self, tmp_path):
        guard = build_guard(str(tmp_path), None)

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash=None,
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token=None,
        )

        assert decision.allowed is False

    def test_malformed_project_root_fails_safely(self):
        """A supplied-but-unusable root must refuse everything, not degrade to unguarded."""
        for value in ("", "   ", "bad\x00root"):
            guard = build_guard(value, "nonce-1")

            assert guard.enabled is True
            assert guard.usable is False

            registration = guard.authorize_registration(
                project_name="Game",
                project_hash="abc123",
                project_path=os.getcwd(),
                canonical_root=os.getcwd(),
                instance_token="nonce-1",
            )
            assert registration.allowed is False, value

            assert guard.authorize_target("someone@deadbeef").allowed is False, value

    def test_canonical_root_normalizes_separators_and_dot_segments(self, tmp_path):
        nested = tmp_path / "Project"
        nested.mkdir()

        with_dots = os.path.join(str(tmp_path), ".", "Project", "..", "Project")
        assert project_roots_match(with_dots, str(nested)) is True
        assert project_roots_match(str(nested) + os.sep, str(nested)) is True

    def test_path_comparison_matches_platform_case_semantics(self, tmp_path):
        """Windows paths compare case-insensitively; POSIX paths do not."""
        project = tmp_path / "GameProject"
        project.mkdir()

        upper = str(project).upper()
        if os.name == "nt":
            assert project_roots_match(upper, str(project)) is True
        else:
            assert project_roots_match(upper, str(project)) is False

        assert canonical_project_root(str(project)) == canonical_project_root(
            str(project) + os.sep)


class TestLaunchNonce:
    def test_expected_nonce_accepted(self, tmp_path):
        guard = build_guard(str(tmp_path), "abc123nonce")

        assert guard.requires_nonce is True
        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token="abc123nonce",
        )
        assert decision.allowed is True

    def test_wrong_nonce_rejected(self, tmp_path):
        guard = build_guard(str(tmp_path), "abc123nonce")

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token="someothernonce",
        )
        assert decision.allowed is False
        assert "nonce" in decision.reason

    def test_missing_nonce_rejected(self, tmp_path):
        guard = build_guard(str(tmp_path), "abc123nonce")

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token=None,
        )
        assert decision.allowed is False
        assert "nonce" in decision.reason

    def test_nonce_not_required_when_server_launched_without_one(self, tmp_path):
        guard = build_guard(str(tmp_path), None)

        assert guard.requires_nonce is False
        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token=None,
        )
        assert decision.allowed is True


class TestDispatchTargeting:
    def _bound_guard(self, tmp_path) -> ManagedRouteGuard:
        guard = build_guard(str(tmp_path), "nonce-1")
        assert guard.authorize_registration(
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token="nonce-1",
        ).allowed
        return guard

    def test_cross_target_dispatch_rejected(self, tmp_path):
        guard = self._bound_guard(tmp_path)

        decision = guard.authorize_target("Neighbour@deadbeef")
        assert decision.allowed is False
        assert "other than" in decision.reason

    def test_own_hash_target_allowed(self, tmp_path):
        guard = self._bound_guard(tmp_path)

        assert guard.authorize_target("hash1").allowed is True
        assert guard.authorize_target("Game@hash1").allowed is True

    def test_own_project_name_target_allowed(self, tmp_path):
        guard = self._bound_guard(tmp_path)

        assert guard.authorize_target("Game").allowed is True

    def test_absent_target_allowed(self, tmp_path):
        """No explicit target means "this server's only instance"."""
        guard = self._bound_guard(tmp_path)

        assert guard.authorize_target(None).allowed is True
        assert guard.authorize_target("   ").allowed is True

    def test_target_refused_before_any_registration(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")

        decision = guard.authorize_target("Anyone@deadbeef")
        assert decision.allowed is False
        assert "registered" in decision.reason


class TestPluginHubGuarding:
    @pytest.mark.asyncio
    async def test_hub_accepts_matching_registration_and_binds(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        guard = build_guard(str(tmp_path), "nonce-1")
        set_active_guard(guard)

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        ws.close.assert_not_called()
        sessions = await registry.list_sessions()
        assert len(sessions) == 1
        assert guard.bound_project_hash == "hash1"

    @pytest.mark.asyncio
    async def test_hub_rejects_foreign_project_registration(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        own = tmp_path / "Own"
        other = tmp_path / "Other"
        own.mkdir()
        other.mkdir()
        set_active_guard(build_guard(str(own), "nonce-1"))

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Other",
            project_hash="deadbeef",
            project_path=str(other),
            instance_token="nonce-1",
        )

        ws.close.assert_awaited_once_with(code=4403)
        assert await registry.list_sessions() == {}

    @pytest.mark.asyncio
    async def test_hub_rejects_wrong_nonce(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        set_active_guard(build_guard(str(tmp_path), "nonce-1"))

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="wrong-nonce",
        )

        ws.close.assert_awaited_once_with(code=4403)
        assert await registry.list_sessions() == {}

    @pytest.mark.asyncio
    async def test_hub_rejects_registration_when_root_malformed(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        set_active_guard(build_guard("", "nonce-1"))

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        ws.close.assert_awaited_once_with(code=4403)
        assert await registry.list_sessions() == {}

    @pytest.mark.asyncio
    async def test_hub_refuses_cross_target_dispatch(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        set_active_guard(build_guard(str(tmp_path), "nonce-1"))

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        with pytest.raises(CrossTargetDispatchError):
            await PluginHub._resolve_session_id("Neighbour@deadbeef")

    @pytest.mark.asyncio
    async def test_hub_unguarded_mode_unchanged(self, tmp_path):
        """With no project root the hub keeps its previous permissive behaviour."""
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        set_active_guard(build_guard(None, None))

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token=None,
        )

        ws.close.assert_not_called()
        sessions = await registry.list_sessions()
        assert len(sessions) == 1

        assert get_active_guard().authorize_target("Neighbour@deadbeef").allowed is True
        session_id = await PluginHub._resolve_session_id("hash1")
        assert session_id in sessions

    @pytest.mark.asyncio
    async def test_binding_released_when_session_disconnects(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        guard = build_guard(str(tmp_path), "nonce-1")
        set_active_guard(guard)

        ws = _make_mock_websocket()
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )
        assert guard.bound_project_hash == "hash1"

        session_id = next(iter((await registry.list_sessions()).keys()))
        await registry.unregister(session_id)
        await PluginHub._release_guard_binding_if_orphaned()

        assert guard.bound_project_hash is None
        assert guard.authorize_target("hash1").allowed is False
