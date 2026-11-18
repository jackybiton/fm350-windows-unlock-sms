# SPDX-License-Identifier: MIT
<#
  Removes the FM350 SMS app for the current user: the logon entry, the Start menu shortcut, the notification and
  fm350sms: link registration, and the program in %LOCALAPPDATA%\FM350Sms\bin.
  Messages, contacts and settings (sms.db, settings.json, the old messages.txt and state.json) are kept unless -RemoveMessages is given.

  Run it from a normal PowerShell window (Start menu), not from inside a packaged (MSIX) app such as Claude:
  there %LOCALAPPDATA% is redirected to a private copy, and the real folder would not be touched.
#>
[CmdletBinding()]
param(
    [switch]$RemoveMessages
)

$ErrorActionPreference = 'Stop'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'FM350Sms'
$exe = Join-Path $dataDirectory 'bin\Fm350Sms.exe'

# Is %LOCALAPPDATA% redirected into a package's private copy? Ask the installed app (0.2.0 or later knows --where);
# otherwise write a probe file and see where it physically lands.
$redirected = $null
$version = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.FileVersion } else { $null }
if ($version -and [version]$version -ge [version]'0.2.0') {
    $where = & $exe --where | Out-String
    if ($LASTEXITCODE -eq 3) {
        # Exit code 3 alone means redirected; the printed path is only for the message.
        $redirected = ($where -split "`r?`n" | Where-Object { $_ -like 'redirected:*' } | Select-Object -First 1) -replace '^redirected:\s*', ''
        if ([string]::IsNullOrWhiteSpace($redirected)) { $redirected = 'yes' }
    }
    elseif ($LASTEXITCODE -ne 0) { throw "Data folder check failed with exit code $LASTEXITCODE; nothing was changed." }
}
else {
    $probeName = 'fm350sms-probe-' + [guid]::NewGuid().ToString('N')
    $probeDirectory = Join-Path $env:LOCALAPPDATA $probeName
    try {
        New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $probeDirectory 'probe.txt') -Value 'x'
        $landed = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA "Packages\*\LocalCache\Local\$probeName\probe.txt") -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($landed) { $redirected = Split-Path (Split-Path $landed.FullName) }
    }
    finally {
        Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($redirected) {
    Write-Host 'ההסרה בוטלה: PowerShell הזה רץ מתוך אפליקציה ארוזה (למשל Claude), ולכן תיקיית הנתונים מנותבת לעותק פרטי.' -ForegroundColor Red
    Write-Host 'הרץ את Uninstall-Sms.ps1 מחלון PowerShell רגיל (מתפריט התחל).' -ForegroundColor Red
    Write-Host "Uninstall aborted: %LOCALAPPDATA% is redirected to $redirected (packaged app such as Claude)." -ForegroundColor Red
    Write-Host 'Run Uninstall-Sms.ps1 from a normal PowerShell window (Start menu).' -ForegroundColor Red
    throw 'Data folder is redirected (MSIX AppData virtualization); nothing was changed.'
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'FM350 SMS' -ErrorAction SilentlyContinue
Get-Process -Name 'Fm350Sms' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item -LiteralPath 'HKCU:\Software\Classes\AppUserModelId\FM350.Sms' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\Classes\fm350sms' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'FM350 SMS.lnk') -Force -ErrorAction SilentlyContinue

if ($RemoveMessages) {
    Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    # Only the program: sms.db, settings.json and the v0.1.0 files (messages.txt, state.json) stay as a backup.
    Remove-Item -LiteralPath (Split-Path $exe) -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'FM350 SMS removed.' -ForegroundColor Green
if (-not $RemoveMessages) { Write-Host "  Messages and contacts kept in $dataDirectory (remove them with -RemoveMessages)." }
