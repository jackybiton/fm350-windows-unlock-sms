# SPDX-License-Identifier: MIT
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$SkipServicePublish
)

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
$pciEnumPath = 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI'
$pciPowerPropertyKey = 'Device Parameters\e5b3b5ac-9725-4f78-963f-03dfb1d828c7'
$wdfPowerKey = 'Device Parameters\WDF'

$publishDirectory = $null
if (-not $SkipServicePublish) {
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
}

try {
    $publishedExecutable = $null
    if (-not $SkipServicePublish) {
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
    }
    elseif (-not (Test-Path -LiteralPath $serviceExecutable)) {
        throw "The installed service executable is missing; cannot use -SkipServicePublish."
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
        Version = 5
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

    # A slot change creates a new network-class instance even though the same
    # modem and driver package are used. Discover those instances on every run
    # so AT diagnostics and suspend policy follow the physical device.
    $registryBackup = @($state.Registry)
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

            if ($identity -notmatch '(FM350|DW5931E|VEN_14C3&DEV_4D75|VID_8087&PID_0B62)') {
                return
            }

            $nativePath = $_.Name
            $alreadyBackedUp = @($registryBackup | Where-Object {
                $_.Path -eq $nativePath
            }).Count -gt 0
            if ($alreadyBackedUp) {
                return
            }

            $atHadValue = $properties.PSObject.Properties.Name -contains 'WWANFeatureAT'
            $suspendHadValue = $properties.PSObject.Properties.Name -contains '*SelectiveSuspend'
            $deviceSleepHadValue = $properties.PSObject.Properties.Name -contains '*DeviceSleepOnDisconnect'
            $registryBackup += [pscustomobject]@{
                Path = $nativePath
                WWANFeatureATHadValue = $atHadValue
                WWANFeatureATOldValue = if ($atHadValue) { $properties.WWANFeatureAT } else { $null }
                SelectiveSuspendHadValue = $suspendHadValue
                SelectiveSuspendOldValue = if ($suspendHadValue) { $properties.'*SelectiveSuspend' } else { $null }
                DeviceSleepOnDisconnectHadValue = $deviceSleepHadValue
                DeviceSleepOnDisconnectOldValue = if ($deviceSleepHadValue) { $properties.'*DeviceSleepOnDisconnect' } else { $null }
            }
            $stateChanged = $true
        }

    $state.Registry = $registryBackup
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

    $devicePowerBackup = @(
        if ($state.PSObject.Properties.Name -contains 'DevicePower') {
            @($state.DevicePower)
        }
    )
    Get-ChildItem -LiteralPath $pciEnumPath -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSChildName -match '^(VEN_14C3&DEV_4D75|VID_8087&PID_0B62)'
        } |
        ForEach-Object { Get-ChildItem -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
        ForEach-Object {
            $settings = @(
                [pscustomobject]@{
                    Path = Join-Path $_.PSPath $wdfPowerKey
                    Name = 'WdfDirectedPowerTransitionEnable'
                },
                [pscustomobject]@{
                    Path = Join-Path $_.PSPath $wdfPowerKey
                    Name = 'IdleInWorkingState'
                }
            )
            if ($_.PSParentPath -match 'VEN_14C3&DEV_4D75') {
                $settings += [pscustomobject]@{
                    Path = Join-Path $_.PSPath $pciPowerPropertyKey
                    Name = 'D3ColdSupported'
                }
            }

            foreach ($setting in $settings) {
                if (-not (Test-Path -LiteralPath $setting.Path)) {
                    continue
                }

                $nativePath = (Get-Item -LiteralPath $setting.Path).Name
                $alreadyBackedUp = @($devicePowerBackup | Where-Object {
                    $_.Path -eq $nativePath -and $_.Name -eq $setting.Name
                }).Count -gt 0
                if ($alreadyBackedUp) {
                    continue
                }

                $properties = Get-ItemProperty -LiteralPath $setting.Path
                $hadValue = $properties.PSObject.Properties.Name -contains $setting.Name
                $devicePowerBackup += [pscustomobject]@{
                    Path = $nativePath
                    Name = $setting.Name
                    HadValue = $hadValue
                    OldValue = if ($hadValue) { $properties.($setting.Name) } else { $null }
                }
                $stateChanged = $true
            }
        }

    if ($state.PSObject.Properties.Name -contains 'DevicePower') {
        $state.DevicePower = $devicePowerBackup
    }
    else {
        $state | Add-Member -NotePropertyName DevicePower -NotePropertyValue $devicePowerBackup
        $stateChanged = $true
    }

    if ($stateChanged) {
    $state.Version = 5
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

    foreach ($entry in @($state.DevicePower)) {
        $registryPath = $entry.Path -replace '^HKEY_LOCAL_MACHINE', 'HKLM:'
        if (Test-Path -LiteralPath $registryPath) {
            # Dell's INF enables PCI D3cold and WDF directed power transitions.
            # The verified X99 platform cannot reliably bring the modem back,
            # resulting in PCIe Surprise Down and CM_PROB_FAILED_START.
            Set-ItemProperty -LiteralPath $registryPath -Name $entry.Name -Type DWord -Value 0
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
    if ($existingUnlockService -and -not $SkipServicePublish) {
        Stop-Service -Name $unlockServiceName -Force -ErrorAction SilentlyContinue
        $existingUnlockService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }

    if (-not $SkipServicePublish) {
        New-Item -ItemType Directory -Path $serviceDirectory -Force | Out-Null
        Copy-Item -LiteralPath $publishedExecutable -Destination $serviceExecutable -Force
    }

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
        if ((Get-Service -Name $unlockServiceName).Status -ne 'Running') {
            Start-Service -Name $unlockServiceName
        }
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
    if ($publishDirectory -and (Test-Path -LiteralPath $publishDirectory)) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
