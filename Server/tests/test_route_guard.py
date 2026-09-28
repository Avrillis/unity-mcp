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
    apply_guarded_binding_environment,
    build_guard,
    canonical_project_root,
    get_active_guard,
    is_usable_nonce,
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


def _make_mock_websocket(connection_id=None):
    ws = AsyncMock()
    ws.headers = {}
    ws.state = SimpleNamespace()
    if connection_id is not None:
        ws.state.connection_id = connection_id
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
        guard = build_guard(str(tmp_path), "nonce-1")

        assert guard.enabled is True
        assert guard.usable is True

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="abc123",
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token="nonce-1",
        )
        assert decision.allowed is True

    def test_correct_project_accepted_via_canonical_field_only(self, tmp_path):
        """A package build that sends only canonical_project_root must still register."""
        guard = build_guard(str(tmp_path), "nonce-1")

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash="abc123",
            project_path=None,
            canonical_root=str(tmp_path),
            instance_token="nonce-1",
        )
        assert decision.allowed is True

    def test_foreign_project_rejected(self, tmp_path):
        own = tmp_path / "OwnProject"
        other = tmp_path / "OtherProject"
        own.mkdir()
        other.mkdir()

        guard = build_guard(str(own), "nonce-1")
        decision = guard.authorize_registration(
            project_name="Other",
            project_hash="deadbeef",
            project_path=str(other),
            canonical_root=None,
            instance_token="nonce-1",
        )

        assert decision.allowed is False
        assert "different Unity project" in decision.reason

    def test_registration_without_project_identity_rejected(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")

        decision = guard.authorize_registration(
            project_name="Game",
            project_hash=None,
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token="nonce-1",
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

    def test_guarded_mode_without_a_nonce_is_enabled_but_unusable(self, tmp_path):
        """--unity-project-root without a usable nonce must never degrade to guarded-but-open."""
        for token in (None, "", "   ", "\x00bad", "bad token", "x" * 500):
            guard = build_guard(str(tmp_path), token)

            assert guard.enabled is True, token
            assert guard.usable is False, token

            decision = guard.authorize_registration(
                project_name="Game",
                project_hash="hash1",
                project_path=str(tmp_path),
                canonical_root=None,
                instance_token=token,
            )
            assert decision.allowed is False, token

            assert guard.authorize_target(None).allowed is False, token
            assert guard.authorize_target("Game@hash1").allowed is False, token


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
        # The registration is only dispatchable once the hub has published the session entry.
        guard.mark_bound()
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

    def test_absent_target_allowed_only_after_binding(self, tmp_path):
        """No explicit target means "this server's bound instance", never "any instance"."""
        guard = self._bound_guard(tmp_path)

        assert guard.authorize_target(None).allowed is True
        assert guard.authorize_target("   ").allowed is True

    def test_target_refused_before_any_registration(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")

        decision = guard.authorize_target("Anyone@deadbeef")
        assert decision.allowed is False
        assert "registered" in decision.reason

    def test_absent_target_refused_before_any_registration(self, tmp_path):
        """Absent-target dispatch must fail closed until an instance is bound."""
        guard = build_guard(str(tmp_path), "nonce-1")

        decision = guard.authorize_target(None)
        assert decision.allowed is False
        assert "registered" in decision.reason

        assert guard.resolve_bound_hash() is None


class TestSingleRegistration:
    """Exactly one Unity connection may be bound to a guarded dedicated server."""

    def _register_with(
        self, guard, tmp_path, *, connection_id, project_hash="hash1",
        instance_token="nonce-1", project_name="Game",
    ):
        decision = guard.authorize_registration(
            project_name=project_name,
            project_hash=project_hash,
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token=instance_token,
            connection_id=connection_id,
        )
        if decision.allowed:
            # Mirrors the hub: a reserved binding becomes dispatchable once its registry entry
            # has been published.
            guard.mark_bound()
        return decision

    def _reserve_only(
        self, guard, tmp_path, *, connection_id, project_hash="hash1",
        instance_token="nonce-1", project_name="Game",
    ):
        """Authorise a registration WITHOUT publishing it (leaves the binding REGISTERING)."""
        return guard.authorize_registration(
            project_name=project_name,
            project_hash=project_hash,
            project_path=str(tmp_path),
            canonical_root=None,
            instance_token=instance_token,
            connection_id=connection_id,
        )

    def test_registration_in_progress_refuses_a_second_registration(self, tmp_path):
        """A reservation that has not been published must not be taken over."""
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._reserve_only(guard, tmp_path, connection_id="c1").allowed is True
        assert guard.binding_state == ManagedRouteGuard.BINDING_REGISTERING

        decision = self._reserve_only(guard, tmp_path, connection_id="c2")

        assert decision.allowed is False
        assert "in progress" in decision.reason
        assert guard.bound_connection_id == "c1"

    def test_reserved_binding_is_not_dispatchable_until_published(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._reserve_only(guard, tmp_path, connection_id="c1").allowed is True

        # While REGISTERING there is no dispatchable instance, so every target fails closed.
        assert guard.resolve_bound_hash() is None
        assert guard.has_bound_instance is False
        assert guard.authorize_target(None).allowed is False
        assert guard.authorize_target("hash1").allowed is False

        guard.mark_bound()

        assert guard.binding_state == ManagedRouteGuard.BINDING_BOUND
        assert guard.resolve_bound_hash() == "hash1"
        assert guard.authorize_target(None).allowed is True

    def test_failed_registration_after_reservation_releases_the_binding(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._reserve_only(guard, tmp_path, connection_id="c1").allowed is True

        # A registration that fails after reserving must not leave the route unusable.
        guard.release_binding()

        assert guard.binding_state == ManagedRouteGuard.BINDING_UNBOUND
        assert self._register_with(guard, tmp_path, connection_id="c2").allowed is True

    def test_first_valid_registration_binds_one_identity(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")

        decision = self._register_with(guard, tmp_path, connection_id="c1")

        assert decision.allowed is True
        assert guard.bound_project_hash == "hash1"
        assert guard.bound_connection_id == "c1"
        assert guard.resolve_bound_hash() == "hash1"

    def test_duplicate_registration_from_another_connection_is_refused(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._register_with(guard, tmp_path, connection_id="c1").allowed is True

        decision = self._register_with(guard, tmp_path, connection_id="c2")

        assert decision.allowed is False
        assert "already bound" in decision.reason
        assert guard.bound_connection_id == "c1"

    def test_same_root_and_nonce_cannot_replace_a_bound_connection(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._register_with(guard, tmp_path, connection_id="c1").allowed is True

        # Identical project root AND identical nonce: still not enough to take over a live
        # binding, because the connection identity differs.
        decision = self._register_with(
            guard, tmp_path, connection_id="c2", project_hash="hash1")

        assert decision.allowed is False
        assert guard.bound_project_hash == "hash1"

    def test_unknown_connections_cannot_take_over_a_binding(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._register_with(guard, tmp_path, connection_id="c1").allowed is True

        # A registration whose connection identity cannot be established is refused rather than
        # treated as "the same" connection.
        decision = self._register_with(guard, tmp_path, connection_id=None)

        assert decision.allowed is False

    def test_same_connection_re_registration_is_idempotent(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._register_with(guard, tmp_path, connection_id="c1").allowed is True

        decision = self._register_with(guard, tmp_path, connection_id="c1")

        assert decision.allowed is True

    def test_binding_released_allows_reconnect_of_same_identity(self, tmp_path):
        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._register_with(guard, tmp_path, connection_id="c1").allowed is True

        guard.release_binding()
        assert guard.resolve_bound_hash() is None
        assert guard.authorize_target(None).allowed is False

        decision = self._register_with(guard, tmp_path, connection_id="c2")
        assert decision.allowed is True
        assert guard.resolve_bound_hash() == "hash1"

    def test_reconnect_still_requires_the_matching_identity(self, tmp_path):
        other_project = tmp_path / "OtherProject"
        other_project.mkdir()

        guard = build_guard(str(tmp_path), "nonce-1")
        assert self._register_with(guard, tmp_path, connection_id="c1").allowed is True
        guard.release_binding()

        assert self._register_with(
            guard, tmp_path, connection_id="c2", instance_token="wrong").allowed is False

        # A reconnect from a different project is still refused (and leaves nothing bound).
        decision = guard.authorize_registration(
            project_name="Other",
            project_hash="other",
            project_path=str(other_project),
            canonical_root=None,
            instance_token="nonce-1",
            connection_id="c3",
        )
        assert decision.allowed is False
        assert guard.resolve_bound_hash() is None


class TestGuardedBindingEnvironment:
    """Inherited server-side routing variables must not redirect a dedicated server."""

    def test_guarded_mode_ignores_and_removes_inherited_routing_variables(self):
        environ = {
            "UNITY_MCP_HTTP_URL": "http://127.0.0.1:9999",
            "UNITY_MCP_HTTP_HOST": "10.0.0.5",
            "UNITY_MCP_HTTP_PORT": "9999",
        }

        url, host, port, ignored = apply_guarded_binding_environment(
            environ, True, "http://127.0.0.1:8101", None, None)

        assert url == "http://127.0.0.1:8101"
        assert host == "127.0.0.1"
        assert port == 8101
        assert set(ignored) == {
            "UNITY_MCP_HTTP_URL", "UNITY_MCP_HTTP_HOST", "UNITY_MCP_HTTP_PORT"}
        assert "UNITY_MCP_HTTP_URL" not in environ
        assert "UNITY_MCP_HTTP_HOST" not in environ
        assert "UNITY_MCP_HTTP_PORT" not in environ

    def test_guarded_mode_without_inherited_values_is_unchanged(self):
        environ = {}

        url, host, port, ignored = apply_guarded_binding_environment(
            environ, True, "http://127.0.0.1:8101", None, None)

        assert (url, host, port, ignored) == ("http://127.0.0.1:8101", "127.0.0.1", 8101, [])

    def test_unguarded_mode_keeps_environment_precedence(self):
        environ = {"UNITY_MCP_HTTP_URL": "http://127.0.0.1:9999"}

        url, host, port, ignored = apply_guarded_binding_environment(
            environ, False, "http://127.0.0.1:8101", None, None)

        assert url == "http://127.0.0.1:9999"
        assert port == 9999
        assert ignored == []
        assert environ["UNITY_MCP_HTTP_URL"] == "http://127.0.0.1:9999"

    def test_guarded_mode_honours_explicit_command_line_host_and_port(self):
        environ = {"UNITY_MCP_HTTP_PORT": "9999"}

        url, host, port, _ = apply_guarded_binding_environment(
            environ, True, "http://127.0.0.1:8101", "127.0.0.1", 8102)

        assert (host, port) == ("127.0.0.1", 8102)

    def test_nonce_shape_validation(self):
        assert is_usable_nonce("0f0a1b2c3d4e5f60718293a4b5c6d7e8") is True
        assert is_usable_nonce("a" * 32) is True
        for bad in (None, "", "   ", " padded ", "with space", "nul\x00byte", "x" * 500, 123):
            assert is_usable_nonce(bad) is False, bad


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

    @pytest.mark.asyncio
    async def test_absent_target_dispatch_uses_only_the_bound_session(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        guard = build_guard(str(tmp_path), "nonce-1")
        set_active_guard(guard)

        ws = _make_mock_websocket(connection_id="c1")
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        # A neighbour session exists in the same registry: absent-target dispatch must still
        # resolve to the bound instance, never to "the first" or "the only other" one.
        await registry.register(
            "neighbour-session", "Neighbour", "neighbourhash", "6000.5.6f1")

        session_id = await PluginHub._resolve_session_id(None)
        resolved = await registry.get_session(session_id)

        assert session_id != "neighbour-session"
        assert resolved.project_hash == "hash1"

    @pytest.mark.asyncio
    async def test_first_session_fallback_cannot_bypass_the_bound_instance(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        guard = build_guard(str(tmp_path), "nonce-1")
        set_active_guard(guard)

        # A foreign session that was connected before the guard bound anything.
        await registry.register(
            "foreign-session", "Neighbour", "neighbourhash", "6000.5.6f1")

        ws = _make_mock_websocket(connection_id="c1")
        hub = _make_hub()
        await _register(
            hub, ws,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        absent = await PluginHub._resolve_session_id(None)
        resolved = await registry.get_session(absent)
        assert resolved.project_hash == "hash1"

        # An explicit foreign target is refused outright rather than resolved.
        with pytest.raises(CrossTargetDispatchError):
            await PluginHub._resolve_session_id("Neighbour@neighbourhash")

    @pytest.mark.asyncio
    async def test_dispatch_refused_until_a_valid_registration_binds(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        set_active_guard(build_guard(str(tmp_path), "nonce-1"))
        await registry.register("other", "Other", "otherhash", "6000.5.6f1")

        # Nothing is bound yet: both absent and explicit dispatch must fail closed instead of
        # falling back to the connected "other" session.
        with pytest.raises(CrossTargetDispatchError):
            await PluginHub._resolve_session_id(None)
        with pytest.raises(CrossTargetDispatchError):
            await PluginHub._resolve_session_id("Other@otherhash")

    @pytest.mark.asyncio
    async def test_duplicate_registration_does_not_replace_the_bound_session(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        guard = build_guard(str(tmp_path), "nonce-1")
        set_active_guard(guard)

        first = _make_mock_websocket(connection_id="c1")
        hub = _make_hub()
        await _register(
            hub, first,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )
        bound_session = next(iter((await registry.list_sessions()).keys()))

        # A second connection that copies the same root and nonce cannot take the binding.
        second = _make_mock_websocket(connection_id="c2")
        await _register(
            hub, second,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        second.close.assert_awaited_once_with(code=4403)
        assert guard.bound_connection_id == "c1"
        assert await registry.get_session_id_by_hash("hash1") == bound_session

    @pytest.mark.asyncio
    async def test_reconnect_after_disconnect_rebinds_the_same_identity(self, tmp_path):
        registry = PluginRegistry()
        PluginHub.configure(registry, loop=asyncio.get_running_loop())
        guard = build_guard(str(tmp_path), "nonce-1")
        set_active_guard(guard)

        first = _make_mock_websocket(connection_id="c1")
        hub = _make_hub()
        await _register(
            hub, first,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )
        assert guard.bound_project_hash == "hash1"

        # The bound connection goes away entirely (a domain reload): its session is removed and
        # the guard releases the binding, so the same guarded identity may reconnect.
        session_id = next(iter((await registry.list_sessions()).keys()))
        await registry.unregister(session_id)
        await PluginHub._release_guard_binding_if_orphaned()
        assert guard.bound_project_hash is None

        # While unbound nothing may be dispatched.
        with pytest.raises(CrossTargetDispatchError):
            await PluginHub._resolve_session_id(None)

        second = _make_mock_websocket(connection_id="c2")
        await _register(
            hub, second,
            project_name="Game",
            project_hash="hash1",
            project_path=str(tmp_path),
            instance_token="nonce-1",
        )

        second.close.assert_not_called()
        assert guard.bound_project_hash == "hash1"
        assert guard.bound_connection_id == "c2"
        assert await PluginHub._resolve_session_id(None) is not None
