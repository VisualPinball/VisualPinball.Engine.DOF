cmake_minimum_required(VERSION 3.24)

# VpeDof: libdof, the hardware libraries it uses (hidapi, libusb, libserialport, libftdi1) and VPE's C ABI shim,
# linked into one shared library per platform. The hardware libraries are compiled from the pinned submodules as
# static libraries, so the package ships a single native binary that cannot clash with other packages' copies.

if(APPLE)
	set(CMAKE_OSX_DEPLOYMENT_TARGET "12.0" CACHE STRING "Minimum macOS version (the player's minimum)")
	if(NOT CMAKE_OSX_ARCHITECTURES)
		set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "macOS architectures")
	endif()
endif()

if(MSVC OR CMAKE_GENERATOR MATCHES "Visual Studio")
	# static CRT: no VC runtime DLLs next to the library
	set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

project(VpeDof C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

set(TP "${CMAKE_CURRENT_SOURCE_DIR}/../third-party")
set(GEN "${CMAKE_BINARY_DIR}/generated")
option(VPE_DOF_BUILD_TESTS "Build the native smoke test" ON)
set(VPE_DOF_OUTPUT_DIR "" CACHE PATH "Folder the library is copied to after the build (the Unity package's Plugins/<rid>)")

if(MSVC)
	add_compile_options(/bigobj /utf-8 /W3 /wd4996 /wd4244 /wd4267 /wd4101 /wd4250 /wd4099)
endif()

# ---------------------------------------------------------------------------------------------------------------------
# libdof sources: copied into the build folder and patched there, so the submodule stays as upstream has it.

set(LIBDOF "${CMAKE_BINARY_DIR}/libdof")
file(GLOB VPE_DOF_PATCHES "${CMAKE_CURRENT_SOURCE_DIR}/patches/*.patch")
list(SORT VPE_DOF_PATCHES)
file(GLOB_RECURSE _libdof_inputs "${TP}/libdof/src/*" "${TP}/libdof/include/*")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS ${VPE_DOF_PATCHES} ${_libdof_inputs})

file(REMOVE_RECURSE "${LIBDOF}")
file(COPY "${TP}/libdof/src" "${TP}/libdof/include" DESTINATION "${LIBDOF}")
file(COPY "${TP}/libdof/third-party/include/" DESTINATION "${LIBDOF}/third-party/include")
find_package(Git REQUIRED)
# A repository of its own, so git apply does not resolve the paths against an enclosing checkout and skip them.
execute_process(COMMAND "${GIT_EXECUTABLE}" init -q WORKING_DIRECTORY "${LIBDOF}" RESULT_VARIABLE _result)
if(NOT _result EQUAL 0)
	message(FATAL_ERROR "git init in ${LIBDOF} failed")
endif()
foreach(patch IN LISTS VPE_DOF_PATCHES)
	execute_process(COMMAND "${GIT_EXECUTABLE}" apply --whitespace=nowarn --verbose "${patch}"
		WORKING_DIRECTORY "${LIBDOF}" RESULT_VARIABLE _result ERROR_VARIABLE _error)
	if(NOT _result EQUAL 0)
		message(FATAL_ERROR "Applying ${patch} to libdof failed: ${_error}")
	endif()
	message(STATUS "Applied ${patch}")
endforeach()
if(VPE_DOF_PATCHES AND NOT EXISTS "${LIBDOF}/src/cab/out/OutputGate.h")
	message(FATAL_ERROR "The libdof patches did not apply to ${LIBDOF}")
endif()

file(GLOB_RECURSE LIBDOF_SOURCES "${LIBDOF}/src/*.cpp")
list(FILTER LIBDOF_SOURCES EXCLUDE REGEX "/src/tools/")
list(APPEND LIBDOF_SOURCES "${LIBDOF}/third-party/include/tinyxml2/tinyxml2.cpp")

# ---------------------------------------------------------------------------------------------------------------------
# Headers under the names libdof includes them by (<hidapi/hidapi.h>, <libusb-1.0/libusb.h>, <libftdi1/ftdi.h>,
# <libserialport.h>). libserialport's MSVC header marks its API dllimport/dllexport; the copy drops that, since
# everything is linked statically.

file(MAKE_DIRECTORY "${GEN}/include/hidapi" "${GEN}/include/libusb-1.0" "${GEN}/include/libftdi1")
configure_file("${TP}/hidapi/hidapi/hidapi.h" "${GEN}/include/hidapi/hidapi.h" COPYONLY)
configure_file("${TP}/libusb/libusb/libusb.h" "${GEN}/include/libusb-1.0/libusb.h" COPYONLY)
configure_file("${TP}/libftdi/src/ftdi.h" "${GEN}/include/libftdi1/ftdi.h" COPYONLY)

set(SERIALPORT "${CMAKE_BINARY_DIR}/libserialport")
file(REMOVE_RECURSE "${SERIALPORT}")
file(COPY "${TP}/libserialport/" DESTINATION "${SERIALPORT}" PATTERN ".git" EXCLUDE)
file(READ "${SERIALPORT}/libserialport.h" _sp_header)
string(REPLACE "__declspec(dllexport)" "" _sp_header "${_sp_header}")
string(REPLACE "__declspec(dllimport)" "" _sp_header "${_sp_header}")
file(WRITE "${SERIALPORT}/libserialport.h" "${_sp_header}")
configure_file("${SERIALPORT}/libserialport.h" "${GEN}/include/libserialport.h" COPYONLY)

# ---------------------------------------------------------------------------------------------------------------------
# libusb

set(LIBUSB "${TP}/libusb/libusb")
set(LIBUSB_SOURCES "${LIBUSB}/core.c" "${LIBUSB}/descriptor.c" "${LIBUSB}/hotplug.c" "${LIBUSB}/io.c" "${LIBUSB}/strerror.c" "${LIBUSB}/sync.c")
if(WIN32)
	list(APPEND LIBUSB_SOURCES "${LIBUSB}/os/events_windows.c" "${LIBUSB}/os/threads_windows.c" "${LIBUSB}/os/windows_common.c"
		"${LIBUSB}/os/windows_usbdk.c" "${LIBUSB}/os/windows_winusb.c")
	set(LIBUSB_CONFIG_DIR "${TP}/libusb/msvc")
else()
	list(APPEND LIBUSB_SOURCES "${LIBUSB}/os/events_posix.c" "${LIBUSB}/os/threads_posix.c")
	if(APPLE)
		list(APPEND LIBUSB_SOURCES "${LIBUSB}/os/darwin_usb.c")
	else()
		# netlink instead of udev for hotplug: no libudev dependency in libusb
		list(APPEND LIBUSB_SOURCES "${LIBUSB}/os/linux_usbfs.c" "${LIBUSB}/os/linux_netlink.c")
	endif()
	include(CheckSymbolExists)
	include(CheckIncludeFile)
	include(CheckStructHasMember)
	include(CheckTypeSize)
	check_symbol_exists(clock_gettime "time.h" HAVE_CLOCK_GETTIME)
	check_symbol_exists(pipe2 "fcntl.h;unistd.h" HAVE_PIPE2)
	check_include_file("sys/eventfd.h" HAVE_EVENTFD)
	check_include_file("sys/timerfd.h" HAVE_TIMERFD)
	check_include_file("asm/types.h" HAVE_ASM_TYPES_H)
	check_symbol_exists(pthread_threadid_np "pthread.h" HAVE_PTHREAD_THREADID_NP)
	check_symbol_exists(pthread_setname_np "pthread.h" HAVE_PTHREAD_SETNAME_NP)
	set(HAVE_STRUCT_TIMESPEC 1)
	set(LIBUSB_CONFIG_DIR "${GEN}/libusb")
	configure_file("${CMAKE_CURRENT_SOURCE_DIR}/cmake/libusb-config.h.in" "${LIBUSB_CONFIG_DIR}/config.h")
endif()
add_library(vpe_libusb STATIC ${LIBUSB_SOURCES})
target_include_directories(vpe_libusb PRIVATE "${LIBUSB_CONFIG_DIR}" PUBLIC "${LIBUSB}")
if(APPLE)
	target_link_libraries(vpe_libusb PUBLIC "-framework CoreFoundation" "-framework IOKit" "-framework Security" objc)
elseif(UNIX)
	find_package(Threads REQUIRED)
	target_link_libraries(vpe_libusb PUBLIC Threads::Threads)
endif()

# ---------------------------------------------------------------------------------------------------------------------
# hidapi

set(HIDAPI "${TP}/hidapi")
if(WIN32)
	add_library(vpe_hidapi STATIC "${HIDAPI}/windows/hid.c")
	target_link_libraries(vpe_hidapi PUBLIC setupapi)
elseif(APPLE)
	add_library(vpe_hidapi STATIC "${HIDAPI}/mac/hid.c")
	target_link_libraries(vpe_hidapi PUBLIC "-framework IOKit" "-framework CoreFoundation" "-framework AppKit")
else()
	# hidraw backend, as libdof uses on Linux
	add_library(vpe_hidapi STATIC "${HIDAPI}/linux/hid.c")
	find_package(PkgConfig REQUIRED)
	pkg_check_modules(UDEV REQUIRED IMPORTED_TARGET libudev)
	target_link_libraries(vpe_hidapi PUBLIC PkgConfig::UDEV)
endif()
target_include_directories(vpe_hidapi PUBLIC "${HIDAPI}/hidapi")
target_compile_definitions(vpe_hidapi PUBLIC HID_API_NO_EXPORT_DEFINE)

# ---------------------------------------------------------------------------------------------------------------------
# libserialport

set(SERIALPORT_SOURCES "${SERIALPORT}/serialport.c" "${SERIALPORT}/timing.c")
if(WIN32)
	list(APPEND SERIALPORT_SOURCES "${SERIALPORT}/windows.c")
elseif(APPLE)
	list(APPEND SERIALPORT_SOURCES "${SERIALPORT}/macosx.c")
else()
	list(APPEND SERIALPORT_SOURCES "${SERIALPORT}/linux.c" "${SERIALPORT}/linux_termios.c")
endif()
add_library(vpe_serialport STATIC ${SERIALPORT_SOURCES})
target_include_directories(vpe_serialport PUBLIC "${SERIALPORT}")
if(WIN32)
	target_compile_definitions(vpe_serialport PRIVATE LIBSERIALPORT_MSBUILD)
	target_link_libraries(vpe_serialport PUBLIC setupapi cfgmgr32)
else()
	target_compile_definitions(vpe_serialport PRIVATE LIBSERIALPORT_ATBUILD)
	check_symbol_exists(realpath "stdlib.h" HAVE_REALPATH)
	check_include_file("sys/file.h" HAVE_SYS_FILE_H)
	check_symbol_exists(flock "sys/file.h" HAVE_FLOCK)
	if(NOT APPLE)
		set(CMAKE_EXTRA_INCLUDE_FILES "linux/termios.h")
		check_type_size("struct termios2" STRUCT_TERMIOS2)
		set(CMAKE_EXTRA_INCLUDE_FILES)
		if(HAVE_STRUCT_TERMIOS2)
			set(HAVE_STRUCT_TERMIOS2 1)
			check_struct_has_member("struct termios2" c_ispeed "linux/termios.h" HAVE_STRUCT_TERMIOS2_C_ISPEED)
			check_struct_has_member("struct termios2" c_ospeed "linux/termios.h" HAVE_STRUCT_TERMIOS2_C_OSPEED)
		endif()
		check_symbol_exists(BOTHER "linux/termios.h" HAVE_DECL_BOTHER)
		set(CMAKE_EXTRA_INCLUDE_FILES "linux/serial.h")
		check_type_size("struct serial_struct" STRUCT_SERIAL_STRUCT)
		set(CMAKE_EXTRA_INCLUDE_FILES)
		if(HAVE_STRUCT_SERIAL_STRUCT)
			set(HAVE_STRUCT_SERIAL_STRUCT 1)
		endif()
	endif()
	check_struct_has_member("struct termios" c_ispeed "termios.h" HAVE_STRUCT_TERMIOS_C_ISPEED)
	check_struct_has_member("struct termios" c_ospeed "termios.h" HAVE_STRUCT_TERMIOS_C_OSPEED)
	configure_file("${CMAKE_CURRENT_SOURCE_DIR}/cmake/libserialport-config.h.in" "${GEN}/libserialport/config.h")
	target_include_directories(vpe_serialport PRIVATE "${GEN}/libserialport")
	if(APPLE)
		target_link_libraries(vpe_serialport PUBLIC "-framework IOKit" "-framework CoreFoundation")
	endif()
endif()

# ---------------------------------------------------------------------------------------------------------------------
# libftdi1

set(LIBFTDI "${TP}/libftdi/src")
set(MAJOR_VERSION 1)
set(MINOR_VERSION 5)
set(VERSION_STRING "1.5")
set(SNAPSHOT_VERSION "vpe")
configure_file("${LIBFTDI}/ftdi_version_i.h.in" "${GEN}/libftdi/ftdi_version_i.h" @ONLY)
add_library(vpe_ftdi STATIC "${LIBFTDI}/ftdi.c" "${LIBFTDI}/ftdi_stream.c")
target_include_directories(vpe_ftdi PRIVATE "${GEN}/libftdi" PUBLIC "${LIBFTDI}")
target_link_libraries(vpe_ftdi PUBLIC vpe_libusb)

# ---------------------------------------------------------------------------------------------------------------------
# VpeDof

add_library(VpeDof SHARED
	${LIBDOF_SOURCES}
	src/vpe_dof.cpp
	src/vpe_dof_devices.cpp)
target_include_directories(VpeDof PRIVATE
	"${CMAKE_CURRENT_SOURCE_DIR}/include"
	"${LIBDOF}/include"
	"${LIBDOF}/src"
	"${LIBDOF}/third-party/include"
	"${GEN}/include")
target_compile_definitions(VpeDof PRIVATE VPE_DOF_BUILD)
if(WIN32)
	target_compile_definitions(VpeDof PRIVATE NOMINMAX)
endif()
target_link_libraries(VpeDof PRIVATE vpe_hidapi vpe_libusb vpe_serialport vpe_ftdi)
if(WIN32)
	target_link_libraries(VpeDof PRIVATE ws2_32 setupapi cfgmgr32 hid)
	set_target_properties(VpeDof PROPERTIES PREFIX "" OUTPUT_NAME "VpeDof")
elseif(APPLE)
	set_target_properties(VpeDof PROPERTIES OUTPUT_NAME "VpeDof" INSTALL_NAME_DIR "@rpath" BUILD_WITH_INSTALL_NAME_DIR ON
		LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/cmake/vpe_dof.exp")
	# only the C ABI: tinyxml2 and the hardware libraries mark their API visible themselves
	target_link_options(VpeDof PRIVATE "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/cmake/vpe_dof.exp")
else()
	set_target_properties(VpeDof PROPERTIES OUTPUT_NAME "VpeDof" LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/cmake/vpe_dof.map")
	# Only the C ABI is exported (the version script also hides the C++ template instantiations and tinyxml2's API).
	# libstdc++ and libgcc are linked statically, so the library runs on distributions with an older libstdc++ than
	# the build machine's and cannot clash with the one the host process loaded.
	target_link_options(VpeDof PRIVATE "-Wl,--exclude-libs,ALL" "-Wl,-z,defs"
		"-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/cmake/vpe_dof.map" -static-libstdc++ -static-libgcc)
endif()

if(MSVC)
	# libserialport defines these itself.
	foreach(_target vpe_libusb vpe_hidapi vpe_ftdi VpeDof)
		target_compile_definitions(${_target} PRIVATE _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_DEPRECATE)
	endforeach()
endif()

if(VPE_DOF_OUTPUT_DIR)
	# The library's Unity import settings (unity/<rid>/<library>.meta, with fixed GUIDs, and the folder's) are copied
	# with it, before it: the binaries are not in git, and Unity deletes a .meta in a package whose asset is missing,
	# so the package keeps no .meta of its own for them.
	if(WIN32)
		set(_unity_rid "win-x64")
	elseif(APPLE)
		set(_unity_rid "osx")
	else()
		set(_unity_rid "linux-x64")
	endif()
	set(_unity_meta "${CMAKE_CURRENT_SOURCE_DIR}/unity")
	get_filename_component(_output "${VPE_DOF_OUTPUT_DIR}" ABSOLUTE)
	add_custom_command(TARGET VpeDof POST_BUILD
		COMMAND "${CMAKE_COMMAND}" -E make_directory "${_output}"
		COMMAND "${CMAKE_COMMAND}" -E copy "${_unity_meta}/${_unity_rid}.meta" "${_output}.meta"
		COMMAND "${CMAKE_COMMAND}" -E copy "${_unity_meta}/${_unity_rid}/$<TARGET_FILE_NAME:VpeDof>.meta" "${_output}/"
		COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_FILE:VpeDof>" "${_output}/")
endif()

if(VPE_DOF_BUILD_TESTS)
	add_executable(vpe_dof_smoke tests/smoke.c)
	target_include_directories(vpe_dof_smoke PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
	target_link_libraries(vpe_dof_smoke PRIVATE VpeDof)
endif()
