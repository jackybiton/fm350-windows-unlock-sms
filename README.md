# FM350 for Windows: FCC Unlock & SMS

Tools for the **Dell DW5931E / Fibocom FM350-GL** 5G modem on Windows, including non-Dell PCs:

1. **FCC unlock service.** Removes the startup FCC software lock (hardware radio stuck at *Off*) and unlocks the modem automatically at every boot.
2. **SMS app (`Fm350Sms`).** Receive and send SMS through the modem from a desktop app with notifications, conversations grouped by contact, and Google Contacts import.

Both tools talk to the modem over MBIM through Windows' own Mobile Broadband APIs. They need no COM/AT port and never flash or modify modem firmware. No Dell drivers, authentication services or other proprietary binaries are redistributed.

[中文说明 (Chinese)](README.zh-CN.md) · [Changelog](CHANGELOG.md) · [Research notes](docs/RESEARCH.md) · [MBIM details](docs/MBIM.md)

## Contents

- [Requirements](#requirements)
- [Get the code](#get-the-code)
- [Part 1: FCC unlock](#part-1-fcc-unlock)
- [Part 2: SMS app](#part-2-sms-app)
- [Troubleshooting](#troubleshooting)
- [How the unlock works](#how-the-unlock-works)
- [Verified setups](#verified-setups)
- [Safety, license and credits](#safety-license-and-credits)

## Requirements

- Windows 10 (19041 or later) or Windows 11, x64.
- A Dell DW5931E / Fibocom FM350-GL with a suitable Dell (or Intel 5G Solution 5000) driver installed.
- [.NET SDK 10](https://dotnet.microsoft.com/download) to build from source. The installed programs are self-contained and need no .NET runtime.
- [Git](https://git-scm.com/download/win), or download the repository as a ZIP from GitHub.

## Get the code

```powershell
git clone https://github.com/jackybiton/fm350-windows-unlock-sms.git
cd fm350-windows-unlock-sms
Set-ExecutionPolicy -Scope Process Bypass
```

Run `Set-ExecutionPolicy` in every new PowerShell window before running the scripts below; it only affects that window.

## Part 1: FCC unlock

You need this part if Windows recognises the modem, SIM, IMEI and ICCID but shows:

```text
Hardware radio state : Off
Software radio state : On
```

On non-Dell motherboards Dell's `WWAN ModemAuthenticator` cannot find the Dell SMBIOS data it needs, and its log shows lines such as:

```text
CheckDellPcIdentifyRegistry return false
Can not find type: 1 in SMBIOS table
DoFccUnlock: Not find SMBIOS FCC Type
```

### Install

Open **Windows PowerShell as Administrator** in the project folder and run:

```powershell
.\Install.ps1
```

The installer:

1. Builds a self-contained x64 .NET Windows service and backs up the original state of the matching adapter instances and of the Dell authentication service.
2. Keeps the PCIe COM AT diagnostic port enabled, and turns off selective suspend, sleep on disconnect, PCI D3cold and WDF idle-in-working-state on the adapter and its COM AT child devices. This reduces failures caused by incompatible low-power transitions (PCIe Surprise Down, device power failures, Code 10). It cannot fix an unstable motherboard, adapter or power supply.
3. Stops and disables the Dell authenticator, which cannot work without Dell SMBIOS.
4. Installs `FM350UnlockService` (runs as `LocalSystem`, starts automatically). It watches the radio state and PCIe re-enumeration through WinRT.
5. Authenticates over the verified private MBIM command as soon as a locked modem appears. When the radio is already on, it only monitors.
6. Removes the AT startup task left by older versions.

Log file: `C:\ProgramData\FM350Unlock\service.log`. On success it records the MBIM challenge, both Set responses and the post-authentication Query, and Windows then shows the hardware radio state as *On*.

### Check and test

```powershell
netsh mbn show radio interface=*
netsh mbn show connection interface=*

# Run the unlock once by hand, or test the algorithm without a modem
.\Unlock-FM350.ps1
.\Unlock-FM350.ps1 -SelfTest

# Service diagnostics (after a build)
$svc = ".\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe"
& $svc --self-test
& $svc --probe-mbim
& $svc --query-mbim-default
```

### Uninstall

As Administrator:

```powershell
.\Uninstall.ps1
```

This removes the service and any legacy task, and restores the recorded adapter and device-power registry values and the Dell service startup mode. Logs and the rollback record stay in `C:\ProgramData\FM350Unlock`.

## Part 2: SMS app

`Fm350Sms` is a per-user desktop app with a Hebrew (right-to-left) interface. It does not need administrator rights or a COM/AT port.

### Install

> **Important:** open a **normal PowerShell window from the Start menu**. Administrator rights are not needed. Do *not* run it from a terminal inside another app such as the Claude desktop app: packaged (MSIX) apps redirect `%LOCALAPPDATA%` for the programs they start into a private copy, so an app installed from there would keep its data in a separate, hidden folder. The installer and the app detect this and stop.

```powershell
.\Install-Sms.ps1
```

The installer builds the app, runs its self-test, copies it to `%LOCALAPPDATA%\FM350Sms\bin`, adds a **Start menu shortcut "FM350 SMS"**, makes it start minimized at every logon, and starts it. If a `contacts` folder exists next to the installer, the contacts import dialog opens there. If an earlier run left message history in a redirected private copy, the installer offers to merge it into the real database (after a backup; repeating it adds nothing).

To delete each message from the modem/SIM once it has been saved in the app, install with `.\Install-Sms.ps1 -DeleteAfterSave`, or switch it on later in the app's menu. By default messages stay on the modem.

### Use

- **Open the app** from the Start menu ("FM350 SMS"), by double-clicking the tray icon next to the clock, or by clicking a new-message notification. Closing the window keeps it running in the tray; choose *יציאה* (Exit) in the tray menu to quit.
- **Conversations** are grouped by contact and sorted by the newest message. The search box filters by name, number and text.
- **Contacts:** *ייבוא אנשי קשר* (Import contacts) reads a Google Contacts export. In [contacts.google.com](https://contacts.google.com) choose *Export → Google CSV* (vCard `.vcf` works too). Importing again replaces the imported contacts. Names you set in the app (*שמור איש קשר* save contact, *שנה שם* rename, new contact) are kept across imports, and *ייצוא* (Export) writes them as a Google CSV you can import back into Google.
- **Send:** type in an open conversation or use *הודעה חדשה* (New message). **Enter** sends, **Shift+Enter** starts a new line. Each conversation keeps its own draft. Long messages are sent in up to 10 parts; the counter shows characters left and parts.
- **Send status:** *נשלחה* = the modem accepted it. *נשלחה (המודם לא אישר)* = handed to the modem but not confirmed (some firmware reports an error even when the SMS is delivered), so it is not offered for resending. *ייתכן שנשלחה* = the modem did not answer or only some parts went out; *שלח שוב* (Send again) asks before resending. *נכשלה* = failed; *נסה שוב* retries.
- **Links** in messages appear under the message and open in your browser. The real site name is shown, and the full address on hover.
- **Delete:** right-click a message or a conversation. This deletes it from the app only (never from the modem), and it will not come back.

Messages, contacts and settings are stored in `%LOCALAPPDATA%\FM350Sms` (`sms.db`, SQLite, and `settings.json`). Messages saved by the older 0.1.0 receiver (`messages.txt`) are imported once.

### Command line

```powershell
$sms = "$env:LOCALAPPDATA\FM350Sms\bin\Fm350Sms.exe"
& $sms --where | Out-String                         # data folder in use and whether it is redirected
& $sms --list | Out-String                          # messages currently stored on the modem
& $sms --test-notification                          # show a test notification
& $sms --merge-db C:\path\to\sms.db | Out-String    # merge another database (backup first; safe to repeat)
& $sms --self-test | Out-String                     # built-in tests
```

### Update or uninstall

Run `.\Install-Sms.ps1` again to update; messages, contacts and settings are kept. To remove the app (also from a normal PowerShell window):

```powershell
.\Uninstall-Sms.ps1                   # keeps messages and contacts
.\Uninstall-Sms.ps1 -RemoveMessages   # also deletes them
```

## Troubleshooting

**Unlock**

- *No COM AT port:* not needed by the service. For manual band/cell diagnostics, make sure the matching adapter instance has `WWANFeatureAT=1` and check Device Manager after a reboot.
- *Hardware radio still Off:* look at the Query, challenge, verify and postVerify fields in `service.log`. A Query of `<skipped>` is normal for `SUBSYS_35001CF8`; a successful postVerify is usually `0200000001000000`.
- *SIM recognised but not registered:* check antennas, APN, carrier bands and plan; this is no longer an FCC lock problem.
- *USB:* the service uses the PCIe/MBIM path and does not need the motherboard USB cable, so that USB header can stay with Bluetooth.
- *Adapter disappears under load, Code 10, WHEA "Surprise Down", or other PCIe/NVMe devices dropping at the same time:* this points to PCIe link or power stability, not to a wrong FCC response. Check for a compliant adapter with capacitors, slot power and signal quality, BIOS PCIe settings and motherboard stability.

**SMS**

- *The app refuses to start, or the installer stops with "redirected":* it was started from inside a packaged app. Use a normal PowerShell window or the Start menu shortcut.
- *No notifications:* check Settings → System → Notifications → FM350 SMS, and run `--test-notification`.
- *Status bar shows "מודם לא זמין" (modem unavailable):* the modem is off, locked (see Part 1) or being reset; the app retries by itself and incoming messages wait on the modem.
- *Log file:* `%LOCALAPPDATA%\FM350Sms\fm350sms.log`.

## How the unlock works

Dell's authenticator cannot get the FCC seed when the Dell SMBIOS OEM structure is missing. The DW5931E seed is the ASCII string `DW5931EFCCLOCK`. MBIM and the fallback AT tool share the same hash response:

1. Get the modem's 4-byte challenge: over MBIM from offset 4 of the first Set response; with AT from `AT+GTFCCLOCKGEN`.
2. Compute `SHA-256("DW5931EFCCLOCK")` and take the first 4 bytes: `49 09 b5 a4`.
3. Concatenate the 4 raw challenge bytes with those 4 bytes and compute SHA-256 again.
4. MBIM sends a little-endian `DWORD 1` plus the first 4 raw bytes of the new digest. The AT tool reads those 4 bytes as a big-endian unsigned integer and sends `AT+GTFCCLOCKVER=<response>` in decimal.

This differs from the vendor hash used by the Lenovo FM350-GL, so Lenovo's `3df8c719` does not apply.

The original authenticator uses `MobileBroadbandDeviceService` `{F85D46EF-AB26-4081-9868-4D183C0A3AEC}`, with command `1` for the FCC Query and both Sets. On a cold start the modem can appear as the generic Fibocom persona `SUBSYS_35001CF8` (no answer to the leading Query while locked, so the service uses Set, Set, then a confirming Query) or as the Dell persona `SUBSYS_59311028` (Query, Set, Set, confirming Query). Both paths succeed only when the confirming Query's second field becomes `1`. See [docs/MBIM.md](docs/MBIM.md) and [docs/RESEARCH.md](docs/RESEARCH.md); device identities, SIM data, host names and network identifiers have been removed from them.

## Verified setups

**FCC unlock:** Dell DW5931E / Fibocom FM350-GL (PCI `14C3:4D75`, Dell subsystem `1028:5931`), firmware `81600.0000.00.29.23.28_CT / F32`, Windows 10 IoT Enterprise LTSC 19044 x64, non-Dell X99 motherboard, China Telecom 5G SA. FCC authentication, automatic cold-start unlock and a 5G SA data connection were verified. That host later showed PCIe/WHEA and NVMe drop-outs under load, including after the FM350 was removed, so long-term hardware stability on that sample is not claimed.

**SMS app:** Intel 5G Solution 5000 / Fibocom FM350 firmware `81600.0000.00.29.22.06_GC`, Windows 11, an Israeli carrier: receiving and sending Hebrew (UCS2) SMS, multipart messages and notifications were verified.

Other motherboards, adapters, firmware versions, carriers and OEM variants are unverified. See [CHANGELOG.md](CHANGELOG.md) for details.

## Safety, license and credits

Use only on hardware you own or are authorized to maintain, and follow your local radio regulations, carrier rules and hardware certification requirements. MIT licensed, provided without warranty.

- Based on [AsterZ99/fm350-dell-fcc-unlock-windows](https://github.com/AsterZ99/fm350-dell-fcc-unlock-windows) (FCC unlock); this fork adds the SMS app.
- The ModemManager community's public work on the FM350-GL FCC challenge/response informed the protocol analysis.
- Fibocom, Dell and Intel/MediaTek retain the rights to their products and trademarks. This project is not affiliated with them.
