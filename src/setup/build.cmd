@echo off
setlocal EnableExtensions

set "arch=%~1"
if "%arch%"=="" set "arch=x64"
if /I not "%arch%"=="x64" if /I not "%arch%"=="x86" if /I not "%arch%"=="arm64" (
    echo Unsupported architecture: %arch%
    exit /b 2
)

rem Studio publication and the pinned WiX package are produced by one
rem repository-bounded build entry point. Non-x64 keeps a native-only build.
if /I "%arch%"=="x64" (
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\studio\build.ps1" -Architecture x64 -Configuration Release
) else (
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\studio\build.ps1" -Architecture %arch% -Configuration Release -SkipInstaller
)
exit /b %ERRORLEVEL%
