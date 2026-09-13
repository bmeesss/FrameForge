# FrameForge

**FrameForge** is an open-source **Windows** desktop companion for [Counter-Strike 2](https://store.steampowered.com/app/730).  
It analyzes your PC and CS2 installation, recommends **safe** optimizations, manages **documented** CS2 configuration files, and provides **backup / rollback**.

> ### This is not a cheat client
>
> FrameForge contains **no** aimbots, ESP, triggerbots, wallhacks, spinbots, or any other gameplay advantage software.  
> It does **not** inject DLLs, edit CS2 memory, load kernel drivers, or bypass VAC / anti-cheat.  
> All functionality is external: system information, Steam library detection, configuration files, and user-approved reversible tweaks.

## Features (MVP foundation)

- **Dashboard** — CS2 detection status, CPU / GPU / RAM, Windows version, install path, recommendation count
- **CS2 detection** — multi-library Steam discovery via `libraryfolders.vdf` and `appmanifest_730.acf` (no single hardcoded path)
- **Hardware detection** — CPU name/cores/threads, GPU label, total RAM, OS version, architecture
- **Optimization engine** — `IOptimization` with preview, backup requirements, apply, revert; pipeline: Detect → Analyze → Preview → Backup → Apply → Verify → Rollback on failure
- **CS2 configuration** — parse/write `.cfg` files with side-car backups and atomic replaces
- **Profiles** — data-driven Competitive / Balanced / Quality / Custom (JSON)
- **Backups** — `Backups/metadata.json` with timestamps, affected files, optimization IDs, previous values, restore
- **Logging** — structured local logs (startup, detection, analysis, apply/revert, errors); secrets redacted
- **UI** — modern dark WPF shell: Home, Optimize, CS2 Settings, Profiles, Backups, Settings
- **Settings** — automatic backup, launch-at-startup flag, logging level, theme, reset

## Tech stack

- C# / .NET 10 (LTS)
- WPF + MVVM
- Dependency injection (`Microsoft.Extensions.DependencyInjection` via shared framework)
- JSON local data (`System.Text.Json`)
- xUnit-style tests (offline-friendly runner included)

## Solution structure

```
src/
  FrameForge.App              WPF front-end
  FrameForge.Core             Models & abstractions
  FrameForge.Hardware         Hardware probes
  FrameForge.CS2              Steam / CS2 detect + cfg
  FrameForge.Optimization     Catalog + pipeline
  FrameForge.Benchmark        Lightweight samples
  FrameForge.Infrastructure   DI, backup, profiles, logs
tests/
  FrameForge.Tests
assets/profiles/              Built-in profile JSON
```

## Build

```bash
dotnet --version          # requires .NET 10 SDK
dotnet restore
dotnet build FrameForge.sln -c Release
```

Run the UI (Windows):

```bash
dotnet run --project src/FrameForge.App -c Release
```

## Test

```bash
dotnet run --project tests/FrameForge.Tests -c Release
```

## Safety posture

| Allowed | Not allowed |
|---------|-------------|
| Steam library file parsing | DLL injection |
| Reading/writing user cfg files with backup | Process memory read/write |
| Hardware/OS inventory | ESP / aimbot / triggerbot |
| Reversible config presets | Anti-cheat bypass |
| Local backups & logs | Kernel cheats / drivers for game manipulation |

See [SECURITY.md](SECURITY.md) for reporting guidelines.

## Status of deferred work

The foundation intentionally **does not** auto-apply risky Windows registry or powercfg tweaks yet.  
Those appear as **advisory** recommendations with clear “not implemented” apply paths.  
Undocumented CS2 convars are out of scope until verified as user-facing.

## License

MIT — see [LICENSE](LICENSE).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
