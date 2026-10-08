#ifdef NDEBUG
#undef NDEBUG
#endif

#include "../../../dll/src/pch.h"
#include "../../../dll/src/Include/ContextMenu.h"
#include "../../../dll/src/Include/NativeMenuConstruction.h"
#include "../../../dll/src/Include/StudioCapture.h"
#include "../PreviewJson.h"

#include <cassert>
#include <cstdint>
#include <future>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include <sddl.h>

using Nilesoft::Shell::MenuItemInfo;
using Nilesoft::Shell::StudioCapture;
using Nilesoft::Shell::StudioCaptureMetadata;
using Nilesoft::Shell::menuitem_t;
using ShellStudio::PreviewJson::Kind;
using ShellStudio::PreviewJson::Value;

// NativeMenu owns an expression Scope in production.  The serializer target
// intentionally links no evaluator implementation, so this inert fixture stub
// is sufficient for the constructor's exception-cleanup path.
namespace Nilesoft::Shell
{
	void Scope::clear(bool)
	{
	}
}

namespace
{
	void Require(bool condition, const char *message)
	{
		if(!condition)
			throw std::runtime_error(message);
	}

	const Value *Member(const Value &object, std::string_view name)
	{
		const auto *value = object.Find(name);
		Require(value != nullptr, "Expected JSON member is missing.");
		return value;
	}

	Value Parse(std::string_view json)
	{
		Value value;
		std::string error;
		Require(ShellStudio::PreviewJson::Parse(json, value, error),
			"Capture serializer emitted invalid JSON.");
		return value;
	}

	std::wstring CurrentPipeName(uint32_t &session)
	{
		HANDLE token = nullptr;
		Require(::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token) == TRUE,
			"Could not open the test process token.");
		DWORD size = 0;
		::GetTokenInformation(token, TokenUser, nullptr, 0, &size);
		std::vector<BYTE> buffer(size);
		Require(size != 0 && ::GetTokenInformation(token, TokenUser, buffer.data(), size, &size) == TRUE,
			"Could not read the test process SID.");
		LPWSTR sid = nullptr;
		Require(::ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER *>(buffer.data())->User.Sid, &sid) == TRUE,
			"Could not format the test process SID.");
		std::wstring result = L"\\\\.\\pipe\\QweShell.Studio.Capture.";
		result += sid;
		::LocalFree(sid);
		::CloseHandle(token);
		Require(::ProcessIdToSessionId(::GetCurrentProcessId(), reinterpret_cast<DWORD *>(&session)) == TRUE,
			"Could not read the test process session.");
		result += L"." + std::to_wstring(session);
		return result;
	}

	void WriteFrame(HANDLE pipe, std::string_view payload)
	{
		const uint32_t size = static_cast<uint32_t>(payload.size());
		DWORD written = 0;
		Require(::WriteFile(pipe, &size, sizeof(size), &written, nullptr) == TRUE && written == sizeof(size),
			"Could not write capture frame length.");
		Require(::WriteFile(pipe, payload.data(), size, &written, nullptr) == TRUE && written == size,
			"Could not write capture frame payload.");
	}

	std::string ReadFrame(HANDLE pipe)
	{
		uint32_t size = 0;
		DWORD read = 0;
		Require(::ReadFile(pipe, &size, sizeof(size), &read, nullptr) == TRUE && read == sizeof(size),
			"Could not read capture frame length.");
		Require(size > 0 && size <= StudioCapture::MaxMessageBytes, "Capture frame length was invalid.");
		std::string payload(size, '\0');
		Require(::ReadFile(pipe, payload.data(), size, &read, nullptr) == TRUE && read == size,
			"Could not read capture frame payload.");
		return payload;
	}

	void TestTransportHandshakeAndSnapshot()
	{
		uint32_t session = 0;
		const auto pipeName = CurrentPipeName(session);
		HANDLE server = ::CreateNamedPipeW(pipeName.c_str(), PIPE_ACCESS_DUPLEX,
			PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 8192, 8192, 0, nullptr);
		Require(server != INVALID_HANDLE_VALUE, "Could not create the capture test pipe.");
		std::promise<void> readyPromise;
		std::promise<bool> snapshotPromise;
		auto ready = readyPromise.get_future();
		auto snapshot = snapshotPromise.get_future();
		std::thread peer([&]()
		{
			try
			{
				Require(::ConnectNamedPipe(server, nullptr) == TRUE || ::GetLastError() == ERROR_PIPE_CONNECTED,
					"Native capture did not connect to the test pipe.");
				const std::string request = "{\"version\":1,\"type\":\"capture.start\",\"captureId\":\"transport-test\",\"sessionId\":" +
					std::to_string(session) + ",\"includeOriginal\":true}";
				WriteFrame(server, request);
				const auto readyFrame = ReadFrame(server);
				Require(readyFrame.find("\"type\":\"capture.ready\"") != std::string::npos,
					"Native capture did not acknowledge the request.");
				readyPromise.set_value();
				const auto snapshotFrame = ReadFrame(server);
				snapshotPromise.set_value(snapshotFrame.find("\"type\":\"menu.snapshot\"") != std::string::npos);
			}
			catch(...)
			{
				try { readyPromise.set_exception(std::current_exception()); } catch(...) {}
				try { snapshotPromise.set_exception(std::current_exception()); } catch(...) {}
			}
		});

		StudioCapture capture;
		Require(capture.Start(), "Native capture worker did not start.");
		Require(ready.wait_for(std::chrono::seconds(3)) == std::future_status::ready,
			"Native capture worker did not complete its handshake.");
		ready.get();
		menuitem_t root;
		auto *item = new menuitem_t();
		item->title = L"Transport";
		item->name = L"transport";
		root.items.push_back(item);
		StudioCaptureMetadata metadata;
		metadata.context = L"file";
		Require(capture.PublishOriginal(&root, metadata), "Native capture did not queue a snapshot.");
		Require(snapshot.wait_for(std::chrono::seconds(3)) == std::future_status::ready && snapshot.get(),
			"Native capture did not publish a snapshot frame.");
		capture.Stop();
		peer.join();
		::CloseHandle(server);
	}

	std::vector<std::string> EvidenceVersionFixtures()
	{
		StudioCaptureMetadata metadata;
		std::vector<std::string> fixtures{
			StudioCapture::SerializeOriginalForTesting(nullptr, metadata),
			StudioCapture::SerializeFinalForTesting({}, metadata, {})};

		menuitem_t rawRoot;
		auto *rawParent = new menuitem_t();
		rawParent->type = 1;
		rawParent->title = L"System popup";
		rawParent->name = L"system popup";
		auto *rawChild = new menuitem_t();
		rawChild->title = L"System child";
		rawChild->name = L"system child";
		rawParent->items.push_back(rawChild);
		rawRoot.items.push_back(rawParent);

		MenuItemInfo finalParent, systemChild, customChild;
		finalParent.title.text = L"Final popup";
		finalParent.title.normalize = L"final popup";
		finalParent.hSubMenu = reinterpret_cast<HMENU>(1);
		systemChild.title.text = L"System child";
		systemChild.is_system = true;
		customChild.title.text = L"Source-free custom child";
		finalParent.items = {&systemChild, &customChild};
		// Empty ledgers and absent source identities must still version completeness.
		for(const bool effectiveSettings : {false, true})
		{
			metadata.hasEffectiveSettings = effectiveSettings;
			fixtures.push_back(StudioCapture::SerializeOriginalForTesting(&rawRoot, metadata));
			fixtures.push_back(StudioCapture::SerializeFinalForTesting({&finalParent}, metadata, {}));
			fixtures.push_back(StudioCapture::SerializeFinalForTesting(finalParent.items, metadata,
				L"final popup"));
		}
		return fixtures;
	}

	void RequireVersionedEvidence(const Value &value)
	{
		if(value.kind == Kind::Object)
		{
			if((value.Find("phase") || value.Find("id")) &&
				(value.Find("completeness") || value.Find("source") ||
				value.Find("ruleOutcomes") || value.Find("propertyEffects") ||
				value.Find("effectiveSettings")))
			{
				const auto *version = value.Find("evidenceVersion");
				Require(version && version->kind == Kind::Number && version->text == "1",
					"Completeness or evidence is missing its supported evidence version.");
			}
			for(const auto &member : value.object)
				RequireVersionedEvidence(member.second);
		}
		else if(value.kind == Kind::Array)
			for(const auto &item : value.array)
				RequireVersionedEvidence(item);
	}

	void TestEvidenceVersionsWithoutSourceOrLedger()
	{
		for(const auto &json : EvidenceVersionFixtures())
			RequireVersionedEvidence(Parse(json)); // Parse also rejects duplicate members.
	}

	void ExportEvidenceVersionFixtures(const std::filesystem::path &path)
	{
		std::ofstream output(path, std::ios::binary | std::ios::trunc);
		Require(output.good(), "Could not open the capture fixture output.");
		const auto fixtures = EvidenceVersionFixtures();
		for(const auto &json : fixtures)
		{
			Require(!json.empty() && json.size() <= StudioCapture::MaxMessageBytes,
				"Capture fixture exceeded its protocol bound.");
			output << json << '\n';
		}
		output.close();
		Require(!output.fail(), "Could not write the capture fixtures.");
		std::cout << "Exported " << fixtures.size() << " native capture fixtures\n";
	}

	void TestOriginalStateAndReservedImage()
	{
		menuitem_t root;
		auto *item = new menuitem_t();
		item->type = 0;
		item->title = L"Native item";
		item->name = L"native item";
		item->keys = L"Ctrl+N";
		item->disabled = true;
		item->checked = 1;
		item->radio_check = true;
		item->is_default = true;
		item->owner_draw = true;
		item->image = HBMMENU_CALLBACK;
		root.items.push_back(item);

		StudioCaptureMetadata metadata;
		metadata.context = L"file";
		const auto json = StudioCapture::SerializeOriginalForTesting(&root, metadata);
		const auto document = Parse(json);
		const auto *original = Member(document, "original");
		Require(original->kind == Kind::Array && original->array.size() == 1,
			"Original capture entry was not serialized.");
		const auto &entry = original->array.front();
		Require(Member(entry, "radio")->boolean, "Original radio state was lost.");
		Require(Member(entry, "isDefault")->boolean, "Original default state was lost.");
		Require(Member(entry, "ownerDraw")->boolean, "Original owner-draw state was lost.");
		Require(Member(entry, "keys")->text == "Ctrl+N", "Original accelerator keys were lost.");
		const auto *image = Member(entry, "image");
		Require(Member(*image, "status")->text == "unavailable",
			"Reserved menu bitmap was not reported as unavailable.");
		Require(Member(*image, "reason")->text.find("reserved") != std::string::npos,
			"Reserved menu bitmap did not carry a reason.");
	}

	void TestFinalOwnerDrawDoesNotInspectCallbackData()
	{
		MenuItemInfo item;
		item.fType = MFT_OWNERDRAW | MFT_RADIOCHECK;
		item.fState = MFS_DEFAULT | MFS_CHECKED;
		item.dwItemData = 1;
		item.title.text = L"Owner drawn";
		item.title.normalize = L"Owner drawn";
		item.title.raw = L"Owner drawn";

		StudioCaptureMetadata metadata;
		std::vector<MenuItemInfo *> entries{&item};
		const auto json = StudioCapture::SerializeFinalForTesting(entries, metadata, {});
		const auto document = Parse(json);
		const auto *serialized = Member(Member(document, "entries")->array.front(), "ownerDraw");
		Require(serialized->boolean, "Final owner-draw state was lost.");
		Require(Member(Member(document, "entries")->array.front(), "radio")->boolean,
			"Final radio state was lost.");
		Require(Member(Member(document, "entries")->array.front(), "isDefault")->boolean,
			"Final default state was lost.");
		Require(Member(Member(document, "entries")->array.front(), "keys")->text.empty(),
			"Unexpected final accelerator keys were emitted.");
}

	void TestEntryBudgetStopsSiblingTraversal()
	{
		menuitem_t root;
		std::vector<menuitem_t *> items;
		items.reserve(4097);
		for(size_t index = 0; index < 4097; ++index)
		{
			auto *item = new menuitem_t();
			item->type = 0;
			item->title = L"Item";
			item->name = L"item";
			items.push_back(item);
			root.items.push_back(item);
		}

		StudioCaptureMetadata metadata;
		const auto json = StudioCapture::SerializeOriginalForTesting(&root, metadata);
		const auto document = Parse(json);
		Require(Member(document, "original")->array.size() == 4096,
			"Original capture exceeded its entry budget.");
	}

	void TestCompletenessLimitsUseNullableIntegers()
	{
		menuitem_t root;
		auto *item = new menuitem_t();
		item->type = 1;
		item->title = L"Limited";
		item->name = L"limited";
		item->studio_completeness.state = "unavailable";
		item->studio_completeness.depthLimit = 64;
		item->studio_completeness.itemLimit = 4096;
		item->studio_completeness.evaluationLimit = 50000;
		root.items.push_back(item);

		StudioCaptureMetadata metadata;
		const auto document = Parse(StudioCapture::SerializeOriginalForTesting(&root, metadata));
		const auto *completeness = Member(Member(document, "original")->array.front(),
			"completeness");
		Require(Member(*completeness, "depthLimit")->kind == Kind::Number &&
			Member(*completeness, "depthLimit")->text == "64",
			"Depth limit was not serialized as a numeric bound.");
		Require(Member(*completeness, "itemLimit")->kind == Kind::Number &&
			Member(*completeness, "itemLimit")->text == "4096",
			"Item limit was not serialized as a numeric bound.");
		Require(Member(*completeness, "evaluationLimit")->kind == Kind::Number &&
			Member(*completeness, "evaluationLimit")->text == "50000",
			"Evaluation limit was not serialized as a numeric bound.");
		const auto *diagnostics = Member(*completeness, "diagnostics");
		Require(diagnostics && diagnostics->kind == Kind::Array,
			"Completeness diagnostics were not serialized as an array.");
		item->studio_completeness.diagnostics.push_back(L"CAPTURE_DEPTH_LIMIT: test");
		const auto diagnosticDocument = Parse(
			StudioCapture::SerializeOriginalForTesting(&root, metadata));
		const auto *diagnosticsValue = Member(Member(diagnosticDocument, "original")
			->array.front(), "completeness")->Find("diagnostics");
		Require(diagnosticsValue != nullptr && diagnosticsValue->kind == Kind::Array &&
			!diagnosticsValue->array.empty(), "Completeness diagnostic entry was missing.");
		const auto &diagnostic = diagnosticsValue->array.front();
		Require(Member(diagnostic, "code")->text == "CAPTURE_INCOMPLETE" &&
			Member(diagnostic, "message")->text == "CAPTURE_DEPTH_LIMIT: test" &&
			Member(diagnostic, "severity")->text == "warning",
			"Completeness diagnostics did not match the managed Diagnostic contract.");
	}

	void TestStructuredEvidenceAndEffectiveSettings()
	{
		// The serializer only borrows parsed definitions.  Keep this fixture alive
		// until process exit so the focused serializer target does not need the
		// full expression runtime solely to destroy an otherwise inert NativeMenu.
		auto *source = new Nilesoft::Shell::NativeMenu(true);
		source->source_file = L"config.nss";
		source->source_node_id = L"n12";
		source->source_hash = "hash-12";
		source->source_occurrence_id = L"import-1";
		source->source_end = 34;

		MenuItemInfo item;
		item.title.text = L"Renamed";
		item.title.normalize = L"renamed";
		item.owner_dynamic = source;

		Nilesoft::Shell::StudioCaptureRuleOutcome outcome;
		outcome.source = source;
		outcome.ruleId = "dynamic.title";
		outcome.outcome = "matched";
		outcome.reason = L"renamed";
		item.evidence.ruleOutcomes.push_back(outcome);

		Nilesoft::Shell::StudioCapturePropertyEffect effect;
		effect.source = source;
		effect.property = "title";
		effect.effect = "applied";
		effect.value = L"Renamed";
		item.evidence.propertyEffects.push_back(effect);

		StudioCaptureMetadata metadata;
		metadata.hasEffectiveSettings = true;
		metadata.modifyItemsEnabled = false;
		metadata.modifyItemsTitle = false;
		metadata.modifyItemsImage = 0;
		metadata.modifyItemsPosition = 0;
		metadata.newItemsEnabled = true;
		std::vector<MenuItemInfo *> entries{&item};
		const auto document = Parse(StudioCapture::SerializeFinalForTesting(entries,
			metadata, {}));

		Require(Member(document, "evidenceVersion")->kind == Kind::Number &&
			Member(document, "evidenceVersion")->text == "1",
			"Effective settings evidence version was not serialized.");
		const auto *settings = Member(document, "effectiveSettings");
		Require(settings->kind == Kind::Object, "Effective settings were not serialized.");
		const auto *modifyItems = Member(*settings, "modifyItems");
		Require(!Member(*modifyItems, "enabled")->boolean &&
			!Member(*modifyItems, "title")->boolean &&
			Member(*modifyItems, "image")->text == "0",
			"Effective modifyItems gates were not preserved.");
		const auto *settingSources = Member(*settings, "sources");
		Require(settingSources->kind == Kind::Array &&
			settingSources->array.size() >= 1,
			"Effective setting source ledger was not serialized.");
		Require(Member(settingSources->array.front(), "source")->kind == Kind::Null,
			"Unavailable effective setting source was not explicit null.");

		const auto &serialized = Member(document, "entries")->array.front();
		const auto *sourceReference = Member(serialized, "source");
		Require(Member(*sourceReference, "file")->text == "config.nss" &&
			Member(*sourceReference, "nodeId")->text == "n12" &&
			Member(*sourceReference, "start")->text == "12" &&
			Member(*sourceReference, "end")->text == "34" &&
			Member(*sourceReference, "occurrenceId")->text == "import-1" &&
			Member(*sourceReference, "hash")->text == "hash-12",
			"Source identity was not serialized losslessly.");
		Require(Member(serialized, "evidenceVersion")->text == "1",
			"Entry evidence version was not serialized.");
		const auto *ruleOutcomes = Member(serialized, "ruleOutcomes");
		Require(ruleOutcomes->kind == Kind::Array && ruleOutcomes->array.size() == 1,
			"Rule outcome evidence was not serialized.");
		Require(Member(ruleOutcomes->array.front(), "ruleId")->text == "dynamic.title" &&
			Member(ruleOutcomes->array.front(), "outcome")->text == "matched",
			"Rule outcome fields were not serialized.");
		const auto *propertyEffects = Member(serialized, "propertyEffects");
		Require(propertyEffects->kind == Kind::Array && propertyEffects->array.size() == 1 &&
			Member(propertyEffects->array.front(), "property")->text == "title" &&
			Member(propertyEffects->array.front(), "effect")->text == "applied" &&
			Member(propertyEffects->array.front(), "value")->text == "Renamed",
			"Property effect evidence was not serialized.");
	}

	void TestEvidenceLimitIsExplicit()
	{
		MenuItemInfo item;
		item.title.text = L"Evidence limit";
		item.title.normalize = L"evidence-limit";
		item.evidence.truncated = true;
		item.evidence.messageLimit =
			Nilesoft::Shell::StudioCaptureEvidence::MaxItems;
		Nilesoft::Shell::StudioCaptureRuleOutcome outcome;
		outcome.ruleId = "dynamic.title";
		outcome.outcome = "matched";
		item.evidence.ruleOutcomes.push_back(std::move(outcome));

		StudioCaptureMetadata metadata;
		std::vector<MenuItemInfo *> entries{&item};
		const auto document = Parse(StudioCapture::SerializeFinalForTesting(entries,
			metadata, {}));
		const auto &serialized = Member(document, "entries")->array.front();
		const auto *completeness = Member(serialized, "completeness");
		Require(Member(*completeness, "state")->text == "unavailable" &&
			Member(*completeness, "childrenCaptured")->boolean &&
			!Member(*completeness, "complete")->boolean &&
			Member(*completeness, "messageLimit")->text == "128",
			"Evidence truncation did not make entry completeness explicit.");
		const auto *diagnostics = Member(*completeness, "diagnostics");
		Require(diagnostics->kind == Kind::Array && !diagnostics->array.empty(),
			"Evidence limit diagnostic was not serialized.");
		Require(Member(diagnostics->array.front(), "code")->text ==
			"CAPTURE_EVIDENCE_LIMIT" &&
			Member(diagnostics->array.front(), "severity")->text == "warning",
			"Evidence limit diagnostic did not use its explicit contract code.");
	}

	void TestImportedSourceWithoutOccurrenceIsUnavailable()
	{
		// A repeated import can produce a valid local file/span while the legacy
		// native parser cannot identify which import occurrence supplied it.  The
		// capture must preserve the semantic entry and evidence, but never expose
		// that partial location as an edit-capable source reference.
		auto *source = new Nilesoft::Shell::NativeMenu(true);
		source->source_file = L"shared.nss";
		source->source_node_id = L"n17";
		source->source_hash = "hash-shared";
		source->source_end = 41;
		source->source_occurrence_unavailable = true;

		MenuItemInfo item;
		item.title.text = L"Imported entry";
		item.title.normalize = L"imported entry";
		item.owner_dynamic = source;
		Nilesoft::Shell::StudioCaptureRuleOutcome outcome;
		outcome.source = source;
		outcome.ruleId = "imported.rule";
		outcome.outcome = "matched";
		item.evidence.ruleOutcomes.push_back(outcome);

		StudioCaptureMetadata metadata;
		std::vector<MenuItemInfo *> entries{&item};
		const auto document = Parse(StudioCapture::SerializeFinalForTesting(entries,
			metadata, {}));
		const auto &serialized = Member(document, "entries")->array.front();
		Require(serialized.Find("source") == nullptr,
			"Ambiguous imported source was emitted as an editable-looking entry source.");
		const auto *completeness = Member(serialized, "completeness");
		Require(Member(*completeness, "state")->text == "unavailable" &&
			!Member(*completeness, "complete")->boolean,
			"Missing imported occurrence did not make completeness unavailable.");
		const auto *diagnostics = Member(*completeness, "diagnostics");
		Require(diagnostics->kind == Kind::Array && !diagnostics->array.empty() &&
			Member(diagnostics->array.front(), "code")->text ==
				"CAPTURE_SOURCE_OCCURRENCE_UNAVAILABLE",
			"Missing imported occurrence did not carry an explicit diagnostic.");
		const auto *outcomes = Member(serialized, "ruleOutcomes");
		Require(outcomes->kind == Kind::Array && outcomes->array.size() == 1 &&
			Member(outcomes->array.front(), "source")->kind == Kind::Null,
			"Ambiguous imported evidence retained a partial source reference.");
	}

	void TestAutomaticCaptureCoordinatorPreservesSafeNestedRows()
	{
		using Nilesoft::Shell::NativeMenuConstruction::AutomaticCaptureCallbacks;
		using Nilesoft::Shell::NativeMenuConstruction::MaterializeAutomaticPopups;

		enum class ActionKind
		{
			None,
			Command,
			AssignmentAndFunction,
		};
		struct Action
		{
			ActionKind kind = ActionKind::None;
			std::wstring command;
			std::wstring arguments;
		};

		std::vector<MenuItemInfo *> allocated;
		auto makeEntry = [&](uint32_t id, std::wstring title, HMENU submenu = nullptr)
		{
			auto *entry = new MenuItemInfo();
			entry->id = id;
			entry->title.text = std::move(title);
			entry->title.normalize = entry->title.text;
			entry->hSubMenu = submenu;
			allocated.push_back(entry);
			return entry;
		};

		const auto submenu = [](uintptr_t value) -> HMENU
		{
			return reinterpret_cast<HMENU>(value);
		};
		auto *root = makeEntry(1, L"Outer", submenu(0x101));
		MenuItemInfo *nested = nullptr;
		MenuItemInfo *denied = nullptr;
		std::unordered_map<MenuItemInfo *, Action> actions;
		bool commandInvoked = false;
		bool assignmentInvoked = false;
		bool functionInvoked = false;

		std::unordered_set<HMENU> activeMenus;
		const AutomaticCaptureCallbacks callbacks{
			.canContinue = []() { return true; },
			.submenu = [](const MenuItemInfo *entry)
			{
				return entry ? entry->hSubMenu : nullptr;
			},
			.entryId = [](const MenuItemInfo *entry)
			{
				return entry ? entry->id : 0;
			},
			.construct = [&](MenuItemInfo *entry, size_t,
				const std::vector<uint32_t> &, std::vector<MenuItemInfo *> &children)
			{
				if(entry == root)
				{
					nested = makeEntry(2, L"Nested", submenu(0x102));
					denied = makeEntry(3, L"Denied", submenu(0x103));
					children = {nested, denied};
					return true;
				}
				if(entry == nested)
				{
					auto *literal = makeEntry(4, L"Literal command row");
					actions.emplace(literal, Action{ActionKind::Command,
						L"tool.exe", L"--safe"});
					auto *noCommand = makeEntry(5, L"No command row");
					actions.emplace(noCommand, Action{ActionKind::None,
						L"", L"--unused"});
					children = {literal, noCommand};
					return true;
				}
				if(entry == denied)
				{
					actions.emplace(entry, Action{ActionKind::AssignmentAndFunction,
						L"cmd('never-run')", L"$mutated = 'bad'"});
					// The automatic capture contract rejects command, assignment,
					// and unknown function evaluation before invoking any of them.
					return false;
				}
				return false;
			},
			.commit = [&](MenuItemInfo *entry,
				std::vector<MenuItemInfo *> &&children, bool constructed)
			{
				entry->items = std::move(children);
				entry->studio_completeness.childrenCaptured = constructed;
				entry->studio_children_captured = constructed;
				entry->studio_completeness.state = constructed ? "materialized" : "unavailable";
				entry->studio_completeness.complete = constructed;
				if(!constructed)
					entry->studio_completeness.diagnostics.emplace_back(
						L"PREVIEW_UNAVAILABLE: command, assignment, or function evaluation was denied.");
			},
			.markIncomplete = [&](MenuItemInfo *, std::wstring_view) {},
		};

		std::vector<MenuItemInfo *> roots{root};
		MaterializeAutomaticPopups(roots, 0, {}, 4, activeMenus, callbacks);

		Require(root->items.size() == 2,
			"Automatic capture coordinator did not preserve both nested branches.");
		Require(nested != nullptr && nested->items.size() == 2,
			"Automatic capture coordinator did not materialize the nested custom rows.");
		const auto *literal = nested->items[0];
		const auto *noCommand = nested->items[1];
		Require(actions.at(const_cast<MenuItemInfo *>(literal)).command == L"tool.exe" &&
			actions.at(const_cast<MenuItemInfo *>(literal)).arguments == L"--safe" &&
			literal->title.text.equals(L"Literal command row"),
			"Nested literal title and command arguments were not represented.");
		Require(actions.at(const_cast<MenuItemInfo *>(noCommand)).command.empty() &&
			actions.at(const_cast<MenuItemInfo *>(noCommand)).arguments == L"--unused",
			"No-command row did not remain actionless while retaining its arguments.");
		Require(denied != nullptr && denied->studio_completeness.state == "unavailable" &&
			!denied->studio_completeness.complete &&
			!denied->studio_completeness.diagnostics.empty(),
			"Denied assignment/function branch was treated as complete.");
		Require(!commandInvoked && !assignmentInvoked && !functionInvoked,
			"Automatic capture invoked a denied command, assignment, or function.");

		for(auto *entry : allocated)
			delete entry;
	}

	HBITMAP MakeBitmap(uint32_t width, uint32_t height, uint8_t blue,
		uint8_t green, uint8_t red, uint8_t alpha)
	{
		BITMAPINFO info{};
		info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
		info.bmiHeader.biWidth = static_cast<LONG>(width);
		info.bmiHeader.biHeight = -static_cast<LONG>(height);
		info.bmiHeader.biPlanes = 1;
		info.bmiHeader.biBitCount = 32;
		info.bmiHeader.biCompression = BI_RGB;
		void *bits = nullptr;
		auto bitmap = ::CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits,
			nullptr, 0);
		Require(bitmap != nullptr && bits != nullptr, "Could not create bitmap fixture.");
		auto *pixels = static_cast<uint8_t *>(bits);
		for(uint64_t index = 0; index < static_cast<uint64_t>(width) * height; ++index)
		{
			pixels[index * 4U] = blue;
			pixels[index * 4U + 1U] = green;
			pixels[index * 4U + 2U] = red;
			pixels[index * 4U + 3U] = alpha;
		}
		return bitmap;
	}

	void TestImageNormalizationAndBudget()
	{
		const auto bitmap = MakeBitmap(1, 1, 200, 100, 50, 128);
		menuitem_t root;
		auto *item = new menuitem_t();
		item->image = bitmap;
		item->title = L"Alpha";
		item->name = L"alpha";
		root.items.push_back(item);
		StudioCaptureMetadata metadata;
		const auto json = StudioCapture::SerializeOriginalForTesting(&root, metadata);
		const auto document = Parse(json);
		const auto *imageObject = Member(Member(document, "original")->array.front(), "image");
		const auto *image = Member(*imageObject, "pixels");
		Require(Member(*imageObject, "status")->text == "available",
			"Valid bitmap was not captured.");
		Require(image->text == "ZDIZgA==", "Bitmap channels were not normalized to premultiplied BGRA.");
		root.items.clear();
		delete item;
		::DeleteObject(bitmap);

		const auto budgetBitmap = MakeBitmap(64, 64, 20, 30, 40, 255);
		std::vector<menuitem_t *> items;
		items.reserve(65);
		for(size_t index = 0; index < 65; ++index)
		{
			auto *entry = new menuitem_t();
			entry->image = budgetBitmap;
			entry->title = L"Budget";
			entry->name = L"budget";
			items.push_back(entry);
			root.items.push_back(entry);
		}
		const auto budgetJson = StudioCapture::SerializeOriginalForTesting(&root, metadata);
		const auto budgetDocument = Parse(budgetJson);
		const auto &captured = Member(budgetDocument, "original")->array;
		std::size_t available = 0;
		std::size_t unavailable = 0;
		std::string statusReport;
		for(const auto &entry : captured)
		{
			const auto *capturedImage = Member(entry, "image");
			const auto status = Member(*capturedImage, "status")->text;
			if(status == "available") ++available;
			if(status == "unavailable")
			{
				++unavailable;
				statusReport += std::to_string(unavailable) + ":" +
					(Member(*capturedImage, "reason")->text) + " ";
			}
		}
		Require(available == 64 && unavailable == 1,
			("Aggregate bitmap budget was not enforced per image: available=" +
				std::to_string(available) + " unavailable=" + std::to_string(unavailable) +
				" " + statusReport).c_str());
		::DeleteObject(budgetBitmap);
	}
}

int wmain(int argc, wchar_t *argv[])
{
	try
	{
		if(argc == 3 && std::wstring_view(argv[1]) == L"--export-evidence-fixtures")
		{
			ExportEvidenceVersionFixtures(argv[2]);
			return 0;
		}
		Require(argc == 1, "Usage: StudioCaptureSerializationTests [--export-evidence-fixtures <jsonl>]");
		TestEvidenceVersionsWithoutSourceOrLedger();
		TestOriginalStateAndReservedImage();
		TestFinalOwnerDrawDoesNotInspectCallbackData();
		TestImageNormalizationAndBudget();
		TestEntryBudgetStopsSiblingTraversal();
		TestCompletenessLimitsUseNullableIntegers();
		TestStructuredEvidenceAndEffectiveSettings();
		TestEvidenceLimitIsExplicit();
		TestImportedSourceWithoutOccurrenceIsUnavailable();
		TestAutomaticCaptureCoordinatorPreservesSafeNestedRows();
		TestTransportHandshakeAndSnapshot();
		std::cout << "Studio capture serialization tests passed (11 tests)\n";
		return 0;
	}
	catch(const std::exception &error)
	{
		std::cerr << "Studio capture serialization test failed: " << error.what() << '\n';
		return 1;
	}
}
