> **Last updated: 2026-03-04**

# Contributing to Jellyfin Server

This guide covers everything you need to develop, build, test, and submit changes to the Jellyfin server.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.x | See `global.json` — `rollForward: latestMinor` |
| Git | any recent | `git clone` with submodules not required |
| FFmpeg | 7.x | Required for transcoding tests; install via devcontainer or manually |
| Docker | optional | For devcontainer workflow |

### macOS (Homebrew)

```bash
brew install dotnet
```

### Linux (Debian/Ubuntu)

```bash
wget https://dot.net/v1/dotnet-install.sh && bash dotnet-install.sh --channel 10.0
```

### Windows

Download the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) installer.

### DevContainer (recommended for new contributors)

Open the repo in VS Code and accept the "Reopen in Container" prompt. The devcontainer installs:
- .NET 10
- FFmpeg
- All recommended VS Code extensions

---

## Build

```bash
# Build the server entry point
dotnet build Jellyfin.Server/Jellyfin.Server.csproj

# Build the entire solution (all projects)
dotnet build Jellyfin.sln
```

Debug builds activate all code analyzers (StyleCop, BannedApiAnalyzers, IDisposableAnalyzers, MultithreadingAnalyzer). **Expect build failures if your code has missing XML docs or uses banned APIs.**

---

## Run Locally

```bash
dotnet run --project Jellyfin.Server/Jellyfin.Server.csproj \
  -- --datadir /tmp/jellyfin-data --webdir /tmp/jellyfin-web --nowebclient
```

The server starts on `http://localhost:8096` by default.

---

## Test

```bash
# Run all tests (cross-platform matrix: Linux, macOS, Windows)
dotnet test Jellyfin.sln --configuration Release --verbosity minimal

# Run a single test project
dotnet test tests/Jellyfin.Api.Tests/Jellyfin.Api.Tests.csproj

# Run tests matching a name filter
dotnet test Jellyfin.sln --filter "ClassName=MyServiceTests"

# Run with code coverage
dotnet test Jellyfin.sln \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverletArgs.runsettings
```

Coverage output: `merged/Cobertura.xml` (merged by ReportGenerator in CI).

### Regenerate OpenAPI Spec

After adding or changing any API endpoint:

```bash
dotnet test tests/Jellyfin.Server.Integration.Tests/Jellyfin.Server.Integration.Tests.csproj \
  -c Release \
  --filter "Jellyfin.Server.Integration.Tests.OpenApiSpecTests"
```

Commit the updated `openapi.json` — the CI diff job will flag unintentional breaking changes.

---

## Code Style

All style rules are enforced by the compiler in Debug builds. Key rules:

- **Nullable enabled** — mark nullable types with `?`, never silence with `null!` without a comment
- **Warnings as errors** — fix every warning; do not suppress with `#pragma warning disable`
- **XML docs** — every `public` type and member must have `/// <summary>`
- **No `Task.Result`** — always `await` instead
- **Central NuGet versions** — versions in `Directory.Packages.props` only, never in `.csproj`
- **File-scoped namespaces** — use `namespace Jellyfin.Example;` (not block-scoped)

See `.github/instructions/csharp.instructions.md` for the full ruleset.

---

## Pull Request Process

1. Fork the repo and create a feature branch from `master`
2. Make your changes; ensure `dotnet build` and `dotnet test` pass locally
3. Fill out the PR template (`.github/pull_request_template.md`):
   - **Changes**: 1–5 sentence summary
   - **Issues**: tag with `Fixes #NNN`
4. CI runs automatically:
   - `ci-tests.yml` — tests on Linux, macOS, Windows
   - `ci-openapi.yml` — OpenAPI diff
   - `ci-codeql-analysis.yml` — security scan
5. A maintainer will review and merge

### Title format

Use the imperative mood:
- ✅ `Add lyrics endpoint for audio items`
- ✅ `Fix null reference in LibraryController`
- ❌ `Added lyrics endpoint`
- ❌ `Fixed null reference`

---

## Adding a New Package Dependency

1. Add the version to `Directory.Packages.props`:
   ```xml
   <PackageVersion Include="SomePackage" Version="1.2.3" />
   ```
2. Add the reference to the relevant `.csproj` (no `Version=` attribute):
   ```xml
   <PackageReference Include="SomePackage" />
   ```

**Never** specify both a version in `Directory.Packages.props` AND in the `.csproj` — that causes `NU1008`.

---

## Project Conventions

See `.github/instructions/` for detailed instructions per concern:

| Topic | File |
|---|---|
| C# style | `csharp.instructions.md` |
| API controllers | `api.instructions.md` |
| Tests | `testing.instructions.md` |
| CI/CD workflows | `ci-cd.instructions.md` |
| Documentation | `docs.instructions.md` |
