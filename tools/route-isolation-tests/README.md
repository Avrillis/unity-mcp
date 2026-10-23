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

### C17-R3-FIX3 additions (`McpFix3Tests.cs`)

| Area | Behaviour under test |
| --- | --- |
| `McpPackageLockProvenance` | an explicit integer `depth: 0` is required; missing, null, string, float and non-zero depths all fail closed |
| `McpServerSourceResolver` (UPM URL) | exactly one `path=` parameter: duplicate keys, empty values, unexpected keys, empty components, malformed escapes and encoded separators are all refused |
| `McpOwnershipMutation.TerminateIfStillOwned` | the final termination check re-observes the whole live tuple inside the critical section - a changed/vanished/unreadable PID file, a changed PID-file path, a missing or altered launch nonce, or a different record all refuse the kill |
| `McpOwnershipStore` | an existing-but-unreadable record is UNKNOWN, never ABSENT, and blocks first publication, replacement, promotion and deletion |
| `McpRouteConfiguration` | the managed host allowlist is exactly `localhost` / `127.0.0.1` / `::1`; `127.0.0.2` and every other 127/8 address are refused, while the legacy unmanaged validator keeps its wider loopback behaviour |

The matching Python half lives in `Server/tests/test_fix3_guarded_rest_routes.py`, which executes
the production `/api/command` and `/api/custom-tools` route bodies over a real Starlette ASGI
transport against a registry that contains a foreign session (inserted first) plus the guarded
session (inserted second).

### C17-R3-FIX4 addition (`McpFix4Tests.cs`)

| Area | Behaviour under test |
| --- | --- |
| `McpRouteConfiguration` | the managed allowlist matches the **raw authority text**, read before `Uri`/`IPAddress` canonicalization: only `localhost` (case-insensitive), the literal `127.0.0.1` and the bracketed `[::1]` are accepted. Alternate spellings that the platform parser normalizes to 127.0.0.1 (`127.1`, `0177.0.0.1`, `2130706433`) are refused, as are malformed authorities (missing/empty/non-numeric port, port 0 or > 65535, userinfo, unbracketed colon hosts, non-http scheme, query/fragment, unexpected paths) |

The FIX4 tests also cover `McpRouteConfiguration.TryExtractManagedAuthority`, the raw-authority
reader shared with `TryGetExplicitPort`, and the end-to-end `Resolve` entry point so the alias
spellings fail closed through the real configuration path.

## Companion checks

* `tools/compile-check-dotnet.ps1` compiles the whole `MCPForUnity` Runtime + Editor assembly
  against a local Unity installation's reference DLLs. It never launches the Editor and
  covers the Unity-facing adapters that these tests cannot reach.
* `Server/tests/test_route_guard.py` covers the narrow Python server-side guard
  (`--unity-project-root`, launch nonce, foreign registration, cross-target dispatch).

### MCP-WIN-01 addition (`McpWin01Tests.cs`)

Windows 11 no longer ships `wmic.exe`. The package used it to read the dedicated server's command
line, so the ownership proof could never confirm the launch nonce and a managed `starting` record
was never promoted to `running`. `WindowsProcessCommandLine.cs` now reads the command line through
CIM (`Win32_Process`) using the fixed in-box Windows PowerShell.

| Area | Behaviour under test |
| --- | --- |
| `WindowsProcessCommandLine` query | only a validated positive integer PID reaches the query; the executable is the fixed absolute system path; the run is time-bounded; invalid PIDs never launch anything |
| Output parser | exactly one well-formed result line; Base64 UTF-8 round-trips quotes, spaces, backslashes, Unicode and fake status markers verbatim; non-zero exit, timeout, truncation, empty, malformed, non-strict Base64, invalid UTF-8 and NUL are all rejected |
| Live Windows queries | a real child process (quotes, spaces, Unicode), the test process itself, a nonexistent PID, a terminated PID, a process whose metadata the OS withholds, and a real timeout (these run only on Windows) |
| `ProcessDetector` (production class) | the normalized lower-case contract is unchanged; every failure mode returns false and an empty value |
| Ownership chain | the real `McpProcessInspector` + `ProcessDetector` + `McpRouteObservationBuilder` + `McpOwnershipEvaluator` + `McpManagedConnectionGate`: a valid WORKER-04-style server is promoted and the nonce released; an unreadable command line, nonce mismatch, wrong port, other worktree, PID reuse, other editor lifetime, multiple or foreign listeners, missing server, bad PID file and an adoption of another lifetime's pending record are all refused |

`ProcessDetector.cs` and `McpProcessInspector.cs` are the real production classes. They compile
against `UnityApiShims.cs`, a few lines of test-only stand-ins for `Application`, `RuntimePlatform`,
`McpLog` and `ExecPath`, so nothing in the shipped package changes for testing.

Cross-target dispatch refusal is a server-side behaviour (`Server/tests/test_route_guard.py`).
## Why the sources are compiled, not referenced

The Route sources deliberately contain no `UnityEngine`/`UnityEditor` references. That keeps
the ownership logic testable in isolation and prevents a Unity type from leaking into a
decision that has to stay deterministic.
