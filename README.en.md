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

This setup verified FCC authentication, automatic cold-start unlock, and a working China Telecom 5G SA data connection. The host later exhibited PCIe/WHEA and NVMe disappearance under load, including a storage failure after the FM350 had been removed. Long-duration hardware stability on this sample is therefore not claimed. Other motherboards, adapters, firmware, carriers, and OEM variants remain unverified. See [CHANGELOG.md](CHANGELOG.md) for the validation boundary and version history.

## Install

Install a suitable Dell driver first. Then open Windows PowerShell as Administrator:

```powershell
git clone https://github.com/AsterZ99/fm350-dell-fcc-unlock-windows.git
cd fm350-dell-fcc-unlock-windows
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

The installer builds a self-contained x64 .NET Windows service, backs up matching driver settings, keeps the PCIe COM AT diagnostic endpoint enabled, and disables adapter selective suspend, sleep on disconnect, PCI D3cold, and WDF idle-in-working-state transitions on both the adapter and its COM AT child devices. These settings reduce failures caused by incompatible low-power transitions; they cannot repair unstable motherboard, adapter, or power-delivery hardware. The installer also disables the Dell SMBIOS-dependent authenticator. `FM350UnlockService` runs as `LocalSystem`, observes radio state through WinRT, and performs the verified private-MBIM Query/Set authentication as soon as a locked modem appears. It removes the former AT startup task. The production unlock path has no COM-port or physical-USB dependency; COM AT remains available for manual band/cell diagnostics.

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

## SMS app (optional)

`Fm350Sms` is a small Hebrew (right-to-left) SMS app for the modem. It reads and sends SMS over MBIM through the Windows Mobile Broadband API (`IMbnSms`), so it needs no COM AT port and no administrator rights. Long messages that arrive in several parts are joined, and GSM 7-bit, 8-bit and UCS2 text (for example Hebrew or Chinese) is decoded.

- **Window and tray.** Conversations are listed by newest message, with a search box across names, numbers and text. Closing the window keeps the app running in the notification area. Each new SMS shows a Windows notification, and clicking it opens that conversation.
- **Contacts.** *אנשי קשר → ייבוא* (Contacts → Import) reads a Google Contacts CSV export (or a vCard `.vcf`) and names the conversations. A new import replaces the imported contacts. Names you set in the app (rename, new contact) are kept across imports, and *ייצוא* (Export) writes them as a Google CSV file that Google Contacts can import.
- **Delete.** Right-click a message or a conversation to delete it, after a confirmation. This deletes it from the app only, never from the modem, and a deleted message does not come back.
- **Send.** Use *הודעה חדשה* (New message), or type in an open conversation. Enter sends and Shift+Enter starts a new line. Long messages are sent in several parts. "נשלחה" means the modem accepted the message. "נשלחה (המודם לא אישר)" means it was handed to the modem but the modem did not confirm it, so check with the recipient before sending it again. A failed message can be retried. If the modem did not answer or only some parts went out, the bubble says so and *שלח שוב* (send again) asks first, because the recipient may already have it. A message can have at most 10 parts.
- **Storage.** Messages, contacts and settings are kept in `%LOCALAPPDATA%\FM350Sms\sms.db` (SQLite) and `settings.json`. Messages saved by v0.1.0 (`messages.txt`) are imported once.

Install for the current user (requires .NET SDK 10):

```powershell
.\Install-Sms.ps1                   # keep messages on the modem/SIM
.\Install-Sms.ps1 -DeleteAfterSave  # delete each message from the modem once saved (saved in settings.json)
```

> **Run the installer from a normal PowerShell window** (Start menu → PowerShell). Packaged (MSIX) apps such as the Claude desktop app redirect `%LOCALAPPDATA%` for every process they start into a private copy (`%LOCALAPPDATA%\Packages\<app>\LocalCache\Local`). An app installed or started from there would use a second, separate database. The installer and the app detect this and stop. If an earlier run left history in the Claude copy, the installer offers to merge it into the real database, after making a backup.

The app starts minimized at every logon (HKCU Run key). `-DeleteAfterSave` is stored in `settings.json`, so turning it off later in the app's menu sticks; running the installer again without the switch keeps the current choice. If a `contacts` folder exists next to the installer, the import dialog opens there. Useful commands:

```powershell
$sms = "$env:LOCALAPPDATA\FM350Sms\bin\Fm350Sms.exe"
& $sms --where | Out-String               # data folder in use, and whether it is redirected
& $sms --list | Out-String                # what is stored on the modem
& $sms --test-notification
& $sms --merge-db C:\path\to\sms.db | Out-String   # merge another database (backup first; safe to repeat)
```

Remove it with `.\Uninstall-Sms.ps1`, also from a normal PowerShell window. Messages and contacts are kept unless you pass `-RemoveMessages`. Receiving a UCS2 (Hebrew) SMS was verified on firmware `81600.0000.00.29.22.06_GC` with an Israeli carrier. Class 0 (flash) messages are not supported.

## Uninstall

Run as Administrator:

```powershell
.\Uninstall.ps1
```

The uninstaller removes the .NET service and any legacy fallback task, then restores the recorded adapter and device-power registry values and Dell service startup mode.

## License and disclaimer

MIT licensed and provided without warranty. Use only on hardware you own or are authorized to maintain, and comply with local radio regulations and carrier requirements. This project is not affiliated with Fibocom, Dell, Intel, or MediaTek.
