# Changelog

All notable changes to iSCSI-Util are documented in this file.

## [5.0.1] - 2026-11-15

### Added

- Ghost target cleanup in *Configuration → Maintenance → Remove ghost
  targets…*. Node records that the storage server no longer exports are
  detected per reachable portal, shown in a confirmation dialog and removed
  after logging out their sessions.
- Records that belong to an unreachable portal are listed separately and only
  removed when the user explicitly opts in.
- Progress dialog while scanning portals and removing records, reporting the
  current portal or target and the final result.
- Cleanup summary with removed, skipped (still mounted) and failed targets.
- Ghost cleanup also removes the matching entries from the local
  `session_history.json` and refreshes the Sessions and Targets views.

### Changed

- Discovery now uses `iscsiadm -o new`, both in the GUI and in the CLI helper,
  so an existing node record is never rewritten and its per-node CHAP
  credentials are preserved.

### Fixed

- CHAP detection no longer reads the node database files directly. Those files
  are `0600 root`, so unprivileged reads always failed and targets with CHAP
  were reported as unauthenticated; detection now uses
  `iscsiadm -m node -o show` through `sudo`.
- Removed the obsolete file-based CHAP detection from the persistence layer and
  the unused call in the targets view.

## [5.0.0] - 2026-11-14

This is a major release that consolidates the work done after 4.5: multi-LUN
support, safer persistence, a redesigned Status view and a broad stability and
security pass.

### Added

- Multi-LUN support: targets expose each LUN as an independent child with its
  own device, filesystem, mount point and size.
- Per-LUN actions: connect, mount, initialize and enable autostart without
  affecting other LUNs.
- Hot LUN rescan: detect new LUNs and capacity changes without restarting the
  session; mounted ext2/3/4, XFS and Btrfs filesystems grow online.
- IOPS metric in the Status performance card.
- Safe boot persistence with `_netdev`, `nofail` and
  `x-systemd.requires=iscsid.service`.
- Dedicated CHAP and Mutual CHAP overview.

### Changed

- Redesigned Status view for the fixed 500x580 window, including a dynamic
  banner and a vertical safety scroll.
- Configuration folder pickers now use Avalonia `StorageProvider`.
- Shell commands use POSIX-safe argument quoting and a stable `C` locale.

### Fixed

- CHAP and Mutual CHAP no longer overwrite stored secrets with the masked
  `********` value returned by `iscsiadm -o show`. Existing secrets are kept
  unless the user enters a real password.
- CHAP portals are normalized before calling `iscsiadm`, avoiding duplicated
  `:3260` suffixes.
- Invalid CHAP credentials mark the local configuration as not usable so the
  CHAP dialog can be opened again from the target view.
- Real status detection for the Status view, with tracing written to the log.
- Mount/unmount and smart action button behavior.
- Sudo password rejection now asks for a new password instead of blocking.
- Startup and session `async void` handlers no longer lose exceptions.
- iSCSI operations are serialized between views and no longer block the UI
  thread.
- Command execution has a real timeout and reads stdout/stderr concurrently.
- Atomic configuration writes and de-duplicated log entries.

### Security

- Updated Avalonia from 11.3.9 to 11.3.23, which resolves
  `Tmds.DBus.Protocol` to the patched 0.21.3 and removes
  GHSA-xrw6-gwf8-vvr9 (high severity).
- `dotnet list package --vulnerable` reports no vulnerable packages.

### Internal

- Nullable reference types are now clean across the project.
- Removed the unused duplicate `Credenciales` class from `Utils`.
- Added `.gitignore` and removed `bin/` and `obj/` build artifacts from the
  repository.
- Added `scripts/build-release.sh` to reproduce the release artifacts.
