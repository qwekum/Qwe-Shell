#include <pch.h>

#include "Include/ContextMenu.h"
#include "Include/StudioCapture.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <chrono>
#include <limits>
#include <memory>
#include <utility>

#include <sddl.h>

#pragma comment(lib, "advapi32.lib")

// Windows headers expose min/max as macros.  The capture serializer uses the
// standard algorithms and must not let those macros rewrite qualified calls.
#ifdef min
#undef min
#endif
#ifdef max
#undef max
#endif

namespace Nilesoft::Shell
{
	namespace
	{
		constexpr size_t kMaxQueueMessages = 8;
		constexpr size_t kMaxEntries = 4096;
		constexpr size_t kMaxDepth = 64;
		constexpr size_t kMaxStringChars = 65536;
		constexpr size_t kMaxCaptureId = 128;
		constexpr size_t kMaxTraceEntries = 64;
		constexpr size_t kMaxTraceChars = 1024;

		bool IsSpace(char value)
		{
			return value == ' ' || value == '\t' || value == '\r' || value == '\n';
		}

		bool AppendUtf8(std::string &destination, std::wstring_view value)
		{
			if(value.empty())
				return true;

			if(value.size() > kMaxStringChars)
				return false;
			const auto count = value.size();
			if(count > static_cast<size_t>(std::numeric_limits<int>::max()))
				return false;

			const auto *source = value.data();
			const auto sourceLength = static_cast<int>(count);
			int length = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
				source, sourceLength, nullptr, 0, nullptr, nullptr);
			if(length <= 0)
			{
				// Keep a malformed source string from aborting capture.  The
				// fallback preserves all valid characters and replaces invalid
				// UTF-16 sequences according to the Windows conversion API.
				length = ::WideCharToMultiByte(CP_UTF8, 0, source, sourceLength,
					nullptr, 0, nullptr, nullptr);
			}
			if(length <= 0)
				return false;

			const auto oldSize = destination.size();
			destination.resize(oldSize + static_cast<size_t>(length));
			if(::WideCharToMultiByte(CP_UTF8, 0, source, sourceLength,
				destination.data() + oldSize, length, nullptr, nullptr) != length)
			{
				destination.resize(oldSize);
				return false;
			}
			return true;
		}

		class JsonBuilder final
		{
		public:
			explicit JsonBuilder(size_t limit = StudioCapture::MaxMessageBytes)
				: limit_(limit)
			{
			}

			bool valid() const { return valid_; }
			std::string take() && { return std::move(value_); }

			void raw(std::string_view value)
			{
				if(!valid_ || value.size() > limit_ - std::min(value_.size(), limit_))
				{
					valid_ = false;
					return;
				}
				value_.append(value.data(), value.size());
			}

			void quoted(std::string_view value)
			{
				if(!valid_)
					return;

				raw("\"");
			static constexpr char hex[] = "0123456789abcdef";
				for(unsigned char character : value)
				{
					switch(character)
					{
						case '"': raw("\\\""); break;
						case '\\': raw("\\\\"); break;
						case '\b': raw("\\b"); break;
						case '\f': raw("\\f"); break;
						case '\n': raw("\\n"); break;
						case '\r': raw("\\r"); break;
						case '\t': raw("\\t"); break;
						default:
							if(character < 0x20)
							{
								char escaped[6] = {'\\', 'u', '0', '0',
									hex[(character >> 4) & 0x0f], hex[character & 0x0f]};
								raw(std::string_view(escaped, sizeof(escaped)));
							}
							else
							{
								char byte = static_cast<char>(character);
								raw(std::string_view(&byte, 1));
							}
							break;
					}
					if(!valid_)
						break;
				}
				raw("\"");
			}

			void quoted(std::wstring_view value)
			{
				std::string utf8;
				if(!AppendUtf8(utf8, value))
				{
					valid_ = false;
					return;
				}
				quoted(utf8);
			}

			void memberKey(std::string_view key, bool &first)
			{
				if(!first)
					raw(",");
				first = false;
				quoted(key);
				raw(":");
			}

			void memberString(bool &first, std::string_view key, std::string_view value)
			{
				memberKey(key, first);
				quoted(value);
			}

			void memberString(bool &first, std::string_view key, std::wstring_view value)
			{
				memberKey(key, first);
				quoted(value);
			}

			void memberUInt(bool &first, std::string_view key, uint64_t value)
			{
				memberKey(key, first);
				char buffer[32]{};
				auto result = std::to_chars(std::begin(buffer), std::end(buffer), value);
				if(result.ec != std::errc{})
					valid_ = false;
				else
					raw(std::string_view(buffer, static_cast<size_t>(result.ptr - buffer)));
			}

			void memberBool(bool &first, std::string_view key, bool value)
			{
				memberKey(key, first);
				raw(value ? "true" : "false");
			}

		private:
			std::string value_;
			size_t limit_;
			bool valid_ = true;
		};

		void BeginArray(JsonBuilder &builder, bool &first, std::string_view key)
		{
			builder.memberKey(key, first);
			builder.raw("[");
		}

		void EndArray(JsonBuilder &builder)
		{
			builder.raw("]");
		}

		void BeginObject(JsonBuilder &builder, bool &first)
		{
			if(!first)
				builder.raw(",");
			first = false;
			builder.raw("{");
		}

		void EndObject(JsonBuilder &builder)
		{
			builder.raw("}");
		}

		std::wstring CopyWide(const Nilesoft::Text::string &value)
		{
			if(value.empty())
				return {};
			return std::wstring(value.c_str(), value.length());
		}

		std::wstring JoinPath(std::wstring_view parent, std::wstring_view child)
		{
			if(parent.empty())
				return std::wstring(child);
			if(child.empty())
				return std::wstring(parent);

			std::wstring result(parent);
			if(result.back() != L'/')
				result.push_back(L'/');
			result.append(child);
			return result;
		}

		std::string Hex(uint32_t value)
		{
			char buffer[16]{};
			auto result = std::to_chars(std::begin(buffer), std::end(buffer), value, 16);
			if(result.ec != std::errc{})
				return {};
			return std::string(buffer, result.ptr);
		}

		std::string Utf8(std::wstring_view value)
		{
			std::string result;
			if(!AppendUtf8(result, value))
				return {};
			return result;
		}

		std::string EntryId(std::wstring_view parentPath, size_t index,
			uint32_t identity, bool system)
		{
			std::string result = system ? "system:" : "custom:";
			result += Utf8(parentPath);
			if(!parentPath.empty())
				result.push_back('/');
			result += std::to_string(index);
			result.push_back(':');
			result += Hex(identity);
			return result;
		}

		std::string StableId(uint32_t identity, bool system, const MUID *ui)
		{
			if(ui && ui->id != 0)
				return std::string("shell.muid:") + Hex(ui->id);
			if(system && identity != 0)
				return std::string("shell.title:") + Hex(identity);
			return {};
		}

		const char *Kind(bool separator, bool popup)
		{
			if(separator)
				return "separator";
			return popup ? "menu" : "item";
		}

		void AppendSource(JsonBuilder &builder, bool &first, const NativeMenu *source)
		{
			if(!source)
				return;
			if(!source->source_file.empty())
				builder.memberString(first, "sourceFile", CopyWide(source->source_file));
			if(!source->source_node_id.empty())
				builder.memberString(first, "sourceNodeId", CopyWide(source->source_node_id));
			if(!source->source_hash.empty())
				builder.memberString(first, "sourceHash", source->source_hash);
		}

		void AppendTrace(JsonBuilder &builder, bool &first,
			const StudioCaptureTrace &trace)
		{
			BeginArray(builder, first, "trace");
			for(size_t index = 0; index < trace.size() && index < kMaxTraceEntries; ++index)
			{
				if(index != 0)
					builder.raw(",");
				const auto length = std::min(trace[index].size(), kMaxTraceChars);
				builder.quoted(std::wstring_view(trace[index].data(), length));
			}
			EndArray(builder);
		}

		void AppendRawEntry(JsonBuilder &builder, bool &first,
			const menuitem_t *item, std::wstring_view inheritedParent,
			size_t index, size_t depth, size_t &entryCount)
		{
			if(!item || depth > kMaxDepth || entryCount++ >= kMaxEntries)
			{
				builder.raw("null");
				return;
			}

			BeginObject(builder, first);
			bool memberFirst = true;
			const bool separator = item->is_separator();
			const bool popup = item->is_menu();
			const auto parent = item->path.empty()
				? std::wstring(inheritedParent)
				: CopyWide(item->path);
			const auto identity = item->uid();
			const auto id = EntryId(parent, index, identity, true);
			const auto stableId = StableId(identity, true, item->ui);
			const auto title = separator ? std::wstring{} : CopyWide(item->title);
			const auto matchTitle = separator ? std::wstring{} : CopyWide(item->name);

			builder.memberString(memberFirst, "id", id);
			builder.memberUInt(memberFirst, "index", index);
			builder.memberString(memberFirst, "title", title);
			builder.memberString(memberFirst, "kind", Kind(separator, popup));
			builder.memberString(memberFirst, "origin", "system");
			if(!stableId.empty())
				builder.memberString(memberFirst, "stableId", stableId);
			if(!matchTitle.empty())
				builder.memberString(memberFirst, "matchTitle", matchTitle);
			if(!parent.empty())
				builder.memberString(memberFirst, "parentPath", parent);
			builder.memberBool(memberFirst, "disabled", item->disabled);
			builder.memberBool(memberFirst, "checked", item->checked != 0);
			builder.memberBool(memberFirst, "childrenCaptured", !popup || !item->items.empty());
			AppendTrace(builder, memberFirst, item->trace);
			BeginArray(builder, memberFirst, "children");
			const auto childPath = popup
				? JoinPath(parent, matchTitle.empty() ? title : matchTitle)
				: parent;
			bool childFirst = true;
			for(size_t childIndex = 0; childIndex < item->items.size(); ++childIndex)
			{
				AppendRawEntry(builder, childFirst, item->items[childIndex],
					childPath, childIndex, depth + 1, entryCount);
			}
			EndArray(builder);
			EndObject(builder);
		}

		void AppendFinalEntry(JsonBuilder &builder, bool &first,
			const MenuItemInfo *item, std::wstring_view inheritedParent,
			size_t index, size_t depth, size_t &entryCount)
		{
			if(!item || depth > kMaxDepth || entryCount++ >= kMaxEntries)
			{
				builder.raw("null");
				return;
			}

			BeginObject(builder, first);
			bool memberFirst = true;
			const bool separator = item->is_separator();
			const bool popup = item->is_popup();
			const bool system = item->is_system && !item->dynamic;
			const auto parent = item->path.empty()
				? std::wstring(inheritedParent)
				: CopyWide(item->path);
			const auto identity = item->hash != 0 ? item->hash : item->id;
			const auto id = EntryId(parent, index, identity, system);
			const auto stableId = StableId(identity, system, item->ui);
			const auto title = separator ? std::wstring{} : CopyWide(item->title.text);
			const auto matchTitle = separator ? std::wstring{} : CopyWide(item->title.normalize);

			builder.memberString(memberFirst, "id", id);
			builder.memberUInt(memberFirst, "index", index);
			builder.memberString(memberFirst, "title", title);
			builder.memberString(memberFirst, "kind", Kind(separator, popup));
			builder.memberString(memberFirst, "origin", system ? "system" : "custom");
			// Dynamic entries retain the parsed NativeMenu that produced them.
			// Emit its source identity while that object is still owned by the
			// runtime cache; transient HMENU/command ids never cross the pipe.
			AppendSource(builder, memberFirst, item->owner_dynamic);
			if(!stableId.empty())
				builder.memberString(memberFirst, "stableId", stableId);
			if(!matchTitle.empty())
				builder.memberString(memberFirst, "matchTitle", matchTitle);
			if(!parent.empty())
				builder.memberString(memberFirst, "parentPath", parent);
			builder.memberBool(memberFirst, "disabled", item->is_disabled());
			builder.memberBool(memberFirst, "checked", item->is_checked());
			builder.memberBool(memberFirst, "childrenCaptured", !popup || !item->items.empty());
			AppendTrace(builder, memberFirst, item->trace);
			BeginArray(builder, memberFirst, "children");
			bool childFirst = true;
			const auto childPath = popup
				? JoinPath(parent, matchTitle.empty() ? title : matchTitle)
				: parent;
			for(size_t childIndex = 0; childIndex < item->items.size(); ++childIndex)
				AppendFinalEntry(builder, childFirst, item->items[childIndex], childPath,
					childIndex, depth + 1, entryCount);
			EndArray(builder);
			EndObject(builder);
		}

		void AppendPaths(JsonBuilder &builder, bool &first,
			const std::vector<std::wstring> &paths)
		{
			BeginArray(builder, first, "paths");
			bool pathFirst = true;
			for(size_t index = 0; index < paths.size() && index < 128; ++index)
			{
				if(!pathFirst)
					builder.raw(",");
				pathFirst = false;
				builder.quoted(paths[index]);
			}
			EndArray(builder);
		}

		void AppendCommonSnapshot(JsonBuilder &builder, bool &first,
			std::string_view phase, const StudioCaptureMetadata &metadata,
			std::wstring_view parentPath)
		{
			builder.memberUInt(first, "version", StudioCapture::ProtocolVersion);
			// The request id is filled by SnapshotMessage after serialization.
			builder.memberString(first, "captureId", "");
			builder.memberString(first, "phase", phase);
			builder.memberString(first, "configPath", metadata.configPath);
			builder.memberString(first, "context", metadata.context);
			builder.memberString(first, "contextCategory", metadata.contextCategory);
			builder.memberString(first, "runtimeGeneration", metadata.runtimeGeneration);
			builder.memberString(first, "parentPath", parentPath);
			AppendPaths(builder, first, metadata.paths);
		}

		std::string SerializeOriginalTree(const menuitem_t *root,
			const StudioCaptureMetadata &metadata)
		{
			JsonBuilder builder(StudioCapture::MaxMessageBytes - 1024);
			builder.raw("{");
			bool first = true;
			AppendCommonSnapshot(builder, first, "original", metadata, {});

			BeginArray(builder, first, "original");
			bool itemFirst = true;
			size_t entryCount = 0;
			if(root)
			{
				for(size_t index = 0; index < root->items.size(); ++index)
					AppendRawEntry(builder, itemFirst, root->items[index], {}, index, 0,
						entryCount);
			}
			EndArray(builder);

			BeginArray(builder, first, "entries");
			EndArray(builder);
			BeginArray(builder, first, "diagnostics");
			EndArray(builder);
			builder.raw("}");
			return builder.valid() ? std::move(builder).take() : std::string{};
		}

		std::string SerializeFinalEntries(const std::vector<MenuItemInfo *> &entries,
			const StudioCaptureMetadata &metadata, std::wstring_view parentPath)
		{
			JsonBuilder builder(StudioCapture::MaxMessageBytes - 1024);
			builder.raw("{");
			bool first = true;
			AppendCommonSnapshot(builder, first, "final", metadata, parentPath);

			BeginArray(builder, first, "original");
			EndArray(builder);
			BeginArray(builder, first, "entries");
			bool itemFirst = true;
			size_t entryCount = 0;
			for(size_t index = 0; index < entries.size(); ++index)
				AppendFinalEntry(builder, itemFirst, entries[index], parentPath, index, 0,
					entryCount);
			EndArray(builder);

			BeginArray(builder, first, "diagnostics");
			EndArray(builder);
			builder.raw("}");
			return builder.valid() ? std::move(builder).take() : std::string{};
		}

		bool DecodeJsonString(std::string_view json, size_t &position, std::string &value)
		{
			if(position >= json.size() || json[position] != '"')
				return false;
			++position;
			value.clear();
			while(position < json.size())
			{
				const unsigned char character = static_cast<unsigned char>(json[position++]);
				if(character == '"')
					return true;
				if(character < 0x20)
					return false;
				if(character != '\\')
				{
					value.push_back(static_cast<char>(character));
					continue;
				}
				if(position >= json.size())
					return false;
				const char escape = json[position++];
				switch(escape)
				{
					case '"': value.push_back('"'); break;
					case '\\': value.push_back('\\'); break;
					case '/': value.push_back('/'); break;
					case 'b': value.push_back('\b'); break;
					case 'f': value.push_back('\f'); break;
					case 'n': value.push_back('\n'); break;
					case 'r': value.push_back('\r'); break;
					case 't': value.push_back('\t'); break;
					case 'u':
					{
						if(position + 4 > json.size())
							return false;
						uint32_t codepoint = 0;
						for(size_t index = 0; index < 4; ++index)
						{
							const char digit = json[position++];
							codepoint <<= 4;
							if(digit >= '0' && digit <= '9') codepoint |= digit - '0';
							else if(digit >= 'a' && digit <= 'f') codepoint |= digit - 'a' + 10;
							else if(digit >= 'A' && digit <= 'F') codepoint |= digit - 'A' + 10;
							else return false;
						}
						// Request identifiers and field names are ASCII.  Preserve a
						// non-ASCII escape as a valid UTF-8 scalar for completeness.
						if(codepoint <= 0x7f)
							value.push_back(static_cast<char>(codepoint));
						else if(codepoint <= 0x7ff)
						{
							value.push_back(static_cast<char>(0xc0 | (codepoint >> 6)));
							value.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
						}
						else
						{
							value.push_back(static_cast<char>(0xe0 | (codepoint >> 12)));
							value.push_back(static_cast<char>(0x80 | ((codepoint >> 6) & 0x3f)));
							value.push_back(static_cast<char>(0x80 | (codepoint & 0x3f)));
						}
						break;
					}
					default:
						return false;
				}
				if(value.size() > kMaxCaptureId * 8)
					return false;
			}
			return false;
		}

		bool FindJsonValue(std::string_view json, std::string_view key, size_t &valuePosition)
		{
			size_t position = 0;
			while(position < json.size())
			{
				while(position < json.size() && json[position] != '"')
					++position;
				if(position >= json.size())
					return false;

				size_t keyPosition = position;
				std::string foundKey;
				if(!DecodeJsonString(json, keyPosition, foundKey))
					return false;
				while(keyPosition < json.size() && IsSpace(json[keyPosition]))
					++keyPosition;
				if(keyPosition >= json.size() || json[keyPosition] != ':')
					return false;
				++keyPosition;
				while(keyPosition < json.size() && IsSpace(json[keyPosition]))
					++keyPosition;

				if(foundKey == key)
				{
					valuePosition = keyPosition;
					return true;
				}

				// Move past the value enough to avoid treating a quoted value's
				// contents as another property name.
				if(keyPosition < json.size() && json[keyPosition] == '"')
				{
					std::string ignored;
					if(!DecodeJsonString(json, keyPosition, ignored))
						return false;
				}
				position = keyPosition;
			}
			return false;
		}

		bool FindJsonString(std::string_view json, std::string_view key, std::string &value)
		{
			size_t position = 0;
			return FindJsonValue(json, key, position) && DecodeJsonString(json, position, value);
		}

		bool FindJsonUInt(std::string_view json, std::string_view key, uint64_t &value)
		{
			size_t position = 0;
			if(!FindJsonValue(json, key, position))
				return false;

			if(position < json.size() && json[position] == '"')
			{
				std::string text;
				if(!DecodeJsonString(json, position, text) || text.empty())
					return false;
				auto parsed = std::from_chars(text.data(), text.data() + text.size(), value, 10);
				return parsed.ec == std::errc{} && parsed.ptr == text.data() + text.size();
			}

			const auto begin = json.data() + position;
			const auto end = json.data() + json.size();
			auto parsed = std::from_chars(begin, end, value, 10);
			return parsed.ec == std::errc{} && parsed.ptr != begin;
		}

		bool FindJsonBool(std::string_view json, std::string_view key, bool &value)
		{
			size_t position = 0;
			if(!FindJsonValue(json, key, position))
				return false;
			if(json.substr(position, 4) == "true")
			{
				value = true;
				return true;
			}
			if(json.substr(position, 5) == "false")
			{
				value = false;
				return true;
			}
			return false;
		}

		bool SafeCaptureId(std::string_view value)
		{
			if(value.empty() || value.size() > kMaxCaptureId)
				return false;
			for(unsigned char character : value)
			{
				if((character >= 'a' && character <= 'z') ||
					(character >= 'A' && character <= 'Z') ||
					(character >= '0' && character <= '9') ||
					character == '-' || character == '_' || character == '.' ||
					character == ':')
					continue;
				return false;
			}
			return true;
		}

		std::wstring TokenUserSidString(HANDLE token)
		{
			DWORD length = 0;
			::GetTokenInformation(token, TokenUser, nullptr, 0, &length);
			if(length == 0)
				return {};
			std::vector<BYTE> buffer(length);
			if(!::GetTokenInformation(token, TokenUser, buffer.data(), length, &length))
				return {};

			LPWSTR sidText = nullptr;
			if(!::ConvertSidToStringSidW(reinterpret_cast<PSID>(
				reinterpret_cast<TOKEN_USER *>(buffer.data())->User.Sid), &sidText))
				return {};
			std::wstring result(sidText);
			::LocalFree(sidText);
			return result;
		}

		std::wstring CurrentUserSidString()
		{
			HANDLE token = nullptr;
			if(!::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &token))
				return {};
			std::wstring result = TokenUserSidString(token);
			::CloseHandle(token);
			return result;
		}

		bool SameUserAsCurrentProcess(HANDLE process)
		{
			HANDLE currentToken = nullptr;
			HANDLE peerToken = nullptr;
			const bool opened = ::OpenProcessToken(::GetCurrentProcess(), TOKEN_QUERY, &currentToken) &&
				::OpenProcessToken(process, TOKEN_QUERY, &peerToken);
			if(!opened)
			{
				if(currentToken) ::CloseHandle(currentToken);
				if(peerToken) ::CloseHandle(peerToken);
				return false;
			}

			DWORD currentLength = 0;
			DWORD peerLength = 0;
			::GetTokenInformation(currentToken, TokenUser, nullptr, 0, &currentLength);
			::GetTokenInformation(peerToken, TokenUser, nullptr, 0, &peerLength);
			bool same = false;
			if(currentLength != 0 && peerLength != 0)
			{
				std::vector<BYTE> currentBuffer(currentLength);
				std::vector<BYTE> peerBuffer(peerLength);
				if(::GetTokenInformation(currentToken, TokenUser, currentBuffer.data(), currentLength, &currentLength) &&
					::GetTokenInformation(peerToken, TokenUser, peerBuffer.data(), peerLength, &peerLength))
				{
					auto currentSid = reinterpret_cast<TOKEN_USER *>(currentBuffer.data())->User.Sid;
					auto peerSid = reinterpret_cast<TOKEN_USER *>(peerBuffer.data())->User.Sid;
					same = ::EqualSid(currentSid, peerSid) == TRUE;
				}
			}
			::CloseHandle(currentToken);
			::CloseHandle(peerToken);
			return same;
		}
	}

	StudioCapture::~StudioCapture()
	{
		Stop();
	}

	bool StudioCapture::Start(HWND notificationWindow)
	{
		std::lock_guard<std::mutex> lock(mutex_);
		if(worker_.joinable())
			return true;

		uint32_t sessionId = 0;
		pipeName_ = BuildPipeName(sessionId);
		if(pipeName_.empty())
			return false;

		stopEvent_ = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
		if(!stopEvent_)
		{
			pipeName_.clear();
			return false;
		}

		sessionId_ = sessionId;
		notificationWindow_ = notificationWindow;
		stopping_ = false;
		connected_ = false;
		active_ = false;
		failed_ = false;
		includeOriginal_ = true;
		captureId_.clear();
		outbound_.clear();
		try
		{
			worker_ = std::thread([this]() noexcept { Run(); });
		}
		catch(...)
		{
			::CloseHandle(stopEvent_);
			stopEvent_ = nullptr;
			pipeName_.clear();
			notificationWindow_ = nullptr;
			return false;
		}
		return true;
	}

	void StudioCapture::Stop() noexcept
	{
		HANDLE stopEvent = nullptr;
		HANDLE pipe = INVALID_HANDLE_VALUE;
		{
			std::lock_guard<std::mutex> lock(mutex_);
			stopping_ = true;
			stopEvent = stopEvent_;
			pipe = pipe_;
		}

		if(stopEvent)
			::SetEvent(stopEvent);
		if(pipe != INVALID_HANDLE_VALUE && pipe != nullptr)
			::CancelIoEx(pipe, nullptr);
		changed_.notify_all();

		if(worker_.joinable())
		{
			try
			{
				worker_.join();
			}
			catch(...)
			{
				// std::thread::join only fails for a programming error.  Never
				// allow cleanup from a context-menu destructor to throw.
			}
		}

		std::lock_guard<std::mutex> lock(mutex_);
		pipe_ = INVALID_HANDLE_VALUE;
		connected_ = false;
		active_ = false;
		failed_ = false;
		captureId_.clear();
		outbound_.clear();
		if(stopEvent_)
			::CloseHandle(stopEvent_);
		stopEvent_ = nullptr;
		pipeName_.clear();
		notificationWindow_ = nullptr;
	}

	bool StudioCapture::IsActive() const
	{
		std::lock_guard<std::mutex> lock(mutex_);
		return connected_ && active_ && !stopping_;
	}

	bool StudioCapture::WantsOriginal() const
	{
		std::lock_guard<std::mutex> lock(mutex_);
		return connected_ && active_ && includeOriginal_ && !stopping_;
	}

	bool StudioCapture::IsStopping() const
	{
		std::lock_guard<std::mutex> lock(mutex_);
		return stopping_;
	}

	void StudioCapture::SetPipe(HANDLE pipe)
	{
		std::lock_guard<std::mutex> lock(mutex_);
		pipe_ = pipe;
	}

	void StudioCapture::ClearConnection()
	{
		std::lock_guard<std::mutex> lock(mutex_);
		connected_ = false;
		active_ = false;
		failed_ = false;
		includeOriginal_ = true;
		captureId_.clear();
		outbound_.clear();
	}

	void StudioCapture::Fail(std::string_view code, std::string_view message) noexcept
	{
		try
		{
			std::lock_guard<std::mutex> lock(mutex_);
			if(!connected_ || !active_ || failed_)
				return;
			FailLocked(code, message);
		}
		catch(...)
		{
			// Capture diagnostics must never escape the Explorer thread.  The
			// connection remains inactive if error-message construction failed.
		}
		changed_.notify_one();
	}

	void StudioCapture::FailLocked(std::string_view code, std::string_view message) noexcept
	{
		if(!connected_ || !active_ || failed_)
			return;

		failed_ = true;
		active_ = false;
		// A failure supersedes queued snapshots.  Keeping stale snapshots ahead
		// of the diagnostic would make the managed client report an incomplete
		// capture instead of the actual cause.
		outbound_.clear();
		try
		{
			auto error = ErrorMessage(code, message);
			if(!error.empty() && error.size() <= MaxMessageBytes)
				outbound_.push_back(Outbound{std::move(error)});
		}
		catch(...)
		{
			// There is no safe allocation left for a diagnostic.  The failed_ gate
			// still prevents further snapshots and lets the worker close cleanly.
			outbound_.clear();
		}
		changed_.notify_one();
	}

	bool StudioCapture::EnqueueLocked(std::string payload) noexcept
	{
		if(!connected_ || !active_)
			return false;
		if(payload.empty())
		{
			FailLocked("CAPTURE_MESSAGE_EMPTY", "The native capture produced an empty message.");
			return false;
		}
		if(payload.size() > MaxMessageBytes)
		{
			FailLocked("CAPTURE_MESSAGE_TOO_LARGE",
				"The native capture message exceeded the protocol size limit.");
			return false;
		}
		if(outbound_.size() >= kMaxQueueMessages)
		{
			FailLocked("CAPTURE_QUEUE_OVERFLOW",
				"The native capture queue filled before Studio could receive the snapshot.");
			return false;
		}
		try
		{
			outbound_.push_back(Outbound{std::move(payload)});
		}
		catch(...)
		{
			FailLocked("CAPTURE_QUEUE_MEMORY",
				"The native capture could not queue the snapshot in memory.");
			return false;
		}
		return true;
	}

	std::wstring StudioCapture::BuildPipeName(uint32_t &sessionId)
	{
		DWORD processSession = 0;
		if(::ProcessIdToSessionId(::GetCurrentProcessId(), &processSession))
			sessionId = processSession;
		else
			sessionId = ::WTSGetActiveConsoleSessionId();
		const auto sid = CurrentUserSidString();
		if(sid.empty())
			return {};
		return std::wstring(L"\\\\.\\pipe\\QweShell.Studio.Capture.") + sid +
			L"." + std::to_wstring(sessionId);
	}

	HANDLE StudioCapture::ConnectToServer(const std::wstring &name, HANDLE stopEvent)
	{
		for(;;)
		{
			if(::WaitForSingleObject(stopEvent, 0) == WAIT_OBJECT_0)
				return INVALID_HANDLE_VALUE;

			HANDLE pipe = ::CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE,
				0, nullptr, OPEN_EXISTING,
				FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
				nullptr);
			if(pipe != INVALID_HANDLE_VALUE)
			{
				DWORD mode = PIPE_READMODE_BYTE;
				if(::SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr) && ValidatePeerUser(pipe))
					return pipe;
				::CloseHandle(pipe);
			}

			if(::WaitForSingleObject(stopEvent, 250) == WAIT_OBJECT_0)
				return INVALID_HANDLE_VALUE;
		}
	}

	bool StudioCapture::ValidatePeerUser(HANDLE pipe)
	{
		ULONG processId = 0;
		if(!::GetNamedPipeServerProcessId(pipe, &processId) || processId == 0)
			return false;
		HANDLE process = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
		if(!process)
			return false;
		const bool result = SameUserAsCurrentProcess(process);
		::CloseHandle(process);
		return result;
	}

	bool StudioCapture::ReadExact(HANDLE pipe, HANDLE stopEvent, void *buffer, size_t size)
	{
		auto *destination = static_cast<BYTE *>(buffer);
		size_t offset = 0;
		while(offset < size)
		{
			HANDLE event = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
			if(!event)
				return false;
			OVERLAPPED overlapped{};
			overlapped.hEvent = event;
			DWORD transferred = 0;
			const DWORD requested = static_cast<DWORD>(std::min<size_t>(size - offset,
				static_cast<size_t>(std::numeric_limits<DWORD>::max())));
			BOOL result = ::ReadFile(pipe, destination + offset, requested, &transferred,
				&overlapped);
			if(!result && ::GetLastError() == ERROR_IO_PENDING)
			{
				HANDLE events[] = { stopEvent, event };
				const auto wait = ::WaitForMultipleObjects(2, events, FALSE, INFINITE);
				if(wait == WAIT_OBJECT_0)
				{
					::CancelIoEx(pipe, &overlapped);
					::CloseHandle(event);
					return false;
				}
				if(wait != WAIT_OBJECT_0 + 1 || !::GetOverlappedResult(pipe, &overlapped,
					&transferred, FALSE))
				{
					::CloseHandle(event);
					return false;
				}
			}
			else if(!result)
			{
				::CloseHandle(event);
				return false;
			}
			::CloseHandle(event);
			if(transferred == 0)
				return false;
			offset += transferred;
		}
		return true;
	}

	bool StudioCapture::WriteExact(HANDLE pipe, HANDLE stopEvent, const void *buffer, size_t size)
	{
		const auto *source = static_cast<const BYTE *>(buffer);
		size_t offset = 0;
		while(offset < size)
		{
			HANDLE event = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
			if(!event)
				return false;
			OVERLAPPED overlapped{};
			overlapped.hEvent = event;
			DWORD transferred = 0;
			const DWORD requested = static_cast<DWORD>(std::min<size_t>(size - offset,
				static_cast<size_t>(std::numeric_limits<DWORD>::max())));
			BOOL result = ::WriteFile(pipe, source + offset, requested, &transferred,
				&overlapped);
			if(!result && ::GetLastError() == ERROR_IO_PENDING)
			{
				HANDLE events[] = { stopEvent, event };
				const auto wait = ::WaitForMultipleObjects(2, events, FALSE, INFINITE);
				if(wait == WAIT_OBJECT_0)
				{
					::CancelIoEx(pipe, &overlapped);
					::CloseHandle(event);
					return false;
				}
				if(wait != WAIT_OBJECT_0 + 1 || !::GetOverlappedResult(pipe, &overlapped,
					&transferred, FALSE))
				{
					::CloseHandle(event);
					return false;
				}
			}
			else if(!result)
			{
				::CloseHandle(event);
				return false;
			}
			::CloseHandle(event);
			if(transferred == 0)
				return false;
			offset += transferred;
		}
		return true;
	}

	bool StudioCapture::ReadFrame(HANDLE pipe, HANDLE stopEvent, std::string &payload)
	{
		std::array<BYTE, sizeof(uint32_t)> header{};
		if(!ReadExact(pipe, stopEvent, header.data(), header.size()))
			return false;
		const uint32_t length = static_cast<uint32_t>(header[0]) |
			(static_cast<uint32_t>(header[1]) << 8) |
			(static_cast<uint32_t>(header[2]) << 16) |
			(static_cast<uint32_t>(header[3]) << 24);
		if(length == 0 || length > MaxMessageBytes)
			return false;
		try
		{
			payload.assign(length, '\0');
		}
		catch(...)
		{
			return false;
		}
		return ReadExact(pipe, stopEvent, payload.data(), payload.size());
	}

	bool StudioCapture::WriteFrame(HANDLE pipe, HANDLE stopEvent, std::string_view payload)
	{
		if(payload.empty() || payload.size() > MaxMessageBytes ||
			payload.size() > std::numeric_limits<uint32_t>::max())
			return false;
		const uint32_t length = static_cast<uint32_t>(payload.size());
		std::array<BYTE, sizeof(uint32_t)> header{
			static_cast<BYTE>(length & 0xff),
			static_cast<BYTE>((length >> 8) & 0xff),
			static_cast<BYTE>((length >> 16) & 0xff),
			static_cast<BYTE>((length >> 24) & 0xff)};
		return WriteExact(pipe, stopEvent, header.data(), header.size()) &&
			WriteExact(pipe, stopEvent, payload.data(), payload.size());
	}

	bool StudioCapture::ParseRequest(std::string_view payload, uint32_t expectedSession,
		Request &request)
	{
		uint64_t version = 0;
		uint64_t session = 0;
		std::string type;
		std::string captureId;
		if(!FindJsonUInt(payload, "version", version) || version != ProtocolVersion ||
			!FindJsonString(payload, "type", type) ||
			!FindJsonString(payload, "captureId", captureId) ||
			!FindJsonUInt(payload, "sessionId", session) ||
			session > std::numeric_limits<uint32_t>::max() ||
			static_cast<uint32_t>(session) != expectedSession ||
			!SafeCaptureId(captureId))
			return false;

		request.captureId = std::move(captureId);
		request.sessionId = static_cast<uint32_t>(session);
		if(type == "capture.start")
		{
			request.kind = Request::Kind::Start;
			request.includeOriginal = true;
			bool includeOriginal = true;
			size_t includePosition = 0;
			if(FindJsonValue(payload, "includeOriginal", includePosition))
			{
				if(!FindJsonBool(payload, "includeOriginal", includeOriginal))
					return false;
			}
			request.includeOriginal = includeOriginal;
			return true;
		}
		if(type == "capture.cancel")
		{
			request.kind = Request::Kind::Cancel;
			return true;
		}
		if(type == "capture.end")
		{
			request.kind = Request::Kind::End;
			return true;
		}
		return false;
	}

	std::string StudioCapture::ErrorMessage(std::string_view code, std::string_view message)
	{
		JsonBuilder builder;
		builder.raw("{");
		bool first = true;
		builder.memberUInt(first, "version", ProtocolVersion);
		builder.memberString(first, "type", "capture.error");
		builder.memberString(first, "code", code);
		builder.memberString(first, "message", message);
		builder.raw("}");
		return builder.valid() ? std::move(builder).take() : std::string{};
	}

	std::string StudioCapture::ReadyMessage(std::string_view captureId, uint32_t sessionId)
	{
		JsonBuilder builder;
		builder.raw("{");
		bool first = true;
		builder.memberUInt(first, "version", ProtocolVersion);
		builder.memberString(first, "type", "capture.ready");
		builder.memberString(first, "captureId", captureId);
		builder.memberUInt(first, "sessionId", sessionId);
		builder.raw("}");
		return builder.valid() ? std::move(builder).take() : std::string{};
	}

	std::string StudioCapture::EndMessage(std::string_view captureId, std::string_view reason)
	{
		JsonBuilder builder;
		builder.raw("{");
		bool first = true;
		builder.memberUInt(first, "version", ProtocolVersion);
		builder.memberString(first, "type", "capture.end");
		builder.memberString(first, "captureId", captureId);
		builder.memberString(first, "reason", reason);
		builder.raw("}");
		return builder.valid() ? std::move(builder).take() : std::string{};
	}

	std::string StudioCapture::SnapshotMessage(std::string_view captureId,
		std::string_view phase, std::string snapshot)
	{
		try
		{
			if(snapshot.empty() || captureId.empty())
				return {};
			const std::string marker = "\"captureId\":\"\"";
			JsonBuilder snapshotBuilder;
			// Replace the deliberately empty body id with the request id.  The id
			// is validated to a restricted ASCII alphabet before this point.
			const auto markerPosition = snapshot.find(marker);
			if(markerPosition == std::string::npos)
				return {};
			JsonBuilder idBuilder;
			idBuilder.quoted(captureId);
			if(!idBuilder.valid())
				return {};
			const auto id = std::move(idBuilder).take();
			snapshot.replace(markerPosition + std::string("\"captureId\":").size(),
				std::string("\"\"").size(), id);

			snapshotBuilder.raw("{");
			bool first = true;
			snapshotBuilder.memberUInt(first, "version", ProtocolVersion);
			snapshotBuilder.memberString(first, "type", "menu.snapshot");
			snapshotBuilder.memberString(first, "captureId", captureId);
			snapshotBuilder.memberString(first, "phase", phase);
			snapshotBuilder.memberKey("snapshot", first);
			snapshotBuilder.raw(snapshot);
			snapshotBuilder.raw("}");
			const auto candidate = std::move(snapshotBuilder).take();
			if(candidate.empty() || candidate.size() > MaxMessageBytes)
				return {};
			// Rebuild the small envelope to return it without retaining an extra
			// copy of the bounded candidate.
			JsonBuilder result;
			result.raw("{");
			first = true;
			result.memberUInt(first, "version", ProtocolVersion);
			result.memberString(first, "type", "menu.snapshot");
			result.memberString(first, "captureId", captureId);
			result.memberString(first, "phase", phase);
			result.memberKey("snapshot", first);
			result.raw(snapshot);
			result.raw("}");
			return result.valid() ? std::move(result).take() : std::string{};
		}
		catch(...)
		{
			return {};
		}
	}

	std::string StudioCapture::SerializeOriginal(const menuitem_t *root,
		const StudioCaptureMetadata &metadata)
	{
		try
		{
			return SerializeOriginalTree(root, metadata);
		}
		catch(...)
		{
			return {};
		}
	}

	std::string StudioCapture::SerializeFinal(const std::vector<MenuItemInfo *> &entries,
		const StudioCaptureMetadata &metadata, std::wstring_view parentPath)
	{
		try
		{
			return SerializeFinalEntries(entries, metadata, parentPath);
		}
		catch(...)
		{
			return {};
		}
	}

	bool StudioCapture::PublishOriginal(const menuitem_t *root,
		const StudioCaptureMetadata &metadata)
	{
		try
		{
			std::string captureId;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || !includeOriginal_)
					return false;
				captureId = captureId_;
			}
			const auto snapshot = SerializeOriginal(root, metadata);
			if(snapshot.empty())
			{
				Fail("CAPTURE_SERIALIZATION", "The native capture could not serialize the original menu.");
				return false;
			}
			const auto message = SnapshotMessage(captureId, "original", snapshot);
			if(message.empty())
			{
				Fail("CAPTURE_SERIALIZATION", "The native capture snapshot exceeded the protocol limit.");
				return false;
			}
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || captureId_ != captureId)
					return false;
				if(!EnqueueLocked(message))
					return false;
			}
			changed_.notify_one();
			return true;
		}
		catch(...)
		{
			Fail("CAPTURE_SERIALIZATION", "The native capture could not publish the original menu.");
			return false;
		}
	}

	bool StudioCapture::PublishFinal(const std::vector<MenuItemInfo *> &entries,
		const StudioCaptureMetadata &metadata, std::wstring_view parentPath)
	{
		try
		{
			std::string captureId;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_)
					return false;
				captureId = captureId_;
			}
			const auto snapshot = SerializeFinal(entries, metadata, parentPath);
			if(snapshot.empty())
			{
				Fail("CAPTURE_SERIALIZATION", "The native capture could not serialize the displayed menu.");
				return false;
			}
			const auto message = SnapshotMessage(captureId, "final", snapshot);
			if(message.empty())
			{
				Fail("CAPTURE_SERIALIZATION", "The native capture snapshot exceeded the protocol limit.");
				return false;
			}
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || captureId_ != captureId)
					return false;
				if(!EnqueueLocked(message))
					return false;
			}
			changed_.notify_one();
			return true;
		}
		catch(...)
		{
			Fail("CAPTURE_SERIALIZATION", "The native capture could not publish the displayed menu.");
			return false;
		}
	}

	void StudioCapture::HandleClient(HANDLE pipe) noexcept
	{
		try
		{
			std::string requestPayload;
			if(!ReadFrame(pipe, stopEvent_, requestPayload))
				return;

			Request request;
			if(!ParseRequest(requestPayload, sessionId_, request) ||
				request.kind != Request::Kind::Start)
			{
				WriteFrame(pipe, stopEvent_, ErrorMessage("INVALID_REQUEST",
					"Expected a valid capture.start request."));
				return;
			}

			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(stopping_)
					return;
				connected_ = true;
				active_ = true;
				failed_ = false;
				includeOriginal_ = request.includeOriginal;
				captureId_ = request.captureId;
				outbound_.clear();
				EnqueueLocked(ReadyMessage(captureId_, sessionId_));
			}
			// Wake the owning menu thread immediately.  This closes the race where
			// the context was initialized before Studio's first request reached the
			// capture worker: the retry runs on the UI thread while the HMENU is still
			// owned by this context, and only then serializes the immutable snapshot.
			HWND notificationWindow = nullptr;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				notificationWindow = notificationWindow_;
			}
			if(notificationWindow)
				::PostMessageW(notificationWindow, CaptureArmedMessage,
					reinterpret_cast<WPARAM>(this), 0);
			changed_.notify_one();

			for(;;)
			{
				if(IsStopping())
					return;

				Outbound outgoing;
				bool hasOutgoing = false;
				bool failedAfterWrite = false;
				{
					std::unique_lock<std::mutex> lock(mutex_);
					changed_.wait_for(lock, std::chrono::milliseconds(100), [this]()
					{
						return stopping_ || failed_ || !outbound_.empty();
					});
					if(stopping_)
						return;
					if(!outbound_.empty())
					{
						outgoing = std::move(outbound_.front());
						outbound_.pop_front();
						hasOutgoing = true;
						failedAfterWrite = failed_ && outbound_.empty();
					}
					else if(failed_)
						return;
				}

				if(hasOutgoing && !WriteFrame(pipe, stopEvent_, outgoing.payload))
					return;
				if(failedAfterWrite)
					return;

				DWORD available = 0;
				if(!::PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
					return;
				if(available == 0)
					continue;

				std::string controlPayload;
				if(!ReadFrame(pipe, stopEvent_, controlPayload))
					return;
				std::string activeCaptureId;
				{
					std::lock_guard<std::mutex> lock(mutex_);
					activeCaptureId = captureId_;
				}
				Request control;
				if(!ParseRequest(controlPayload, sessionId_, control) ||
					control.captureId != activeCaptureId)
				{
					if(!WriteFrame(pipe, stopEvent_, ErrorMessage("INVALID_REQUEST",
						"The capture request does not match the active capture.")))
						return;
					continue;
				}
				if(control.kind == Request::Kind::Cancel || control.kind == Request::Kind::End)
				{
					const auto reason = control.kind == Request::Kind::Cancel
						? "cancelled" : "client-ended";
					const auto end = EndMessage(control.captureId, reason);
					{
						std::lock_guard<std::mutex> lock(mutex_);
						active_ = false;
						outbound_.clear();
					}
					WriteFrame(pipe, stopEvent_, end);
					return;
				}
			}
		}
		catch(...)
		{
			// A malformed client or an allocation failure ends this connection.
		}
	}

	void StudioCapture::Run() noexcept
	{
		try
		{
			for(;;)
			{
				if(IsStopping())
					break;
				HANDLE pipe = ConnectToServer(pipeName_, stopEvent_);
				if(pipe == INVALID_HANDLE_VALUE)
					break;
				if(IsStopping())
				{
					::CloseHandle(pipe);
					break;
				}

				SetPipe(pipe);
				HandleClient(pipe);
				SetPipe(INVALID_HANDLE_VALUE);
				::CloseHandle(pipe);
				ClearConnection();
			}
		}
		catch(...)
		{
			// Keep exceptions from escaping into the thread runtime.  Stop() is
			// still able to join and release the worker deterministically.
		}
		SetPipe(INVALID_HANDLE_VALUE);
		ClearConnection();
	}
}
