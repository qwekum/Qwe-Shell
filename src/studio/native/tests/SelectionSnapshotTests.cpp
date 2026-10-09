#include "../../../dll/src/pch.h"
#include "../PreviewJson.h"
#include "../../../dll/src/Include/SelectionSnapshot.h"

#include <Windows.h>

#include <array>
#include <cstddef>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace
{
	using Nilesoft::Shell::Selections;
	using Nilesoft::Shell::SelectionSnapshot::MaxItems;
	using Nilesoft::Shell::SelectionSnapshot::MaxJsonBytes;

	void Require(bool condition, const char *message)
	{
		if(!condition)
			throw std::runtime_error(message);
	}

	struct SelectionFixture
	{
		alignas(Selections) std::array<std::byte, sizeof(Selections)> storage{};
		Selections *selection;
		std::vector<Selections::PathItem *> items;

		SelectionFixture() : selection(::new(storage.data()) Selections())
		{
		}

		~SelectionFixture() noexcept
		{
			selection->Items.clear();
			for(auto *item : items)
				delete item;
		}

		void AddPath(std::wstring_view value)
		{
			auto *item = new Selections::PathItem();
			item->Path.assign(value.data(), value.size());
			selection->Items.push_back(item);
			items.push_back(item);
		}

		void AddNullPath()
		{
			selection->Items.push_back(nullptr);
		}

		void SetParent(std::wstring_view value)
		{
			selection->Parent.assign(value.data(), value.size());
		}
	};

	bool ReadUtf8(const std::wstring &path, std::string &result)
	{
		result.clear();
		const auto file = ::CreateFileW(path.c_str(), GENERIC_READ,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
			OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
		if(file == INVALID_HANDLE_VALUE)
			return false;

		LARGE_INTEGER size{};
		bool ok = ::GetFileSizeEx(file, &size) != FALSE && size.QuadPart >= 0 &&
			size.QuadPart <= static_cast<LONGLONG>(MaxJsonBytes);
		if(ok)
		{
			try
			{
				result.resize(static_cast<std::size_t>(size.QuadPart));
			}
			catch(...)
			{
				ok = false;
			}
		}
		std::size_t offset = 0U;
		while(ok && offset < result.size())
		{
			const auto remaining = result.size() - offset;
			const auto request = static_cast<DWORD>((std::min)(remaining,
				static_cast<std::size_t>(1U << 20U)));
			DWORD read = 0U;
			ok = ::ReadFile(file, result.data() + offset, request, &read, nullptr) != FALSE &&
				read > 0U;
			offset += read;
		}
		const auto closeOk = ::CloseHandle(file) != FALSE;
		return ok && closeOk && offset == result.size();
	}

	ShellStudio::PreviewJson::Value ParseSnapshot(const std::wstring &path)
	{
		std::string json;
		Require(ReadUtf8(path, json), "snapshot file could not be read");
		ShellStudio::PreviewJson::Limits limits;
		limits.maxBytes = MaxJsonBytes;
		limits.maxStringBytes = MaxJsonBytes;
		ShellStudio::PreviewJson::Value value;
		std::string error;
		Require(ShellStudio::PreviewJson::Parser(json, limits).Parse(value, error),
			error.empty() ? "snapshot JSON did not parse" : error.c_str());
		return value;
	}

	const ShellStudio::PreviewJson::Value *Member(
		const ShellStudio::PreviewJson::Value &object, std::string_view name)
	{
		const auto *value = object.Find(name);
		Require(value != nullptr, "snapshot JSON omitted a required member");
		return value;
	}

	void RequireString(const ShellStudio::PreviewJson::Value &object,
		std::string_view name, std::string_view expected)
	{
		const auto *value = Member(object, name);
		Require(value->kind == ShellStudio::PreviewJson::Kind::String,
			"snapshot member is not a JSON string");
		Require(value->text == expected, "snapshot JSON string had the wrong value");
	}

	void RequireBool(const ShellStudio::PreviewJson::Value &object,
		std::string_view name, bool expected)
	{
		const auto *value = Member(object, name);
		Require(value->kind == ShellStudio::PreviewJson::Kind::Boolean,
			"snapshot member is not a JSON boolean");
		Require(value->boolean == expected, "snapshot JSON boolean had the wrong value");
	}

	void RemoveSnapshot(const std::wstring &path)
	{
		Require(!path.empty(), "test received an empty snapshot path");
		Require(::DeleteFileW(path.c_str()) != FALSE, "test snapshot cleanup failed");
		Require(::GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES,
			"test snapshot remained after cleanup");
	}

	void RequireOutputName(const std::wstring &path)
	{
		const auto separator = path.find_last_of(L"\\/");
		const auto name = path.substr(separator == std::wstring::npos ? 0U : separator + 1U);
		Require(name.rfind(L"qwe-shell-selection-{", 0U) == 0U,
			"snapshot did not use the unique selection filename prefix");
		Require(name.size() > 22U && name.substr(name.size() - 6U) == L"}.json",
			"snapshot did not use the UUID filename suffix");
	}

	void TestUnicodeAndCompleteSelection()
	{
		SelectionFixture fixture;
		fixture.selection->Window.id = Nilesoft::Shell::WINDOW_EXPLORER;
		fixture.selection->Window.desktop = true;
		fixture.selection->Background = true;
		fixture.SetParent(L"C:\\Root\\\"quoted\"");
		fixture.AddPath(L"C:\\\x4f8b\x5b50\\\xD83D\xDE00.txt");
		fixture.AddPath(L"D:\\second\\item.bin");

		std::wstring first;
		Require(Nilesoft::Shell::SelectionSnapshot::TryWrite(*fixture.selection, first),
			"complete Unicode selection snapshot was not written");
		RequireOutputName(first);
		const auto value = ParseSnapshot(first);
		Require(value.kind == ShellStudio::PreviewJson::Kind::Object,
			"snapshot root is not a JSON object");
		const auto *version = Member(value, "version");
		Require(version->kind == ShellStudio::PreviewJson::Kind::Number && version->text == "1",
			"snapshot version is not 1");
		RequireString(value, "context", "desktop");
		RequireString(value, "parentPath", "C:\\Root\\\"quoted\"");
		RequireBool(value, "isBackground", true);
		RequireBool(value, "isDesktop", true);
		const auto *paths = Member(value, "paths");
		Require(paths->kind == ShellStudio::PreviewJson::Kind::Array && paths->array.size() == 2U,
			"snapshot did not retain every selected path");
		Require(paths->array[0].kind == ShellStudio::PreviewJson::Kind::String &&
			paths->array[0].text == "C:\\\xE4\xBE\x8B\xE5\xAD\x90\\\xF0\x9F\x98\x80.txt",
			"snapshot lost Unicode path content");
		Require(paths->array[1].kind == ShellStudio::PreviewJson::Kind::String &&
			paths->array[1].text == "D:\\second\\item.bin",
			"snapshot lost the second selected path");
		RemoveSnapshot(first);
		std::cout << "PASS complete-selection-unicode\n";
	}

	void TestUniqueOutputs()
	{
		SelectionFixture fixture;
		fixture.selection->Window.id = Nilesoft::Shell::WINDOW_DESKTOP;
		fixture.AddPath(L"C:\\one.txt");
		std::wstring first;
		std::wstring second;
		Require(Nilesoft::Shell::SelectionSnapshot::TryWrite(*fixture.selection, first),
			"first unique snapshot was not written");
		Require(Nilesoft::Shell::SelectionSnapshot::TryWrite(*fixture.selection, second),
			"second unique snapshot was not written");
		Require(first != second, "two snapshots reused the same path");
		Require(::GetFileAttributesW(first.c_str()) != INVALID_FILE_ATTRIBUTES &&
			::GetFileAttributesW(second.c_str()) != INVALID_FILE_ATTRIBUTES,
			"unique snapshot output was not present");
		RemoveSnapshot(first);
		RemoveSnapshot(second);
		std::cout << "PASS unique-output\n";
	}

	void TestSupportedContexts()
	{
		const auto check = [](SelectionFixture &fixture, std::string_view expected)
		{
			std::wstring output;
			Require(Nilesoft::Shell::SelectionSnapshot::TryWrite(*fixture.selection, output), "context snapshot failed");
			const auto value = ParseSnapshot(output);
			RequireString(value, "context", expected);
			RequireBool(value, "isBackground", fixture.selection->Background);
			RequireBool(value, "isDesktop", fixture.selection->Window.desktop || fixture.selection->Types[Nilesoft::Shell::FSO_DESKTOP] != 0);
			RemoveSnapshot(output);
		};
		SelectionFixture desktop;
		desktop.selection->Window.id = Nilesoft::Shell::WINDOW_DESKTOP;
		check(desktop, "desktop");
		desktop.selection->Background = true;
		check(desktop, "desktop");
		SelectionFixture background;
		background.selection->Window.id = Nilesoft::Shell::WINDOW_EXPLORER;
		background.selection->Background = true;
		check(background, "explorer.background");
		SelectionFixture folder;
		folder.selection->Window.id = Nilesoft::Shell::WINDOW_EXPLORER;
		folder.AddPath(L"C:\\folder");
		folder.items[0]->Group = Nilesoft::IO::PathType::Directory;
		check(folder, "explorer.folder");
		folder.AddPath(L"C:\\second-folder");
		folder.items[1]->Group = Nilesoft::IO::PathType::Directory;
		check(folder, "explorer.selection");
		SelectionFixture file;
		file.AddPath(L"C:\\file.txt");
		check(file, "explorer.selection");
		std::cout << "PASS supported-contexts\n";
	}

	void TestCreateNewOwnership()
	{
		std::wstring collision;
		Require(Nilesoft::Shell::SelectionSnapshot::detail::NewCandidate(collision),
			"could not create a collision test path");
		const auto file = ::CreateFileW(collision.c_str(), GENERIC_WRITE, 0, nullptr,
			CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
		Require(file != INVALID_HANDLE_VALUE, "could not create collision test file");
		const std::string sentinel = "preserve-existing-file";
		DWORD written = 0U;
		const auto writeOk = ::WriteFile(file, sentinel.data(),
			static_cast<DWORD>(sentinel.size()), &written, nullptr) != FALSE &&
			written == sentinel.size();
		Require(::CloseHandle(file) != FALSE && writeOk,
			"could not initialize collision test file");

		Require(!Nilesoft::Shell::SelectionSnapshot::detail::WriteFileBytes(collision, "{}"),
			"CREATE_NEW unexpectedly replaced an existing file");
		std::string retained;
		Require(ReadUtf8(collision, retained) && retained == sentinel,
			"failed CREATE_NEW did not preserve the existing file");
		Require(::DeleteFileW(collision.c_str()) != FALSE,
			"collision test file cleanup failed");
		std::cout << "PASS create-new-ownership\n";
	}

	void TestExplicitUserSecurity()
	{
		SelectionFixture fixture;
		fixture.AddPath(L"C:\\SnapshotFixture\\security.txt");
		std::wstring path;
		Require(Nilesoft::Shell::SelectionSnapshot::TryWrite(*fixture.selection, path),
			"explicit-owner snapshot creation failed");
		try
		{
			HANDLE token = nullptr;
			Require(::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token) != FALSE,
				"current user token query failed");
			alignas(TOKEN_USER) std::array<BYTE, sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE> userBytes{};
			DWORD returned = 0U;
			const auto queried = ::GetTokenInformation(token, TokenUser, userBytes.data(),
				static_cast<DWORD>(userBytes.size()), &returned) != FALSE;
			::CloseHandle(token);
			Require(queried, "current user SID query failed");
			const auto user = reinterpret_cast<const TOKEN_USER *>(userBytes.data())->User.Sid;
			alignas(SECURITY_DESCRIPTOR) std::array<BYTE, 1024> securityBytes{};
			Require(::GetFileSecurityW(path.c_str(), OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
				securityBytes.data(), static_cast<DWORD>(securityBytes.size()), &returned) != FALSE,
				"emitted snapshot security query failed");
			PSID owner = nullptr;
			BOOL defaulted = FALSE;
			Require(::GetSecurityDescriptorOwner(securityBytes.data(), &owner, &defaulted) != FALSE &&
				owner && ::EqualSid(user, owner), "emitted snapshot owner differs from TokenUser (including elevated tokens)");
			SECURITY_DESCRIPTOR_CONTROL control = 0U;
			DWORD revision = 0U;
			Require(::GetSecurityDescriptorControl(securityBytes.data(), &control, &revision) != FALSE &&
				(control & SE_DACL_PROTECTED) != 0U, "emitted snapshot inherits its directory ACL");
			BOOL present = FALSE;
			PACL acl = nullptr;
			Require(::GetSecurityDescriptorDacl(securityBytes.data(), &present, &acl, &defaulted) != FALSE &&
				present && acl && acl->AceCount == 1U, "emitted snapshot does not have a user-only ACL");
			void *rawAce = nullptr;
			Require(::GetAce(acl, 0U, &rawAce) != FALSE, "emitted snapshot user ACE missing");
			const auto ace = static_cast<const ACCESS_ALLOWED_ACE *>(rawAce);
			Require(ace->Header.AceType == ACCESS_ALLOWED_ACE_TYPE && ace->Header.AceFlags == 0U &&
				ace->Mask == FILE_ALL_ACCESS && ::EqualSid(user, const_cast<DWORD *>(&ace->SidStart)),
				"emitted snapshot grants unrelated or inherited access");
		}
		catch(...)
		{
			::DeleteFileW(path.c_str());
			throw;
		}
		Require(::DeleteFileW(path.c_str()) != FALSE, "explicit-owner test cleanup failed");
		std::cout << "PASS explicit-user-owner-protected-acl\n";
	}

	void TestItemBounds()
	{
		SelectionFixture accepted;
		for(std::size_t index = 0U; index < MaxItems; ++index)
			accepted.AddPath(L"C:\\item");
		std::wstring output;
		Require(Nilesoft::Shell::SelectionSnapshot::TryWrite(*accepted.selection, output),
			"the maximum supported item count was rejected");
		RemoveSnapshot(output);

		SelectionFixture rejected;
		for(std::size_t index = 0U; index <= MaxItems; ++index)
			rejected.AddPath(L"C:\\item");
		output.clear();
		Require(!Nilesoft::Shell::SelectionSnapshot::TryWrite(*rejected.selection, output) &&
			output.empty(), "an oversized item count was accepted or returned a file");
		std::cout << "PASS item-bounds\n";
	}

	void TestJsonAndInputFailures()
	{
		SelectionFixture emptyPath;
		emptyPath.selection->Window.id = Nilesoft::Shell::WINDOW_UI;
		emptyPath.AddPath(L"");
		std::wstring output;
		Require(!Nilesoft::Shell::SelectionSnapshot::TryWrite(*emptyPath.selection, output) &&
			output.empty(), "an empty selected path was accepted");
		Require(Nilesoft::Shell::SelectionSnapshot::Write(*emptyPath.selection).empty(),
			"Write returned a path after an invalid selection");

		SelectionFixture nullPath;
		nullPath.AddNullPath();
		output.clear();
		Require(!Nilesoft::Shell::SelectionSnapshot::TryWrite(*nullPath.selection, output) &&
			output.empty(), "a null selected item was accepted");

		SelectionFixture invalidUnicode;
		invalidUnicode.AddPath(std::wstring(1U, static_cast<wchar_t>(0xD800U)));
		output.clear();
		Require(!Nilesoft::Shell::SelectionSnapshot::TryWrite(*invalidUnicode.selection, output) &&
			output.empty(), "an unpaired UTF-16 surrogate was accepted");

		SelectionFixture embeddedNull;
		embeddedNull.AddPath(std::wstring({L'C', L':', L'\\', L'x', L'\0', L'y'}));
		output.clear();
		Require(!Nilesoft::Shell::SelectionSnapshot::TryWrite(*embeddedNull.selection, output) &&
			output.empty(), "an embedded null path was accepted");

		SelectionFixture oversizedJson;
		oversizedJson.AddPath(std::wstring(MaxJsonBytes + 128U, L'x'));
		output.clear();
		Require(!Nilesoft::Shell::SelectionSnapshot::TryWrite(*oversizedJson.selection, output) &&
			output.empty(), "an oversized UTF-8 snapshot was accepted");
		std::cout << "PASS input-and-json-failures\n";
	}

	void ExportManagedFixtures(const std::wstring &manifestPath)
	{
		Require(!manifestPath.empty() && !std::filesystem::path(manifestPath).is_relative(),
			"fixture manifest path must be absolute");
		namespace Snapshot = Nilesoft::Shell::SelectionSnapshot;
		std::vector<std::wstring> outputs;
		std::string manifest = R"({"version":1,"producer":"SelectionSnapshot::TryWrite","fixtures":[)";
		const auto append = [&manifest](std::string_view text)
		{
			Require(Snapshot::detail::Append(manifest, text), "fixture manifest exceeds its bound");
		};
		const auto text = [&manifest](const wchar_t *value, std::size_t length)
		{
			Require(Snapshot::detail::AppendJsonString(manifest, value, length), "fixture manifest text is invalid");
		};
		const auto add = [&](std::string_view id, SelectionFixture &fixture, std::string_view context,
			std::string_view supported, std::string_view rejected)
		{
			std::wstring output;
			Require(Snapshot::TryWrite(*fixture.selection, output), "native fixture emission failed");
			outputs.push_back(output);
			if(outputs.size() > 1U) append(",");
			append(R"({"id":")"); append(id); append(R"(","snapshotPath":)"); text(output.data(), output.size());
			append(R"(,"expectedContext":")"); append(context); append(R"(","expectedPaths":[)");
			bool first = true;
			for(const auto *item : fixture.selection->Items)
			{
				if(!first) append(",");
				first = false;
				text(item->Path.c_str(), item->Path.length());
			}
			append(R"(],"expectedTypes":[)"); first = true;
			for(const auto *item : fixture.selection->Items)
			{
				if(!first) append(",");
				first = false;
				append(item->IsDirectory() ? "\"directory\"" : "\"file\"");
			}
			append(R"(],"expectedParentPath":)");
			text(fixture.selection->Parent.c_str(), fixture.selection->Parent.length());
			append(R"(,"expectedIsBackground":)"); append(fixture.selection->Background ? "true" : "false");
			append(R"(,"expectedIsDesktop":)"); append(fixture.selection->Window.desktop ? "true" : "false");
			append(R"(,"supportedOperation":")"); append(supported);
			append(R"(","rejectedOperation":")"); append(rejected); append("\"}");
		};
		try
		{
			const auto initialize = [](SelectionFixture &fixture)
			{
				fixture.selection->Window.id = Nilesoft::Shell::WINDOW_EXPLORER;
				fixture.SetParent(L"C:\\SnapshotFixture");
			};
			SelectionFixture folder;
			initialize(folder); folder.AddPath(L"C:\\SnapshotFixture\\folder \x4f8b\x5b50");
			folder.items[0]->Group = Nilesoft::IO::PathType::Directory;
			add("folder-one", folder, "explorer.folder", "folder.type.set", "capture.window");
			folder.AddPath(L"C:\\SnapshotFixture\\second folder");
			folder.items[1]->Group = Nilesoft::IO::PathType::Directory;
			add("folder-many", folder, "explorer.selection", "files.unblock", "folder.type.set");
			SelectionFixture files;
			initialize(files); files.AddPath(L"C:\\SnapshotFixture\\file \x4f8b.txt");
			files.items[0]->Group = Nilesoft::IO::PathType::File;
			add("file-one", files, "explorer.selection", "files.unblock", "folder.type.set");
			files.AddPath(L"C:\\SnapshotFixture\\second \xD83D\xDE00.txt");
			files.items[1]->Group = Nilesoft::IO::PathType::File;
			add("file-many", files, "explorer.selection", "launch.user-script", "launch.terminal");
			SelectionFixture mixed;
			initialize(mixed); mixed.AddPath(L"C:\\SnapshotFixture\\file \x4f8b.txt");
			mixed.items[0]->Group = Nilesoft::IO::PathType::File;
			mixed.AddPath(L"C:\\SnapshotFixture\\folder \x4f8b\x5b50");
			mixed.items[1]->Group = Nilesoft::IO::PathType::Directory;
			add("mixed", mixed, "explorer.selection", "launch.custom", "folder.type.set");
			SelectionFixture desktop;
			initialize(desktop); desktop.selection->Window.id = Nilesoft::Shell::WINDOW_DESKTOP;
			desktop.selection->Window.desktop = true; desktop.selection->Background = true;
			desktop.AddPath(L"C:\\SnapshotFixture\\Desktop"); desktop.items[0]->Group = Nilesoft::IO::PathType::Directory;
			add("desktop", desktop, "desktop", "launch.user-script", "files.unblock");
			SelectionFixture background;
			initialize(background); background.selection->Background = true;
			background.AddPath(L"C:\\SnapshotFixture\\Background"); background.items[0]->Group = Nilesoft::IO::PathType::Directory;
			add("explorer-background", background, "explorer.background", "launch.terminal", "folder.type.set");
			append("]}");
			Require(Snapshot::detail::WriteFileBytes(manifestPath, manifest), "fixture manifest could not be created (use a new path)");
			std::cout << "PASS exported-managed-fixtures (7 native snapshots)\n";
		}
		catch(...)
		{
			for(const auto &output : outputs) ::DeleteFileW(output.c_str());
			throw;
		}
	}
}

int wmain(int argc, wchar_t **argv)
{
	try
	{
		if(argc != 1 && (argc != 3 || std::wstring_view(argv[1]) != L"--export-managed-fixtures"))
			throw std::runtime_error("Usage: SelectionSnapshotTests.exe [--export-managed-fixtures ABSOLUTE_NEW_MANIFEST_PATH]");
		TestUnicodeAndCompleteSelection();
		TestUniqueOutputs();
		TestSupportedContexts();
		TestCreateNewOwnership();
		TestExplicitUserSecurity();
		TestItemBounds();
		TestJsonAndInputFailures();
		std::cout << "Selection snapshot tests passed\n";
		if(argc == 3) ExportManagedFixtures(argv[2]);
		return 0;
	}
	catch(const std::exception &error)
	{
		std::cerr << "Selection snapshot tests failed: " << error.what() << "\n";
		return 1;
	}
}
