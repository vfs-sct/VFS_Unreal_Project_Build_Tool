@echo off
rem Runs rename_unreal_project.py with system Python, or with the Python
rem bundled in an installed Unreal Engine if there is none.
rem Usage: RenameUnrealProject.bat <path to .uproject or folder> <NewName> [options]

setlocal
set "SCRIPT=%~dp0rename_unreal_project.py"

where python >nul 2>nul && python -c "import sys" >nul 2>nul && (python "%SCRIPT%" %* & goto :eof)

for /d %%E in ("%ProgramFiles%\Epic Games\UE_*") do (
    if exist "%%E\Engine\Binaries\ThirdParty\Python3\Win64\python.exe" set "UEPY=%%E\Engine\Binaries\ThirdParty\Python3\Win64\python.exe"
)
if defined UEPY (
    "%UEPY%" "%SCRIPT%" %*
    goto :eof
)

echo Could not find Python. Install Python 3, or Unreal Engine under "%ProgramFiles%\Epic Games".
exit /b 1
