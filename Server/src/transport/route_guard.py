"""Route/ownership isolation for a dedicated (managed) MCP server instance.

This is the server half of the TLN multi-worker isolation contract. A dedicated server is
launched per Unity Editor with::

    --http-url http://127.0.0.1:<port> --unity-project-root <canonical project root>
                                       --unity-instance-token <per-launch nonce>

When ``--unity-project-root`` is present the server is *guarded*: it binds itself to that
project identity and then

  * rejects a ``register`` message whose project is not the bound project,
  * rejects a registration that does not echo the expected launch nonce, and
  * rejects dispatch to any target other than the bound Unity instance.

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

logger = logging.getLogger(__name__)


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
        return self.usable and bool(self._instance_token)

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

    def describe(self) -> str:
        if not self.enabled:
            return "disabled"
        if not self.usable:
            return f"invalid ({self._invalid_reason})"
        nonce = "nonce-required" if self.requires_nonce else "no-nonce"
        return f"guarded root='{self._project_root}' ({nonce})"

    # ------------------------------------------------------------ registration
    def authorize_registration(
        self,
        *,
        project_name: str | None,
        project_hash: str | None,
        project_path: str | None,
        canonical_root: str | None,
        instance_token: str | None,
    ) -> GuardDecision:
        """Validate an incoming Unity ``register`` message.

        Fails closed: a guard that could not be constructed, a missing project identity or a
        missing/mismatched nonce all refuse the registration.
        """
        if not self.enabled:
            return GuardDecision(True)

        if not self.usable:
            return GuardDecision(
                False,
                f"this server was launched with an unusable --unity-project-root ({self._invalid_reason})",
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

        if self.requires_nonce:
            if not instance_token:
                return GuardDecision(
                    False, "registration did not echo the expected launch nonce")
            if instance_token != self._instance_token:
                return GuardDecision(False, "registration echoed the wrong launch nonce")

        self._bound_project_hash = project_hash
        self._bound_project_name = project_name or None
        return GuardDecision(True)

    def release_binding(self) -> None:
        """Forget the bound instance (the guarded session disconnected)."""
        self._bound_project_hash = None
        self._bound_project_name = None

    # ---------------------------------------------------------------- dispatch
    def authorize_target(self, unity_instance: str | None) -> GuardDecision:
        """Validate an explicit dispatch target.

        ``None`` means "no explicit target": the guarded server only ever has one instance, so
        that is allowed. A target that cannot be matched to the bound instance is refused
        before any command is sent, so a caller cannot address a neighbouring route.
        """
        if not self.enabled:
            return GuardDecision(True)

        if not self.usable:
            return GuardDecision(
                False,
                f"this server was launched with an unusable --unity-project-root ({self._invalid_reason})",
            )

        if unity_instance is None or not str(unity_instance).strip():
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
    before. A supplied but unusable root yields an enabled-but-unusable guard, which refuses
    every registration and dispatch instead of silently degrading to an unguarded server.
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

    return ManagedRouteGuard(project_root, instance_token, canonical)


_active_guard: ManagedRouteGuard = ManagedRouteGuard(None, None, None)


def set_active_guard(guard: ManagedRouteGuard | None) -> None:
    """Install the guard for this server process (called once from ``main``)."""
    global _active_guard
    _active_guard = guard if guard is not None else ManagedRouteGuard(None, None, None)


def get_active_guard() -> ManagedRouteGuard:
    """The guard for this server process; disabled unless a project root was supplied."""
    return _active_guard


__all__ = [
    "GuardDecision",
    "ManagedRouteGuard",
    "build_guard",
    "canonical_project_root",
    "get_active_guard",
    "project_roots_match",
    "set_active_guard",
]
