# Changelog

All notable changes to iSCSI-Util are documented in this file.

## [5.2.1] - 2026-11-21

### Fixed

- The **Remove ghost targets** dialog no longer produces a horizontal
  scrollbar for long IQNs. Each record is now a compact card with the short
  name in large text (the same abbreviation used by the session and target
  cards), the full IQN in smaller wrapped text, and the portal and flags below
  it. Horizontal scrolling is disabled and only the list scrolls vertically.
- The list of unreachable portals in the confirmation checkbox and the
  cleanup summary dialog now wrap as well, so no long IQN is cut off.

## [5.2.0] - 2026-11-19

### Added

- When `iscsid` is stopped or failed, the Status tab now shows an actionable
  hint with the exact command (`sudo systemctl enable --now iscsid`) and the
  action button becomes **Start Service**. Starting the daemon does not
  interrupt active sessions, so no confirmation is requested. The button
  switches back to **Reload** as soon as the daemon is running, so one control
  covers both actions.
- The hostname is read from `/proc/sys/kernel/hostname` and the uptime from
  `/proc/uptime`, with `hostname`/`hostnamectl` and `uptime -p` as fallbacks.
  This removes the dependency on net-tools and util-linux in minimal systems
  and keeps the values independent of the locale.
- Network and IP detection fall back to .NET APIs when `iproute2` is missing,
  and latency no longer requires `awk`: the default gateway is parsed in C#
  and both `ping -w` (iputils) and `ping -W` (BusyBox) are tried.
- Added tests for uptime parsing and formatting, gateway selection, latency
  parsing, the service hint and the dynamic Reload/Start decision.

### Changed

- *System Information* now gives uptime and hostname a full row each, so long
  values wrap instead of squeezing the neighbouring column in the fixed
  500px window. Every status value uses `TextWrapping="Wrap"`.
- The settings button is now 30x30 with an 18pt centered glyph, matching
  SAMBA-Util.
- Removed the redundant **Refresh** button from the Status tab: **Reload**
  already performs the same full status refresh while the daemon is running.
- The *Last operation* row is now **Last Update** and always shows the time of
  the last successful status read. Action results remain in the status banner.

### Fixed

- Long hostnames and uptime values are no longer truncated with an ellipsis.

## [5.1.0] - 2026-11-18

### Added

- Startup dependency check: iSCSI-Util now detects whether open-iscsi is
  installed by inspecting the `iscsiadm`/`iscsid` binaries and the
  `iscsid.service` unit (locale-independently). When it is missing, a
  dedicated English notice lists the install command for each distribution
  instead of failing silently. The same check runs in the CLI.
- Startup privilege check: iSCSI-Util now detects whether the current user can
  run `sudo` (via group membership in `sudo`/`wheel`/`admin` and a
  locale-independent `sudo -n -l` probe). When they cannot, a dedicated English
  notice explains how to grant sudo access; the CLI prints the same guidance
  instead of looping forever on a password that can never be accepted.
- The AppImage now bundles OpenSSL 3, ICU and the X11 stack, copied out of an
  AlmaLinux 9 container so they do not force a newer glibc on the target
  system. This makes the AppImage run on a wider range of distributions
  without depending on the host library versions.
- The AppImage ships AppStream metainfo and update information, and the release
  build also produces a `.zsync` file for delta updates.
- Added an xUnit test project (`ISCSI-Util.Tests`) that covers the dependency
  and privilege detection and the Status view parsing helpers.

### Changed

- The AppImage now carries the folder-based self-contained build (instead of
  the single-file one), so nothing has to be extracted into `$HOME` at
  startup, and `AppRun` sets `LD_LIBRARY_PATH` and `PATH`.
- `ISCSI-Util.tar.gz` is now byte-for-byte reproducible, so the AUR checksum is
  stable across builds.

### Fixed

- All user-facing desktop notifications and error messages are now in English.
- Every button uses `HorizontalContentAlignment="Center"` so its label is
  always centered.
- Status view false positives:
  - An ICMP `ping` that is blocked no longer marks the system as *Degraded*;
    latency is now shown as an informational value (`Unavailable`).
  - The network check no longer reports *Disconnected* when the `ip` command is
    unavailable, and it now also considers the IPv6 default route.
  - The reported IP address is chosen by ignoring virtual interfaces (Docker,
    libvirt, VPN, …) instead of hardcoding private ranges, which used to hide
    valid addresses such as `172.16.x` or `192.168.194.x`.
  - A failed `systemctl` probe is reported as *Unknown* instead of *Stopped*,
    and a `failed` unit is reported as *Failed* (critical).

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
