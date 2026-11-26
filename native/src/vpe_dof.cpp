// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

// C ABI over libdof. Drives libdof's Pinball directly instead of its DOF class, so the host chooses the global config
// path and can use the output gate, port tests and file confinement added by native/patches.

#include "vpe_dof.h"

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <exception>
#include <mutex>
#include <string>
#include <thread>
#include <typeinfo>
#include <unordered_map>
#include <vector>

#if defined(__GNUC__) || defined(__clang__)
#include <cxxabi.h>
#include <cstdlib>
#endif

#include "DOF/Config.h"
#include "DOF/DOF.h"
#include "Log.h"
#include "Pinball.h"
#include "cab/Cabinet.h"
#include "cab/out/IOutputController.h"
#include "cab/out/OutputControllerList.h"
#include "cab/out/OutputGate.h"
#include "cab/out/OutputList.h"
#include "cab/toys/IToy.h"
#include "cab/toys/ToyList.h"
#include "cab/toys/lwequivalent/LedWizEquivalent.h"
#include "general/FileInfo.h"
#include "general/IOConfigurator.h"
#include "general/Path.h"
#include "general/PathConfinement.h"
#include "globalconfiguration/GlobalConfig.h"
#include "ledcontrol/loader/LedControlConfig.h"
#include "ledcontrol/loader/LedControlConfigList.h"
#include "ledcontrol/loader/TableConfig.h"
#include "ledcontrol/loader/TableConfigList.h"

namespace
{

enum class State
{
	Created,
	Running,
	Finishing,
	Finished,
};

}

struct vpe_dof
{
	DOF::Pinball* pinball = nullptr;
	// The gate of the session this instance began. It outlives the instance (OutputGate::BeginSession).
	DOF::OutputGate* gate = nullptr;
	std::string basePath;
	std::atomic<State> state { State::Created };
	std::mutex lifecycle;
};

namespace
{

// Guards the single instance and the process-wide state of libdof.
std::mutex g_processLock;
vpe_dof* g_instance = nullptr;

// The host's log callback and its user pointer, swapped together. Replaced targets are never freed: a libdof thread
// may be delivering a line to one.
struct LogTarget
{
	vpe_dof_log_fn callback;
	void* user;
};
std::atomic<const LogTarget*> g_logTarget { nullptr };
// Lines being delivered, so vpe_dof_set_log can wait until none still uses the target it replaced.
std::atomic<int32_t> g_logCalls { 0 };
thread_local bool t_inLogCallback = false;
std::atomic<int32_t> g_logLevel { VPE_DOF_LOG_INFO };

// Lower is more severe. libdof's levels are not ordered by severity (INFO 0, WARN 1, ERROR 2, DEBUG 3).
int Severity(int32_t level)
{
	switch (level)
	{
	case VPE_DOF_LOG_ERROR: return 0;
	case VPE_DOF_LOG_WARN: return 1;
	case VPE_DOF_LOG_INFO: return 2;
	default: return 3;
	}
}

// Copies the message, cut at a character boundary if the buffer is too small.
void WriteError(char* buffer, int32_t size, const char* message)
{
	if (!buffer || size <= 0)
		return;
	const char* text = message ? message : "";
	size_t length = std::strlen(text);
	size_t capacity = static_cast<size_t>(size) - 1;
	if (length > capacity)
	{
		length = capacity;
		while (length > 0 && (static_cast<unsigned char>(text[length]) & 0xC0) == 0x80)
			length--;
	}
	std::memcpy(buffer, text, length);
	buffer[length] = '\0';
}

void LIBDOFCALLBACK OnLibdofLog(DOF_LogLevel level, const char* format, va_list args);

// libdof buffers log lines in memory until its log is initialized. It never gets a log file here, so mark the log as
// initialized without one before anything logs. libdof's log callback and level are set once and for all to a
// trampoline that filters by severity, so libdof's unsynchronized logger fields are never written while it logs.
void QuietLogFile()
{
	static std::once_flag once;
	std::call_once(once, []() {
		DOF::Log::Init(false);
		DOF::Config* config = DOF::Config::GetInstance();
		config->SetLogLevel(DOF_LogLevel_DEBUG);
		config->SetLogCallback(&OnLibdofLog);
	});
}

void LIBDOFCALLBACK OnLibdofLog(DOF_LogLevel level, const char* format, va_list args)
{
	// counted before the target is read: a caller that replaces it and then sees no line in flight knows none has the old one
	g_logCalls.fetch_add(1, std::memory_order_seq_cst);
	struct Leave
	{
		~Leave() { g_logCalls.fetch_sub(1, std::memory_order_release); }
	} leave;
	const LogTarget* target = g_logTarget.load(std::memory_order_seq_cst);
	if (!target || !target->callback || !format)
		return;
	if (Severity(static_cast<int32_t>(level)) > Severity(g_logLevel.load(std::memory_order_relaxed)))
		return;
	vpe_dof_log_fn callback = target->callback;
	void* user = target->user;
	char stackBuffer[1024];
	va_list copy;
	va_copy(copy, args);
	int length = std::vsnprintf(stackBuffer, sizeof(stackBuffer), format, copy);
	va_end(copy);
	if (length < 0)
		return;
	t_inLogCallback = true;
	try
	{
		if (static_cast<size_t>(length) < sizeof(stackBuffer))
		{
			callback(static_cast<int32_t>(level), stackBuffer, user);
		}
		else
		{
			std::vector<char> heapBuffer(static_cast<size_t>(length) + 1);
			std::vsnprintf(heapBuffer.data(), heapBuffer.size(), format, args);
			callback(static_cast<int32_t>(level), heapBuffer.data(), user);
		}
	}
	catch (...)
	{
	}
	t_inLogCallback = false;
}

std::string GlobalConfigPath(const std::string& basePath) { return DOF::Path::Combine(basePath, "directoutputconfig", "GlobalConfig_B2SServer.xml"); }

}

// Serializes hidapi's process-wide init and exit against device enumeration (vpe_dof_devices.cpp).
std::mutex& VpeDofHidLock()
{
	static std::mutex lock;
	return lock;
}

namespace
{

std::string Sanitize(std::string value)
{
	std::replace(value.begin(), value.end(), '|', '/');
	std::replace(value.begin(), value.end(), '\n', ' ');
	std::replace(value.begin(), value.end(), '\r', ' ');
	return value;
}

// The class name of a toy ("LedWizEquivalent"), from its dynamic type.
std::string TypeName(const DOF::IToy& toy)
{
	std::string name = typeid(toy).name();
#if defined(__GNUC__) || defined(__clang__)
	int status = 0;
	char* demangled = abi::__cxa_demangle(name.c_str(), nullptr, nullptr, &status);
	if (demangled)
	{
		if (status == 0)
			name = demangled;
		std::free(demangled);
	}
#endif
	for (const char* prefix : { "class ", "struct " })
	{
		if (name.rfind(prefix, 0) == 0)
			name = name.substr(std::char_traits<char>::length(prefix));
	}
	size_t ns = name.rfind("::");
	return ns == std::string::npos ? name : name.substr(ns + 2);
}

bool IsRunning(vpe_dof* dof) { return dof && dof->state.load(std::memory_order_acquire) == State::Running; }

// Called with dof->lifecycle held.
int32_t FinishLocked(vpe_dof* dof, char* error, int32_t errorSize)
{
	State state = dof->state.load();
	if (state == State::Finished)
		return VPE_DOF_OK;
	if (state == State::Created)
	{
		dof->state = State::Finished;
		return VPE_DOF_OK;
	}
	dof->state = State::Finishing;
	int32_t status = VPE_DOF_OK;
	try
	{
		dof->pinball->Finish();
	}
	catch (const std::exception& e)
	{
		WriteError(error, errorSize, e.what());
		status = VPE_DOF_ERROR;
	}
	catch (...)
	{
		WriteError(error, errorSize, "libdof failed to finish");
		status = VPE_DOF_ERROR;
	}
	try
	{
		DOF::Log::Finish();
		DOF::Log::Init(false);
	}
	catch (...)
	{
	}
	dof->state = State::Finished;
	return status;
}

}

extern "C" {

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_abi_version(void) { return VPE_DOF_ABI_VERSION; }

VPE_DOF_API const char* VPE_DOF_CALL vpe_dof_libdof_version(void) { return LIBDOF_VERSION; }

VPE_DOF_API void VPE_DOF_CALL vpe_dof_set_log(vpe_dof_log_fn callback, void* user, int32_t level)
{
	try
	{
		QuietLogFile();
		g_logLevel.store(std::clamp<int32_t>(level, VPE_DOF_LOG_INFO, VPE_DOF_LOG_DEBUG), std::memory_order_relaxed);
		g_logTarget.store(callback ? new LogTarget { callback, user } : nullptr, std::memory_order_seq_cst);
		// Lines that read the previous target finish before this returns, so the caller can unload what it pointed to: a
		// line counts itself before it reads the target, so once the count is zero after the store, every later line reads
		// the new one. Without a deadline, since one would let a stalled line call into what was unloaded; the callback
		// must not block. Not from inside a callback, which would wait for itself.
		if (!t_inLogCallback)
		{
			while (g_logCalls.load(std::memory_order_seq_cst) != 0)
				std::this_thread::yield();
		}
	}
	catch (...)
	{
	}
}

VPE_DOF_API vpe_dof* VPE_DOF_CALL vpe_dof_create(const char* base_path, int32_t* status)
{
	auto setStatus = [status](int32_t value) {
		if (status)
			*status = value;
	};
	if (!base_path || !*base_path)
	{
		setStatus(VPE_DOF_INVALID_ARGUMENT);
		return nullptr;
	}
	vpe_dof* dof = nullptr;
	bool ioInitialized = false;
	try
	{
		std::lock_guard<std::mutex> lock(g_processLock);
		if (g_instance)
		{
			setStatus(VPE_DOF_BUSY);
			return nullptr;
		}
		try
		{
			QuietLogFile();
			dof = new vpe_dof();
			dof->basePath = base_path;
			DOF::Config::GetInstance()->SetBasePath(base_path);
			DOF::PathConfinement::SetRoot(base_path);
			// A configuration that cannot be used fails init instead of falling back to defaults or auto-configuring
			// whatever hardware is connected.
			DOF::Config::GetInstance()->SetStrictSetup(true);
			dof->gate = &DOF::OutputGate::BeginSession();
			{
				std::lock_guard<std::mutex> hid(VpeDofHidLock());
				DOF::IOConfigurator::Initialize();
			}
			ioInitialized = true;
			dof->pinball = new DOF::Pinball();
		}
		catch (...)
		{
			if (dof)
			{
				delete dof->pinball;
				delete dof;
			}
			if (ioInitialized)
			{
				try
				{
					std::lock_guard<std::mutex> hid(VpeDofHidLock());
					DOF::IOConfigurator::Shutdown();
				}
				catch (...)
				{
				}
			}
			setStatus(VPE_DOF_ERROR);
			return nullptr;
		}
		g_instance = dof;
		setStatus(VPE_DOF_OK);
		return dof;
	}
	catch (...)
	{
		setStatus(VPE_DOF_ERROR);
		return nullptr;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_init(vpe_dof* dof, const char* table_file, const char* rom_name, char* error, int32_t error_size)
{
	if (!dof)
		return VPE_DOF_INVALID_ARGUMENT;
	try
	{
		std::lock_guard<std::mutex> lock(dof->lifecycle);
		if (dof->state.load() != State::Created)
		{
			WriteError(error, error_size, "init was already called");
			return VPE_DOF_INVALID_STATE;
		}
		try
		{
			// Without its global config libdof would fall back to defaults and auto-configure the cabinet from whatever
			// hardware is connected. The host always writes one, so a missing file is an error.
			const std::string globalConfig = GlobalConfigPath(dof->basePath);
			if (!DOF::FileInfo(globalConfig).Exists())
			{
				WriteError(error, error_size, ("the configuration has no " + globalConfig).c_str());
				dof->state = State::Finished;
				return VPE_DOF_ERROR;
			}
			dof->pinball->Setup(globalConfig, table_file ? table_file : "", rom_name ? rom_name : "");
			dof->pinball->Init();
			dof->state = State::Running;
			return VPE_DOF_OK;
		}
		catch (const std::exception& e)
		{
			WriteError(error, error_size, e.what());
		}
		catch (...)
		{
			WriteError(error, error_size, "libdof failed to initialize");
		}
		// Init may have started controller threads before it failed: stop what was started.
		dof->state = State::Running;
		FinishLocked(dof, nullptr, 0);
		return VPE_DOF_ERROR;
	}
	catch (...)
	{
		WriteError(error, error_size, "libdof failed to initialize");
		return VPE_DOF_ERROR;
	}
}

VPE_DOF_API void VPE_DOF_CALL vpe_dof_data(vpe_dof* dof, char type, int32_t number, int32_t value)
{
	if (!IsRunning(dof))
		return;
	try
	{
		dof->pinball->ReceiveData(type, number, value);
	}
	catch (...)
	{
	}
}

VPE_DOF_API void VPE_DOF_CALL vpe_dof_data_named(vpe_dof* dof, const char* name, int32_t value)
{
	if (!IsRunning(dof) || !name)
		return;
	try
	{
		dof->pinball->ReceiveData(std::string(name), value);
	}
	catch (...)
	{
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_pending_input(vpe_dof* dof)
{
	if (!IsRunning(dof))
		return 0;
	try
	{
		return dof->pinball->GetPendingInputCount();
	}
	catch (...)
	{
		return 0;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_finish(vpe_dof* dof, char* error, int32_t error_size)
{
	if (!dof)
		return VPE_DOF_INVALID_ARGUMENT;
	try
	{
		std::lock_guard<std::mutex> lock(dof->lifecycle);
		return FinishLocked(dof, error, error_size);
	}
	catch (...)
	{
		WriteError(error, error_size, "libdof failed to finish");
		return VPE_DOF_ERROR;
	}
}

VPE_DOF_API void VPE_DOF_CALL vpe_dof_destroy(vpe_dof* dof)
{
	if (!dof)
		return;
	try
	{
		{
			std::lock_guard<std::mutex> lock(dof->lifecycle);
			FinishLocked(dof, nullptr, 0);
		}
		std::lock_guard<std::mutex> lock(g_processLock);
		try
		{
			delete dof->pinball;
		}
		catch (...)
		{
		}
		dof->pinball = nullptr;
		try
		{
			std::lock_guard<std::mutex> hid(VpeDofHidLock());
			DOF::IOConfigurator::Shutdown();
		}
		catch (...)
		{
		}
		if (g_instance == dof)
			g_instance = nullptr;
		delete dof;
	}
	catch (...)
	{
	}
}

VPE_DOF_API int64_t VPE_DOF_CALL vpe_dof_set_output_gate(vpe_dof* dof, int32_t open)
{
	if (!dof)
		return -1;
	try
	{
		if (dof->state.load() == State::Running)
			return dof->pinball->SetOutputGate(open != 0);
		// Never waits for a running init or finish.
		std::unique_lock<std::mutex> lock(dof->lifecycle, std::try_to_lock);
		if (!lock.owns_lock() || dof->state.load() != State::Created)
			return -1;
		// No outputs exist yet: the state applies to everything init creates.
		DOF::OutputGate& gate = *dof->gate;
		int64_t generation = gate.Request(open != 0);
		gate.Apply(open != 0);
		gate.Publish(generation);
		return generation;
	}
	catch (...)
	{
		return -1;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_set_connect_tests(vpe_dof* dof, int32_t allowed)
{
	if (!dof)
		return VPE_DOF_INVALID_ARGUMENT;
	try
	{
		// The session that create began allows them, so this lasts for the instance.
		dof->gate->SetConnectTests(allowed != 0);
		return VPE_DOF_OK;
	}
	catch (...)
	{
		return VPE_DOF_ERROR;
	}
}

VPE_DOF_API int64_t VPE_DOF_CALL vpe_dof_latch_output_gate(vpe_dof* dof)
{
	if (!dof)
		return -1;
	try
	{
		// Latches the instance's session at once in every state, without DOF's main thread: the controllers' updaters send
		// the closing. Init keeps it (the session began at create), and finish turns everything off anyway. A later
		// instance has a session of its own, which this never reaches.
		return dof->gate->Latch();
	}
	catch (...)
	{
		return -1;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_gate_state(vpe_dof* dof, int64_t generation, int32_t* confirmed, int32_t* pending, int32_t* failed, int32_t* untouched)
{
	if (confirmed)
		*confirmed = 0;
	if (pending)
		*pending = 0;
	if (failed)
		*failed = 0;
	if (untouched)
		*untouched = 0;
	if (!dof || generation < 0)
		return 0;
	try
	{
		DOF::OutputGate& gate = *dof->gate;
		DOF::OutputGate::Counts counts;
		DOF::OutputGate::Status status = gate.GetState(generation, counts);
		if (confirmed)
			*confirmed = counts.confirmed;
		if (pending)
			*pending = counts.pending;
		if (failed)
			*failed = counts.failed;
		if (untouched)
			*untouched = counts.untouched;
		switch (status)
		{
		case DOF::OutputGate::Status::Applied: return 1;
		case DOF::OutputGate::Status::Superseded: return 2;
		default: return 0;
		}
	}
	catch (...)
	{
		return 0;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_gate_pending(vpe_dof* dof, int64_t generation, vpe_dof_string_fn callback, void* user)
{
	if (!dof || !callback || generation < 0)
		return VPE_DOF_INVALID_ARGUMENT;
	try
	{
		for (const std::string& name : dof->gate->GetPending(generation))
			callback(Sanitize(name).c_str(), user);
		return VPE_DOF_OK;
	}
	catch (...)
	{
		return VPE_DOF_ERROR;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_test_output(vpe_dof* dof, int32_t device, int32_t port, int32_t value)
{
	if (!IsRunning(dof))
		return VPE_DOF_INVALID_STATE;
	try
	{
		DOF::Cabinet* cabinet = dof->pinball->GetCabinet();
		DOF::ToyList* toys = cabinet ? cabinet->GetToys() : nullptr;
		if (!toys)
			return VPE_DOF_INVALID_ARGUMENT;
		// The cabinet's toys and their outputs are fixed after init, so the lookup can run on this thread.
		DOF::IOutput* output = nullptr;
		for (DOF::IToy* toy : *toys)
		{
			auto* lwe = dynamic_cast<DOF::LedWizEquivalent*>(toy);
			if (lwe && lwe->GetLedWizNumber() == device)
			{
				output = lwe->GetOutput(port);
				break;
			}
		}
		if (!output)
			return VPE_DOF_INVALID_ARGUMENT;
		uint8_t v = static_cast<uint8_t>(std::clamp<int32_t>(value, 0, 255));
		return dof->pinball->Post([output, v]() { output->SetOutput(v); }) ? VPE_DOF_OK : VPE_DOF_INVALID_STATE;
	}
	catch (...)
	{
		return VPE_DOF_ERROR;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_describe(vpe_dof* dof, vpe_dof_string_fn callback, void* user)
{
	if (!dof || !callback)
		return VPE_DOF_INVALID_ARGUMENT;
	if (!IsRunning(dof))
		return VPE_DOF_INVALID_STATE;
	try
	{
		DOF::Cabinet* cabinet = dof->pinball->GetCabinet();
		if (!cabinet)
			return VPE_DOF_INVALID_STATE;
		if (DOF::OutputControllerList* controllers = cabinet->GetOutputControllers())
		{
			for (DOF::IOutputController* controller : *controllers)
			{
				if (!controller)
					continue;
				const DOF::OutputList* outputs = controller->GetOutputs();
				std::string line = "controller|" + Sanitize(controller->GetXmlElementName()) + "|" + Sanitize(controller->GetName()) + "|"
					+ std::to_string(outputs ? outputs->size() : 0);
				callback(line.c_str(), user);
			}
		}
		if (DOF::ToyList* toys = cabinet->GetToys())
		{
			for (DOF::IToy* toy : *toys)
			{
				if (!toy)
					continue;
				std::string line = "toy|" + Sanitize(TypeName(*toy)) + "|" + Sanitize(toy->GetName());
				callback(line.c_str(), user);
			}
		}
		return VPE_DOF_OK;
	}
	catch (...)
	{
		return VPE_DOF_ERROR;
	}
}

VPE_DOF_API int32_t VPE_DOF_CALL vpe_dof_rom_names(const char* base_path, const char* table_file, vpe_dof_string_fn callback, void* user)
{
	if (!base_path || !*base_path || !callback)
		return -1;
	DOF::GlobalConfig* globalConfig = nullptr;
	DOF::LedControlConfigList* configs = nullptr;
	try
	{
		QuietLogFile();
		DOF::PathConfinement::ThreadRoot confinement(base_path);
		std::string globalConfigPath = GlobalConfigPath(base_path);
		if (DOF::FileInfo(globalConfigPath).Exists())
			globalConfig = DOF::GlobalConfig::GetGlobalConfigFromConfigXmlFile(DOF::FileInfo(globalConfigPath).FullName());
		if (!globalConfig)
			globalConfig = new DOF::GlobalConfig();
		globalConfig->SetGlobalConfigFilename(globalConfigPath);

		std::unordered_map<int, DOF::FileInfo> iniFiles = globalConfig->GetIniFilesDictionary(table_file ? table_file : "");
		configs = new DOF::LedControlConfigList();
		if (!iniFiles.empty())
			configs->LoadLedControlFiles(iniFiles, false);

		int32_t count = 0;
		for (DOF::LedControlConfig* config : *configs)
		{
			DOF::TableConfigList* tables = config ? config->GetTableConfigurations() : nullptr;
			if (!tables)
				continue;
			for (size_t i = 0; i < tables->Size(); i++)
			{
				std::string line = std::to_string(config->GetLedWizNumber()) + "|" + Sanitize((*tables)[i]->GetShortRomName());
				callback(line.c_str(), user);
				count++;
			}
		}
		delete configs;
		delete globalConfig;
		return count;
	}
	catch (...)
	{
		delete configs;
		delete globalConfig;
		return -1;
	}
}

}
