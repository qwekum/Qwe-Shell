#pragma once

// The selection snapshot is an explicit, local hand-off for scripts that need
// the complete native selection.  It deliberately writes a new file for every
// call; callers receive the path only after the file has been flushed.

#include <Windows.h>
#include <objbase.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <string>
#include <string_view>
#include <utility>

namespace Nilesoft::Shell::SelectionSnapshot
{
	inline constexpr std::size_t MaxItems = 4096U;
	inline constexpr std::size_t MaxJsonBytes = 4U * 1024U * 1024U;

	namespace detail
	{
		inline bool Append(std::string &json, std::string_view value) noexcept
		{
			if(value.size() > MaxJsonBytes || json.size() > MaxJsonBytes - value.size())
				return false;
			try
			{
				json.append(value.data(), value.size());
				return true;
			}
			catch(...)
			{
				return false;
			}
		}

		inline bool Append(std::string &json, char value) noexcept
		{
			return Append(json, std::string_view(&value, 1));
		}

		inline bool WideToUtf8(const wchar_t *value, std::size_t length,
			std::string &result) noexcept
		{
			result.clear();
			if(length == 0U)
				return true;
			if(!value || length > static_cast<std::size_t>((std::numeric_limits<int>::max)()))
				return false;
			for(std::size_t index = 0U; index < length; ++index)
				if(value[index] == L'\0')
					return false;

			const auto characterCount = static_cast<int>(length);
			const auto byteCount = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
				value, characterCount, nullptr, 0, nullptr, nullptr);
			if(byteCount <= 0 || static_cast<std::size_t>(byteCount) > MaxJsonBytes)
				return false;
			try
			{
				result.resize(static_cast<std::size_t>(byteCount));
			}
			catch(...)
			{
				return false;
			}
			const auto written = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
				value, characterCount, result.data(), byteCount, nullptr, nullptr);
			if(written != byteCount)
			{
				result.clear();
				return false;
			}
			return true;
		}

		inline bool AppendJsonString(std::string &json, const wchar_t *value,
			std::size_t length) noexcept
		{
			std::string utf8;
			if(!WideToUtf8(value, length, utf8) || !Append(json, '"'))
				return false;

			for(const auto byte : utf8)
			{
				const auto code = static_cast<unsigned char>(byte);
				switch(code)
				{
				case '"':
					if(!Append(json, "\\\"")) return false;
					break;
				case '\\':
					if(!Append(json, "\\\\")) return false;
					break;
				case '\b':
					if(!Append(json, "\\b")) return false;
					break;
				case '\f':
					if(!Append(json, "\\f")) return false;
					break;
				case '\n':
					if(!Append(json, "\\n")) return false;
					break;
				case '\r':
					if(!Append(json, "\\r")) return false;
					break;
				case '\t':
					if(!Append(json, "\\t")) return false;
					break;
				default:
					if(code < 0x20U)
					{
						constexpr char Hex[] = "0123456789abcdef";
						if(!Append(json, "\\u00") || !Append(json, Hex[(code >> 4U) & 0x0fU]) ||
							!Append(json, Hex[code & 0x0fU]))
							return false;
					}
					else if(!Append(json, static_cast<char>(code)))
						return false;
					break;
				}
			}
			return Append(json, '"');
		}

		inline bool AppendJsonString(std::string &json, const string &value) noexcept
		{
			const auto length = value.length();
			if(length > 0U && !value.c_str())
				return false;
			return AppendJsonString(json, value.c_str(), length);
		}

		inline std::string_view ContextName(std::uint32_t windowId) noexcept
		{
			switch(windowId)
			{
			case WINDOW_UI: return "ui";
			case WINDOW_SYSMENU: return "system";
			case WINDOW_EDIT: return "edit";
			case WINDOW_START: return "start";
			case WINDOW_TASKBAR: return "taskbar";
			case WINDOW_DESKTOP: return "desktop";
			case WINDOW_EXPLORER: return "explorer";
			case WINDOW_EXPLORER_TREE: return "explorer-tree";
			case WINDOW_COMPUTER: return "computer";
			case WINDOW_RECYCLEBIN: return "recycle-bin";
			case WINDOW_LIBRARIES: return "libraries";
			case WINDOW_HOME: return "home";
			case WINDOW_QUICK_ACCESS: return "quick-access";
			default: return "unknown";
			}
		}

		inline bool BuildJson(const Selections &selection, std::string &json) noexcept
		{
			json.clear();
			if(selection.Items.size() > MaxItems)
				return false;
			try
			{
				json.reserve(512U);
			}
			catch(...)
			{
				return false;
			}

			if(!Append(json, R"({"version":1,"context":")") ||
				!Append(json, ContextName(selection.Window.id)) ||
				!Append(json, selection.Background ? ".background\"" : ".selection\""))
				return false;
			if(!Append(json, R"(,"paths":[)"))
				return false;

			bool first = true;
			for(const auto *item : selection.Items)
			{
				if(!item || item->Path.length() == 0U || !item->Path.c_str())
					return false;
				if(!first && !Append(json, ','))
					return false;
				first = false;
				if(!AppendJsonString(json, item->Path))
					return false;
			}

			if(!Append(json, R"(],"parentPath":)"))
				return false;
			if(!AppendJsonString(json, selection.Parent) ||
				!Append(json, R"(,"isBackground":)"))
				return false;
			if(!Append(json, selection.Background ? "true" : "false") ||
				!Append(json, R"(,"isDesktop":)"))
				return false;
			if(!Append(json, (selection.Window.desktop || selection.Types[FSO_DESKTOP] != 0)
				? "true" : "false") || !Append(json, '}'))
				return false;
			return json.size() <= MaxJsonBytes;
		}

		inline bool GetTempDirectory(std::wstring &directory) noexcept
		{
			try
			{
				std::array<wchar_t, 32768> buffer{};
				const auto length = ::GetTempPathW(static_cast<DWORD>(buffer.size()), buffer.data());
				if(length == 0U || length >= buffer.size())
					return false;
				directory.assign(buffer.data(), length);
				if(directory.empty() || directory.back() != L'\\')
					directory.push_back(L'\\');
				if(directory.rfind(L"\\\\", 0U) == 0U)
					return false;
				std::array<wchar_t, 32768> volumePath{};
				if(!::GetVolumePathNameW(directory.c_str(), volumePath.data(),
					static_cast<DWORD>(volumePath.size())))
					return false;
				const auto driveType = ::GetDriveTypeW(volumePath.data());
				if(driveType == DRIVE_UNKNOWN || driveType == DRIVE_NO_ROOT_DIR ||
					driveType == DRIVE_REMOTE)
					return false;
				return true;
			}
			catch(...)
			{
				return false;
			}
		}

		inline bool NewCandidate(std::wstring &path) noexcept
		{
			GUID guid{};
			if(FAILED(::CoCreateGuid(&guid)))
				return false;
			std::array<wchar_t, 64> guidText{};
			if(::StringFromGUID2(guid, guidText.data(), static_cast<int>(guidText.size())) <= 0)
				return false;
			std::wstring directory;
			if(!GetTempDirectory(directory))
				return false;
			try
			{
				path = std::move(directory);
				path.append(L"qwe-shell-selection-");
				path.append(guidText.data());
				path.append(L".json");
				if(path.size() > 32767U)
				{
					path.clear();
					return false;
				}
				return true;
			}
			catch(...)
			{
				path.clear();
				return false;
			}
		}

		inline bool WriteFileBytes(const std::wstring &path, const std::string &json) noexcept
		{
			const auto file = ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
				CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
			if(file == INVALID_HANDLE_VALUE)
				return false;

			bool ok = true;
			std::size_t offset = 0U;
			while(ok && offset < json.size())
			{
				const auto remaining = json.size() - offset;
				const auto chunkSize = static_cast<DWORD>((std::min)(remaining,
					static_cast<std::size_t>(1U << 20U)));
				DWORD written = 0U;
				ok = ::WriteFile(file, json.data() + offset, chunkSize, &written, nullptr) != FALSE &&
					written == chunkSize;
				offset += written;
			}
			if(ok)
				ok = ::FlushFileBuffers(file) != FALSE;
			const auto closeOk = ::CloseHandle(file) != FALSE;
			if(!ok || !closeOk)
			{
				// CREATE_NEW succeeded above, so this process owns the path and
				// may remove the incomplete output.  Keep this cleanup here:
				// callers must never delete a candidate when CREATE_NEW failed.
				::DeleteFileW(path.c_str());
				::SetLastError(ERROR_WRITE_FAULT);
			}
			return ok && closeOk;
		}

		inline bool WriteUnique(const std::string &json, std::wstring &path) noexcept
		{
			path.clear();
			for(int attempt = 0; attempt < 8; ++attempt)
			{
				std::wstring candidate;
				if(!NewCandidate(candidate))
					return false;
				if(WriteFileBytes(candidate, json))
				{
					try { path = candidate; } catch(...) { ::DeleteFileW(candidate.c_str()); return false; }
					return true;
				}
				const auto error = ::GetLastError();
				if(error == ERROR_FILE_EXISTS || error == ERROR_ALREADY_EXISTS)
					continue;
				return false;
			}
			return false;
		}
	}

	inline bool TryWrite(const Selections &selection, std::wstring &path) noexcept
	{
		path.clear();
		try
		{
			std::string json;
			if(!detail::BuildJson(selection, json) || json.size() > MaxJsonBytes)
				return false;
			return detail::WriteUnique(json, path);
		}
		catch(...)
		{
			path.clear();
			return false;
		}
	}

	inline std::wstring Write(const Selections &selection) noexcept
	{
		std::wstring path;
		(void)TryWrite(selection, path);
		return path;
	}
}
