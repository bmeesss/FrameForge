# FrameForge

**FrameForge** is an open-source **Windows** desktop companion for [Counter-Strike 2](https://store.steampowered.com/app/730).  
It analyzes your PC and CS2 installation, recommends **safe** optimizations, manages **documented** CS2 configuration files, and provides **backup / rollback**.

> ### This is not a cheat client
>
> FrameForge contains **no** aimbots, ESP, triggerbots, wallhacks, spinbots, or any other gameplay advantage software.  
> It does **not** inject DLLs, edit CS2 memory, load kernel drivers, or bypass VAC / anti-cheat.  
> All functionality is external: system information, Steam library detection, configuration files, and user-approved reversible tweaks.

## Implemented features

- **Dashboard** — CS2 detection status, CPU / GPU / RAM / OS / architecture, recommendation counts, backups count, active profile
- **Optimization Score** — readiness score (0–max) based only on detected conditions (CS2 found, cfg healthy, backups, auto-backup setting, catalog, hardware inventory, profile). Each factor shows why points were awarded or withheld. **No FPS estimates.**
- **CS2 detection** — multi-root Steam discovery (env vars, common locations, drive scan, optional Windows registry), `libraryfolders.vdf` (modern + legacy), `appmanifest_730.acf`. Custom Steam/CS2 paths in Settings. Missing Steam/CS2 returns a result object — does not throw.
- **Hardware detection** — CPU name/cores/threads, GPU label (best-effort), total RAM, OS version label, architecture. Unknown values reported gracefully.
- **Optimization engine** — `IOptimization` with preview, backup requirements, apply, revert. Pipeline: Detect → Analyze → Preview → Backup → Apply → Verify → Rollback on failure (per-optimization revert + backup restore).
- **CS2 configuration** — parse/write `.cfg` with comment/unknown-line preservation, side-car backup before overwrite, atomic writes.
- **Profiles** — data-driven Competitive / Balanced / Quality / Custom (JSON). Activating a profile selects recommended items; it does **not** silently apply them.
- **Backups** — `Backups/metadata.json` with timestamps, affected files, optimization IDs, previous values, file snapshots, restore with result object. Corrupted metadata is quarantined.
- **Logging** — structured local logs; secrets redacted.
- **UI** — dark WPF shell: Home, Optimize, CS2 Settings, Profiles, Backups, Settings — with loading state, empty states, error banner, and confirmation dialogs before apply/restore/reset.

## Not implemented yet (honest)

- Automatic Windows power-plan / registry tweaks (advisory only)
- Windows Game Mode status probe (score factor present, 0 points)
- Windows GPU name via DXGI/WMI
- Launch-at-startup registration
- Full CS2 frame-time benchmark / FPS claims
- Automatic Steam launch-option editing

## Tech stack

- C# / **.NET 10** (LTS) — see `global.json`
- WPF + MVVM (Windows)
- Dependency injection via shared `Microsoft.AspNetCore.App` framework reference (no extra NuGet required for foundation)
- JSON local data (`System.Text.Json`)
- xUnit-style tests (offline-friendly runner included)

## Solution structure

```
src/
  FrameForge.App              WPF front-end (Windows)
  FrameForge.Core             Models, abstractions, atomic IO
  FrameForge.Hardware         Hardware probes
  FrameForge.CS2              Steam / CS2 detect + cfg
  FrameForge.Optimization     Catalog, pipeline, score
  FrameForge.Benchmark        Lightweight samples
  FrameForge.Infrastructure   DI, backup, profiles, logs
tests/
  FrameForge.Tests
assets/profiles/              Built-in profile JSON
```

## Build

```bash
dotnet --version          # requires .NET 10 SDK
dotnet restore FrameForge.sln
dotnet build FrameForge.sln -c Release
```

### Linux / CI note

Core libraries and tests build on Linux. The WPF project compiles a **stub** on non-Windows so solution build succeeds without the Windows Desktop pack.  
Build and run the real UI on **Windows 10/11**:

```bash
dotnet run --project src/FrameForge.App -c Release
```

## Test

```bash
dotnet run --project tests/FrameForge.Tests -c Release
# or
./build.sh
```

## Safety posture

| Allowed | Not allowed |
|---------|-------------|
| Steam library file parsing | DLL injection |
| Reading/writing user cfg files with backup | Process memory read/write |
| Hardware/OS inventory | ESP / aimbot / triggerbot |
| Reversible config presets | Anti-cheat bypass |
| Local backups & logs | Kernel cheats / drivers for game manipulation |

See [SECURITY.md](SECURITY.md).

## License

MIT — see [LICENSE](LICENSE).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
