@echo off
rem Builds SampleCppNodes.dll with the Visual C++ compiler - no project file
rem and no CMake needed. Run it from anywhere; the DLL is written beside it.
rem
rem     build.cmd [folder holding sirius_node.hpp]
rem
rem Needs Visual Studio 2019 or later with "Desktop development with C++",
rem or the Build Tools for Visual Studio with the same workload.

setlocal

rem The headers: named on the command line, or the cpp folder of the SDK
rem this sample came in, or the application's source tree.
set "SIRIUS_INCLUDE=%~1"
if "%SIRIUS_INCLUDE%"=="" if exist "%~dp0..\cpp\sirius_node.hpp" set "SIRIUS_INCLUDE=%~dp0..\cpp"
if "%SIRIUS_INCLUDE%"=="" if exist "%~dp0..\..\src\Sirius.Vision\Cpp\sirius_node.hpp" set "SIRIUS_INCLUDE=%~dp0..\..\src\Sirius.Vision\Cpp"

if not exist "%SIRIUS_INCLUDE%\sirius_node.hpp" (
    echo sirius_node.hpp was not found. Name its folder: build.cmd "C:\Program Files\Sirius Studio Plus\cpp"
    exit /b 1
)

rem The compiler: already on PATH in a Developer Command Prompt; otherwise
rem found through Visual Studio's own locator.
where cl >nul 2>nul
if errorlevel 1 (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if not exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
        echo No Visual C++ compiler was found. Install "Desktop development with C++".
        exit /b 1
    )

    for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
)

if not "%VSINSTALL%"=="" call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul

where cl >nul 2>nul
if errorlevel 1 (
    echo No Visual C++ compiler was found. Install "Desktop development with C++".
    exit /b 1
)

pushd "%~dp0"

rem /LD a DLL; /EHsc C++ exceptions; /MD the shared C++ runtime, which the
rem application already carries; /Zi with /DEBUG a .pdb, so a crash can be
rem traced to its line.
cl /nologo /LD /EHsc /MD /O2 /W4 /Zi /I "%SIRIUS_INCLUDE%" sample_nodes.cpp /Fe:SampleCppNodes.dll /link /DEBUG /OPT:REF /OPT:ICF
set "RESULT=%ERRORLEVEL%"

popd
exit /b %RESULT%
