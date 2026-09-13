# GPU utilization (Phase 11 research)

## Decision: **Unavailable**

FrameForge does **not** sample GPU utilization in this release.

### PDH investigation (Windows)

Windows Performance Data Helper (PDH) can expose counters such as
`\GPU Engine(*)\Utilization Percentage` on some systems. Requirements for
shipping that path were:

- No NuGet / no vendor SDK
- No driver install, injection, or memory access
- Reliable discovery without hardcoded adapter indices
- Graceful failure when counters are absent
- Low overhead

### Why not implemented

1. Counter sets differ by driver and Windows build; many machines expose no
   GPU Engine counters or only per-process engine instances that need
   elevated access or unstable instance names.
2. Mapping multi-GPU / iGPU+dGPU without hardcoded names is fragile.
3. A wrong or zero reading would be worse than an honest **Unavailable**.
4. Frame-time / FPS still require injection or graphics hooks we refuse.

### Future

If a documented, dependency-free PDH probe proves stable across consumer
Windows 10/11 + common drivers, it can be added behind the existing
`MetricSummary.Unavailable` path without inventing values when discovery fails.
