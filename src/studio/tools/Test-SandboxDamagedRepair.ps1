[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstalledDirectory,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)

# This test deliberately damages an installed package and the machine-wide
# registration. Run it only in a disposable Windows Sandbox guest. The
# guest-account guard is intentionally before any installation or registry
# mutation; the evidence writer below still records failures reached in the
# guest even when a later step fails.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$startedUtc = [DateTime]::UtcNow.ToString('o')
$evidencePath = $null
$repairProcess = $null
$registrationProcess = $null
$fixtureDirectory = $null
$identityName = $null
try { $identityName = [Security.Principal.WindowsIdentity]::GetCurrent().Name } catch { }
$result = [ordered]@{
    Test = 'SandboxDamagedRepair'
    StartedUtc = $startedUtc
    FinishedUtc = $null
    Computer = $env:COMPUTERNAME
    User = $env:USERNAME
    UserIdentity = $identityName
    Success = $false
    InstalledDirectory = $InstalledDirectory
    PackagePath = $PackagePath
    EvidenceDirectory = $EvidenceDirectory
    Package = $null
    PackageAfter = $null
    BinariesBefore = @()
    BinariesMoved = @()
    BinariesAfter = @()
    PreservedFilesBefore = @()
    PreservedFilesAfter = @()
    Registration = [ordered]@{}
    Repair = $null
    RegistrationRepair = $null
    FixtureDirectory = $null
    Error = $null
}

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) {
        throw "Path must be absolute: $Path"
    }
    return [IO.Path]::GetFullPath($Path)
}

function Test-UnderRoot {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Target
    )

    $rootFull = (Get-FullPath $Root).TrimEnd('\')
    $targetFull = Get-FullPath $Target
    return $targetFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
        $targetFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-UnderRoot {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Description
    )

    if (-not (Test-UnderRoot -Root $Root -Target $Target)) {
        throw "$Description is outside the installed directory: $Target"
    }
}

function Get-FileReceipt {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if (-not $item.PSIsContainer -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) {
        $hash = Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256
        return [ordered]@{
            Path = $item.FullName
            Bytes = [int64]$item.Length
            SHA256 = $hash.Hash.ToUpperInvariant()
        }
    }
    throw "Expected a regular non-reparse file: $Path"
}

function Assert-ReceiptMatch {
    param(
        [Parameter(Mandatory)][object]$Expected,
        [Parameter(Mandatory)][object]$Actual,
        [Parameter(Mandatory)][string]$Description
    )

    if ($null -eq $Actual -or $Actual.SHA256 -ne $Expected.SHA256 -or
        [int64]$Actual.Bytes -ne [int64]$Expected.Bytes) {
        $actualText = if ($null -eq $Actual) { 'missing' } else { "$($Actual.SHA256)/$($Actual.Bytes)" }
        throw "$Description changed; expected $($Expected.SHA256)/$($Expected.Bytes), observed $actualText."
    }
}

function Write-TextFixture {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$Content
    )

    $target = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    Assert-UnderRoot -Root $Root -Target $target -Description "Fixture path"
    $parent = [IO.Path]::GetDirectoryName($target)
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    if (Test-Path -LiteralPath $target -PathType Leaf) {
        $existing = Get-Content -LiteralPath $target -Raw -ErrorAction Stop
        if ($existing.Contains('QWE_SHELL_DAMAGED_REPAIR_FIXTURE')) {
            return $target
        }
        # Keep the existing user content and add a deterministic marker. This
        # avoids silently replacing an earlier guest fixture or user edit.
        [IO.File]::AppendAllText($target, "`r`n// QWE_SHELL_DAMAGED_REPAIR_FIXTURE`r`n$Content`r`n", [Text.UTF8Encoding]::new($false))
    }
    else {
        [IO.File]::WriteAllText($target, "// QWE_SHELL_DAMAGED_REPAIR_FIXTURE`r`n$Content`r`n", [Text.UTF8Encoding]::new($false))
    }
    return $target
}

function Get-ProductRegistration {
    param(
        [Parameter(Mandatory)][string]$ExpectedDllPath,
        [Parameter(Mandatory)][string]$Clsid
    )

    $relativePath = "SOFTWARE\Classes\CLSID\$Clsid"
    $base = $null
    $key = $null
    $inproc = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $key = $base.OpenSubKey($relativePath, $false)
        if ($null -eq $key) {
            return [ordered]@{ Exists = $false; Clsid = $Clsid; Key = $relativePath }
        }
        $inproc = $key.OpenSubKey('InprocServer32', $false)
        $description = [string]$key.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $module = if ($null -eq $inproc) { $null } else {
            [string]$inproc.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
        $moduleMatches = $false
        if (-not [string]::IsNullOrWhiteSpace($module)) {
            try {
                $moduleMatches = ([IO.Path]::GetFullPath($module)).Equals(
                    (Get-FullPath $ExpectedDllPath), [StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $moduleMatches = $false
            }
        }
        return [ordered]@{
            Exists = $true
            Clsid = $Clsid
            Key = $relativePath
            Description = $description
            InprocServer32 = $module
            ModuleMatchesInstalledDll = $moduleMatches
        }
    }
    finally {
        if ($null -ne $inproc) { $inproc.Dispose() }
        if ($null -ne $key) { $key.Dispose() }
        if ($null -ne $base) { $base.Dispose() }
    }
}

function Remove-ProductRegistration {
    param(
        [Parameter(Mandatory)][string]$ExpectedDllPath,
        [Parameter(Mandatory)][string]$Clsid
    )

    $before = Get-ProductRegistration -ExpectedDllPath $ExpectedDllPath -Clsid $Clsid
    if (-not $before.Exists) { throw "Expected product registration is missing: $Clsid" }
    if (-not $before.ModuleMatchesInstalledDll) {
        throw "Refusing to remove a CLSID whose InprocServer32 is not the installed product DLL: $Clsid"
    }

    $relativePath = "SOFTWARE\Classes\CLSID\$Clsid"
    $base = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $base.DeleteSubKeyTree($relativePath)
    }
    finally {
        if ($null -ne $base) { $base.Dispose() }
    }

    $after = Get-ProductRegistration -ExpectedDllPath $ExpectedDllPath -Clsid $Clsid
    if ($after.Exists) { throw "Product CLSID remained after the scoped removal: $Clsid" }
    return [ordered]@{ Before = $before; AfterRemoval = $after; Removed = $true }
}

function Start-BoundedHiddenProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds,
        [Parameter(Mandatory)][string]$Description
    )

    $quotedArguments = @($ArgumentList | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    $process = Start-Process -FilePath $FilePath -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru
    $null = $process.Handle
    $receipt = [ordered]@{
        Description = $Description
        FilePath = $FilePath
        Arguments = $ArgumentList
        ProcessId = $process.Id
        StartedUtc = $null
        Exited = $false
        TimedOut = $false
        ExitCode = $null
        FinishedUtc = $null
    }
    try {
        try { $receipt.StartedUtc = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
        $receipt.Exited = $process.WaitForExit($TimeoutMilliseconds)
        if ($receipt.Exited) {
            $receipt.ExitCode = $process.ExitCode
            $receipt.FinishedUtc = [DateTime]::UtcNow.ToString('o')
        }
        if (-not $receipt.Exited) {
            $receipt.TimedOut = $true
        }
        return $receipt
    }
    finally {
        $process.Dispose()
    }
}

try {
    if ($env:USERNAME -ne 'WDAGUtilityAccount' -or
        [string]::IsNullOrWhiteSpace($identityName) -or
        $identityName -notmatch '(?i)(^|\\)WDAGUtilityAccount$') {
        throw 'This damaged-repair test is restricted to the Windows Sandbox guest account.'
    }

    $evidencePath = Get-FullPath $EvidenceDirectory
    $installPath = Get-FullPath $InstalledDirectory
    $packageFullPath = Get-FullPath $PackagePath
    if (-not (Test-Path -LiteralPath $installPath -PathType Container)) {
        throw "Installed directory is missing: $installPath"
    }
    if (-not (Test-Path -LiteralPath $packageFullPath -PathType Leaf)) {
        throw "MSI package is missing: $packageFullPath"
    }
    New-Item -ItemType Directory -Force -Path $evidencePath | Out-Null

    $result.InstalledDirectory = $installPath
    $result.PackagePath = $packageFullPath
    $result.EvidenceDirectory = $evidencePath

    $packageReceipt = Get-FileReceipt $packageFullPath
    if ([IO.Path]::GetExtension($packageFullPath) -ine '.msi') {
        throw "PackagePath must point to an MSI: $packageFullPath"
    }
    $result.Package = $packageReceipt

    $shellExe = [IO.Path]::GetFullPath((Join-Path $installPath 'shell.exe'))
    $shellDll = [IO.Path]::GetFullPath((Join-Path $installPath 'shell.dll'))
    Assert-UnderRoot -Root $installPath -Target $shellExe -Description 'Registrar path'
    Assert-UnderRoot -Root $installPath -Target $shellDll -Description 'Product DLL path'
    foreach ($path in @($shellExe, $shellDll)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installed product file is missing: $path" }
    }

    # These are the two generated native files required by the packaged Studio
    # preview path. Keep the damage list explicit: never recurse or remove a
    # wildcard from the install directory.
    $damagedRelativePaths = @(
        'Studio\ShellStudio.PreviewWorker.exe',
        'Studio\ShellStudio.Language.dll'
    )
    $binaryReceipts = @()
    foreach ($relativePath in $damagedRelativePaths) {
        $target = [IO.Path]::GetFullPath((Join-Path $installPath $relativePath))
        Assert-UnderRoot -Root $installPath -Target $target -Description 'Damaged binary path'
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Installed preview file is missing: $target" }
        $binaryReceipts += Get-FileReceipt $target
    }
    $result.BinariesBefore = $binaryReceipts

    # Seed the same seven user-owned categories as the integration record. The
    # root and shipped import are appended to, so an existing guest edit stays
    # in the fixture; new paths are task-owned and deterministic.
    $fixtureContents = [ordered]@{
        'shell.nss' = "import 'imports/user-preservation.nss'"
        'imports\user-preservation.nss' = "item(title='Preservation fixture' cmd='notepad.exe')"
        'imports\studio.nss' = "// Studio-authored preservation fixture"
        'imports\theme.nss' = "// Modified shipped import must survive repair"
        'studio-assets\preserve.txt' = 'User asset sentinel'
        'templates\preserve.json' = '{"fixture":"user-template"}'
        'recovery\preserve.bak' = 'Original recovery content'
    }
    $preservedReceipts = @()
    foreach ($relativePath in $fixtureContents.Keys) {
        $target = Write-TextFixture -Root $installPath -RelativePath $relativePath -Content $fixtureContents[$relativePath]
        $preservedReceipts += Get-FileReceipt $target
    }
    $result.PreservedFilesBefore = $preservedReceipts

    $tempPath = Get-FullPath $env:TEMP
    $fixtureDirectory = [IO.Path]::GetFullPath((Join-Path $tempPath ('qwe-shell-damaged-repair-' + [guid]::NewGuid().ToString('N'))))
    if (-not (Test-UnderRoot -Root $tempPath -Target $fixtureDirectory)) {
        throw "Damage fixture unexpectedly resolves outside the guest temp directory: $fixtureDirectory"
    }
    if (Test-UnderRoot -Root $installPath -Target $fixtureDirectory) {
        throw "Damage fixture unexpectedly resolves inside the install directory: $fixtureDirectory"
    }
    New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
    $result.FixtureDirectory = $fixtureDirectory

    # Remove only the product's context-menu CLSID, after checking its server
    # points at this install. The icon-overlay and every unrelated registry key
    # remain untouched.
    $primaryClsid = '{BAE3934B-8A6A-4BFB-81BD-3FC599A1BAF1}'
    $result.Registration = Remove-ProductRegistration -ExpectedDllPath $shellDll -Clsid $primaryClsid

    foreach ($receipt in $binaryReceipts) {
        $target = $receipt.Path
        $relativePath = $target.Substring($installPath.TrimEnd('\').Length).TrimStart('\')
        $destination = [IO.Path]::GetFullPath((Join-Path $fixtureDirectory $relativePath))
        Assert-UnderRoot -Root $installPath -Target $target -Description 'Move source path'
        Assert-UnderRoot -Root $fixtureDirectory -Target $destination -Description 'Move destination path'
        if (Test-Path -LiteralPath $destination) { throw "Damage fixture target already exists: $destination" }
        $destinationParent = [IO.Path]::GetDirectoryName($destination)
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        Move-Item -LiteralPath $target -Destination $destination
        if (Test-Path -LiteralPath $target) { throw "Damaged binary was not moved: $target" }
        $movedReceipt = Get-FileReceipt $destination
        Assert-ReceiptMatch -Expected $receipt -Actual $movedReceipt -Description "Moved binary $relativePath"
        $result.BinariesMoved += [ordered]@{ Original = $receipt; Fixture = $movedReceipt }
    }

    $repairLog = [IO.Path]::GetFullPath((Join-Path $evidencePath 'damaged-repair-msi.log'))
    $repairArguments = @('/fvomus', $packageFullPath, '/qn', '/norestart', '/L*v', $repairLog)
    $repairProcess = Start-BoundedHiddenProcess -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') `
        -ArgumentList $repairArguments -TimeoutMilliseconds 300000 -Description 'MSI damaged-install repair'
    $result.Repair = $repairProcess
    if ($repairProcess.TimedOut) {
        throw 'MSI damaged-install repair exceeded 300 seconds; retain the guest and process evidence.'
    }
    if ($repairProcess.ExitCode -notin @(0, 3010)) {
        throw "MSI damaged-install repair failed with exit code $($repairProcess.ExitCode)."
    }

    $binaryAfter = @()
    foreach ($receipt in $binaryReceipts) {
        $after = Get-FileReceipt $receipt.Path
        Assert-ReceiptMatch -Expected $receipt -Actual $after -Description "Repaired binary $($receipt.Path)"
        $binaryAfter += $after
    }
    $result.BinariesAfter = $binaryAfter

    $packageAfter = Get-FileReceipt $packageFullPath
    Assert-ReceiptMatch -Expected $packageReceipt -Actual $packageAfter -Description 'MSI package'
    $result.PackageAfter = $packageAfter

    $registrationAfterRepair = Get-ProductRegistration -ExpectedDllPath $shellDll -Clsid $primaryClsid
    $result.Registration.AfterRepair = $registrationAfterRepair

    # Registration is an install custom action rather than an MSI Registry
    # table entry. Repair restores files; invoke the supported registrar after
    # repair so the test also proves a damaged product registration can be
    # recovered without touching the host. -t is intentionally omitted because
    # it changes Windows' modern-menu TreatAs mapping.
    $registrationProcess = Start-BoundedHiddenProcess -FilePath $shellExe `
        -ArgumentList @('-r', '-s') -TimeoutMilliseconds 30000 -Description 'Product registration recovery'
    $result.RegistrationRepair = $registrationProcess
    if ($registrationProcess.TimedOut) {
        throw 'Registration recovery exceeded 30 seconds; retain the guest and process evidence.'
    }
    if ($registrationProcess.ExitCode -ne 0) {
        throw "Registration recovery failed with exit code $($registrationProcess.ExitCode)."
    }
    $registrationRestored = Get-ProductRegistration -ExpectedDllPath $shellDll -Clsid $primaryClsid
    $result.Registration.AfterRecovery = $registrationRestored
    if (-not $registrationRestored.Exists -or -not $registrationRestored.ModuleMatchesInstalledDll) {
        throw 'Product CLSID was not restored to the repaired installed DLL.'
    }

    $preservedAfter = @()
    foreach ($receipt in $preservedReceipts) {
        $after = Get-FileReceipt $receipt.Path
        Assert-ReceiptMatch -Expected $receipt -Actual $after -Description "Preserved user file $($receipt.Path)"
        $preservedAfter += $after
    }
    $result.PreservedFilesAfter = $preservedAfter
    $result.Success = $true
}
catch {
    $result.Error = [ordered]@{
        Type = $_.Exception.GetType().FullName
        Message = $_.Exception.Message
        Detail = $_.Exception.ToString()
        ScriptStackTrace = $_.ScriptStackTrace
    }
}
finally {
    $result.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    if ($null -ne $evidencePath) {
        try {
            if (-not (Test-Path -LiteralPath $evidencePath -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $evidencePath | Out-Null
            }
            $evidenceFile = Join-Path $evidencePath 'DamagedRepair.json'
            $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidenceFile -Encoding UTF8
        }
        catch {
            [Console]::Error.WriteLine("Could not write damaged-repair evidence: $($_.Exception.Message)")
        }
    }
}

$result
if (-not $result.Success) { exit 1 }
