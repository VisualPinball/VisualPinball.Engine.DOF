// Visual Pinball Engine
// Copyright (C) 2026 freezy and VPE Team
//
// SPDX-License-Identifier: GPL-3.0-or-later

// Tests of libdof's internals that no configuration reaches through the C ABI: effects that were never started, a toy
// that fails while an effect starts, schedules, the order of host commands and input, and the output gate's
// accounting, with the steps other threads can come between run one at a time. Exits non-zero if a check fails; a crash is a failure too.

#include <cstdio>
#include <functional>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

#include "Pinball.h"
#include "cab/Cabinet.h"
#include "cab/out/Output.h"
#include "cab/out/OutputGate.h"
#include "cab/schedules/ScheduledSettings.h"
#include "cab/toys/IMatrixToy.h"
#include "cab/toys/ToyList.h"
#include "fx/EffectBase.h"
#include "fx/EffectList.h"
#include "fx/matrixfx/RGBAMatrixShiftEffect.h"
#include "fx/timmedfx/DelayEffect.h"
#include "general/color/RGBAColor.h"
#include "pinballsupport/InputQueue.h"
#include "table/Table.h"
#include "table/TableElementData.h"

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
		gate.Record(generation, open && !latched, false);
		return generation;
	}

	static int64_t BeginLatch(OutputGate& gate) { return gate.BeginLatch(); }
	static void FinishLatch(OutputGate& gate, int64_t generation) { gate.FinishLatch(generation); }
	static int64_t LatchGeneration(const OutputGate& gate) { return gate.m_latchGeneration.load(); }
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

	// 'C' for a command, 'I' for input, '-' for nothing.
	static std::string Take(Pinball& pinball, int count)
	{
		std::string order;
		for (int i = 0; i < count; i++)
		{
			std::function<void()> command;
			TableElementData data;
			Pinball::Work work = pinball.TakeWork(false, command, data);
			order += work == Pinball::Work::Command ? 'C' : work == Pinball::Work::Input ? 'I' : '-';
		}
		return order;
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

void UntouchedUnitsAreCountedApart()
{
	OutputGate& gate = OutputGate::Instance();
	gate.BeginSession();
	OutputGate::Unit* used = gate.Register("used");
	OutputGate::Unit* unused = gate.Register("unused");

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

	int64_t closed = gate.Request(false);
	gate.Apply(false);
	gate.Publish(closed);
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
	OutputGate& gate = OutputGate::Instance();
	gate.BeginSession();
	OutputGate::Unit* unit = gate.Register("unit");
	int64_t closed = gate.Request(false);
	gate.Apply(false);
	gate.Publish(closed);
	unit->Ack(gate.Current());

	// The opening is queued for DOF's main thread when the latch comes; the main thread applies it afterwards.
	int64_t open = gate.Request(true);
	int64_t latched = gate.Latch();
	CHECK(latched > open, "the latch gets a later generation");
	CHECK(!gate.IsOpen(), "the latch closes at once");
	CHECK(!gate.Apply(true), "an opening applied after the latch is a closing");
	CHECK(!gate.IsOpen(), "and leaves the gate closed");
	gate.Publish(open);
	OutputGate::Snapshot snapshot = gate.Current();
	CHECK(snapshot.generation == latched && !snapshot.open, "the overtaken opening is not published");

	OutputGate::Counts counts;
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "the overtaken opening is superseded");
	unit->Ack(snapshot);
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the latch is confirmed");
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded, "superseded stays superseded");

	int64_t later = gate.Request(true);
	CHECK(!gate.Apply(true), "a later opening is a closing");
	gate.Publish(later);
	unit->Ack(gate.Current());
	CHECK(gate.GetState(later, counts) == OutputGate::Status::Applied && counts.confirmed == 1 && counts.untouched == 0, "and confirms the closed state");

	gate.BeginSession();
	CHECK(gate.IsOpen() && !gate.IsLatched(), "a new session is not latched");
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

// A schedule applies to the outputs its device lists. One that lists none changes no output, as in DOF: a 0% schedule
// would otherwise turn off the whole device.
void ScheduleAppliesToTheOutputsItLists()
{
	// Gated outputs report 0 while the gate is closed.
	OutputGate::Instance().BeginSession();
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

// The names GetPending returns are the units GetState counts as pending or failed.
bool NamesAgree(int64_t generation)
{
	OutputGate::Counts counts;
	OutputGate::Instance().GetState(generation, counts);
	return OutputGate::Instance().GetPending(generation).size() == static_cast<size_t>(counts.pending + counts.failed);
}

void OpeningReservedBeforeALatchIsSuperseded()
{
	OutputGate& gate = OutputGate::Instance();
	gate.BeginSession();
	OutputGate::Unit* unit = gate.Register("unit");
	int64_t closed = gate.Request(false);
	gate.Apply(false);
	gate.Publish(closed);
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
	CHECK(NamesAgree(open) && NamesAgree(latched), "names agree with the counts");

	// The main thread publishes the opening while the latch is half done: it goes out as a closing.
	gate.BeginSession();
	unit = gate.Register("unit");
	open = gate.Request(true);
	latched = OutputGateTestAccess::BeginLatch(gate);
	CHECK(!gate.Apply(true), "the opening applies as a closing");
	gate.Publish(open);
	OutputGate::Snapshot snapshot = gate.Current();
	CHECK(snapshot.generation == open && !snapshot.open, "it is published closed");
	OutputGateTestAccess::FinishLatch(gate, latched);
	unit->Ack(gate.Current());
	CHECK(gate.GetState(open, counts) == OutputGate::Status::Superseded && counts.confirmed == 0, "it is still superseded");
	CHECK(gate.GetState(latched, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the latch is confirmed");
}

void EarliestLatchDecides()
{
	OutputGate& gate = OutputGate::Instance();
	gate.BeginSession();
	OutputGate::Unit* unit = gate.Register("unit");
	int64_t first = OutputGateTestAccess::BeginLatch(gate);
	int64_t second = OutputGateTestAccess::BeginLatch(gate);
	OutputGateTestAccess::FinishLatch(gate, second);
	OutputGateTestAccess::FinishLatch(gate, first);
	CHECK(OutputGateTestAccess::LatchGeneration(gate) == first, "the earlier latch is the session's latch");
	unit->Ack(gate.Current());

	OutputGate::Counts counts;
	CHECK(gate.GetState(first, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the earlier latch is confirmed");
	CHECK(gate.GetState(second, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the later latch is confirmed");

	// An opening requested between two overlapping latches: what it reports while the first is not done stays so.
	gate.BeginSession();
	unit = gate.Register("unit");
	first = OutputGateTestAccess::BeginLatch(gate);
	int64_t open = gate.Request(true);
	CHECK(!gate.Apply(true), "the opening applies as a closing");
	gate.Publish(open);
	second = OutputGateTestAccess::BeginLatch(gate);
	OutputGateTestAccess::FinishLatch(gate, second);
	unit->Ack(gate.Current());
	OutputGate::Status before = gate.GetState(open, counts);
	CHECK(before == OutputGate::Status::Applied && counts.confirmed == 1, "the opening is a closing, confirmed");
	OutputGateTestAccess::FinishLatch(gate, first);
	CHECK(gate.GetState(open, counts) == before && counts.confirmed == 1, "and stays so once the first latch is done");
	CHECK(gate.GetState(first, counts) == OutputGate::Status::Applied && counts.confirmed == 1, "the first latch is confirmed");

	// A latch that pauses after taking its generation, while openings (one requested before it, one after) and a closing
	// are applied: what they report then stays so once it is done.
	gate.BeginSession();
	unit = gate.Register("unit");
	int64_t earlier = gate.Request(true);
	first = OutputGateTestAccess::BeginLatch(gate);
	open = gate.Request(true);
	CHECK(!gate.Apply(true), "the earlier opening applies as a closing");
	gate.Publish(earlier);
	CHECK(!gate.Apply(true), "the later opening applies as a closing");
	gate.Publish(open);
	int64_t closed = gate.Request(false);
	gate.Apply(false);
	gate.Publish(closed);
	unit->Ack(gate.Current());
	const int64_t changes[] = { earlier, open, closed };
	OutputGate::Status states[3];
	int confirmed[3];
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

void PermanentFailureOutranksAnAck()
{
	OutputGate& gate = OutputGate::Instance();
	gate.BeginSession();
	OutputGate::Unit* unit = gate.Register("unit");
	int64_t closed = gate.Request(false);
	gate.Apply(false);
	gate.Publish(closed);
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
	OutputGate& gate = OutputGate::Instance();
	gate.BeginSession();
	OutputGate::Unit* used = gate.Register("used");
	OutputGate::Unit* unused = gate.Register("unused");
	OutputGate::Unit* failing = gate.Register("failing");
	int64_t open = gate.Request(true);
	gate.Apply(true);
	gate.Publish(open);
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
	CHECK(NamesAgree(open), "names agree with the counts");

	int64_t closed = gate.Request(false);
	gate.Apply(false);
	gate.Publish(closed);
	gate.GetState(closed, counts);
	CHECK(counts.failed == 3, "finished units fail later generations");
	CHECK(NamesAgree(closed), "names agree with the counts");
}

}

int main()
{
	EffectsThatNeverStartedAreNotFinished();
	MatrixEffectWhoseToyFailsIsFinished();
	UntouchedUnitsAreCountedApart();
	LatchOvertakesAQueuedOpening();
	OpeningReservedBeforeALatchIsSuperseded();
	EarliestLatchDecides();
	PermanentFailureOutranksAnAck();
	CleanFinishKeepsTheSettledResult();
	ScheduleAppliesToTheOutputsItLists();
	CommandsAndInputKeepTheirOrder();
	if (failures)
	{
		std::fprintf(stderr, "%d check(s) failed\n", failures);
		return 1;
	}
	std::printf("ok\n");
	return 0;
}
