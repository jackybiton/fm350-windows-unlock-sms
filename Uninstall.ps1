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

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json

    foreach ($entry in @($state.Registry)) {
        $registryPath = $entry.Path -replace '^HKEY_LOCAL_MACHINE', 'HKLM:'
        if (-not (Test-Path -LiteralPath $registryPath)) {
            continue
        }

        if ($entry.HadValue) {
            Set-ItemProperty -LiteralPath $registryPath -Name 'WWANFeatureAT' -Type DWord -Value $entry.OldValue
        }
        else {
            Remove-ItemProperty -LiteralPath $registryPath -Name 'WWANFeatureAT' -ErrorAction SilentlyContinue
        }
    }

    if ($state.Service.Exists) {
        $startupType = switch ($state.Service.StartType) {
            'Automatic' { 'Automatic' }
            'Manual' { 'Manual' }
            'Disabled' { 'Disabled' }
            default { 'Manual' }
        }
        Set-Service -Name $serviceName -StartupType $startupType -ErrorAction SilentlyContinue
        if ($state.Service.WasRunning -and $startupType -ne 'Disabled') {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
    }
}

Remove-Item -LiteralPath $installedScript -Force -ErrorAction SilentlyContinue
Write-Output 'FM350 auto-unlock was removed and recorded system settings were restored.'
Write-Output "Logs and the rollback record remain in $installDirectory for audit purposes."
