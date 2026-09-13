# Security Policy

## What FrameForge is

FrameForge is a **legitimate** Counter-Strike 2 performance companion and configuration tool.

It is **not** a cheat client.

## Hard security boundaries

FrameForge **must never**:

- Inject DLLs into CS2 or any other process
- Read or write CS2 process memory
- Implement ESP, aimbot, triggerbot, wallhacks, or similar
- Bypass, spoof, or interact with VAC / anti-cheat systems
- Ship or load kernel drivers for game manipulation
- Hide its process from security software

Allowed functionality is limited to:

- External hardware / OS information queries
- Steam library and CS2 install path detection (filesystem + optional registry read of Steam install path)
- Documented CS2 configuration file read/write with backups
- User-approved system recommendations that can be reverted
- Local logging and backup/restore of user files

## Configuration & backup safety

- Existing user files are never overwritten without a side-car or catalog backup first
- Writes use temp-file + replace where the OS allows
- Unknown cfg lines and comments are preserved
- User keys are not silently deleted on revert when a full file snapshot is unavailable
- Corrupted backup metadata is quarantined; the app continues with an empty store

## Logging hygiene

Logs must not contain passwords, tokens, API keys, or memory dumps. The file logger redacts common secret assignment patterns.

## Reporting vulnerabilities

If you discover a security issue (for example path traversal in backup restore, unsafe file writes, or accidental introduction of process injection):

1. **Do not** open a public issue with exploit details.
2. Contact the maintainers privately with description, reproduction steps, and impact.
3. Allow reasonable time for a fix before public disclosure.

## Safe contribution rules

Pull requests that add cheat functionality, memory editing, injection, or anti-cheat bypass will be rejected.

When adding optimizations:

- Prefer advisory recommendations over automatic system changes
- Always implement `Revert()` and backup metadata
- Do not use undocumented CS2 keys without evidence they are user-facing config
- Never log secrets or unnecessary hardware identifiers
