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
