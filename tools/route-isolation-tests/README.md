# MCP package route-isolation tests

Deterministic tests for the Unity-package half of the TLN multi-worker isolation contract.
They run **without Unity** and without any licence, which is what makes them usable as a
gate on machines that cannot open the Editor.

```bash
dotnet test tools/route-isolation-tests/MCPForUnity.RouteIsolation.Tests.csproj
```

## What is covered

The fixtures exercise the dependency-free sources under
`MCPForUnity/Editor/Services/Route/`, which are compiled directly into the test assembly:

| Area | Source |
| --- | --- |
| Process-scoped configuration, precedence, fail-closed validation | `McpRouteConfiguration.cs` |
| Resolve-once process cache | `McpRouteConfigurationCache.cs` |
| Project-local record, paths, schema | `McpRunStateRecord.cs` |
| Terminate / adopt / reconcile decisions | `McpOwnershipEvaluator.cs` |

Cases `A`–`Z` of the C17-R3 specification are covered here; the names of the test methods
start with the letter they satisfy.

`McpProductionAdapterTests.cs` additionally exercises the production adapters named by
C17-R3-FIX1, using the same compiled production sources with only their Unity shells
substituted:

| Adapter | Behaviour under test |
| --- | --- |
| `McpRunStateFile` | unique-temp atomic publication, replacement, failure preserves the old record, concurrent writers, abandoned-temp reclamation |
| `McpOwnershipEvaluator` (adoption) | pending/unknown ownership is never overwritten; only positively established staleness is replaceable |
| `McpManagedConnectionGate` | the launch nonce is released only for coherent current ownership |
| `McpTerminationIdentity` | retained-identity termination; PID reuse / exit / unreadable lifetime never kill |
| `McpServerSourceResolver` + `McpPackageLockProvenance` | package and server share repository + full commit + `Server/`; no PyPI, no floating ref, no credentials |
| `McpServerEnvironmentSanitizer` | inherited `UNITY_MCP_HTTP_*` routing variables are removed before launch |
| `McpClientRoute` | generated client configuration follows the resolved process route |
| `McpManagedServerArguments` | guarded launch argument construction (pidfile + nonce) and fail-closed tokens |
| `McpRouteObservationBuilder` | the live observation used by the gate and the stop path |

### C17-R3-FIX2 additions (`McpFix2Tests.cs`)

| Adapter | Behaviour under test |
| --- | --- |
| `McpManagedPreConnectGate` | the managed pre-connect authorization is ordered strictly before any socket open; an unproven/stale/copied/foreign/wrong-endpoint ownership state never opens a connection |
| `McpOwnershipMutation` | termination final revalidation inside the cross-process critical section (record, listener and retained-lifetime re-checks; conditional deletion) |
| `McpOwnershipStore` | the real file-backed ownership store: conditional publish/delete, malformed/unreadable state blocking, cross-process lock failure, first-publication races |
| `McpRouteConfiguration` | the managed URL contract: explicit textual port required, loopback only, LAN opt-in ignored |
| `McpRunStateRecord` | per-lifecycle `record_id` identity and its survival across a valid `starting -> running` transition |

The Python half of FIX2 lives in `Server/tests/test_fix2_guarded_registration.py`: serialised
guarded registration (a second connection cannot take a reservation), reservation rollback on
failure, disconnect-driven release, and the central guarded dispatch selector both REST routes
use.

## Companion checks

* `tools/compile-check-dotnet.ps1` compiles the whole `MCPForUnity` Runtime + Editor assembly
  against a local Unity installation's reference DLLs. It never launches the Editor and
  covers the Unity-facing adapters that these tests cannot reach.
* `Server/tests/test_route_guard.py` covers the narrow Python server-side guard
  (`--unity-project-root`, launch nonce, foreign registration, cross-target dispatch).

## Why the sources are compiled, not referenced

The Route sources deliberately contain no `UnityEngine`/`UnityEditor` references. That keeps
the ownership logic testable in isolation and prevents a Unity type from leaking into a
decision that has to stay deterministic.
