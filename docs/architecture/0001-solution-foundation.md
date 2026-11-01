# Solution foundation

Task: BE-001.1 — Create Solution & Project References

## Decision

Use .NET SDK 10.0.203, .NET 10, and C# 14. `global.json` requires that SDK, and `Directory.Build.props` supplies common compiler settings. Nullable reference types, implicit usings, checked integer arithmetic, deterministic builds, and warnings as errors are enabled.

Use `ExactLedger.sln` and the `el` project/namespace prefix. Keep four backend projects: Domain, Application, Infrastructure, and API. Split features into folders rather than separate assemblies. The Swift client is a separate Xcode target, not a .NET project.

## Dependencies

```text
el.Domain          -> no project references
el.Application     -> el.Domain
el.Infrastructure  -> el.Application, el.Domain
el.Api             -> el.Application, el.Infrastructure
```

Application owns the ports required by its use cases; Infrastructure implements them. API registers implementations using dependency injection. Pure entities and value objects do not need interfaces merely to exist. There are no business adapters to register in this initial skeleton.

Domain features are grouped into Common, Ledger, Knowledge, Settlement, and Clearing. Application has matching feature folders plus Abstractions. Database mappings belong in `el.Infrastructure/Persistence/Configurations` when persistence is implemented.

## Packages and tests

Use central package versions and committed NuGet lock files. Restore in locked mode for reproducible builds. The xUnit projects are ready for domain and integration tests. API host tests verify the executable foundation; no synthetic domain test or empty business entity is introduced.

Explicitly pin Microsoft.OpenApi to 2.7.5: the SDK API template otherwise resolves 2.0.0, which NuGet flags under [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc). The advisory identifies 2.7.5 as patched in the 2.x line. Keep dependency auditing enabled rather than suppressing the warning.

## Boundaries

This change creates the host and project structure. Business entities, EF Core/DbContext, monetary algorithms, authentication, scheduled business jobs, and bank adapters are separate tasks. PostgreSQL configuration has a named connection-string slot but is not consumed yet. CI implementation and automated architecture guards remain BE-001.2 and BE-001.3 respectively.
