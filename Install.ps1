# SPDX-License-Identifier: MIT
#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskName = 'FM350 FCC Unlock'
$serviceName = 'WWAN ModemAuthenticator'
$unlockServiceName = 'FM350UnlockService'
$installDirectory = Join-Path $env:ProgramData 'FM350Unlock'
$installedScript = Join-Path $installDirectory 'Unlock-FM350.ps1'
$serviceDirectory = Join-Path $installDirectory 'Service'
$serviceExecutable = Join-Path $serviceDirectory 'Fm350UnlockService.exe'
$serviceProject = Join-Path $PSScriptRoot 'src\Fm350Unlock.Service\Fm350Unlock.Service.csproj'
$statePath = Join-Path $installDirectory 'install-state.json'
$adapterClassPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'

if (-not (Test-Path -LiteralPath $serviceProject)) {
    throw 'The .NET service project is missing.'
}
if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 10 or later is required to build the service.'
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('fm350-service-' + [guid]::NewGuid().ToString('N'))))
if (-not $publishDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to use a publish directory outside the system temporary directory.'
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

try {
    & dotnet.exe publish $serviceProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishedExecutable = Join-Path $publishDirectory 'Fm350UnlockService.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable)) {
        throw 'The published service executable was not produced.'
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
                $deviceSleepHadValue = $properties.PSObject.Properties.Name -contains '*DeviceSleepOnDisconnect'
                $registryBackup += [pscustomobject]@{
                    Path = $_.Name
                    WWANFeatureATHadValue = $atHadValue
                    WWANFeatureATOldValue = if ($atHadValue) { $properties.WWANFeatureAT } else { $null }
                    SelectiveSuspendHadValue = $suspendHadValue
                    SelectiveSuspendOldValue = if ($suspendHadValue) { $properties.'*SelectiveSuspend' } else { $null }
                    DeviceSleepOnDisconnectHadValue = $deviceSleepHadValue
                    DeviceSleepOnDisconnectOldValue = if ($deviceSleepHadValue) { $properties.'*DeviceSleepOnDisconnect' } else { $null }
                }
            }
        }

    [pscustomobject]@{
        Version = 4
        Service = [pscustomobject]@{
            Exists = ($null -ne $service)
            StartType = if ($service) { $service.StartType.ToString() } else { $null }
            WasRunning = if ($service) { $service.Status -eq 'Running' } else { $false }
        }
        Registry = $registryBackup
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
    }

    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $stateChanged = $false
    foreach ($entry in @($state.Registry)) {
    if ($entry.PSObject.Properties.Name -notcontains 'DeviceSleepOnDisconnectHadValue') {
        $registryPath = $entry.Path -replace '^HKEY_LOCAL_MACHINE', 'HKLM:'
        $properties = if (Test-Path -LiteralPath $registryPath) {
            Get-ItemProperty -LiteralPath $registryPath
        }
        else {
            $null
        }
        $hadValue = $null -ne $properties -and
            $properties.PSObject.Properties.Name -contains '*DeviceSleepOnDisconnect'
        $entry | Add-Member -NotePropertyName DeviceSleepOnDisconnectHadValue -NotePropertyValue $hadValue
        $entry | Add-Member -NotePropertyName DeviceSleepOnDisconnectOldValue -NotePropertyValue $(
            if ($hadValue) { $properties.'*DeviceSleepOnDisconnect' } else { $null }
        )
        $stateChanged = $true
    }
    }
    if ($stateChanged) {
    $state.Version = 4
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
    }

    foreach ($entry in @($state.Registry)) {
    $registryPath = $entry.Path -replace '^HKEY_LOCAL_MACHINE', 'HKLM:'
    if (Test-Path -LiteralPath $registryPath) {
        # Keep the PCIe COM AT diagnostic endpoint available for optional
        # band/cell diagnostics. Automatic FCC authentication remains MBIM-only.
        Set-ItemProperty -LiteralPath $registryPath -Name 'WWANFeatureAT' -Type DWord -Value 1
        Set-ItemProperty -LiteralPath $registryPath -Name '*SelectiveSuspend' -Type DWord -Value 0
        Set-ItemProperty -LiteralPath $registryPath -Name '*DeviceSleepOnDisconnect' -Type DWord -Value 0
    }
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Set-Service -Name $serviceName -StartupType Disabled
    }

    # Remove the AT-based fallback installed by versions before 0.4. The
    # production service must be the only component performing FCC unlocks.
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installedScript -Force -ErrorAction SilentlyContinue

    $existingUnlockService = Get-Service -Name $unlockServiceName -ErrorAction SilentlyContinue
    if ($existingUnlockService) {
        Stop-Service -Name $unlockServiceName -Force -ErrorAction SilentlyContinue
        $existingUnlockService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }

    New-Item -ItemType Directory -Path $serviceDirectory -Force | Out-Null
    Copy-Item -LiteralPath $publishedExecutable -Destination $serviceExecutable -Force

    if (-not $existingUnlockService) {
        New-Service `
            -Name $unlockServiceName `
            -BinaryPathName ('"{0}"' -f $serviceExecutable) `
            -DisplayName 'FM350 FCC Unlock Service' `
            -Description 'Unlocks Dell DW5931E / Fibocom FM350 FCC radio state without Dell SMBIOS.' `
            -StartupType Automatic | Out-Null
    }
    else {
        Set-Service -Name $unlockServiceName -StartupType Automatic
    }

    & sc.exe failure $unlockServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to configure service recovery.'
    }

    try {
        Start-Service -Name $unlockServiceName
        $installedService = Get-Service -Name $unlockServiceName
        $installedService.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    }
    catch {
        throw
    }

    Write-Output "Installed. The Windows service '$unlockServiceName' is running."
    Write-Output "Service log: $(Join-Path $installDirectory 'service.log')"
    Write-Output 'Unlock transport: MBIM device-service command 1; COM AT remains enabled for diagnostics.'
    Write-Output 'Restart Windows once to validate early device-arrival handling.'
}
finally {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
