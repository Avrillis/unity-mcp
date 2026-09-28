"""Route/ownership isolation for a dedicated (managed) MCP server instance.

This is the server half of the TLN multi-worker isolation contract. A dedicated server is
launched per Unity Editor with::

    --http-url http://127.0.0.1:<port> --unity-project-root <canonical project root>
                                       --unity-instance-token <per-launch nonce>

When ``--unity-project-root`` is present the server is *guarded*: it binds itself to that
project identity and then

  * rejects a ``register`` message whose project is not the bound project,
  * rejects a registration that does not echo the expected launch nonce, and
  * binds exactly ONE Unity connection and rejects any other registration while that
    connection is live, and
  * dispatches only to that bound Unity instance - never to a "first", "active" or
    "default" instance.

A guarded server that was launched without a usable launch nonce stays ENABLED but UNUSABLE:
it refuses every registration and every dispatch. It never degrades to an unguarded server,
because "guarded" is what stops one worker's agent from reaching a sibling route.

This is deliberately narrow, per the architecture decision. It provides route and ownership
isolation; it is **not** an authorization system. The nonce is correlation data, not a
credential, and none of this defends against hostile processes under the same OS account.

With no ``--unity-project-root`` the guard is disabled and the server keeps its existing
multi-instance behaviour unchanged.
"""

from __future__ import annotations

import logging
import os
from dataclasses import dataclass
from urllib.parse import urlparse

logger = logging.getLogger(__name__)

# Upper bound for the launch nonce. The package sends a 32-character hex GUID; anything
# wildly larger is not a nonce this package produced.
MAX_NONCE_LENGTH = 200


def is_usable_nonce(value: str | None) -> bool:
    """True when ``value`` is a structurally valid launch nonce.

    The nonce is correlation data, not a credential, but it still has to be a value this
    package could have produced: non-empty, bounded, and free of whitespace/control
    characters so it can never be smuggled into a log line or a command.
    """
    if not isinstance(value, str):
        return False

    stripped = value.strip()
    if not stripped or stripped != value or len(stripped) > MAX_NONCE_LENGTH:
        return False

    for char in stripped:
        if char.isspace() or ord(char) < 0x20 or ord(char) == 0x7F:
            return False

    return True


def canonical_project_root(path: str | None) -> str | None:
    """Canonicalize a project root for comparison, or None when unusable.

    Windows comparisons are case-insensitive and separator-insensitive; POSIX comparisons
    stay case-sensitive. ``realpath`` resolves symlinks/short names so a project reached via
    a mapped drive, a 8.3 name or a junction still matches its canonical form.
    """
    if path is None:
        return None
    if not isinstance(path, str):
        return None

    stripped = path.strip().strip('"')
    if not stripped:
        return None

    # Reject embedded NULs and other characters that cannot appear in a real path rather
    # than letting a malformed value fall through to a permissive comparison.
    if "\x00" in stripped or "\r" in stripped or "\n" in stripped:
        return None

    try:
        expanded = os.path.expanduser(stripped)
        resolved = os.path.realpath(os.path.abspath(os.path.normpath(expanded)))
    except (OSError, ValueError):
        return None

    if not resolved:
        return None

    return os.path.normcase(resolved)


def project_roots_match(candidate: str | None, expected: str | None) -> bool:
    """True when two project roots canonicalize to the same location."""
    candidate_root = canonical_project_root(candidate)
    expected_root = canonical_project_root(expected)
    if candidate_root is None or expected_root is None:
        return False
    return candidate_root == expected_root


@dataclass(frozen=True)
class GuardDecision:
    allowed: bool
    reason: str = ""

    def __bool__(self) -> bool:  # pragma: no cover - convenience only
        return self.allowed


class ManagedRouteGuard:
    """Binds one dedicated server instance to one Unity project identity."""

    def __init__(
        self,
        project_root: str | None,
        instance_token: str | None,
        canonical_root: str | None,
        invalid_reason: str | None = None,
    ) -> None:
        self._project_root = project_root
        self._instance_token = instance_token
        self._canonical_root = canonical_root
        self._invalid_reason = invalid_reason
        # Populated once the bound Unity instance registers.
        self._bound_project_hash: str | None = None
        self._bound_project_name: str | None = None
        self._bound_connection_id: str | None = None

    # ---------------------------------------------------------------- properties
    @property
    def enabled(self) -> bool:
        """True when a project root was supplied (even if it turned out to be invalid)."""
        return self._project_root is not None or self._invalid_reason is not None

    @property
    def usable(self) -> bool:
        """True when the guard has a valid project root and can authorize registrations."""
        return self._invalid_reason is None and self._canonical_root is not None

    @property
    def requires_nonce(self) -> bool:
        """Guarded mode always requires the launch nonce, not just when one was supplied."""
        return self.usable

    @property
    def project_root(self) -> str | None:
        return self._project_root

    @property
    def invalid_reason(self) -> str | None:
        return self._invalid_reason

    @property
    def bound_project_hash(self) -> str | None:
        return self._bound_project_hash

    @property
    def bound_project_name(self) -> str | None:
        return self._bound_project_name

    @property
    def bound_connection_id(self) -> str | None:
        return self._bound_connection_id

    @property
    def has_bound_instance(self) -> bool:
        """True while a Unity connection is bound to this dedicated server."""
        return self._bound_project_hash is not None

    def describe(self) -> str:
        if not self.enabled:
            return "disabled"
        if not self.usable:
            return f"invalid ({self._invalid_reason})"
        return f"guarded root='{self._project_root}' (nonce-required)"

    # ------------------------------------------------------------- dispatch target
    def resolve_bound_hash(self) -> str | None:
        """The only Unity instance this server may dispatch to, or None when unbound.

        Used for absent-target dispatch and for resolving a session without ever falling
        back to ordinary first/active/default instance selection.
        """
        if not self.enabled:
            return None
        return self._bound_project_hash

    # ------------------------------------------------------------ registration
    def authorize_registration(
        self,
        *,
        project_name: str | None,
        project_hash: str | None,
        project_path: str | None,
        canonical_root: str | None,
        instance_token: str | None,
        connection_id: str | None = None,
    ) -> GuardDecision:
        """Validate an incoming Unity ``register`` message.

        Fails closed: a guard that could not be constructed, a missing project identity or a
        missing/mismatched nonce all refuse the registration. Only one Unity connection may be
        bound at a time; a second concurrent registration is refused even when it echoes the
        same project root and the same nonce.
        """
        if not self.enabled:
            return GuardDecision(True)

        if not self.usable:
            return GuardDecision(
                False,
                f"this dedicated MCP server is not usable: {self._invalid_reason}",
            )

        if not project_hash:
            return GuardDecision(False, "registration is missing project_hash")

        # Accept either the canonical root the package sends explicitly or the raw project
        # path, so an older package build that predates canonical_project_root still works.
        candidates = [canonical_root, project_path]
        matched = any(project_roots_match(candidate, self._project_root) for candidate in candidates)
        if not matched:
            return GuardDecision(
                False,
                "registration is for a different Unity project than this server was launched for",
            )

        if not instance_token:
            return GuardDecision(
                False, "registration did not echo the expected launch nonce")
        if instance_token != self._instance_token:
            return GuardDecision(False, "registration echoed the wrong launch nonce")

        # Exactly one bound Unity connection. A reconnect of the SAME logical guarded identity
        # (same project + same nonce) is only accepted once the previous connection is gone -
        # the hub releases the binding on disconnect. While a binding is live, a second
        # registration cannot take it over, even with identical root and nonce.
        if self._bound_project_hash is not None:
            same_connection = (
                connection_id is not None
                and self._bound_connection_id is not None
                and connection_id == self._bound_connection_id
            )
            if not same_connection:
                return GuardDecision(
                    False,
                    "another Unity connection is already bound to this dedicated MCP server",
                )
        else:
            self._bound_connection_id = connection_id

        self._bound_project_hash = project_hash
        self._bound_project_name = project_name or None
        return GuardDecision(True)

    def release_binding(self) -> None:
        """Forget the bound instance (the guarded session disconnected)."""
        self._bound_project_hash = None
        self._bound_project_name = None
        self._bound_connection_id = None

    # ---------------------------------------------------------------- dispatch
    def authorize_target(self, unity_instance: str | None) -> GuardDecision:
        """Validate an explicit dispatch target.

        ``None`` means "no explicit target": the guarded server may then only use its own bound
        instance, and only once one has registered. Before that, every dispatch fails closed.
        An explicit target that cannot be matched to the bound instance is refused before any
        command is sent, so a caller cannot address a neighbouring route.
        """
        if not self.enabled:
            return GuardDecision(True)

        if not self.usable:
            return GuardDecision(
                False,
                f"this dedicated MCP server is not usable: {self._invalid_reason}",
            )

        if unity_instance is None or not str(unity_instance).strip():
            if self._bound_project_hash is None:
                return GuardDecision(
                    False,
                    "no Unity instance is registered with this dedicated MCP server yet",
                )
            return GuardDecision(True)

        target = str(unity_instance).strip()

        bound_hash = self._bound_project_hash
        bound_name = self._bound_project_name or ""

        if bound_hash is None:
            # Nothing has registered yet; there is no identity to check against, so refuse
            # rather than let the caller fall through to a different instance.
            return GuardDecision(
                False, "no Unity instance is registered with this dedicated server yet")

        if "@" in target:
            target_name, _, target_hash = target.rpartition("@")
            matches = bool(target_hash) and target_hash.lower() == bound_hash.lower()
        else:
            # A bare value may be either the instance hash or the project name.
            candidate = target.lower()
            matches = candidate == bound_hash.lower() or (
                bool(bound_name) and candidate == bound_name.lower())

        if matches:
            return GuardDecision(True)

        return GuardDecision(
            False,
            "targeting a Unity instance other than this dedicated server's own instance is refused",
        )


def build_guard(project_root: str | None, instance_token: str | None) -> ManagedRouteGuard:
    """Construct the guard from server launch arguments.

    With no ``--unity-project-root`` the guard is disabled and the server behaves exactly as
    before. A supplied but unusable root OR a missing/invalid launch nonce yields an
    enabled-but-unusable guard, which refuses every registration and dispatch instead of
    silently degrading to an unguarded server.
    """
    if project_root is None:
        return ManagedRouteGuard(None, instance_token, None)

    canonical = canonical_project_root(project_root)
    if canonical is None:
        logger.error(
            "--unity-project-root value %r is not a usable path; refusing all Unity "
            "registrations and dispatch for this server",
            project_root,
        )
        return ManagedRouteGuard(
            project_root, instance_token, None,
            invalid_reason="the supplied project root is not a usable path",
        )

    if not is_usable_nonce(instance_token):
        logger.error(
            "Dedicated mode was requested without a usable --unity-instance-token; refusing all "
            "Unity registrations and dispatch for this server"
        )
        return ManagedRouteGuard(
            project_root, instance_token, canonical,
            invalid_reason="guarded dedicated mode requires a launch nonce (--unity-instance-token)",
        )

    return ManagedRouteGuard(project_root, instance_token, canonical)


_active_guard: ManagedRouteGuard = ManagedRouteGuard(None, None, None)


# Server-side variables that can redirect a launched server away from the endpoint its editor
# owns. A guarded (dedicated) launch must never let an inherited value win.
ROUTING_ENVIRONMENT_VARIABLES = (
    "UNITY_MCP_HTTP_URL",
    "UNITY_MCP_HTTP_HOST",
    "UNITY_MCP_HTTP_PORT",
)


def apply_guarded_binding_environment(
    environ,
    guarded: bool,
    cli_url: str,
    cli_host: str | None,
    cli_port: int | None,
) -> tuple[str, str, int, list[str]]:
    """Resolve the HTTP binding for this process.

    Returns ``(url, host, port, ignored_names)``.

    In guarded (dedicated) mode the endpoint supplied on the command line wins and inherited
    server-side routing variables are removed from ``environ`` before anything else reads them,
    so an inherited value can never redirect the server away from the endpoint its editor owns.
    Without a project root the existing environment-over-command-line precedence is preserved
    unchanged.
    """
    ignored: list[str] = []

    env_url = environ.get("UNITY_MCP_HTTP_URL")
    env_host = environ.get("UNITY_MCP_HTTP_HOST")
    env_port_raw = environ.get("UNITY_MCP_HTTP_PORT")

    if guarded:
        for name, value in (
            ("UNITY_MCP_HTTP_URL", env_url),
            ("UNITY_MCP_HTTP_HOST", env_host),
            ("UNITY_MCP_HTTP_PORT", env_port_raw),
        ):
            if value is not None:
                ignored.append(name)
            environ.pop(name, None)
        url = cli_url
    else:
        url = env_url or cli_url

    parsed = urlparse(url)
    host = cli_host or (None if guarded else env_host) or parsed.hostname or "127.0.0.1"

    env_port: int | None = None
    if not guarded and env_port_raw is not None:
        try:
            env_port = int(env_port_raw)
        except ValueError:
            logger.warning(
                "Invalid UNITY_MCP_HTTP_PORT value %r, ignoring", env_port_raw)
            env_port = None

    port = cli_port or env_port or parsed.port or 8080
    return url, host, port, ignored


def set_active_guard(guard: ManagedRouteGuard | None) -> None:
    """Install the guard for this server process (called once from ``main``)."""
    global _active_guard
    _active_guard = guard if guard is not None else ManagedRouteGuard(None, None, None)


def get_active_guard() -> ManagedRouteGuard:
    """The guard for this server process; disabled unless a project root was supplied."""
    return _active_guard


__all__ = [
    "ROUTING_ENVIRONMENT_VARIABLES",
    "GuardDecision",
    "ManagedRouteGuard",
    "apply_guarded_binding_environment",
    "build_guard",
    "canonical_project_root",
    "get_active_guard",
    "is_usable_nonce",
    "project_roots_match",
    "set_active_guard",
]
