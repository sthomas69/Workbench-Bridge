# Versioning

Workbench-Bridge uses a git-driven build number so every commit produces a
unique, strictly increasing version with no manual bookkeeping. The scheme was
applied via the reusable [`dotnet-versioning`](.claude/skills/dotnet-versioning/SKILL.md)
skill.

## Scheme

| Build kind | `Version` | Runtime `--version` |
|---|---|---|
| Local dev  | `{prefix}.{build}-dev` | `{prefix}.{build}-dev+{git-hash}` |
| CI/release | `{prefix}.{build}`     | `{prefix}.{build}`                |

- `{prefix}` — the semantic `VersionPrefix` pinned in each csproj (currently
  `0.5.0`), bumped by hand at release time.
- `{build}` — the git commit count (`git rev-list --count HEAD`), computed at
  build time. Increments by exactly one per commit.
- `{git-hash}` — the short HEAD hash, added as build metadata on dev builds only.

`AssemblyVersion` and `FileVersion` are always the numeric `{prefix}.{build}`
(no suffix), keeping them valid 4-part version numbers.

Example local dev build at commit count 30: `0.5.0.30-dev+26bf4ec`.

## Where the version lives

| File | Role |
|---|---|
| [`Directory.Build.props`](Directory.Build.props) | Sets `IsDevBuild` (true locally, false under CI) and defines the `DEVBUILD` compile constant. |
| [`Directory.Build.targets`](Directory.Build.targets) | `ComputeVersion` target derives the build number from git and composes `Version` / `InformationalVersion` / `AssemblyVersion` / `FileVersion`. |
| `src/WorkbenchBridge.Cli/WorkbenchBridge.Cli.csproj` | `<VersionPrefix>0.5.0</VersionPrefix>` |
| `src/WorkbenchBridge.Service/WorkbenchBridge.Service.csproj` | `<VersionPrefix>0.5.0</VersionPrefix>` |

Each project carries its own `VersionPrefix` but they share the single
git-derived build number from `Directory.Build.targets`.

## Build number

The build number is the git commit count, queried at build time:

```
git rev-list --count HEAD
```

- No external state — the number is implicit in git history.
- Increments automatically with each commit; an uncommitted rebuild keeps the
  same number.
- Falls back to `1` if git is unavailable.

## CI override

Pass an explicit build number to skip the git query (useful for shallow CI
clones where the commit count undercounts):

```
dotnet build /p:BuildNumber=$(Build.BuildId)
```

CI also sets `IsDevBuild=false` automatically — Azure DevOps exports
`TF_BUILD=true` and GitHub Actions (and most CIs) export `CI=true` — which drops
the `-dev` suffix and git-hash metadata, yielding a clean `{prefix}.{build}`
release version. Force it locally with `dotnet build /p:IsDevBuild=false`.

## Runtime version display

Both apps read the informational version back from the entry assembly:

```csharp
Assembly.GetEntryAssembly()
    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion;
```

- **CLI** — `workbenchbridge-cli --version` (and the bare `version` command)
  prints `workbenchbridge-cli {version}`.
- **Service** — logs `ESP32 Workbench Bridge service starting (v{version})` at
  startup, and reports the same string over IPC for `status` / `version`.

## Releasing a new version

1. Bump `<VersionPrefix>` in the relevant csproj(s) (e.g. `0.5.0` → `0.6.0`).
2. Commit `chore: bump version to v0.6.0`.
3. Build — the build number increments automatically with the commit. CI builds
   produce the clean, suffix-free release version.
