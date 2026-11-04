@echo off
rem Builds UnrealProjectRenamer.exe (one folder up) from RenamerApp.cs.
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
  -out:"%HERE%..\UnrealProjectRenamer.exe" ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Web.Extensions.dll -r:System.Management.dll ^
  -resource:"%HERE%..\rename_unreal_project.py",rename_unreal_project.py ^
  "%HERE%RenamerApp.cs"
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built %HERE%..\UnrealProjectRenamer.exe
