# Changelog

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
