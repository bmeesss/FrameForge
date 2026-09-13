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
- **Benchmark engine** — external OS-level session to compare system states (before/after). CPU, process CPU/memory, system memory; GPU util/temps and CS2 frame-time marked unavailable without injection. Local JSON history, A/B comparison, JSON/CSV export. **Does not guarantee FPS improvements.**
- **Guided Optimize & Benchmark** — orchestrated baseline → preview → **explicit confirm** → backup → apply → verify → post-benchmark → compare → Keep|Restore. Uses the same settings apply path only. Local guided-run history. **No FPS guarantees.**
- **Custom / individual optimization** — pick single or multiple supported CS2 settings (or a full profile). In-memory search/filter, custom sets, save-as-profile via existing profile system, guided A/B through Phase 5 service. **Recommended ≠ measured.**
- **Performance intelligence** — learns only from **local** guided benchmark history. Per-setting records, confidence heuristics, single vs multi-setting evidence, system fingerprint (no PII). **No telemetry. No FPS guarantees.**
- **Safe targeted restore** — restores individual FrameForge-managed keys only when assessment proves safety; refuses user-changed values. Never silent full-backup fallback. Re-checks managed files before write; external cfg changes invalidate assessments.
- **One-click retest** — re-runs a single setting through the existing guided optimize & benchmark workflow from Performance History / Custom Optimization.
- **Benchmark UX (Phase 9)** — live phase/elapsed/remaining/samples/interval/warm-up/CS2 status from the engine only (Preparing → Warm-up → Benchmarking → Finishing → Analyzing → Completed). Cancel shows Stopping… then cancelled. CS2 exit mid-run fails safely with a clear explanation and **never** auto-restarts CS2. Unavailable metrics stay labeled Unavailable.
- **Guided confirm detail** — confirmation lists profile/name, setting count, files, current→target values, backup plan, bench plan, and risk before apply; apply phases message Creating backup / Applying / Verifying.
- **Intelligence export/import** — `.frameforge-intelligence.json` and `.frameforge-snapshots.json`; no PII; validate + preview (default) then merge or import-as-new; imported fingerprints stay labeled Different system — never silently merged into the current machine.
- **External cfg detection** — lightweight watcher on `frameforge_settings.cfg` and the autoexec FRAMEFORGE section only; hashes managed content; user lines outside markers do not invalidate restore safety.
- **History reliability** — AtomicFile for history writes; corrupt JSON quarantined (not silent delete); intelligence index rebuilds from guided run files when needed.
- **Logging** — structured local logs; secrets redacted.
- **UI** — dark WPF shell with loading/empty/error states and confirmation dialogs. Execution status warning when the managed cfg is not wired into autoexec. Benchmark page with live sample timeline. Optimize & Benchmark page with step progress and Keep/Restore. Custom Optimization picker page. Tooltips on restore/import; dialogs default focus to safest action (Cancel).

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

## Benchmark engine (external only)

### Purpose

Compare **two system states** objectively (e.g. before vs after a profile apply).  
FrameForge **does not guarantee FPS improvements** and will not advertise fake boosts.

### What is measured

| Metric | Source | Notes |
|--------|--------|--------|
| System CPU % | Windows `GetSystemTimes` / Linux `/proc/stat` | Delta between samples |
| CS2 process CPU % | `Process.TotalProcessorTime` | External process info only |
| CS2 working set | `Process.WorkingSet64` | External process info only |
| System memory % | Windows `GlobalMemoryStatusEx` / Linux `/proc/meminfo` | |
| Frame-time / FPS | — | **Unavailable** (no CS2 injection, no DirectX hook) |
| GPU utilization / temps | — | **Unavailable** in this build (no vendor SDK / DXGI hook) |

FPS is only derived as `1000 / frameTimeMs` when frame-time samples exist. P1 / P0.1 labels are **frame-time percentiles** (99th / 99.9th), not FPS percentiles.

### What is not measured / not done

- No DLL injection, memory reading, kernel drivers, or anti-cheat bypass  
- No automatic optimization apply as part of the benchmark (measure only)  
- No network upload of results  

### Session parameters

- Duration: **30 / 60 / 120** seconds  
- Sample interval: **50–1000 ms** (default 100 ms)  
- Warm-up: default **10 s** (excluded from aggregates)  
- States: Idle → Preparing → Running → Stopping → Completed | Failed | Cancelled  

### Storage

Local only:

```text
%LocalAppData%/FrameForge/Benchmarks/yyyy-MM-dd_HHmmss_<id>.json
```

Export JSON (full run) or CSV (raw samples) from the Benchmark page. No telemetry.

### Monitoring overhead

Default 100 ms interval; each sample is a few OS counter / process property reads. Avoid intervals below 50 ms so the monitor itself does not skew results.

### Before / after comparison

Select two history runs (A and B). The UI shows metric, before, after, absolute difference, and percent difference for **available** metrics only. Unavailable metrics stay labeled unavailable.

## Guided Optimize & Benchmark

Orchestrates existing systems into one safe A/B workflow. It does **not** invent a second apply path and does **not** change Windows registry, power plans, or startup items.

### Workflow

1. **Select** a profile (or explicit settings map) — never auto-apply “all optimizations”.
2. **Validate** install + desired values; tip to close extra apps for repeatability.
3. **CS2 process check** for benchmarks — if CS2 is not running, FrameForge explains and stops. It does **not** launch CS2.
4. **Baseline benchmark** with the configured duration / interval / warm-up.
5. **Preview diff** (files, values, risk) — **Preview only** mode stops here (detect/validate/diff, no writes).
6. **Explicit Confirm Apply** — required; cannot be skipped.
7. **Backup** managed cfg + autoexec. Backup failure → **STOP** (no apply).
8. **Apply** via existing `ICs2SettingsService.ApplyDiffAsync` only → **verify**. Verify failure → auto-restore → run ends Restored/Failed.
9. **Post benchmark** with the **same** duration / interval / warm-up as the baseline.
10. **Compare** measured metrics → classification (see below).
11. **Keep** (leave cfg) or **Restore** (backup restore + verify). Benchmark history is never deleted on restore.
12. **Persist** the guided run under local `GuidedRuns/`.

### States

`Idle` → `Preparing` → `BenchmarkingBefore` → `PreparingOptimization` → `AwaitingConfirmation` → `Applying` → `BenchmarkingAfter` → `Comparing` → `AwaitingDecision` → `Completed` | `Restoring` → `Restored` | `Cancelled` | `Failed`.

### Condition warnings

If CPU / GPU / RAM / OS / CS2 process / profile / duration / interval / power / resolution / refresh differ between baseline and post (when known), FrameForge warns that the comparison **may not be reliable**. Warnings rarely hard-block; they are recorded on the run.

### Comparison & classification

Rows: metric · before · after · Δ · % · interpretation. Unavailable metrics stay **N/A**. FrameForge **never invents FPS**.

Classification uses **measured** metrics only (noise band ≈ 3% + metric epsilons; preferred direction is usually “lower is better” for CPU/memory load):

| Result | Rule (summary) |
|--------|----------------|
| **Improved** | All available decisive metrics moved in the preferred direction beyond noise |
| **Regressed** | All available decisive metrics moved against the preferred direction beyond noise |
| **Neutral** | Changes within noise / no decisive movement |
| **Mixed** | Some improved and some regressed beyond noise |
| **Inconclusive** | No usable metrics (e.g. all N/A) |

There is **no auto “this improved your FPS”** claim. Results depend on conditions (background load, map, resolution, power mode, etc.).

### Cancel safety

- **Before apply** (including at confirmation): stop cleanly; no cfg changes.
- **During apply**: let apply/rollback finish via the settings service.
- **During benchmark**: stop the engine; prior cfg is preserved if apply has not succeeded.

### Storage

```text
%LocalAppData%/FrameForge/GuidedRuns/<id>.json
```

Corrupt history files are skipped; valid runs still list.

### Limits (Phase 5)

- No registry / power-plan / startup / system mods.
- No second write path — only existing CS2 settings apply + backup.
- No guaranteed performance gains.
- Frame-time / GPU still unavailable without injection (same as Benchmark).

### Example workflows

**Preview only:** select Competitive → *Preview Only* → review diff → no files written.

**Full keep:** start CS2 → select Balanced → *Run Guided Optimization* → baseline finishes → *Confirm Apply* → post benchmark → *Keep Changes*.

**Full restore:** same as full keep through comparison → *Restore Previous* → cfg returned to pre-apply backup; baseline/post benchmark JSON remain in history.

## Custom / individual optimization selection

Users are not forced to apply an entire profile. Three supported targets all use the **same** pipeline:

Validation → Diff → Backup → Apply → Verify → (optional Guided) Benchmark → Compare → Keep/Restore

| Target | How |
|--------|-----|
| **Single setting** | Check one row on Custom Optimization → Preview or Quick test |
| **Multiple settings** | Multi-select → temporary set → Preview / Guided test |
| **Full profile** | Profiles page or Guided page (unchanged) |

There is **no second configuration-writing path** — apply always goes through `ICs2SettingsService`.

### Catalog

Built from supported `Cs2SettingDefinition` entries only. Each item exposes: Id, Name, Category, Description, CurrentValue, RecommendedValue, Risk (Low/Medium/High), ExpectedImpact (Low/Medium/High/Unknown + qualitative description), Supported, RequiresRestart.

- **Expected impact** = intended relevance, **not** a guaranteed FPS increase.  
- **Never** shown as “+10 FPS” / “FPS boost guaranteed”.  
- **Measured results** come only from the benchmark / guided comparison UI.

UI categories: Performance, HUD, Mouse, Audio, Communication, Gameplay (mapped from existing CS2 setting categories).

### Picker UX

- Search by name / config key (in-memory; no disk I/O while typing)  
- Filter by category, risk, “only changed”, “only differ from recommended”  
- Nothing selected by default  
- High-risk items require explicit confirmation before preview/apply  
- Clear distinction: **Recommended** vs **Current** vs **Target** vs **Measured result**

### Custom optimization sets

`CustomOptimizationSet` (Id, Name, Description, Settings, CreatedAt, UpdatedAt) is stored under:

```text
%LocalAppData%/FrameForge/CustomOptimizationSets/<id>.json
```

Separate from built-in/custom **profiles**. CRUD: create, rename, duplicate, edit (re-save selection), delete. Validation rejects unknown keys, unsupported settings, invalid values, malformed JSON, and future schema versions. Import never executes data.

### Save as Profile

Selected settings → **Save as Profile** calls existing `IProfileService.SaveCustomProfileAsync`. No duplicated profile persistence.

### Guided integration

Custom selection → `GuidedTargetKind.SettingsMap` on existing `IGuidedOptimizationService`. Guided history (schema v2) records selected keys/values, optional custom set id/name, baseline/post benchmarks, decision, classification. v1 history files still load.

### Reset

“Reset via backup” uses the existing FrameForge backup restore for managed cfg + autoexec. It is **not** a guaranteed single-key surgical restore; the UI states this honestly.

## Performance intelligence (local only)

FrameForge answers: *“What happened when this setting was tested on this PC?”* using **only** local guided-run history.

### What it does **not** do

- No cloud services, no telemetry upload  
- No invented FPS values  
- No claim of statistical certainty  
- No guaranteed improvements  

### System fingerprint

Stable local hash of non-sensitive fields: CPU model, GPU model, RAM amount, OS version, architecture.  
**Excluded:** username, email, Steam account, IP, serials, MAC, filesystem secrets. Missing fields → `unknown`.

### Evidence types

| Type | Meaning |
|------|---------|
| **SingleSetting** | Exactly one setting changed — may count as **direct** evidence |
| **MultiSetting** | Several settings changed together — **associated only**; never treated as causal proof for each key |

### Confidence (heuristic labels)

Documented rules in `PerformanceConfidenceRules`:

| Valid direct tests | Typical label |
|--------------------|---------------|
| 0 | **Unknown** |
| 1 | **Low** |
| 2–3 consistent | **Medium** |
| 4+ consistent | **High** |
| Conflicting improved/regressed | stays **Low** |

Multi-setting evidence alone never raises confidence above Unknown. These are **not** scientific confidence intervals.

### Storage

```text
%LocalAppData%/FrameForge/PerformanceHistory/index.json
%LocalAppData%/FrameForge/PerformanceHistory/setting-change-snapshots.json
```

In-memory analysis cache; rebuild on guided history change or manual refresh — not on every UI repaint.

### Per-key snapshot metadata

When FrameForge applies managed settings, each key change is recorded (SettingId, Previous/New value, File, Timestamp, BackupId) **alongside** the existing full backup.

### Safe targeted restore

`ITargetedRestoreService` assesses then optionally restores **one key** inside `frameforge_settings.cfg`.

| Assessment | Meaning |
|------------|---------|
| **SafeToTargetRestore** | Snapshot consistent; current managed value equals last FrameForge-applied value |
| **UnsafeToTargetRestore** | Current value differs (user/external change) or path/ownership uncertain — **refused** |
| **InsufficientEvidence** | Incomplete/corrupt snapshot metadata |
| **NotTracked** | No snapshot for this setting |

**Mandatory rule:** if the user later changes `fps_max` from the FrameForge-applied value, targeted restore **will not** overwrite it.

Restore flow: Assess → refuse if unsafe → backup current managed file → write previous value for tracked key only → verify key + unrelated keys → record snapshot.  
**Does not** silently fall back to full backup restore (that remains a separate Backups action).  
**Does not** rewrite user autoexec content outside FRAMEFORGE markers.

### One-click retest

From Performance History or Custom Optimization: **Retest this setting** runs the existing guided pipeline (baseline → confirm → backup → apply target → post bench → keep/restore).  
Target choices: Recommended / Previously tested / Custom — never silent.  
After completion, intelligence cache rebuilds (new evidence, confidence, latest classification).

### Fingerprint separation

Evidence is tied to `SystemFingerprintId`. Hardware changes do **not** merge into one aggregate. UI defaults to **current system**; optional “all systems” filter labels other rows as *Different system fingerprint*.

### UI

- **Custom Optimization** rows show Last tested / Tests / Latest / Confidence when evidence exists, plus “Based on your tests” blurbs  
- **Performance History** page: fingerprint, per-setting aggregates, direct vs associated evidence, multi-run compare, simple trend of recorded CPU deltas  

## Not implemented yet (honest)


- Automatic Windows power-plan / registry tweaks (advisory only)
- Windows Game Mode status probe (score factor present, 0 points)
- Windows GPU name via DXGI/WMI
- Launch-at-startup registration
- CS2 render frame-time without injection (not possible under our safety rules)
- GPU utilization via documented counters without vendor lock-in
- **Automatic** Steam launch-option editing (manual recommendation only)
- Binary / `video.txt` graphics quality sliders
- Continuous / scheduled guided runs
- Bulk multi-setting targeted restore UX polish / progress UI
- In-game overlay for live recommended vs measured comparison

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
  FrameForge.Benchmark        External benchmark engine + OS sampler
  FrameForge.Infrastructure   DI, backup, profiles, logs, guided, custom sets, performance intelligence
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
