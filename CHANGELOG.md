# Changelog

## Unreleased

- `Install-Sms.ps1` starts the app at sign-in with a per-user scheduled task
  ("FM350 SMS", no administrator rights) instead of the HKCU Run key, which
  Windows starts one entry at a time and could delay by several minutes. The
  old Run value is removed; the Run key is used only if the task cannot be
  created. `Uninstall-Sms.ps1` removes the task.
- `Fm350Sms` has an app icon (blue tile, white speech bubble, signal arcs):
  embedded in the exe (Start menu shortcut, Explorer, taskbar), used for the
  tray icon at the system's small-icon size, the window title bar, and
  registered for notifications.
- Project renamed to `fm350-windows-unlock-sms`. `README.md` is now one
  English guide for both the FCC unlock and the SMS app; the Chinese guide
  moved to `README.zh-CN.md`, and `README.en.md` was merged into `README.md`.
- `Install-Sms.ps1` adds a Start menu shortcut "FM350 SMS"; `Uninstall-Sms.ps1`
  removes it.
- Web links in messages (http, https, www.) are listed under the message as
  clickable links that open in the default browser. The real host is shown
  (punycode for look-alike domains) and the full address appears on hover;
  other schemes and links with a user name before `@` are ignored.

## Fm350Sms 0.2.0 - 2026-11-18

### Added

- `Fm350Sms` is now a Hebrew (right-to-left) WPF SMS app with a window, a tray
  icon, and notifications that open the conversation. It replaces the v0.1.0
  receiver in place (same exe, data folder, Run value and notification id).
- Messages are stored in SQLite (`%LOCALAPPDATA%\FM350Sms\sms.db`). The v0.1.0
  `messages.txt` and `state.json` are imported once and left untouched.
- Conversations grouped by contact, with search and rename.
- Contacts import from a Google Contacts CSV export (current and older
  layout) or vCard, and export of the app's own contacts as Google CSV.
- Delete a message or a conversation (in the app only, never on the modem).
- Sending SMS over MBIM, including long messages. Enter sends and Shift+Enter
  starts a new line. "נשלחה (המודם לא אישר)" marks a message the modem did not
  confirm. Failed messages can be retried; one that timed out or was only
  partly sent says so, and "שלח שוב" asks before sending it again. A message
  can have at most 10 parts.
- `--where` prints the data folder in use and whether it is redirected (exit
  code 3 when it is).
- `--merge-db <sms.db>` merges another database into the current one. It makes
  an online backup first, reads the source through a temporary copy, skips
  duplicates, and adds nothing on a second run.

### Changed

- `Install-Sms.ps1` starts the app minimized at logon (`--minimized`), stores
  `-DeleteAfterSave` in `settings.json` (so turning it off in the app's menu
  sticks; without the switch an existing value is kept, and an older install's
  `--delete-after-save` is carried over), registers the `fm350sms:`
  link, and points the contacts import at the repository's `contacts` folder
  when it exists. It builds outside `src\Fm350Sms\bin`, so a running copy no
  longer breaks the build.
- `Install-Sms.ps1` and `Uninstall-Sms.ps1` stop without changing anything when
  `%LOCALAPPDATA%` is redirected by a packaged (MSIX) host such as the Claude
  desktop app, and the app refuses to start there. The installer offers to
  merge history found in the Claude private copy into the real database.
- `Uninstall-Sms.ps1` also removes the `fm350sms:` link registration, and keeps
  `sms.db`, `settings.json` and the v0.1.0 `messages.txt` and `state.json`
  unless `-RemoveMessages` is given.

### Removed

- The temporary `--send-test` command.

## Fm350Sms 0.1.0

- `Fm350Sms`, an optional per-user SMS receiver. It reads the modem's SMS
  store over MBIM through the Windows Mobile Broadband API (`IMbnSms`), shows
  each new message as a Windows notification and appends it to
  `%LOCALAPPDATA%\FM350Sms\messages.txt`. It decodes GSM 7-bit, 8-bit and UCS2
  PDUs, joins concatenated messages, and needs neither an AT port nor
  administrator rights. `Install-Sms.ps1` and `Uninstall-Sms.ps1` manage it;
  `-DeleteAfterSave` removes saved messages from the modem.
- CI builds the SMS receiver and runs its PDU decoder self-test.

## 0.4.1 - 2026-09-14

### Changed

- Perform at most one successful FCC authentication during one continuously
  present PnP device lifetime. The guard is cleared only after the modem
  disappears or is re-enumerated, so intentionally turning the cellular radio
  off no longer causes repeated private-MBIM sessions.
- Re-scan matching network-class and PCI device instances whenever the
  installer is re-run. This keeps configuration and rollback coverage intact
  after moving the modem to another PCIe slot.
- Preserve the optional COM AT endpoint for manual band and cell diagnostics;
  automatic authentication remains MBIM-only and has no physical-USB
  dependency.
- Back up and disable selective suspend, sleep-on-disconnect, PCI D3cold, WDF
  directed power transitions, and working-state idle on matching modem and AT
  child instances. The version 5 rollback record lets `Uninstall.ps1` restore
  every recorded value to its pre-install state.
- Add `Install.ps1 -SkipServicePublish` for reapplying device configuration to
  an already installed service without rebuilding or replacing its executable.

### Validation result and scope

Cold-start testing on the documented Dell DW5931E/F32 sample confirmed that the
standalone .NET service can complete the private-MBIM FCC challenge/response,
turn the Windows hardware radio on, and connect automatically to China Telecom
5G SA. This validates the authentication algorithm, payloads, device-service
route, and startup integration.

The same host later showed PCIe/WHEA and NVMe disappearance under high system
load, including a storage failure after the FM350 had been removed. That is
evidence of a host, slot, adapter, power-delivery, or broader PCIe stability
problem independent of FCC authentication. The power-policy changes above can
avoid unsupported low-power transitions, but they cannot repair an electrically
unstable platform. Long-duration modem stability on this particular X99 host is
therefore not claimed.

## 0.4.0 - 2026-09-11

- Replaced the legacy AT startup task with a self-contained .NET Windows
  service using the verified private-MBIM command 1 flow.
- Added support for both the generic `SUBSYS_35001CF8` and Dell
  `SUBSYS_59311028` enumeration paths.
