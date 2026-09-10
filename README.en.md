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
git clone https://github.com/OWNER/fm350-dell-fcc-unlock-windows.git
cd fm350-dell-fcc-unlock-windows
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

The installer backs up matching driver settings, enables the COM AT port, disables driver selective suspend and the Dell SMBIOS-dependent authenticator service, and creates a `SYSTEM` task named `FM350 FCC Unlock`. It runs at startup and checks the hardware-radio state once per minute, reopening the AT port only when an in-session modem reset has restored the FCC lock.

Log file:

```text
C:\ProgramData\FM350Unlock\unlock.log
```

Run manually or test the algorithm without a modem:

```powershell
.\Unlock-FM350.ps1
.\Unlock-FM350.ps1 -SelfTest
```

See [README.md](README.md) and [docs/RESEARCH.md](docs/RESEARCH.md) for the algorithm, diagnostics, research notes, rollback steps, and safety notes.

## Uninstall

Run as Administrator:

```powershell
.\Uninstall.ps1
```

The uninstaller removes the startup task and restores the recorded registry values and Dell service startup mode.

## License and disclaimer

MIT licensed and provided without warranty. Use only on hardware you own or are authorized to maintain, and comply with local radio regulations and carrier requirements. This project is not affiliated with Fibocom, Dell, Intel, or MediaTek.
