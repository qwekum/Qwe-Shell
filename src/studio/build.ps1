[CmdletBinding()]
param(
    # The native solution defines Release configurations only.
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Architecture = 'x64',
    [ValidatePattern('^v[0-9]{3}$')]
    [string]$PlatformToolset = 'v145',
    [switch]$SkipInstaller,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$studioRoot = Join-Path $repoRoot 'src\studio'
$artifactRoot = Join-Path $studioRoot 'artifacts'
$nativeRoot = Join-Path $artifactRoot 'native'
$studioPublish = Join-Path $artifactRoot 'publish\studio'
$toolHostPublish = Join-Path $artifactRoot 'publish\toolhost'
$packageSource = Join-Path $repoRoot 'src\bin'
$packageBin = Join-Path $repoRoot 'bin'
$packageStudio = Join-Path $packageBin 'studio'
$packageToolHost = Join-Path $packageStudio 'ToolHost'
$solution = Join-Path $repoRoot 'src\Shell.sln'
$languageProject = Join-Path $studioRoot 'native\ShellStudio.Language.vcxproj'
$previewProject = Join-Path $studioRoot 'native\ShellStudio.PreviewWorker.vcxproj'
$studioProject = Join-Path $studioRoot 'ShellStudio\ShellStudio.csproj'
$toolHostProject = Join-Path $studioRoot 'ShellStudio.ToolHost\ShellStudio.ToolHost.csproj'
$wixProject = Join-Path $repoRoot 'src\setup\wix\setup.wixproj'

function Assert-UnderRoot([string]$path, [string]$root) {
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the repository: $resolvedPath"
    }
}

function Invoke-NativeBuild([string]$target, [string]$intDir) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio vswhere.exe was not found.' }
    $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($vsPath)) { throw 'A Visual Studio C++ x64 installation was not found.' }
    $vsDevCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $vsDevCmd)) { throw "VsDevCmd.bat was not found at $vsDevCmd" }

    $arguments = @(
        $solution,
        '/m:1', "/t:$target",
        "/p:Configuration=$Configuration", "/p:Platform=$Architecture",
        # The doubled trailing slash survives cmd.exe's quoted argument
        # parsing and reaches MSBuild as the required single trailing slash.
        "/p:PlatformToolset=$PlatformToolset", "/p:OutDir=$nativeRoot\\", "/p:IntDir=$intDir\\",
        '/v:minimal'
    )
    $escaped = $arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
    # The Codex host can provide both PATH and Path environment keys. Clear
    # the legacy-cased entry before VsDevCmd so .NET Framework MSBuild does not
    # reject the duplicated key while launching CL.exe.
    $command = "set `"Path=`" && call `"$vsDevCmd`" -arch=$Architecture -host_arch=x64 >nul && msbuild $($escaped -join ' ')"
    & cmd.exe /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw "Native $target build failed with exit code $LASTEXITCODE." }
}

function Invoke-StudioNativeBuild([string]$project, [string]$name, [string]$outputName) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio vswhere.exe was not found.' }
    $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($vsPath)) { throw 'A Visual Studio C++ x64 installation was not found.' }
    $vsDevCmd = Join-Path $vsPath 'Common7\Tools\VsDevCmd.bat'
    $languageOut = Join-Path $nativeRoot $name
    $languageInt = Join-Path $nativeRoot "obj\$name"
    $languageArguments = @(
        $project, '/m:1', "/p:Configuration=$Configuration", '/p:Platform=x64',
        "/p:PlatformToolset=$PlatformToolset", "/p:OutDir=$languageOut\\", "/p:IntDir=$languageInt\\", '/v:minimal'
    )
    $escaped = $languageArguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
    $command = "set `"Path=`" && call `"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && msbuild $($escaped -join ' ')"
    # Keep build diagnostics visible without allowing MSBuild's output to
    # become function output.  The caller must receive exactly one path.
    & cmd.exe /d /s /c $command 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Studio $name build failed with exit code $LASTEXITCODE." }
    $languageDll = Join-Path $languageOut $outputName
    if (-not (Test-Path -LiteralPath $languageDll)) { throw "Studio native output was not produced: $languageDll" }
    return $languageDll
}

function Invoke-Dotnet([string[]]$arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

if ($Architecture -ne 'x64' -and -not $SkipInstaller) {
    throw 'The Studio publication is x64-only. Use -SkipInstaller for native x86 or ARM64 builds.'
}

Assert-UnderRoot $artifactRoot $repoRoot
Assert-UnderRoot $packageSource $repoRoot
Assert-UnderRoot $packageBin $repoRoot

if ($Clean) {
    foreach ($path in @($nativeRoot, $studioPublish, $toolHostPublish, $packageStudio)) {
        Assert-UnderRoot $path $repoRoot
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}

New-Item -ItemType Directory -Force -Path $nativeRoot, $studioPublish, $toolHostPublish, $packageBin | Out-Null

Push-Location $repoRoot
try {
    Invoke-NativeBuild 'dll' (Join-Path $nativeRoot 'obj\dll')
    Invoke-NativeBuild 'exe' (Join-Path $nativeRoot 'obj\exe')
    if ($Architecture -eq 'x64') {
        $languageDll = Invoke-StudioNativeBuild $languageProject 'studio-language' 'ShellStudio.Language.dll'
        $previewWorker = Invoke-StudioNativeBuild $previewProject 'preview-worker' 'ShellStudio.PreviewWorker.exe'
        Invoke-Dotnet @('restore', $studioProject, '-r', 'win-x64', '--disable-parallel', '-m:1', '-p:BuildInParallel=false')
        Invoke-Dotnet @('publish', $studioProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-m:1', '-p:BuildInParallel=false', '-o', $studioPublish, "/p:NativeLanguagePath=$languageDll", "/p:PreviewWorkerPath=$previewWorker")
        if (-not (Test-Path -LiteralPath (Join-Path $studioPublish 'ShellStudio.exe'))) { throw 'Self-contained Studio publication did not produce ShellStudio.exe.' }
        if (-not (Test-Path -LiteralPath (Join-Path $studioPublish 'ShellStudio.Language.dll'))) { throw 'Self-contained Studio publication did not include ShellStudio.Language.dll.' }
        if (-not (Test-Path -LiteralPath (Join-Path $studioPublish 'ShellStudio.PreviewWorker.exe'))) { throw 'Studio publication did not include ShellStudio.PreviewWorker.exe.' }

        Invoke-Dotnet @('restore', $toolHostProject, '-r', 'win-x64', '--disable-parallel', '-m:1', '-p:BuildInParallel=false')
        Invoke-Dotnet @('publish', $toolHostProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-m:1', '-p:BuildInParallel=false', '-o', $toolHostPublish)
        if (-not (Test-Path -LiteralPath (Join-Path $toolHostPublish 'ShellStudio.ToolHost.exe'))) { throw 'Self-contained ToolHost publication did not produce ShellStudio.ToolHost.exe.' }

        # The package directory is a task-owned generated target.  Assert the
        # exact target immediately before recursive removal and preserve the
        # two publications as separate subtrees so each remains independently
        # launchable and self-contained.
        Assert-UnderRoot $packageStudio $repoRoot
        if (Test-Path -LiteralPath $packageStudio) { Remove-Item -LiteralPath $packageStudio -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $packageStudio, $packageToolHost | Out-Null
        Copy-Item -Path (Join-Path $studioPublish '*') -Destination $packageStudio -Recurse -Force
        Copy-Item -Path (Join-Path $toolHostPublish '*') -Destination $packageToolHost -Recurse -Force
    }

    # The installer consumes a complete generated package root.  Keep the
    # checked-in source payload separate from generated native and Studio
    # outputs, then overlay the exact build products below.
    if (-not $SkipInstaller) {
        if (-not (Test-Path -LiteralPath $packageSource)) { throw "Package source payload was not found: $packageSource" }
        Copy-Item -Path (Join-Path $packageSource '*') -Destination $packageBin -Recurse -Force
        Invoke-NativeBuild 'ca' (Join-Path $nativeRoot 'obj\ca')
    }

    foreach ($file in @('shell.dll', 'shell.exe')) {
        $source = Join-Path $nativeRoot $file
        if (-not (Test-Path -LiteralPath $source)) { throw "Native output was not produced: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $packageBin $file) -Force
    }

    if (-not $SkipInstaller) {
        $caSource = Join-Path $nativeRoot 'ca.dll'
        if (-not (Test-Path -LiteralPath $caSource)) { throw "Custom action output was not produced: $caSource" }
        Copy-Item -LiteralPath $caSource -Destination (Join-Path $packageBin 'ca.dll') -Force

        Invoke-Dotnet @('restore', $wixProject, '--disable-parallel', '-m:1', '-p:BuildInParallel=false')
        $msi = Join-Path $packageBin "setup-$($Architecture.ToLower()).msi"
        Assert-UnderRoot $msi $repoRoot
        if (Test-Path -LiteralPath $msi) { Remove-Item -LiteralPath $msi -Force }
        Invoke-Dotnet @('build', $wixProject, '-c', $Configuration, '--no-restore', '-m:1', '-p:BuildInParallel=false', "/p:Platform=$Architecture")
        if (-not (Test-Path -LiteralPath $msi)) { throw "WiX build did not produce $msi" }
    }
}
finally {
    Pop-Location
}

Write-Host "Build completed: $Configuration $Architecture"
