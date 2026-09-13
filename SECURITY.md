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
- Steam library and CS2 install path detection
- Documented CS2 configuration file read/write with backups
- User-approved system recommendations that can be reverted
- Local logging and backup/restore of user files

## Reporting vulnerabilities

If you discover a security issue in FrameForge (for example path traversal in backup restore, unsafe file writes, or accidental introduction of process injection):

1. **Do not** open a public issue with exploit details.
2. Email or privately message the maintainers with:
   - A clear description of the issue
   - Steps to reproduce
   - Impact assessment
3. Allow reasonable time for a fix before public disclosure.

## Safe contribution rules

Pull requests that add cheat functionality, memory editing, injection, or anti-cheat bypass will be rejected.

When adding optimizations:

- Prefer advisory recommendations over automatic system changes
- Always implement `Revert()` and backup metadata
- Do not use undocumented CS2 keys without evidence they are user-facing config
- Never log passwords, tokens, or hardware identifiers beyond what is needed for support
