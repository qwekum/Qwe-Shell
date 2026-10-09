[CmdletBinding()]
param(
 [Parameter(Mandatory)][guid]$SandboxId,
 [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$RunName,
  [ValidateSet('capture','submenu','matrix','scroll')][string]$Mode='capture',
 [ValidateSet(96,144)][int]$ExpectedDpi=144
)
$ErrorActionPreference='Stop'
$cli=Join-Path $env:LOCALAPPDATA Microsoft/WindowsApps/wsb.exe
$evidence=Join-Path $PSScriptRoot evidence
$resultPath=Join-Path $evidence "$RunName/result.json"
if(Test-Path -LiteralPath $resultPath){throw 'Choose a new run name; existing evidence must not be overwritten.'}
function Invoke-Guest([string]$Action){
 $command="powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\QweShellInput\guest.ps1 $Action -RunName $RunName -Mode $Mode"
 $raw=& $cli exec --id $SandboxId.ToString() --command $command --run-as ExistingLogin --raw
 if($LASTEXITCODE -ne 0){throw "Sandbox CLI failed for $Action"}
 $status=$raw | ConvertFrom-Json
 if($status.ExitCode -ne 0){throw "Guest $Action failed with exit code $($status.ExitCode). Inspect $RunName-$Action.json."}
}
Invoke-Guest Dismiss
Invoke-Guest Harness
Start-Sleep -Seconds 2
Invoke-Guest Menu
if($Mode -in @('submenu','matrix','scroll')){Invoke-Guest Submenu}
Invoke-Guest Screenshot
$deadline=[DateTime]::UtcNow.AddSeconds(45)
while(-not (Test-Path -LiteralPath $resultPath)){
 if([DateTime]::UtcNow -gt $deadline){throw 'Harness result timed out.'}
 Start-Sleep -Milliseconds 300
}
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if(-not $result.success -or -not $result.nativeAppearance -or $result.appearanceDpi -ne $ExpectedDpi -or -not $result.verifiedHitEntryId){
 $result | ConvertTo-Json -Depth 5
 throw 'Native-renderer acceptance failed.'
}
if($Mode -in @('submenu','matrix','scroll') -and ($result.nativeSubmenus -lt 1 -or -not $result.verifiedSubmenuHitEntryId)){throw 'Submenu image or verified child hit target missing.'}
$result | Select-Object success,mode,appearanceDpi,visibleRows,nativeSubmenus,verifiedHitEntryId,verifiedSubmenuHitEntryId
