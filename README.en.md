# Dell DW5931E / Fibocom FM350-GL FCC Unlock for Windows

Unlocks the startup FCC software lock on a Dell DW5931E (Fibocom FM350-GL) installed in a non-Dell PC and configures automatic unlock at Windows startup.

This project does not flash or modify modem firmware and does not redistribute Dell drivers, authentication services, or other proprietary binaries.

## Verified setup

- Dell DW5931E / Fibocom FM350-GL
- PCI ID `14C3:4D75`, Dell subsystem `1028:5931`
- Firmware `81600.0000.00.29.23.28_CT / F32`
- Windows 10 IoT Enterprise LTSC 19044 x64
- Non-Dell X99 motherboard
- China Telecom 5G SA

Other firmware, carrier, and OEM variants are currently unverified.

## Install

Install a suitable Dell driver first. Then open Windows PowerShell as Administrator:

```powershell
git clone https://github.com/AsterZ99/fm350-dell-fcc-unlock-windows.git
cd fm350-dell-fcc-unlock-windows
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

The installer builds a self-contained x64 .NET Windows service, backs up matching driver settings, keeps the PCIe COM AT diagnostic endpoint enabled, disables adapter selective suspend and device sleep on disconnect to avoid PCIe Surprise Down/Completion Timeout on affected X99 systems, and disables the Dell SMBIOS-dependent authenticator. `FM350UnlockService` runs as `LocalSystem`, observes radio state through WinRT, and performs the verified private-MBIM Query/Set authentication as soon as a locked modem appears. It removes the former AT startup task. The production unlock path has no COM-port or physical-USB dependency; COM AT remains available for manual band/cell diagnostics.

During a cold start, the verified modem can enumerate as either the generic Fibocom persona `SUBSYS_35001CF8` or the Dell runtime persona `SUBSYS_59311028`. The former does not answer the leading Query while FCC-locked, so the service uses Set, Set, then a confirming Query. The Dell persona uses the vendor sequence Query, Set, Set, then confirming Query. Both paths require the final Query's second field to become `1`, and neither mixes AT into an authentication attempt.

Installing from source requires .NET SDK 10. The installed single-file service is self-contained and does not require a preinstalled .NET runtime.

Log file:

```text
C:\ProgramData\FM350Unlock\service.log
```

Run manually or test the algorithm without a modem:

```powershell
.\Unlock-FM350.ps1
.\Unlock-FM350.ps1 -SelfTest
.\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe --self-test
.\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe --probe-mbim
.\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe --query-mbim-default
```

See [README.md](README.md) and [docs/RESEARCH.md](docs/RESEARCH.md) for the algorithm, diagnostics, research notes, rollback steps, and safety notes.

## Uninstall

Run as Administrator:

```powershell
.\Uninstall.ps1
```

The uninstaller removes the .NET service and any legacy fallback task, then restores the recorded registry values and Dell service startup mode.

## License and disclaimer

MIT licensed and provided without warranty. Use only on hardware you own or are authorized to maintain, and comply with local radio regulations and carrier requirements. This project is not affiliated with Fibocom, Dell, Intel, or MediaTek.
