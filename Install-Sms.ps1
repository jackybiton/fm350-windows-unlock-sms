# SPDX-License-Identifier: MIT
<#
  Installs the FM350 SMS app for the current user (no administrator rights needed).

  It builds src\Fm350Sms as a self-contained single-file exe, copies it to
  %LOCALAPPDATA%\FM350Sms\bin, starts it minimized to the tray at every sign-in (a per-user scheduled task "FM350 SMS"; the HKCU Run key
  only if the task cannot be created) and starts it now.
  Messages, contacts and settings are kept in %LOCALAPPDATA%\FM350Sms (sms.db, settings.json).

  Run it from a normal PowerShell window (Start menu). Inside a packaged (MSIX) app, for example the Claude desktop
  app, %LOCALAPPDATA% is redirected to a private copy and the app would use a second database; the installer
  detects that and stops before changing anything.

    .\Install-Sms.ps1                    # keep messages on the modem/SIM after saving them
    .\Install-Sms.ps1 -DeleteAfterSave   # remove each message from the modem once it is saved
                                         # (stored in settings.json; the app's menu can turn it off again)
#>
[CmdletBinding()]
param(
    [switch]$DeleteAfterSave
)

$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'FM350 SMS'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'FM350Sms'
$binDirectory = Join-Path $dataDirectory 'bin'
$exe = Join-Path $binDirectory 'Fm350Sms.exe'
$settingsFile = Join-Path $dataDirectory 'settings.json'
$contactsFolder = Join-Path $PSScriptRoot 'contacts'
$project = Join-Path $PSScriptRoot 'src\Fm350Sms\Fm350Sms.csproj'

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 10 or later is required to build the SMS app (https://dotnet.microsoft.com/download).'
}

$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) ('fm350-sms-' + [guid]::NewGuid().ToString('N'))
# Build output outside src\Fm350Sms\bin too: a copy of the app running from there would lock it.
$buildDirectory = $publishDirectory + '-build\'
try {
    & dotnet.exe publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false "-p:OutDir=$buildDirectory" -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    $published = Join-Path $publishDirectory 'Fm350Sms.exe'

    & $published --self-test | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'SMS app self-test failed; not installing.' }

    # Before stopping anything or writing anything: where would the app really keep its data?
    & $published --where | Out-Host
    if ($LASTEXITCODE -eq 3) {
        Write-Host ''
        Write-Host 'ההתקנה בוטלה: PowerShell הזה רץ מתוך אפליקציה ארוזה (למשל Claude), ולכן תיקיית הנתונים מנותבת לעותק פרטי.' -ForegroundColor Red
        Write-Host 'הרץ את Install-Sms.ps1 מחלון PowerShell רגיל (מתפריט התחל).' -ForegroundColor Red
        Write-Host 'Installation aborted: this PowerShell runs inside a packaged (MSIX) app such as Claude, so %LOCALAPPDATA% is redirected to a private copy.' -ForegroundColor Red
        Write-Host 'Run Install-Sms.ps1 from a normal PowerShell window (Start menu), not from inside Claude or another packaged app.' -ForegroundColor Red
        throw 'Data folder is redirected (MSIX AppData virtualization); nothing was changed.'
    }
    if ($LASTEXITCODE -ne 0) { throw "Data folder check failed with exit code $LASTEXITCODE; nothing was changed." }

    Get-Process -Name 'Fm350Sms' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Path $binDirectory -Force | Out-Null
    Copy-Item -LiteralPath $published -Destination $exe -Force
}
finally {
    Remove-Item -LiteralPath $publishDirectory, $buildDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

# History written by instances that ran inside the Claude desktop app lives in its private copy of the data folder.
$packaged = @(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Packages\Claude_*\LocalCache\Local\FM350Sms\sms.db') -ErrorAction SilentlyContinue)
foreach ($source in $packaged) {
    Write-Host ''
    Write-Host "נמצאה היסטוריית הודעות בעותק הפרטי של Claude: $($source.FullName)" -ForegroundColor Yellow
    Write-Host "Found message history in Claude's private copy of the data folder: $($source.FullName)" -ForegroundColor Yellow
    $answer = ''
    try {
        $answer = Read-Host 'למזג אותה למסד הנתונים האמיתי? / Merge it into the real database? [y/N]'
    }
    catch {
        $answer = ''
    }

    if ($answer -match '^\s*(y|yes|כ|כן)\s*$') {
        $mergeOutput = & $exe --merge-db $source.FullName | Out-String
        $mergeCode = $LASTEXITCODE
        Write-Host $mergeOutput
        if ($mergeCode -ne 0) {
            $backupLine = $mergeOutput -split "`r?`n" | Where-Object { $_ -like 'Backup:*' } | Select-Object -First 1
            if ($backupLine) {
                $backupPath = ($backupLine -replace '^Backup:\s*', '').Trim()
                Write-Warning "Merge failed (exit code $mergeCode); the history may not have been merged. A backup was made at $backupPath. The source file is untouched."
            }
            else {
                Write-Warning "Merge failed (exit code $mergeCode) before any change; the database was not changed. The source file is untouched."
            }
        }
    }
    else {
        Write-Host 'לא מוזג. / Not merged.'
    }
}

# -DeleteAfterSave is stored in settings.json (not on the logon command line), so turning it off in the app's menu sticks.
# An older install that put --delete-after-save in the Run value keeps deleting, unless settings.json already says otherwise.
$previousRun = (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
$carryDeleteAfterSave = (-not $DeleteAfterSave) -and ("$previousRun" -match '--delete-after-save')
$arguments = '--minimized'

# Start at logon through a per-user scheduled task: it runs right after sign-in, while HKCU\...\Run entries are
# started one by one and can be minutes late. No administrator rights are needed for a task of the current user.
# If the task cannot be created, fall back to the Run value.
$taskName = 'FM350 SMS'
$taskUser = "$env:USERDOMAIN\$env:USERNAME"
try {
    $taskAction = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $binDirectory
    $taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Limited
    # No time limit (the default stops a task after 3 days), never a second copy, and also on battery.
    $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal `
        -Settings $taskSettings -Description 'Starts the FM350 SMS app in the tray at sign-in.' -Force -ErrorAction Stop | Out-Null
    Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    Write-Host "Starts at sign-in: scheduled task '$taskName'."
}
catch {
    Write-Warning "Could not create the logon task ($($_.Exception.Message)); using the Run key instead, which Windows may start a few minutes after sign-in."
    Set-ItemProperty -Path $runKey -Name $runValue -Value ('"{0}" {1}' -f $exe, $arguments)
}

$hasContactsFolder = Test-Path -LiteralPath $contactsFolder -PathType Container
if ($hasContactsFolder -or $DeleteAfterSave -or $carryDeleteAfterSave) {
    $settings = [ordered]@{}
    if (Test-Path -LiteralPath $settingsFile) {
        try {
            $existing = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($p in $existing.PSObject.Properties) { $settings[$p.Name] = $p.Value }
        }
        catch {
            Write-Warning "settings.json is unreadable and will be rewritten: $($_.Exception.Message)"
        }
    }

    if ($hasContactsFolder) { $settings['ContactsFolder'] = (Resolve-Path -LiteralPath $contactsFolder).ProviderPath }
    # Without the switch an existing DeleteAfterSave value (set from the app's menu) is left as it is.
    if ($DeleteAfterSave) { $settings['DeleteAfterSave'] = $true }
    elseif ($carryDeleteAfterSave -and -not $settings.Contains('DeleteAfterSave')) { $settings['DeleteAfterSave'] = $true }
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    [IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
}

& $exe --register | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Registering notifications and the fm350sms: link failed (exit code $LASTEXITCODE)." }

# Start menu shortcut, so the window can be opened by searching "FM350 SMS" (no arguments = show the window).
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'FM350 SMS.lnk'
try {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $binDirectory
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'FM350 SMS'
    $shortcut.Save()
}
catch {
    Write-Warning "Could not create the Start menu shortcut: $($_.Exception.Message)"
}

Start-Process -FilePath $exe -ArgumentList $arguments

Write-Host ''
Write-Host 'FM350 SMS installed and running in the notification area (tray).' -ForegroundColor Green
Write-Host '  Open it from the Start menu ("FM350 SMS") or the tray icon.'
Write-Host "  Data:     $(Join-Path $dataDirectory 'sms.db')"
Write-Host "  Log:      $(Join-Path $dataDirectory 'fm350sms.log')"
Write-Host "  List what is stored on the modem:  & '$exe' --list | Out-String"
