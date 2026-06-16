---
name: dotnet-versioning
description: >-
  Apply a proven, git-driven versioning scheme to any .NET solution. A
  repo-root Directory.Build.targets derives a monotonically increasing build
  number from the git commit count at build time; Directory.Build.props sets an
  IsDevBuild flag and a DEVBUILD compile constant. Each project pins a
  VersionPrefix and the build number is appended automatically, with a -dev
  suffix and +git-hash metadata on local builds. Use when the user wants version
  numbers, a build-number scheme, --version output, or auto-incrementing
  versions wired into a .NET console app, Windows service, web app, or MAUI app.
---

# .NET project versioning

A reusable versioning pattern for .NET solutions. The build number comes from
the git commit count (`git rev-list --count HEAD`) computed at build time, so it
increases by exactly one per commit, needs no external state, and survives
clones and CI. There is **no MAUI dependency** — this skill handles plain
console apps, worker/Windows services, and ASP.NET Core, and only layers in the
MAUI-specific pieces when the target project is actually a MAUI app.

## The scheme

| Build kind | `Version` | `AssemblyInformationalVersion` (runtime) |
|---|---|---|
| Local dev | `{prefix}.{build}-dev` | `{prefix}.{build}-dev+{git-hash}` |
| CI / release | `{prefix}.{build}` | `{prefix}.{build}` |

- `{prefix}` is the semantic `VersionPrefix` pinned in each csproj (e.g. `0.5.0`),
  bumped by hand at release time.
- `{build}` is the git commit count — a small, strictly increasing integer.
- `AssemblyVersion` and `FileVersion` are always the numeric `{prefix}.{build}`
  (no suffix), so they stay valid 4-part version numbers.
- Local builds get the `-dev` prerelease suffix plus `+{git-hash}` build metadata
  so a dev binary is never confused with a release.
- CI is detected via the `TF_BUILD` (Azure DevOps) and `CI` (GitHub Actions and
  most others) environment variables, which flip `IsDevBuild` to false.

## How version data flows (non-MAUI)

1. csproj pins `<VersionPrefix>0.5.0</VersionPrefix>`.
2. `Directory.Build.targets` runs `git rev-list --count HEAD` at build time,
   trims it, and composes the full version. It writes `Version`,
   `InformationalVersion`, `AssemblyVersion`, and `FileVersion` **inside a target**
   that runs before the SDK consumes them, so the build number is baked in.
3. The .NET SDK emits `AssemblyInformationalVersionAttribute` (and friends) into
   the generated `AssemblyInfo.cs`.
4. Runtime code reads it back with
   `Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()`.

> Why compose the version *inside a target* rather than as a plain property?
> The SDK turns `$(Version)` into `AssemblyVersion`/`InformationalVersion` while
> evaluating static properties, which happens before any `<Exec>` can run git.
> Setting the version properties in a target that runs `BeforeTargets` the SDK's
> version-consuming targets is what makes the git-derived build number stick.

## Procedure

### 1. Survey what exists — never overwrite blindly

- Check the repo root for `Directory.Build.targets` and `Directory.Build.props`.
  If either exists, **read it and merge** the versioning logic rather than
  clobbering unrelated settings. Mention what you preserved.
- Identify the solution layout and the project type of each **compilable, shipped**
  project (skip test projects unless asked):
  - **Console / CLI** — `OutputType=Exe`, `Microsoft.NET.Sdk`.
  - **Worker / Windows service** — `Microsoft.NET.Sdk.Worker`.
  - **Web** — `Microsoft.NET.Sdk.Web`.
  - **MAUI** — `UseMaui=true` / `Microsoft.NET.Sdk` with MAUI workload.
- Confirm the repo is a git repo (`git rev-parse --git-dir`). The fallback build
  number is `1` if git is unavailable, but warn the user — the scheme is far less
  useful without git history.

### 2. Create `Directory.Build.props` (if missing)

Sets the dev-build flag and the `DEVBUILD` compile constant. This file is
project-type agnostic — the same content works everywhere.

```xml
<Project>
  <!-- IsDevBuild: true for local developer builds, false under CI.
       Azure DevOps sets TF_BUILD=true; GitHub Actions and most CIs set CI=true.
       Override explicitly with: dotnet build /p:IsDevBuild=false -->
  <PropertyGroup>
    <IsDevBuild Condition="'$(IsDevBuild)' == '' and '$(TF_BUILD)' != 'true' and '$(CI)' != 'true'">true</IsDevBuild>
    <IsDevBuild Condition="'$(IsDevBuild)' == ''">false</IsDevBuild>
  </PropertyGroup>

  <!-- DEVBUILD compile constant for #if DEVBUILD guards in version-display code. -->
  <PropertyGroup Condition="'$(IsDevBuild)' == 'true'">
    <DefineConstants>$(DefineConstants);DEVBUILD</DefineConstants>
  </PropertyGroup>
</Project>
```

> If a `Directory.Build.props` already exists, add only the two `PropertyGroup`
> blocks above into it.

### 3. Create `Directory.Build.targets` (if missing)

**For console / service / web (non-MAUI) projects** — use this. It does **not**
patch any AppxManifest and does **not** set `ApplicationVersion`.

```xml
<Project>
  <!--
    GIT-DRIVEN BUILD NUMBER
    =======================
    Build number = git commit count (rev-list of HEAD), computed at build time.
    Increments by one per commit, needs no external state.
    (Keep "rev-list" out of the literal git command form here: an XML comment
    may not contain a double dash, so do not write the --count flag in comments.)

    Version layout:
      Local dev : {VersionPrefix}.{build}-dev+{git-hash}   (IsDevBuild=true)
      CI/release: {VersionPrefix}.{build}                  (IsDevBuild=false)
    AssemblyVersion / FileVersion are always the numeric {VersionPrefix}.{build}.

    CI override: pass /p:BuildNumber=$(Build.BuildId) to skip the git query.
  -->
  <Target Name="ComputeVersion"
          BeforeTargets="GetAssemblyVersion;GenerateAssemblyInfo;CoreCompile;BeforeBuild"
          Condition="'$(VersionPrefix)' != ''">

    <!-- Build number from git commit count (skip if CI already supplied one). -->
    <Exec Command="git rev-list --count HEAD"
          ConsoleToMSBuild="true"
          StandardOutputImportance="low"
          IgnoreExitCode="true"
          WorkingDirectory="$(MSBuildProjectDirectory)"
          Condition="'$(BuildNumber)' == ''">
      <Output TaskParameter="ConsoleOutput" PropertyName="BuildNumber" />
    </Exec>

    <!-- Short git hash for dev build metadata. -->
    <Exec Command="git rev-parse --short HEAD"
          ConsoleToMSBuild="true"
          StandardOutputImportance="low"
          IgnoreExitCode="true"
          WorkingDirectory="$(MSBuildProjectDirectory)"
          Condition="'$(GitHash)' == '' and '$(IsDevBuild)' == 'true'">
      <Output TaskParameter="ConsoleOutput" PropertyName="GitHash" />
    </Exec>

    <PropertyGroup>
      <BuildNumber Condition="'$(BuildNumber)' != ''">$(BuildNumber.Trim())</BuildNumber>
      <BuildNumber Condition="'$(BuildNumber)' == '' or '$(BuildNumber)' == '0'">1</BuildNumber>
      <GitHash Condition="'$(GitHash)' != ''">$(GitHash.Trim())</GitHash>

      <!-- Numeric 4-part version used for AssemblyVersion / FileVersion. -->
      <_NumericVersion>$(VersionPrefix).$(BuildNumber)</_NumericVersion>
      <AssemblyVersion>$(_NumericVersion)</AssemblyVersion>
      <FileVersion>$(_NumericVersion)</FileVersion>

      <!-- We compose the informational version by hand, so stop the SDK from
           also appending +SourceRevisionId. -->
      <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>

      <!-- Dev builds: -dev suffix + git hash metadata. Release: bare numeric. -->
      <Version Condition="'$(IsDevBuild)' == 'true'">$(_NumericVersion)-dev</Version>
      <Version Condition="'$(IsDevBuild)' != 'true'">$(_NumericVersion)</Version>
      <InformationalVersion Condition="'$(IsDevBuild)' == 'true' and '$(GitHash)' != ''">$(_NumericVersion)-dev+$(GitHash)</InformationalVersion>
      <InformationalVersion Condition="'$(IsDevBuild)' == 'true' and '$(GitHash)' == ''">$(_NumericVersion)-dev</InformationalVersion>
      <InformationalVersion Condition="'$(IsDevBuild)' != 'true'">$(_NumericVersion)</InformationalVersion>
    </PropertyGroup>

    <Message Text="[version] $(MSBuildProjectName) -> $(InformationalVersion)" Importance="high" />
  </Target>
</Project>
```

**For MAUI projects** — additionally (do **not** apply the below to non-MAUI
projects):
- Set `<ApplicationVersion>$(BuildNumber)</ApplicationVersion>` in the target
  (drives Android `versionCode` / iOS `CFBundleVersion`).
- Keep `<ApplicationDisplayVersion>` as the semantic version in the csproj.
- Patch the generated `AppxManifest.xml` Identity `Version` via `XmlPoke` in a
  `SetMsixBuildVersion` target (`BeforeTargets="_GenerateAppxPackageFile"`,
  condition `$(TargetFramework.Contains('-windows'))`), because MAUI copies the
  manifest verbatim and the MSBuild version property does not override MSIX
  identity. Use `$(ApplicationDisplayVersion).$(BuildNumber)` as the value.

### 4. Pin `VersionPrefix` in each compilable project

Add to the main `<PropertyGroup>` of every shipped project's csproj:

```xml
<VersionPrefix>0.5.0</VersionPrefix>
```

Each project may carry its own `VersionPrefix` (e.g. a CLI and a service can
differ), but they all share the single git-derived build number from the targets
file. Do not add `VersionPrefix` to test projects unless asked.

### 5. Wire up runtime version display

Read the informational version from the **entry assembly** and strip any `+`
metadata if you want a cleaner display. Helper:

```csharp
using System.Reflection;

static string GetVersion() =>
    Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? "unknown";
```

Surface it the way that fits the project type:
- **CLI** — handle `--version` (and a bare `version` command) printing
  `{tool-name} {version}`.
- **Worker / Windows service** — log it once at startup
  (`logger.LogInformation("{Service} v{Version} starting", name, GetVersion())`).
- **Web** — render it in a page footer or expose a `/version` endpoint.
- **MAUI** — show `AppInfo.Current.VersionString` / `BuildString` in a settings
  footer, guarded by `#if DEVBUILD` for the `dev-` prefix.

Prefer `GetEntryAssembly()` over `GetExecutingAssembly()` so a shared library
reports the host app's version, not its own. If existing code already uses
`GetExecutingAssembly()` within the entry project, it works the same — leave it
unless consolidating.

### 6. Create / update `VERSIONING.md`

Document, at the repo root: the scheme table, where `VersionPrefix` lives per
project, the git-commit-count mechanism, the CI override
(`/p:BuildNumber=...`), the `IsDevBuild` / `DEVBUILD` behavior, and the
release-bump steps. See the template at the end of this file.

### 7. Verify (do not skip)

1. `dotnet build` the solution — it must succeed, and the
   `[version] … -> …` message should show the composed version.
2. Run the version display and confirm the format, e.g.
   `dotnet run --project <cli> -- --version` shows
   `{prefix}.{N}-dev+{hash}` where `N == git rev-list --count HEAD`.
3. Rebuild **without committing** and confirm the build number is unchanged.
4. Make a trivial commit, rebuild, and confirm the build number incremented by
   exactly 1.
5. (Optional) `dotnet build /p:IsDevBuild=false` and confirm the version drops
   the `-dev` suffix and git hash.

## Gotchas

- **Timing.** If the build number doesn't appear, the version properties were
  set as static properties instead of inside the `ComputeVersion` target — the
  SDK had already consumed `$(Version)`. Keep the composition in the target with
  the `BeforeTargets` list above.
- **Stale `obj/`.** AssemblyInfo is cached in `obj/`. If a version change
  doesn't show, the project may not have rebuilt; force with `--no-incremental`
  or touch a source file. A new commit changes the build number and forces a
  regen anyway.
- **Detached / shallow clones.** `git rev-list --count HEAD` on a shallow CI
  clone undercounts. Prefer the `/p:BuildNumber=$(Build.BuildId)` override in CI,
  which the targets honor (git is not queried when `BuildNumber` is preset).
- **Don't double-append the hash.** `IncludeSourceRevisionInInformationalVersion`
  is set false on purpose; the hash is composed manually. If you remove that,
  you can get `+hash+hash`.
- **MAUI bits stay in MAUI.** Never add `ApplicationVersion` or AppxManifest
  patching to a console/service/web project — there is no manifest to patch and
  `ApplicationVersion` is meaningless there.

## VERSIONING.md template (non-MAUI)

```markdown
# Versioning

## Scheme

| Build kind | `Version` | Runtime `--version` |
|---|---|---|
| Local dev  | `{prefix}.{build}-dev`        | `{prefix}.{build}-dev+{git-hash}` |
| CI/release | `{prefix}.{build}`            | `{prefix}.{build}`                |

`{prefix}` = semantic `VersionPrefix` in each csproj (bumped by hand on release).
`{build}`  = git commit count (`git rev-list --count HEAD`), set at build time.

## Where the version lives

- `Directory.Build.props` (repo root) — `IsDevBuild` flag + `DEVBUILD` constant.
- `Directory.Build.targets` (repo root) — `ComputeVersion` target derives the
  build number and composes `Version` / `InformationalVersion` /
  `AssemblyVersion` / `FileVersion`.
- `<project>.csproj` — `<VersionPrefix>` per project.

## Build number

Git commit count, computed at build time. Increments by one per commit; no
external state. Falls back to `1` if git is unavailable.

## CI override

`dotnet build /p:BuildNumber=$(Build.BuildId)` — when `BuildNumber` is supplied,
git is not queried. CI also sets `IsDevBuild=false` automatically via `TF_BUILD`
/ `CI`, dropping the `-dev` suffix.

## Releasing

1. Bump `<VersionPrefix>` in the relevant csproj(s).
2. Commit `chore: bump version to vX.Y.Z`.
3. Build — the build number increments automatically with the commit.
```
