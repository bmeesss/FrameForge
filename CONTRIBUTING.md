# Contributing to FrameForge

Thanks for helping build a clean, legitimate CS2 performance companion.

## Ground rules

1. **No cheats.** No injection, memory editing, ESP, aimbots, triggerbots, or anti-cheat interaction.
2. **Reversible changes only.** Every optimization must support backup + revert.
3. **Documented config only.** Do not invent undocumented CS2 convars.
4. **Honest TODOs.** If something is not implemented, expose an interface or TODO — do not fake it.
5. **No fabricated FPS claims.** Optimization Score and docs must only reflect detected conditions.

## Development setup

### Requirements

- Windows 10/11 for running the WPF UI
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- Optional: Linux/macOS for building and testing class libraries

### Build

```bash
dotnet restore FrameForge.sln
dotnet build FrameForge.sln -c Release
```

On non-Windows hosts the WPF app builds as a stub; core libraries and tests still build fully.

### Test

```bash
dotnet run --project tests/FrameForge.Tests -c Release
```

When nuget.org is available you may add official `xunit` packages and use `dotnet test`. The foundation suite uses an offline-friendly xUnit-compatible runner so CI works without external feeds.

## Project layout

| Project | Responsibility |
|---------|----------------|
| `FrameForge.App` | WPF UI (MVVM) |
| `FrameForge.Core` | Models, interfaces, atomic file helpers |
| `FrameForge.Hardware` | CPU/GPU/RAM/OS detection |
| `FrameForge.CS2` | Steam library + CS2 detection + cfg IO |
| `FrameForge.Optimization` | Optimization contracts, catalog, pipeline, score |
| `FrameForge.Benchmark` | Lightweight system samples |
| `FrameForge.Infrastructure` | DI, backup, profiles, settings, logging |
| `FrameForge.Tests` | Unit tests |

## Adding an optimization

1. Implement `IOptimization` (prefer subclassing `OptimizationBase`).
2. Provide `CanApply`, `Explain`, `Apply`, `Revert`, and `BackupRequirements`.
3. Register it in `OptimizationCatalog`.
4. Add unit tests for validation and (if file-based) apply/revert.
5. Keep risk level honest; default to advisory if automatic apply is unsafe.
6. Ensure pipeline failure can roll your change back independently.

## Coding standards

- Nullable reference types enabled
- Async/await with cancellation tokens for long work
- Small services, clear interfaces
- JSON for local persistence via `FrameForgeJson` / `AtomicFile`
- No giant classes / duplicated logic
- Platform-specific code stays in the right project (`Hardware`, `CS2`, or Windows-only App)

## Pull requests

- Keep PRs focused
- Include tests for new logic
- Update README / SECURITY if behavior changes
- Confirm the change does not cross the anti-cheat boundary
- Do not commit `bin/`, `obj/`, logs, or local SDK copies
