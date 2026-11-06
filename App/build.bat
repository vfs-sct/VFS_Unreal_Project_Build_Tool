@echo off
rem Builds VFSUnrealProjectBuildTool.exe (one folder up) from RenamerApp.cs.
rem Needs Visual Studio's C# compiler; the .exe targets .NET Framework 4.8,
rem which is built into Windows 10/11, so it runs without installing anything.

setlocal
set "HERE=%~dp0"
set "CSC="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq delims=" %%P in (`"%VSWHERE%" -latest -products * -find MSBuild\**\Roslyn\csc.exe`) do set "CSC=%%P"
)
if not defined CSC set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

"%CSC%" -nologo -target:winexe -optimize+ -langversion:7.3 ^
  -out:"%HERE%..\VFSUnrealProjectBuildTool.exe" ^
  -win32icon:"%HERE%RenamerApp.ico" ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Web.Extensions.dll -r:System.Management.dll ^
  -resource:"%HERE%..\VFSUnrealProjectBuildTool.py",VFSUnrealProjectBuildTool.py ^
  "%HERE%RenamerApp.cs"
set "RESULT=%ERRORLEVEL%"
rem If the app is open while building, the compiler renames the running copy to
rem <guid>_VFSUnrealProjectBuildTool.exe (a running .exe can be renamed but not overwritten)
rem and writes the new .exe. Remove those old copies; one still running is skipped until closed.
del /q "%HERE%..\*_VFSUnrealProjectBuildTool.exe" 2>nul
if not "%RESULT%"=="0" (
    echo Build failed. If it says the .exe is in use, close the app and run this again.
    exit /b 1
)
echo Built %HERE%..\VFSUnrealProjectBuildTool.exe
