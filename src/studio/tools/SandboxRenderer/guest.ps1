param([ValidateSet('Prepare','Register','Menu','Screenshot','Harness','Submenu','Dismiss','MeasureDpi')][string]$Action,
 [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$RunName='capture',
 [ValidateSet('capture','submenu','matrix','scroll','startup')][string]$Mode='capture')
$ErrorActionPreference='Stop'
$manifest=Get-Content C:\QweShellInput\manifest.json -Raw | ConvertFrom-Json
if($env:COMPUTERNAME -eq $manifest.HostComputer -or $env:USERNAME -notin @('SYSTEM','WDAGUtilityAccount')){throw 'Guest identity guard failed.'}
$result=[ordered]@{Action=$Action;Computer=$env:COMPUTERNAME;User=$env:USERNAME;Success=$false}
if($Action -in @('Menu','Submenu','Dismiss')){
 Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class GuestMenuInput {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc,IntPtr data);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd,StringBuilder value,int max);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetMenuString(IntPtr menu,uint item,StringBuilder text,int max,uint flags);
 [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr hwnd,IntPtr menu,uint item,out Rect rect);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 public static IntPtr Popup() {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,d)=>{var s=new StringBuilder(80);GetClassName(h,s,80);if(IsWindowVisible(h)&&s.ToString()=="#32768"){found=h;return false;}return true;},IntPtr.Zero);
  return found;
 }
 public static Rect NamedRow(string name) {
  var hwnd=Popup(); if(hwnd==IntPtr.Zero)throw new Exception("No visible menu popup");
  var menu=SendMessage(hwnd,0x01E1,IntPtr.Zero,IntPtr.Zero);
  for(uint i=0;i<GetMenuItemCount(menu);i++) {var text=new StringBuilder(1024);GetMenuString(menu,i,text,1024,0x400);if(text.ToString().Replace("&","").Contains(name)){Rect r;if(GetMenuItemRect(hwnd,menu,i,out r))return r;}}
  throw new Exception("Requested menu row missing: "+name);
 }
}
'@
}
try {
 switch($Action){
  'Dismiss' {
   if($env:USERNAME -ne 'WDAGUtilityAccount'){throw 'Interactive guest required.'}
   $popup=[GuestMenuInput]::Popup()
   if($popup -ne [IntPtr]::Zero){
    $shell=New-Object -ComObject Shell.Application
    $window=@($shell.Windows()) | Where-Object {$_.LocationURL -eq 'file:///C:/QweShellTest/Targets'} | Select-Object -First 1
    if(-not $window){throw 'Cannot dismiss menu without its owned Explorer target.'}
    $rect=New-Object GuestMenuInput+Rect
    if(-not [GuestMenuInput]::GetWindowRect([IntPtr]$window.HWND,[ref]$rect)){throw 'Cannot locate target title bar.'}
    $null=[GuestMenuInput]::SetCursorPos($rect.Left+150,$rect.Top+16)
    [GuestMenuInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    [GuestMenuInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
   }
   Start-Sleep -Milliseconds 300
   if([GuestMenuInput]::Popup() -ne [IntPtr]::Zero){throw 'Previous menu did not dismiss.'}
  }
  'MeasureDpi' {
   Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GuestDpi {
 [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
}
'@
   $old=[GuestDpi]::SetThreadDpiAwarenessContext([IntPtr](-4))
   try {
    $result.SystemDpi=[GuestDpi]::GetDpiForSystem()
    $result.ExplorerDpi=[GuestDpi]::GetDpiForWindow([GuestDpi]::GetShellWindow())
   } finally { $null=[GuestDpi]::SetThreadDpiAwarenessContext($old) }
  }
  'Prepare' {
   Copy-Item C:\QweShellInput\app C:\QweShellTest -Recurse
   foreach($file in $manifest.Files){if((Get-FileHash (Join-Path C:\QweShellTest $file.Path)).Hash -ne $file.SHA256){throw "Hash mismatch: $($file.Path)"}}
    if($Mode -in @('matrix','scroll')){
    Copy-Item C:\QweShellInput\renderer-fixture.nss C:\QweShellTest\renderer-fixture.nss
    Add-Content C:\QweShellTest\shell.nss "`nimport 'renderer-fixture.nss'" -Encoding UTF8
    $result.FixtureSHA256=(Get-FileHash C:\QweShellTest\renderer-fixture.nss).Hash
   }
   New-Item -ItemType Directory -Force C:\QweShellTest\Targets\SelectedFolder | Out-Null
   $result.VerifiedFiles=$manifest.Files.Count
  }
  'Register' {
   $p=Start-Process C:\QweShellTest\shell.exe -ArgumentList '-r -s -t' -WindowStyle Hidden -PassThru
   if(-not $p.WaitForExit(30000)){throw 'Registration timed out.'}
   $result.ExitCode=$p.ExitCode
   if($p.ExitCode -ne 0){throw 'Registration failed.'}
   $result.RegisteredDLL=(Get-ItemProperty 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{BAE3934B-8A6A-4BFB-81BD-3FC599A1BAF1}\InprocServer32').'(default)'
  }
  'Harness' {
   if(-not (Test-Path C:\QweShellHarness)){Copy-Item C:\QweShellInput\harness C:\QweShellHarness -Recurse}
   $p=Start-Process C:\QweShellHarness\ShellStudio.SandboxHarness.exe -ArgumentList "C:\QweShellTest\shell.nss C:\QweShellEvidence\$RunName $Mode" -WindowStyle Hidden -PassThru -RedirectStandardOutput "C:\QweShellEvidence\$RunName-stdout.log" -RedirectStandardError "C:\QweShellEvidence\$RunName-stderr.log"
   $result.Pid=$p.Id
  }
  'Screenshot' {
   Add-Type -AssemblyName System.Windows.Forms,System.Drawing
   $bounds=[Windows.Forms.SystemInformation]::VirtualScreen
   $bitmap=New-Object Drawing.Bitmap $bounds.Width,$bounds.Height
   $graphics=[Drawing.Graphics]::FromImage($bitmap)
   $graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size)
   $bitmap.Save("C:\QweShellEvidence\$RunName-desktop.png")
   $graphics.Dispose(); $bitmap.Dispose()
  }
  'Menu' {
   if($env:USERNAME -ne 'WDAGUtilityAccount'){throw 'Interactive guest required.'}
   $shell=New-Object -ComObject Shell.Application
   $window=@($shell.Windows()) | Where-Object {$_.LocationURL -eq 'file:///C:/QweShellTest/Targets'} | Select-Object -First 1
   if(-not $window){
    $shell.Open('C:\QweShellTest\Targets')
    Start-Sleep -Seconds 3
    $window=@($shell.Windows()) | Where-Object {$_.LocationURL -eq 'file:///C:/QweShellTest/Targets'} | Select-Object -First 1
   }
   if(-not $window){throw 'Explorer target window not found.'}
   $window.Document.SelectItem($window.Document.Folder.ParseName('SelectedFolder'),29)
   $ws=New-Object -ComObject WScript.Shell
   $null=$ws.AppActivate($window.LocationName)
   Start-Sleep -Seconds 1
   Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
   $item=$null
   $lookupDeadline=[DateTime]::UtcNow.AddSeconds(3)
   do {
    # Explorer can expose the list container before its virtualized children.
    # Enumerating refreshes that provider; retain the exact-name requirement.
    $element=[Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.HWND)
    $items=$element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $item=@($items | Where-Object {$_.Current.Name -eq 'SelectedFolder' -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::ListItem}) | Select-Object -First 1
    if(-not $item){Start-Sleep -Milliseconds 200}
   } while(-not $item -and [DateTime]::UtcNow -lt $lookupDeadline)
   if(-not $item){throw 'Selected folder automation element missing.'}
   $item.SetFocus()
   $null=[GuestMenuInput]::SetForegroundWindow([IntPtr]$window.HWND)
   Start-Sleep -Milliseconds 300
   $bounds=$item.Current.BoundingRectangle
   $null=[GuestMenuInput]::SetCursorPos([int]($bounds.Left+100),[int]($bounds.Top+$bounds.Height/2))
   [GuestMenuInput]::mouse_event(8,0,0,0,[UIntPtr]::Zero)
   [GuestMenuInput]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
   Start-Sleep -Milliseconds 500
   if([GuestMenuInput]::Popup() -eq [IntPtr]::Zero){throw 'Context menu did not open.'}
   $result.Window=$window.LocationName
  }
  'Submenu' {
   Start-Sleep -Milliseconds 500
    $name=if($Mode -eq 'matrix'){'Renderer fixture'}elseif($Mode -eq 'scroll'){'Renderer scroll'}else{'Pin/Unpin'}
   $rect=[GuestMenuInput]::NamedRow($name)
   $null=[GuestMenuInput]::SetCursorPos([int](($rect.Left+$rect.Right)/2),[int](($rect.Top+$rect.Bottom)/2))
   Start-Sleep -Milliseconds 700
  }
 }
 $result.Success=$true
}catch{$result.Error=$_.Exception.ToString()}
$result | ConvertTo-Json -Depth 6 | Set-Content "C:\QweShellEvidence\$RunName-$Action.json"
if(-not $result.Success){exit 1}
