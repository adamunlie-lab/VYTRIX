@echo off
rem Builds VytrixCore.dll (x64, Release) with the installed MSVC toolchain.
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo [VYTRIX] MSVC toolchain not found. Install the "Desktop development with C++" workload.
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "OUT=%~dp0bin"
if not exist "%OUT%" mkdir "%OUT%"
pushd "%~dp0VytrixCore"
cl /nologo /std:c++20 /O2 /EHsc /MT /W4 /wd4100 /DUNICODE /D_UNICODE /LD VytrixCore.cpp /Fo"%OUT%\\" /Fe"%OUT%\VytrixCore.dll" /link /NOLOGO /DYNAMICBASE /NXCOMPAT /GUARD:CF
set ERR=%ERRORLEVEL%
popd
exit /b %ERR%
