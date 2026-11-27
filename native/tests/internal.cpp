// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

// Tests of libdof's internals that no configuration reaches through the C ABI: effects that were never started, a toy
// that fails while an effect starts, schedules, the order of host commands and input, path confinement, and the output
// gate: its accounting and sessions, with the steps other threads can come between run one at a time, and a scripted
// controller whose writes a latch overtakes or whose device is lost. Exits non-zero if a check fails; a crash is a
// failure too.

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <random>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "Pinball.h"
#include "cab/Cabinet.h"
#include "cab/out/Output.h"
#include "cab/out/OutputControllerCompleteBase.h"
#include "cab/out/OutputControllerList.h"
#include "cab/out/OutputGate.h"
#include "cab/schedules/ScheduledSettings.h"
#include "cab/toys/IMatrixToy.h"
#include "cab/toys/ToyList.h"
#include "DOF/Config.h"
#include "fx/EffectBase.h"
#include "fx/EffectList.h"
#include "fx/matrixfx/RGBAMatrixShiftEffect.h"
#include "fx/timmedfx/DelayEffect.h"
#include "general/FileInfo.h"
#include "general/FilePattern.h"
#include "general/FileReader.h"
#include "general/PathConfinement.h"
#include "globalconfiguration/GlobalConfig.h"
#include "general/color/RGBAColor.h"
#include "pinballsupport/InputQueue.h"
#include "table/Table.h"
#include "table/TableElementData.h"

#include <tinyxml2/tinyxml2.h>
#ifdef _WIN32
#include <windows.h>
#endif

using namespace DOF;

namespace DOF
{

// Runs the steps of the gate that other threads can come between, one at a time.
class OutputGateTestAccess
{
public:
	// Request, with a latch on another thread between reserving the generation and recording it.
	static int64_t RequestWithLatchInBetween(OutputGate& gate, bool open, int64_t& latch)
	{
		std::lock_guard<std::mutex> lock(gate.m_mutex);
		bool latched;
		int64_t generation = gate.Reserve(latched);
		latch = gate.Latch();
		OutputGate::Entry entry;
		entry.open = open && !latched;
		gate.Record(generation, entry);
		return generation;
	}

	// The two halves of a latch: closes and takes its generation, then publishes it.
	static int64_t BeginLatch(OutputGate& gate) { return gate.BeginLatch(); }
	static void FinishLatch(OutputGate& gate, int64_t generation) { gate.FinishLatch(generation); }
};

// Queues host commands the way Pinball::Post does and takes the main thread's work one item at a time.
class PinballTestAccess
{
public:
	static void Post(Pinball& pinball)
	{
		std::lock_guard<std::mutex> lock(pinball.m_mainThreadMutex);
		pinball.m_commands.push_back({ pinball.m_inputQueue->Count(), [] { } });
	}

	// 'C' for a command, 'I' for input, '-' for nothing. `drain`: as the main thread takes its work once asked to stop
	// (DrainCommands), only what the queued commands wait for.
	static std::string Take(Pinball& pinball, int count, bool drain = false)
	{
		std::string order;
		for (int i = 0; i < count; i++)
		{
			std::function<void()> command;
			TableElementData data;
			Pinball::Work work = pinball.TakeWork(drain, command, data);
			order += work == Pinball::Work::Command ? 'C' : work == Pinball::Work::Input ? 'I' : '-';
		}
		return order;
	}

	// Applies a gate change as DOF's main thread runs it, through RunCommand.
	static void Apply(Pinball& pinball, bool open, int64_t generation)
	{
		std::function<void()> command = [&pinball, open, generation]() { pinball.ApplyOutputGate(open, generation); };
		pinball.RunCommand(command);
	}

	// What Finish does first.
	static void Stop(Pinball& pinball)
	{
		std::lock_guard<std::mutex> lock(pinball.m_mainThreadMutex);
		pinball.m_stopping = true;
	}
};

}

static int failures = 0;

#define CHECK(condition, message)                                                                                      \
	do                                                                                                                 \
	{                                                                                                                  \
		if (!(condition))                                                                                              \
		{                                                                                                              \
			std::fprintf(stderr, "FAIL: %s\n", message);                                                               \
			failures++;                                                                                                \
		}                                                                                                              \
	} while (0)

namespace
{

// A matrix toy that cannot give a layer, as one whose layer does not fit in memory.
class FailingMatrix : public IMatrixToy<RGBAColor>
{
public:
	void Init(Cabinet*) override { }
	void Reset() override { }
	void Finish() override { }
	RGBAColor* GetLayer(int) override { throw std::runtime_error("no layer"); }
	int GetHeight() const override { return 1; }
	int GetWidth() const override { return 1; }
	RGBAColor GetElement(int, int, int) override { return RGBAColor(); }
	void SetElement(int, int, int, const RGBAColor&) override { }
};

// A table whose start failed before it started its effects (a cabinet that failed to start) deletes them unstarted.
void EffectsThatNeverStartedAreNotFinished()
{
	Table* table = new Table();
	DelayEffect* delay = new DelayEffect();
	delay->SetName("delay");
	table->GetEffects()->Add(delay);
	delete table;

	EffectList* effects = new EffectList();
	DelayEffect* other = new DelayEffect();
	other->SetName("delay");
	effects->Add(other);
	effects->Finish();
	delete effects;
}

// An effect whose toy fails while it starts is finished with the table, which it has to know by then.
void MatrixEffectWhoseToyFailsIsFinished()
{
	Pinball pinball;
	FailingMatrix* toy = new FailingMatrix();
	toy->SetName("matrix");
	pinball.GetCabinet()->GetToys()->AddToy(toy);

	Table table;
	RGBAMatrixShiftEffect* shift = new RGBAMatrixShiftEffect();
	shift->SetName("shift");
	shift->SetToyName("matrix");
	table.GetEffects()->Add(static_cast<EffectBase*>(shift));

	bool failed = false;
	try
	{
		table.Init(&pinball);
	}
	catch (const std::exception&)
	{
		failed = true;
	}
	CHECK(failed, "the effect whose toy fails does not start");
	table.Finish();
}

// The names GetPending returns are the units GetState counts as pending or failed.
bool NamesAgree(OutputGate& gate, int64_t generation)
{
	OutputGate::Counts counts;
	gate.GetState(generation, counts);
	return gate.GetPending(generation).size() == static_cast<size_t>(counts.pending + counts.failed);
}

// A change applied and published the way DOF's main thread does it.
int64_t Change(OutputGate& gate, bool open)
{
	int64_t generation = gate.Request(open);
	gate.Apply(open);
	gate.Publish(generation);
	return generation;
}

bool WaitFor(const std::function<bool()>& condition)
{
	auto start = std::chrono::steady_clock::now();
	while (!condition())
	{
		if (std::chrono::steady_clock::now() - start > std::chrono::seconds(2))
			return false;
		std::this_thread::sleep_for(std::chrono::milliseconds(5));
	}
	return true;
}

// A controller whose device takes one report per output, and logs what got to it. A hook runs before each report.
class ScriptedController : public OutputControllerCompleteBase
{
public:
	std::string GetXmlElementName() const override { return "ScriptedController"; }

	void Set(uint8_t first, uint8_t second)
	{
		uint8_t values[2] = { first, second };
		SetValues(0, values, 2);
		Update();
	}

	void Note(const std::string& what)
	{
		std::lock_guard<std::mutex> lock(m_logMutex);
		m_log.push_back(what);
	}

	std::vector<std::string> Written()
	{
		std::lock_guard<std::mutex> lock(m_logMutex);
		return m_log;
	}

	bool HasWritten(const std::string& what)
	{
		std::vector<std::string> log = Written();
		return std::find(log.begin(), log.end(), what) != log.end();
	}

	std::function<void(int, const std::vector<uint8_t>&)> beforeReport;
	std::function<void()> onDisconnect;
	std::atomic<bool> failNextWrite { false };
	std::atomic<bool> failReconnect { false };
	std::atomic<bool> blackoutWorks { true };
	std::atomic<int> connects { 0 };
	std::atomic<int> blackouts { 0 };
	// The connect sends a connect test as the Wemos D1 controller does: decided by TakeConnectTest, and checked again
	// at the write. failAfterTest makes the connect fail once the device took it (the controller did not answer);
	// beforeTest runs between the decision and the write (the log line). handshakeFailsOnce makes the first connect
	// return before the test, as a failed Teensy handshake does, keeping testOnConnect for a reconnect.
	std::atomic<bool> testOnConnect { false };
	std::atomic<bool> failAfterTest { false };
	std::atomic<bool> handshakeFailsOnce { false };
	std::function<void()> beforeTest;
	// Runs at the start of the first connect, before the updater observes the gate for the first time.
	std::function<void()> onFirstConnect;
	// Every write fails while it is set; reconnects still work.
	std::atomic<bool> failWrites { false };

protected:
	int GetNumberOfConfiguredOutputs() override { return 2; }
	bool VerifySettings() override { return true; }

	void ConnectToController() override
	{
		int attempt = connects++;
		if (attempt == 0 && onFirstConnect)
			onFirstConnect();
		if (attempt > 0 && failReconnect)
			throw std::runtime_error("the device is gone");
		if (attempt == 0 && handshakeFailsOnce)
			return;
		bool test = testOnConnect && TakeConnectTest();
		testOnConnect = false;
		if (!test)
			return;
		if (beforeTest)
			beforeTest();
		OutputGate::Snapshot gate = ObserveGate();
		if (!gate.open)
			return;
		OutputGate::Transfer transfer(GateUnit(), gate);
		if (TransferCancelled())
			return;
		Note("test");
		ConnectTestSent(gate);
		if (failAfterTest)
			throw std::runtime_error("the device does not answer");
	}

	void DisconnectFromController() override
	{
		if (onDisconnect)
			onDisconnect();
		if (m_inUseState != InUseState::Running)
			return;
		blackouts++;
		m_disconnectTurnedOff = blackoutWorks;
		Note("blackout");
	}

	void UpdateOutputs(const std::vector<uint8_t>& values) override
	{
		if (failNextWrite.exchange(false) || failWrites)
			throw std::runtime_error("the write failed");
		for (int i = 0; i < 2 && i < static_cast<int>(values.size()); i++)
		{
			if (beforeReport)
				beforeReport(i, values);
			if (TransferCancelled())
				return;
			Note(std::to_string(i) + "=" + std::to_string(values[i]));
		}
	}

private:
	std::mutex m_logMutex;
	std::vector<std::string> m_log;
};

void UntouchedUnitsAreCountedApart()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle used = gate.Register("used");
	OutputGate::UnitHandle unused = gate.Register("unused");

	int64_t open = gate.Request(true);
	CHECK(gate.Apply(true), "the gate opens");
	gate.Publish(open);
	OutputGate::Snapshot snapshot = gate.Current();
	used->Ack(snapshot);
	unused->Untouched(snapshot);
	OutputGate::Counts counts;
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Applied, "the opening is applied");
	CHECK(counts.confirmed == 1 && counts.untouched == 1 && counts.pending == 0 && counts.failed == 0, "a unit DOF leaves alone is untouched, not confirmed");
	CHECK(gate.GetPending(open).empty(), "an untouched unit is not pending");

	int64_t closed = Change(gate, false);
	snapshot = gate.Current();
	used->Ack(snapshot);
	unused->Untouched(snapshot);
	CHECK(gate.GetState(closed, counts) == OutputGate::Status::Applied, "the closing is applied");
	CHECK(counts.confirmed == 1 && counts.untouched == 0 && counts.pending == 1, "a closing goes to every unit");
	std::vector<std::string> pending = gate.GetPending(closed);
	CHECK(pending.size() == 1 && pending[0] == "unused", "the unused unit has to write the closing");
}

void LatchOvertakesAQueuedOpening()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	Change(gate, false);
	unit->Ack(gate.Current());

	// The opening is queued for DOF's main thread when the latch comes; the main thread applies it afterwards.
	int64_t open = gate.Request(true);
	int64_t latched = gate.Latch();
	OutputGate::Counts counts;
	CHECK(latched > open, "the latch gets a later generation");
	CHECK(!gate.IsOpen(), "the latch closes at once");
	CHECK(gate.GetState(open, counts) == OutputGate::Status::NotApplied, "the overtaken opening is not applied until DOF's main thread gets to it");
	CHECK(!gate.Apply(true), "an opening applied after the latch is a closing");
	CHECK(!gate.IsOpen(), "and leaves the gate closed");
	gate.Publish(open);
	OutputGate::Snapshot snapshot = gate.Current();
	CHECK(snapshot.generation == latched && !snapshot.open, "the overtaken opening is not published");

	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "then it is superseded");
	unit->Ack(snapshot);
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the latch is confirmed");
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "superseded stays superseded");

	int64_t later = gate.Request(true);
	CHECK(!gate.Apply(true), "a later opening is a closing");
	gate.Publish(later);
	unit->Ack(gate.Current());
	CHECK(gate.GetState(later, counts) == OutputGate::Status::Applied && counts.confirmed == 1 && counts.untouched == 0, "and confirms the closed state");

	OutputGate& next = OutputGate::BeginSession();
	CHECK(next.IsOpen() && !next.IsLatched(), "a new session is not latched");
	CHECK(gate.IsLatched(), "the ended one stays latched");
}

void OpeningReservedBeforeALatchIsSuperseded()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	Change(gate, false);
	unit->Ack(gate.Current());

	int64_t latched = 0;
	int64_t open = OutputGateTestAccess::RequestWithLatchInBetween(gate, true, latched);
	CHECK(latched > open, "the latch comes after the opening's generation");
	CHECK(!gate.Apply(true), "the opening applies as a closing");
	gate.Publish(open);
	unit->Ack(gate.Current());

	OutputGate::Counts counts;
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "the opening the latch overtook is superseded");
	CHECK(counts.confirmed == 0, "and does not claim the latch's writes");
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the latch is confirmed");
	CHECK(NamesAgree(gate, open) && NamesAgree(gate, latched), "names agree with the counts");
}

// The main thread publishes an opening while a latch is half done (its generation taken, not published yet): it goes
// out as a closing, and what it reports does not change when the latch is done.
void OpeningPublishedDuringALatchIsSuperseded()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t open = gate.Request(true);
	int64_t latched = OutputGateTestAccess::BeginLatch(gate);
	CHECK(!gate.Apply(true), "the opening applies as a closing");
	gate.Publish(open);
	OutputGate::Snapshot snapshot = gate.Current();
	CHECK(snapshot.generation == open && !snapshot.open, "it is published closed");
	OutputGate::Counts counts;
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "it is superseded");
	OutputGateTestAccess::FinishLatch(gate, latched);
	unit->Ack(gate.Current());
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded && counts.confirmed == 0, "it is still superseded");
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the latch is confirmed");
}

// What a change reports is decided when it is published, from what was published: an opening published before a
// latch is applied while the latch is half done, and superseded once the latch reached the units; the latch is not
// applied until it published itself.
void ResultsAreDecidedWhenPublished()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t open = Change(gate, true);
	unit->Ack(gate.Current());
	OutputGate::Counts counts;
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the opening is applied");
	int64_t latched = OutputGateTestAccess::BeginLatch(gate);
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "a latch that is half done changes nothing reported");
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::NotApplied, "nor is it applied");
	CHECK(!gate.Current().open, "but the units send the closing already");
	OutputGateTestAccess::FinishLatch(gate, latched);
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied, "a latch publishes itself");
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "the opening is superseded once the latch reached the units");
	unit->Ack(gate.Current());
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the latch is confirmed");
}

void EarliestLatchDecides()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t first = OutputGateTestAccess::BeginLatch(gate);
	int64_t second = OutputGateTestAccess::BeginLatch(gate);
	CHECK(second > first, "latches take their generations in order");
	OutputGateTestAccess::FinishLatch(gate, second);
	OutputGateTestAccess::FinishLatch(gate, first);
	unit->Ack(gate.Current());

	OutputGate::Counts counts;
	CHECK(gate.GetState(first, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the earlier latch is confirmed");
	CHECK(gate.GetState(second, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the later latch is confirmed");
}

// An opening requested between two overlapping latches is a closing: what it reports while the first is not done
// stays so.
void OpeningBetweenTwoLatchesIsAClosing()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t first = OutputGateTestAccess::BeginLatch(gate);
	int64_t open = gate.Request(true);
	CHECK(!gate.Apply(true), "the opening applies as a closing");
	gate.Publish(open);
	int64_t second = OutputGateTestAccess::BeginLatch(gate);
	OutputGateTestAccess::FinishLatch(gate, second);
	unit->Ack(gate.Current());
	OutputGate::Counts counts;
	OutputGate::Status before = gate.GetState(open, counts);
	CHECK(before == OutputGate::Status::Applied && counts.confirmed == 1, "the opening is a closing, confirmed");
	OutputGateTestAccess::FinishLatch(gate, first);
	CHECK(gate.GetState(open, counts) == before && counts.confirmed == 1, "and stays so once the first latch is done");
	CHECK(gate.GetState(first, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the first latch is confirmed");
}

// A latch that pauses after taking its generation, while openings (one requested before it, one after) and a closing
// are applied: what they report then stays so once it is done.
void PausedLatchChangesNothingReported()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t earlier = gate.Request(true);
	int64_t first = OutputGateTestAccess::BeginLatch(gate);
	int64_t open = gate.Request(true);
	CHECK(!gate.Apply(true), "the earlier opening applies as a closing");
	gate.Publish(earlier);
	CHECK(!gate.Apply(true), "the later opening applies as a closing");
	gate.Publish(open);
	int64_t closed = Change(gate, false);
	unit->Ack(gate.Current());
	const int64_t changes[] = { earlier, open, closed };
	OutputGate::Status states[3];
	int confirmed[3];
	OutputGate::Counts counts;
	for (int i = 0; i < 3; i++)
	{
		states[i] = gate.GetState(changes[i], counts);
		confirmed[i] = counts.confirmed;
	}
	CHECK(states[0] == OutputGate::Status::Superseded && confirmed[0] == 0, "the earlier opening is superseded");
	CHECK(states[1] == OutputGate::Status::Applied && confirmed[1] == 1, "the later opening is a closing, confirmed");
	OutputGateTestAccess::FinishLatch(gate, first);
	for (int i = 0; i < 3; i++)
		CHECK(gate.GetState(changes[i], counts) == states[i] && counts.confirmed == confirmed[i], "the paused latch changes nothing");
}

// A latch reaches its own session only: an ended session's latch neither closes the current one nor cancels its
// transfers, and a unit keeps the session it registered in.
void LatchesStayInTheirSession()
{
	OutputGate& ended = OutputGate::BeginSession();
	OutputGate::UnitHandle old = ended.Register("old");
	OutputGate& current = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = current.Register("unit");
	CHECK(&OutputGate::Instance() == &current, "the new session is the current one");
	CHECK(old->BelongsTo(ended) && !old->BelongsTo(current), "a unit keeps its session");
	{
		OutputGate::Transfer transfer(unit, unit->Observe());
		ended.Latch();
		CHECK(!OutputGate::TransferCancelled(), "an ended session's latch does not cancel the current session's transfer");
	}
	CHECK(current.IsOpen() && !current.IsLatched() && unit->Observe().open, "nor close it");
	{
		OutputGate::Transfer transfer(old, old->Observe());
		CHECK(!OutputGate::TransferCancelled(), "a transfer of the ended session's closing goes on");
	}
	CHECK(current.Apply(true), "the current session still opens");
}

// A latch overtakes the transfer of an opening at its next write point; a closing's transfer goes on.
void LatchCancelsTheTransferOfAnOpening()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	CHECK(!OutputGate::TransferCancelled(), "nothing to cancel outside a transfer");
	{
		OutputGate::Transfer transfer(unit, unit->Observe());
		CHECK(!OutputGate::TransferCancelled() && !transfer.Cancelled(), "an opening's transfer goes on while the gate is open");
		gate.Latch();
		CHECK(OutputGate::TransferCancelled() && transfer.Cancelled(), "a latch stops it at the next write point");
	}
	CHECK(!OutputGate::TransferCancelled(), "the transfer is over");
	{
		OutputGate::Transfer transfer(unit, unit->Observe());
		CHECK(!OutputGate::TransferCancelled() && !transfer.Cancelled(), "the closing's transfer goes on");
	}
}

// Once a latch returned, no value of the opening that was being sent reaches the device: the transfer stops before
// its next write, stays unconfirmed, and the closing goes to every output next.
void LatchStopsAControllerAtTheNextWrite()
{
	OutputGate& gate = OutputGate::BeginSession();
	ScriptedController controller;
	controller.SetName("scripted");
	std::atomic<int64_t> latched { -1 };
	controller.beforeReport = [&gate, &controller, &latched](int report, const std::vector<uint8_t>& values) {
		if (report == 1 && values[1] == 255 && latched.load() < 0)
		{
			latched = gate.Latch();
			controller.Note("latch");
		}
	};
	controller.Init(nullptr);
	controller.Set(255, 255);
	bool confirmed = WaitFor([&gate, &latched]() {
		OutputGate::Counts counts;
		return latched.load() >= 0 && gate.GetState(latched.load(), counts) == OutputGate::Status::Applied && counts.confirmed == 1;
	});
	CHECK(confirmed, "the latch is confirmed");
	try
	{
		controller.Finish();
	}
	catch (const std::exception&)
	{
		CHECK(false, "the controller finishes");
	}
	std::vector<std::string> log = controller.Written();
	auto mark = std::find(log.begin(), log.end(), "latch");
	CHECK(mark != log.end(), "the latch came during the opening's transfer");
	CHECK(std::none_of(mark, log.end(), [](const std::string& write) { return write.find("=255") != std::string::npos; }), "no value of the opening goes out after the latch");
	CHECK(std::find(mark, log.end(), "0=0") != log.end() && std::find(mark, log.end(), "1=0") != log.end(), "the closing goes to every output");
}

// A final blackout confirms the closing observed before it was sent and one published while it was sent, and the
// unit stops after it, keeping that. It confirms no opening.
void FinalBlackoutConfirmsTheClosing()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t closed = Change(gate, false);
	OutputGate::Snapshot before = unit->Observe();
	int64_t latched = gate.Latch();
	unit->AckOff(before);
	unit->Stop();
	OutputGate::Counts counts;
	CHECK(gate.GetState(closed, counts) == OutputGate::Status::Applied && counts.confirmed == 1 && counts.failed == 0, "the closing before the blackout is confirmed");
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1 && counts.failed == 0, "and the one published while it was sent");
	CHECK(NamesAgree(gate, latched), "names agree with the counts");

	OutputGate& next = OutputGate::BeginSession();
	OutputGate::UnitHandle other = next.Register("unit");
	int64_t open = Change(next, true);
	other->AckOff(other->Observe());
	other->Stop();
	CHECK(next.GetState(open, counts) == OutputGate::Status::Applied && counts.confirmed == 0 && counts.failed == 1, "a blackout confirms no opening");
}

// A unit whose device was lost blocks its updates, never its blackout: the blackout at the end still goes to the
// device, and once it got there the unit finishes cleanly instead of failing.
void LostDeviceStillGetsItsBlackout()
{
	for (bool works : { true, false })
	{
		OutputGate& gate = OutputGate::BeginSession();
		ScriptedController controller;
		controller.SetName("scripted");
		controller.failReconnect = true;
		controller.blackoutWorks = works;
		controller.Init(nullptr);
		controller.Set(255, 0);
		CHECK(WaitFor([&controller]() { return controller.HasWritten("0=255"); }), "the opening's value goes out");
		int64_t open = gate.Applied();
		controller.failNextWrite = true;
		controller.Set(128, 0);
		CHECK(WaitFor([&controller]() { return controller.blackouts.load() >= 2; }), "the updater ends after the reconnect failed");
		std::string error;
		try
		{
			controller.Finish();
		}
		catch (const std::exception& e)
		{
			error = e.what();
		}
		CHECK(controller.blackouts.load() == 2, "the blackout is tried again at the end");
		OutputGate::Counts counts;
		gate.GetState(open, counts);
		if (works)
		{
			CHECK(error.empty(), "a blackout that got to the device finishes cleanly");
			CHECK(counts.confirmed == 1 && counts.failed == 0, "and keeps what the unit confirmed");
		}
		else
		{
			CHECK(error.find("lost its connection") != std::string::npos, "a blackout that did not get there fails the finish");
			CHECK(counts.failed == 1, "and the unit");
		}
	}
}

// A connect test lights the device before any value changed: the unit confirmed the opening it went out with, not as
// untouched, and the session ends with the blackout. It confirms that opening only: after a closing turned the device
// off, a later opening it wrote nothing for is untouched.
void ConnectTestCountsAsUse()
{
	OutputGate& gate = OutputGate::BeginSession();
	int64_t open = Change(gate, true);
	ScriptedController controller;
	controller.SetName("scripted");
	controller.testOnConnect = true;
	controller.Init(nullptr);
	OutputGate::Counts counts;
	CHECK(WaitFor([&]() { return gate.GetState(open, counts) == OutputGate::Status::Applied && counts.confirmed == 1; }), "the opening counts the tested unit as used");
	CHECK(controller.HasWritten("test") && counts.untouched == 0, "not as untouched");
	int64_t closed = Change(gate, false);
	CHECK(WaitFor([&]() { return gate.GetState(closed, counts) == OutputGate::Status::Applied && counts.confirmed == 1; }), "the closing is written");
	size_t written = controller.Written().size();
	int64_t reopened = Change(gate, true);
	CHECK(WaitFor([&]() { return gate.GetState(reopened, counts) == OutputGate::Status::Applied && counts.pending == 0; }), "the opening after it is settled");
	CHECK(counts.untouched == 1 && counts.confirmed == 0, "the test does not confirm an opening after a closing");
	CHECK(controller.Written().size() == written, "nothing was written for it");
	controller.Finish();
	std::vector<std::string> log = controller.Written();
	auto finished = log.begin() + static_cast<std::ptrdiff_t>(written);
	CHECK(std::find(finished, log.end(), "0=0") != log.end() && std::find(finished, log.end(), "1=0") != log.end(), "the session ends with the blackout");
}

// A connect that fails after the device took its connect test ends as a session does: with the blackout, and with an
// error unless the blackout got there. The unit fails either way.
void ConnectThatFailsAfterItsTestEndsDark()
{
	for (bool blackoutWorks : { true, false })
	{
		OutputGate& gate = OutputGate::BeginSession();
		int64_t open = Change(gate, true);
		ScriptedController controller;
		controller.SetName("scripted");
		controller.testOnConnect = true;
		controller.failAfterTest = true;
		controller.failNextWrite = !blackoutWorks;
		controller.Init(nullptr);
		OutputGate::Counts counts;
		CHECK(WaitFor([&]() { return gate.GetState(open, counts) == OutputGate::Status::Applied && counts.failed == 1; }), "the unit fails");
		std::string error;
		try
		{
			controller.Finish();
		}
		catch (const std::exception& e)
		{
			error = e.what();
		}
		std::vector<std::string> log = controller.Written();
		auto test = std::find(log.begin(), log.end(), "test");
		CHECK(test != log.end(), "the test was sent");
		if (blackoutWorks)
		{
			CHECK(std::find(test, log.end(), "0=0") != log.end() && std::find(test, log.end(), "1=0") != log.end(), "the blackout follows it");
			CHECK(error.empty(), "a blackout that got there finishes cleanly");
		}
		else
			CHECK(error.find("after its connect test") != std::string::npos, "one that did not fails the finish");
	}
}

// A unit registered while the gate is closed turns its device off first, also when the gate opened before its first
// update: a session that starts closed starts dark on every unit, whatever an earlier one left lit. A connect test
// does not go out before that.
void SessionThatStartsClosedStartsDark()
{
	OutputGate& gate = OutputGate::BeginSession();
	int64_t closed = Change(gate, false);
	ScriptedController controller;
	controller.SetName("scripted");
	controller.testOnConnect = true;
	// The updater is held in its first connect, before it observes the gate, until the opening is published.
	std::atomic<bool> opened { false };
	std::atomic<bool> held { false };
	controller.onFirstConnect = [&]() {
		held = true;
		WaitFor([&opened]() { return opened.load(); });
	};
	controller.Init(nullptr);
	CHECK(WaitFor([&held]() { return held.load(); }), "the updater is held in its first connect");
	int64_t open = Change(gate, true);
	opened = true;
	OutputGate::Counts counts;
	// The opening supersedes the closing; the unit still has to confirm it.
	CHECK(WaitFor([&]() {
		gate.GetState(closed, counts);
		return counts.confirmed == 1;
	}),
		  "the closing the unit was registered under is written");
	CHECK(controller.HasWritten("0=0") && controller.HasWritten("1=0"), "to every output");
	CHECK(WaitFor([&]() { return gate.GetState(open, counts) == OutputGate::Status::Applied && counts.pending == 0; }) && counts.untouched == 1, "the opening after it leaves the unit alone");
	CHECK(!controller.HasWritten("test"), "no connect test went out before the closing was written");
	controller.Finish();
}

// A session that starts closed sends no connect test, also not on a reconnect once the gate opened: the test was used
// up when the unit registered under the closing, though the first connect never got to it.
void SessionThatStartsClosedTestsOnNoReconnect()
{
	OutputGate& gate = OutputGate::BeginSession();
	Change(gate, false);
	ScriptedController controller;
	controller.SetName("scripted");
	controller.testOnConnect = true;
	controller.handshakeFailsOnce = true;
	controller.failNextWrite = true;
	controller.Init(nullptr);
	Change(gate, true);
	controller.Set(255, 0);
	CHECK(WaitFor([&controller]() { return controller.connects.load() >= 2 && controller.HasWritten("0=255"); }), "the controller reconnects and writes");
	CHECK(!controller.HasWritten("test"), "without a connect test");
	controller.Finish();
}

// A unit whose startup blackout failed sends it again once the device is back, before anything else, also when the gate
// opened meanwhile and no value changed: the device an earlier session left lit is turned off. Until then the unit
// fails the openings after that closing, rather than leaving them pending.
void FailedStartupBlackoutIsSentAgain()
{
	OutputGate& gate = OutputGate::BeginSession();
	int64_t closed = Change(gate, false);
	ScriptedController controller;
	controller.SetName("scripted");
	controller.failWrites = true;
	std::atomic<bool> opened { false };
	controller.onFirstConnect = [&opened]() { WaitFor([&opened]() { return opened.load(); }); };
	controller.Init(nullptr);
	int64_t open = Change(gate, true);
	opened = true;
	OutputGate::Counts counts;
	CHECK(WaitFor([&]() { return controller.connects.load() >= 2 && gate.GetState(open, counts) == OutputGate::Status::Applied && counts.failed == 1; }), "the opening fails while the device takes nothing");
	controller.failWrites = false;
	CHECK(WaitFor([&]() {
		gate.GetState(closed, counts);
		return counts.confirmed == 1;
	}),
		  "the closing is written once the device is back");
	CHECK(controller.HasWritten("0=0") && controller.HasWritten("1=0"), "to every output");
	CHECK(WaitFor([&]() { return gate.GetState(open, counts) == OutputGate::Status::Applied && counts.untouched == 1; }), "then the opening leaves the unit alone");
	controller.Finish();
}

// A connect test is checked again at its write: a latch between the decision and the write (one in the log line)
// keeps it from going out.
void LatchBeforeTheConnectTestSkipsIt()
{
	OutputGate& gate = OutputGate::BeginSession();
	Change(gate, true);
	ScriptedController controller;
	controller.SetName("scripted");
	controller.testOnConnect = true;
	std::atomic<int64_t> latched { -1 };
	controller.beforeTest = [&]() { latched = gate.Latch(); };
	controller.Init(nullptr);
	OutputGate::Counts counts;
	CHECK(WaitFor([&]() {
		int64_t generation = latched.load();
		return generation >= 0 && gate.GetState(generation, counts) == OutputGate::Status::Applied && counts.confirmed == 1;
	}),
		  "the latch is written");
	CHECK(!controller.HasWritten("test"), "the test after it is not");
	controller.Finish();
}

// The final blackout confirms the closings published before and while it was sent, at the blackout: a closing published
// later, while the controller disconnects, was not carried by it and fails as one after the unit stopped does, unless
// the disconnect sends a blackout of its own after it.
void BlackoutConfirmsWhereItIsSent()
{
	for (bool disconnectBlackout : { false, true })
	{
		OutputGate& gate = OutputGate::BeginSession();
		ScriptedController controller;
		controller.SetName("scripted");
		controller.blackoutWorks = disconnectBlackout;
		controller.Init(nullptr);
		controller.Set(255, 0);
		CHECK(WaitFor([&controller]() { return controller.HasWritten("0=255"); }), "the opening's value goes out");
		int64_t latched = -1;
		controller.onDisconnect = [&]() {
			if (latched < 0)
				latched = gate.Latch();
		};
		controller.Finish();
		OutputGate::Counts counts;
		CHECK(latched >= 0 && gate.GetState(latched, counts) == OutputGate::Status::Applied, "the latch came while the controller disconnected");
		if (disconnectBlackout)
			CHECK(counts.confirmed == 1 && counts.failed == 0, "a blackout the disconnect sends after it confirms it");
		else
			CHECK(counts.confirmed == 0 && counts.failed == 1, "the final blackout before it does not");
	}
}

// Once Finish started, the session takes no opening; a closing latches the gate.
void FinishTakesNoOpening()
{
	OutputGate& gate = OutputGate::BeginSession();
	Pinball pinball;
	PinballTestAccess::Stop(pinball);
	CHECK(pinball.SetOutputGate(true) == -1 && gate.IsOpen(), "an opening is refused and changes nothing");
	int64_t closed = pinball.SetOutputGate(false);
	OutputGate::Counts counts;
	CHECK(closed >= 0 && gate.IsLatched() && !gate.IsOpen(), "a closing latches the gate");
	CHECK(gate.GetState(closed, counts) == OutputGate::Status::Applied, "and is published at once");
	CHECK(pinball.SetOutputGate(true) == -1 && !gate.IsOpen(), "the gate stays closed");
}

void PermanentFailureOutranksAnAck()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle unit = gate.Register("unit");
	int64_t closed = Change(gate, false);
	unit->Ack(gate.Current());
	OutputGate::Counts counts;
	CHECK(gate.GetState(closed, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the closing is confirmed");

	unit->Die();
	gate.GetState(closed, counts);
	CHECK(counts.confirmed == 0 && counts.failed == 1, "a unit whose updater died is failed");
	std::vector<std::string> pending = gate.GetPending(closed);
	CHECK(pending.size() == 1 && pending[0] == "unit", "and listed");
}

void CleanFinishKeepsTheSettledResult()
{
	OutputGate& gate = OutputGate::BeginSession();
	OutputGate::UnitHandle used = gate.Register("used");
	OutputGate::UnitHandle unused = gate.Register("unused");
	OutputGate::UnitHandle failing = gate.Register("failing");
	int64_t open = Change(gate, true);
	OutputGate::Snapshot snapshot = gate.Current();
	used->Ack(snapshot);
	unused->Untouched(snapshot);
	failing->Untouched(snapshot);
	failing->Fail(snapshot.generation);

	OutputGate::Counts counts;
	gate.GetState(open, counts);
	CHECK(counts.confirmed == 1 && counts.untouched == 1 && counts.failed == 1, "a unit that failed after it was left alone is failed");
	std::vector<std::string> pending = gate.GetPending(open);
	CHECK(pending.size() == 1 && pending[0] == "failing", "and listed");

	used->Stop();
	unused->Stop();
	failing->Stop();
	gate.GetState(open, counts);
	CHECK(counts.confirmed == 1 && counts.untouched == 1 && counts.failed == 1 && counts.pending == 0, "a clean finish keeps what was settled");
	CHECK(NamesAgree(gate, open), "names agree with the counts");

	int64_t closed = Change(gate, false);
	gate.GetState(closed, counts);
	CHECK(counts.failed == 3, "finished units fail later generations");
	CHECK(NamesAgree(gate, closed), "names agree with the counts");
}

// A folder this run created, so the test writes and removes nothing it did not make. Empty if none could be created.
std::filesystem::path NewTempDirectory(const std::string& prefix)
{
	std::random_device random;
	for (int attempt = 0; attempt < 100; attempt++)
	{
		std::filesystem::path path = std::filesystem::temp_directory_path() / (prefix + "-" + std::to_string(random()) + std::to_string(random()));
		std::error_code ec;
		if (std::filesystem::create_directory(path, ec) && !ec)
			return path;
	}
	return std::filesystem::path();
}

// Path confinement decides on the path as written first: a path outside the root, a network share among them, is
// refused without being looked up, and the lookups (whether a file exists, its size) go through the check.
void LookupsOutsideTheRootAreRefused()
{
	std::filesystem::path root = NewTempDirectory("vpe-dof-internal-confinement");
	CHECK(!root.empty(), "the test has a folder of its own");
	if (root.empty())
		return;
	std::string inside = (root / "inside.ini").string();
	FILE* file = std::fopen(inside.c_str(), "wb");
	if (file)
		std::fclose(file);
#ifdef _WIN32
	const std::string share = "\\\\192.0.2.1\\dof\\directoutputconfig.ini";
	const std::string outside = "C:\\Windows\\win.ini";
#else
	const std::string share = "//192.0.2.1/dof/directoutputconfig.ini";
	const std::string outside = "/etc/hosts";
#endif
	PathConfinement::SetRoot(root.string());
	auto start = std::chrono::steady_clock::now();
	bool shareAllowed = PathConfinement::IsLookupAllowed(share) || FileInfo(share).Exists();
	auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
	CHECK(!shareAllowed, "a network share outside the root is refused");
	CHECK(elapsed < 1000, "without being looked up");
	CHECK(!FileInfo(outside).Exists(), "a file outside the root does not exist for libdof");
	bool threw = false;
	try
	{
		FileInfo(outside).Length();
	}
	catch (const std::exception&)
	{
		threw = true;
	}
	CHECK(threw, "nor has it a size");
	CHECK(!FileInfo((root / ".." / "elsewhere.ini").string()).Exists(), "a path leading out of the root is refused");
	CHECK(FileInfo(inside).Exists(), "a file inside the root is found");
	PathConfinement::SetRoot("");
	std::error_code ec;
	std::filesystem::remove_all(root, ec);
}

// The check decides on the path the reader opens. On POSIX a ".." after a symbolic link leaves the link's target, so
// "link/../secret.ini" opens the file next to the target, outside the root, while its normalized form ("secret.ini" in
// the root) is inside. Windows removes ".." from a path before it follows any link, as the check does, and creating a
// link needs a privilege there, so this runs on POSIX only.
void PathThroughALinkIsCheckedAsItIsOpened()
{
#ifndef _WIN32
	std::filesystem::path root = NewTempDirectory("vpe-dof-internal-root");
	std::filesystem::path outside = NewTempDirectory("vpe-dof-internal-outside");
	CHECK(!root.empty() && !outside.empty(), "the test has folders of its own");
	if (root.empty() || outside.empty())
		return;
	std::error_code ec;
	std::filesystem::create_directory(outside / "target", ec);
	for (const std::filesystem::path& file : { outside / "secret.ini", root / "secret.ini" })
	{
		FILE* handle = std::fopen(file.c_str(), "wb");
		if (handle)
		{
			std::fputs(file == root / "secret.ini" ? "inside" : "outside", handle);
			std::fclose(handle);
		}
	}
	std::filesystem::create_directory_symlink(outside / "target", root / "link", ec);
	CHECK(!ec, "the link is created");
	const std::string through = (root / "link" / ".." / "secret.ini").string();
	PathConfinement::SetRoot(root.string());
	CHECK(!PathConfinement::IsLookupAllowed(through), "a path that leaves the root through a link is refused");
	CHECK(!FileInfo(through).Exists(), "and does not exist for libdof");
	std::string read;
	try
	{
		read = FileReader::ReadFileToString(through);
	}
	catch (const std::exception&)
	{
	}
	CHECK(read.empty(), "nor is it read");
	CHECK(FileReader::ReadFileToString((root / "secret.ini").string()) == "inside", "the file inside the root still is");
	PathConfinement::SetRoot("");
	std::filesystem::remove_all(root, ec);
	std::filesystem::remove_all(outside, ec);
#endif
}

// A path inside the root is read whatever its spelling: through the junction the root was given through or another
// one, in 8.3 or long names per component, in any case (non-ASCII too). A path on another share, in a device namespace
// or a UNC path is refused without being looked up, also when it names a file inside. Windows only, and the 8.3 checks
// only where the volume has short names.
void RootSpellingsAreOneFolder()
{
#ifdef _WIN32
	std::filesystem::path base = NewTempDirectory("vpe-dof-internal-spellings");
	CHECK(!base.empty(), "the test has a folder of its own");
	if (base.empty())
		return;
	std::filesystem::path store = base / std::filesystem::path(L"St\u00f6re Unic\u00f8de");
	std::error_code ec;
	std::filesystem::create_directories(store / "rev" / "config", ec);
	for (const std::filesystem::path& file : { store / "rev" / "config" / "Cabinet.xml", base / "outside.xml" })
	{
		FILE* handle = _wfopen(file.c_str(), L"wb");
		if (handle)
			std::fclose(handle);
	}
	bool junction = true;
	for (const char* name : { "link", "other" })
	{
		std::wstring command = L"cmd /c mklink /J \"" + (base / name).wstring() + L"\" \"" + store.wstring() + L"\" >nul";
		junction = junction && _wsystem(command.c_str()) == 0 && std::filesystem::exists(base / name / "rev");
	}
	CHECK(junction, "the junctions are created");
	auto shortForm = [](const std::filesystem::path& path) {
		wchar_t buffer[2048];
		DWORD length = GetShortPathNameW(path.c_str(), buffer, 2048);
		return length && length < 2048 ? std::wstring(buffer, length) : std::wstring();
	};
	auto ascii = [](const std::wstring& text) {
		std::string result;
		for (wchar_t c : text)
		{
			if (c > 0x7F)
				return std::string();
			result += static_cast<char>(c);
		}
		return result;
	};
	std::string shortRoot = ascii(shortForm(store / "rev"));
	std::string shortCabinet = ascii(shortForm(store / "rev" / "config" / "Cabinet.xml"));
	std::string shortOutside = ascii(shortForm(base / "outside.xml"));
	std::string junctionRoot = ascii((base / "link" / "rev").wstring());
	std::string junctionCabinet = junctionRoot + "\\config\\Cabinet.xml";
	std::string otherCabinet = ascii((base / "other" / "rev" / "config" / "Cabinet.xml").wstring());
	// The folder in 8.3, the junction and what is inside it long: spellings mixed per component.
	std::string shortBase = ascii(shortForm(base));
	auto upper = [](std::string text) {
		for (char& c : text)
			c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
		return text;
	};
	if (junction && !junctionRoot.empty())
	{
		PathConfinement::SetRoot(junctionRoot);
		CHECK(FileInfo(junctionCabinet).Exists(), "a file through the junction the root was given through is found");
		CHECK(FileInfo(upper(junctionCabinet)).Exists(), "in any case");
		CHECK(!FileInfo(junctionRoot + "\\..\\..\\outside.xml").Exists(), "a path leading out of it is refused");
		if (!shortCabinet.empty())
		{
			CHECK(FileInfo(shortCabinet).Exists(), "a file through the 8.3 spelling of the resolved root is found");
			CHECK(!FileInfo(shortRoot + "\\..\\..\\outside.xml").Exists(), "a path leading out of that spelling is refused");
		}
		if (!shortOutside.empty())
			CHECK(!FileInfo(shortOutside).Exists(), "an 8.3 path outside the root is refused");
		CHECK(FileInfo(otherCabinet).Exists(), "a file through another junction to the root is found");
		CHECK(!FileInfo(ascii((base / "other" / "rev").wstring()) + "\\..\\..\\outside.xml").Exists(), "a path leading out of it is refused");
		if (!shortBase.empty() && shortBase != ascii(base.wstring()))
		{
			CHECK(FileInfo(shortBase + "\\link\\rev\\config\\Cabinet.xml").Exists(), "a file through an 8.3 folder above a long one is found");
			PathConfinement::SetRoot(shortBase + "\\link\\rev");
			CHECK(FileInfo(junctionCabinet).Exists(), "and with that root, the long spelling");
			PathConfinement::SetRoot(junctionRoot);
		}
		// The same file, written so that only the volume it is on decides: refused without being looked up.
		std::string drive = junctionCabinet.substr(0, 1);
		std::string rest = junctionCabinet.substr(2);
		for (const std::string& written : { "\\\\.\\" + junctionCabinet, "\\??\\" + junctionCabinet,"\\\\localhost\\" + drive + "$" + rest, "\\\\?\\UNC\\localhost\\" + drive + "$" + rest })
			CHECK(!FileInfo(written).Exists(), ("a device or UNC path to a file inside is refused: " + written).c_str());
		// An extended-length path names its drive: it is checked as the drive's path is.
		CHECK(FileInfo("\\\\?\\" + junctionCabinet).Exists(), "an extended-length path inside is found");
		CHECK(!FileInfo("\\\\?\\" + junctionRoot + "\\..\\..\\outside.xml").Exists(), "one leading out is refused");
		DWORD drives = GetLogicalDrives();
		for (char letter = 'A'; letter <= 'Z'; letter++)
		{
			if (letter == std::toupper(static_cast<unsigned char>(drive[0])) || !(drives & (1u << (letter - 'A'))))
				continue;
			std::string other = std::string(1, letter) + ":" + rest;
			CHECK(!FileInfo(other).Exists(), ("a path on another drive is refused: " + other).c_str());
			CHECK(!FileInfo("\\\\?\\" + other).Exists(), ("also extended-length: " + other).c_str());
			break;
		}
	}
	// A folder with a non-ASCII name, given and read in another case. Paths are narrow strings in the active code page,
	// so only where it has the name.
	std::filesystem::path accented = base / std::filesystem::path(L"\u00e9t\u00e9");
	std::filesystem::create_directories(accented / "config", ec);
	FILE* accentedFile = _wfopen((accented / "config" / "Cabinet.xml").c_str(), L"wb");
	if (accentedFile)
		std::fclose(accentedFile);
	auto narrow = [](const std::wstring& text) {
		char buffer[2048];
		BOOL lossy = FALSE;
		int length = WideCharToMultiByte(CP_ACP, 0, text.c_str(), static_cast<int>(text.size()), buffer, sizeof(buffer), nullptr, GetACP() == CP_UTF8 ? nullptr : &lossy);
		return length > 0 && !lossy ? std::string(buffer, length) : std::string();
	};
	std::string accentedRoot = narrow(accented.wstring());
	std::string accentedUpper = narrow((base / std::filesystem::path(L"\u00c9T\u00c9") / "config" / "Cabinet.xml").wstring());
	if (!accentedRoot.empty() && !accentedUpper.empty())
	{
		PathConfinement::SetRoot(accentedRoot);
		CHECK(FileInfo(accentedUpper).Exists(), "a non-ASCII root finds its files in another case");
	}
	else
		std::printf("RootSpellingsAreOneFolder: the code page has no accented names, their case not checked\n");
	if (!shortRoot.empty() && !shortCabinet.empty())
	{
		PathConfinement::SetRoot(shortRoot);
		CHECK(FileInfo(shortCabinet).Exists(), "a root given in 8.3 finds its files in 8.3");
		CHECK(FileInfo(upper(shortCabinet)).Exists(), "in any case");
	}
	else
		std::printf("RootSpellingsAreOneFolder: no 8.3 names on this volume, short spellings not checked\n");
	PathConfinement::SetRoot("");
	std::filesystem::remove(base / "link", ec);
	std::filesystem::remove(base / "other", ec);
	std::filesystem::remove_all(base, ec);
#endif
}

// A table folder outside the configuration folder is not looked up, but its names still make the table config's path,
// which is checked as any other.
void TableFolderNamesComeFromTheFilename()
{
	std::filesystem::path root = NewTempDirectory("vpe-dof-internal-tableroot");
	std::filesystem::path tables = NewTempDirectory("vpe-dof-internal-tables");
	CHECK(!root.empty() && !tables.empty(), "the test has folders of its own");
	if (root.empty() || tables.empty())
		return;
	std::string name = tables.filename().string();
	FILE* handle = std::fopen((root / (name + ".xml")).string().c_str(), "wb");
	if (handle)
		std::fclose(handle);
	handle = std::fopen((tables / "Table.xml").string().c_str(), "wb");
	if (handle)
		std::fclose(handle);
	std::string table = (tables / "Table.vpx").string();
	PathConfinement::SetRoot(root.string());
	GlobalConfig config;
	std::unordered_map<std::string, std::string> values = config.GetReplaceValuesDictionary(table);
	CHECK(values.count("TableDirName") && values["TableDirName"] == name, "the table folder's name comes from the filename");
	config.GetTableConfigFilePatterns().push_back(FilePattern((root / "{TableDirName}.xml").string()));
	FileInfo* inside = config.GetTableConfigFile(table);
	CHECK(inside != nullptr, "a table config it names inside the root is found");
	delete inside;
	config.GetTableConfigFilePatterns().clear();
	config.GetTableConfigFilePatterns().push_back(FilePattern((tables / "{TableName}.xml").string()));
	FileInfo* outside = config.GetTableConfigFile(table);
	CHECK(outside == nullptr, "one outside the root is not, though it exists");
	delete outside;
	PathConfinement::SetRoot("");
	std::error_code ec;
	std::filesystem::remove_all(root, ec);
	std::filesystem::remove_all(tables, ec);
}

// A controller libdof cannot read is skipped in strict setup too, as a toy is, and the others load: VPE activates
// cabinets without libdof reading them, so setup must not refuse one as a whole for it.
void UnreadableControllerIsSkipped()
{
	bool strict = Config::GetInstance()->IsStrictSetup();
	Config::GetInstance()->SetStrictSetup(true);
	tinyxml2::XMLDocument doc;
	// Unreadable XML (FromXml returns false), a setting a setter refuses (it throws), and a unit selector that is missing or
	// not a number, which would fail Init and so the whole cabinet.
	doc.Parse("<OutputControllers><ArtNet><Name>unset</Name><Universe>unset</Universe></ArtNet>"
			  "<PacUIO Name=\"out of range\" Id=\"2\" />"
			  "<PacLed64 Name=\"out of range\" Id=\"5\" /><PacUIO Name=\"no Id\" /><PacLed64 Name=\"no Id\" />"
			  "<PacLed64 Name=\"bad Id\" Id=\"one\" /><LedWiz><Name>no number</Name></LedWiz>"
			  "<LedWiz><Name>bad number</Name><Number>one</Number></LedWiz>"
			  "<PacUIO Name=\"fraction\" Id=\"0.5\" /><PacLed64 Name=\"suffix\" Id=\"1old\" />"
			  "<LedWiz><Name>fraction</Name><Number>2.5</Number></LedWiz>"
			  "<ArtNet><Name>suffix</Name><Universe>1old</Universe></ArtNet>"
			  "<ArtNet><Name>valid</Name><Universe>1</Universe></ArtNet></OutputControllers>");
	OutputControllerList controllers;
	bool threw = false;
	try
	{
		controllers.FromXml(doc.RootElement());
	}
	catch (const std::exception&)
	{
		threw = true;
	}
	CHECK(!threw, "setup does not refuse the cabinet");
	CHECK(controllers.size() == 1 && controllers.front()->GetName() == "valid", "the readable controller loads");
	Config::GetInstance()->SetStrictSetup(strict);
}

// A session's gate is freed, with its history and units, once nothing holds it: not when the next session begins while
// a unit handle (a controller, an updater thread that still runs) or an instance holds it, and as soon as the last one
// lets go. A host that runs many sessions keeps no gate nothing uses.
void EndedSessionsAreFreed()
{
	std::weak_ptr<OutputGate> ended;
	OutputGate::UnitHandle handle;
	{
		OutputGate& gate = OutputGate::BeginSession();
		ended = gate.shared_from_this();
		handle = gate.Register("updater");
		for (int i = 0; i < 100; i++)
			gate.Publish(gate.Request(i % 2 == 0));
	}
	std::weak_ptr<OutputGate> current = OutputGate::BeginSession().shared_from_this();
	CHECK(!ended.expired(), "a gate stays while a unit handle holds it");
	handle->Ack(handle->Observe());
	CHECK(handle->Observe().generation > 0, "and its unit still works");
	handle.reset();
	CHECK(ended.expired(), "and is freed with the last handle");
	CHECK(!current.expired(), "the current session's gate stays");

	std::shared_ptr<OutputGate> instance = OutputGate::Instance().shared_from_this();
	OutputGate::BeginSession();
	CHECK(!current.expired(), "an instance keeps its gate after the next session began");
	instance.reset();
	CHECK(current.expired(), "and frees it");

	// Many sessions keep only the current one.
	std::vector<std::weak_ptr<OutputGate>> sessions;
	for (int i = 0; i < 1000; i++)
	{
		OutputGate& gate = OutputGate::BeginSession();
		gate.Register("unit")->Stop();
		gate.Publish(gate.Request(false));
		sessions.push_back(gate.shared_from_this());
	}
	CHECK(std::count_if(sessions.begin(), sessions.end(), [](const std::weak_ptr<OutputGate>& gate) { return !gate.expired(); }) == 1, "only the current session's gate is kept");
}

// Finish drains what the main thread has left the same way: an opening that came with input still queued before it, as
// one posted right before finish, runs after that input, and input queued after the last command is left. The order is
// decided by the queue, not by how fast the main thread takes it, so this holds the input in place instead of racing it.
void FinishRunsTheInputQueuedBeforeAnOpening()
{
	Pinball pinball;
	pinball.ReceiveData('S', 1, 0);
	pinball.ReceiveData('S', 2, 0);
	PinballTestAccess::Post(pinball);
	pinball.ReceiveData('S', 1, 1);
	PinballTestAccess::Stop(pinball);
	CHECK(PinballTestAccess::Take(pinball, 4, true) == "IIC-", "the input before the opening first, the input after it left");
}

// A command runs after the input queued before it and before the input queued after it: a closing followed by an on
// sends no on, and an off followed by an opening does not send the on that came before.
void CommandsAndInputKeepTheirOrder()
{
	Pinball pinball;
	PinballTestAccess::Post(pinball);
	pinball.ReceiveData('S', 1, 1);
	CHECK(PinballTestAccess::Take(pinball, 3) == "CI-", "a closing followed by an on");

	pinball.ReceiveData('S', 1, 0);
	PinballTestAccess::Post(pinball);
	CHECK(PinballTestAccess::Take(pinball, 3) == "IC-", "an off followed by an opening");

	pinball.ReceiveData('S', 1, 1);
	PinballTestAccess::Post(pinball);
	pinball.ReceiveData('S', 1, 0);
	PinballTestAccess::Post(pinball);
	pinball.ReceiveData('S', 1, 1);
	CHECK(PinballTestAccess::Take(pinball, 6) == "ICICI-", "interleaved");
}

// A controller whose first output throws when it is told about a change, as a failing effect or device can.
class RaisingController : public OutputControllerBase
{
public:
	std::vector<int> raised;
	std::string GetXmlElementName() const override { return "RaisingController"; }
	tinyxml2::XMLElement* ToXml(tinyxml2::XMLDocument&) const override { return nullptr; }
	bool FromXml(const tinyxml2::XMLElement*) override { return true; }
	void Init(Cabinet*) override { }
	void Finish() override { }
	void Update() override { }

protected:
	void OnOutputValueChanged(IOutput* output) override
	{
		if (output->GetNumber() == 1)
			throw std::runtime_error("an effect failed");
		raised.push_back(output->GetNumber());
	}
};

// A gate change reaches every output and is published whatever one of them throws: a closing that stayed unpublished
// would report not applied forever, with the outputs after the one that threw left on.
void GateChangeIsPublishedWhateverAnOutputThrows()
{
	OutputGate& gate = OutputGate::BeginSession();
	Pinball pinball;
	RaisingController* controller = new RaisingController();
	for (int number = 1; number <= 3; number++)
	{
		Output* output = new Output();
		output->SetNumber(number);
		output->SetName("Output" + std::to_string(number));
		output->SetGated(true);
		output->SetOutput(255);
		controller->GetOutputs()->Add(output);
	}
	OutputControllerList* controllers = pinball.GetCabinet()->GetOutputControllers();
	controllers->push_back(controller);

	int64_t closing = gate.Request(false);
	PinballTestAccess::Apply(pinball, false, closing);
	OutputGate::Counts counts;
	CHECK(gate.GetState(closing, counts) == OutputGate::Status::Applied, "the closing is published");
	CHECK(!gate.IsOpen(), "the gate is closed");
	CHECK(controller->raised == std::vector<int>({ 2, 3 }), "the outputs after the one that threw are told");

	controller->raised.clear();
	int64_t opening = gate.Request(true);
	PinballTestAccess::Apply(pinball, true, opening);
	CHECK(gate.GetState(opening, counts) == OutputGate::Status::Applied, "the opening is published");
	CHECK(controller->raised == std::vector<int>({ 2, 3 }), "and reaches the other outputs");

	controllers->erase(std::find(controllers->begin(), controllers->end(), static_cast<IOutputController*>(controller)));
	delete controller;
}

// A schedule applies to the outputs its device lists. One that lists none changes no output, as in DOF: a 0% schedule
// would otherwise turn off the whole device.
void ScheduleAppliesToTheOutputsItLists()
{
	// Gated outputs report 0 while the gate is closed.
	OutputGate::BeginSession();
	tinyxml2::XMLDocument doc;
	doc.Parse(
		"<ScheduledSettings><ScheduledSetting><Name>all day</Name><Enabled>true</Enabled><ClockStart>0000</ClockStart><ClockEnd>0000</ClockEnd>"
		"<ScheduledSettingDeviceList>"
		"<ScheduledSettingDevice><ConfigPostfixID>1</ConfigPostfixID><Outputs></Outputs><OutputPercent>0</OutputPercent></ScheduledSettingDevice>"
		"<ScheduledSettingDevice><ConfigPostfixID>2</ConfigPostfixID><Outputs>2</Outputs><OutputPercent>0</OutputPercent></ScheduledSettingDevice>"
		"</ScheduledSettingDeviceList></ScheduledSetting></ScheduledSettings>");
	ScheduledSettings& schedules = ScheduledSettings::GetInstance();
	CHECK(schedules.FromXml(doc.FirstChildElement("ScheduledSettings")) && schedules.size() == 1, "the schedule loads");

	auto strength = [&schedules](int device, int number) {
		Output output;
		output.SetNumber(number);
		output.SetOutput(200);
		IOutput* result = schedules.GetNewRecalculatedOutput(&output, 1, device - 1);
		int value = result->GetOutput();
		if (result != &output)
			delete result;
		return value;
	};
	CHECK(strength(1, 1) == 200 && strength(1, 2) == 200, "a device without outputs keeps its strength");
	CHECK(strength(2, 2) == 0, "a listed output gets the schedule's strength");
	CHECK(strength(2, 3) == 200, "an output that is not listed keeps its strength");
	schedules.Clear();
}

}

int main()
{
	EffectsThatNeverStartedAreNotFinished();
	MatrixEffectWhoseToyFailsIsFinished();
	UntouchedUnitsAreCountedApart();
	LatchOvertakesAQueuedOpening();
	OpeningReservedBeforeALatchIsSuperseded();
	OpeningPublishedDuringALatchIsSuperseded();
	ResultsAreDecidedWhenPublished();
	EarliestLatchDecides();
	OpeningBetweenTwoLatchesIsAClosing();
	PausedLatchChangesNothingReported();
	LatchesStayInTheirSession();
	LatchCancelsTheTransferOfAnOpening();
	LatchStopsAControllerAtTheNextWrite();
	FinalBlackoutConfirmsTheClosing();
	LostDeviceStillGetsItsBlackout();
	FinishTakesNoOpening();
	ConnectTestCountsAsUse();
	ConnectThatFailsAfterItsTestEndsDark();
	SessionThatStartsClosedStartsDark();
	SessionThatStartsClosedTestsOnNoReconnect();
	LatchBeforeTheConnectTestSkipsIt();
	FailedStartupBlackoutIsSentAgain();
	BlackoutConfirmsWhereItIsSent();
	PermanentFailureOutranksAnAck();
	CleanFinishKeepsTheSettledResult();
	ScheduleAppliesToTheOutputsItLists();
	CommandsAndInputKeepTheirOrder();
	FinishRunsTheInputQueuedBeforeAnOpening();
	GateChangeIsPublishedWhateverAnOutputThrows();
	LookupsOutsideTheRootAreRefused();
	PathThroughALinkIsCheckedAsItIsOpened();
	RootSpellingsAreOneFolder();
	TableFolderNamesComeFromTheFilename();
	UnreadableControllerIsSkipped();
	EndedSessionsAreFreed();
	if (failures)
	{
		std::fprintf(stderr, "%d check(s) failed\n", failures);
		return 1;
	}
	std::printf("ok\n");
	return 0;
}
