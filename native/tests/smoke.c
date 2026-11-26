// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

// Loads VpeDof through its C ABI and runs two sessions against a configuration folder (argument 1, default "."):
// create, gate before init, init, describe, gate changes, gate latch, finish, destroy. Exits non-zero on the first failure.
// Usage: vpe_dof_smoke [base path] [table file] [rom]

#include <stdio.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif

#include "vpe_dof.h"

static void SleepMs(int ms)
{
#ifdef _WIN32
	Sleep((DWORD)ms);
#else
	struct timespec delay = { ms / 1000, (long)(ms % 1000) * 1000000L };
	nanosleep(&delay, NULL);
#endif
}

static int failures = 0;

#define CHECK(condition, message)                                                                                       \
	do                                                                                                                  \
	{                                                                                                                   \
		if (!(condition))                                                                                               \
		{                                                                                                               \
			fprintf(stderr, "FAIL: %s\n", message);                                                                     \
			failures++;                                                                                                 \
		}                                                                                                               \
	} while (0)

static void VPE_DOF_CALL OnLog(int32_t level, const char* message, void* user)
{
	(void)user;
	printf("[libdof %d] %s\n", level, message);
}

static void VPE_DOF_CALL OnLine(const char* value, void* user)
{
	printf("  %s: %s\n", (const char*)user, value);
}

static void RunSession(const char* basePath, const char* table, const char* rom)
{
	int32_t status = -1;
	vpe_dof* dof = vpe_dof_create(basePath, &status);
	CHECK(dof != NULL && status == VPE_DOF_OK, "create");
	if (!dof)
		return;

	vpe_dof* second = vpe_dof_create(basePath, &status);
	CHECK(second == NULL && status == VPE_DOF_BUSY, "second instance is refused");

	int64_t closed = vpe_dof_set_output_gate(dof, 0);
	CHECK(closed > 0, "gate closes before init");
	int32_t confirmed, pending, failed, untouched;
	CHECK(vpe_dof_gate_state(dof, closed, &confirmed, &pending, &failed, &untouched) == 1, "gate applied before init");
	CHECK(confirmed == 0 && pending == 0 && failed == 0 && untouched == 0, "no units before init");
	CHECK(vpe_dof_gate_state(dof, closed, NULL, NULL, NULL, NULL) == 1, "the counts are optional");
	CHECK(vpe_dof_set_connect_tests(NULL, 0) == VPE_DOF_INVALID_ARGUMENT, "connect tests need an instance");
	CHECK(vpe_dof_set_connect_tests(dof, 0) == VPE_DOF_OK, "connect tests turned off before init");

	char error[512] = { 0 };
	int32_t result = vpe_dof_init(dof, table, rom, error, (int32_t)sizeof(error));
	CHECK(result == VPE_DOF_OK, error[0] ? error : "init");
	CHECK(vpe_dof_init(dof, table, rom, error, (int32_t)sizeof(error)) == VPE_DOF_INVALID_STATE, "second init is refused");

	vpe_dof_describe(dof, OnLine, "describe");

	int64_t open = vpe_dof_set_output_gate(dof, 1);
	CHECK(open > closed, "gate opens after init");
	vpe_dof_data(dof, 'S', 1, 1);
	vpe_dof_data(dof, 'S', 1, 0);
	vpe_dof_data_named(dof, "smoke", 1);

	int64_t latched = vpe_dof_latch_output_gate(dof);
	CHECK(latched > open, "gate latches closed");
	int64_t after = vpe_dof_set_output_gate(dof, 1);
	CHECK(after > latched, "an opening after the latch still gets a generation");
	// The opening after the latch applies as a closing: every unit writes it, none is left alone.
	int32_t gateStatus = 0;
	for (int i = 0; i < 400; i++)
	{
		gateStatus = vpe_dof_gate_state(dof, after, &confirmed, &pending, &failed, &untouched);
		if (gateStatus == 1 && pending == 0)
			break;
		SleepMs(5);
	}
	CHECK(gateStatus == 1 && pending == 0, "the opening after the latch settles");
	CHECK(untouched == 0, "a closing leaves no unit untouched");

	result = vpe_dof_finish(dof, error, (int32_t)sizeof(error));
	CHECK(result == VPE_DOF_OK, error[0] ? error : "finish");
	CHECK(vpe_dof_set_output_gate(dof, 0) == -1, "gate refused after finish");
	vpe_dof_destroy(dof);
}

// The fixture's global config points its log, with ClearLogOnSessionStart, at <base>/../canary.log, outside the
// configuration: confinement must leave that file as it is.
static int CanaryIntact(const char* basePath)
{
	char path[1024];
	snprintf(path, sizeof(path), "%s/../canary.log", basePath);
	FILE* file = fopen(path, "rb");
	if (!file)
		return 0;
	char content[32] = { 0 };
	size_t length = fread(content, 1, sizeof(content) - 1, file);
	fclose(file);
	return length >= 6 && length <= 8 && memcmp(content, "canary", 6) == 0;
}

int main(int argc, char** argv)
{
	const char* basePath = argc > 1 ? argv[1] : ".";
	const char* table = argc > 2 ? argv[2] : "";
	const char* rom = argc > 3 ? argv[3] : "";

	CHECK(vpe_dof_abi_version() == VPE_DOF_ABI_VERSION, "ABI version");
	printf("libdof %s\n", vpe_dof_libdof_version());
	vpe_dof_set_log(OnLog, NULL, VPE_DOF_LOG_INFO);

	printf("rom rows: %d\n", vpe_dof_rom_names(basePath, table, OnLine, "rom"));
	vpe_dof_enumerate_devices(OnLine, "device");

	int checkCanary = CanaryIntact(basePath);
	RunSession(basePath, table, rom);
	RunSession(basePath, table, rom);
	if (checkCanary)
		CHECK(CanaryIntact(basePath), "the log file outside the configuration is untouched");

	vpe_dof_set_log(NULL, NULL, VPE_DOF_LOG_INFO);
	if (failures)
	{
		fprintf(stderr, "%d check(s) failed\n", failures);
		return 1;
	}
	printf("ok\n");
	return 0;
}
