[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstalledDirectory,
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

# Run the exact installed worker/DLL pair inside a disposable guest. This
# exercises native composition without applying a configuration to Explorer.
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This test requires the Windows Sandbox guest account.' }
$install = (Resolve-Path -LiteralPath $InstalledDirectory).Path
$fixtures = (Resolve-Path -LiteralPath $FixtureDirectory).Path
$worker = Join-Path $install 'Studio\ShellStudio.PreviewWorker.exe'
$language = Join-Path $install 'Studio\ShellStudio.Language.dll'
foreach ($file in @($worker, $language)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Installed preview binary missing: $file" }
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$receipt = Join-Path $EvidenceDirectory 'installed-preview.json'
if (Test-Path -LiteralPath $receipt) { throw 'Choose a new evidence directory; an existing receipt must not be overwritten.' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PreviewGuestWindows {
    private delegate bool Enumerate(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Enumerate callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    public static int Visible(int process, bool requireTransparent) {
        int count = 0;
        bool invalidStyle = false;
        EnumWindows((window, parameter) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process && IsWindowVisible(window)) {
                if (requireTransparent && (GetWindowLongPtr(window, -20).ToInt64() & 0x20) == 0)
                    invalidStyle = true;
                count++;
            }
            return true;
        }, IntPtr.Zero);
        if (invalidStyle) throw new InvalidOperationException("Native preview intercepts host mouse input.");
        return count;
    }
}
'@

function Read-Exact([IO.Stream]$Stream, [int]$Length) {
    $buffer = New-Object byte[] $Length
    $offset = 0
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($offset -lt $Length) {
        $remaining = [int][Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $read = $Stream.ReadAsync($buffer, $offset, $Length - $offset)
        if (-not $read.Wait($remaining)) { throw 'Native worker response timed out.' }
        if ($read.Result -eq 0) { throw 'Native worker returned a truncated frame.' }
        $offset += $read.Result
    }
    return ,$buffer
}

function Invoke-Preview([string]$Name, [string]$Operation, [hashtable]$Payload) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $worker
    $start.Arguments = '--language "' + $language + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $started = $false
    $inputEncoding = [Console]::InputEncoding
    try {
        # Windows PowerShell's .NET Framework StreamWriter inherits this
        # encoding and AutoFlush can write its BOM before the binary prefix.
        [Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
        if (-not $process.Start()) { throw 'Native preview worker did not start.' }
        $started = $true
        $ownerStartedUtc = $process.StartTime.ToUniversalTime().ToString('o')
        $request = @{ version=1; id=$Name; revision='installed-package'; operation=$Operation; payload=$Payload }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 24 -Compress))
        $prefix = [BitConverter]::GetBytes([int]$bytes.Length)
        $process.StandardInput.BaseStream.Write($prefix, 0, 4)
        $process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $process.StandardInput.BaseStream.Flush()
        $prefix = Read-Exact $process.StandardOutput.BaseStream 4
        $length = [BitConverter]::ToInt32($prefix, 0)
        if ($length -le 0 -or $length -gt 16777216) { throw 'Worker response exceeds the frame contract.' }
        $responseBytes = Read-Exact $process.StandardOutput.BaseStream $length
        [IO.File]::WriteAllBytes((Join-Path $EvidenceDirectory ($Name + '.json')), $responseBytes)
        $response = [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
        if ($response.id -ne $Name -or $response.revision -ne 'installed-package' -or $response.operation -ne $Operation) {
            throw 'Worker response identity mismatch.'
        }
        if ($response.status -ne 'ok' -or -not $response.result.available) {
            throw ('Native preview unavailable: ' + ($response.diagnostics | ConvertTo-Json -Compress))
        }
        $frame = $response.result.frame
        $pixels = [Convert]::FromBase64String($frame.pixels)
        if ($frame.format -ne 'Pbgra32' -or $pixels.Length -ne [long]$frame.width * $frame.height * 4) {
            throw 'Native preview pixel contract failed.'
        }
        $windows = 0
        if ($Operation -eq 'compose') {
            $windows = [PreviewGuestWindows]::Visible($process.Id, $true)
            if ($process.HasExited -or $windows -lt 1) { throw 'Composed worker has no owned visible window.' }
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(10000)) { throw 'Native worker did not exit after its owner pipe closed.' }
        if ($process.ExitCode -ne 0 -or [PreviewGuestWindows]::Visible($process.Id, $false) -ne 0) {
            throw 'Native worker or its window survived owner shutdown.'
        }
        return @{ Name=$Name; Operation=$Operation; Passed=$true; Width=$frame.width; Height=$frame.height; Dpi=$frame.dpi; OwnedWindows=$windows; ProcessId=$process.Id; OwnerStartedUtc=$ownerStartedUtc; OwnerExitClosedWindows=$true }
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
        $process.Dispose()
        [Console]::InputEncoding = $inputEncoding
    }
}

$results = New-Object Collections.Generic.List[object]
$record = [ordered]@{
    StartedUtc=[DateTime]::UtcNow.ToString('o'); Passed=$false
    OS=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture)
    Binaries=@($worker,$language | ForEach-Object { @{ Name=[IO.Path]::GetFileName($_); SHA256=(Get-FileHash -LiteralPath $_).Hash } })
    Cases=$results
}
try {
    foreach ($theme in @('catppuccin-mocha-mauve','catppuccin-latte-mauve')) {
        $source = [IO.File]::ReadAllText((Join-Path $fixtures ($theme + '.nss')))
        foreach ($dpi in @(96,144,192)) {
            foreach ($mode in @('standalone','captured')) {
                $rows = @(@{id='open';title='Open'}, @{id='checked';title='Checked';checked=$true}, @{id='disabled';title='Disabled';disabled=$true})
                $payload = @{ source=$source; context=@{dpi=$dpi;themeMode=1}; mode=$mode; sampleMenu=$rows; capture=@{original=$rows}; viewportHeight=400; trace=$true }
                $results.Add((Invoke-Preview "$theme-$dpi-$mode" 'render' $payload))
            }
        }
        $payload = @{ source=$source; context=@{dpi=96;themeMode=1}; mode='standalone'; sampleMenu=@(@{title='Composed preview'}); viewportHeight=300 }
        $results.Add((Invoke-Preview "$theme-composed" 'compose' $payload))
    }
    $record.Passed = $true
}
catch { $record.Error = $_.Exception.ToString() }
finally {
    $record.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $receipt
}
if (-not $record.Passed) { throw $record.Error }
$record | Select-Object Passed,StartedUtc,FinishedUtc
