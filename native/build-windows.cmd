@echo off
rem Builds VpeDof for win-x64 with Visual Studio and copies it into the Unity package.
rem Usage: native\build-windows.cmd [Release|Debug]
rem CMake uses the newest Visual Studio installed (2022 or newer; 2026 needs CMake 4.2), or the one an existing build
rem folder was configured with. VPE_DOF_GENERATOR names another one ("Visual Studio 17 2022"). BUILD_DIR overrides the build folder.
setlocal
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release
set ROOT=%~dp0..
set BUILD=%ROOT%\build\win-x64
if not "%BUILD_DIR%"=="" set BUILD=%BUILD_DIR%
set OUT=%ROOT%\VisualPinball.Engine.DOF.Unity\Plugins\win-x64

if not exist "%ROOT%\third-party\libdof\src" (
	echo The submodules are missing. Run: git submodule update --init --recursive
	exit /b 1
)

rem a machine-wide CMAKE_GENERATOR (such as Ninja) does not take -A
set CMAKE_GENERATOR=
if "%VPE_DOF_GENERATOR%"=="" (
	cmake -S "%~dp0." -B "%BUILD%" -A x64 -DVPE_DOF_OUTPUT_DIR="%OUT%" || exit /b 1
) else (
	cmake -S "%~dp0." -B "%BUILD%" -G "%VPE_DOF_GENERATOR%" -A x64 -DVPE_DOF_OUTPUT_DIR="%OUT%" || exit /b 1
)
cmake --build "%BUILD%" --config %CONFIG% --parallel || exit /b 1
rem "||" reads the exit code itself: a crash exits below 0, which "if errorlevel 1" misses, and an ERRORLEVEL variable
rem inherited from the environment would hide it from %errorlevel%.
"%BUILD%\%CONFIG%\vpe_dof_smoke.exe" "%~dp0tests\fixtures\artnet" "%~dp0tests\fixtures\artnet\tables\smoke.vpx" vpetest > "%BUILD%\smoke.log" 2>&1 || (
	type "%BUILD%\smoke.log"
	echo The native smoke test failed.
	exit /b 1
)
"%BUILD%\%CONFIG%\vpe_dof_internal.exe" > "%BUILD%\internal.log" 2>&1 || (
	type "%BUILD%\internal.log"
	echo The native internal tests failed.
	exit /b 1
)
echo VpeDof %CONFIG% built and copied to %OUT%
