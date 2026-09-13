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
- **CS2 Settings** — strongly typed, category-grouped catalog of **documented cfg-backed** keys only. Read → validate → **diff preview** → confirm → backup → apply → verify → auto-rollback on failure. Managed file: `frameforge_settings.cfg` (user autoexec / unknown lines preserved). Reset restores the last FrameForge settings backup.
- **Profiles** — data-driven Competitive / Balanced / Quality plus custom profiles (`schemaVersion: 1`). Built-ins are protected (no overwrite/delete/rename). Create / rename / duplicate / delete custom profiles. Import / export `.frameforge-profile.json` with validation (rejects unknown keys, out-of-range values, unsupported future schema). Apply always shows a diff and requires confirmation — never silent.
- **Backups** — `Backups/metadata.json` with timestamps, affected files, optimization IDs, previous values, file snapshots, restore with result object. Corrupted metadata is quarantined.
- **Logging** — structured local logs; secrets redacted.
- **UI** — dark WPF shell: Home, Optimize, CS2 Settings, Profiles, Backups, Settings — with loading state, empty states, error banner, and confirmation dialogs before apply/restore/reset.

## Supported CS2 settings (cfg-backed)

Only keys FrameForge will read/write. Video quality sliders that live only in binary/`video.txt` are **excluded** until a safe documented format exists.

| Category | Config key | Notes |
|----------|------------|--------|
| Video | `fps_max` | 0–1000; `0` = uncapped (display/GPU may still limit) |
| Video | `fps_max_ui` | UI / menu frame limit |
| Advanced Video | `engine_low_latency_sleep_after_client_tick` | boolean |
| HUD | `cl_hud_telemetry_frametime_show` | 0/1 |
| HUD | `cl_hud_telemetry_ping_show` | 0/1 |
| HUD | `cl_hud_telemetry_net_misdelivery_show` | 0/1 |
| HUD | `cl_showloadout` | 0/1 |
| HUD | `cl_hud_radar_scale` | 0.5–1.3 |
| HUD | `cl_hud_color` | 0–12 |
| Game | `cl_teamid_overhead_always` | 0/1 |
| Game | `cl_use_opens_buy_menu` | 0/1 |
| Game | `mm_dedicated_search_maxping` | 20–350 |
| Game | `joystick` | 0/1 |
| Keyboard/Mouse | `sensitivity` | 0.01–20 |
| Keyboard/Mouse | `zoom_sensitivity_ratio` | 0.01–5 |
| Keyboard/Mouse | `m_rawinput` | 0/1 |
| Keyboard/Mouse | `m_customaccel` | 0–3 |
| Audio | `volume` | 0.0–1.0 |
| Audio | `snd_voipvolume` | 0.0–1.0 |
| Audio | `snd_headphone_eq` | 0–3 |
| Audio | `snd_musicvolume_multiplier` | 0.0–1.0 |
| Communication | `cl_mute_enemy_team` | 0/1 |
| Communication | `cl_mute_all_but_friends_and_party` | 0/1 |
| Communication | `cl_sanitize_player_names` | 0/1 |

### Settings apply safety flow

1. **Validate** values against the catalog (type, range, allowed set).  
2. **Diff** current vs desired (setting, current, new, reason, risk, restart flag).  
3. **Confirm** in the UI — nothing is written until you accept.  
4. **Backup** (when automatic backup is enabled) of the managed cfg + previous values.  
5. **Apply** to `frameforge_settings.cfg` only.  
6. **Verify** written values; on failure **auto-rollback** via the backup.  
7. **Reset FrameForge changes** restores the last settings backup only (does not wipe unrelated user files).

### Profiles import / export

- Extension: `.frameforge-profile.json`  
- Required: `schemaVersion` (currently `1`), `name`, `settings` map  
- Rejected: schema version newer than the app supports, unknown keys, invalid values  
- Import content is **data only** — never executed  
- Built-in ids (`competitive`, `balanced`, `quality`) are never overwritten on import

## Not implemented yet (honest)

- Automatic Windows power-plan / registry tweaks (advisory only)
- Windows Game Mode status probe (score factor present, 0 points)
- Windows GPU name via DXGI/WMI
- Launch-at-startup registration
- Full CS2 frame-time benchmark / FPS claims
- Automatic Steam launch-option editing
- Binary / `video.txt` graphics quality sliders (excluded until documented)

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
  FrameForge.CS2              Steam / CS2 detect + cfg + settings catalog/service
  FrameForge.Optimization     Catalog, pipeline, score
  FrameForge.Benchmark        Lightweight samples
  FrameForge.Infrastructure   DI, backup, profiles, logs
tests/
  FrameForge.Tests
assets/profiles/              Built-in profile JSON (schema v1)
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
