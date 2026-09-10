# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [ValidateRange(1, 100)] [int]$MaxAttempts = 30,
    [ValidateRange(1, 30)] [int]$RetrySeconds = 4,
    [string]$LogPath = (Join-Path $env:ProgramData 'FM350Unlock\unlock.log'),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$vendorSeed = 'DW5931EFCCLOCK'

if (-not ('Fm350NativeFile' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class Fm350NativeFile
{
    private const uint GenericReadWrite = 0xC0000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    public static SafeFileHandle OpenPort(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            GenericReadWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return handle;
    }
}
'@
}

function Write-UnlockLog {
    param([Parameter(Mandatory)] [string]$Message)

    $logDirectory = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }
    Add-Content -LiteralPath $LogPath -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message)
}

function Get-Fm350AtPort {
    $device = Get-PnpDevice -Class Ports -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Status -eq 'OK' -and
            $_.FriendlyName -match '(FM350|DW5931E).*COM AT.*\((COM\d+)\)'
        } |
        Select-Object -First 1

    if ($null -eq $device) {
        return $null
    }

    $null = $device.FriendlyName -match '\((COM\d+)\)'
    return $Matches[1]
}

function Test-Fm350HardwareRadioOn {
    try {
        $state = (& netsh mbn show radio interface=* 2>&1 | Out-String)
        # Windows PowerShell 5.1 reads UTF-8 scripts without a BOM as ANSI, so
        # construct localized labels from code points instead of source literals.
        $hardwareRadioStateCn = -join ([char[]](30828, 20214, 26080, 32447, 30005, 29366, 24577))
        $radioOnCn = -join ([char[]](25171, 24320))
        $localizedPattern = [regex]::Escape($hardwareRadioStateCn) + '\s*:\s*' + [regex]::Escape($radioOnCn)
        return ($state -match 'Hardware radio state\s*:\s*On') -or ($state -match $localizedPattern)
    }
    catch {
        return $false
    }
}

function Invoke-Fm350At {
    param(
        [Parameter(Mandatory)] [string]$Port,
        [Parameter(Mandatory)] [string]$Command
    )

    # System.IO.Ports.SerialPort cannot open this UMDF port on some Dell drivers.
    # Open the device with CreateFile and wrap the native handle instead.
    $handle = [Fm350NativeFile]::OpenPort("\\.\$Port")
    $stream = [IO.FileStream]::new($handle, [IO.FileAccess]::ReadWrite, 4096, $false)

    try {
        $commandBytes = [Text.Encoding]::ASCII.GetBytes($Command + "`r")
        $stream.Write($commandBytes, 0, $commandBytes.Length)
        $stream.Flush()
        Start-Sleep -Milliseconds 800

        $buffer = New-Object byte[] 8192
        $asyncRead = $stream.BeginRead($buffer, 0, $buffer.Length, $null, $null)
        if (-not $asyncRead.AsyncWaitHandle.WaitOne(5000)) {
            throw "Timed out waiting for $Command"
        }

        $count = $stream.EndRead($asyncRead)
        return [Text.Encoding]::ASCII.GetString($buffer, 0, $count)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DellUnlockResponse {
    param([Parameter(Mandatory)] [string]$ChallengeHex)

    if ($ChallengeHex -notmatch '^[0-9A-Fa-f]{8}$') {
        throw 'The FCC challenge must contain exactly eight hexadecimal digits.'
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $seedDigest = $sha256.ComputeHash([Text.Encoding]::ASCII.GetBytes($vendorSeed))
        $combined = New-Object byte[] 8

        for ($index = 0; $index -lt 4; $index++) {
            $combined[$index] = [Convert]::ToByte($ChallengeHex.Substring($index * 2, 2), 16)
            $combined[$index + 4] = $seedDigest[$index]
        }

        $responseDigest = $sha256.ComputeHash($combined)
        $responseHex = (($responseDigest[0..3] | ForEach-Object ToString x2) -join '')
        return [Convert]::ToUInt32($responseHex, 16)
    }
    finally {
        $sha256.Dispose()
    }
}

if ($SelfTest) {
    $vendorHash = (Get-DellUnlockResponse -ChallengeHex 'c53900f7')
    if ($vendorHash -ne 771512217) {
        throw "Algorithm self-test failed: expected 771512217, got $vendorHash"
    }
    Write-Output 'FM350 Dell FCC unlock algorithm self-test passed.'
    exit 0
}

if (Test-Fm350HardwareRadioOn) {
    exit 0
}

Write-UnlockLog 'Hardware radio is off; unlock attempt started.'

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    try {
        $port = Get-Fm350AtPort
        if ([string]::IsNullOrWhiteSpace($port)) {
            throw 'FM350 AT port is not ready.'
        }

        $challengeReply = Invoke-Fm350At -Port $port -Command 'AT+GTFCCLOCKGEN'
        if ($challengeReply -notmatch '0x([0-9A-Fa-f]{8})') {
            throw 'The modem did not return an FCC challenge.'
        }

        $challenge = $Matches[1].ToLowerInvariant()
        $response = Get-DellUnlockResponse -ChallengeHex $challenge
        $verifyReply = Invoke-Fm350At -Port $port -Command ("AT+GTFCCLOCKVER={0}" -f $response)

        if ($verifyReply -match '\+GTFCCLOCKVER:\s*1') {
            Write-UnlockLog "FCC unlock succeeded on $port (attempt $attempt)."
            exit 0
        }

        Start-Sleep -Seconds 1
        if (Test-Fm350HardwareRadioOn) {
            Write-UnlockLog "FCC unlock succeeded on $port (radio is on, attempt $attempt)."
            exit 0
        }

        throw ('The modem rejected the FCC response: ' + ($verifyReply -replace '[\r\n]+', ' '))
    }
    catch {
        Write-UnlockLog "Attempt $attempt failed: $($_.Exception.Message)"
        Start-Sleep -Seconds $RetrySeconds
    }
}

Write-UnlockLog "FCC unlock failed after $MaxAttempts attempts."
exit 1
