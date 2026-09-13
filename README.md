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
- **Optimization engine** — `IOptimization` with preview, backup requirements, apply, revert. Pipeline: Detect → Analyze → Preview → Backup → Apply → Verify → Rollback on failure.
- **CS2 configuration integration** — real cfg directory from Steam detection; managed settings file + marked autoexec hook so CS2 actually executes FrameForge settings (see below).
- **CS2 Settings** — strongly typed, category-grouped catalog of **documented cfg-backed** keys only. Flow: Read → Validate → **Diff preview (with real file paths)** → Confirm → Backup → Apply → Verify → auto-rollback on failure.
- **Profiles** — schema v1 Competitive / Balanced / Quality + custom CRUD/import/export. Apply uses the same real-cfg path as Settings.
- **Backups** — file snapshots + metadata; restore returns exact previous bytes; files created by an apply are deleted on restore.
- **Logging** — structured local logs; secrets redacted.
- **UI** — dark WPF shell with loading/empty/error states and confirmation dialogs. Execution status warning when the managed cfg is not wired into autoexec.

## How FrameForge modifies CS2 configuration

### Where files live

FrameForge uses the **cfg directory discovered by CS2 detection** (never a hardcoded Steam path), typically:

```text
<Steam library>/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/
```

### Exact files FrameForge may write

| File | Role |
|------|------|
| `frameforge_settings.cfg` | **Managed settings file.** Holds documented convar key/value pairs owned by FrameForge. |
| `autoexec.cfg` | **Integration hook only.** FrameForge inserts or updates a marked section; it never deletes user lines outside the markers. |

No other CS2 files are modified by the Settings/Profiles apply path.

### How CS2 executes FrameForge settings

CS2 does **not** auto-load arbitrary `*.cfg` files. FrameForge therefore:

1. Writes values to `frameforge_settings.cfg`.
2. Ensures `autoexec.cfg` contains:

```cfg
// FRAMEFORGE BEGIN
// Managed by FrameForge — do not edit between these markers.
// Settings live in frameforge_settings.cfg so your other autoexec lines stay intact.
exec frameforge_settings.cfg
// FRAMEFORGE END
```

3. On many installs, CS2 runs `autoexec.cfg` at startup. If it does not on your setup, FrameForge **detects** that the managed file is present but not executed and shows:

> FrameForge configuration is installed but is not currently executed by CS2.

**FrameForge does not automatically edit Steam launch options.** If needed, you may add `+exec autoexec.cfg` manually in Steam → CS2 → Properties → Launch Options.

### What is preserved

- All autoexec content **outside** `// FRAMEFORGE BEGIN` … `// FRAMEFORGE END`.
- Unknown / comment lines inside any cfg FrameForge parses.
- User binds and personal settings in autoexec.

### Apply safety flow

1. **Read** actual cfg directory + merge supported keys (other cfg &lt; autoexec &lt; managed).  
2. **Validate** values against the catalog.  
3. **Diff** (setting, current, new, reason, risk) + list of **real file paths**.  
4. **Confirm** in the UI — nothing is written until you accept.  
5. **Backup** `frameforge_settings.cfg` and `autoexec.cfg` (including “will be created” paths).  
6. **Apply** managed file + autoexec marked section.  
7. **Verify** written values and that user autoexec content outside markers remains; on failure **auto-rollback**.  
8. **Reset / restore** restores snapshot bytes (and deletes files that did not exist before apply).

### Profiles

Selecting a profile only marks it active / previews a diff. **Apply profile** uses the same backup → write → verify path against the real CS2 cfg directory. The UI does not claim success unless files were written (or there was nothing to change).

## Supported CS2 settings (cfg-backed)

Only keys FrameForge will read/write. Unverified or binary-only options are excluded.

| Category | Config key | Notes |
|----------|------------|--------|
| Video | `fps_max` | 0–1000; applied via cfg |
| Video | `fps_max_ui` | UI / menu frame limit |
| Advanced Video | `engine_low_latency_sleep_after_client_tick` | boolean `true`/`false` |
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
| Keyboard/Mouse | `zoom_sensitivity_ratio_mouse` | 0.01–5 (CS2 name) |
| Keyboard/Mouse | `m_rawinput` | 0/1 (legacy Source; may be redundant on some builds) |
| Keyboard/Mouse | `m_customaccel` | 0–3 (legacy Source) |
| Audio | `volume` | 0.0–1.0 |
| Audio | `snd_voipvolume` | 0.0–1.0 |
| Audio | `snd_menumusic_volume` | 0.0–1.0 |
| Audio | `snd_mvp_volume` | 0.0–1.0 |
| Communication | `cl_mute_enemy_team` | 0/1 |
| Communication | `cl_mute_all_but_friends_and_party` | 0/1 |
| Communication | `cl_sanitize_player_names` | 0/1 |

### Intentionally disabled / excluded

| Key / area | Reason |
|------------|--------|
| `snd_musicvolume_multiplier`, `snd_headphone_eq` | Not verified stable across CS2 builds |
| `cl_auto_cursor_defend`, invented placeholders | Unverified / not documented for writes |
| Binary / `video.txt` graphics quality sliders | No safe documented text format yet |
| Steam launch options | Detect + recommend only — never auto-inject in this phase |

### Profiles import / export

- Extension: `.frameforge-profile.json`  
- Required: `schemaVersion` (currently `1`), `name`, `settings` map  
- Rejected: newer schema, unknown keys, invalid values  
- Import is **data only** — never executed  
- Built-in ids are never overwritten on import  

## Not implemented yet (honest)

- Automatic Windows power-plan / registry tweaks (advisory only)
- Windows Game Mode status probe (score factor present, 0 points)
- Windows GPU name via DXGI/WMI
- Launch-at-startup registration
- Full CS2 frame-time benchmark / FPS claims
- **Automatic** Steam launch-option editing (manual recommendation only)
- Binary / `video.txt` graphics quality sliders

## Tech stack

- C# / **.NET 10** (LTS) — see `global.json`
- WPF + MVVM (Windows)
- Dependency injection via shared `Microsoft.AspNetCore.App` framework reference
- JSON local data (`System.Text.Json`)
- xUnit-style tests (offline-friendly runner included)

## Solution structure

```
src/
  FrameForge.App              WPF front-end (Windows)
  FrameForge.Core             Models, abstractions, atomic IO
  FrameForge.Hardware         Hardware probes
  FrameForge.CS2              Steam / CS2 detect + cfg + settings + autoexec integration
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
| Marked autoexec section (`FRAMEFORGE BEGIN/END`) | Blind autoexec overwrite |
| Hardware/OS inventory | ESP / aimbot / triggerbot |
| Reversible config presets | Anti-cheat bypass |
| Local backups & logs | Kernel cheats / drivers for game manipulation |
| Launch-option **recommendations** | Silent launch-option injection |

See [SECURITY.md](SECURITY.md).

## License

MIT — see [LICENSE](LICENSE).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).
