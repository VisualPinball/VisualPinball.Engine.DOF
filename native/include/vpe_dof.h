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
//
// The library is loaded for the lifetime of the process, as Unity loads native plugins, and is not made to be unloaded:
// the GCC runtime linked statically on Linux keeps an exception pool, and the file confinement a thread-local root,
// which dlclose does not free, so repeated dlopen/dlclose leaks. Destroy the instance and create a new one instead.

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
#define VPE_DOF_ABI_VERSION 6

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
/// Receives one output controller of a cabinet config (vpe_dof_cabinet_controllers). The strings are only valid during
/// the call.
typedef void (VPE_DOF_CALL *vpe_dof_controller_fn)(int32_t loaded, const char* type, const char* name, const char* reason, void* user);

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_abi_version(void);
/// libdof's version, such as "0.5.4".
VPE_DOF_API const char* VPE_DOF_CALL vpe_dof_libdof_version(void);
/// Sets the log callback (or NULL) and the most detailed level passed to it, by severity: VPE_DOF_LOG_ERROR passes errors,
/// VPE_DOF_LOG_WARN adds warnings, VPE_DOF_LOG_INFO adds information and VPE_DOF_LOG_DEBUG everything. Process-wide;
/// the callback and its user pointer change together. Returns once no line is being delivered to the previous one (not
/// waiting when called from a callback), so the previous callback can be unloaded afterwards. The callback must not block.
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
/// Turns every output off, stops DOF's threads and disconnects the controllers. Blocks until they stopped. Fails if
/// a controller could not turn its outputs off, neither with its final update nor with its disconnect blackout; the zeros that went out also confirm a closing published just before.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_finish(vpe_dof* dof, char* error, int32_t error_size);
/// Frees the instance. Calls finish first if it was not called.
VPE_DOF_API void VPE_DOF_CALL vpe_dof_destroy(vpe_dof* dof);

/// Closes (open = 0) or opens (open = 1) the output gate. While closed, every output sends 0 and keeps the value
/// effects set for when it opens again; controllers also skip their connect test sequences. Before init it applies at
/// once, so a session can start closed: every controller then turns its outputs off before an opening reaches it, also
/// one whose first update comes after the gate opened. After init it runs on DOF's main thread and returns at once.
/// Returns the gate generation, which vpe_dof_gate_state reports against, or -1 while init runs, for an opening once
/// finish started, and after finish. It never waits for DOF's main thread or a device; it only takes short internal
/// locks. A change queued before finish is still applied, after the table element changes queued before it; once finish
/// started, a closing latches the gate (vpe_dof_latch_output_gate) and returns the latch's generation. Every instance
/// has a gate of its own, open when it is created; after vpe_dof_latch_output_gate a change applies as a closing.
VPE_DOF_API int64_t VPE_DOF_CALL vpe_dof_set_output_gate(vpe_dof* dof, int32_t open);
/// Turns the controllers' connect test sequences (TestOnConnect of Wemos D1 and UMX controllers) off (allowed = 0) for
/// the rest of the instance, or on again. They run only while the gate is open for the controller anyway (one connected
/// while it was closed turned its outputs off first), and once: a test the gate skipped does not run on a reconnect.
/// Turned off, they do not run when the gate opens either, so a controller that connects late or reconnects while the
/// gate is open, as in a port test, does not light its whole strip. Call before init to cover the controllers init
/// connects. Every new instance allows them. VPE_DOF_INVALID_ARGUMENT if `dof` is NULL.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_set_connect_tests(vpe_dof* dof, int32_t allowed);
/// Closes the output gate at once, from any thread and in any state, and latches it closed for the rest of the
/// instance. An opening requested before the latch that DOF's main thread has not applied yet never reaches the
/// controllers as an opening: it reports 0 until DOF's main thread gets to it, then 2 (superseded). Every change
/// requested after the latch applies as a closing and reports the closed state. When latches race, every change
/// requested after the first one reserved its generation, the other latches included, is a closing, and a result a
/// caller saw does not change when a later latch returns. The closing is published to the controllers at once and every
/// controller's updater sends it without DOF's main thread: an idle updater checks the gate every 50 ms, its polling
/// interval, which bounds how long it takes to notice; a busy one stops the opening's transfer before its next write to
/// the device (each report, packet, command or retry), and then sends the closing to every output. A write that was
/// already past that check when the latch came can still reach the device after the latch returned: at most that one
/// write per controller, followed by the closing. Nothing else of the opening goes out. Returns the closing's
/// generation, or -1 if `dof` is NULL. Safe to call from a realtime thread or a fault handler: it takes no lock and
/// never allocates. Once it returned, the gate reports closed to every caller, also to an opening DOF's main thread is
/// applying at that moment. It only ever reaches this instance's gate, never a later instance's.
VPE_DOF_API int64_t VPE_DOF_CALL vpe_dof_latch_output_gate(vpe_dof* dof);
/// Reports a gate change: 0 until it was published to the controllers (by DOF's main thread; a latch publishes its own
/// closing at once), then 1, or 2 (superseded) if it is an opening a latch overtook (see vpe_dof_latch_output_gate). A 1
/// becomes 2 once a later change that reached the controllers set the other state; 2 is final. Every change the host
/// requested is applied in turn, none is dropped, so a change reports 0 only until DOF's main thread gets to it. Also how many controller units confirmed the state: a transfer to their device carried it, or a later change
/// to the same state. How many have not yet, how many failed (device missing, write failed, or the updater ended on its
/// own, which outranks what it confirmed before; a unit that finished cleanly keeps what it confirmed or left alone and
/// fails later changes), and how many are untouched: units DOF never used, which it leaves alone while the gate is
/// open, so nothing was written to them. A closing has no untouched units: it is written to every unit. Any of the
/// count pointers may be NULL. The
/// counts describe the latest generation exactly. For an older one they are what each unit wrote last in that state,
/// at different times, so they do not tell what the outputs show now: ask about the latest generation to know the
/// outputs' state. A generation old enough to be forgotten reports 2. A confirmed state is not a physical
/// acknowledgement: it means the transport took the data (Art-Net, for one, only sends a packet). Counts stay after
/// finish.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_gate_state(vpe_dof* dof, int64_t generation, int32_t* confirmed, int32_t* pending, int32_t* failed, int32_t* untouched);
/// Names the controllers that have not confirmed `generation` yet, through `callback`: the ones vpe_dof_gate_state
/// counts as pending or failed.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_gate_pending(vpe_dof* dof, int64_t generation, vpe_dof_string_fn callback, void* user);

/// Sets one port of an ini device (the N of directoutputconfigN.ini; 1 for directoutputconfig.ini) to a value, as the
/// port test does. Runs on DOF's main thread and goes through the output gate. VPE_DOF_INVALID_ARGUMENT if the device
/// or port does not exist, or the port names a toy (an LED strip or matrix) instead of a controller output.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_test_output(vpe_dof* dof, int32_t device, int32_t port, int32_t value);

/// Routes the ports of an ini device (the N of directoutputconfigN.ini) to other outputs of its LedWiz equivalent, for
/// cabling that differs from the configuration: port i + 1 drives the output port targets[i] drives in the cabinet, its
/// own for 0 or i + 1. A port whose target the equivalent has no output for drives nothing. Applied when init sets up
/// the cabinet, before the table config makes its toys, so games and port tests follow it. Call before vpe_dof_init; a
/// later call for the device replaces its map, a count of 0 removes it. VPE_DOF_INVALID_ARGUMENT for a device outside
/// 1..999, a count outside 0..4096, or no targets for a count; VPE_DOF_INVALID_STATE after init.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_set_output_map(vpe_dof* dof, int32_t device, const int32_t* targets, int32_t count);

/// After init: lists "controller|<type>|<name>|<outputs>" and "toy|<type>|<name>" lines describing the cabinet.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_describe(vpe_dof* dof, vpe_dof_string_fn callback, void* user);

/// Lists the table rows of the ini files libdof finds under `base_path` for `table_file` as "<device>|<rom>" lines,
/// where device is the N of directoutputconfigN.ini. Needs no instance and can run next to one. Returns the number of
/// rows or -1 on error.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_rom_names(const char* base_path, const char* table_file, vpe_dof_string_fn callback, void* user);

/// Reads the output controllers of the cabinet config the global config under `base_path` names, as a session would,
/// and reports each controller element through `callback`, each field as it is: loaded is 1 if a session loads it and 0
/// if it skips it, type is the element name, name its name (may be empty) and reason, for a skipped one, why (no Id;
/// Number "one" is not a whole number; libdof has no output controller of this type; ...).
/// It opens no device, starts no thread, builds no outputs, registers nothing with the output gate and leaves alone the
/// device lists a running instance uses: it needs no instance and can run next to one. Like a session, it reads no file
/// outside `base_path`. Returns the number of controllers (0 when the global config names no cabinet config) or -1, with
/// a message in `error`, for what fails a session's init: no global config, one that cannot be read, or a cabinet config
/// it names that does not exist or is not valid XML.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_cabinet_controllers(const char* base_path, vpe_dof_controller_fn callback, void* user, char* error, int32_t error_size);

/// Lists connected devices without opening them for output, one line per device:
/// "hid|<vid>|<pid>|<usage page>|<usage>|<interface>|<release>|<manufacturer>|<product>|<serial>|<path>",
/// "usb|<vid>|<pid>|<bus>|<address>|<port path>" and
/// "serial|<port>|<description>|<transport>|<vid>|<pid>|<serial>". Numbers are decimal; '|' in strings becomes '/'.
VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_enumerate_devices(vpe_dof_string_fn callback, void* user);

#ifdef __cplusplus
}
#endif
