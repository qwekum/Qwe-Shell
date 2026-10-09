[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstalledDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

# This probe changes registration. Run only in a disposable Windows Sandbox.
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'This registration probe is restricted to the Windows Sandbox guest account.'
}
$registrar = Join-Path (Resolve-Path -LiteralPath $InstalledDirectory).Path 'shell.exe'
if (-not (Test-Path -LiteralPath $registrar -PathType Leaf)) { throw 'Installed registrar missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $InstalledDirectory 'shell.dll') -PathType Leaf)) {
    throw 'Installed DLL missing; cannot test successful registration.'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$fixture = Join-Path $env:TEMP ('shell-registrar-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
Copy-Item -LiteralPath $registrar -Destination (Join-Path $fixture 'shell.exe')
$results = @()
foreach ($case in @(
    @{ Name='registered'; Exe=$registrar; Expected=0 },
    @{ Name='missing-dll'; Exe=(Join-Path $fixture 'shell.exe'); Expected=1 }
)) {
    $process = Start-Process -FilePath $case.Exe -ArgumentList '-r -s -t' -WindowStyle Hidden -PassThru
    $started = $process.StartTime.ToUniversalTime().ToString('o')
    $exited = $process.WaitForExit(30000)
    $code = if ($exited) { $process.ExitCode } else { $null }
    $results += [pscustomobject]@{
        Case=$case.Name; Expected=$case.Expected; ExitCode=$code
        Passed=($exited -and $code -eq $case.Expected)
        ProcessId=$process.Id; StartedUtc=$started; Exited=$exited
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'registrar-exit-codes.json')
    if (-not $exited) { throw 'Registrar timed out; retain the guest and fixture for diagnosis.' }
}
if (@($results | Where-Object { -not $_.Passed }).Count) { throw 'Registrar exit-code regression.' }
$results
