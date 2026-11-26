@echo off
rem Builds VpeDof for win-x64 with Visual Studio 2022 and copies it into the Unity package.
rem Usage: native\build-windows.cmd [Release|Debug]
setlocal
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release
set ROOT=%~dp0..
set BUILD=%ROOT%\build\win-x64
set OUT=%ROOT%\VisualPinball.Engine.DOF.Unity\Plugins\win-x64

if not exist "%ROOT%\third-party\libdof\src" (
	echo The submodules are missing. Run: git submodule update --init --recursive
	exit /b 1
)

cmake -S "%~dp0." -B "%BUILD%" -G "Visual Studio 17 2022" -A x64 -DVPE_DOF_OUTPUT_DIR="%OUT%" || exit /b 1
cmake --build "%BUILD%" --config %CONFIG% --parallel || exit /b 1
"%BUILD%\%CONFIG%\vpe_dof_smoke.exe" "%~dp0tests\fixtures\artnet" "%~dp0tests\fixtures\artnet\tables\smoke.vpx" vpetest > "%BUILD%\smoke.log" 2>&1
if errorlevel 1 (
	type "%BUILD%\smoke.log"
	echo The native smoke test failed.
	exit /b 1
)
echo VpeDof %CONFIG% built and copied to %OUT%
