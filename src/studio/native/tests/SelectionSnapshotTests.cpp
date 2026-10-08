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
		RequireString(value, "context", "explorer.background");
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
}

int main()
{
	try
	{
		TestUnicodeAndCompleteSelection();
		TestUniqueOutputs();
		TestCreateNewOwnership();
		TestItemBounds();
		TestJsonAndInputFailures();
		std::cout << "Selection snapshot tests passed\n";
		return 0;
	}
	catch(const std::exception &error)
	{
		std::cerr << "Selection snapshot tests failed: " << error.what() << "\n";
		return 1;
	}
}
