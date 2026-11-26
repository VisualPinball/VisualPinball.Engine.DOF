// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

// C ABI of VpeDof, VPE's build of libdof. Every function is safe to call from C and never lets a C++ exception escape:
// failures are returned as a status and, where a buffer is passed, a UTF-8 message. Strings are UTF-8 and
// NUL-terminated. Callbacks are called on the thread that calls the function they are passed to, unless noted.
//
// libdof's configuration base path, log and device lists are process-wide, so at most one DOF instance can exist at a
// time. libdof reads files only inside the instance's base path and never writes or deletes any file. On Windows,
// libdof passes paths to the narrow (ANSI code page) file API: pass paths that code page can represent.

#pragma once

#include <stdint.h>

#if defined(_WIN32)
	#if defined(VPE_DOF_BUILD)
		#define VPE_DOF_API __declspec(dllexport)
	#else
		#define VPE_DOF_API __declspec(dllimport)
	#endif
	#define VPE_DOF_CALL __cdecl
#else
	#define VPE_DOF_API __attribute__((visibility("default")))
	#define VPE_DOF_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/// Version of this ABI. Bumped on every incompatible change; the managed binding refuses a library with another one.
#define VPE_DOF_ABI_VERSION 1

enum vpe_dof_status
{
	VPE_DOF_OK = 0,
	VPE_DOF_ERROR = 1,
	VPE_DOF_INVALID_ARGUMENT = 2,
	VPE_DOF_INVALID_STATE = 3,
	VPE_DOF_BUSY = 4,
};

enum vpe_dof_log_level
{
	VPE_DOF_LOG_INFO = 0,
	VPE_DOF_LOG_WARN = 1,
	VPE_DOF_LOG_ERROR = 2,
	VPE_DOF_LOG_DEBUG = 3,
};

typedef struct vpe_dof vpe_dof;

/// Receives one formatted log line. Called on whichever libdof thread logs, so it must be thread-safe.
typedef void (VPE_DOF_CALL *vpe_dof_log_fn)(int32_t level, const char* message, void* user);
/// Receives one string of a list.
typedef void (VPE_DOF_CALL *vpe_dof_string_fn)(const char* value, void* user);

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_abi_version(void);
/// libdof's version, such as "0.5.4".
VPE_DOF_API const char* VPE_DOF_CALL vpe_dof_libdof_version(void);
/// Sets the log callback (or NULL) and the most detailed level passed to it, by severity: VPE_DOF_LOG_ERROR passes errors,
/// VPE_DOF_LOG_WARN adds warnings, VPE_DOF_LOG_INFO adds information and VPE_DOF_LOG_DEBUG everything. Process-wide;
/// the callback and its user pointer change together, and a line being delivered may still reach the previous one.
VPE_DOF_API void VPE_DOF_CALL vpe_dof_set_log(vpe_dof_log_fn callback, void* user, int32_t level);

/// Creates the DOF instance. `base_path` is the folder that contains `directoutputconfig/`, where libdof reads
/// `GlobalConfig_B2SServer.xml`; libdof reads no file outside it. Returns NULL with VPE_DOF_BUSY in `status` while
/// another instance exists.
VPE_DOF_API vpe_dof* VPE_DOF_CALL vpe_dof_create(const char* base_path, int32_t* status);
/// Loads the configuration for a table and ROM, connects the output controllers and starts DOF's threads. Call once.
/// `table_file` names a table file (its folder and base name are used for lookups); `rom_name` may be empty. Fails
/// without `directoutputconfig/GlobalConfig_B2SServer.xml` in the base path. After a failure the instance is finished.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_init(vpe_dof* dof, const char* table_file, const char* rom_name, char* error, int32_t error_size);
/// Queues a table element change: type 'S', 'L', 'G', 'W', 'N', 'D' or 'E', the element number and its value.
VPE_DOF_API void VPE_DOF_CALL vpe_dof_data(vpe_dof* dof, char type, int32_t number, int32_t value);
/// Queues a change of a named table element (`$name` in DOF configs, without the dollar sign).
VPE_DOF_API void VPE_DOF_CALL vpe_dof_data_named(vpe_dof* dof, const char* name, int32_t value);
/// Table element changes queued but not processed by DOF's main thread yet.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_pending_input(vpe_dof* dof);
/// Turns every output off, stops DOF's threads and disconnects the controllers. Blocks until they stopped.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_finish(vpe_dof* dof, char* error, int32_t error_size);
/// Frees the instance. Calls finish first if it was not called.
VPE_DOF_API void VPE_DOF_CALL vpe_dof_destroy(vpe_dof* dof);

/// Closes (open = 0) or opens (open = 1) the output gate. While closed, every output sends 0 and keeps the value effects
/// set for when it opens again; controllers also skip their connect test sequences. Before init it applies at once,
/// so a session can start closed; after init it runs on DOF's main thread and returns at once. Returns the gate
/// generation, which vpe_dof_gate_state reports against, or -1 while init or finish runs and after finish. It never
/// waits for DOF's main thread or a device; it only takes short internal locks. A change queued before finish is still
/// applied. The gate opens with every new instance.
VPE_DOF_API int64_t VPE_DOF_CALL vpe_dof_set_output_gate(vpe_dof* dof, int32_t open);
/// Reports a gate change: 0 until DOF's main thread applied it, then 1, or 2 if a later change set the other state
/// while some unit had not confirmed this one. Also how many controller units confirmed the state: a transfer to their
/// device carried it or a later change to the same state, or, for a unit DOF never used, the gate is open and DOF
/// leaves it alone. And how many have not yet, and how many failed (device missing, write failed, updater stopped or
/// finished). The counts describe the latest generation exactly. For an older one they are what each unit wrote last
/// in that state, at different times, so they do not tell what the outputs show now and 2 can later turn into 1: ask
/// about the latest generation to know the outputs' state. A confirmed state is not a physical acknowledgement:
/// Art-Net, for one, only sends a packet. Counts stay after finish.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_gate_state(vpe_dof* dof, int64_t generation, int32_t* confirmed, int32_t* pending, int32_t* failed);
/// Names the controllers that have not confirmed `generation` yet, through `callback`.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_gate_pending(vpe_dof* dof, int64_t generation, vpe_dof_string_fn callback, void* user);

/// Sets one port of an ini device (the N of directoutputconfigN.ini; 1 for directoutputconfig.ini) to a value, as the
/// port test does. Runs on DOF's main thread and goes through the output gate. VPE_DOF_INVALID_ARGUMENT if the device
/// or port does not exist, or the port names a toy (an LED strip or matrix) instead of a controller output.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_test_output(vpe_dof* dof, int32_t device, int32_t port, int32_t value);

/// After init: lists "controller|<type>|<name>|<outputs>" and "toy|<type>|<name>" lines describing the cabinet.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_describe(vpe_dof* dof, vpe_dof_string_fn callback, void* user);

/// Lists the table rows of the ini files libdof finds under `base_path` for `table_file` as "<device>|<rom>" lines,
/// where device is the N of directoutputconfigN.ini. Needs no instance and can run next to one. Returns the number of
/// rows or -1 on error.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_rom_names(const char* base_path, const char* table_file, vpe_dof_string_fn callback, void* user);

/// Lists connected devices without opening them for output, one line per device:
/// "hid|<vid>|<pid>|<usage page>|<usage>|<interface>|<release>|<manufacturer>|<product>|<serial>|<path>",
/// "usb|<vid>|<pid>|<bus>|<address>|<port path>" and
/// "serial|<port>|<description>|<transport>|<vid>|<pid>|<serial>". Numbers are decimal; '|' in strings becomes '/'.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_enumerate_devices(vpe_dof_string_fn callback, void* user);

#ifdef __cplusplus
}
#endif
