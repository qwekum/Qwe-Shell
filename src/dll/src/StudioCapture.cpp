#include <pch.h>

#include "Include/ContextMenu.h"
#include "Include/StudioCapture.h"
#include "../../shared/FileSystemObjects.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <limits>
#include <memory>
#include <unordered_set>
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
		// Notifications can outlive a ContextMenu allocation. Use process-wide
		// tokens so address reuse cannot make an old notification current again.
		std::atomic<uint64_t> captureEpochCounter{0};
		uint64_t NextCaptureEpoch() noexcept
		{
			auto previous = captureEpochCounter.load(std::memory_order_relaxed);
			while(previous < static_cast<uint64_t>(std::numeric_limits<intptr_t>::max()))
			{
				if(captureEpochCounter.compare_exchange_weak(previous, previous + 1,
					std::memory_order_relaxed))
					return previous + 1;
			}
			return 0;
		}
		constexpr size_t kMaxQueueMessages = 8;
		constexpr size_t kMaxEntries = 4096;
		constexpr size_t kMaxDepth = 64;
		constexpr size_t kMaxStringChars = 65536;
		constexpr size_t kMaxCaptureId = 128;
		constexpr size_t kMaxTraceEntries = 64;
		constexpr size_t kMaxTraceChars = 1024;
		// Captured menu images are copied from already-owned HBITMAPs.  Keep each
		// copy small enough for the preview worker and cap the aggregate raw pixel
		// budget so a large menu cannot consume the entire IPC frame with icons.
		constexpr uint32_t kMaxCapturedImageWidth = 512;
		constexpr uint32_t kMaxCapturedImageHeight = 512;
		constexpr uint64_t kMaxCapturedImagePixels =
			static_cast<uint64_t>(kMaxCapturedImageWidth) * kMaxCapturedImageHeight;
		constexpr size_t kMaxCapturedImageBytes =
			static_cast<size_t>(kMaxCapturedImagePixels) * 4U;
		constexpr size_t kMaxCapturedImageBytesTotal =
			StudioCapture::MaxMessageBytes / 4U;
		constexpr uint32_t kMaxAppearanceWidth = 2048;
		constexpr uint32_t kMaxAppearanceHeight = 4096;
		constexpr uint64_t kMaxAppearancePixels = 600000;
		constexpr size_t kMaxAppearanceRows = 4096;
		constexpr uint32_t kMinAppearanceDpi = 48;
		constexpr uint32_t kMaxAppearanceDpi = 768;

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
			bool canAppend(size_t size) const
			{
				return valid_ && size <= limit_ - std::min(value_.size(), limit_);
			}
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

			void memberInt(bool &first, std::string_view key, int64_t value)
			{
				memberKey(key, first);
				integer(value);
			}

			void integer(int64_t value)
			{
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

		bool TrySourceStart(const Nilesoft::Text::string &value, uint64_t &start)
		{
			if(value.length() < 2 || value.c_str()[0] != L'n')
				return false;

			uint64_t parsed = 0;
			const auto limit = std::numeric_limits<uint64_t>::max();
			for(size_t index = 1; index < value.length(); ++index)
			{
				const wchar_t character = value.c_str()[index];
				if(character < L'0' || character > L'9')
					return false;
				const auto digit = static_cast<uint64_t>(character - L'0');
				if(parsed > (limit - digit) / 10U)
					return false;
				parsed = parsed * 10U + digit;
			}
			start = parsed;
			return true;
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

		bool ValidAppearanceGeometry(const StudioCaptureAppearance &appearance)
		{
			if(!appearance.available || appearance.width == 0 || appearance.height == 0 ||
				appearance.width > kMaxAppearanceWidth ||
				appearance.height > kMaxAppearanceHeight ||
				appearance.dpi < kMinAppearanceDpi || appearance.dpi > kMaxAppearanceDpi ||
				static_cast<uint64_t>(appearance.width) * appearance.height > kMaxAppearancePixels ||
				appearance.pixels.size() != static_cast<size_t>(appearance.width) *
					appearance.height * 4U || appearance.rows.empty() ||
				appearance.rows.size() > kMaxAppearanceRows)
				return false;

			std::unordered_set<std::string> rowIds;
			for(const auto &row : appearance.rows)
			{
				if(row.entryId.empty() || row.width == 0 || row.height == 0 ||
					row.x >= appearance.width || row.y >= appearance.height ||
					row.width > appearance.width - row.x ||
					row.height > appearance.height - row.y ||
					!rowIds.insert(row.entryId).second)
					return false;
			}

			for(size_t index = 0; index < appearance.pixels.size(); index += 4)
			{
				const auto alpha = appearance.pixels[index + 3];
				if(appearance.pixels[index] > alpha ||
					appearance.pixels[index + 1] > alpha ||
					appearance.pixels[index + 2] > alpha)
					return false;
			}
			return true;
		}

		std::string Base64(std::string_view bytes)
		{
			static constexpr char alphabet[] =
				"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
			if(bytes.empty())
				return {};
			if(bytes.size() > (std::numeric_limits<size_t>::max() / 4U) * 3U)
				return {};

			std::string encoded;
			encoded.reserve(((bytes.size() + 2U) / 3U) * 4U);
			for(size_t index = 0; index < bytes.size(); index += 3)
			{
				const auto first = static_cast<unsigned char>(bytes[index]);
				const auto second = index + 1 < bytes.size()
					? static_cast<unsigned char>(bytes[index + 1]) : 0;
				const auto third = index + 2 < bytes.size()
					? static_cast<unsigned char>(bytes[index + 2]) : 0;
				encoded.push_back(alphabet[first >> 2]);
				encoded.push_back(alphabet[((first & 0x03U) << 4) | (second >> 4)]);
				encoded.push_back(index + 1 < bytes.size()
					? alphabet[((second & 0x0FU) << 2) | (third >> 6)] : '=');
				encoded.push_back(index + 2 < bytes.size()
					? alphabet[third & 0x3FU] : '=');
			}
			return encoded;
		}

		struct CapturedMenuImage
		{
			bool present = false;
			bool available = false;
			uint32_t width = 0;
			uint32_t height = 0;
			std::vector<uint8_t> pixels;
			const char *reason = "The menu image could not be copied safely.";
		};

		bool IsSpecialMenuBitmap(HBITMAP bitmap) noexcept
		{
			if(!bitmap)
				return false;
			// Windows reserves the low menu bitmap values (and -1) for symbols and
			// callbacks.  They are not GDI bitmap handles and must never reach the
			// GDI inspection APIs below.
			const auto value = reinterpret_cast<intptr_t>(bitmap);
			return value >= static_cast<intptr_t>(-1) && value <= 11;
		}

		CapturedMenuImage CaptureMenuBitmap(HBITMAP bitmap)
		{
			CapturedMenuImage result;
			result.present = bitmap != nullptr;
			if(!bitmap)
				return result;
			if(IsSpecialMenuBitmap(bitmap))
			{
				result.reason = "The menu image is a reserved system bitmap handle.";
				return result;
			}
			if(::GetObjectType(bitmap) != OBJ_BITMAP)
			{
				result.reason = "The menu image handle is not a GDI bitmap.";
				return result;
			}

			BITMAP bitmapInfo{};
			if(::GetObjectW(bitmap, sizeof(bitmapInfo), &bitmapInfo) != sizeof(bitmapInfo) ||
				bitmapInfo.bmWidth <= 0 || bitmapInfo.bmHeight <= 0 ||
				bitmapInfo.bmWidth > static_cast<LONG>(kMaxCapturedImageWidth) ||
				bitmapInfo.bmHeight > static_cast<LONG>(kMaxCapturedImageHeight) ||
				bitmapInfo.bmPlanes != 1 ||
				(bitmapInfo.bmBitsPixel != 8 && bitmapInfo.bmBitsPixel != 16 &&
					bitmapInfo.bmBitsPixel != 24 && bitmapInfo.bmBitsPixel != 32))
			{
				result.reason = "The menu image has unsupported bitmap dimensions or pixels.";
				return result;
			}

			result.width = static_cast<uint32_t>(bitmapInfo.bmWidth);
			result.height = static_cast<uint32_t>(bitmapInfo.bmHeight);
			const auto byteCount = static_cast<size_t>(result.width) * result.height * 4U;
			if(byteCount == 0 || byteCount > kMaxCapturedImageBytes)
			{
				result.width = result.height = 0;
				result.reason = "The menu image exceeds the bounded pixel limit.";
				return result;
			}

			try
			{
				result.pixels.resize(byteCount);
			}
			catch(...)
			{
				result.width = result.height = 0;
				result.reason = "The menu image could not be allocated.";
				return result;
			}

			const auto dc = ::CreateCompatibleDC(nullptr);
			if(!dc)
			{
				result.pixels.clear();
				result.width = result.height = 0;
				result.reason = "The menu image could not create a readback DC.";
				return result;
			}

			BITMAPINFO dibInfo{};
			dibInfo.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
			dibInfo.bmiHeader.biWidth = bitmapInfo.bmWidth;
			dibInfo.bmiHeader.biHeight = -bitmapInfo.bmHeight;
			dibInfo.bmiHeader.biPlanes = 1;
			dibInfo.bmiHeader.biBitCount = 32;
			dibInfo.bmiHeader.biCompression = BI_RGB;
			const auto copied = ::GetDIBits(dc, bitmap, 0, result.height,
				result.pixels.data(), &dibInfo, DIB_RGB_COLORS);
			::DeleteDC(dc);
			if(copied != static_cast<int>(result.height))
			{
				result.pixels.clear();
				result.width = result.height = 0;
				result.reason = "The menu image pixels could not be read.";
				return result;
			}

			// BI_RGB 32-bit readback commonly leaves alpha at zero for an opaque
			// legacy bitmap.  Treat an all-zero alpha plane as opaque, preserve real
			// alpha, and premultiply straight-alpha channels when necessary.
			bool allAlphaZero = true;
			for(size_t index = 3; index < result.pixels.size(); index += 4)
				if(result.pixels[index] != 0)
				{
					allAlphaZero = false;
					break;
				}
			for(size_t index = 0; index < result.pixels.size(); index += 4)
			{
				auto *pixel = result.pixels.data() + index;
				if(bitmapInfo.bmBitsPixel < 32 || allAlphaZero)
					pixel[3] = 255;
				const auto alpha = pixel[3];
				if(alpha == 0)
				{
					pixel[0] = pixel[1] = pixel[2] = 0;
				}
				else if(pixel[0] > alpha || pixel[1] > alpha || pixel[2] > alpha)
				{
					for(int channel = 0; channel < 3; ++channel)
						pixel[channel] = static_cast<uint8_t>(
							(static_cast<unsigned>(pixel[channel]) * alpha + 127U) / 255U);
				}
			}

			result.available = true;
			result.reason = nullptr;
			return result;
		}

		std::string CapturedImageObject(const CapturedMenuImage &image,
			std::string_view encoded)
		{
			JsonBuilder builder;
			builder.raw("{");
			bool first = true;
			builder.memberString(first, "format", "Pbgra32");
			builder.memberString(first, "status", image.available ? "available" : "unavailable");
			builder.memberUInt(first, "width", image.available ? image.width : 0);
			builder.memberUInt(first, "height", image.available ? image.height : 0);
			builder.memberString(first, "pixels", encoded);
			if(!image.available && image.reason && *image.reason)
				builder.memberString(first, "reason", image.reason);
			builder.raw("}");
			return builder.valid() ? std::move(builder).take() : std::string{};
		}

		void AppendCapturedImage(JsonBuilder &builder, bool &first, HBITMAP bitmap,
			size_t &imageBytes)
		{
			if(!bitmap)
				return;

			// Once the aggregate raw-pixel budget is exhausted, do not inspect
			// another borrowed handle.  A menu can contain thousands of entries and
			// each handle may belong to an extension that is already being torn down;
			// preserving an explicit unavailable value is both safer and bounded.
			if(imageBytes >= kMaxCapturedImageBytesTotal)
			{
				CapturedMenuImage unavailable;
				unavailable.present = true;
				unavailable.reason = "The captured menu images exceeded their aggregate pixel limit.";
				const auto object = CapturedImageObject(unavailable, {});
				const auto required = object.size() + 8U + (first ? 0U : 1U);
				if(!object.empty() && builder.canAppend(required))
				{
					builder.memberKey("image", first);
					builder.raw(object);
				}
				return;
			}

			auto image = CaptureMenuBitmap(bitmap);
			std::string encoded;
			if(image.available)
			{
				if(image.pixels.size() > kMaxCapturedImageBytesTotal -
					std::min(imageBytes, kMaxCapturedImageBytesTotal))
				{
					image.available = false;
					image.width = image.height = 0;
					image.pixels.clear();
					image.reason = "The captured menu images exceeded their aggregate pixel limit.";
				}
				else
				{
					try
					{
						encoded = Base64(std::string_view(
							reinterpret_cast<const char *>(image.pixels.data()), image.pixels.size()));
					}
					catch(...)
					{
						image.available = false;
						image.width = image.height = 0;
						image.pixels.clear();
						image.reason = "The captured menu image could not be encoded.";
					}
				}
			}

			if(image.available && encoded.empty())
			{
				image.available = false;
				image.width = image.height = 0;
				image.pixels.clear();
				image.reason = "The captured menu image could not be encoded.";
			}

			if(image.available)
			{
				const auto object = CapturedImageObject(image, encoded);
				const auto required = object.size() + 8U + (first ? 0U : 1U);
				if(object.empty() || !builder.canAppend(required))
				{
					image.available = false;
					image.width = image.height = 0;
					image.pixels.clear();
					encoded.clear();
					image.reason = "The captured menu image exceeded the bounded capture frame.";
				}
				else
				{
					builder.memberKey("image", first);
					builder.raw(object);
					imageBytes += image.pixels.size();
					return;
				}
			}

			const auto object = CapturedImageObject(image, {});
			const auto required = object.size() + 8U + (first ? 0U : 1U);
			if(!object.empty() && builder.canAppend(required))
			{
				builder.memberKey("image", first);
				builder.raw(object);
			}
		}

		HBITMAP FinalImage(const MenuItemInfo *item, bool &present)
		{
			present = false;
			if(!item)
				return nullptr;

			HBITMAP image = item->image.hbitmap;
			if(image)
				present = true;
			else if(item->image_select.hbitmap)
			{
				image = item->image_select.hbitmap;
				present = true;
			}

			if(!image && !item->is_ownerdraw() &&
				(item->hbmpItem || item->hbmpUnchecked ||
				item->hbmpChecked || item->dwItemData))
			{
				image = const_cast<MenuItemInfo *>(item)->get_image();
				present = true;
			}
			return image;
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

		bool HasSourceIdentity(const NativeMenu *source) noexcept
		{
			return source && !source->source_occurrence_unavailable &&
				(!source->source_file.empty() ||
				!source->source_node_id.empty() || !source->source_hash.empty() ||
				!source->source_occurrence_id.empty() || source->source_end != 0);
		}

		bool SourceOccurrenceUnavailable(const NativeMenu *source) noexcept
		{
			return source && source->source_occurrence_unavailable;
		}

		void AppendSourceReferenceFields(JsonBuilder &builder, bool &first,
			const NativeMenu *source)
		{
			if(!source)
				return;
			if(!source->source_file.empty())
				builder.memberString(first, "file", CopyWide(source->source_file));
			if(!source->source_node_id.empty())
			{
				const auto node = CopyWide(source->source_node_id);
				builder.memberString(first, "nodeId", node);
				uint64_t sourceStart = 0;
				if(TrySourceStart(source->source_node_id, sourceStart))
					builder.memberUInt(first, "start", sourceStart);
			}
			if(!source->source_hash.empty())
				builder.memberString(first, "hash", source->source_hash);
			if(source->source_end != 0)
				builder.memberUInt(first, "end", source->source_end);
			if(!source->source_occurrence_id.empty())
				builder.memberString(first, "occurrenceId", CopyWide(source->source_occurrence_id));
		}

		void AppendSource(JsonBuilder &builder, bool &first, const NativeMenu *source)
		{
			if(!HasSourceIdentity(source))
				return;
			builder.memberKey("source", first);
			builder.raw("{");
			bool sourceFirst = true;
			AppendSourceReferenceFields(builder, sourceFirst, source);
			if(!source->source_file.empty())
				builder.memberString(first, "sourceFile", CopyWide(source->source_file));
			if(!source->source_node_id.empty())
			{
				const auto node = CopyWide(source->source_node_id);
				builder.memberString(first, "sourceNodeId", node);
				uint64_t sourceStart = 0;
				if(TrySourceStart(source->source_node_id, sourceStart))
					builder.memberUInt(first, "sourceStart", sourceStart);
			}
			if(!source->source_hash.empty())
				builder.memberString(first, "sourceHash", source->source_hash);
			builder.raw("}");
		}

		void AppendCompleteness(JsonBuilder &builder, bool &first,
			const StudioCaptureCompleteness &value,
			const StudioCaptureEvidence *evidence = nullptr,
			const NativeMenu *source = nullptr)
		{
			StudioCaptureCompleteness completeness = value;
			const bool evidenceLimited = evidence &&
				(evidence->truncated ||
				 evidence->ruleOutcomes.size() >= StudioCaptureEvidence::MaxItems ||
				 evidence->propertyEffects.size() >= StudioCaptureEvidence::MaxItems);
			if(evidenceLimited)
			{
				completeness.state = "unavailable";
				// Evidence is attached to an already materialized entry.  The
				// entry's children remain represented, while the evidence ledger
				// itself is explicitly incomplete.
				completeness.childrenCaptured = true;
				completeness.complete = false;
				completeness.messageLimit = evidence->messageLimit != 0
					? evidence->messageLimit : StudioCaptureEvidence::MaxItems;
			}
			const bool sourceOccurrenceUnavailable =
				SourceOccurrenceUnavailable(source);
			if(sourceOccurrenceUnavailable)
			{
				completeness.state = "unavailable";
				completeness.complete = false;
			}
			builder.memberKey("completeness", first);
			builder.raw("{");
			bool memberFirst = true;
			builder.memberString(memberFirst, "state", completeness.state);
			builder.memberBool(memberFirst, "childrenCaptured", completeness.childrenCaptured);
			builder.memberBool(memberFirst, "complete", completeness.complete);
			if(completeness.depthLimit != 0)
				builder.memberUInt(memberFirst, "depthLimit", completeness.depthLimit);
			if(completeness.itemLimit != 0)
				builder.memberUInt(memberFirst, "itemLimit", completeness.itemLimit);
			if(completeness.messageLimit != 0)
				builder.memberUInt(memberFirst, "messageLimit", completeness.messageLimit);
			if(completeness.providerLimit != 0)
				builder.memberUInt(memberFirst, "providerLimit", completeness.providerLimit);
			if(completeness.evaluationLimit != 0)
				builder.memberUInt(memberFirst, "evaluationLimit", completeness.evaluationLimit);
			BeginArray(builder, memberFirst, "diagnostics");
			bool diagnosticFirst = true;
			if(evidenceLimited)
			{
				builder.raw("{\"code\":\"CAPTURE_EVIDENCE_LIMIT\",\"message\":");
				builder.quoted(L"Structured capture evidence reached its bounded item limit.");
				builder.raw(",\"severity\":\"warning\"}");
				diagnosticFirst = false;
			}
			if(sourceOccurrenceUnavailable)
			{
				if(!diagnosticFirst) builder.raw(",");
				builder.raw("{\"code\":\"CAPTURE_SOURCE_OCCURRENCE_UNAVAILABLE\",\"message\":");
				builder.quoted(L"The imported source occurrence was not retained; source edits are unavailable for this entry.");
				builder.raw(",\"severity\":\"warning\"}");
				diagnosticFirst = false;
			}
			for(const auto &diagnostic : completeness.diagnostics)
			{
				if(!diagnosticFirst) builder.raw(",");
				diagnosticFirst = false;
				builder.raw("{\"code\":\"CAPTURE_INCOMPLETE\",\"message\":");
				builder.quoted(diagnostic);
				builder.raw(",\"severity\":\"warning\"}");
			}
			EndArray(builder);
			builder.raw("}");
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

		void AppendEvidenceSource(JsonBuilder &builder, bool &first,
			const NativeMenu *source)
		{
			if(HasSourceIdentity(source))
			{
				builder.memberKey("source", first);
				builder.raw("{");
				bool sourceFirst = true;
				AppendSourceReferenceFields(builder, sourceFirst, source);
				builder.raw("}");
			}
			else
			{
				builder.memberKey("source", first);
				builder.raw("null");
			}
		}

		void AppendEvidence(JsonBuilder &builder, bool &first,
			const StudioCaptureEvidence &evidence, const NativeMenu *source,
			std::string_view entryId)
		{
			if(!HasSourceIdentity(source) && evidence.empty())
				return;

			builder.memberUInt(first, "evidenceVersion", StudioCaptureEvidence::Version);
			if(!evidence.ruleOutcomes.empty())
			{
				BeginArray(builder, first, "ruleOutcomes");
				bool itemFirst = true;
				for(size_t index = 0; index < evidence.ruleOutcomes.size() &&
					index < StudioCaptureEvidence::MaxItems; ++index)
				{
					const auto &outcome = evidence.ruleOutcomes[index];
					if(!itemFirst) builder.raw(",");
					itemFirst = false;
					builder.raw("{");
					bool memberFirst = true;
					builder.memberString(memberFirst, "ruleId", outcome.ruleId.empty()
						? std::string_view("native.evaluation") : std::string_view(outcome.ruleId));
					if(!entryId.empty())
						builder.memberString(memberFirst, "entryId", entryId);
					AppendEvidenceSource(builder, memberFirst, outcome.source);
					builder.memberString(memberFirst, "outcome", outcome.outcome);
					if(!outcome.reason.empty())
						builder.memberString(memberFirst, "reason", outcome.reason);
					builder.raw("}");
				}
				EndArray(builder);
			}
			if(!evidence.propertyEffects.empty())
			{
				BeginArray(builder, first, "propertyEffects");
				bool itemFirst = true;
				for(size_t index = 0; index < evidence.propertyEffects.size() &&
					index < StudioCaptureEvidence::MaxItems; ++index)
				{
					const auto &effect = evidence.propertyEffects[index];
					if(!itemFirst) builder.raw(",");
					itemFirst = false;
					builder.raw("{");
					bool memberFirst = true;
					if(!entryId.empty())
						builder.memberString(memberFirst, "entryId", entryId);
					builder.memberString(memberFirst, "property", effect.property);
					builder.memberString(memberFirst, "effect", effect.effect);
					if(!effect.value.empty())
						builder.memberString(memberFirst, "value", effect.value);
					AppendEvidenceSource(builder, memberFirst, effect.source);
					builder.raw("}");
				}
				EndArray(builder);
			}
		}

		void AppendRawEntry(JsonBuilder &builder, bool &first,
			const menuitem_t *item, std::wstring_view inheritedParent,
			size_t index, size_t depth, size_t &entryCount, size_t &imageBytes)
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
			builder.memberBool(memberFirst, "radio", item->radio_check);
			builder.memberBool(memberFirst, "isDefault", item->is_default);
			builder.memberBool(memberFirst, "ownerDraw", item->owner_draw);
			builder.memberString(memberFirst, "keys", std::wstring_view(item->keys));
			AppendCapturedImage(builder, memberFirst, item->image, imageBytes);
			AppendEvidence(builder, memberFirst, item->evidence, nullptr, id);
			builder.memberBool(memberFirst, "childrenCaptured", !popup ||
				item->studio_completeness.childrenCaptured || !item->items.empty());
			AppendCompleteness(builder, memberFirst, item->studio_completeness,
				&item->evidence);
			AppendTrace(builder, memberFirst, item->trace);
			BeginArray(builder, memberFirst, "children");
			const auto childPath = popup
				? JoinPath(parent, matchTitle.empty() ? title : matchTitle)
				: parent;
			bool childFirst = true;
			for(size_t childIndex = 0; childIndex < item->items.size() &&
				childIndex < kMaxEntries && entryCount < kMaxEntries; ++childIndex)
			{
				AppendRawEntry(builder, childFirst, item->items[childIndex],
					childPath, childIndex, depth + 1, entryCount, imageBytes);
			}
			EndArray(builder);
			EndObject(builder);
		}

		void AppendFinalEntry(JsonBuilder &builder, bool &first,
			const MenuItemInfo *item, std::wstring_view inheritedParent,
			size_t index, size_t depth, size_t &entryCount, size_t &imageBytes)
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
			// Dynamic and static entries retain the parsed NativeMenu that produced them.
			// Emit its source identity while that object is still owned by the
			// runtime cache; transient HMENU/command ids never cross the pipe.
			const auto source = item->owner_dynamic ? item->owner_dynamic : item->owner_static;
			AppendSource(builder, memberFirst, source);
			AppendEvidence(builder, memberFirst, item->evidence, source, id);
			if(!stableId.empty())
				builder.memberString(memberFirst, "stableId", stableId);
			if(!matchTitle.empty())
				builder.memberString(memberFirst, "matchTitle", matchTitle);
			if(!parent.empty())
				builder.memberString(memberFirst, "parentPath", parent);
			builder.memberBool(memberFirst, "disabled", item->is_disabled());
			builder.memberBool(memberFirst, "checked", item->is_checked());
			builder.memberBool(memberFirst, "radio", item->is_radiocheck());
			builder.memberBool(memberFirst, "isDefault", (item->fState & MFS_DEFAULT) != 0);
			builder.memberBool(memberFirst, "ownerDraw", item->is_ownerdraw());
			builder.memberString(memberFirst, "keys", std::wstring_view(item->keys));
			bool imagePresent = false;
			const auto image = FinalImage(item, imagePresent);
			if(imagePresent)
				AppendCapturedImage(builder, memberFirst, image, imageBytes);
			builder.memberBool(memberFirst, "childrenCaptured", !popup ||
				item->studio_completeness.childrenCaptured || !item->items.empty());
			AppendCompleteness(builder, memberFirst, item->studio_completeness,
				&item->evidence, source);
			AppendTrace(builder, memberFirst, item->trace);
			BeginArray(builder, memberFirst, "children");
			bool childFirst = true;
			const auto childPath = popup
				? JoinPath(parent, matchTitle.empty() ? title : matchTitle)
				: parent;
			for(size_t childIndex = 0; childIndex < item->items.size() &&
				childIndex < kMaxEntries && entryCount < kMaxEntries; ++childIndex)
				AppendFinalEntry(builder, childFirst, item->items[childIndex], childPath,
					childIndex, depth + 1, entryCount, imageBytes);
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

		void AppendSelection(JsonBuilder &builder, bool &first,
			const StudioCaptureSelection &selection)
		{
			builder.memberKey("selection", first);
			builder.raw("{");
			bool memberFirst = true;
			builder.memberUInt(memberFirst, "version", StudioCaptureSelection::Version);
			builder.memberBool(memberFirst, "background", selection.background);
			builder.memberInt(memberFirst, "windowId", selection.windowId);
			builder.memberInt(memberFirst, "mode", selection.mode);
			builder.memberInt(memberFirst, "front", selection.front);
			builder.memberBool(memberFirst, "windowDesktop", selection.windowDesktop);
			builder.memberBool(memberFirst, "windowExplorer", selection.windowExplorer);
			builder.memberBool(memberFirst, "windowExplorerTree", selection.windowExplorerTree);
			builder.memberString(memberFirst, "parent", selection.parent);
			builder.memberString(memberFirst, "parentRaw", selection.parentRaw);
			builder.memberString(memberFirst, "directory", selection.directory);

			BeginArray(builder, memberFirst, "types");
			// FSO_MAX is part of the wire contract.  A malformed or older producer
			// still yields a deterministic zero-filled array rather than changing
			// the shape that the managed reader validates.
			for(size_t index = 0; index < FSO_MAX; ++index)
			{
				if(index != 0)
					builder.raw(",");
				const auto value = index < selection.types.size() ? selection.types[index] : 0;
				builder.integer(value);
			}
			EndArray(builder);

			BeginArray(builder, memberFirst, "items");
			for(size_t index = 0; index < selection.items.size() &&
				index < StudioCapture::MaxSelectionItems; ++index)
			{
				if(index != 0)
					builder.raw(",");
				const auto &item = selection.items[index];
				builder.raw("{");
				bool itemFirst = true;
				builder.memberString(itemFirst, "path", item.path);
				builder.memberString(itemFirst, "raw", item.raw);
				builder.memberString(itemFirst, "name", item.name);
				builder.memberString(itemFirst, "title", item.title);
				builder.memberString(itemFirst, "extension", item.extension);
				builder.memberInt(itemFirst, "type", item.type);
				builder.memberInt(itemFirst, "group", item.group);
				builder.memberBool(itemFirst, "readOnly", item.readOnly);
				builder.memberBool(itemFirst, "hidden", item.hidden);
				builder.memberBool(itemFirst, "isLink", item.isLink);
				builder.raw("}");
			}
			EndArray(builder);
			builder.raw("}");
		}

		void AppendSettingSource(JsonBuilder &builder, bool &first,
			std::string_view property, std::string_view value)
		{
			if(!first)
				builder.raw(",");
			first = false;
			builder.raw("{");
			bool memberFirst = true;
			builder.memberString(memberFirst, "property", property);
			builder.memberString(memberFirst, "value", value);
			// Settings are stored as evaluated cache values rather than NativeMenu
			// rules.  Null source is deliberate evidence that this producer cannot
			// resolve a setting's authored node identity yet.
			builder.memberKey("source", memberFirst);
			builder.raw("null");
			builder.raw("}");
		}

		void AppendEffectiveSettings(JsonBuilder &builder, bool &first,
			const StudioCaptureMetadata &metadata)
		{
			if(!metadata.hasEffectiveSettings)
				return;
			builder.memberKey("effectiveSettings", first);
			builder.raw("{");
			bool memberFirst = true;

			builder.memberKey("modifyItems", memberFirst);
			builder.raw("{");
			bool itemsFirst = true;
			builder.memberBool(itemsFirst, "enabled", metadata.modifyItemsEnabled);
			builder.memberBool(itemsFirst, "title", metadata.modifyItemsTitle);
			builder.memberBool(itemsFirst, "visibility", metadata.modifyItemsVisibility);
			builder.memberBool(itemsFirst, "parent", metadata.modifyItemsParent);
			builder.memberBool(itemsFirst, "separator", metadata.modifyItemsSeparator);
			builder.memberBool(itemsFirst, "keys", metadata.modifyItemsKeys);
			builder.memberInt(itemsFirst, "image", metadata.modifyItemsImage);
			builder.memberInt(itemsFirst, "position", metadata.modifyItemsPosition);
			builder.raw("}");

			builder.memberKey("modifyMenu", memberFirst);
			builder.raw("{");
			bool menuFirst = true;
			builder.memberBool(menuFirst, "removeDuplicate", metadata.removeDuplicate);
			builder.memberBool(menuFirst, "removeDisabled", metadata.removeDisabled);
			builder.memberBool(menuFirst, "removeSeparator", metadata.removeSeparator);
			builder.raw("}");

			builder.memberKey("modifyProperties", memberFirst);
			builder.raw("{");
			bool propertiesFirst = true;
			builder.memberBool(propertiesFirst, "enabled", metadata.newItemsEnabled);
			builder.memberBool(propertiesFirst, "image", metadata.newItemsImage);
			builder.memberBool(propertiesFirst, "keys", metadata.newItemsKeys);
			builder.raw("}");

			BeginArray(builder, memberFirst, "sources");
			bool sourceFirst = true;
			AppendSettingSource(builder, sourceFirst, "modifyItems.enabled",
				metadata.modifyItemsEnabled ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyItems.title",
				metadata.modifyItemsTitle ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyItems.visibility",
				metadata.modifyItemsVisibility ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyItems.parent",
				metadata.modifyItemsParent ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyItems.separator",
				metadata.modifyItemsSeparator ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyItems.keys",
				metadata.modifyItemsKeys ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyItems.image",
				std::to_string(metadata.modifyItemsImage));
			AppendSettingSource(builder, sourceFirst, "modifyItems.position",
				std::to_string(metadata.modifyItemsPosition));
			AppendSettingSource(builder, sourceFirst, "modifyMenu.removeDuplicate",
				metadata.removeDuplicate ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyMenu.removeDisabled",
				metadata.removeDisabled ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyMenu.removeSeparator",
				metadata.removeSeparator ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyProperties.enabled",
				metadata.newItemsEnabled ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyProperties.image",
				metadata.newItemsImage ? "true" : "false");
			AppendSettingSource(builder, sourceFirst, "modifyProperties.keys",
				metadata.newItemsKeys ? "true" : "false");
			EndArray(builder);
			builder.raw("}");
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
			AppendSelection(builder, first, metadata.selection);
			if(metadata.hasEffectiveSettings)
				builder.memberUInt(first, "evidenceVersion", StudioCaptureEvidence::Version);
			AppendEffectiveSettings(builder, first, metadata);
		}

		void AppendAppearance(JsonBuilder &builder, bool &first,
			const StudioCaptureAppearance &appearance)
		{
			const bool available = ValidAppearanceGeometry(appearance);
			builder.memberKey("appearance", first);
			builder.raw("{");
			bool memberFirst = true;
			builder.memberUInt(memberFirst, "version", StudioCaptureAppearance::Version);
			builder.memberString(memberFirst, "source", StudioCaptureAppearance::Source);
			builder.memberString(memberFirst, "alphaMode", StudioCaptureAppearance::AlphaMode);
			builder.memberBool(memberFirst, "desktopEffectsOmitted",
				appearance.desktopEffectsOmitted);
			builder.memberString(memberFirst, "status", available ? "available" : "unavailable");
			builder.memberUInt(memberFirst, "width", available ? appearance.width : 0);
			builder.memberUInt(memberFirst, "height", available ? appearance.height : 0);
			builder.memberUInt(memberFirst, "dpi", appearance.dpi);

			std::string encoded;
			if(available)
				encoded = Base64(std::string_view(
					reinterpret_cast<const char *>(appearance.pixels.data()),
					appearance.pixels.size()));
			builder.memberString(memberFirst, "pixels", encoded);

			BeginArray(builder, memberFirst, "rows");
			if(available)
			{
				for(size_t index = 0; index < appearance.rows.size(); ++index)
				{
					if(index != 0)
						builder.raw(",");
					const auto &row = appearance.rows[index];
					builder.raw("{");
					bool rowFirst = true;
					builder.memberString(rowFirst, "entryId", row.entryId);
					builder.memberUInt(rowFirst, "x", row.x);
					builder.memberUInt(rowFirst, "y", row.y);
					builder.memberUInt(rowFirst, "width", row.width);
					builder.memberUInt(rowFirst, "height", row.height);
					builder.raw("}");
				}
			}
			EndArray(builder);
			builder.raw("}");
		}

		void AppendAppearanceDiagnostic(JsonBuilder &builder, bool &first,
			const StudioCaptureAppearance &appearance)
		{
			if(ValidAppearanceGeometry(appearance))
				return;
			const auto message = appearance.unavailableReason.empty()
				? std::string_view("The exact popup pixels were unavailable.")
				: std::string_view(appearance.unavailableReason);
			if(!first)
				builder.raw(",");
			first = false;
			builder.raw("{");
			bool memberFirst = true;
			builder.memberString(memberFirst, "code", "CAPTURE_APPEARANCE_UNAVAILABLE");
			builder.memberString(memberFirst, "message", message);
			builder.memberString(memberFirst, "severity", "warning");
			builder.raw("}");
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
			size_t imageBytes = 0;
			if(root)
			{
				for(size_t index = 0; index < root->items.size() &&
					index < kMaxEntries && entryCount < kMaxEntries; ++index)
					AppendRawEntry(builder, itemFirst, root->items[index], {}, index, 0,
						entryCount, imageBytes);
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
			const StudioCaptureMetadata &metadata, std::wstring_view parentPath,
			const StudioCaptureAppearance *appearance = nullptr)
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
			size_t imageBytes = 0;
			for(size_t index = 0; index < entries.size() && index < kMaxEntries &&
				entryCount < kMaxEntries; ++index)
				AppendFinalEntry(builder, itemFirst, entries[index], parentPath, index, 0,
					entryCount, imageBytes);
			EndArray(builder);

			if(appearance)
				AppendAppearance(builder, first, *appearance);

			BeginArray(builder, first, "diagnostics");
			if(appearance)
			{
				bool diagnosticFirst = true;
				AppendAppearanceDiagnostic(builder, diagnosticFirst, *appearance);
			}
			EndArray(builder);
			builder.raw("}");
			if(builder.valid())
				return std::move(builder).take();

			// A large semantic tree plus a valid bitmap can exceed the shared
			// 4 MiB frame.  Preserve the semantic capture and make the loss of
			// pixels explicit instead of dropping the whole final snapshot.
			if(appearance && appearance->available)
			{
				StudioCaptureAppearance unavailable;
				unavailable.dpi = appearance->dpi;
				unavailable.desktopEffectsOmitted = appearance->desktopEffectsOmitted;
				unavailable.unavailableReason =
					"The exact popup pixels exceeded the bounded capture frame.";
				return SerializeFinalEntries(entries, metadata, parentPath, &unavailable);
			}
			return {};
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

	std::string StudioCapture::FinalEntryId(const MenuItemInfo *item,
		std::wstring_view inheritedParent, size_t index)
	{
		if(!item)
			return {};
		const bool system = item->is_system && !item->dynamic;
		const auto parent = item->path.empty()
			? std::wstring(inheritedParent)
			: std::wstring(item->path.c_str(), item->path.length());
		const auto identity = item->hash != 0 ? item->hash : item->id;
		return EntryId(parent, index, identity, system);
	}

#ifdef STUDIO_CAPTURE_SERIALIZATION_TESTS
	std::string StudioCapture::SerializeOriginalForTesting(const menuitem_t *root,
		const StudioCaptureMetadata &metadata)
	{
		return SerializeOriginal(root, metadata);
	}

	std::string StudioCapture::SerializeFinalForTesting(
		const std::vector<MenuItemInfo *> &entries,
		const StudioCaptureMetadata &metadata, std::wstring_view parentPath)
	{
		return SerializeFinal(entries, metadata, parentPath);
	}
#endif

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
		activeEpoch_ = 0;
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
		activeEpoch_ = 0;
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

	uint64_t StudioCapture::ActiveEpoch() const
	{
		std::lock_guard<std::mutex> lock(mutex_);
		return connected_ && active_ && !stopping_ ? activeEpoch_ : 0;
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
		RetireLocked();
		connected_ = false;
		failed_ = false;
		includeOriginal_ = true;
		captureId_.clear();
		outbound_.clear();
	}

	void StudioCapture::RetireLocked() noexcept
	{
		if(!active_)
		{
			activeEpoch_ = 0;
			return;
		}

		const auto retiredEpoch = activeEpoch_;
		active_ = false;
		activeEpoch_ = 0;
		if(notificationWindow_ && retiredEpoch != 0)
		{
			// Studio targets x64, where LPARAM carries the complete uint64_t epoch.
			::PostMessageW(notificationWindow_, CaptureRetiredMessage,
				reinterpret_cast<WPARAM>(this), static_cast<LPARAM>(retiredEpoch));
		}
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

	void StudioCapture::FailIfEpoch(uint64_t expectedEpoch,
		std::string_view code, std::string_view message) noexcept
	{
		if(expectedEpoch == 0)
			return;
		try
		{
			std::lock_guard<std::mutex> lock(mutex_);
			if(!connected_ || !active_ || failed_ || activeEpoch_ != expectedEpoch)
				return;
			FailLocked(code, message);
		}
		catch(...)
		{
			// A stale or malformed publisher must never let a diagnostic escape the
			// Explorer thread or affect a later capture epoch.
		}
		changed_.notify_one();
	}

	void StudioCapture::FailLocked(std::string_view code, std::string_view message) noexcept
	{
		if(!connected_ || !active_ || failed_)
			return;

		failed_ = true;
		RetireLocked();
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

	std::string StudioCapture::SerializeFinal(const std::vector<MenuItemInfo *> &entries,
		const StudioCaptureMetadata &metadata, std::wstring_view parentPath,
		const StudioCaptureAppearance &appearance)
	{
		try
		{
			return SerializeFinalEntries(entries, metadata, parentPath, &appearance);
		}
		catch(...)
		{
			return {};
		}
	}

	bool StudioCapture::PublishOriginal(const menuitem_t *root,
		const StudioCaptureMetadata &metadata)
	{
		uint64_t captureEpoch = 0;
		try
		{
			std::string captureId;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || !includeOriginal_)
					return false;
				captureId = captureId_;
				captureEpoch = activeEpoch_;
				if(captureEpoch == 0)
					return false;
			}
			const auto snapshot = SerializeOriginal(root, metadata);
			if(snapshot.empty())
			{
				FailIfEpoch(captureEpoch, "CAPTURE_SERIALIZATION",
					"The native capture could not serialize the original menu.");
				return false;
			}
			const auto message = SnapshotMessage(captureId, "original", snapshot);
			if(message.empty())
			{
				FailIfEpoch(captureEpoch, "CAPTURE_SERIALIZATION",
					"The native capture snapshot exceeded the protocol limit.");
				return false;
			}
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || captureId_ != captureId ||
					activeEpoch_ != captureEpoch)
					return false;
				if(!EnqueueLocked(message))
					return false;
			}
			changed_.notify_one();
			return true;
		}
		catch(...)
		{
			FailIfEpoch(captureEpoch, "CAPTURE_SERIALIZATION",
				"The native capture could not publish the original menu.");
			return false;
		}
	}

	bool StudioCapture::PublishFinal(const std::vector<MenuItemInfo *> &entries,
		const StudioCaptureMetadata &metadata, std::wstring_view parentPath)
	{
		uint64_t captureEpoch = 0;
		try
		{
			std::string captureId;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_)
					return false;
				captureId = captureId_;
				captureEpoch = activeEpoch_;
				if(captureEpoch == 0)
					return false;
			}
			const auto snapshot = SerializeFinal(entries, metadata, parentPath);
			if(snapshot.empty())
			{
				FailIfEpoch(captureEpoch, "CAPTURE_SERIALIZATION",
					"The native capture could not serialize the displayed menu.");
				return false;
			}
			const auto message = SnapshotMessage(captureId, "final", snapshot);
			if(message.empty())
			{
				FailIfEpoch(captureEpoch, "CAPTURE_SERIALIZATION",
					"The native capture snapshot exceeded the protocol limit.");
				return false;
			}
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || captureId_ != captureId ||
					activeEpoch_ != captureEpoch)
					return false;
				if(!EnqueueLocked(message))
					return false;
			}
			changed_.notify_one();
			return true;
		}
		catch(...)
		{
			FailIfEpoch(captureEpoch, "CAPTURE_SERIALIZATION",
				"The native capture could not publish the displayed menu.");
			return false;
		}
	}

	bool StudioCapture::PublishFinal(const std::vector<MenuItemInfo *> &entries,
		const StudioCaptureMetadata &metadata, std::wstring_view parentPath,
		const StudioCaptureAppearance &appearance, uint64_t expectedEpoch)
	{
		try
		{
			if(expectedEpoch == 0)
				return false;
			std::string captureId;
			uint64_t captureEpoch = 0;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || activeEpoch_ == 0 ||
					activeEpoch_ != expectedEpoch)
					return false;
				captureId = captureId_;
				captureEpoch = activeEpoch_;
			}
			const auto snapshot = SerializeFinal(entries, metadata, parentPath, appearance);
			if(snapshot.empty())
			{
				FailIfEpoch(expectedEpoch, "CAPTURE_SERIALIZATION",
					"The native capture could not serialize the post-paint appearance.");
				return false;
			}
			const auto message = SnapshotMessage(captureId, "final", snapshot);
			if(message.empty())
			{
				FailIfEpoch(expectedEpoch, "CAPTURE_SERIALIZATION",
					"The native post-paint appearance exceeded the protocol limit.");
				return false;
			}
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(!connected_ || !active_ || captureId_ != captureId ||
					activeEpoch_ != captureEpoch || activeEpoch_ != expectedEpoch)
					return false;
				if(!EnqueueLocked(message))
					return false;
			}
			changed_.notify_one();
			return true;
		}
		catch(...)
		{
			FailIfEpoch(expectedEpoch, "CAPTURE_SERIALIZATION",
				"The native capture could not publish the post-paint appearance.");
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

			HWND notificationWindow = nullptr;
			uint64_t captureEpoch = 0;
			{
				std::lock_guard<std::mutex> lock(mutex_);
				if(stopping_)
					return;
				connected_ = true;
				active_ = true;
				failed_ = false;
				activeEpoch_ = NextCaptureEpoch();
				if(activeEpoch_ == 0)
				{
					connected_ = false;
					active_ = false;
					return;
				}
				captureEpoch = activeEpoch_;
				includeOriginal_ = request.includeOriginal;
				captureId_ = request.captureId;
				outbound_.clear();
				if(!EnqueueLocked(ReadyMessage(captureId_, sessionId_)))
					captureEpoch = 0;
				notificationWindow = notificationWindow_;
			}
			// Wake the owning menu thread immediately.  This closes the race where
			// the context was initialized before Studio's first request reached the
			// capture worker: the retry runs on the UI thread while the HMENU is still
			// owned by this context, and only then serializes the immutable snapshot.
			if(notificationWindow && captureEpoch != 0)
				::PostMessageW(notificationWindow, CaptureArmedMessage,
					reinterpret_cast<WPARAM>(this), static_cast<LPARAM>(captureEpoch));
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
						RetireLocked();
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
