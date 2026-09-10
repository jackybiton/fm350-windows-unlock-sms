# SPDX-License-Identifier: MIT
#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskName = 'FM350 FCC Unlock'
$serviceName = 'WWAN ModemAuthenticator'
$installDirectory = Join-Path $env:ProgramData 'FM350Unlock'
$installedScript = Join-Path $installDirectory 'Unlock-FM350.ps1'
$statePath = Join-Path $installDirectory 'install-state.json'
$adapterClassPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'

if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Unlock-FM350.ps1'))) {
    throw 'Unlock-FM350.ps1 is missing.'
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $statePath)) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $registryBackup = @()

    Get-ChildItem -LiteralPath $adapterClassPath -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^\d{4}$' } |
        ForEach-Object {
            $properties = Get-ItemProperty -LiteralPath $_.PSPath
            $identity = @(
                $properties.DriverDesc,
                $properties.ComponentId,
                $properties.MatchingDeviceId,
                $properties.ProviderName
            ) -join ' '

            if ($identity -match '(FM350|DW5931E|VEN_14C3&DEV_4D75|VID_8087&PID_0B62)') {
                $atHadValue = $properties.PSObject.Properties.Name -contains 'WWANFeatureAT'
                $suspendHadValue = $properties.PSObject.Properties.Name -contains '*SelectiveSuspend'
                $registryBackup += [pscustomobject]@{
                    Path = $_.Name
                    WWANFeatureATHadValue = $atHadValue
                    WWANFeatureATOldValue = if ($atHadValue) { $properties.WWANFeatureAT } else { $null }
                    SelectiveSuspendHadValue = $suspendHadValue
                    SelectiveSuspendOldValue = if ($suspendHadValue) { $properties.'*SelectiveSuspend' } else { $null }
                }
            }
        }

    [pscustomobject]@{
        Version = 2
        Service = [pscustomobject]@{
            Exists = ($null -ne $service)
            StartType = if ($service) { $service.StartType.ToString() } else { $null }
            WasRunning = if ($service) { $service.Status -eq 'Running' } else { $false }
        }
        Registry = $registryBackup
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
}

$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
foreach ($entry in @($state.Registry)) {
    $registryPath = $entry.Path -replace '^HKEY_LOCAL_MACHINE', 'HKLM:'
    if (Test-Path -LiteralPath $registryPath) {
        Set-ItemProperty -LiteralPath $registryPath -Name 'WWANFeatureAT' -Type DWord -Value 1
        Set-ItemProperty -LiteralPath $registryPath -Name '*SelectiveSuspend' -Type DWord -Value 0
    }
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Set-Service -Name $serviceName -StartupType Disabled
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unlock-FM350.ps1') -Destination $installedScript -Force

$powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $powerShell -Argument (
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $installedScript
)
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$watchdogTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $action `
    -Trigger @($startupTrigger, $watchdogTrigger) `
    -Settings $settings `
    -User 'SYSTEM' `
    -RunLevel Highest `
    -Force | Out-Null

Start-ScheduledTask -TaskName $taskName
Write-Output "Installed. The SYSTEM task '$taskName' is running."
Write-Output "Log: $(Join-Path $installDirectory 'unlock.log')"
Write-Output 'If the AT port was not previously enabled, restart Windows once.'
