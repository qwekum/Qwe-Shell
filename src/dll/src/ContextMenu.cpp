#include <pch.h>
#include "Include/Theme.h"
#include "Include/ContextMenu.h"
#include "Include/NativeMenuConstruction.h"
#include "Include/NativeRowSurface.h"
#include "Expression/Constants.h"
#include "Include/stb_image_write.h"

using namespace Nilesoft::Diagnostics;
#include <atomic>
#include <initializer_list>
#include <mutex>
#include <stdexcept>

extern Logger &_log;

namespace
{
	constexpr size_t kMaxCaptureTraceEntries = 64;
	constexpr size_t kMaxCaptureTraceChars = 1024;
	constexpr size_t kMaxCaptureEvidenceText = 1024;
	constexpr size_t kMaxRetainedCaptureEvaluations = 4096;
	constexpr size_t kMaxAutomaticCaptureDepth = 64;
	constexpr size_t kMaxAutomaticCaptureItems = 4096;
	constexpr unsigned long long kAutomaticCaptureBudgetMs = 100;
	constexpr uint32_t kMaxAppearanceWidth = 2048;
	constexpr uint32_t kMaxAppearanceHeight = 4096;
	constexpr uint64_t kMaxAppearancePixels = 600000;
	constexpr UINT kAppearanceTimerDelayMs = 16;
	constexpr size_t kMaxAppearanceRows = 4096;
	constexpr size_t kMaxAppearanceBytes =
		static_cast<size_t>(kMaxAppearancePixels) * 4U;
	constexpr size_t kMaxRetainedAppearanceBytes = 16U * 1024U * 1024U;
	std::atomic<UINT_PTR> next_appearance_timer_id{0x6A510000U};
	thread_local Nilesoft::Shell::StudioCapture *capture_trace_owner = nullptr;
	thread_local Nilesoft::Shell::StudioCaptureEvidence *capture_evidence_owner = nullptr;

	UINT_PTR NextAppearanceTimerId() noexcept
	{
		auto id = next_appearance_timer_id.fetch_add(1, std::memory_order_relaxed);
		if(id == 0)
			id = next_appearance_timer_id.fetch_add(1, std::memory_order_relaxed);
		return id;
	}

	void NormalizePremultiplied(uint8_t *pixel) noexcept
	{
		if(!pixel)
			return;
		const auto alpha = pixel[3];
		if(pixel[0] > alpha) pixel[0] = alpha;
		if(pixel[1] > alpha) pixel[1] = alpha;
		if(pixel[2] > alpha) pixel[2] = alpha;
	}

	void CompositePremultiplied(uint8_t *destination, const uint8_t *source) noexcept
	{
		if(!destination || !source)
			return;
		const auto sourceAlpha = source[3];
		if(sourceAlpha == 0)
			return;

		const auto inverse = static_cast<unsigned>(255U - sourceAlpha);
		for(int channel = 0; channel < 3; ++channel)
		{
			const auto sourceValue = source[channel] > sourceAlpha
				? sourceAlpha : source[channel];
			const auto destinationValue = destination[channel];
			destination[channel] = static_cast<uint8_t>(sourceValue +
				((static_cast<unsigned>(destinationValue) * inverse + 127U) / 255U));
		}
		const auto destinationAlpha = destination[3];
		destination[3] = static_cast<uint8_t>(sourceAlpha +
			((static_cast<unsigned>(destinationAlpha) * inverse + 127U) / 255U));
		NormalizePremultiplied(destination);
	}

	bool IntersectAppearanceRect(const RECT &source, long width, long height,
		RECT &clipped, long scrollInset = 0) noexcept
	{
		RECT bounds{0, scrollInset, width, height - scrollInset};
		return ::IntersectRect(&clipped, &source, &bounds) &&
			clipped.right > clipped.left && clipped.bottom > clipped.top;
	}

	bool GetTopDownBitmap(HBITMAP bitmap, long width, long height,
		std::vector<uint8_t> &pixels)
	{
		if(!bitmap || width <= 0 || height <= 0)
			return false;
		const auto byteCount = static_cast<size_t>(width) *
			static_cast<size_t>(height) * 4U;
		if(byteCount > kMaxAppearanceBytes)
			return false;
		BITMAPINFO info{};
		info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
		info.bmiHeader.biWidth = width;
		info.bmiHeader.biHeight = -height;
		info.bmiHeader.biPlanes = 1;
		info.bmiHeader.biBitCount = 32;
		info.bmiHeader.biCompression = BI_RGB;
		pixels.resize(byteCount);
		Nilesoft::Drawing::DC dc(::CreateCompatibleDC(nullptr), 1);
		if(!dc || ::GetDIBits(dc, bitmap, 0, static_cast<UINT>(height),
			pixels.data(), &info, DIB_RGB_COLORS) != static_cast<int>(height))
		{
			pixels.clear();
			return false;
		}
		for(size_t index = 0; index < pixels.size(); index += 4)
			NormalizePremultiplied(pixels.data() + index);
		return true;
	}

	bool GetMenuItemClientRect(HWND popup, HMENU menu, UINT position,
		RECT &rect) noexcept
	{
		if(!popup || !menu || !::GetMenuItemRect(popup, menu, position, &rect))
			return false;
		POINT topLeft{rect.left, rect.top};
		POINT bottomRight{rect.right, rect.bottom};
		if(!::ScreenToClient(popup, &topLeft) ||
			!::ScreenToClient(popup, &bottomRight))
			return false;
		rect = {topLeft.x, topLeft.y, bottomRight.x, bottomRight.y};
		return rect.right > rect.left && rect.bottom > rect.top;
	}

	class CaptureTraceScope final
	{
	public:
		explicit CaptureTraceScope(Nilesoft::Shell::StudioCapture *owner)
			: previous_(capture_trace_owner)
		{
			capture_trace_owner = owner;
		}
		~CaptureTraceScope() { capture_trace_owner = previous_; }

	private:
		Nilesoft::Shell::StudioCapture *previous_;
	};

	class CaptureEvidenceScope final
	{
	public:
		explicit CaptureEvidenceScope(Nilesoft::Shell::StudioCaptureEvidence *owner)
			: previous_(capture_evidence_owner)
		{
			capture_evidence_owner = owner;
		}
		~CaptureEvidenceScope() { capture_evidence_owner = previous_; }

	private:
		Nilesoft::Shell::StudioCaptureEvidence *previous_;
	};

	bool CaptureTracingEnabled()
	{
		return capture_trace_owner != nullptr && capture_trace_owner->IsActive();
	}

	void CaptureTraceFailure() noexcept
	{
		if(capture_trace_owner)
			capture_trace_owner->Fail("CAPTURE_TRACE_MEMORY",
				"The native capture could not retain a rule evaluation trace.");
	}

	std::string CaptureEvidenceId(std::wstring_view value) noexcept
	{
		try
		{
			std::string result;
			result.reserve(value.size());
			for(const auto character : value)
			{
				if(character > 0x7f)
					return {};
				result.push_back(static_cast<char>(character));
			}
			return result;
		}
		catch(...)
		{
			return {};
		}
	}

	std::string CaptureOutcome(bool matched, std::wstring_view action) noexcept
	{
		if(action.find(L"removed") != std::wstring_view::npos ||
			action.find(L"would remove") != std::wstring_view::npos)
			return "removed";
		if(action.find(L"overwritten") != std::wstring_view::npos ||
			action.find(L"would be replaced") != std::wstring_view::npos ||
			action.find(L"replaced by") != std::wstring_view::npos ||
			action.find(L"replaced ") != std::wstring_view::npos)
			return "overwritten";
		if(action.find(L"blocked") != std::wstring_view::npos ||
			action.find(L"modification disabled") != std::wstring_view::npos ||
			action.find(L"not evaluated") != std::wstring_view::npos)
			return "blocked";
		if(action.find(L"not applied") != std::wstring_view::npos ||
			action.find(L"not defined") != std::wstring_view::npos ||
			action.find(L"evaluation failed") != std::wstring_view::npos ||
			action.find(L"empty; skipped") != std::wstring_view::npos ||
			action.find(L"invalid pattern; skipped") != std::wstring_view::npos ||
			action.find(L"destination unchanged") != std::wstring_view::npos ||
			action.find(L"skipped") != std::wstring_view::npos ||
			action.find(L"rejected") != std::wstring_view::npos)
			return "skipped";
		return matched ? "matched" : "skipped";
	}

	std::string CapturePropertyName(std::wstring_view rule) noexcept
	{
		const auto separator = rule.find(L'.');
		if(separator == std::wstring_view::npos)
			return {};
		const auto family = rule.substr(0, separator);
		if(family != L"static" && family != L"dynamic" && family != L"remove")
			return {};
		auto property = rule.substr(separator + 1);
		if(property == L"rule" || property == L"result" || property == L"match")
			return {};
		if(property == L"types")
			property = L"type";
		else if(property == L"moveto")
			property = L"parent";
		return CaptureEvidenceId(property);
	}

	std::wstring CaptureEvidenceValue(std::wstring_view action)
	{
		const auto equals = action.find(L'=');
		if(equals == std::wstring_view::npos)
			return {};
		const auto prefix = action.substr(0, equals);
		if(prefix != L"value" && prefix != L"pos" && prefix != L"id")
			return {};
		auto value = action.substr(equals + 1);
		const auto separator = value.find(L';');
		if(separator != std::wstring_view::npos)
			value = value.substr(0, separator);
		if(value.size() > kMaxCaptureEvidenceText)
			value = value.substr(0, kMaxCaptureEvidenceText);
		return std::wstring(value);
	}

	void CaptureStructuredEvidence(const Nilesoft::Shell::NativeMenu *source,
		std::wstring_view rule, bool matched, std::wstring_view action) noexcept
	{
		if(!CaptureTracingEnabled() || !capture_evidence_owner)
			return;
		try
		{
			auto &evidence = *capture_evidence_owner;
			const auto markEvidenceLimit = [&]() noexcept
			{
				evidence.truncated = true;
				evidence.messageLimit = Nilesoft::Shell::StudioCaptureEvidence::MaxItems;
			};
			if(evidence.ruleOutcomes.size() < Nilesoft::Shell::StudioCaptureEvidence::MaxItems)
			{
				Nilesoft::Shell::StudioCaptureRuleOutcome outcome;
				outcome.source = source;
				outcome.ruleId = CaptureEvidenceId(rule);
				if(outcome.ruleId.empty())
					outcome.ruleId = "native.evaluation";
				outcome.outcome = CaptureOutcome(matched, action);
				if(action.size() > kMaxCaptureEvidenceText)
					outcome.reason.assign(action.substr(0, kMaxCaptureEvidenceText));
				else
					outcome.reason.assign(action);
				evidence.ruleOutcomes.push_back(std::move(outcome));
				if(evidence.ruleOutcomes.size() >=
					Nilesoft::Shell::StudioCaptureEvidence::MaxItems)
					markEvidenceLimit();
			}
			else
				markEvidenceLimit();

			const auto property = CapturePropertyName(rule);
			if(!property.empty() && evidence.propertyEffects.size() <
				Nilesoft::Shell::StudioCaptureEvidence::MaxItems)
			{
				Nilesoft::Shell::StudioCapturePropertyEffect effect;
				effect.source = source;
				effect.property = property;
				effect.effect = CaptureOutcome(matched, action);
				if(effect.effect == "matched")
					effect.effect = "applied";
				effect.value = CaptureEvidenceValue(action);
				evidence.propertyEffects.push_back(std::move(effect));
				if(evidence.propertyEffects.size() >=
					Nilesoft::Shell::StudioCaptureEvidence::MaxItems)
					markEvidenceLimit();
			}
			else if(!property.empty())
				markEvidenceLimit();
		}
		catch(...)
		{
			CaptureTraceFailure();
		}
	}

	void CaptureTrace(Nilesoft::Shell::StudioCaptureTrace &trace,
		std::wstring_view text) noexcept
	{
		if(!CaptureTracingEnabled() || trace.size() >= kMaxCaptureTraceEntries || text.empty())
			return;
		try
		{
			const auto length = text.size() < kMaxCaptureTraceChars
				? text.size() : kMaxCaptureTraceChars;
			trace.emplace_back(text.substr(0, length));
		}
		catch(...)
		{
			CaptureTraceFailure();
		}
	}

	void CaptureTrace(Nilesoft::Shell::StudioCaptureTrace &trace, std::wstring_view rule,
		bool matched, std::wstring_view action = {}) noexcept
	{
		if(!CaptureTracingEnabled())
			return;
		try
		{
			std::wstring text(rule);
			text += matched ? L": matched" : L": not matched";
			if(!action.empty())
			{
				text += L"; ";
				text += action;
			}
			CaptureStructuredEvidence(nullptr, rule, matched, action);
			CaptureTrace(trace, text);
		}
		catch(...)
		{
			CaptureTraceFailure();
		}
	}

	void CaptureTraceNumber(Nilesoft::Shell::StudioCaptureTrace &trace,
		std::wstring_view prefix, int value) noexcept
	{
		if(!CaptureTracingEnabled())
			return;
		try
		{
			std::wstring text(prefix);
			text += std::to_wstring(value);
			CaptureTrace(trace, text);
		}
		catch(...)
		{
			CaptureTraceFailure();
		}
	}

	void CaptureTraceSource(Nilesoft::Shell::StudioCaptureTrace &trace,
		const Nilesoft::Shell::NativeMenu *source,
		std::wstring_view rule, bool matched, std::wstring_view action = {}) noexcept
	{
		if(!CaptureTracingEnabled())
			return;
		try
		{
			if(!source || (source->source_file.empty() && source->source_node_id.empty()))
			{
				CaptureTrace(trace, rule, matched, action);
				return;
			}
			CaptureStructuredEvidence(source, rule, matched, action);
			std::wstring named(rule);
			named += L" [source=";
			if(!source->source_file.empty())
				named.append(source->source_file.c_str(), source->source_file.length());
			if(!source->source_node_id.empty())
			{
				named += L'#';
				named.append(source->source_node_id.c_str(), source->source_node_id.length());
			}
			named += L"]";
			named += matched ? L": matched" : L": not matched";
			if(!action.empty())
			{
				named += L"; ";
				named += action;
			}
			CaptureTrace(trace, named);
		}
		catch(...)
		{
			CaptureTraceFailure();
		}
	}

	void CaptureTraceSourceNumber(Nilesoft::Shell::StudioCaptureTrace &trace,
		const Nilesoft::Shell::NativeMenu *source, std::wstring_view rule,
		std::wstring_view prefix, int value) noexcept
	{
		if(!CaptureTracingEnabled())
			return;
		try
		{
			std::wstring action(prefix);
			action += std::to_wstring(value);
			CaptureTraceSource(trace, source, rule, true, action);
		}
		catch(...)
		{
			CaptureTraceFailure();
		}
	}

	bool CaptureIdentIn(uint32_t id, std::initializer_list<uint32_t> values) noexcept
	{
		for(const auto value : values)
			if(id == value)
				return true;
		return false;
	}

	bool CaptureIdentPath(const Nilesoft::Shell::Ident &id,
		std::initializer_list<uint32_t> values) noexcept
	{
		if(id.length() != values.size())
			return false;
		size_t index = 0;
		for(const auto value : values)
		{
			if(id[index++] != value)
				return false;
		}
		return true;
	}

	bool CaptureStringMember(uint32_t id) noexcept
	{
		return CaptureIdentIn(id, {Nilesoft::Shell::IDENT_LEN,
			Nilesoft::Shell::IDENT_LENGTH, Nilesoft::Shell::IDENT_NULL,
			Nilesoft::Shell::IDENT_EMPTY, Nilesoft::Shell::IDENT_UPPER,
			Nilesoft::Shell::IDENT_LOWER, Nilesoft::Shell::IDENT_CAPITALIZE,
			Nilesoft::Shell::IDENT_HASH, Nilesoft::Shell::IDENT_TRIM,
			Nilesoft::Shell::IDENT_TRIMSTART, Nilesoft::Shell::IDENT_TRIMEND,
			Nilesoft::Shell::IDENT_SET, Nilesoft::Shell::IDENT_CHAR,
			Nilesoft::Shell::IDENT_GET, Nilesoft::Shell::IDENT_AT,
			Nilesoft::Shell::IDENT_SUB, Nilesoft::Shell::IDENT_LEFT,
			Nilesoft::Shell::IDENT_RIGHT, Nilesoft::Shell::IDENT_PADDING,
			Nilesoft::Shell::IDENT_PADLEFT, Nilesoft::Shell::IDENT_PADRIGHT,
			Nilesoft::Shell::IDENT_NOT, Nilesoft::Shell::IDENT_EQ,
			Nilesoft::Shell::IDENT_EQUALS, Nilesoft::Shell::IDENT_START,
			Nilesoft::Shell::IDENT_END, Nilesoft::Shell::IDENT_FIND,
			Nilesoft::Shell::IDENT_FINDLAST, Nilesoft::Shell::IDENT_CONTAINS,
			Nilesoft::Shell::IDENT_REPLACE, Nilesoft::Shell::IDENT_REMOVE,
			Nilesoft::Shell::IDENT_JOIN, Nilesoft::Shell::IDENT_SPLIT,
			Nilesoft::Shell::IDENT_TAG, Nilesoft::Shell::IDENT_FORMAT});
	}

	bool CaptureStringPath(const Nilesoft::Shell::Ident &id) noexcept
	{
		if(id.length() == 1)
			return true;
		if(id.length() == 2)
			return CaptureStringMember(id[1]);
		return id.length() == 3 && id[1] == Nilesoft::Shell::IDENT_DECODE &&
			id[2] == Nilesoft::Shell::IDENT_URL;
	}

	bool CaptureSelectionMember(uint32_t id) noexcept
	{
		return CaptureIdentIn(id, {Nilesoft::Shell::IDENT_COUNT,
			Nilesoft::Shell::IDENT_READONLY, Nilesoft::Shell::IDENT_HIDDEN,
			Nilesoft::Shell::IDENT_BACK, Nilesoft::Shell::IDENT_LEN,
			Nilesoft::Shell::IDENT_LENGTH, Nilesoft::Shell::IDENT_WORKDIR,
			Nilesoft::Shell::IDENT_CURDIR, Nilesoft::Shell::IDENT_PATH,
			Nilesoft::Shell::IDENT_FULL, Nilesoft::Shell::IDENT_PARENT,
			Nilesoft::Shell::IDENT_LOCATION, Nilesoft::Shell::IDENT_ROOT,
			Nilesoft::Shell::IDENT_ITEM, Nilesoft::Shell::IDENT_NAME,
			Nilesoft::Shell::IDENT_TITLE, Nilesoft::Shell::IDENT_FILE,
			Nilesoft::Shell::IDENT_DIR, Nilesoft::Shell::IDENT_DIRECTORY,
			Nilesoft::Shell::IDENT_MODE, Nilesoft::Shell::IDENT_TYPE,
			Nilesoft::Shell::IDENT_TYPES, Nilesoft::Shell::IDENT_PATHS,
			Nilesoft::Shell::IDENT_TITLES, Nilesoft::Shell::IDENT_NAMES,
			Nilesoft::Shell::IDENT_EXTS, Nilesoft::Shell::IDENT_DIRS,
			Nilesoft::Shell::IDENT_DIRECTORIES});
	}

	bool CaptureSelectionChild(uint32_t parent, uint32_t id) noexcept
	{
		if(!CaptureIdentIn(id, {Nilesoft::Shell::IDENT_LEN,
			Nilesoft::Shell::IDENT_LENGTH, Nilesoft::Shell::IDENT_NAME,
			Nilesoft::Shell::IDENT_TITLE, Nilesoft::Shell::IDENT_QUOTE,
			Nilesoft::Shell::IDENT_COUNT}))
			return false;
		return parent != Nilesoft::Shell::IDENT_ITEM ||
			id == Nilesoft::Shell::IDENT_LEN || id == Nilesoft::Shell::IDENT_LENGTH;
	}

	bool CaptureSelectionPath(const Nilesoft::Shell::Ident &id) noexcept
	{
		if(id.length() == 1)
			return true;
		if(!CaptureSelectionMember(id[1]))
			return false;
		if(id.length() == 2)
			return true;
		if(id.length() != 3)
			return false;
		if(id[1] == Nilesoft::Shell::IDENT_PATH ||
			id[1] == Nilesoft::Shell::IDENT_FULL)
			return CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_LEN,
				Nilesoft::Shell::IDENT_LENGTH, Nilesoft::Shell::IDENT_NAME,
				Nilesoft::Shell::IDENT_TITLE, Nilesoft::Shell::IDENT_QUOTE});
		if(id[1] == Nilesoft::Shell::IDENT_PARENT ||
			id[1] == Nilesoft::Shell::IDENT_LOCATION ||
			id[1] == Nilesoft::Shell::IDENT_FILE ||
			id[1] == Nilesoft::Shell::IDENT_DIR ||
			id[1] == Nilesoft::Shell::IDENT_DIRECTORY)
			return CaptureSelectionChild(id[1], id[2]);
		if(CaptureIdentIn(id[1], {Nilesoft::Shell::IDENT_TYPES,
			Nilesoft::Shell::IDENT_PATHS, Nilesoft::Shell::IDENT_TITLES,
			Nilesoft::Shell::IDENT_NAMES, Nilesoft::Shell::IDENT_EXTS,
			Nilesoft::Shell::IDENT_DIRS, Nilesoft::Shell::IDENT_DIRECTORIES}))
			return CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_COUNT,
				Nilesoft::Shell::IDENT_NAME, Nilesoft::Shell::IDENT_TITLE,
				Nilesoft::Shell::IDENT_QUOTE});
		// `sel.type` and `sel.mode` are read-only values.  Their nested
		// namespaces are intentionally closed until each enum member is audited.
		return false;
	}

	bool CapturePathPath(const Nilesoft::Shell::Ident &id) noexcept
	{
		if(id.length() == 2 && CaptureIdentIn(id[1], {
			Nilesoft::Shell::IDENT_EXT, Nilesoft::Shell::IDENT_ROOT,
			Nilesoft::Shell::IDENT_NAME, Nilesoft::Shell::IDENT_TITLE,
			Nilesoft::Shell::IDENT_PARENT, Nilesoft::Shell::IDENT_LOCATION,
			Nilesoft::Shell::IDENT_JOIN, Nilesoft::Shell::IDENT_COMBINE,
			Nilesoft::Shell::IDENT_SEP, Nilesoft::Shell::IDENT_SEPARATOR,
			Nilesoft::Shell::IDENT_ISABSOLUTE, Nilesoft::Shell::IDENT_ISRELATIVE,
			Nilesoft::Shell::IDENT_ISCLSID, Nilesoft::Shell::IDENT_ISNAMESPACE,
			Nilesoft::Shell::IDENT_REMOVEEXTENSION}))
			return true;
		if(id.length() == 3 && (id[1] == Nilesoft::Shell::IDENT_PARENT ||
			id[1] == Nilesoft::Shell::IDENT_LOCATION))
			return CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_NAME,
				Nilesoft::Shell::IDENT_TITLE});
		return id.length() == 3 && id[1] == Nilesoft::Shell::IDENT_FILE &&
			CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_EXT,
				Nilesoft::Shell::IDENT_NAME, Nilesoft::Shell::IDENT_TITLE});
	}

	bool CaptureColorPath(const Nilesoft::Shell::Ident &id) noexcept
	{
		if(id.length() == 1)
			return true;
		if(id.length() != 2)
			return false;
		if(CaptureIdentIn(id[1], {Nilesoft::Shell::IDENT_COLOR_RGB,
			Nilesoft::Shell::IDENT_COLOR_RGBA, Nilesoft::Shell::IDENT_COLOR_INVERT,
			Nilesoft::Shell::IDENT_COLOR_LIGHT, Nilesoft::Shell::IDENT_COLOR_DARK,
			Nilesoft::Shell::IDENT_COLOR_ADJUST, Nilesoft::Shell::IDENT_COLOR_LIGHTEN,
			Nilesoft::Shell::IDENT_COLOR_DARKEN, Nilesoft::Shell::IDENT_OPACITY}))
			return true;
		if(CaptureIdentIn(id[1], {Nilesoft::Shell::IDENT_COLOR_ACCENT,
			Nilesoft::Shell::IDENT_COLOR_ACCENT_LIGHT1,
			Nilesoft::Shell::IDENT_COLOR_ACCENT_LIGHT2,
			Nilesoft::Shell::IDENT_COLOR_ACCENT_LIGHT3,
			Nilesoft::Shell::IDENT_COLOR_ACCENT_DARK1,
			Nilesoft::Shell::IDENT_COLOR_ACCENT_DARK2,
			Nilesoft::Shell::IDENT_COLOR_ACCENT_DARK3,
			Nilesoft::Shell::IDENT_COLOR_RANDOM,
			Nilesoft::Shell::IDENT_BOX}))
			return false;
	for(const auto &entry : Nilesoft::Shell::ColorTable)
		if(std::get<0>(entry) == id[1])
			return true;
	return false;
	}

	bool CaptureThemePath(const Nilesoft::Shell::Ident &id) noexcept
	{
		if(id.length() == 2 && CaptureIdentIn(id[1], {
			Nilesoft::Shell::IDENT_AUTO, Nilesoft::Shell::IDENT_THEME_SYSTEM,
			Nilesoft::Shell::IDENT_THEME_CLASSIC, Nilesoft::Shell::IDENT_THEME_LIGHT,
			Nilesoft::Shell::IDENT_THEME_DARK, Nilesoft::Shell::IDENT_THEME_HIGHCONTRAST,
			Nilesoft::Shell::IDENT_THEME_BLACK, Nilesoft::Shell::IDENT_THEME_WHITE,
			Nilesoft::Shell::IDENT_THEME_MODERN, Nilesoft::Shell::IDENT_THEME_CUSTOM,
			Nilesoft::Shell::IDENT_ISDARK, Nilesoft::Shell::IDENT_ISLIGHT,
			Nilesoft::Shell::IDENT_ISHIGHCONTRAST, Nilesoft::Shell::IDENT_MODE,
			Nilesoft::Shell::IDENT_BACKGROUND}))
			return true;
	if(id.length() == 3 && id[1] == Nilesoft::Shell::IDENT_MODE)
		return CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_ZERO,
			Nilesoft::Shell::IDENT_SYSTEM});
	if(id.length() == 3 && id[1] == Nilesoft::Shell::IDENT_BACKGROUND)
		return CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_OPACITY,
			Nilesoft::Shell::IDENT_EFFECT});
	// Theme item colours are immutable reads from the already loaded theme.
	return id.length() == 5 && id[1] == Nilesoft::Shell::IDENT_ITEM &&
		CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_BACK,
			Nilesoft::Shell::IDENT_TEXT}) &&
		CaptureIdentIn(id[3], {Nilesoft::Shell::IDENT_NORMAL,
			Nilesoft::Shell::IDENT_SELECT}) && id[4] == Nilesoft::Shell::IDENT_DISABLE;
}

	bool CaptureViewPath(const Nilesoft::Shell::Ident &id) noexcept
	{
		return id.length() == 2 && CaptureIdentIn(id[1], {
			Nilesoft::Shell::IDENT_AUTO, Nilesoft::Shell::IDENT_VIEW_COMPACT,
			Nilesoft::Shell::IDENT_VIEW_SMALL, Nilesoft::Shell::IDENT_VIEW_MEDIUM,
			Nilesoft::Shell::IDENT_VIEW_LARGE, Nilesoft::Shell::IDENT_VIEW_WIDE});
	}

	bool CaptureThisPath(const Nilesoft::Shell::Ident &id) noexcept
	{
		if(id.length() == 2 && CaptureIdentIn(id[1], {
			Nilesoft::Shell::IDENT_TYPE, Nilesoft::Shell::IDENT_CHECKED,
			Nilesoft::Shell::IDENT_POS, Nilesoft::Shell::IDENT_DISABLED,
			Nilesoft::Shell::IDENT_SYS, Nilesoft::Shell::IDENT_NAME,
			Nilesoft::Shell::IDENT_TITLE, Nilesoft::Shell::MENU_VERB,
			Nilesoft::Shell::IDENT_COUNT, Nilesoft::Shell::IDENT_LEVEL,
			Nilesoft::Shell::IDENT_ID, Nilesoft::Shell::IDENT_PARENT,
			Nilesoft::Shell::IDENT_ISUWP, Nilesoft::Shell::IDENT_CLSID}))
			return true;
	if(id.length() == 3 && CaptureIdentIn(id[1], {
			Nilesoft::Shell::IDENT_NAME, Nilesoft::Shell::IDENT_TITLE}))
		return CaptureIdentIn(id[2], {Nilesoft::Shell::IDENT_LEN,
			Nilesoft::Shell::IDENT_LENGTH, Nilesoft::Shell::IDENT_ZERO});
	return false;
}

	// Automatic capture may evaluate ordinary string/selection expressions, but
	// a function is allowed to run natively only when its implementation is
	// known to be read-only.  Unknown functions remain unavailable by default;
	// this is a positive allowlist and is deliberately independent of the much
	// larger runtime function inventory.
	Nilesoft::Shell::PreviewPolicy::Dispatch CaptureFunctionDispatch(
		Nilesoft::Shell::FuncExpression &function,
		Nilesoft::Shell::Context &, Nilesoft::Object &) noexcept
	{
		const auto unavailable = Nilesoft::Shell::PreviewPolicy::Dispatch::Unavailable;
		const auto native = Nilesoft::Shell::PreviewPolicy::Dispatch::Native;
		if(function.ischild && function.Parent)
			return function.Id.length() == 1 && CaptureStringMember(function.Id[0])
				? native : unavailable;

		const auto id = function.Id[0];
		if(id == Nilesoft::Shell::IDENT_STR)
			return CaptureStringPath(function.Id) ? native : unavailable;
		if(id == Nilesoft::Shell::IDENT_SEL)
			return CaptureSelectionPath(function.Id) ? native : unavailable;
		if(id == Nilesoft::Shell::IDENT_PATH)
			return CapturePathPath(function.Id) ? native : unavailable;
		if(id == Nilesoft::Shell::IDENT_COLOR)
			return CaptureColorPath(function.Id) ? native : unavailable;
		if(id == Nilesoft::Shell::IDENT_THEME)
			return CaptureThemePath(function.Id) ? native : unavailable;
		if(id == Nilesoft::Shell::IDENT_VIEW)
			return CaptureViewPath(function.Id) ? native : unavailable;
		if(id == Nilesoft::Shell::IDENT_THIS)
			return CaptureThisPath(function.Id) ? native : unavailable;

		// These implementations only combine or inspect already evaluated
		// arguments and do not touch Explorer, the filesystem, or process state.
		if(CaptureIdentIn(id, {Nilesoft::Shell::IDENT_IF,
			Nilesoft::Shell::IDENT_NOT, Nilesoft::Shell::IDENT_TOHEX,
			Nilesoft::Shell::IDENT_PRINT, Nilesoft::Shell::IDENT_CHAR,
			Nilesoft::Shell::IDENT_SHL, Nilesoft::Shell::IDENT_SHR,
			Nilesoft::Shell::IDENT_EQUAL, Nilesoft::Shell::IDENT_EQUALS,
			Nilesoft::Shell::IDENT_GREATER, Nilesoft::Shell::IDENT_LESS,
			Nilesoft::Shell::IDENT_LEN, Nilesoft::Shell::IDENT_LENGTH,
			Nilesoft::Shell::IDENT_QUOTE, Nilesoft::Shell::IDENT_TOINT,
			Nilesoft::Shell::IDENT_TOUINT, Nilesoft::Shell::IDENT_TODOUBLE,
			Nilesoft::Shell::IDENT_TOFLOAT}))
			return function.Id.length() == 1 ? native : unavailable;

		// Read-only enum/value namespaces are safe when their member set is
		// closed.  No runtime aliases are accepted here.
		if(id == Nilesoft::Shell::IDENT_MODE)
			return CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_NONE}) ||
				CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_SINGLE}) ||
				CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_UNIQUE}) ||
				CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_MULTIPLE}) ||
				CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_MULTI}) ||
				CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_MULTI_SINGLE}) ||
				CaptureIdentPath(function.Id, {Nilesoft::Shell::IDENT_MODE,
				Nilesoft::Shell::IDENT_MODE_MULTI_UNIQUE}) ? native : unavailable;
		return unavailable;
	}
}

#pragma region

/*
void plutovg_surface_write_to_hdc(plutovg_surface_t *surface, int x, int y, HDC hdc)
{
	BYTE bitmapinfo[FIELD_OFFSET(BITMAPINFO, bmiColors) + (3 * sizeof(DWORD))];
	auto &bih = *(BITMAPINFOHEADER *)bitmapinfo;
	bih.biSize = sizeof(BITMAPINFOHEADER);

	bih.biWidth = surface->width;
	bih.biHeight = surface->height;

	bih.biPlanes = 1;
	bih.biBitCount = 32;

	bih.biCompression = BI_BITFIELDS;
	bih.biSizeImage = 0;
	bih.biClrUsed = 0;
	bih.biClrImportant = 0;

	auto *pMasks = (DWORD *)(&bitmapinfo[bih.biSize]);
	pMasks[0] = 0xff0000; // Red
	pMasks[1] = 0x00ff00; // Green
	pMasks[2] = 0x0000ff; // Blue

	::StretchDIBits(hdc,
				  x,
				  y + surface->height,
				  surface->width,
				  -surface->height,
				  0,
				  0,
				  surface->width,
				  surface->height,
				  surface->data,
				  (BITMAPINFO *)&bih,
				  DIB_RGB_COLORS,
				  SRCCOPY);
}
*/

struct DWM
{
	enum class BackdropType : int
	{
		Default = 0,
		None = 1,
		Mica = 2,
		Acrylic = 3,
		Tabbed = 4,
	};

	enum class Corner : int
	{
		Default = 0,
		None = 1,
		Round = 2,
		RoundSmall = 3,
		Last = 4,
	};

	HWND handle{};
	DWM(HWND hWnd = nullptr) : handle(hWnd) {}

	HRESULT RemoveCorner()
	{
		return SetCorner(Corner::None);
	}

	HRESULT SetCorner(Corner value = Corner::Round)
	{
		const auto DWMWA_WINDOW_CORNER_PREFERENCE = 33U;
		return SetAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, value);
	}

	HRESULT SetBorderColor(COLORREF color)
	{
		const auto DWMWA_BORDER_COLOR = 34U;
		return SetAttribute(DWMWA_BORDER_COLOR, color);
	}

	/// <summary>
	/// Enable or disable immersive dark mode.
	/// Requires Windows build 19041 or higher.
	/// </summary>
	HRESULT SetImmersiveDarkMode(BOOL state = TRUE)
	{
		const auto DWMWA_IMMERSIVE_DARK_MODE = 20U;
		return SetAttribute(DWMWA_IMMERSIVE_DARK_MODE, state);
	}

	/// <summary>
	/// Set backdrop type on target window
	/// Requires Windows build 22523 or higher.
	/// </summary>
	HRESULT SetBackdropType(BackdropType backdropType)
	{
		const auto DWMWA_SYSTEMBACKDROP_TYPE = 38;
		return SetAttribute(DWMWA_SYSTEMBACKDROP_TYPE, backdropType);
	}

	/// <summary>
	/// Enable or Disable Mica on target window
	/// Supported on Windows builds from 22000 to 22523. It doesn't work on 22523, use <see cref="SetBackdropType(IntPtr, DWM_SYSTEMBACKDROP_TYPE)"/> instead.
	/// </summary>
	HRESULT SetMica(BOOL state = true)
	{
		const auto DWMWA_MICA = 1029U;
		return SetAttribute(DWMWA_MICA, state);
	}

	template<typename T>
	HRESULT SetAttribute(DWORD dwAttribute, T pvAttribute)
	{
		return ::DwmSetWindowAttribute(handle, dwAttribute, (const void *)&pvAttribute, sizeof(T));
	}

	auto ExtendFrameIntoClientArea(MARGINS margins = { -1 })
	{
		return ::DwmExtendFrameIntoClientArea(handle, &margins);
	}
};

inline HMENU GET_HMENU(HWND hWnd) { return SendMSG<HMENU>(hWnd, MN_GETHMENU, 0, 0); }

auto ver = &Windows::Version::Instance();

#pragma endregion

namespace Nilesoft
{
	//std::mutex mtx; // mutex for critical section
	namespace Shell
	{
		//D2D_DC d2d;

		//std::mutex _mutex; // mutex for critical section
		//std::lock_guard<std::mutex> lock(_mutex);

		inline static MenuItemInfo *get_item(uint32_t id, const std::vector<MenuItemInfo *> &list)
		{
			for(auto item : list)
			{
				if(item->wID == id) return item;
			}
			return nullptr;
		}

		inline static MenuItemInfo *get_item(uint32_t id, HMENU hMenu, const std::vector<MenuItemInfo *> &list)
		{
			for(auto item : list)
			{
				if(item->handle == hMenu)
				{
					if(item->wID == id)
					{
						return item;
					}
				}
			}
			return nullptr;
		}

		ContextMenu::ContextMenu(HWND hWnd, HMENU hMenu, Point const &pt)
		{
			//d2d.create_render();
			//d2d.create_res();

			Window window = hWnd;

			hwnd.owner = hWnd;
			Processes[this] = true;

			dpi.val = Theme::GetDpi(pt, hwnd.owner);
			dpi.org = dpi.val;
			//static_cast<int>(std::ceil(640.f * dpi / 96.f))
			_hMenu_original = hMenu;
			_hMenu = ::CreatePopupMenu();

			_theme.dpi = &dpi;
			_context.dpi = &dpi;
			_context.wnd.owner = hwnd.owner;
			_context.hMenu = hMenu;
			_window = hwnd.owner;

			_cache = Initializer::instance->cache;
			if(Initializer::instance->dpi != dpi.val)
				_cache->reload(dpi.val);

			Initializer::instance->dpi = dpi.val;

			_tip.ctx = this;

			if(keyboard.get_keys_state(true))
			{
				// shift key is down "Extended Mode"
				//context.Extended = ::GetAsyncKeyState(VK_SHIFT) < 0;
				//_context.Extended = keyboard.key_shift();
			}

			_context.Keyboard = &keyboard;

			ThreadId = window.get_threadId(&ProcessId);

			GUITHREADINFO gti = { sizeof(GUITHREADINFO) };
			if(::GetGUIThreadInfo(ThreadId, &gti))
			{
				hwnd.active = gti.hwndActive;
				hwnd.focus = gti.hwndFocus;
				_context.wnd.active = hwnd.active;
				_context.wnd.focus = gti.hwndFocus;
			}

			_context.wnd.active = hwnd.active;
			_context.wnd.focus = hwnd.focus;

			Monitor monitor(pt);
			if(monitor.info())
			{
				_rcMonitor = monitor.rcMonitor;
				_context.helper.is_primary_monitor = monitor.is_primary();
			}

			// current language
			languageId = ::GetThreadUILanguage();

			/*
			ctx->is_layoutRTL = flag.has(TPM_LAYOUTRTL) or (::GetWindowLongPtr(hWnd, GWL_EXSTYLE) & WS_EX_LAYOUTRTL) != 0;
			MBF(L"%d", ctx->is_layoutRTL);
				DWORD pdwDefaultLayout = 0;
			GetProcessDefaultLayout(&pdwDefaultLayout) && pdwDefaultLayout == LAYOUT_RTL;
			auto is_middle_east_enabled = ::GetSystemMetrics(SM_MIDEASTENABLED);
			*/
			is_layoutRTL = (window.get_ex_style() & WS_EX_LAYOUTRTL) != 0;
		}

		ContextMenu::~ContextMenu()
		{
			try
			{
				Processes.erase(this);
				//if(_cache)
				//	_cache->GC.clear();
				Uninitialize();
				if(Initializer::instance)
					Initializer::instance->collect_retired_caches();
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
		}

		bool ContextMenu::prepare_new_items(PositionList &posList,
										  const std::vector<NativeMenu *> &list,
										  MenuItemInfo *owner,
										  menu_t *menu, bool moved)
		{
			CaptureTraceScope traceScope(&_studio_capture);
			if(list.empty())
				return false;

			const NativeMenuConstruction::SelectionInput selection{
				&Selected, true, true};

			int _this_index = 0;

			for(auto item : list)
			{
				if(!construction_consume_item())
					break;
				try 
				{
					StudioCaptureTrace trace;
					StudioCaptureEvidence evidence;
					CaptureEvidenceScope evidenceScope(&evidence);
					construction_context()._this = nullptr;
					//std::lock_guard<std::mutex> lock(_mutex);
					//_context.variables.runtime = &item->owner->variables;
					construction_context().variables.local = &item->owner->variables;

					if(item->properties == 0)
					{
						if(item->is_separator())
						{
							auto mii = construction_gc().push(new MenuItemInfo(MIIM_ID | MIIM_FTYPE, MFT_SEPARATOR, -1));
							mii->owner = owner;
							mii->dynamic = true;
							mii->type = NativeMenuType::Separator;
							CaptureTraceSource(mii->trace, item, L"dynamic.separator", true, L"accepted");
							mii->evidence = std::move(evidence);
							posList.Auto.push_back(mii);
						}
						continue;
					}

					auto not_sep = !item->is_separator();

					this_item _this{};
					construction_context()._this = &_this;
					_this.level = (int)parent_level.size();

					_this.type = not_sep ? (item->is_menu() ? 2 : 1) : 0;
					_this.pos = _this_index++;
					CaptureTraceNumber(trace, L"dynamic.index=", _this.pos);

					const bool types_match =
						NativeMenuConstruction::dynamic_types_match(selection, *item);
					CaptureTraceSource(trace, item, L"dynamic.types", types_match,
						types_match ? L"accepted" : L"rejected");
					if(!types_match)
						continue;
					
					/*
					if(Selected.Window.id >= WINDOW_TASKBAR && !Selected.Check(item->fso))
						continue;
					else if(Selected.Window.id <= WINDOW_TASKBAR && !item->fso.all_types)
					{
						if(item->fso.Types[FSO_TASKBAR] != Selected.Types[FSO_TASKBAR])
							continue;
					}*/

					if(item->where)
					{
						const bool where_match = construction_context().eval_bool(item->where);
						CaptureTraceSource(trace, item, L"dynamic.where", where_match,
							where_match ? L"accepted" : L"rejected");
						if(!where_match)
							continue;
					}
					else
						CaptureTraceSource(trace, item, L"dynamic.where", true, L"not defined");

					string value;

					if(!item->is_separator() && !moved)
					{
						if(item->owner != menu->parent)
						{
							const bool moveto_eval = construction_context().Eval(item->moveto, value, true);
							const bool has_moveto = moveto_eval && !value.trim(L'/').empty();
							CaptureTraceSource(trace, item, L"dynamic.moveto", has_moveto,
								has_moveto ? L"moved" : L"not applied");
							if(has_moveto)
							{
								auto mii = construction_gc().push();
								mii->dynamic = true;
								mii->owner_dynamic = item;
								mii->trace = trace;
								if(mii->parse_parent(value))
									construction_moved_dynamics().push_back(mii);
								continue;
							}
						}
					}
					else if(!item->is_separator())
						CaptureTraceSource(trace, item, L"dynamic.moveto", false, L"not evaluated");

					auto visibility = construction_context().parse_visibility(item->visibility);
					const bool visible = visibility != Visibility::Hidden;
					CaptureTraceSource(trace, item, L"dynamic.visibility", visible,
						visible ? (visibility == Visibility::Disabled ? L"disabled" : L"enabled") : L"hidden; rejected");

					if(!visible)
						continue;

					_this.disabled = visibility == Visibility::Disabled;
					_this.vis = static_cast<int>(visibility);

					auto privileges = Privileges::None;
					auto mode = SelectionMode::Single;

					if(owner)
					{
						mode = owner->mode;
						privileges = owner->privileges;
					}

					mode = construction_context().parse_mode(item->mode, mode);

					const bool mode_match =
						NativeMenuConstruction::dynamic_mode_match(selection, mode);
					CaptureTraceSource(trace, item, L"dynamic.mode", mode_match,
						mode_match ? L"accepted" : L"rejected");
					if(!mode_match)
						continue;

					auto position = Position::Auto;
					string indexof;
					int indexof_pos = 0, indexof_def = -1;

					if(item->position)
					{
						Object obj = construction_context().Eval(item->position).move();

						if(obj.is_array(true))
						{
							auto ptr = obj.get_pointer();
							if((uint32_t)ptr[0] == IDENT_INDEXOF)
							{
								int ac = ptr[1];
								indexof = ptr[2].to_string().move();
								indexof_pos = ptr[3];
								if(ac == 3)
									indexof_def = (int)construction_context().parse_pos(ptr[4], Position::Auto);
								position = (Shell::Position)indexof.trim().hash();
							}
						}
						else if(!obj.is_null())
						{
							position = construction_context().parse_pos(obj, Position::Auto);
						}
					}

					//	auto position = item->parse_position(&_context);
					_this.pos = static_cast<int>(position);
					CaptureTraceSourceNumber(trace, item, L"dynamic.position", L"pos=",
						static_cast<int>(position));

					auto push_back = [&](MenuItemInfo *mii)
					{
						mii->position = position;
						mii->dynamic = true;

						switch(position)
						{
							case Position::Top:
								posList.Top.push_back(mii);
								break;
							case Position::Middle:
								posList.Middle.push_back(mii);
								break;
							case Position::Bottom:
								posList.Bottom.push_back(mii);
								break;
							case Position::Auto:
							case Position::None:
								posList.Auto.push_back(mii);
								break;
							default:
								posList.Custom.push_back(mii);
								break;
						}
					};

					if(item->is_separator())
					{
						auto mii = construction_gc().push(new MenuItemInfo(MIIM_ID | MIIM_FTYPE, MFT_SEPARATOR, -1));
						mii->type = NativeMenuType::Separator;
						mii->trace = std::move(trace);
						mii->indexof.val = indexof.move();
						mii->indexof.pos = indexof_pos;
						mii->indexof.def = indexof_def;
						push_back(mii);
					}
					else
					{
						string title;
						bool title_evaluated = false;
						bool title_accepted = false;
						try
						{
							title_evaluated = construction_context().Eval(item->title, title);
							title_accepted = title_evaluated && !title.empty();
							if(!title_accepted)
							{
								/*if(item->is_menu())
									is_container = true;
								else */if(!item->image.defined)
									continue;
							}
						}
						catch(...) 
						{
						}
						if(!title_evaluated)
							CaptureTraceSource(trace, item, L"dynamic.title", false, L"evaluation failed");
						if(title_evaluated)
							CaptureTraceSource(trace, item, L"dynamic.title", title_accepted,
								title_accepted ? L"accepted" : (item->image.defined ? L"empty; image fallback" : L"empty; rejected"));
						if(!title_accepted && !item->image.defined)
							continue;

						_this.title = title;
						_this.length = title.length<uint32_t>();

						const bool find_evaluated = construction_context().Eval(item->find, value, true);
						if(find_evaluated && !value.empty())
						{
							const bool find_match = selection.matches_find(value);
							CaptureTraceSource(trace, item, L"dynamic.find", find_match,
								find_match ? L"accepted" : L"rejected");
							if(!find_match) continue;
						}
						else
							CaptureTraceSource(trace, item, L"dynamic.find", true, L"not defined");

						// `this.id` is an authored, context-scoped identity when `id`
						// resolves to a nonzero value.  Evaluate it once here so the
						// native command id and the expression context observe the same
						// value; the title hash remains available for matching/capture.
						uint32_t authoredId = 0;
						if(item->explicit_id)
						{
							Object explicitValue = construction_context().Eval(item->explicit_id).move();
							authoredId = construction_context().obj2hash(explicitValue, 0);
							CaptureTraceSourceNumber(trace, item, L"dynamic.id", L"id=",
								static_cast<int>(authoredId));
						}
						const auto nativeId = authoredId != 0 ? authoredId : ident.get_id();
						auto mii = construction_gc().push(new MenuItemInfo(MIIM_STRING | MIIM_ID | MIIM_DATA | MIIM_STATE, 0, nativeId));
						mii->trace = std::move(trace);
						mii->evidence = std::move(evidence);

						mii->owner = owner;
						mii->indexof.val = indexof.move();
						mii->indexof.pos = indexof_pos;
						mii->indexof.def = indexof_def;

						mii->set_title(title.move());
						
						mii->id = authoredId != 0 ? authoredId : mii->hash;

						mii->privileges = privileges;

						_this.title_normalize = mii->title.normalize;
						_this.id = mii->id;

						if(item->is_menu() && item->cmd->admin)
							mii->privileges = construction_context().parse_privileges(item->cmd);

						int checked = 0;

						if(item->is_item())
						{
							checked = construction_context().eval_number<int>(item->checked, 0);
							if(_settings.new_items.keys)
								construction_context().Eval(item->keys, mii->keys);
						}

						if(checked)
						{
							_this.checked = 1;
							mii->fState = MFS_CHECKED;
							if(checked == 2)
							{
								mii->fType |= MFT_RADIOCHECK;
								_this.checked = 2;
							}
						}

						mii->separator = (int)construction_context().parse_separator(item->separator);
						mii->column = construction_context().eval_number<int>(item->column, 0);
						mii->type = NativeMenuType::Item;
						mii->handle = menu->handle;
						mii->owner_dynamic = item;
						mii->mode = mode;
						mii->visibility = visibility;

						if(visibility != Visibility::Enabled)
							mii->fState |= MFS_DISABLED;

						if(mii->column > 0)
							mii->fType |= mii->column == 2 ? MFT_MENUBARBREAK : MFT_MENUBREAK;

						bool image_disabled_by_checked = mii->is_checked() && _theme.image.display == 0;

						if(!_cache->dynamic.image.disabled() && _settings.new_items.image && !image_disabled_by_checked)
						{
							bool parent_disabled = false;

							if(owner)
								parent_disabled = owner->image.import == ImageImport::Disabled;

							if(item->image.enabled() && !parent_disabled)
							{
								if(item->image.import == ImageImport::Inherit)
								{
									if(owner && owner->image.hbitmap)
									{
										//already resolved
										mii->image.inherit(&owner->image);
									}
								}
								else try
								{
									//use the resolved image location
									if(item->image.import == ImageImport::Image)
									{
										if(construction_context().Image(item->image.expr, mii, font.icon, false))
										{
											if(mii->image.import == ImageImport::Inherit)
											{
												if(owner)
												{
													if(owner->image.hbitmap)
														mii->image.inherit(&owner->image);
													else
														mii->image = owner->image;
												}
											}
										}
									}
									else if(item->image.import == ImageImport::Command)
									{
										auto cmd = item->cmd;
										if(!(cmd->command.expr || cmd->command.type != COMMAND_INVALID))
											cmd = item->commands.at(1);

										if(cmd)
										{
											string path;

											if(cmd->command.type == COMMAND_PROMPT)
												path = Environment::Variable(def_COMSPEC).move();
											else if(cmd->command.type == COMMAND_SHELL || cmd->command.type == COMMAND_EXPLORER)
												path = def_EXPLORER;
											else if(cmd->command.type == COMMAND_POWERSHELL)
												path = def_POWERSHELL;
											else if(cmd->command.type == COMMAND_PWSH)
											{
												path = def_PWSH;
												//auto len = ::SearchPathW(nullptr, def_PWSH, nullptr, MAX_PATH, path.buffer(MAX_PATH), nullptr);
												//if(len > 0) path.release(len); else path = def_PWSH;
											}
											else
											{
											construction_context().Eval(cmd->command.expr, path);
											}

											path.trim(str_trim);
											
											if(!path.empty())
											{
												Hash hash = path.hash();
												if(!hash.equals({ 0, IDENT_NONE, IDENT_DISABLED }))
												{
													//ask the cache for an existing image or add.
													//this will identify the image in the cache as "used" or "active".
													if(auto n = Path::Extension(path); n.length() == 0)
														path += L".exe";
													mii->image.hbitmap = Image::From(path.c_str(), _theme.image.size);
													mii->image.size = {(long) _theme.image.size ,(long)_theme.image.size };
													mii->image.import = ImageImport::Image;
													mii->image.inherited = false;
												}
											}
										}
									}

									if(construction_context().Image(item->images.select, mii, font.icon, true))
									{
										/*if(mii->image_select.import == ImageImport::Inherit)
										{
											if(owner)
											{
												if(owner->image_select.hbitmap)
												{
													mii->image_select.hbitmap = owner->image.hbitmap;
													mii->image_select.size = owner->image_select.size;
													mii->image_select.inherit = true;
													mii->image.inherit(&menu->owner->image);
												}
												else {
													mii->image = owner->image;
												}
											}
										}*/
									}
								}
								catch(...)
								{
								}
							}
						}

						auto is_container = false;
						if(item->is_menu() && item->expanded)
							is_container = construction_context().Eval(item->expanded).to_bool();

						if(is_container)
							prepare_new_items(posList, item->items, mii, menu);
						else
						{
							if(owner)
								mii->path = (owner->path + L'/' + owner->title.normalize).trim(L'/').move();
						
							//_log.info(mii->path);

							///else
							//	mii->path = mii->owner->path + L'/' + mii->owner->title.text;
							//mii->tip = item->tip;
							construction_context().eval_tip(item->tip, mii->tip.text, mii->tip.type, mii->tip.time);

							if(item->is_menu())
							{
								if(_studio_construction_context)
								{
									mii->fMask |= MIIM_SUBMENU;
									mii->hSubMenu = reinterpret_cast<HMENU>(mii);
									mii->type = NativeMenuType::Menu;
								}
								else
									mii->set_popup_menu();
								//mii->destory = false;
								auto m_sub = &construction_menus()[mii->hSubMenu];
								m_sub->type = m_sub->MFT_DYNAMIC;
								m_sub->destory = true;
								m_sub->id = mii->hash;
								m_sub->hash = mii->hash;
								m_sub->owner = mii;
								m_sub->dynamics = item->items;
								//m_sub->parent = item;
								m_sub->parent = item->owner;

								string path;
								if(!mii->path.empty())
									path = mii->path + L'/';

								path += mii->title.normalize;
								m_sub->path = path.trim(L'/').move();

								//_log.info(L"%s-%s", m_sub->path.c_str(), menu->path.c_str());
							}
							push_back(mii);
						}
					}
				}
				catch(...) 
				{
				}
			}
			return true;
		}

		void ContextMenu::prepare_system_item(menuitem_t *item, MenuItemInfo *mii, menu_t *menu)
		{
			CaptureTraceScope traceScope(&_studio_capture);
			CaptureEvidenceScope evidenceScope(mii ? &mii->evidence : nullptr);
			auto fix_image_size = [](auto v1, auto v2)->long {
				double d = double(v1) / v2;
				return long(d * v2);
			};

			auto image_disabled = !_theme.image.enabled || _settings.modify_items.image == 0;

			auto ev_si = [=](NativeMenu *si, MenuItemInfo *mii, menuitem_t *item)
			{
				CaptureTraceSource(mii->trace, si, L"static.rule", true, L"matched");
				if(item->type == 0 && si->checked)
				{
					if(auto checked = construction_context().eval_number<int>(si->checked, 0); checked > 0)
					{
						CaptureTraceSource(mii->trace, si, L"static.checked", true,
							checked == 2 ? L"radio" : L"checked");
						construction_context()._this->checked = 1;
						mii->fState = MFS_CHECKED;
						if(checked == 2)
						{
							mii->fType |= MFT_RADIOCHECK;
							construction_context()._this->checked = 2;
						}
					}
					else
						CaptureTraceSource(mii->trace, si, L"static.checked", false, L"not applied");
				}

				bool image_disabled_by_checked = mii->is_checked() && _theme.image.display == 0;

				if(image_disabled_by_checked)
					mii->image.import = ImageImport::Disabled;
				else if(!image_disabled && si->images.normal)
					construction_context().Image(si->images.normal, mii, font.icon, false);

				if(mii->image.import == ImageImport::Disabled || image_disabled || image_disabled_by_checked)
					mii->image.destroy();
				else if(menu->owner && mii->image.import == ImageImport::Inherit)
					mii->image.inherit(&menu->owner->image);

				if(_settings.modify_items.separator)
				{
					mii->separator = (int)construction_context().parse_separator(si->separator);
					construction_context()._this->sep = mii->separator;
					if(si->separator)
					CaptureTraceSourceNumber(mii->trace, si, L"static.separator", L"value=",
						mii->separator);
				}

				auto position = Position::Auto;
				string indexof;
				int indexof_pos = 0, indexof_def = -1;

				if(si->position && _settings.modify_items.position)
				{
					Object obj = construction_context().Eval(si->position).move();

					if(obj.is_array(true))
					{
						auto ptr = obj.get_pointer();
						if((uint32_t)ptr[0] == IDENT_INDEXOF)
						{
							int ac = ptr[1];
							indexof = ptr[2].to_string().move();
							indexof_pos = ptr[3];
							if(ac == 3)
								indexof_def = (int)construction_context().parse_pos(ptr[4], Position::Auto);
							position = (Position)indexof.trim().hash();
						}
					}
					else if(!obj.is_null())
					{
						position = construction_context().parse_pos(obj, Position::Auto);
					}
				}
				if(si->position)
					CaptureTraceSourceNumber(mii->trace, si, L"static.position", L"pos=",
						static_cast<int>(position));

				construction_context()._this->pos = static_cast<int>(position);

				mii->indexof.val = indexof.move();
				mii->indexof.pos = indexof_pos;
				mii->indexof.def = indexof_def;

				mii->position = position;

				if(_settings.modify_items.title)
				{
					string new_title;
					if(construction_context().Eval(si->title, new_title) && !new_title.empty())
					{
						mii->set_title(new_title.move());
						CaptureTraceSource(mii->trace, si, L"static.title", true, L"renamed");
					}
					else if(si->title)
						CaptureTraceSource(mii->trace, si, L"static.title", false, L"not applied");
				}

				if(mii->is_item() && _settings.modify_items.keys)
				{
					construction_context().Eval(si->keys, mii->keys, true);
					if(si->keys)
						CaptureTraceSource(mii->trace, si, L"static.keys", true, L"applied");
				}

				//mii->tip = item->tip;
				construction_context().eval_tip(si->tip, mii->tip.text, mii->tip.type, mii->tip.time);
			};


			mii->dwItemData = item->dwItemData;
			mii->handle = menu->handle;
			mii->trace = item->trace;
			mii->evidence = item->evidence;

			this_item _this; construction_context()._this = &_this;

			_this.type = item->type;
			_this.pos = (int)item->position;
			_this.checked = item->checked ? (item->radio_check ? 2 : 1) : 0;
			_this.disabled = item->disabled;

			_this.system = true;
			_this.id = mii->id;
			_this.parent = menu->id;
			_this.level = (int)parent_level.size();

			mii->position = item->position;
			mii->visibility = item->visibility;

			if(item->is_separator())
			{
				mii->fType |= MFT_SEPARATOR;
				mii->wID = (uint32_t)-1;
				//mii->fix_separator_id();
			}
			else
			{
				mii->fMask |= MIIM_STATE;

				mii->wID = item->wid;

				if(mii->wID == 0 || mii->wID == (uint32_t)-1)
					mii->wID = ident.get_id();

				mii->id = item->uid();
				mii->hash = item->hash;

				if(item->disabled)
					mii->fState |= MFS_DISABLED;

				if(item->checked)
					mii->fState |= MFS_CHECKED;

				if(item->radio_check)
					mii->fType |= MFT_RADIOCHECK;

				bool is_auto_img = false;
				bool image_disabled_by_checked = item->checked && _theme.image.display == 0;

				if(!item->title.empty())
				{
					mii->fMask |= MIIM_STRING;

					mii->set_title(item->title);
					mii->length = item->length;
					mii->title.normalize = item->name;
					mii->keys = item->keys;

					if(!mii->ui)
						mii->ui = Initializer::get_muid(mii->hash);

					if(mii->ui)
					{
						mii->is_system = true;
						mii->id = mii->ui->id;
						mii->image.expr = mii->ui->image;
					}
					else
					{
						mii->id = mii->hash;
						mii->image.expr = _cache->get_image(mii->id);
					}

					_this.id = mii->id;
					_this.length = mii->length;
					_this.title = mii->title.text;
					_this.title_normalize = mii->title.normalize;
				}

				if(_settings.modify_items.image == 2 && !image_disabled_by_checked)
				{
					if(menu->is_main or item->is_toplevel or menu->id == IDENT_ID_VIEW)
					{
						if(Selected.Window.id != WINDOW_SYSMENU)
						{
							if(mii->image.expr or mii->ui)
							{
								is_auto_img = mii->image.expr;
								if(!is_auto_img && mii->ui)
									is_auto_img = bool(mii->ui->image_glyph[0] + mii->ui->image_glyph[1]);

								if(is_auto_img)
									construction_context().Image(nullptr, mii, font.icon, false);
								else
									mii->ui = nullptr;
							}
						}
					}
				}
				else
				{
					mii->ui = nullptr;
				}

				// titlebar menu
				if(menu->is_main && Selected.Window.id == WINDOW_SYSMENU)
				{
					bool ok = false;
					auto img = &mii->image.draw;
					if(mii->id == IDENT_ID_RESTORE)
					{
						ok = true;
						img->glyph.code[0] = L'\uE297';
						img->glyph.code[1] = 0;
					}
					else if(mii->id == IDENT_ID_CLOSE)
					{
						ok = true;
						img->glyph.code[0] = L'\uE29a';
						img->glyph.code[1] = 0;
					}
					else if(mii->id == IDENT_ID_MINIMIZE)
					{
						ok = true;
						img->glyph.code[0] = L'\uE298';
						img->glyph.code[1] = 0;
					}
					else if(mii->id == IDENT_ID_MAXIMIZE)
					{
						ok = true;
						img->glyph.code[0] = L'\uE299';
						img->glyph.code[1] = 0;
					}

					if(ok)
					{
						mii->image.import = ImageImport::Draw;
						img->type = img->DT_GLYPH;
						img->glyph.font = font.icon10;
						img->glyph.size = { (long)_theme.image.size, (long)_theme.image.size };
						img->glyph.color[0] = _theme.image.color[0];
						img->glyph.color[1] = _theme.image.color[1];
					}
				}

				if(item->image)
				{
					if(image_disabled || image_disabled_by_checked)
					{
						mii->image.destroy();
					}
					else if(!is_auto_img)
					{
						BITMAP bitmap{};
						if(::GetObjectW(item->image, sizeof(BITMAP), &bitmap) == sizeof(BITMAP))
						{
							SIZE size = { bitmap.bmWidth, bitmap.bmHeight };
							if(_theme.image.scale)
							{
								size = { static_cast<long>(_theme.image.size), static_cast<long>(_theme.image.size) };

								if(bitmap.bmWidth != size.cx or bitmap.bmHeight != size.cy)
								{
									auto def_size = size;

									size.cx = dpi.value<long>(bitmap.bmWidth);
									size.cy = dpi.value<long>(bitmap.bmHeight);

									if(size.cx > def_size.cx or size.cy > def_size.cy)
									{
										size.cx = fix_image_size(def_size.cx, size.cx);
										size.cy = fix_image_size(def_size.cy, size.cy);
									}
								}
							}

							uint8_t *bits{};
							if(auto hbitmap = WIC::ToBitmap32(item->image, size, &bits); hbitmap)
							{
								mii->image.hbitmap = hbitmap;
								mii->image.size = size;

								if(!bitmap.bmBits && bits)
								{
									auto p = (RGBA *)bits;
									if(p->a == 0 and (p->r > 0 || p->g > 0 || p->b > 0))
									{
										for(int i0 = 0; i0 < size.cx * size.cy; i0++)
										{
											if(p->a == 0) p->a = 255;
											p++;
										}
									}
								}
							}
						}
					}
				}

				if(item->is_menu())
				{
					mii->type = NativeMenuType::Menu;
					mii->fMask |= MIIM_SUBMENU;
					mii->hSubMenu = _studio_construction_context
						? reinterpret_cast<HMENU>(mii) : CreatePopupMenu();
					mii->sys_items = &item->items;

					auto m_sub = &construction_menus()[mii->hSubMenu];
					m_sub->handle = mii->hSubMenu;
					m_sub->destory = true;
					
					m_sub->id = mii->id;
					m_sub->hash = mii->hash;

					if(mii->ui)
						m_sub->id = mii->ui->id;
					
					m_sub->owner = mii;
					m_sub->std_items = &item->items;

					string path;
					if(!mii->path.empty())
						path = mii->path + L'/';

					path += mii->title.normalize;
					m_sub->path = path.trim(L'/').move();
				}
			}

			for(auto si : item->native_items)
			{
				if(si->has_clsid)
					continue;
				// The last matching static rule is the effective source for the
				// serialized property evidence.  Keep the pointer on the row while
				// retaining the full ordered trace above.
				mii->owner_static = si;
				ev_si(si, mii, item);
			}
		}

		bool ContextMenu::prepare_system_items(PositionList &list, menu_t *menu)
		{
			if(!menu || !menu->std_items)
				return false;

			//int _index = 0;
			for(auto item : *menu->std_items)
			{
				if(!construction_consume_item())
					break;
				construction_context()._this = nullptr;
				auto mii = construction_gc().push(new MenuItemInfo(MIIM_ID | MIIM_DATA | MIIM_FTYPE, 0, 0));
				prepare_system_item(item, mii, menu);
				list.push(mii);
			}

			return true;
		}

		bool ContextMenu::prepare_system_items2(PositionList &list, menu_t *menu)
		{
			if(!menu)
				return false;

			for(auto item : __movable_system_items)
			{
				if(!construction_consume_item())
					break;
				if(item->path.equals(menu->path))
				{
					auto mii = construction_gc().push(new MenuItemInfo(MIIM_ID | MIIM_DATA | MIIM_FTYPE, 0, 0));
					prepare_system_item(item, mii, menu);
					list.push(mii);
				}
			}

			return true;
		}

		bool ContextMenu::construct_popup_entries(menu_t *menu,
			std::vector<MenuItemInfo *> &items, bool capture)
		{
			CaptureTraceScope traceScope(capture ? &_studio_capture : nullptr);
			if(!menu)
				return false;

			items.clear();
			items.reserve(100);
			menu->draw.checks = 0;
			menu->draw.images = 0;
			menu->draw.popups = 0;

			// Dynamic and static sources are already owned by the active cache.  A
			// capture menu_t is a request-local value, so assigning these pointers
			// does not mutate the live menu map or create a native popup handle.
			MenuItemInfo *owner = menu->is_main ? nullptr : menu->owner;
			if(menu->is_main)
			{
				if(!_cache || !__system_menu_tree)
					return false;
				menu->dynamics = _cache->dynamic.items;
				menu->std_items = &__system_menu_tree->items;
			}

			PositionList systemItems;
			PositionList newItems;
			if(!prepare_system_items(systemItems, menu))
				return false;

			std::vector<MenuItemInfo *> bottomItems;
			if(_settings.new_items.enabled)
			{
				if(!(owner && owner->is_disabled()))
				{
					prepare_new_items(newItems, menu->dynamics, owner, menu);

					// Reinsert dynamic definitions that were authored for this
					// exact popup path.  The request-local moved vector is used by
					// automatic capture; ordinary construction keeps the existing
					// live vector.
					std::vector<NativeMenu *> movedDefinitions;
					for(auto moved : construction_moved_dynamics())
					{
						if(!moved || moved->parent.empty())
							continue;
						if(moved->owner_dynamic && moved->is_parent(parent_level))
							movedDefinitions.push_back(moved->owner_dynamic);
					}
					if(!movedDefinitions.empty())
						prepare_new_items(newItems, movedDefinitions, owner, menu, true);
				}

				prepare_system_items2(newItems, menu);
				items.insert(items.end(), newItems.Top.begin(), newItems.Top.end());
			}

			if(systemItems.size() > 0)
			{
				items.insert(items.end(), systemItems.Top.begin(), systemItems.Top.end());
				items.insert(items.end(), systemItems.Auto.begin(), systemItems.Auto.end());
				bottomItems.insert(bottomItems.end(), systemItems.Bottom.begin(),
					systemItems.Bottom.end());
			}

			if(newItems.size() > 0)
			{
				size_t dynamicPosition = items.size();
				if(menu->is_main && Selected.Window.id > WINDOW_UI)
				{
					const auto findPosition = [&items]() -> size_t
					{
						const auto count = static_cast<int>(items.size());
						int position = count / 2;
						int separator = -1;
						if(position > count)
							position = count;
						for(int index = position; index >= 0; --index)
						{
							if(items[static_cast<size_t>(index)]->is_separator())
							{
								separator = index;
								break;
							}
						}
						if(separator < 0)
						{
							for(int index = position; index < count; ++index)
							{
								if(items[static_cast<size_t>(index)]->is_separator())
								{
									separator = index;
									break;
								}
							}
						}
						if(separator >= 0)
							position = separator;
						return static_cast<size_t>(position);
					};

					const auto position = construction_context().parse_position(
						_cache->dynamic.position);
					switch(position)
					{
					case Position::Auto:
						if(Selected.Window.id <= WINDOW_EDIT)
							dynamicPosition = items.size();
						else
						{
							const bool recycleBin = !Selected.Background &&
								Selected.Window.id == WINDOW_RECYCLEBIN;
							if(!Selected.Types[FSO_TASKBAR] && !recycleBin)
								dynamicPosition = items.empty() ? 1 : findPosition();
						}
						break;
					case Position::Top:
						dynamicPosition = 0;
						break;
					case Position::Middle:
						dynamicPosition = findPosition();
						break;
					case Position::Bottom:
						dynamicPosition = items.size();
						break;
					default:
						dynamicPosition = static_cast<size_t>(position);
						break;
					}
				}
				if(dynamicPosition > items.size())
					dynamicPosition = items.size();
				items.insert(items.begin() + static_cast<ptrdiff_t>(dynamicPosition),
					newItems.Auto.begin(), newItems.Auto.end());
				bottomItems.insert(bottomItems.end(), newItems.Bottom.begin(),
					newItems.Bottom.end());
			}

			items.insert(items.end(), bottomItems.begin(), bottomItems.end());
			auto middle = items.size() / 2;
			if(systemItems.size() > 0)
				items.insert(items.begin() + static_cast<ptrdiff_t>(middle),
					systemItems.Middle.begin(), systemItems.Middle.end());
			if(newItems.size() > 0)
			{
				middle = items.size() / 2;
				items.insert(items.begin() + static_cast<ptrdiff_t>(middle),
					newItems.Middle.begin(), newItems.Middle.end());
			}

			auto addCustomItems = [&items](std::vector<MenuItemInfo *> &customItems)
			{
				for(auto item : customItems)
				{
					if(!item)
						continue;
					if(item->indexof.val.empty())
					{
						const auto position = static_cast<int>(item->position);
						if(position < 0 || position > static_cast<int>(items.size()))
							items.push_back(item);
						else
							items.insert(items.begin() + position, item);
					}
					else
					{
						int index = 0;
						bool found = false;
						FindPattern pattern;
						if(pattern.split(item->indexof.val, L'|'))
						{
							for(const auto &match : pattern.matches)
							{
								index = 0;
								for(auto existing : items)
								{
									if(pattern.find(match, &existing->title.normalize))
									{
										found = true;
										const auto insertAt = std::clamp(index + item->indexof.pos,
											0, static_cast<int>(items.size()));
										items.insert(items.begin() + insertAt, item);
										break;
									}
									++index;
								}
								if(found)
									break;
							}
						}
						if(!found)
						{
							if(item->indexof.def == -2)
								items.insert(items.begin(), item);
							else if(item->indexof.def == -3)
								items.insert(items.begin() + items.size() / 2, item);
							else
								items.push_back(item);
						}
					}
				}
			};

			addCustomItems(newItems.Custom);
			addCustomItems(systemItems.Custom);

			// In capture mode synthetic submenu handles cannot be queried with the
			// Win32 menu APIs.  Use the retained source vectors to decide whether a
			// popup is genuinely empty; an unopened branch remains observable.
			for(auto iterator = items.begin(); iterator != items.end(); ++iterator)
			{
				auto item = *iterator;
				if(!item)
					continue;
				bool emptyPopup = false;
				if(item->is_popup())
				{
					if(capture)
					{
						bool hasSource = !item->items.empty();
						if(auto found = construction_menus().find(item->hSubMenu);
							found != construction_menus().end())
						{
							hasSource = hasSource ||
								(found->second.std_items && !found->second.std_items->empty()) ||
								!found->second.dynamics.empty();
						}
						emptyPopup = !hasSource;
					}
					else
						emptyPopup = MENU::get_count(item->hSubMenu) == 0;
				}
				if(emptyPopup)
				{
					int retained = 0;
					for(auto movable : __movable_system_items)
						retained += movable && movable->path.equals(
							(item->path + L"/" + item->title.normalize).trim(L'/'));
					for(auto moved : construction_moved_dynamics())
						retained += moved && !moved->parent.empty() &&
							item->id == moved->parent.front();
					if(retained == 0 &&
						((item->dynamic && item->owner_dynamic &&
							item->owner_dynamic->items.empty()) ||
						(item->sys_items && item->sys_items->empty())))
					{
						iterator = items.erase(iterator);
						if(iterator == items.end())
							break;
						continue;
					}
				}
				if(!item->is_separator())
				{
					if(item->is_checked() && _theme.image.display == 0)
						item->image.destroy();
					menu->draw.checks += item->is_checked();
					menu->draw.images += item->has_image_or_draw();
					menu->draw.popups += item->is_popup();
				}
			}

			while(!items.empty() && items.back()->is_separator())
				items.pop_back();
			if(!items.empty() && items.back()->is_sep_after())
				items.back()->separator &= ~(int)Separator::Bottom;

			if(capture)
			{
				for(auto item : items)
				{
					if(!item)
						continue;
					if(item->is_popup())
					{
						item->studio_completeness.state = "observed";
						item->studio_completeness.childrenCaptured = false;
						item->studio_completeness.complete = false;
					}
					else
					{
						item->studio_completeness.state = "materialized";
						item->studio_completeness.childrenCaptured = true;
						item->studio_completeness.complete = true;
					}
				}
			}
			return true;
		}

		bool ContextMenu::capture_unopened_submenus(menu_t *root)
		{
			const auto epoch = _studio_capture.ActiveEpoch();
			if(!epoch || !_cache || !__system_menu_tree)
				return false;
			if(_studio_automatic_capture_published_epoch == epoch)
				return true;
			menu_t automaticRoot;
			if(!root)
			{
				automaticRoot.handle = _hMenu;
				automaticRoot.is_main = true;
				automaticRoot.type = menu_t::MFT_SYSTEM;
				automaticRoot.std_items = &__system_menu_tree->items;
				automaticRoot.dynamics = _cache->dynamic.items;
				root = &automaticRoot;
			}

			PreviewPolicy policy;
			policy.maxSteps = 50000;
			policy.maxDepth = kMaxAutomaticCaptureDepth;
			policy.deadline = ::GetTickCount64() + kAutomaticCaptureBudgetMs;
			policy.allowAssignments = false;
			policy.dispatch = CaptureFunctionDispatch;

			Context captureContext = _context;
			captureContext.Runtime = false;
			captureContext.Preview = &policy;
			captureContext._this = nullptr;

			GC<MenuItemInfo> captureGc;
			std::unordered_map<HMENU, menu_t> captureMenus;
			std::vector<MenuItemInfo *> movedDynamics;
			size_t remainingItems = kMaxAutomaticCaptureItems;
			bool budgetExhausted = false;

			const auto previousContext = _studio_construction_context;
			const auto previousGc = _studio_construction_gc;
			const auto previousMenus = _studio_construction_menus;
			const auto previousMoved = _studio_construction_moved_dynamics;
			const auto previousBudget = _studio_construction_item_budget;
			const auto previousBudgetFlag = _studio_construction_budget_exhausted;
			const auto previousParentLevel = parent_level;
			struct ConstructionRestore final
			{
				ContextMenu *owner{};
				Context *context{};
				GC<MenuItemInfo> *gc{};
				std::unordered_map<HMENU, menu_t> *menus{};
				std::vector<MenuItemInfo *> *moved{};
				size_t *budget{};
				bool *budgetFlag{};
				std::vector<uint32_t> parent;
				~ConstructionRestore() noexcept
				{
					if(!owner)
						return;
					owner->_studio_construction_context = context;
					owner->_studio_construction_gc = gc;
					owner->_studio_construction_menus = menus;
					owner->_studio_construction_moved_dynamics = moved;
					owner->_studio_construction_item_budget = budget;
					owner->_studio_construction_budget_exhausted = budgetFlag;
					owner->parent_level = std::move(parent);
				}
			} restore{this, previousContext, previousGc, previousMenus, previousMoved,
				previousBudget, previousBudgetFlag, previousParentLevel};

			_studio_construction_context = &captureContext;
			_studio_construction_gc = &captureGc;
			_studio_construction_menus = &captureMenus;
			_studio_construction_moved_dynamics = &movedDynamics;
			_studio_construction_item_budget = &remainingItems;
			_studio_construction_budget_exhausted = &budgetExhausted;
			parent_level.clear();

			menu_t captureRoot = *root;
			captureRoot.handle = _hMenu ? _hMenu : reinterpret_cast<HMENU>(&captureRoot);
			captureRoot.is_main = true;
			captureRoot.owner = nullptr;
			captureRoot.std_items = &__system_menu_tree->items;
			captureRoot.dynamics = _cache->dynamic.items;
			captureContext.hMenu = captureRoot.handle;

			const auto addDiagnostic = [](StudioCaptureCompleteness &completeness,
				std::wstring_view diagnostic)
			{
				if(diagnostic.empty() || completeness.diagnostics.size() >= 8)
					return;
				completeness.diagnostics.emplace_back(diagnostic);
			};
			const auto policyDiagnostic = [&]() -> std::wstring
			{
				if(policy.code.empty() && policy.message.empty())
					return L"PREVIEW_UNAVAILABLE: expression evaluation was not permitted.";
				std::wstring result;
				result.assign(policy.code.begin(), policy.code.end());
				if(!result.empty() && !policy.message.empty())
					result += L": ";
				result += policy.message;
				return result;
			};

			std::function<void(std::vector<MenuItemInfo *> &, std::wstring_view)> markUnavailable;
			markUnavailable = [&](std::vector<MenuItemInfo *> &entries,
				std::wstring_view diagnostic)
			{
				for(auto entry : entries)
				{
					if(!entry || !entry->is_popup())
						continue;
					if(!entry->studio_completeness.childrenCaptured)
					{
						entry->studio_completeness.state = "unavailable";
						entry->studio_completeness.complete = false;
						if(policy.code == "PREVIEW_LIMIT")
							entry->studio_completeness.evaluationLimit =
								static_cast<uint32_t>(policy.maxSteps);
						addDiagnostic(entry->studio_completeness, diagnostic);
					}
					markUnavailable(entry->items, diagnostic);
				}
			};

			std::unordered_set<HMENU> activeMenus;
			const NativeMenuConstruction::AutomaticCaptureCallbacks captureCallbacks{
				.canContinue = [&]()
				{
					if(policy.failed)
						return false;
					if(::GetTickCount64() > policy.deadline)
					{
						policy.Fail("PREVIEW_LIMIT",
							L"Automatic capture exceeded its time limit.");
						return false;
					}
					return true;
				},
				.submenu = [](const MenuItemInfo *entry)
				{
					return entry ? entry->hSubMenu : nullptr;
				},
				.entryId = [](const MenuItemInfo *entry)
				{
					return entry ? entry->id : 0;
				},
				.construct = [&](MenuItemInfo *entry, size_t,
					const std::vector<uint32_t> &childAncestors,
					std::vector<MenuItemInfo *> &childEntries)
				{
					if(!entry)
						return false;
					const auto handle = entry->hSubMenu;
					menu_t child;
					if(auto found = captureMenus.find(handle); found != captureMenus.end())
						child = found->second;
					else
					{
						child.handle = handle;
						child.owner = entry;
						child.type = entry->dynamic ? menu_t::MFT_DYNAMIC : menu_t::MFT_SYSTEM;
						child.std_items = entry->sys_items;
						if(entry->owner_dynamic)
							child.dynamics = entry->owner_dynamic->items;
						if(!child.std_items && child.dynamics.empty())
						{
							entry->studio_completeness.state = "unavailable";
							entry->studio_completeness.complete = false;
							addDiagnostic(entry->studio_completeness,
								L"CAPTURE_SOURCE_UNAVAILABLE: submenu source was not retained.");
							return false;
						}
					}
					child.handle = handle;
					child.owner = entry;
					child.is_main = false;
					if(child.path.empty())
					{
						string childPath = entry->path;
						if(!childPath.empty())
							childPath += L'/';
						childPath += entry->title.normalize;
						child.path = childPath.trim(L'/').move();
					}

					const auto savedParent = parent_level;
					parent_level = childAncestors;
					bool constructed = false;
					try
					{
						constructed = construct_popup_entries(&child, childEntries, true);
					}
					catch(...)
					{
						parent_level = savedParent;
						throw;
					}
					parent_level = savedParent;
					return constructed;
				},
				.commit = [&](MenuItemInfo *entry,
					std::vector<MenuItemInfo *> &&childEntries, bool constructed)
				{
					if(!entry)
						return;
					entry->items = std::move(childEntries);
					entry->studio_completeness.childrenCaptured = constructed;
					entry->studio_children_captured = constructed;
					entry->studio_completeness.state = constructed ? "materialized" : "unavailable";
					entry->studio_completeness.complete = constructed && !budgetExhausted &&
						!policy.failed;
					if(budgetExhausted)
					{
						entry->studio_completeness.itemLimit =
							static_cast<uint32_t>(kMaxAutomaticCaptureItems);
						addDiagnostic(entry->studio_completeness,
							L"CAPTURE_ITEM_LIMIT: automatic capture reached its item limit.");
					}
					if(policy.failed)
						addDiagnostic(entry->studio_completeness, policyDiagnostic());
				},
				.markUnavailable = markUnavailable,
				.markIncomplete = [&](MenuItemInfo *entry, std::wstring_view diagnostic)
				{
					if(!entry)
						return;
					entry->studio_completeness.state = "unavailable";
					entry->studio_completeness.complete = false;
					if(diagnostic.find(L"CAPTURE_DEPTH_LIMIT") != std::wstring_view::npos)
						entry->studio_completeness.depthLimit =
							static_cast<uint32_t>(kMaxAutomaticCaptureDepth);
					addDiagnostic(entry->studio_completeness, diagnostic);
				}
			};

			std::vector<MenuItemInfo *> captured;
			const bool constructed = construct_popup_entries(&captureRoot, captured, true);
			if(!constructed)
				return false;
			NativeMenuConstruction::MaterializeAutomaticPopups(captured, 0, {},
				kMaxAutomaticCaptureDepth, activeMenus, captureCallbacks);
			if(budgetExhausted)
				markUnavailable(captured,
					L"CAPTURE_ITEM_LIMIT: automatic capture reached its item limit.");
			if(policy.failed)
				markUnavailable(captured, policyDiagnostic());

			const auto metadata = capture_metadata();
			if(!_studio_capture.PublishFinal(captured, metadata, {}))
				return false;
			_studio_automatic_capture_published_epoch = epoch;
			return true;
		}

		int level = 0;
		//finalize
		LRESULT ContextMenu::OnInitMenuPopup(HMENU hMenu, [[maybe_unused]] uint32_t uPosition)
		{
			CaptureTraceScope traceScope(&_studio_capture);
			__trace(L"ContextMenu.InitMenuPopup begin");
			publish_original_capture_if_armed();

			MENU m = hMenu;
			LRESULT ret = msg.invoke();

			auto menu = &_menus[hMenu];
			menu->handle = hMenu;
			menu->is_main = _hMenu == hMenu;
			current.hMenu = hMenu;
			current.menu = menu;

			if(menu->is_main)
			{
				level++;
			}
			else if(menu->id != 0)
			{
				level++;
				parent_level.push_back(menu->id);
			}
			/*else if(menu->id == 0 && !menu->is_dynamic())
			{
				return 0;
			}*/

			if(menu->is_main)
			{
				//__system_menuitems
				//if(!load_from_dllgetclassobject)
				//	_settings.dynamics.enabled = false;
			}

		//	_log.info(L"init %d %x", parent_level.size(), menu->id);

			std::vector<MenuItemInfo *> items;
			if(!construct_popup_entries(menu, items, false))
				return ret;


			DC dc = hwnd.owner;
			dc.set_font(font.handle);

			//menu->draw = {};
			menu->draw.width = 0;
			menu->draw.height = 0;
			menu->draw.length = 0;

			long max_text_len = 0;
			int col = 0;

			// insert items to native menu
			int i = 0, x = 0;
			for(auto item : items)
			{
				item->remove_bitmap();
				item->add_ownerdraw();
				item->handle = hMenu;
				item->size = {};

				/*if(!item->is_separator() && item->title.empty() && menu->is_main)
				{
					if(!item->has_image_or_draw())
						continue;
				}*/
				if(!item->is_separator())
				{
					//item->wID += 100;
				}

				if(auto res = m.insert(item, i, true, x++); res)
				{
					if(item->is_separator())
					{
						menu->popup_height += item->size.cy +
							_theme.separator.margin.top + _theme.separator.margin.bottom +
							_theme.separator.size;
					}
					else
					{
						if(item->is_column() > 0)
						{
							col++;
							menu->has_col = true;
						}

						if(item->cch > 0)
						{
							Rect rc;

							if(item->column == 1)
								menu->draw.width = 0;

							dc.measure(item->title, item->title.length(), &rc, DT_NOCLIP | _theme.text.prefix);

							if(!item->keys.empty())
							{
								Rect rc_keys;
								dc.measure(item->keys, item->keys.length<uint32_t>(), &rc_keys, DT_NOCLIP | _theme.text.prefix);
								rc.right += rc_keys.width() + _theme.text.tap;
							}

							if(rc.right > max_text_len)
								max_text_len = rc.right;
							else
								rc.right = max_text_len;

							menu->draw.length = std::max<uint32_t>(rc.right, menu->draw.length);
							menu->draw.width = rc.right;

							if(menu->draw.checks || menu->draw.images)
							{
								// image siz
								item->size.cx += _theme.image.size;
								// padding left for text
								item->size.cx += _theme.image.gap;

								if(_theme.image.display == 2)
								{
									if(menu->draw.checks && menu->draw.images)
									{
										// image siz
										item->size.cx += _theme.image.size;
										// padding left for text
										item->size.cx += _theme.image.gap;
									}
								}
							}

							if(menu->draw.popups)
								item->size.cx += _theme.image.gap + symbol.chevron.size.cx;
							
							item->size.cx += rc.right;
							item->size.cy = rc.bottom;

							if(menu->draw.height < rc.bottom)
								menu->draw.height = rc.bottom;
						}

						menu->popup_height += item->size.cy +
							_theme.back.padding.top + _theme.back.padding.bottom + 
							_theme.back.margin.top + _theme.back.margin.bottom;

						_items.push_back(item);
					}

					if(item->dynamic && item->is_popup_or_item())
					{
						if(item->is_popup())
							_items_popup.push_back(item);
						else
							_items_command.push_back(item);
					}

					if(menu->is_main && item->is_popup())
						_main_popup.push_back(item);

					i += res;
				}
				else if(!item->is_separator())
				{
					//_log.warning(L"Not inserted %s", item->title.c_str());
				}
			}

			dc.reset_font();

			// The handshake may complete while this popup is being prepared.  A
			// second gate check closes that bounded race even if the posted wake-up
			// message is not dispatched until after the menu returns.
			publish_original_capture_if_armed();

			long image__size = _theme.image.size;
			if(menu->draw.height < image__size)
				menu->draw.height = image__size;

			if(menu->draw.height % 2)
				menu->draw.height++;

			menu->popup_height += _theme.border.padding.top + _theme.border.padding.bottom + _theme.border.size + _theme.border.size;
			

			MENUINFO mi = { sizeof(mi), MIM_STYLE | MIM_BACKGROUND | MIM_MAXHEIGHT };
			if(m.get(&mi))
			{
				if(mi.hbrBack)
				{
					if(hMenu == _hMenu)
						mi.hbrBack = _hbackground0;
					else
						::DeleteObject(mi.hbrBack);
				}

				Flag<uint32_t> style(mi.dwStyle);
				style.add(MNS_CHECKORBMP);
				style.add(MNS_NOCHECK);

				menu->popup_height += 100;
				auto h = _rcMonitor.height();
				if(menu->popup_height >= h)
				{
					menu->popup_height = 0;
					if(menu->is_main)
					{
						//mi.cyMax = h - 100;
						//style.add(MIM_MAXHEIGHT);
						//menu->popup_height = mi.cyMax;
					}
				}
				else {
					menu->popup_height = 0;
				}

				mi.hbrBack = composition ? GetStockBrush(BLACK_BRUSH) : ::CreateSolidBrush(_theme.background.color.to_BGR());
				m.set(&mi);
			}

			// Retain this exact display order until the deferred capture publishes
			// the visible popup pixels and row rectangles.  This is also needed
			// when the Studio handshake arrives after initialization.
			try
			{
				// Menu insertion adds before/after separators and suppresses duplicate
				// separators. Retain the actual HMENU order, not the input vector.
				std::vector<MenuItemInfo *> displayed;
				const auto count = ::GetMenuItemCount(hMenu);
				if(count < 0 || static_cast<size_t>(count) > kMaxAppearanceRows)
					throw std::runtime_error("Invalid displayed menu size");
				displayed.reserve(static_cast<size_t>(count));
				std::unordered_set<MenuItemInfo *> used;
				for(int position = 0; position < count; ++position)
				{
					MENUITEMINFOW info{sizeof(info)};
					info.fMask = MIIM_ID | MIIM_FTYPE;
					if(!::GetMenuItemInfoW(hMenu, position, TRUE, &info))
						throw std::runtime_error("Unavailable displayed menu row");
					MenuItemInfo *entry = nullptr;
					for(auto candidate : items)
					{
						if(candidate->wID == info.wID &&
							candidate->is_separator() == ((info.fType & MFT_SEPARATOR) != 0) &&
							used.find(candidate) == used.end())
						{
							entry = candidate;
							used.insert(candidate);
							break;
						}
					}
					if(!entry && (info.fType & MFT_SEPARATOR))
						entry = _gc.push(new MenuItemInfo(MIIM_ID | MIIM_FTYPE,
							MFT_SEPARATOR, info.wID));
					if(!entry)
						throw std::runtime_error("Unmapped displayed menu row");
					displayed.push_back(entry);
				}
				_studio_final_entries[hMenu] = std::move(displayed);
				clear_appearance_cache(hMenu);
			}
			catch(...)
			{
				_studio_final_entries.erase(hMenu);
				_studio_capture.Fail("CAPTURE_APPEARANCE_MEMORY",
					"The native capture could not retain the displayed popup entries.");
			}

			// The final vector is the exact set of entries about to be displayed
			// for this popup.  StudioCapture serializes it before this stack frame
			// returns; it never receives HMENU handles or native pointers.
			if(_studio_capture.IsActive())
			{
				try
				{
					const auto metadata = capture_metadata();
					_studio_capture.PublishFinal(_studio_final_entries.at(hMenu), metadata,
						menu->path.empty() ? std::wstring{} :
						std::wstring(menu->path.c_str(), menu->path.length()));
				}
				catch(...)
				{
					_studio_capture.Fail("CAPTURE_METADATA",
						"The native capture could not collect menu context metadata.");
				}
			}

			__trace(L"ContextMenu.InitMenuPopup end");

			return ret;
		}

		//if(CompareString(LOCALE_INVARIANT, NORM_IGNORECASE, FRIENDLY_NAME_DOCUMENTS, -1, friendlyPath.c_str(), -1)
		LRESULT ContextMenu::OnUninitMenuPopup(HMENU hMenu)
		{
			auto menu = &_menus[hMenu];
			current.hMenu = hMenu;

			if(level > 0) level--;

			if(!parent_level.empty())
				parent_level.pop_back();

			_tip.hide();

			/*
			if(!menu->is_dynamic())
				menu->dynamics.clear();
			*/

			auto ret = msg.invoke();
			::DestroyMenu(hMenu);
			_studio_final_entries.erase(hMenu);
			clear_appearance_cache(hMenu);
			
			__trace(L"ContextMenu.UninitMenuPopup");
			
			current.hMenu = nullptr;
			menu->wnd = nullptr;
			return ret;
		}

		LRESULT ContextMenu::OnInitMenu([[maybe_unused]] HMENU hMenu)
		{
			//_hMenu = hMenu;
			//_context.hMenu = hMenu;

			_tip.create();
			//m_log->info(L"WM_INITMENU");
			//m_INITMENU = hMenu;
			return msg.invoke();
		}

		//VirtualFolder
		LRESULT ContextMenu::OnMenuSelect([[maybe_unused]] HMENU hMenu, [[maybe_unused]] uint32_t itemId, [[maybe_unused]] uint32_t flags)
		{
			//log->info(L"OnMenuSelect");
			current.hMenu = hMenu;
			_tip.hide();
			return msg.invoke();
		}

		//
		void ContextMenu::draw_string(HDC hdc, HFONT hFont, const Rect *rc, const Color &color, const wchar_t *text, int length, DWORD format, bool disable_BufferedPaint)
		{
			if(color.a == 0)
				return;

			BufferedPaint bp(hdc, rc);
			HDC hdcPaint = disable_BufferedPaint ? nullptr : bp.begin(color.a);

			if(!hdcPaint)
				hdcPaint = hdc;
			if(hdcPaint)
			{
				bp.clear();
				//::SetTextColor(hdcPaint, color);
				//::SetBkMode(hdcPaint, TRANSPARENT);
				auto hFontOld = ::SelectObject(hdcPaint, hFont);
				DTTOPTS dttOpts = { sizeof(DTTOPTS),  DTT_COMPOSITED | DTT_TEXTCOLOR, color.to_BGR() };
				//::SetTextAlign(hdcPaint, TA_BASELINE | TA_UPDATECP);
				::DrawThemeTextEx(_hTheme, hdcPaint, 0, 0, text, length, format, const_cast<Rect *>(rc), &dttOpts);
				::SelectObject(hdcPaint, hFontOld);
			}
		}

		void ContextMenu::draw_scroll_arrows(HDC hdc, long width, long height)
		{
			const auto inset = dpi(14);
			const auto format = DT_NOCLIP | DT_SINGLELINE | DT_VCENTER | DT_CENTER;
			Rect top{0, 0, width, inset};
			Rect bottom{0, height - inset, width, height};
			draw_string(hdc, font.icon, &top, _theme.symbols.chevron.nor, L"\uE009", 1, format);
			draw_string(hdc, font.icon, &bottom, _theme.symbols.chevron.nor, L"\uE00A", 1, format);
		}

		void ContextMenu::draw_rect(DC *dc, const POINT &pt, const SIZE &size, const Color &color, const Color &border, int radius)
		{
			PlutoVG pluto(size.cx, size.cy);

			if(border.a > 0)
			{
				pluto.rect(.5, .5, size.cx - 1, size.cy - 1, radius, radius);
				if(color.a > 0)
					pluto.fill(color.to_RGB(), color.a, true);
				pluto.stroke_width(1)
					.stroke_fill(border.to_RGB(), border.a);
			}
			else
			{
				pluto.rect(0, 0, size.cx, size.cy, radius, radius)
					.fill(color.to_RGB(), color.a, true);
			}

			auto_gdi<HBITMAP> hbitmap(pluto.tobitmap());
			dc->draw_image(pt, size, hbitmap.get());
		}


		struct DRAWITEMSTATE
		{
			bool selected{};
			bool disabled{};
			bool grayed{};
			bool checked{};
			bool focus{};
			bool default${};
			bool hotlight{};
			bool inactive{};
			bool no_accel{};
			bool no_focus_rect{};

			DRAWITEMSTATE(uint32_t flags)
			{
				Flag<uint32_t> fs = flags;
				selected = fs.has(ODS_SELECTED);
				grayed = fs.has(ODS_GRAYED);
				disabled = fs.has(ODS_DISABLED) || grayed;
				checked = fs.has(ODS_SELECTED);
			}
		};

		LRESULT ContextMenu::OnDrawItem_D2D(DRAWITEMSTRUCT *di)
		{
			LRESULT lret = TRUE;

			auto hMenu = reinterpret_cast<HMENU>(di->hwndItem);
			auto rc = reinterpret_cast<const Rect *>(&di->rcItem);
			DC dc = di->hDC;
			Flag<uint32_t> faction = di->itemAction;
			Flag<uint32_t> fState = di->itemState;

			DRAWITEMSTATE state(di->itemState);

			auto draw_entire = faction.has(ODA_DRAWENTIRE);
			//state.selected = fState.has(ODS_SELECTED);
			//state.disabled = fState.has(ODS_DISABLED) || fState.has(ODS_GRAYED);

			Color back_color = _theme.back.color.nor;
			Color text_color = _theme.text.color.nor;

			_tip.hide(!draw_entire);

			D2D d2d2;
			d2d2.init_res();
			d2d2.begin(di->hDC, *rc);

			D2D1_SIZE_F size_f = { static_cast<float>(rc->width()), static_cast<float>(rc->height()) };

			//d2d->render->SetDpi(ctx->dpi.val, ctx->dpi.val);
			//d2d->render->SetTransform(D2D1::Matrix3x2F::Identity());

			d2d2.render->Clear(_theme.background.color);

			if(state.selected)
			{
				if(state.disabled)
				{
					back_color = _theme.back.color.sel_dis;
					text_color = _theme.text.color.sel_dis;
				}
				else
				{
					back_color = _theme.back.color.sel;
					text_color = _theme.text.color.sel;
				}
			}
			else if(state.disabled)
			{
				back_color = _theme.back.color.nor_dis;
				text_color = _theme.text.color.nor_dis;
			}

			if(di->itemID == MF_NOITEM)
			{
				auto rect = *rc;
				rect.left += _theme.separator.margin.left;
				rect.right -= _theme.separator.margin.right;
				//rect.top += _theme.separator.margin.top;		
				rect.top += _theme.separator.margin.top;
				rect.bottom = rect.top + _theme.separator.size;
				//dc.fill_rect(*rc, composition ? dc.stock_brush(BLACK_BRUSH) : _hbackground);
				//draw_rect(&dc, rect.point(), { rect.width(), _theme.separator.size }, _theme.separator.color);
				//d2d2.brush->SetColor(D2D1::ColorF(0.f, 1.f, 0.f, 1.f));
				d2d2.brush->SetColor(_theme.separator.color);
				//d2d2.render->DrawLine(D2D1::Point2F((float)rect.left, 0), D2D1::Point2F((float)rect.right, 10),
				//					  d2d2.brush, 0.5f);

				D2D1_RECT_F rectF{};
				rectF.left = (float)rect.left;
				rectF.right = (float)rect.width();
				rectF.top = ((float)(rc->height() + _theme.separator.size) / 2.f) - _theme.separator.size;
				rectF.bottom = rectF.top + (float)_theme.separator.size;
				
				d2d2.render->FillRectangle(rectF, d2d2.brush);

				d2d2.end(true);
				dc.exclude_clip_rect(*rc);
				return lret;
			}

			auto menu_it = _menus.find(hMenu);
			auto menu = menu_it == _menus.end() ? nullptr : &menu_it->second;

			auto mii = get_item(di->itemID, hMenu, _items);

			if(!mii || (mii->title.empty() && !ident.equals(mii->wID)))
			{
				lret = msg.invoke();
				//DC dc_layer(dc.CreateCompatibleDC(), 1);
				dc.set_back_mode(true);
				dc.set_back(back_color);
				dc.set_text(text_color);
				//dc.fill_rect(di->rcItem, composition ? dc.stock_brush(BLACK_BRUSH) : _hbackground);
				
				d2d2.end(true);
				dc.exclude_clip_rect(*rc);
				return lret;
			}

			auto is_label = mii->visibility == Visibility::Label;
			auto is_static = mii->visibility == Visibility::Static;
			auto is_static_or_label = is_static || is_label;

			if(draw_entire)
			{
				mii->index = MENU::get_index(hMenu, mii->wID);
				::GetMenuItemRect(0, hMenu, mii->index, &mii->rect);
				
				//dc.fill_rect(di->rcItem, composition ? dc.stock_brush(BLACK_BRUSH) : _hbackground);
			}
			else
			{
				if(state.disabled && is_static_or_label)
				{
					d2d2.end(true);
					dc.exclude_clip_rect(*rc);
					return lret;
				}
			}

			if(!(state.disabled && is_static_or_label))
			{
				if(state.selected)
				{
					current.select_previtem = current.selectitem;
					current.selectitem = mii;
					if(mii->tip)
						current.tip = mii;
				}
				else
				{
				}
			}

			if(state.disabled)
			{
				if(is_static_or_label)
				{
					state.disabled = false;
					back_color = _theme.back.color.nor;
					text_color = _theme.text.color.nor;
				}
				else
				{
				}
			}

			const long image_size = _theme.image.size;
			auto rcblock = *rc;

			rcblock.top = _theme.back.margin.top;
			rcblock.bottom = rc->height() - _theme.back.margin.bottom;

			if(mii->cch > 0 || !menu->has_col)
			{
				rcblock.left += _theme.back.margin.left;
				rcblock.right -= _theme.back.margin.right;
			}
			else
			{
				rcblock.left += _theme.back.margin.left + dpi(3);
				rcblock.right -= _theme.back.margin.right + dpi(3);
			}

			//const auto width = rcblock.width();
			const auto height = rcblock.height();

			auto rcimg = rcblock;
			auto rcText = rcblock;

			rcimg.top = rcblock.top + ((height - image_size) / 2);
			rcimg.bottom = rcimg.top + image_size;

			if(mii->cch > 0 || !menu->has_col)
			{
				if(mii->cch == 0)
				{
				}
				else
				{
					rcimg.left = rcblock.left + _theme.back.padding.left;
					rcimg.right = rcimg.left + image_size;
				}
			}

			if(!is_static_or_label)
			{
				uint8_t op = back_color.a;

				Color border_color = _theme.back.border.nor;

				if(state.selected)
				{
					if(state.disabled && _theme.back.color.sel_dis.a > 0)
						op = _theme.back.color.sel_dis.a;
					else if(!state.disabled && _theme.back.color.sel_dis.a > 0)
						op = _theme.back.color.sel.a;

					border_color = state.disabled ? _theme.back.border.sel_dis : _theme.back.border.sel;
				}
				else
				{
					if(state.disabled && _theme.back.color.nor_dis.a > 0)
						op = _theme.back.color.nor_dis.a;
					else if(!state.disabled && _theme.back.color.nor.a > 0)
						op = _theme.back.color.nor.a;

					if(state.disabled)
						border_color = _theme.back.border.nor_dis;
				}

				//if(op > 0)
				{
					back_color.a = op;
					//draw_rect(&dc, rcblock.point(), { width, height }, back_color, border_color, _theme.back.radius);

					D2D1_RECT_F rectF = { 0, 0, size_f.width, size_f.height };

					if(state.selected)
					{
						d2d2.brush->SetColor(_theme.background.color);
						d2d2.render->FillRectangle(rectF, d2d2.brush);
					}

					d2d2.brush->SetColor(back_color);

					if(_theme.back.radius == 0)
					{
						d2d2.render->FillRectangle(rectF, d2d2.brush);
					}
					else 
					{
						auto radius = (float)_theme.back.radius;
						
						d2d2.render->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
						d2d2.render->FillRoundedRectangle({ rectF,radius, radius }, d2d2.brush);
					}
				}
			}

			auto has_checked_image = menu->draw.checks && menu->draw.images && (_theme.image.display >= 2);
			
			if(!mii->title.empty())
			{
				Color clrtext = text_color;

				if(!is_label && menu->draw.has_align())
				{
					rcText.left = rcblock.left + _theme.image.size + _theme.image.gap + _theme.back.padding.left;
					if(has_checked_image)
						rcText.left += _theme.image.size + _theme.image.gap;
				}
				else
				{
					rcText.left += _theme.back.padding.left;
				}

				rcText.right -= _theme.back.padding.right;

				if(mii->tab >= 0 && mii->is_popup())
					rcText.right -= _theme.image.size;

				auto txtfmt = DT_NOCLIP | DT_SINGLELINE | DT_VCENTER;

				if(_theme.text.prefix)
					txtfmt |= _theme.text.prefix;


				//rcText.top = rc->top - dpi(1);
				//rcText.bottom = rc->bottom;

				if(mii->tab <= 0 && mii->keys.empty())
				{
					//draw_string(dc, font.handle, &rcText, clrtext, mii->title, mii->title.length<int>(), (mii->tab < 0 ? DT_LEFT : DT_RIGHT) | txtfmt);
					///_log.info(L"%d %s", _theme.font.lfHeight, _theme.font.lfFaceName);
					auto tf = d2d2.createTextFormat(_theme.font.lfFaceName, std::abs(_theme.font.lfHeight));
					if(tf)
					{
						d2d2.render->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE::D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
						tf->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
						tf->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
						tf->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
						//_log.info(L"%d %s", _theme.font.lfHeight, _theme.font.lfFaceName);
						d2d2.brush->SetColor(clrtext);

						D2D1_RECT_F rect_ =
						{
							(float)rcText.left, (float)rcText.top,
							float(rcText.width()),
							float(rcText.height())
						};
						
					
						d2d2.render->DrawTextW(mii->title.normalize, mii->title.normalize.length<uint32_t>(),
											   tf,
											   rect_,
											   d2d2.brush);
						tf->Release();
					}
				}
				else
				{
					/*auto ds = [&](const string &left, const string &right)
					{
						if(!left.empty())
							draw_string(dc, font.handle, &rcText, clrtext, left, left.length<int>(), DT_LEFT | txtfmt);

						if(!right.empty())
						{
							Color c = clrtext;
							LOGFONTW lf{};
							std::memcpy(&lf, &_theme.font.lfHeight, sizeof lf);
							lf.lfHeight = long(lf.lfHeight * 0.80f);
							lf.lfWeight = FW_LIGHT;
							//lf.lfQuality = CLEARTYPE_NATURAL_QUALITY;
							auto_gdi<HFONT> r_hfont(::CreateFontIndirectW(&lf));
							if(menu->id == IDENT_ID_INSERT_UNICODE_CONTROL_CHARACTER)
								c.opacity(state.disabled ? 50 : 100);
							else
								c.opacity(state.disabled ? 30 : 50);

							draw_string(dc, r_hfont.get(), &rcText, c, right, right.length<int>(), DT_RIGHT | txtfmt);
						}
					};

					if(mii->keys.empty())
					{
						string left = mii->title.text.substr(0, mii->tab).trim_end().move();
						string right = mii->title.text.substr(mii->tab).trim_start().move();
						ds(left, right);
					}
					else
					{
						ds(mii->title.text, mii->keys);
					}*/
				}

			}

			if(state.selected && mii->tip)
				//	_tip.show(mii->tip, mii->rect);
				_tip.show(mii->tip.text, mii->tip.type, mii->tip.time, mii->rect);

			d2d2.end(true);

			// exlude menu item rectangle to prevent drawing by windows after us
			dc.exclude_clip_rect(*rc);

			return lret;
		}

		int ooo = 0;

		bool ContextMenu::paint_shell_row(const ShellRowPaintInput &input,
			ShellRowPaintPlan &plan)
		{
			if(!input.hdc)
				return false;

			plan = ShellRowPaintPlan::Resolve(input.itemId, input.itemAction,
				input.itemState, input.item, [&]() noexcept
				{
					ShellRowPaintFeatures features{};
					if(input.item)
					{
						features.staticOrLabel =
							input.item->visibility == Visibility::Static ||
							input.item->visibility == Visibility::Label;
						features.hasTooltip = static_cast<bool>(input.item->tip);
					}
					return features;
				}());

			if(plan.separator)
			{
				DC dc = input.hdc;
				dc.set_back_mode();
				auto rc = reinterpret_cast<const Rect *>(&input.rect);
				auto rect = *rc;
				rect.left += _theme.separator.margin.left;
				rect.right -= _theme.separator.margin.right;
				rect.top += _theme.separator.margin.top;
				rect.bottom = rect.top + _theme.separator.size;
				dc.fill_rect(*rc, composition ? dc.stock_brush(BLACK_BRUSH) :
					_hbackground);
				draw_rect(&dc, rect.point(),
					{ rect.width(), _theme.separator.size }, _theme.separator.color);
				return true;
			}
			
			if(!input.menu || !input.item)
				return false;

			const auto *menu = input.menu;
			const auto *mii = input.item;
			auto rc = reinterpret_cast<const Rect *>(&input.rect);
			DC dc = input.hdc;
			dc.set_back_mode();
			bool disabled = plan.disabled;
			Color back_color = _theme.back.color.nor;
			Color text_color = _theme.text.color.nor;
			if(plan.selected)
			{
				if(plan.disabled)
				{
					back_color = _theme.back.color.sel_dis;
					text_color = _theme.text.color.sel_dis;
				}
				else
				{
					back_color = _theme.back.color.sel;
					text_color = _theme.text.color.sel;
				}
			}
			else if(plan.disabled)
			{
				back_color = _theme.back.color.nor_dis;
				text_color = _theme.text.color.nor_dis;
			}
			auto is_label = mii->visibility == Visibility::Label;
			const bool is_static_or_label = plan.staticOrLabel;

			if(plan.drawEntire)
			{
				dc.fill_rect(*rc, composition ? dc.stock_brush(BLACK_BRUSH) : _hbackground);
			}
			else
			{
				if(plan.skipDisabledStatic)
				{
					return true;
				}
				
				dc.fill_rect(*rc, composition ? dc.stock_brush(BLACK_BRUSH) : _hbackground);
			}

			if(disabled)
			{
				if(is_static_or_label)
				{
					disabled = false;
					back_color = _theme.back.color.nor;
					text_color = _theme.text.color.nor;
				}
			}

			const long image_size = _theme.image.size;

			auto rcblock = *rc;
			
			rcblock.top += _theme.back.margin.top;
			rcblock.bottom -= _theme.back.margin.bottom;

			if(mii->cch > 0 || !menu->has_col)
			{
				rcblock.left += _theme.back.margin.left;
				rcblock.right -= _theme.back.margin.right;
			}
			else
			{
				rcblock.left += _theme.back.margin.left + dpi(3);
				rcblock.right -= _theme.back.margin.right + dpi(3);
			}

			const auto width = rcblock.width();
			const auto height = rcblock.height();

			auto rcimg = rcblock;
			auto rcText = rcblock;

			rcimg.top = rcblock.top + ((height - image_size) / 2);
			rcimg.bottom = rcimg.top + image_size;

			if(mii->cch > 0 || !menu->has_col)
			{
				if(mii->cch == 0)
				{
				}
				else 
				{
					rcimg.left = rcblock.left + _theme.back.padding.left;
					rcimg.right = rcimg.left + image_size;
				}
			}

			if(!is_static_or_label)
			{
				uint8_t op = back_color.a;

				Color border_color = _theme.back.border.nor;

				if(plan.selected)
				{
					if(disabled && _theme.back.color.sel_dis.a > 0)
						op = _theme.back.color.sel_dis.a;
					else if(!disabled && _theme.back.color.sel_dis.a > 0)
						op = _theme.back.color.sel.a;

					border_color = disabled ? _theme.back.border.sel_dis : _theme.back.border.sel;
				}
				else
				{
					if(disabled && _theme.back.color.nor_dis.a > 0)
						op = _theme.back.color.nor_dis.a;
					else if(!disabled && _theme.back.color.nor.a > 0)
						op = _theme.back.color.nor.a;

					if(disabled)
						border_color = _theme.back.border.nor_dis;
				}
				
				//dc.draw_fill_rounded_rect(rcblock, _theme.back.radius+2, 0,0);
				//draw_rect(&dc, rcblock.point(), { width, height }, 0xff000000, {}, _theme.back.radius);
				//draw_rect(&dc, rcblock.point(), { width, height }, _theme.background.color, {}, _theme.back.radius);
				//if(op > 0)
				{
					back_color.a = op;
					draw_rect(&dc, rcblock.point(), { width, height }, back_color, border_color, _theme.back.radius);
				}
			}

			auto has_checked_image = menu->draw.checks && menu->draw.images && (_theme.image.display >= 2);

			if(!is_label)
			{
				auto rcchekhed = rcimg;
				if(has_checked_image)
				{
					auto offset = _theme.image.size + _theme.image.gap;
					rcimg.left += offset;
					rcimg.right += offset;
				}

				//Draw custom submenu arrow and checked
				if(mii->is_popup() || mii->is_checked())
				{
					auto sy = mii->is_popup() ? &symbol.chevron : mii->is_radiocheck() ? &symbol.bullet : &symbol.checked;
					auto hbitmap = plan.selected ? sy->select : sy->normal;
					
					if(disabled)
						hbitmap = plan.selected ? sy->select_disabled : sy->normal_disabled;

					if(mii->is_popup())
					{
						auto rect = rcblock;
						auto x = rect.right - (_theme.back.padding.right + symbol.chevron.size.cx);
						auto y = rcblock.top + ((height - symbol.chevron.size.cy) / 2);
						dc.draw_image({ x, y }, { symbol.chevron.size.cx, symbol.chevron.size.cy }, hbitmap, disabled ? 64 : 255);
					}
					else if(mii->is_checked() && (_theme.image.display != 1 || !mii->has_image_or_draw()))
					{
						long z = _theme.image.size;
						dc.draw_image(rcchekhed.point(), { z, z }, hbitmap, disabled ? 64 : 255);
					}
				}

				auto image = &mii->image;

				if(plan.selected && mii->image_select.isvalid())
					image = &mii->image_select;

				if(image->hbitmap)
				{
					DC memDC(::CreateCompatibleDC(dc), 1);
					if(memDC)
					{
						if(memDC.select_bitmap(image->hbitmap))
						{
							Rect rcim = { rcimg.left + ((image_size - image->size.cx) / 2),
								(rcblock.top + (rcblock.bottom - image->size.cy)) / 2,
								image->size.cx, image->size.cy };
							
							//if(image->bitsPixel < 32)
							//	dc.bitblt({ rcim.left,rcim.top,size.cx,size.cy }, memDC, 0, 0);
							//else
							{
								bool is_16 = image->size.cx <= dpi(16) && image->size.cy <= dpi(16);
								if(image->import == ImageImport::SVG && is_16)
									dc.draw_image(rcim.point(), image->size, memDC, disabled ? 48 : 192);
								dc.draw_image(rcim.point(), image->size, memDC, disabled ? 64 : 255);
							}
							memDC.reset_bitmap();
						}
					}
				}
				else if(image->import == ImageImport::Draw)
				{
					auto draw = &image->draw;
					Color clr = text_color;
					SIZE size{};

					Color color_[2];
					color_[0] = text_color;

					if(draw->type == draw->DT_SHAPE)
					{
						auto shape = &draw->shape;
						size = shape->size;

						if(size.cx > 0 && size.cy > 0)
						{
							clr = shape->color[0];

							if(disabled) clr.opacities();

							if(size.cx > image_size) size.cx = image_size;
							if(size.cy > image_size) size.cy = image_size;

							if(shape->solid)
							{
								if(size.cx <= 3) size.cx = size.cx + 2;
								if(size.cy <= 3) size.cy = size.cy + 2;
							}
							else
							{
								if(size.cx == 1) size.cx = size.cx + 1;
								if(size.cy == 1) size.cy = size.cy + 1;
							}

							long left = rcimg.left + ((image_size - size.cx) / 2);
							if(mii->cch == 0)
							{
								left = rcimg.left + ((width - size.cx) / 2);
							}

							long top = rcimg.top + ((image_size - size.cy) / 2);
							draw_rect(&dc, { left, top}, size, clr);
						}
					}
					else if(draw->type == draw->DT_GLYPH)
					{
						if(mii->cch == 0)
						{
							rcimg = *rc;
							rcimg.left += 3;
							rcimg.right -= 3;
						}

						auto g = &draw->glyph;
						size = { g->size.cx, g->size.cy };
						if(size.cx > 0 && size.cy > 0 && g->font)
						{
							if(size.cx > image_size) size.cx = image_size;
							if(size.cy > image_size) size.cy = image_size;
							
							color_[0] = g->color[0];
							color_[1] = g->color[1];

							if(!color_[0])
							{
								if(_theme.image.color[0])
									color_[0] = _theme.image.color[0];
								else
								{
									if(disabled && (_theme.text.color.nor_dis))
										color_[0] = _theme.text.color.nor_dis;
									else
										color_[0] = text_color;
								}
							}

							if(!color_[1])
								color_[1] = _theme.image.color[0] ? _theme.image.color[1] : text_color;

							if(disabled)
							{
								color_[0].opacities();
								color_[1].opacities();
							}

							auto txtfmt = DT_NOCLIP | DT_SINGLELINE | DT_VCENTER;
							if(g->code[0])
								draw_string(dc, g->font, &rcimg, color_[0], &g->code[0], 1, DT_CENTER | txtfmt);

							if(g->code[1])
								draw_string(dc, g->font, &rcimg, color_[1], &g->code[1], 1, DT_CENTER | txtfmt);
						}
					}
				}
			}

			if(!mii->title.empty())
			{
				Color clrtext = text_color;

				if(!is_label && menu->draw.has_align())
				{
					rcText.left = rcblock.left + _theme.image.size + _theme.image.gap + _theme.back.padding.left;
					if(has_checked_image)
						rcText.left += _theme.image.size + _theme.image.gap;
				}
				else
				{
					rcText.left += _theme.back.padding.left;
				}

				rcText.right -= _theme.back.padding.right;

				if(mii->tab >= 0 && mii->is_popup())
					rcText.right -= _theme.image.size;

				auto txtfmt = DT_NOCLIP | DT_SINGLELINE | DT_VCENTER;

				if(_theme.text.prefix)
					txtfmt |= _theme.text.prefix;

				if(_hTheme)
				{
					rcText.top = rc->top - dpi(1);
					rcText.bottom = rc->bottom;

					if(mii->tab <= 0 && mii->keys.empty())
					{
						draw_string(dc, font.handle, &rcText, clrtext, mii->title, mii->title.length<int>(), (mii->tab < 0 ? DT_LEFT : DT_RIGHT) | txtfmt);
					}
					else
					{
						auto ds = [&](const string &left, const string &right)
						{
							if(!left.empty())
								draw_string(dc, font.handle, &rcText, clrtext, left, left.length<int>(), DT_LEFT | txtfmt);

							if(!right.empty())
							{
								Color c = clrtext;
								LOGFONTW lf{};
								std::memcpy(&lf, &_theme.font.lfHeight, sizeof lf);
								lf.lfHeight = long(lf.lfHeight * 0.80f);
								lf.lfWeight = FW_LIGHT;
								auto_gdi<HFONT> r_hfont(::CreateFontIndirectW(&lf));
								if(menu->id == IDENT_ID_INSERT_UNICODE_CONTROL_CHARACTER)
									c.opacity(disabled ? 50 : 100);
								else
									c.opacity(disabled ? 30 : 50);

								draw_string(dc, r_hfont.get(), &rcText, c, right, right.length<int>(), DT_RIGHT | txtfmt);
							}
						};

						if(mii->keys.empty())
						{
							string left = mii->title.text.substr(0, mii->tab).trim_end().move();
							string right = mii->title.text.substr(mii->tab).trim_start().move();
							ds(left, right);
						}
						else
						{
							ds(mii->title.text, mii->keys);
						}
					}
				}
			}
			
			// exlude menu item rectangle to prevent drawing by windows after us

			return TRUE;
		}

		LRESULT ContextMenu::OnDrawItem(DRAWITEMSTRUCT *di)
		{
			LRESULT lret = TRUE;
			//current.selectitem = nullptr;
			if(di->itemID == 0x5ffffffe)
				return lret;

			// Run the one real owner-draw callback against a translated row DIB.
			// NativeRowSurface presents those exact bits to the menu DC and hands
			// the same pixels to the capture cache at function exit, so capture never
			// reads the visible window surface or invokes the owner callback twice.
			NativeRowSurface::Sink sink = [](void *context, DRAWITEMSTRUCT *draw,
				const uint8_t *pixels, long width, long height) noexcept
			{
				static_cast<ContextMenu *>(context)->cache_painted_row(draw, pixels, width, height);
			};
			NativeRowSurface paintScope(di, this,
				_studio_capture.IsActive() ? sink : nullptr, kMaxAppearancePixels);

			bool render_d2d = false;
			if(render_d2d)
				return OnDrawItem_D2D(di);


			auto hMenu = reinterpret_cast<HMENU>(di->hwndItem);
			auto rc = reinterpret_cast<const Rect *>(&di->rcItem);
			auto menu_it = _menus.find(hMenu);
			auto menu = menu_it == _menus.end() ? nullptr : &menu_it->second;

			auto mii = get_item(di->itemID, hMenu, _items);
			const bool draw_entire = (di->itemAction & ODA_DRAWENTIRE) != 0;

			_tip.hide(!draw_entire);

			ShellRowPaintInput input{};
			input.hdc = di->hDC;
			input.rect = di->rcItem;
			input.itemId = di->itemID;
			input.itemAction = di->itemAction;
			input.itemState = di->itemState;
			input.menu = menu;
			input.item = mii;

			ShellRowPaintPlan plan{};
			const bool shell_row = di->itemID == MF_NOITEM ||
				(mii && !(mii->title.empty() && !ident.equals(mii->wID)));
			if(shell_row && paint_shell_row(input, plan))
			{
				if(plan.skipDisabledStatic)
					paintScope.skip_presentation();
				if(plan.updateGeometry)
				{
					mii->index = MENU::get_index(hMenu, mii->wID);
					::GetMenuItemRect(0, hMenu, mii->index, &mii->rect);
				}

				if(plan.selectedItem)
				{
					current.select_previtem = current.selectitem;
					current.selectitem = plan.selectedItem;
					if(mii->tip)
						current.tip = mii;
				}

				if(plan.exclude)
				{
					::ExcludeClipRect(di->hDC, rc->left, rc->top,
						rc->right, rc->bottom);
					paintScope.mark_excluded();
				}

				if(plan.showTooltip)
					_tip.show(mii->tip.text, mii->tip.type, mii->tip.time, mii->rect);

				return TRUE;
			}
			if(shell_row)
				return msg.invoke();

			Color back_color = _theme.back.color.nor;
			Color text_color = _theme.text.color.nor;
			DRAWITEMSTATE state(di->itemState);
			if(state.selected)
			{
				if(state.disabled)
				{
					back_color = _theme.back.color.sel_dis;
					text_color = _theme.text.color.sel_dis;
				}
				else
				{
					back_color = _theme.back.color.sel;
					text_color = _theme.text.color.sel;
				}
			}
			else if(state.disabled)
			{
				back_color = _theme.back.color.nor_dis;
				text_color = _theme.text.color.nor_dis;
			}

			DC dc = di->hDC;
			dc.set_back_mode();

			if(!mii || (mii->title.empty() && !ident.equals(mii->wID)))
			{
				if(auto_gdi<HBITMAP> hbitmap(dc.createbitmap(rc->width(), rc->height())); hbitmap)
				{
					DC dcmem(dc.CreateCompatibleDC(), 1);
					dcmem.select_bitmap(hbitmap.get());
					::SetViewportOrgEx(dcmem, -rc->left, -rc->top, nullptr);

					auto old_hdc = di->hDC;
					di->hDC = dcmem;

					lret = msg.invoke();

					di->hDC = old_hdc;
					::SetViewportOrgEx(dcmem, 0, 0, nullptr);

					std::vector<COLORREF> pixels(rc->width() * rc->height());

					BITMAPINFOHEADER bmpInfo = { 0 };
					bmpInfo.biSize = sizeof(bmpInfo);
					bmpInfo.biWidth = rc->width();
					bmpInfo.biHeight = -int(rc->height());
					bmpInfo.biPlanes = 1;
					bmpInfo.biBitCount = 32;
					bmpInfo.biCompression = BI_RGB;

					::GetDIBits(dcmem, hbitmap.get(), 0, rc->height(), &pixels[0], (LPBITMAPINFO)&bmpInfo, DIB_RGB_COLORS);

					std::for_each(pixels.begin(), pixels.end(), [](COLORREF &pixel) {
						if(pixel != 0) // black pixels stay transparent
							pixel |= 0xFF000000; // set alpha channel to 100%
					});

					::SetDIBits(dcmem, hbitmap.get(), 0, rc->height(), &pixels[0], (LPBITMAPINFO)&bmpInfo, DIB_RGB_COLORS);
					dc.draw_image(rc->point(), rc->size(), dcmem);

					return lret;
				}

				dc.set_back_mode(true);
				dc.set_back(back_color);
				dc.set_text(text_color);
				dc.fill_rect(di->rcItem, composition ? dc.stock_brush(BLACK_BRUSH) : _hbackground);

				lret = msg.invoke();

				return lret;
			}

			return TRUE;
		}

		LRESULT ContextMenu::OnMeasureItem(MEASUREITEMSTRUCT *mi)
		{
			LRESULT lret = 0;
			auto menu = current.menu;

			mi->itemHeight = 0;
			mi->itemWidth = 0;

			if(mi->itemID == 0x5ffffffe)
			{
				mi->itemWidth = 260;
				mi->itemHeight = 50;
			}
			else if(mi->itemID == MF_NOITEM)
			{
				mi->itemHeight = _theme.separator.margin.height() + _theme.separator.size;
			}
			else
			{
				mi->itemWidth = _theme.back.width();
				auto mii = get_item(mi->itemID, menu->handle, _items);
				if(mii)
				{
					if(mii->cch == 0)
					{
						if(!ident.equals(mi->itemID))
							lret = msg.invoke();
						else
						{
							auto v = (uint32_t)_theme.view2;
							if(v < _theme.image.size)
								v = _theme.image.size;

							mi->itemWidth += v;
							if(mi->itemWidth >= (v*2))
							mi->itemWidth /= 2;
							mi->itemHeight += mii->is_spacer() ? dpi(10u) : v;
						}
					}
					else
					{
						mi->itemHeight += mii->size.cy + _theme.back.height();
						if(mi->itemHeight % 2)
							mi->itemHeight++;

						mi->itemWidth += std::max<uint32_t>(menu->draw.length, mii->size.cx);

						// Remove extra space 'Submenu icon size'
						mi->itemWidth -= dpi.original(14);
					}
				}
			}

			if(_theme.layout.max_width > 0)
			{
				if(mi->itemWidth > _theme.layout.max_width)
					mi->itemWidth = _theme.layout.max_width;
			}

			if(_theme.layout.min_width > 0)
			{
				if(mi->itemWidth < _theme.layout.min_width)
					mi->itemWidth = _theme.layout.min_width;
			}

			return lret;
		}

		LRESULT ContextMenu::OnStart()
		{
			return msg.invoke();
		}

		LRESULT ContextMenu::OnEnd()
		{
			Selections::point = { 0, 0 };
			//InvokeCommand(selectid);
			current.zero();

			__trace(L"ContextMenu.End");

			auto ret = msg.invoke();
			_menus.clear();

			return ret;
		}

		LRESULT ContextMenu::OnTimer([[maybe_unused]] UINT_PTR nIDEvent, [[maybe_unused]] TIMERPROC Timerproc)
		{
			return msg.invoke();
		}

		void ContextMenu::init_cfg()
		{
			//SystemParametersInfoForDpi
			auto sets = &_cache->settings;
			//auto isW11OrGreater = ver->IsWindows11OrGreater();
			auto isHighContrast = Theme::IsHighContrast();

			Object obj;

			_context.theme = &_theme;
			_theme.dpi = &dpi;
			_context.font.icon = FontCache::Default;

			font.menu = {};
			Theme::GetFont(&font.menu, dpi.val);

		//	long zofont = std::abs(font.menu.lfHeight);

			font.menu.lfHeight = dpi(font.menu.lfHeight);

			_context.font.text = font.menu.lfFaceName;

			bool enableTransparency = false;
			bool systemUsesLightTheme = true;
			bool appsUseLightTheme = true;

			if(isHighContrast)
				_theme.system.mode = 2;
			else
			{
				Theme::Personalize(&systemUsesLightTheme, &appsUseLightTheme, &enableTransparency);
				_theme.system.mode = systemUsesLightTheme ? 0 : 1;
			}

			_theme.enableTransparency = enableTransparency;
			_theme.systemUsesLightTheme = systemUsesLightTheme;
			_theme.appsUseLightTheme = appsUseLightTheme;
			_theme.isHighContrast = isHighContrast;
			

			auto is_sys_dark = Selected.Window.isTaskbar() ? !systemUsesLightTheme : !appsUseLightTheme;// Theme::IsDarkMode(Selected.Window.isTaskbar());

			_theme.mode = is_sys_dark;

			auto is_dark = is_sys_dark;
			auto th = &sets->theme;

			if(th->dark)
			{
				obj = _context.Eval(th->dark).move();
				if(obj.not_default())
					is_dark = obj.to_bool();
				
				_theme.mode = is_dark;
			}

			if(th->name)
			{
				obj = _context.Eval(th->name).move();
				if(obj.is_number())
				{
					auto value = obj.to_number<ThemeType>();
					if(value >= ThemeType::Auto && value <= ThemeType::Custom)
					{
						_theme.Type = value;
					}
				}
				else if(obj.is_string())
				{
					Hash value = obj.to_string().trim().hash();
					if(value == IDENT_THEME_MODERN)
						_theme.Type = ThemeType::Modern;
					else if(value == IDENT_THEME_WHITE)
						_theme.Type = ThemeType::White;
					else if(value == IDENT_THEME_BLACK)
						_theme.Type = ThemeType::Black;
					else if(value == IDENT_THEME_CLASSIC)
						_theme.Type = ThemeType::Classic;
					else if(value == IDENT_THEME_SYSTEM)
						_theme.Type = ThemeType::System;
					else if(value == IDENT_THEME_AUTO)
						_theme.Type = ThemeType::Auto;
					else if(value == IDENT_THEME_HIGHCONTRAST)
						_theme.Type = ThemeType::HighContrast;
				}
			}

			_theme.enableTransparency = enableTransparency;
			_theme.systemUsesLightTheme = systemUsesLightTheme;
			_theme.appsUseLightTheme = appsUseLightTheme;
			_theme.isHighContrast = isHighContrast;

			_theme.system.transparency = enableTransparency;
			_theme.system.mode = systemUsesLightTheme ? 0 : 1;

			struct {
				int8_t effect = 0;
				Color tintcolor;
			} transparency;

			if(enableTransparency)
			{
				transparency.effect = 3;
			}

			if(_context.Eval(th->background.effect, obj))
			{
				//effect = none = 0, transparent = 1, blur = 2, acrylic = 3, mica = 4, tabbed = 5, add - to force
				auto ef = [&](Object &o)->bool
				{
					if(o.is_default())
					{
						transparency.effect = -1;
						return true;
					}
					else if(o.is_number())
					{
						transparency.effect = o;
						if(transparency.effect > 5)
							transparency.effect = 0;
						return true;
					}
					else if(o.is_string())
					{
						auto effect = o.Value.String.trim().hash();
						if(effect == IDENT_EFFECT_TRANSPARENT)
							transparency.effect = 1;
						else if(effect == IDENT_EFFECT_BLUR)
							transparency.effect = 2;
						else if(effect == IDENT_EFFECT_ACRYLIC)
							transparency.effect = 3;
						else if(effect == IDENT_NONE)
							transparency.effect = 0;
						else if(effect == IDENT_AUTO)
							transparency.effect = -1;
						return true;
					}

					return false;
				};

				if(!ef(obj) && obj.is_array(true))
				{
					auto ptr = obj.get_pointer();
					int ac = ptr[0];

					if(ef(ptr[1]))
					{
						if(ac >= 2)
							_context.to_color(ptr[2], &transparency.tintcolor);
						if(ac == 3 && ptr[3].not_default())
							transparency.tintcolor.opacity(ptr[3]);
					}
				}

				if(transparency.effect == -1)
				{
					enableTransparency = true;
					transparency.effect = 2;
					if(ver->IsWindows11OrGreater())
						transparency.effect = 3;
				}
			}

			if(!composition || isHighContrast)
			{
				transparency.effect = 0;
			}

			if(transparency.effect == 0)
				enableTransparency = false;
			else if(transparency.effect >= 2)
			{
				if(enableTransparency)
					_context.eval_color(th->background.tintcolor, &transparency.tintcolor);
				else
					transparency.effect = 0;
			}

			switch(_theme.Type)
			{
				case ThemeType::HighContrast:
					_theme.background.effect = 0;
					_theme = Theme::HighContrast();
					break;
				case ThemeType::White:
					_theme = Theme::White(transparency.effect);
					break;
				case ThemeType::Black:
					_theme = Theme::Black(transparency.effect);
					break;
				case ThemeType::Modern:
					_theme = is_dark ? Theme::Modern(ThemeType::Dark, 1, transparency.effect) : Theme::Modern(ThemeType::Light, 0, transparency.effect);
					
					if(enableTransparency and transparency.effect == 3)
					{
						if(!is_dark)
							_theme.background.tintcolor =  0xFFFFFF;
					}

					_settings.modify_items.position = 2;
					_settings.modify_items.image = 2;
					break;
				case ThemeType::Classic:
					_theme = is_dark ? Theme::Dark(false, enableTransparency) : Theme::Light(false, enableTransparency);
					break;
				case ThemeType::Auto:
				case ThemeType::System:
				default:
				{
					if(isHighContrast)
						_theme = Theme::HighContrast();
					else
					{
						_theme = is_dark ? Theme::Dark(false, enableTransparency) : Theme::Light(false, enableTransparency);
						//10240, 10586, 14393, 10593, 16299, 17134
						//17666 , 19042
						//ImmersiveStartDark::Menu;ImmersiveStart::Menu
						_hTheme = _theme.OpenThemeData(hwnd.active, is_dark ? L"DarkMode::Menu" : L"Menu", dpi.val);

						if(_hTheme && ver->IsWindows11OrGreater())
						{
							Color nor, sel, dis, dis_sel;

							// Special handling for Windows 11 Canary and Dev builds
							bool isCanaryOrDev = ver->IsWindows11CanaryOrDev();

							auto get_bk_clr = [&](Color &clr, int iPartId, int iStateId, int x = -1, int y = -1, int size = 9)->bool
							{
								DC dc = hwnd.owner;
								RECT rc = { 0, 0, size, size };
								auto res = ::DrawThemeBackground(_hTheme, dc, iPartId, iStateId, &rc, nullptr);
								if(SUCCEEDED(res))
								{
									x = x < 0 ? size / 2 : x;
									y = y < 0 ? size / 2 : y;
									clr.from(dc.get_pixel(x, y), 100);
									return true;
								}
								return false;
							};

							auto get_clr = [&](Color &clr, int iPartId, int iStateId, int iPropId)->bool
							{
								COLORREF ret = CLR_INVALID;
								if(SUCCEEDED(::GetThemeColor(_hTheme, iPartId, iStateId, iPropId, &ret)))
								{
									clr.from(ret, 100);
									return true;
								}
								return false;
							};

							// Use more reliable theme color detection for Canary builds
							if (isCanaryOrDev)
							{
								// Get text colors with fallbacks to system colors
								if (!get_clr(nor, MENU_POPUPITEM, MPI_NORMAL, TMT_TEXTCOLOR))
								{
									nor.from(::GetSysColor(COLOR_MENUTEXT), 100);
								}
								
								if (!get_clr(sel, MENU_POPUPITEM, MPI_HOT, TMT_TEXTCOLOR))
								{
									sel.from(::GetSysColor(COLOR_HIGHLIGHTTEXT), 100);
								}
								
								if (!get_clr(dis, MENU_POPUPITEM, MPI_DISABLED, TMT_TEXTCOLOR))
								{
									dis.from(::GetSysColor(COLOR_GRAYTEXT), 100);
								}
								
								if (!get_clr(dis_sel, MENU_POPUPITEM, MPI_DISABLEDHOT, TMT_TEXTCOLOR))
								{
									dis_sel.from(::GetSysColor(COLOR_GRAYTEXT), 100);
								}
							}
							else
							{
								// Standard theme color detection for non-Canary builds
								get_clr(nor, MENU_POPUPITEM, MPI_NORMAL, TMT_TEXTCOLOR);
								get_clr(sel, MENU_POPUPITEM, MPI_HOT, TMT_TEXTCOLOR);
								get_clr(dis, MENU_POPUPITEM, MPI_DISABLED, TMT_TEXTCOLOR);
								get_clr(dis_sel, MENU_POPUPITEM, MPI_DISABLEDHOT, TMT_TEXTCOLOR);
							}

							_theme.text.color = { nor, sel, dis, dis_sel };
							_theme.symbols.checked = { nor, sel, dis, dis_sel };
							_theme.symbols.bullet = { nor, sel, dis, dis_sel };
							_theme.symbols.chevron = { nor, sel, dis, dis_sel };

							// More reliable background color detection for Canary builds
							if (isCanaryOrDev)
							{
								if (!get_clr(_theme.background.color, MENU_POPUPBACKGROUND, MPI_NORMAL, TMT_FILLCOLOR))
								{
									if (!get_bk_clr(_theme.background.color, MENU_POPUPITEM, MPI_NORMAL))
									{
										_theme.background.color.from(::GetSysColor(COLOR_MENU), 100);
									}
								}
								
								if (!get_bk_clr(_theme.back.color.sel, MENU_POPUPITEM, MPI_HOT))
								{
									_theme.back.color.sel.from(::GetSysColor(COLOR_HIGHLIGHT), 100);
								}
								
								if (!get_bk_clr(_theme.back.color.nor_dis, MENU_POPUPITEM, MPI_DISABLED))
								{
									_theme.back.color.nor_dis.from(::GetSysColor(COLOR_MENU), 100);
								}
								
								if (!get_bk_clr(_theme.back.color.sel_dis, MENU_POPUPITEM, MPI_DISABLEDHOT))
								{
									_theme.back.color.sel_dis.from(::GetSysColor(COLOR_BTNFACE), 100);
								}
								
								if (!get_bk_clr(_theme.separator.color, MENU_POPUPSEPARATOR, 0, -1, -1, 3))
								{
									_theme.separator.color.from(::GetSysColor(COLOR_GRAYTEXT), 100);
								}
							}
							else
							{
								// Standard background color detection for non-Canary builds
								if(!get_clr(_theme.background.color, MENU_POPUPBACKGROUND, MPI_NORMAL, TMT_FILLCOLOR))
									get_bk_clr(_theme.background.color, MENU_POPUPITEM, MPI_NORMAL);

								get_bk_clr(_theme.back.color.sel, MENU_POPUPITEM, MPI_HOT);
								get_bk_clr(_theme.back.color.nor_dis, MENU_POPUPITEM, MPI_DISABLED);
								get_bk_clr(_theme.back.color.sel_dis, MENU_POPUPITEM, MPI_DISABLEDHOT);
								get_bk_clr(_theme.separator.color, MENU_POPUPSEPARATOR, 0, -1, -1, 3);
							}

							_theme.border.color = _theme.separator.color;// getbkclr(MENU_POPUPBORDERS, 0, 0, 4, 9);
							if(enableTransparency)
							{
								_theme.back.color.sel.opacity(50);
								_theme.back.color.sel_dis.opacity(50);
								_theme.back.color.nor_dis.opacity(25);
							}
							else
							{
								_theme.back.color.nor = _theme.background.color;
							}
							::GetThemeInt(_hTheme, MENU_POPUPSEPARATOR, 0, TMT_BORDERSIZE, (int *)&_theme.separator.size);
						}
					}
					break;
				}
			}

			_theme.system.transparency = enableTransparency;
			_theme.system.mode = systemUsesLightTheme ? 0 : 1;
			_theme.isHighContrast = isHighContrast;
			_theme.appsUseLightTheme = appsUseLightTheme;
			_theme.mode = is_dark;

			_theme.dpi = &dpi;
			_context.theme = &_theme;

			auto set_color = [=](Object &obj, Color *property)
			{
				_context.to_color(obj, property);
			};

			auto eval_color = [=](auto_expr *ep, Color *property)
			{
				if(ep) _context.eval_color(*ep, property);
			};

			auto eval_state = [=](Settings::COLOR *e, Theme::state_t *s)
			{
				_context.eval_color(e->value, &s->sel);
				if(e->value)
				{
					s->nor = s->sel;
					s->nor_dis = s->sel;
					s->sel_dis = s->sel;
				}
				_context.eval_color(e->normal, &s->nor);
				_context.eval_color(e->select, &s->sel);
				_context.eval_color(e->normal_disabled, &s->nor_dis);
				_context.eval_color(e->select_disabled, &s->sel_dis);
			};

			auto eval_color_array = [=](auto_expr *e, Color *clr1, Color *clr2)->void
			{
				Object obj;
				if(_context.Eval(*e, obj))
				{
					if(obj.is_array() && obj.Value.Pointer)
					{
						auto ptr = obj.get_pointer();
						set_color(ptr[1], clr1);
						set_color(ptr[2], clr2);
					}
					else if(obj.not_default())
					{
						set_color(obj, clr1);
					}
				}
			};

			auto eval_margin = [=](Settings::MARGIN *e, Margin *m)
			{
				Object obj;
				if(_context.eval_number(e->left, obj))
					m->left = obj;

				if(_context.eval_number(e->top, obj))
					m->top = obj;

				if(_context.eval_number(e->right, obj))
					m->right = obj;

				if(_context.eval_number(e->bottom, obj))
					m->bottom = obj;
			};

			auto get_rect = [](Object *o, Margin *m)
			{
				if(o)
				{
					if(o->is_array() && o->Value.Pointer)
					{
						auto ptr = o->get_pointer();
						int argc = ptr[0];
						if(argc == 2)
						{
							if(ptr[1].not_default())
							{
								m->left = ptr[1];
								m->right = m->left;
							}

							if(ptr[2].not_default())
							{
								m->top = ptr[2];
								m->bottom = m->top;
							}
						}
						else
						{
							if(ptr[1].not_default())
								m->left = ptr[1];
							if(ptr[2].not_default())
								m->top = ptr[2];
							if(ptr[3].not_default())
								m->right = ptr[3];
							if(ptr[4].not_default())
								m->bottom = ptr[4];
						}
					}
					else if(o->not_default())
					{
						long value = *o;
						if(value >= 0 && value <= 400)
							*m = { value, value, value, value };
					}
				}
			};

			if(!_hTheme)
				_hTheme = _theme.OpenThemeData(nullptr, L"Menu", dpi.val);

			if(_context.eval_number(sets->theme.image.enabled, obj))
			{
				_theme.image.enabled = obj.to_bool();
				
				if(!_theme.image.enabled)
				{
					_settings.modify_items.image = 0;
					_settings.new_items.image = false;
				}
			}

			// settings
			// static items
			if(_context.eval_number(sets->modify_items.enabled, obj))
				_settings.modify_items.enabled = obj.to_bool();

			if(_settings.modify_items.enabled)
			{
				if(_context.eval_number(sets->modify_items.title, obj))
					_settings.modify_items.title = obj.to_bool();

				if(_context.eval_number(sets->modify_items.visibility, obj))
					_settings.modify_items.visibility = obj.to_bool();

				if(_context.eval_number(sets->modify_items.parent, obj))
					_settings.modify_items.parent = obj.to_bool();

				if(_context.eval_number(sets->modify_items.separator, obj))
					_settings.modify_items.separator = obj.to_bool();

				if(_context.eval_number(sets->modify_items.keys, obj))
					_settings.modify_items.keys = obj.to_bool();

				if(_context.eval_number(sets->modify_items.position, obj))
					_settings.modify_items.position = obj.to_number<int>();

				if(_context.eval_number(sets->modify_items.remove.duplicate, obj))
					_settings.modify_items.remove.duplicate = obj.to_number<bool>();

				if(_context.eval_number(sets->modify_items.remove.disabled, obj))
					_settings.modify_items.remove.disabled = obj.to_number<bool>();

				if(_context.eval_number(sets->modify_items.remove.separator, obj))
					_settings.modify_items.remove.separator = obj.to_number<bool>();

				if(_context.eval_number(sets->modify_items.auto_image_group, obj) && obj.to_bool())
				{
					_settings.modify_items.position = 2;
					_settings.modify_items.image = 2;
				}

				if(_theme.image.enabled)
				{
					if(_context.eval_number(sets->modify_items.image, obj))
						_settings.modify_items.image = obj.to_number<int>();
				}
				else 
				{
					_settings.modify_items.image = 0;
				}
			}
			else 
			{
				_settings.modify_items.image = 0;
				_settings.modify_items.position = 0;
				_settings.modify_items.title = false;
				_settings.modify_items.visibility = false;
				_settings.modify_items.parent = false;
				_settings.modify_items.separator = false;
				_settings.modify_items.keys = false;
				_settings.modify_items.remove.duplicate = false;
				_settings.modify_items.remove.disabled = false;
				_settings.modify_items.remove.separator = false;
			}
			
			// new items
			if(_context.eval_number(sets->new_items.enabled, obj))
				_settings.new_items.enabled = obj.to_bool();

			if(_settings.new_items.enabled)
			{
				if(_theme.image.enabled)
				{
					if(_context.eval_number(sets->new_items.image, obj))
						_settings.new_items.image = obj.to_bool();
				}

				if(_context.eval_number(sets->new_items.keys, obj))
					_settings.new_items.keys = obj.to_bool();
			}

			// layout
			if(_context.Eval(th->layout.width, obj))
			{
				if(obj.not_default())
					_theme.layout.min_width = obj;
				else if(obj.is_array() && obj.Value.Pointer)
				{
					auto ptr = obj.get_pointer();
					int argc = ptr[0];
					if(argc >= 1)
					{
						if(ptr[1].not_default())
							_theme.layout.min_width = ptr[1];

						if(argc >= 2 && ptr[2].not_default())
							_theme.layout.max_width = ptr[2];
					}
				}

				if(_theme.layout.min_width < 0)
					_theme.layout.min_width = 0;
				if(_theme.layout.max_width < 0)
					_theme.layout.max_width = 0;
			}

			if(_context.eval_number(th->layout.rtl, obj))
			{
				_theme.layout.rtl = obj;
				is_layoutRTL = _theme.layout.rtl;
			}

			if(_context.eval_number(th->layout.popup.align, obj))
			{
				_theme.layout.popup.align = obj;
				if(_theme.layout.popup.align > 20)
					_theme.layout.popup.align = 20;
				else if(_theme.layout.popup.align < -20)
					_theme.layout.popup.align = -20;
			}

			// theme.background
			if(!_context.eval_color(th->background.color, &_theme.background.color))
			{
				if(enableTransparency)
					_theme.background.color.a = 0;
			}

			if(_context.eval_number(th->background.opacity, obj))
				_theme.background.color.a = _theme.opacity(obj);
			else if(enableTransparency)
				_theme.background.color.a = 0;

			if(_context.Eval(th->background.image, obj) )
			{
				if(!obj.is_default())
					_theme.background.image = obj.to_string().trim().move();
			}

			_theme.background.effect = transparency.effect;

			if(transparency.tintcolor)
				_theme.background.tintcolor = transparency.tintcolor;

			
			if(transparency.effect == 0)
				_theme.background.color.a = 0xFF;
			else
			{
				//_theme.back.color.nor.a = 0;
				//_theme.back.color.dis.a = 0;
			}

			_theme.background.opacity = _theme.background.color.a;

			//_theme.view = _theme.block.height();
			// theme.view
			if(_context.eval_number(th->view, obj))
			{
				auto val = VIEWMODE::get(obj);
				if(val >= 0)
				{
					_theme.back.padding.top = val;
					_theme.back.padding.bottom = val;
				}
			}

			_theme.back.color.nor.from(_theme.background.color);

			/*if(_theme.background.opacity < 0xFF)
			{
				auto op = _theme.background.opacity;
				_theme.back.color.nor.a = 0;
				_theme.back.color.dis.a = 0;
				_theme.back.color.sel.a = op;
				_theme.back.color.dis_sel.a = op / 2;
				_theme.back.opacity = op;
			}*/

			// theme.gradient
			if(_context.eval_number(th->gradient.enabled, obj) && obj != 0)
			{
				if(_context.Eval(th->gradient.linear, obj))
				{
					if(obj.is_array() && obj.Value.Pointer)
					{
						auto ptr = obj.get_pointer();
						_theme.gradient.linear[1] = ptr[1];
						_theme.gradient.linear[2] = ptr[2];
						_theme.gradient.linear[3] = ptr[3];
						_theme.gradient.linear[4] = ptr[4];

						if(_theme.gradient.linear[1] > 0.0 ||
						   _theme.gradient.linear[2] > 0.0 ||
						   _theme.gradient.linear[3] > 0.0 ||
						   _theme.gradient.linear[4] > 0.0)
						{
							_theme.gradient.linear[0] = 1.0;
							_theme.gradient.enabled = true;
						}
					}
				}
				else if(_context.Eval(th->gradient.radial, obj))
				{
					if(obj.is_array() && obj.Value.Pointer)
					{
						auto ptr = obj.get_pointer();
						uint32_t c = ptr[0];
						_theme.gradient.radial[1] = (c >= 1) ? (double)ptr[1] : 100;
						_theme.gradient.radial[2] = (c >= 2) ? (double)ptr[2] : 100;
						_theme.gradient.radial[3] = (c >= 3) ? (double)ptr[3] : 50;
						_theme.gradient.radial[4] = (c >= 4) ? (double)ptr[4] : _theme.gradient.radial[1];
						_theme.gradient.radial[5] = (c >= 5) ? (double)ptr[5] : _theme.gradient.radial[2];
					}
					else if(obj.not_default()) {
						_theme.gradient.radial[1] = 100;
						_theme.gradient.radial[2] = 100;
						_theme.gradient.radial[3] = obj;
						_theme.gradient.radial[4] = 100;
						_theme.gradient.radial[5] = 100;
						_theme.gradient.radial[0] = 1.0;
					}

					if(_theme.gradient.radial[1] > 0.0 ||
					   _theme.gradient.radial[2] > 0.0 ||
					   _theme.gradient.radial[3] > 0.0 ||
					   _theme.gradient.radial[4] > 0.0 ||
					   _theme.gradient.radial[5] > 0.0)
					{
						_theme.gradient.radial[0] = 1.0;
						_theme.gradient.enabled = true;
					}
				}

				if(_theme.gradient.enabled)
				{
					if(_context.Eval(th->gradient.stop, obj))
					{
						if(obj.is_array() && obj.Value.Pointer)
						{
							auto stop = [=](Object *obj, Theme::gradientstop_t *gs)->bool
							{
								if(obj->is_array() && obj->Value.Pointer)
								{
									auto ptr = obj->get_pointer();
									uint32_t ac = ptr[0];
									if(ac >= 1 && ac <= 3)
									{
										gs->offset = ptr[1];
										if(ac >= 2)
											_context.to_color(ptr[2], &gs->color);

										if(ac == 3 && ptr[3].not_default())
											gs->color.opacity(ptr[3]);

										return true;
									}
								}
								return false;
							};

							auto ptr = obj.get_pointer();
							uint32_t c = ptr[0];
							if(c > 0)
							{
								int ret = 0;
								for(auto i = 1u; i <= c; i++)
								{
									Theme::gradientstop_t gs;
									gs.color.opacity(100);
									if(stop(&ptr[i], &gs))
									{
										_theme.gradient.stpos.push_back(gs);
										ret++;
									}
								}
								_theme.gradient.enabled = ret > 0;
							}
						}
					}
				}
			}

			// theme.border
			if(_context.eval_number(th->border.size, obj))
			{
				uint8_t value = obj;
				_theme.border.size = value > 10 ? 10 : value;
			}

			if(_theme.border.size > 0)
			{
				if(_context.eval_number(th->border.enabled, obj) && !obj.to_bool())
					_theme.border.size = 0;

				if(_theme.border.size > 0)
				{
					_context.eval_color(th->border.color, &_theme.border.color);

					if(_context.eval_number(th->border.opacity, obj))
						_theme.border.color.a = _theme.opacity(obj);

					if(_theme.border.color.a == 0)
						_theme.border.size = 0;
				}
			}

			if(_context.eval_number(th->border.radius, obj))
				_theme.border.radius = _theme.radius(obj);

			if(_context.Eval(th->border.padding.value, obj))
				get_rect(&obj, &_theme.border.padding);

			eval_margin(&th->border.padding, &_theme.border.padding);

			// theme.shadow
			if(_context.eval_number(th->shadow.size, obj))
			{
				_theme.shadow.size = obj;
				if(_theme.shadow.size > 30)
					_theme.shadow.size = 30;
			}

			if(_theme.shadow.size > 0)
			{
				if(_context.eval_number(th->shadow.enabled, obj) && !obj.to_bool())
					_theme.shadow.size = 0;

				if(_theme.shadow.size > 0)
				{
					_context.eval_color(th->shadow.color, &_theme.shadow.color);

					if(_context.eval_number(th->shadow.opacity, obj))
						_theme.shadow.color.a = _theme.opacity(obj);

					if(_theme.shadow.color.a == 0)
						_theme.shadow.size = 0;
					else if(_context.eval_number(th->shadow.offset, obj))
					{
						uint8_t val = obj;
						_theme.shadow.offset = val > 30 ? 30 : val;
					}
				}
			}

			if(_theme.shadow.size == 0 || _theme.shadow.color.a == 0)
				_theme.shadow.enabled = false;

			// theme.item.text
			eval_state(&th->item.text.color, &_theme.text.color);

			// theme.item.back
			eval_state(&th->item.back, &_theme.back.color);

			if(_context.eval_number(th->item.opacity, obj))
				_theme.back.opacity = _theme.opacity(obj);

			// theme.item.boorder
			eval_state(&th->item.border, &_theme.back.border);

			if(_context.eval_number(th->item.radius, obj))
				_theme.back.radius = _theme.radius(obj);

			// theme.item.padding
			if(_context.Eval(th->item.padding.value, obj))
				get_rect(&obj, &_theme.back.padding);

			eval_margin(&th->item.padding, &_theme.back.padding);

			// theme.item.margin
			if(_context.Eval(th->item.margin.value, obj))
				get_rect(&obj, &_theme.back.margin);

			eval_margin(&th->item.margin, &_theme.back.margin);

			// theme.item.prefix		mnemonic-prefix
			if(_context.eval_number(th->item.prefix, obj) && obj.not_default())
			{
				int8_t val = obj;
				if(val == 1)
					_theme.text.prefix = 0;
				else if(val == 2)
					_theme.text.prefix = DT_NOPREFIX;
				else
					_theme.text.prefix = DT_HIDEPREFIX;
			}

			if(_theme.text.prefix == 0xFFFFFFFF)
				_theme.text.prefix = keyboard.key_shift() ? 0 : DT_HIDEPREFIX;

			// theme.image
			if(_theme.image.enabled)
			{
				if(_context.eval_number(th->image.gap, obj))
					_theme.image.gap = obj;

				if(_context.eval_number(th->image.scale, obj))
					_theme.image.scale = obj;
			}

			// theme.separator
			if(_context.eval_number(th->separator.size, obj))
			{
				uint8_t value = obj;
				_theme.separator.size = value > 40 ? 40 : value;
			}

			if(!_context.eval_color(th->separator.color, &_theme.separator.color))
			{
				if(_theme.background.opacity < 0xff)
				{
					if(_theme.separator.color.a == 0xff)
						_theme.separator.color.a = _theme.background.opacity + 5;
				}
			}

			if(_context.eval_number(th->separator.opacity, obj))
				_theme.separator.color.a = Color::ToByte(obj);

			if(_context.Eval(th->separator.margin.value, obj))
				get_rect(&obj, &_theme.separator.margin);

			eval_margin(&th->separator.margin, &_theme.separator.margin);

			_theme.set_symbols_as_text(_theme.mode == 1);

			// theme.symbol
			auto ev_sb = [&](auto_expr *expr, bool normal)
			{
				Object obj;
				if(_context.Eval(*expr, obj))
				{
					Color v;

					if(obj.is_array() && obj.Value.Pointer)
					{
						auto ptr = obj.get_pointer();
						if(ptr[1].not_default())
						{
							v = ptr[1].to_color();
							if(normal)
							{
								_theme.symbols.chevron.nor = v;
								_theme.symbols.checked.nor = v;
								_theme.symbols.bullet.nor = v;
							}
							else
							{
								_theme.symbols.chevron.sel = v;
								_theme.symbols.checked.sel = v;
								_theme.symbols.bullet.sel = v;
							}
						}

						if(ptr[2].not_default())
						{
							v = ptr[2].to_color();
							if(normal)
							{
								_theme.symbols.chevron.nor_dis = v;
								_theme.symbols.checked.nor_dis = v;
								_theme.symbols.bullet.nor_dis = v;
							}
							else
							{
								_theme.symbols.chevron.sel_dis = v;
								_theme.symbols.checked.sel_dis = v;
								_theme.symbols.bullet.sel_dis = v;
							}
						}
					}
					else if(obj.not_default())
					{
						v = obj.to_color();
						if(normal)
						{
							_theme.symbols.chevron.nor = v;
							_theme.symbols.checked.nor = v;
							_theme.symbols.bullet.nor = v;
						}
						else
						{
							_theme.symbols.chevron.sel = v;
							_theme.symbols.checked.sel = v;
							_theme.symbols.bullet.sel = v;
						}
					}
				}
			};

			ev_sb(&th->symbol.color.value, true);
			ev_sb(&th->symbol.color.normal, true);
			ev_sb(&th->symbol.color.select, false);

			auto ev_sb_dis = [&](auto_expr *expr, bool normal)
			{
				Object obj;
				if(_context.Eval(*expr, obj))
				{
					Color v;

					if(obj.is_array() && obj.Value.Pointer)
					{
						auto ptr = obj.get_pointer();
						if(ptr[1].not_default())
						{
							v = ptr[1].to_color();
							if(normal)
							{
								_theme.symbols.chevron.nor_dis = v;
								_theme.symbols.checked.nor_dis = v;
								_theme.symbols.bullet.nor_dis = v;
							}
							else
							{
								_theme.symbols.chevron.sel_dis = v;
								_theme.symbols.checked.sel_dis = v;
								_theme.symbols.bullet.sel_dis = v;
							}
						}
					}
					else if(obj.not_default())
					{
						v = obj.to_color();
						if(normal)
						{
							_theme.symbols.chevron.nor_dis = v;
							_theme.symbols.checked.nor_dis = v;
							_theme.symbols.bullet.nor_dis = v;
						}
						else
						{
							_theme.symbols.chevron.sel_dis = v;
							_theme.symbols.checked.sel_dis = v;
							_theme.symbols.bullet.sel_dis = v;
						}
					}
				}
			};

			ev_sb_dis(&th->symbol.color.normal_disabled, true);
			ev_sb_dis(&th->symbol.color.select_disabled, false);

			eval_state(&th->symbol.chevron, &_theme.symbols.chevron);
			eval_state(&th->symbol.bullet, &_theme.symbols.chevron);
			eval_state(&th->symbol.checkmark, &_theme.symbols.chevron);

			if(_context.eval_number(th->image.display, obj))
			{
				int v = obj;
				if(v < 0) v = 0;
				else if(v > 2) v = 2;
				_theme.image.display = v;
			}

			_tip.enabled = false;

			// tip
			if(_context.eval_number(sets->tip.enabled, obj))
				_tip.enabled = obj.to_bool();

			if(_tip.enabled)
			{
				eval_color_array(&sets->tip.normal, &_theme.tip.normal.back, &_theme.tip.normal.text);
				eval_color_array(&sets->tip.primary, &_theme.tip.primary.back, &_theme.tip.primary.text);
				eval_color_array(&sets->tip.info, &_theme.tip.info.back, &_theme.tip.info.text);
				eval_color_array(&sets->tip.success, &_theme.tip.success.back, &_theme.tip.success.text);
				eval_color_array(&sets->tip.warning, &_theme.tip.warning.back, &_theme.tip.warning.text);
				eval_color_array(&sets->tip.danger, &_theme.tip.danger.back, &_theme.tip.danger.text);

				_context.eval_color(sets->tip.border, &_theme.tip.border);

				if(_context.eval_number(sets->tip.width, obj))
				{
					uint16_t value = obj;
					if(value >= 200 && value <= 2000)
						_theme.tip.maxwidth = value;
				}

				//color.rgba(default, 255)
				if(_context.eval_number(sets->tip.opacity, obj))
					_theme.tip.opacity = Color::ToByte(obj);

				if(_context.eval_number(sets->tip.radius, obj))
					_theme.tip.radius = _theme.radius(obj);

				if(_context.eval_number(sets->tip.time, obj))
				{
					double value = obj;
					if(value >= 0.00 && value <= 10.00)
						_theme.tip.time = uint16_t(value * 1000);
				}

				if(_context.Eval(sets->tip.padding.value, obj))
					get_rect(&obj, &_theme.tip.padding);

				eval_margin(&sets->tip.padding, &_theme.tip.padding);
			}

			//New feature "showdelay" to change the menu show delay time and it is applied immediately without saving the value in the registry.
			//Gets or sets the time, in milliseconds, that the system waits before displaying a shortcut menu when the mouse cursor is over a submenu item.
			//New-Item -Path "HKCU:\Software\Control Panel\Desktop" -Name MenuShowDelay -Force -Value 200
			if(_context.eval_number(sets->showdelay, obj))
			{
				::SystemParametersInfoW(SPI_GETMENUSHOWDELAY, 0, &_showdelay[0], 0);
				_showdelay[1] = obj;

				if(_showdelay[1] > 4000)
					_showdelay[1] = 4000;

				if(_showdelay[1] != _showdelay[0])
				{
					::SystemParametersInfoW(SPI_SETMENUSHOWDELAY, _showdelay[1], nullptr, SPIF_SENDCHANGE);
				}
			}

			_theme.text.tap = dpi.value<int8_t>(_theme.text.tap);

			long font_size = -1;

			struct {
				Object name, size, weight, italic;
			}__font;

			// font
			if(th->font.value)
			{
				obj = _context.Eval(th->font.value).move();
				if(obj.is_array(true))
				{
					obj.get(1, __font.size);
					obj.get(2, __font.name);
					obj.get(3, __font.weight);
					obj.get(4, __font.italic);
				}
				else if(obj.is_number())
					__font.size = obj.move();
				else if(obj.is_string())
					__font.name = obj.move();
			}

			if(_context.Eval(th->font.name, obj) && !obj.is_empty())
				__font.name = obj.move();
			
			_context.eval_number(th->font.size, __font.size);

			_context.eval_number(th->font.weight, __font.weight);
			_context.eval_number(th->font.italic, __font.italic);

			_theme.font = {};

			NONCLIENTMETRICSW ncm = { sizeof(ncm) };

			_theme.font.lfQuality = DEFAULT_QUALITY;

			if(_theme.SystemParameters(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0, dpi.val))
			{
				std::memcpy(&_theme.font, &ncm.lfMenuFont, sizeof(ncm.lfMenuFont));
			}
			else if(S_OK != ::GetThemeSysFont(_hTheme, TMT_MENUFONT, &_theme.font))
			{
				_theme.font.lfHeight = 12;dpi.valuexx<long>(12);
				_theme.font.lfQuality = DEFAULT_QUALITY;
				string::Copy(_theme.font.lfFaceName, L"Segoe UI");
			}

			if(ver->IsWindows11OrGreater()) 
			{
				DWORD dwTextScaleFactor = 100, cbData;
				::RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Accessibility", 
							   L"TextScaleFactor", RRF_RT_DWORD, nullptr, &dwTextScaleFactor, &cbData);
				long scale = ((dpi.val + 24) * dwTextScaleFactor) / 100;
				_theme.font.lfHeight = 12 * scale / 100;
			}
					
			if(__font.name.is_string())
			{
				string value = __font.name.to_string().trim().move();
				if(!value.empty())
				{
					if(Font::Installed(value))
					{
						::memset(_theme.font.lfFaceName, 0, sizeof(_theme.font.lfFaceName));
						//::StringCchCopyW(_theme.font.lfFaceName, value.length(), value.c_str());
						string::Copy(_theme.font.lfFaceName, value.c_str(), value.length());
						FontNotFound = false;
					}
					else if(!FontNotFound)
					{
						_log.warning(L"Font name \"%s\" not found.", value.c_str());
						FontNotFound = true;
					}
				}
				_context.font.text = _theme.font.lfFaceName;
			}
			
			if(__font.size.not_default())
			{
				long value = __font.size;
				if(value < 6 )
					font_size = 6;
				else if(value > 200)
					font_size = 200;
				else
					font_size = value;
			}

			if(__font.weight.not_default())
			{
				uint32_t weight = __font.weight;
				if(weight <= 9 && weight >= 1)
					_theme.font.lfWeight = weight * 100;
			}

			if(__font.italic.not_default())
				_theme.font.lfItalic = __font.italic.to_bool();

			if(font_size >= 6)
			{
				//font_size = dpi(font_size);
				if(font_size != _theme.font.lfHeight)
				{
					auto of = std::abs(_theme.font.lfHeight);
					//auto di = font_size - of;
					_theme.font.lfHeight = font_size;
					dpi.val = (font_size * dpi.val) / of;
					//MBF(L"%d, %d, %d", dpi.val, (font_size * dpi.val) / of, di);
				}
			}
			
			if(_theme.image.enabled) 
			{
				if(th->image.color)
				{
					obj = _context.Eval(th->image.color).move();

					if(obj.is_color())
						set_color(obj, &_theme.image.color[0]);
					else if(obj.is_array() && obj.Value.Pointer)
					{
						auto ptr = obj.get_pointer();
						int argc = ptr[0];
						if(argc >= 1)
						{
							set_color(ptr[1], &_theme.image.color[0]);
							if(argc >= 2)
								set_color(ptr[2], &_theme.image.color[1]);
							if(argc >= 3)
								set_color(ptr[3], &_theme.image.color[2]);
						}
					}
				}

				if(th->image.glyph)
				{
					obj = _context.Eval(th->image.glyph).move();
					if(obj.is_string())
					{
						if(string glyph = obj.to_string().trim().move(); !glyph.empty())
							font.icon.Name = glyph.move();
					}
				}
			}

			//_theme.image.size = _theme.text.size;//16 //_theme.SystemMetrics<uint32_t>(SM_CXSMICON, 96/*dpi.val*/);
			
			_theme.scale();

			_theme.text.size = std::abs(_theme.font.lfHeight);
			_theme.image.size = _theme.text.size;


			if(font.icon.Name.empty())
			{
				font.icon.Name = FontCache::Default;
				font.icon.CharSet = SYMBOL_CHARSET;
			}

			_context.font.icon = font.icon.Name;

			font.icon.Size = _theme.image.size;
			font.icon.Quality = CLEARTYPE_QUALITY;
			font.icon.create();

			if(isHighContrast)
				_theme.mode = 2;

			if(_theme.font.lfHeight > 0)
				_theme.font.lfHeight *= -1;

			font.handle = ::CreateFontIndirectW(&_theme.font);

			font.icon10.Name = font.icon.Name;
			font.icon10.Size =int( _theme.image.size * 0.70);
			font.icon10.Quality = CLEARTYPE_QUALITY;
			font.icon10.create();

			//_log.info(L"%d %d %d", font.icon.Size, _theme.font.lfHeight, _theme.image.size);

			DC dc = hwnd.owner;
			dc.set_font(font.handle);
			TEXTMETRICW tm{};
			::GetTextMetricsW(dc, &tm);
			dc.reset_font();

			_theme.view2 = _theme.back.height() + tm.tmHeight;
			if(_theme.view2 % 2)
				_theme.view2++;

			// Erase background of entire client area.
			_hbackground = ::CreateSolidBrush(_theme.background.color.to_BGR());

			if(!composition)
				_theme.transparent = false;

			if(_theme.background.effect == 0)
			{
				if(!_theme.gradient.enabled)
					_theme.transparent = false;
			}
			
			std::string ll[]  
			{
				// Chevron Right
				"M7 16.82L6.17 16L12.17 10L6.17 3.99L7 3.17L13.82 10Z",
				// Chevron Left 
				"M12.99 16.82L13.82 16L7.82 10L13.82 3.99L12.99 3.17L6.17 10Z",
				// Checked Mark
				"M2.68 11.06C2.56 10.94 2.5 10.79 2.5 10.62C2.5 10.45 2.56 10.30 2.68 10.18C2.80 10.06 2.95 10 3.12 10C3.29 10 3.44 10.06 3.56 10.18L7.5 14.11L16.43 5.18C16.55 5.06 16.70 5 16.87 5C17.04 5 17.19 5.06 17.31 5.18C17.43 5.30 17.5 5.45 17.5 5.62C17.5 5.79 17.43 5.94 17.31 6.06L7.93 15.43C7.81 15.56 7.66 15.62 7.5 15.62C7.33 15.62 7.18 15.56 7.06 15.43Z",
				// Radio Bullet
				"M6.62 10L6.62 9.93C6.62 9.47 6.71 9.04 6.89 8.64C7.07 8.24 7.32 7.89 7.63 7.59C7.94 7.29 8.31 7.05 8.72 6.87C9.13 6.70 9.55 6.62 10 6.62C10.46 6.62 10.90 6.70 11.31 6.88C11.72 7.06 12.08 7.30 12.39 7.60C12.69 7.91 12.93 8.27 13.11 8.68C13.29 9.09 13.37 9.53 13.37 10C13.37 10.46 13.29 10.90 13.11 11.31C12.93 11.72 12.69 12.08 12.39 12.39C12.08 12.69 11.72 12.93 11.31 13.11C10.90 13.29 10.46 13.37 10 13.37C9.53 13.37 9.09 13.29 8.68 13.11C8.27 12.93 7.91 12.69 7.60 12.39C7.30 12.08 7.06 11.72 6.88 11.31C6.70 10.90 6.62 10.46 6.62 10Z"
			};

			std::string svg_begin = "<svg viewBox='0 0 20 20'><path d='";

			auto esvg = [=](std::string *data, Theme::state_t const &st, symbole_tag &sy, bool normal_only = false)
			{
				char fmt[30]{};
				::StringCchPrintfA(fmt, 30, "' fill='#%0.6x'/></svg>", st.nor.to_RGB());
				std::string sr = svg_begin + *data + fmt;
				if(PlutoVG plutovg(sr.c_str(), (int)sr.length(), _theme.image.size, _theme.image.size, dpi.val); plutovg)
				{
					sy.normal= plutovg.tobitmap();
					if(!normal_only)
					{
						sy.select = plutovg.tobitmap(st.sel);
						sy.normal_disabled = plutovg.tobitmap(st.nor_dis);
						sy.select_disabled = plutovg.tobitmap(st.sel_dis);
					}
				}
			};

			esvg(&ll[2], _theme.symbols.checked, symbol.checked);
			esvg(&ll[3], _theme.symbols.bullet, symbol.bullet);

			esvg(&ll[is_layoutRTL ? 1 : 0], _theme.symbols.chevron, symbol.chevron);
			
			if(symbol.chevron.normal)
			{
				symbol.chevron.size.cx = _theme.image.size;
				symbol.chevron.size.cy = _theme.image.size;

				auto popup = [&](HBITMAP &hbmp)
				{
					BITMAP bmp{};
					if(Bitmap::Info(hbmp, bmp))
					{
						auto w = bmp.bmWidth;
						auto h = bmp.bmHeight;
						auto b = (uint8_t *)bmp.bmBits;

						int bottom = 0;
						int left = w; 
						int right = 0;
						int top = h;

						for(int y = 0; y < h; y++)
						{
							for(int x = 0; x < w; x++)
							{
								auto a = (b + (x * 4))[3];
								if(a > 0)
								{
									if(x < left) left = x;
									if(x >= right) right = x + 1;
									if(y < top) top = y;
									if(y >= bottom) bottom = y + 1;
								}
							}
							b += (w * 4);
						}
	
						if(left < right && top < bottom)
						{
							SIZE trim = { right - left, bottom - top };
							if(auto hbitmap = dc.CreateDIBSection(trim.cx, trim.cy); hbitmap)
							{
								DC dcmem(dc.CreateCompatibleDC(), 1);
								dcmem.select_bitmap(hbitmap);
								dcmem.bitblt({ 0, 0, trim.cx, trim.cy }, hbmp, left, top);
								dcmem.restore_bitmap();
								::DeleteObject(hbmp);
								hbmp = hbitmap;
								symbol.chevron.size = trim;
							}
						}
					}
				};

				popup(symbol.chevron.normal);
				popup(symbol.chevron.normal_disabled);
				popup(symbol.chevron.select);
				popup(symbol.chevron.select_disabled);
			}
		}

		bool ContextMenu::is_excluded() 
		{
			auto initializer = Initializer::instance;
			auto sets = &_cache->settings;

			if(_context.eval_bool(sets->exclude.value))
				return true;

			Object obj;

			auto is_exc = [=](const Object &obj, const string &name)
			{
				if(name.empty() || obj.is_null())
					return false;

				if(obj.is_array())
				{
					auto ptr = obj.ptr();
					int ac = ptr[0];
					for(auto i = 0; i < ac; i++)
					{
						if(name.equals(ptr[i + 1].to_string(), name))
							return true;
					}
				}
				else if(name.equals(obj.to_string()))
					return true;

				return false;
			};

			if(_context.Eval(sets->exclude.process, obj))
			{
				if(is_exc(obj, initializer->process.name))
					return true;
			}

			if(_context.Eval(sets->exclude.window, obj))
			{
				auto hWnd = hwnd.owner;
				auto hWnd_owner = ::GetAncestor(hWnd, GA_ROOTOWNER);
				string name;
				if(auto h = hWnd_owner ? hWnd_owner : hWnd; h)
					name = Window::class_name(h).move();

				if(is_exc(obj, name))
					return true;
			}

			return false;
		}

		HMENU ContextMenu::MenuHandle() const { return _hMenu; }

		StudioCaptureMetadata ContextMenu::capture_metadata() const
		{
			StudioCaptureMetadata metadata;
			auto copy = [](const string &value) -> std::wstring
			{
				return value.empty() ? std::wstring{} :
					std::wstring(value.c_str(), value.length());
			};

			if(_cache)
			{
				metadata.configPath = _cache->config_path;
				metadata.runtimeGeneration = _cache->runtime_generation;
				metadata.hasEffectiveSettings = true;
				metadata.modifyItemsEnabled = _settings.modify_items.enabled;
				metadata.modifyItemsTitle = _settings.modify_items.title;
				metadata.modifyItemsVisibility = _settings.modify_items.visibility;
				metadata.modifyItemsParent = _settings.modify_items.parent;
				metadata.modifyItemsSeparator = _settings.modify_items.separator;
				metadata.modifyItemsKeys = _settings.modify_items.keys;
				metadata.modifyItemsImage = _settings.modify_items.image;
				metadata.modifyItemsPosition = _settings.modify_items.position;
				metadata.removeDuplicate = _settings.modify_items.remove.duplicate;
				metadata.removeDisabled = _settings.modify_items.remove.disabled;
				metadata.removeSeparator = _settings.modify_items.remove.separator;
				metadata.newItemsEnabled = _settings.new_items.enabled;
				metadata.newItemsImage = _settings.new_items.image;
				metadata.newItemsKeys = _settings.new_items.keys;
			}

			const wchar_t *contextName = L"unknown";
			switch(Selected.Window.id)
			{
				case WINDOW_UI: contextName = L"ui"; break;
				case WINDOW_SYSMENU: contextName = L"system"; break;
				case WINDOW_EDIT: contextName = L"edit"; break;
				case WINDOW_START: contextName = L"start"; break;
				case WINDOW_TASKBAR: contextName = L"taskbar"; break;
				case WINDOW_DESKTOP: contextName = L"desktop"; break;
				case WINDOW_EXPLORER: contextName = L"explorer"; break;
				case WINDOW_EXPLORER_TREE: contextName = L"explorer-tree"; break;
				case WINDOW_COMPUTER: contextName = L"computer"; break;
				case WINDOW_RECYCLEBIN: contextName = L"recycle-bin"; break;
				case WINDOW_LIBRARIES: contextName = L"libraries"; break;
				case WINDOW_HOME: contextName = L"home"; break;
				case WINDOW_QUICK_ACCESS: contextName = L"quick-access"; break;
				default: break;
			}
			metadata.context = contextName;
			metadata.context += Selected.Background ? L".background" : L".selection";

			// Preserve the complete, already-evaluated native selection.  The Studio
			// preview runs on a different machine and must not infer file-system
			// kinds, drive media, or window state from the captured path strings.
			metadata.selection.background = Selected.Background;
			metadata.selection.windowId = static_cast<int32_t>(Selected.Window.id);
			metadata.selection.mode = static_cast<int32_t>(Selected.Mode);
			metadata.selection.front = Selected.front;
			metadata.selection.windowDesktop = Selected.Window.desktop;
			metadata.selection.windowExplorer = Selected.Window.explorer;
			metadata.selection.windowExplorerTree = Selected.Window.explorer_tree;
			metadata.selection.parent = copy(Selected.Parent);
			metadata.selection.parentRaw = copy(Selected.ParentRaw);
			metadata.selection.directory = copy(Selected.Directory);
			metadata.selection.types.resize(FSO_MAX);
			for(size_t index = 0; index < FSO_MAX; ++index)
				metadata.selection.types[index] = Selected.Types[index];
			metadata.selection.items.reserve((std::min)(Selected.Items.size(),
				StudioCapture::MaxSelectionItems));
			for(auto item : Selected.Items)
			{
				if(metadata.selection.items.size() >= StudioCapture::MaxSelectionItems)
					break;
				if(!item)
					continue;
				StudioCaptureSelectionItem captured;
				captured.path = copy(item->Path);
				captured.raw = copy(item->Raw);
				captured.name = copy(item->Name);
				captured.title = copy(item->Title);
				captured.extension = copy(item->Extension);
				captured.type = static_cast<int32_t>(item->Type);
				captured.group = static_cast<int32_t>(item->Group);
				captured.readOnly = item->ReadOnly;
				captured.hidden = item->Hidden;
				captured.isLink = item->IsLink;
				metadata.selection.items.push_back(std::move(captured));
			}

			// Keep the semantic selection category separate from the host window
			// name.  Rules commonly distinguish a file, directory background, or
			// drive background even when all three are opened from Explorer.
			if(Selected.is_taskbar())
				metadata.contextCategory = L"taskbar";
			else if(Selected.is_desktop_window() || Selected.Types[FSO_DESKTOP])
				metadata.contextCategory = L"desktop";
			else if(Selected.Background)
			{
				if(Selected.Types[FSO_BACK_DIRECTORY])
					metadata.contextCategory = L"dir.back";
				else if(Selected.Types[FSO_BACK_DRIVE])
					metadata.contextCategory = L"drive.back";
				else if(Selected.Types[FSO_BACK_NAMESPACE])
					metadata.contextCategory = L"namespace.back";
				else
					metadata.contextCategory = L"background";
			}
			else if(Selected.Types[FSO_FILE])
				metadata.contextCategory = L"file";
			else if(Selected.Types[FSO_DIRECTORY])
				metadata.contextCategory = L"dir";
			else if(Selected.Types[FSO_DRIVE])
				metadata.contextCategory = L"drive";
			else if(Selected.Types[FSO_NAMESPACE])
				metadata.contextCategory = L"namespace";
			else
				metadata.contextCategory = L"unknown";

			for(auto item : Selected.Items)
			{
				if(metadata.paths.size() >= 128)
					break;
				if(item && !item->Path.empty())
					metadata.paths.push_back(copy(item->Path));
			}
			if(metadata.paths.empty() && !Selected.Directory.empty())
				metadata.paths.push_back(copy(Selected.Directory));
			return metadata;
		}

		ContextMenu::CaptureEvaluationScope::~CaptureEvaluationScope() noexcept
		{
			if(owner)
				owner->retain_capture_evaluation(item);
		}

		void ContextMenu::retain_capture_evaluation(menuitem_t *item) noexcept
		{
			if(!item || !item->native_menu || !_studio_capture.IsActive())
				return;

			try
			{
				StudioCaptureTrace evaluated;
				for(const auto &value : item->trace)
				{
					if(value.size() < 7 || value.compare(0, 7, L"static.") != 0)
						continue;
					evaluated.push_back(value);
				}
				const auto hasEvidence = !item->evidence.empty();
				if(evaluated.empty() && !hasEvidence)
					return;

				const StudioCaptureTraceKey key{item->native_menu, item->native_index};
				const auto found = _studio_evaluated_traces.find(key);
				const auto evidenceFound = _studio_evaluated_evidence.find(key);
				const auto traceMissing = found == _studio_evaluated_traces.end();
				const auto evidenceMissing = evidenceFound == _studio_evaluated_evidence.end();
				if((traceMissing && !evaluated.empty()) || (evidenceMissing && hasEvidence))
				if(_studio_evaluated_traces.size() >= kMaxRetainedCaptureEvaluations ||
					_studio_evaluated_evidence.size() >= kMaxRetainedCaptureEvaluations)
				{
					_studio_capture.Fail("CAPTURE_TRACE_MEMORY",
						"The native capture exceeded its retained trace limit.");
					return;
				}
				if(found != _studio_evaluated_traces.end())
					found->second = std::move(evaluated);
				else if(!evaluated.empty())
					_studio_evaluated_traces.emplace(key, std::move(evaluated));
				if(evidenceFound != _studio_evaluated_evidence.end())
					evidenceFound->second = item->evidence;
				else if(hasEvidence)
					_studio_evaluated_evidence.emplace(key, item->evidence);
			}
			catch(...)
			{
				_studio_capture.Fail("CAPTURE_TRACE_MEMORY",
					"The native capture could not retain all evaluated rule outcomes.");
			}
		}

		void ContextMenu::overlay_capture_evaluations(menuitem_t *item)
		{
			if(!item)
				return;

			if(item->native_menu && _studio_capture.IsActive())
			{
				const bool staticRulesEnabled = _cache && !_cache->statics.empty();
				const StudioCaptureTraceKey key{item->native_menu, item->native_index};
				const auto found = _studio_evaluated_traces.find(key);
				const auto evidenceFound = _studio_evaluated_evidence.find(key);
				if(found != _studio_evaluated_traces.end())
				{
					try
					{
						// Static outcomes are the part that can disappear from the
						// display tree.  Give them priority over bookkeeping entries
						// when the bounded trace vector is full.
						StudioCaptureTrace merged;
						for(const auto &value : found->second)
						{
							if(merged.size() >= kMaxCaptureTraceEntries)
								break;
							merged.push_back(value);
						}
						for(const auto &value : item->trace)
						{
							if(value.size() >= 7 && value.compare(0, 7, L"static.") == 0)
								continue;
							if(merged.size() >= kMaxCaptureTraceEntries)
								break;
							merged.push_back(value);
						}
						item->trace = std::move(merged);
					}
					catch(...)
					{
						_studio_capture.Fail("CAPTURE_TRACE_MEMORY",
							"The native capture could not merge retained rule outcomes.");
					}
				}
				if(evidenceFound != _studio_evaluated_evidence.end())
				{
					try
					{
						item->evidence = evidenceFound->second;
					}
					catch(...)
					{
						_studio_capture.Fail("CAPTURE_TRACE_MEMORY",
							"The native capture could not merge structured rule outcomes.");
					}
				}
				if(found == _studio_evaluated_traces.end())
				{
					bool filtered = false;
					for(const auto &value : item->trace)
						filtered = filtered || (value.size() >= 7 &&
							value.compare(0, 7, L"remove.") == 0);
					if(filtered && !_studio_evaluated_traces.empty())
						CaptureTrace(item->trace, L"capture.static", false,
							L"not evaluated; filtered during native enumeration");
					else if(!filtered && staticRulesEnabled &&
						!_studio_capture_active_during_static_evaluation)
						CaptureTrace(item->trace,
							L"capture.static unavailable; capture armed after evaluation");
				}
			}

			for(auto child : item->items)
				overlay_capture_evaluations(child);
		}

		bool ContextMenu::publish_original_capture_if_armed()
		{
			const auto epoch = _studio_capture.ActiveEpoch();
			if(!epoch || _studio_original_published_epoch == epoch || !_studio_real_enumeration_complete ||
				!_studio_static_evaluation_complete ||
				::GetPropW(hwnd.owner, UxSubclass) != 0 ||
				!_studio_capture.WantsOriginal() || !_hMenu_original ||
				!::IsMenu(_hMenu_original))
				return false;
			try
			{
				CaptureTraceScope traceScope(&_studio_capture);

				// The real enumeration has already sent WM_INITMENUPOPUP for every
				// reachable submenu.  The getter pass is therefore read-only and does
				// not re-enter the owner window or run the popup initializer again.
				std::unique_ptr<menuitem_t> original_tree(new menuitem_t);
				original_tree->type = 10;
				build_system_menuitems(_hMenu_original, original_tree.get(), true, true);
				overlay_capture_evaluations(original_tree.get());
				const auto metadata = capture_metadata();
				if(!_studio_capture.PublishOriginal(original_tree.get(), metadata))
					return false;
				_studio_original_published_epoch = epoch;
				_studio_evaluated_traces.clear();
				_studio_evaluated_evidence.clear();
				return true;
			}
			catch(...)
			{
				_studio_capture.FailIfEpoch(epoch, "CAPTURE_METADATA",
					"The native capture could not collect menu context metadata.");
				return false;
			}
		}

		void ContextMenu::begin_appearance_paint(WND *wnd) noexcept
		{
			if(!wnd)
				return;

			auto &cache = wnd->studio_appearance;
			cache.clear();
			cache.epoch = _studio_capture.ActiveEpoch();
			if(!cache.epoch)
				return;

			cache.inPaint = true;
			auto fail = [&cache](std::string_view reason) noexcept
			{
				cache.failed = true;
				cache.geometryReady = false;
				try
				{
					cache.failure.assign(reason.data(), reason.size());
				}
				catch(...)
				{
					cache.failure.clear();
				}
			};

			try
			{
				HMENU menuHandle = wnd->hMenu;
				if(!menuHandle)
				{
					auto candidate = reinterpret_cast<HMENU>(
						::SendMessageW(wnd->handle, MN_GETHMENU, 0, 0));
					if(::IsMenu(candidate))
					{
						menuHandle = candidate;
						wnd->hMenu = candidate;
					}
				}
				if(!menuHandle || !::IsMenu(menuHandle))
				{
					fail("The native popup menu handle was unavailable during paint.");
					return;
				}

				long width = wnd->width;
				long height = wnd->height;
				if(width <= 0 || height <= 0)
				{
					RECT client{};
					if(!::GetClientRect(wnd->handle, &client))
					{
						fail("The native popup client rectangle was unavailable during paint.");
						return;
					}
					width = client.right - client.left;
					height = client.bottom - client.top;
				}
				if(width <= 0 || height <= 0 ||
					static_cast<uint32_t>(width) > kMaxAppearanceWidth ||
					static_cast<uint32_t>(height) > kMaxAppearanceHeight ||
					static_cast<uint64_t>(width) * static_cast<uint64_t>(height) >
						kMaxAppearancePixels)
				{
					fail("The native popup dimensions exceed the bounded appearance contract.");
					return;
				}

				const auto itemCount = ::GetMenuItemCount(menuHandle);
				if(itemCount < 0 || static_cast<size_t>(itemCount) > kMaxAppearanceRows)
				{
					fail("The native popup contains too many rows for appearance capture.");
					return;
				}

				cache.menu = menuHandle;
				cache.width = width;
				cache.height = height;
				RECT windowRect{};
				if(!::GetWindowRect(wnd->handle, &windowRect) ||
					!::ClientToScreen(wnd->handle, &cache.clientOrigin))
				{
					fail("The native popup client origin was unavailable during paint.");
					return;
				}
				cache.clientOrigin.x -= windowRect.left;
				cache.clientOrigin.y -= windowRect.top;
				cache.scrollInset = wnd->has_scroll ? dpi(14) : 0;
				if(cache.scrollInset * 2 >= height)
				{
					fail("The native popup has no content viewport between scroll controls.");
					return;
				}
				cache.dpi = Theme::GetDpi(wnd->handle);
				cache.rows.resize(static_cast<size_t>(itemCount));
				for(size_t position = 0; position < cache.rows.size(); ++position)
				{
					auto &row = cache.rows[position];
					row.position = static_cast<uint32_t>(position);
					MENUITEMINFOW info{sizeof(info)};
					info.fMask = MIIM_ID;
					if(!::GetMenuItemInfoW(menuHandle, static_cast<UINT>(position),
						TRUE, &info))
					{
						fail("A native popup row could not be described during paint.");
						return;
					}
					row.itemId = info.wID;
				}
			}
			catch(...)
			{
				fail("The native popup appearance cache could not be initialized.");
			}
		}

		void ContextMenu::finish_appearance_paint(WND *wnd) noexcept
		{
			if(!wnd)
				return;

			auto &cache = wnd->studio_appearance;
			if(!cache.inPaint)
				return;
			cache.inPaint = false;
			if(!_studio_capture.IsActive() || cache.failed)
				return;

			auto fail = [&cache](std::string_view reason) noexcept
			{
				cache.failed = true;
				cache.geometryReady = false;
				try
				{
					cache.failure.assign(reason.data(), reason.size());
				}
				catch(...)
				{
					cache.failure.clear();
				}
			};

			try
			{
				if(!cache.menu || cache.rows.size() > kMaxAppearanceRows)
				{
					fail("The native popup geometry cache has no valid menu.");
					return;
				}
				const auto itemCount = ::GetMenuItemCount(cache.menu);
				if(itemCount < 0 || static_cast<size_t>(itemCount) != cache.rows.size())
				{
					fail("The native popup row geometry changed during paint.");
					return;
				}

				for(size_t position = 0; position < cache.rows.size(); ++position)
				{
					auto &row = cache.rows[position];
					RECT rect{};
					if(!GetMenuItemClientRect(wnd->handle, cache.menu,
						static_cast<UINT>(position), rect))
					{
						fail("A native popup row rectangle was unavailable after paint.");
						return;
					}
					row.rect = rect;
					::OffsetRect(&rect, cache.clientOrigin.x, cache.clientOrigin.y);
					RECT clipped{};
					row.visible = IntersectAppearanceRect(rect, cache.width,
						cache.height, clipped, cache.scrollInset);
					if(row.visible && !row.painted)
					{
						fail("A visible native popup row was not rendered by its owner callback.");
						return;
					}
					if(row.painted &&
						(row.pixelWidth != rect.right - rect.left ||
						row.pixelHeight != rect.bottom - rect.top))
					{
						fail("A native popup row changed size after its pixels were rendered.");
						return;
					}
				}
				cache.geometryReady = true;
			}
			catch(...)
			{
				fail("The native popup appearance geometry could not be finalized.");
			}
		}

		void ContextMenu::cache_painted_row(DRAWITEMSTRUCT *di,
			const uint8_t *renderedPixels, long renderedWidth,
			long renderedHeight) noexcept
		{
			if(!di || !renderedPixels || renderedWidth <= 0 || renderedHeight <= 0 ||
				!_studio_capture.IsActive())
				return;

			auto fail = [](StudioAppearanceCache &cache,
				std::string_view reason) noexcept
			{
				cache.failed = true;
				cache.geometryReady = false;
				try
				{
					cache.failure.assign(reason.data(), reason.size());
				}
				catch(...)
				{
					cache.failure.clear();
				}
			};

			try
			{
				const auto menuHandle = reinterpret_cast<HMENU>(di->hwndItem);
				if(!menuHandle)
					return;

				WND *wnd = nullptr;
				if(auto mapped = map_menu_wnd.find(menuHandle);
					mapped != map_menu_wnd.end())
				{
					if(auto found = _map.find(mapped->second.hwnd);
						found != _map.end())
						wnd = &found->second;
				}
				if(!wnd)
				{
					for(auto &entry : _map)
					{
						if(entry.second.hMenu == menuHandle)
						{
							wnd = &entry.second;
							break;
						}
					}
				}
				if(!wnd)
					return;

				auto &cache = wnd->studio_appearance;
				if((!cache.inPaint && !cache.geometryReady) || cache.failed ||
					cache.epoch != _studio_capture.ActiveEpoch() || cache.menu != menuHandle ||
					cache.rows.empty())
					return;

				const auto expectedWidth = di->rcItem.right - di->rcItem.left;
				const auto expectedHeight = di->rcItem.bottom - di->rcItem.top;
				if(expectedWidth != renderedWidth || expectedHeight != renderedHeight)
				{
					fail(cache, "The native owner-draw row dimensions changed before caching.");
					return;
				}

				size_t position = UINT32_MAX;
				for(size_t index = 0; index < cache.rows.size(); ++index)
				{
					auto &row = cache.rows[index];
					if(row.itemId != di->itemID)
						continue;
					RECT geometry{};
					if(GetMenuItemClientRect(wnd->handle, menuHandle,
						static_cast<UINT>(index), geometry) &&
						geometry.left == di->rcItem.left &&
						geometry.top == di->rcItem.top &&
						geometry.right == di->rcItem.right &&
						geometry.bottom == di->rcItem.bottom)
					{
						position = index;
						break;
					}
				}
				if(position == UINT32_MAX)
				{
					for(size_t index = 0; index < cache.rows.size(); ++index)
					{
						auto &row = cache.rows[index];
						if(!row.painted && row.itemId == di->itemID)
						{
							position = index;
							break;
						}
					}
				}
				if(position == UINT32_MAX)
				{
					for(size_t index = 0; index < cache.rows.size(); ++index)
					{
						auto &row = cache.rows[index];
						if(row.painted)
							continue;
						RECT geometry{};
						if(GetMenuItemClientRect(wnd->handle, menuHandle,
							static_cast<UINT>(index), geometry) &&
							geometry.left == di->rcItem.left &&
							geometry.top == di->rcItem.top &&
							geometry.right == di->rcItem.right &&
							geometry.bottom == di->rcItem.bottom)
						{
							position = index;
							break;
						}
					}
				}
				if(position == UINT32_MAX)
				{
					for(size_t index = 0; index < cache.rows.size(); ++index)
					{
						auto &row = cache.rows[index];
						if(!row.painted || row.itemId != di->itemID)
							continue;
						RECT geometry{};
						if(GetMenuItemClientRect(wnd->handle, menuHandle,
							static_cast<UINT>(index), geometry) &&
							geometry.left == di->rcItem.left &&
							geometry.top == di->rcItem.top &&
							geometry.right == di->rcItem.right &&
							geometry.bottom == di->rcItem.bottom)
							return;
					}

					fail(cache, "A native owner-draw row could not be mapped to its menu item.");
					return;
				}

				const auto byteCount = static_cast<size_t>(renderedWidth) *
					static_cast<size_t>(renderedHeight) * 4U;
				const auto replacedBytes = cache.rows[position].pixels.size();
				if(byteCount > kMaxAppearanceBytes ||
					cache.pixelCount - replacedBytes > kMaxAppearanceBytes - byteCount)
				{
					fail(cache, "The native popup row pixels exceed the appearance budget.");
					return;
				}
				size_t retainedBytes = 0;
				for(const auto &entry : _map)
				{
					const auto retained = entry.second.studio_appearance.pixelCount;
					if(retained > kMaxRetainedAppearanceBytes -
						(std::min)(retainedBytes, kMaxRetainedAppearanceBytes))
					{
						fail(cache, "Retained native popup appearance pixels exceed the context budget.");
						return;
					}
					retainedBytes += retained;
				}
				if(retainedBytes - replacedBytes > kMaxRetainedAppearanceBytes - byteCount)
				{
					fail(cache, "Retained native popup appearance pixels exceed the context budget.");
					return;
				}

				auto &row = cache.rows[position];
				row.rect = di->rcItem;
				row.pixelWidth = renderedWidth;
				row.pixelHeight = renderedHeight;
				row.pixels.resize(byteCount);
				std::memcpy(row.pixels.data(), renderedPixels, byteCount);
				for(size_t index = 0; index < row.pixels.size(); index += 4)
				{
					auto *pixel = row.pixels.data() + index;
					// Preserve alpha from buffered text/vector/image painters, including
					// black glyphs. Only legacy GDI colors without alpha need repair.
					if(!composition || (pixel[3] == 0 &&
						(pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0)))
						pixel[3] = 0xFF;
					NormalizePremultiplied(pixel);
				}
				row.painted = true;
				cache.pixelCount = cache.pixelCount - replacedBytes + byteCount;
			}
			catch(...)
			{
				if(di)
				{
					const auto menuHandle = reinterpret_cast<HMENU>(di->hwndItem);
					if(auto mapped = map_menu_wnd.find(menuHandle);
						mapped != map_menu_wnd.end())
					{
						if(auto found = _map.find(mapped->second.hwnd);
							found != _map.end())
							fail(found->second.studio_appearance,
								"The native popup row pixels could not be cached.");
					}
				}
			}
		}

		void ContextMenu::clear_appearance_cache(HMENU hMenu) noexcept
		{
			if(!hMenu)
				return;
			try
			{
				if(auto mapped = map_menu_wnd.find(hMenu);
					mapped != map_menu_wnd.end())
				{
					if(auto found = _map.find(mapped->second.hwnd);
						found != _map.end())
						found->second.studio_appearance.clear();
				}
				for(auto &entry : _map)
				{
					if(entry.second.hMenu == hMenu ||
						entry.second.studio_appearance.menu == hMenu)
						entry.second.studio_appearance.clear();
				}
			}
			catch(...)
			{
			}
		}

		StudioCaptureAppearance ContextMenu::capture_popup_appearance(WND *wnd,
			const std::vector<MenuItemInfo *> &entries,
			std::wstring_view parentPath)
		{
			StudioCaptureAppearance appearance;
			appearance.desktopEffectsOmitted =
				composition && _theme.background.effect >= 2 &&
				_theme.background.opacity < 0xFF;

			auto unavailable = [&appearance](std::string_view reason)
			{
				appearance.available = false;
				appearance.width = 0;
				appearance.height = 0;
				appearance.pixels.clear();
				appearance.rows.clear();
				appearance.unavailableReason.assign(reason.data(), reason.size());
				return appearance;
			};

			if(!wnd || !wnd->handle || !::IsWindow(wnd->handle))
				return unavailable("The native popup window is unavailable.");

			const auto &cache = wnd->studio_appearance;
			if(!cache.epoch || cache.epoch != _studio_capture.ActiveEpoch())
				return unavailable("The native popup pixels belong to an earlier capture; recapture after the menu paints.");
			appearance.dpi = cache.dpi != 0 ? cache.dpi : Theme::GetDpi(wnd->handle);
			if(cache.failed)
				return unavailable(cache.failure.empty()
					? "The native popup appearance cache failed." : cache.failure);
			if(cache.inPaint || !cache.geometryReady)
				return unavailable("The native popup appearance was not complete after paint.");
			if(!cache.menu || cache.menu != wnd->hMenu ||
				cache.rows.size() > kMaxAppearanceRows)
				return unavailable("The native popup appearance cache has no stable menu.");
			if(entries.size() != cache.rows.size())
				return unavailable("The native popup entries and painted rows differ.");
			if(appearance.dpi < 48 || appearance.dpi > 768)
				return unavailable("The native popup DPI is outside the bounded appearance contract.");
			RECT windowRect{};
			POINT clientOrigin{};
			if(!::GetWindowRect(wnd->handle, &windowRect) ||
				!::ClientToScreen(wnd->handle, &clientOrigin) ||
				clientOrigin.x - windowRect.left != cache.clientOrigin.x ||
				clientOrigin.y - windowRect.top != cache.clientOrigin.y ||
				windowRect.right - windowRect.left != cache.width ||
				windowRect.bottom - windowRect.top != cache.height)
				return unavailable("The native popup frame changed after its rows were painted.");

			const long margin = 50;
			const auto width = cache.width;
			const auto height = cache.height;
			const auto outputWidth = width + margin + margin;
			const auto outputHeight = height + margin + margin;
			if(width <= 0 || height <= 0 || outputWidth <= 0 || outputHeight <= 0 ||
				static_cast<uint32_t>(outputWidth) > kMaxAppearanceWidth ||
				static_cast<uint32_t>(outputHeight) > kMaxAppearanceHeight ||
				static_cast<uint64_t>(outputWidth) *
					static_cast<uint64_t>(outputHeight) > kMaxAppearancePixels)
				return unavailable("The native popup appearance dimensions exceed the bounded contract.");

			appearance.width = static_cast<uint32_t>(outputWidth);
			appearance.height = static_cast<uint32_t>(outputHeight);
			appearance.pixels.assign(static_cast<size_t>(outputWidth) *
				static_cast<size_t>(outputHeight) * 4U, 0);

			WND previewFrame;
			previewFrame.ctx = this;
			previewFrame.width = width;
			previewFrame.height = height;
			if(!draw_layer(&previewFrame, {outputWidth, outputHeight},
				static_cast<int>(margin), true) || !previewFrame.layer.hbitmap)
				return unavailable("The native popup frame could not be rendered.");
			auto_gdi<HBITMAP> frameBitmap(previewFrame.layer.hbitmap);

			std::vector<uint8_t> layerPixels;
			if(!GetTopDownBitmap(frameBitmap.get(), outputWidth,
				outputHeight, layerPixels))
				return unavailable("The native popup frame pixels could not be read.");

			for(size_t index = 0; index < appearance.pixels.size(); index += 4)
				CompositePremultiplied(appearance.pixels.data() + index,
					layerPixels.data() + index);

			appearance.rows.reserve(cache.rows.size());
			for(const auto &cached : cache.rows)
			{
				RECT currentRect{};
				if(!GetMenuItemClientRect(wnd->handle, cache.menu,
					static_cast<UINT>(cached.position), currentRect) ||
					!::EqualRect(&currentRect, &cached.rect))
					return unavailable("The popup viewport changed after its rows were painted.");
				if(!cached.visible)
					continue;
				if(!cached.painted || cached.position >= entries.size() ||
					!entries[cached.position])
					return unavailable("A visible native popup row had no matching semantic entry.");
				RECT windowRow = cached.rect;
				::OffsetRect(&windowRow, cache.clientOrigin.x, cache.clientOrigin.y);
				RECT clipped{};
				if(!IntersectAppearanceRect(windowRow, width, height, clipped, cache.scrollInset))
					continue;

				const auto sourceWidth = cached.pixelWidth;
				const auto sourceHeight = cached.pixelHeight;
				if(sourceWidth <= 0 || sourceHeight <= 0 ||
					cached.pixels.size() != static_cast<size_t>(sourceWidth) *
						static_cast<size_t>(sourceHeight) * 4U)
					return unavailable("A visible native popup row had incomplete pixels.");

				const auto sourceLeft = clipped.left - windowRow.left;
				const auto sourceTop = clipped.top - windowRow.top;
				const auto copyWidth = clipped.right - clipped.left;
				const auto copyHeight = clipped.bottom - clipped.top;
				for(long y = 0; y < copyHeight; ++y)
				{
					for(long x = 0; x < copyWidth; ++x)
					{
						const auto *source = cached.pixels.data() +
							((static_cast<size_t>(sourceTop + y) * sourceWidth) +
							static_cast<size_t>(sourceLeft + x)) * 4U;
						auto *destination = appearance.pixels.data() +
							((static_cast<size_t>(margin + clipped.top + y) *
								outputWidth) +
							static_cast<size_t>(margin + clipped.left + x)) * 4U;
						CompositePremultiplied(destination, source);
					}
				}

				StudioCaptureAppearanceRow row;
				row.entryId = StudioCapture::FinalEntryId(
					entries[cached.position], parentPath, cached.position);
				if(row.entryId.empty())
					return unavailable("A visible native popup row had no stable entry id.");
				row.x = static_cast<uint32_t>(margin + clipped.left);
				row.y = static_cast<uint32_t>(margin + clipped.top);
				row.width = static_cast<uint32_t>(copyWidth);
				row.height = static_cast<uint32_t>(copyHeight);
				appearance.rows.push_back(std::move(row));
			}

			if(cache.scrollInset != 0)
			{
				// Share the native arrow painter while keeping the arrow strips out
				// of row imagery and edit hit targets. This DIB has no desktop source.
				BITMAPINFO info{};
				info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
				info.bmiHeader.biWidth = width;
				info.bmiHeader.biHeight = -height;
				info.bmiHeader.biPlanes = 1;
				info.bmiHeader.biBitCount = 32;
				info.bmiHeader.biCompression = BI_RGB;
				uint8_t *bits = nullptr;
				auto_gdi<HBITMAP> bitmap(::CreateDIBSection(nullptr, &info, DIB_RGB_COLORS,
					reinterpret_cast<void **>(&bits), nullptr, 0));
				DC memory(::CreateCompatibleDC(nullptr), 1);
				if(!bitmap || !bits || !memory)
					return unavailable("The native scroll controls could not allocate an offscreen surface.");
				auto previous = ::SelectObject(memory, bitmap.get());
				if(!previous || previous == HGDI_ERROR)
					return unavailable("The native scroll control surface could not be selected.");
				std::memset(bits, 0, static_cast<size_t>(width) * height * 4U);
				draw_scroll_arrows(memory, width, height);
				::GdiFlush();
				for(long y = 0; y < height; ++y)
					for(long x = 0; x < width; ++x)
					{
						auto *source = bits + (static_cast<size_t>(y) * width + x) * 4U;
						NormalizePremultiplied(source);
						auto *target = appearance.pixels.data() +
							(static_cast<size_t>(y + margin) * outputWidth + x + margin) * 4U;
						CompositePremultiplied(target, source);
					}
				::SelectObject(memory, previous);
			}

			appearance.available = true;
			appearance.unavailableReason.clear();
			return appearance;
		}

		void ContextMenu::cancel_appearance_capture_timer() noexcept
		{
			if(_studio_appearance_timer_owner && _studio_appearance_timer_id)
				::KillTimer(_studio_appearance_timer_owner, _studio_appearance_timer_id);
			_studio_appearance_timer_owner = nullptr;
			_studio_appearance_timer_hwnd = nullptr;
			_studio_appearance_timer_id = 0;
		}

		void ContextMenu::schedule_appearance_capture(HWND hWnd) noexcept
		{
			try
			{
				if(!_studio_capture.IsActive() || !hWnd || !hwnd.owner ||
					!::IsWindow(hWnd))
				{
					cancel_appearance_capture_timer();
					return;
				}
				if(_studio_appearance_published.find(hWnd) !=
					_studio_appearance_published.end())
					return;
				if(!_studio_popup_order.empty() &&
					_studio_popup_order.back() != hWnd)
					return;
				if(_studio_appearance_timer_owner == hwnd.owner &&
					_studio_appearance_timer_hwnd == hWnd &&
					_studio_appearance_timer_id != 0)
					return;
				if(_studio_appearance_timer_id != 0)
					cancel_appearance_capture_timer();

				const auto timerId = NextAppearanceTimerId();
				if(!::SetTimer(hwnd.owner, timerId, kAppearanceTimerDelayMs, nullptr))
				{
					_studio_capture.Fail("CAPTURE_APPEARANCE_TIMER",
						"The native capture could not defer the popup appearance capture.");
					return;
				}
				_studio_appearance_timer_owner = hwnd.owner;
				_studio_appearance_timer_hwnd = hWnd;
				_studio_appearance_timer_id = timerId;
			}
			catch(...)
			{
				cancel_appearance_capture_timer();
				_studio_capture.Fail("CAPTURE_APPEARANCE_TIMER",
					"The native capture could not schedule the popup appearance capture.");
			}
		}

		void ContextMenu::publish_appearance_after_paint(HWND hWnd) noexcept
		{
			uint64_t captureEpoch = 0;
			try
			{
				captureEpoch = _studio_capture.ActiveEpoch();
				if(!captureEpoch || !hWnd)
				{
					cancel_appearance_capture_timer();
					return;
				}
				if(!_studio_popup_order.empty() &&
					_studio_popup_order.back() != hWnd)
					return;

				auto foundWnd = _map.find(hWnd);
				if(foundWnd == _map.end() || foundWnd->second.handle != hWnd)
				{
					_studio_capture.FailIfEpoch(captureEpoch, "CAPTURE_APPEARANCE",
						"The popup window disappeared before its appearance could be captured.");
					return;
				}
				WND *wnd = &foundWnd->second;
				HMENU menuHandle = wnd->hMenu;
				if(!menuHandle)
				{
					const auto candidate = reinterpret_cast<HMENU>(
						::SendMessageW(hWnd, MN_GETHMENU, 0, 0));
					if(::IsMenu(candidate))
					{
						menuHandle = candidate;
						wnd->hMenu = candidate;
					}
				}
				if(!menuHandle || !::IsMenu(menuHandle))
				{
					_studio_capture.FailIfEpoch(captureEpoch, "CAPTURE_APPEARANCE",
						"The popup menu handle did not stabilize after paint.");
					return;
				}
				auto found = _studio_final_entries.find(menuHandle);
				if(found == _studio_final_entries.end())
				{
					_studio_capture.FailIfEpoch(captureEpoch, "CAPTURE_APPEARANCE",
						"The popup semantic entries were unavailable after paint.");
					return;
				}

				std::wstring parentPath;
				if(auto menu = _menus.find(menuHandle); menu != _menus.end())
					parentPath.assign(menu->second.path.c_str(),
						menu->second.path.length());

				StudioCaptureAppearance appearance;
				if(wnd->studio_appearance.geometryReady &&
					!wnd->studio_appearance.failed)
				{
					try
					{
						appearance = capture_popup_appearance(wnd, found->second,
							parentPath);
					}
					catch(...)
					{
						appearance.dpi = wnd->studio_appearance.dpi;
						appearance.desktopEffectsOmitted =
							composition && _theme.background.effect >= 2 &&
							_theme.background.opacity < 0xFF;
						appearance.unavailableReason =
							"The native popup appearance could not be composed.";
					}
				}
				else
				{
					appearance.dpi = wnd->studio_appearance.dpi != 0
						? wnd->studio_appearance.dpi : Theme::GetDpi(hWnd);
					appearance.desktopEffectsOmitted =
						composition && _theme.background.effect >= 2 &&
						_theme.background.opacity < 0xFF;
					appearance.unavailableReason =
						wnd->studio_appearance.failure.empty()
						? "The native popup paint cache was unavailable."
						: wnd->studio_appearance.failure;
				}

				const auto metadata = capture_metadata();
				if(!_studio_capture.PublishFinal(found->second, metadata,
					parentPath, appearance, captureEpoch))
					return;
				_studio_appearance_published.insert(hWnd);
				cancel_appearance_capture_timer();
			}
			catch(...)
			{
				cancel_appearance_capture_timer();
				_studio_capture.FailIfEpoch(captureEpoch, "CAPTURE_APPEARANCE",
					"The native capture could not publish the post-paint appearance.");
			}
		}

		void ContextMenu::build_main_system_menuitems(menuitem_t *menu, bool is_root)
		{
			CaptureTraceScope traceScope(&_studio_capture);
			if(_studio_capture.IsActive())
				_studio_capture_active_during_static_evaluation = true;
			if(!menu || menu->items.empty())
				return;

			auto items = &menu->items;
			const NativeMenuConstruction::SelectionInput selection{
				&Selected, true, true};

			for(auto si : _cache->statics)
			{
				if(si->has_clsid)
					continue;

				if(!NativeMenuConstruction::static_types_match(selection, *si))
					continue;

				for(size_t i = 0; i < items->size(); i++)
				{
					bool removed = false;
					auto where_defined = false;
					auto item = items->at(i);
					CaptureEvaluationScope captureEvaluation{this, item};
					CaptureEvidenceScope evidenceScope(&item->evidence);
					try 
					{
						string location;
						this_item _this;

						_this.type = item->type;
						_this.pos = static_cast<int>(i);
						_this.checked = item->checked ? (item->radio_check ? 2 : 1) : 0;
						_this.disabled = item->disabled;
						_this.system = true;
						_this.id = item->uid();
						_this.length = item->length;

						_this.parent = menu->uid();
						//_this.level = (int)parent_level.size();

						if(!item->is_separator())
						{
							_this.length = item->length; ;// mii->title.length<uint32_t>();
							_this.title = item->title;
							_this.title_normalize = item->name;
						}

						_context._this = &_this;

						//if(!Selected.verify_types(si->fso))
						//	continue;

						if(si->mode)
						{
							auto mode = _context.parse_mode(si->mode);
							const bool mode_match =
								NativeMenuConstruction::static_mode_match(selection, mode);
							CaptureTraceSource(item->trace, si, L"static.mode", mode_match,
								mode_match ? L"accepted" : L"rejected");
							if(!mode_match)
								break;
						}

						if(si->location)
						{
							const bool location_evaluated = _context.Eval(si->location, location, true);
							if(location_evaluated)
							{
								const bool location_match =
									NativeMenuConstruction::location_matches(is_root,
										location, item->path);
								CaptureTraceSource(item->trace, si, L"static.location", location_match,
									location_match ? L"accepted" : L"skipped");
								if(!location_match)
									goto skip;
							}
							else
								CaptureTraceSource(item->trace, si, L"static.location", false, L"evaluation failed");
						}
						else if(!is_root)
						{
							CaptureTraceSource(item->trace, si, L"static.location", false, L"root only");
							goto skip;
						}

						if(si->where)
						{
							where_defined = _context.Eval(si->where).to_bool();
							CaptureTraceSource(item->trace, si, L"static.where", where_defined,
								where_defined ? L"accepted" : L"skipped");
							if(!where_defined)
								continue;
						}

						if(item->is_separator())
						{
							const bool separator_match = where_defined && !si->find;
							CaptureTraceSource(item->trace, si, L"static.separator", separator_match,
								separator_match ? L"accepted" : L"skipped");
							if(!separator_match)
								goto skip;
						}
						else
						{
							if(item->title.empty())
							{
								CaptureTraceSource(item->trace, si, L"static.title", false, L"empty; skipped");
								goto skip;
							}

							if(!si->find && !where_defined)
							{
								CaptureTraceSource(item->trace, si, L"static.match", false, L"find and where not defined");
								goto skip;
							}
							else
							{
								Object find = _context.Eval(si->find).move();
								if(find.is_null() || find.length() == 0)
								{
									const bool empty_find_match = where_defined && !(si->moveto && !is_root);
									CaptureTraceSource(item->trace, si, L"static.find", empty_find_match,
										empty_find_match ? L"where matched" : L"empty; skipped");
									if(!empty_find_match)
										goto skip;
								}
								else
								{
									string pattern = find.to_string().trim().tolower().move();
									FindPattern find_pattern;
									const bool pattern_valid = find_pattern.split(pattern, L'|');
									if(!pattern_valid && !where_defined)
									{
										CaptureTraceSource(item->trace, si, L"static.find", false, L"invalid pattern; skipped");
										goto skip;
									}

									const bool pattern_match =
										NativeMenuConstruction::static_find_match(pattern,
											item->name);
									CaptureTraceSource(item->trace, si, L"static.find", pattern_match,
										pattern_match ? L"matched" : L"skipped");
									if(!pattern_match)
										goto skip;
								}
							}
						}

						if(si->visibility && _settings.modify_items.visibility)
						{
							item->visibility = _context.parse_visibility(si->visibility);

							if(item->visibility == Visibility::Hidden)
							{
								CaptureTraceSource(item->trace, si, L"static.visibility", true, L"hidden; removed");
								removed= true;
							}
							else if(!item->is_separator())
							{
								CaptureTraceSource(item->trace, si, L"static.visibility", true,
									item->visibility == Visibility::Disabled ? L"disabled" : L"enabled");
								if(item->visibility == Visibility::Enabled)
									item->disabled = false;
									else if(item->visibility == Visibility::Disabled)
										item->disabled = true;
									_context._this->disabled = item->disabled;
							}
						}
						else if(si->visibility)
							CaptureTraceSource(item->trace, si, L"static.visibility", false, L"modification disabled");

						if(removed)
							items->erase(items->begin() + i--);
						else if(_settings.modify_items.parent)
						{
							string moveto;
							const bool moveto_evaluated = _context.Eval(si->moveto, moveto, true);
							if(moveto_evaluated)
							{
								moveto.trim(L'/');

								if(!moveto.equals(item->path))
								{
									CaptureTraceSource(item->trace, si, L"static.moveto", true, L"moved");
									item->path = moveto.move();
									if(auto submenu = __map_system_menu[item->path.hash()]; submenu)
									{
										item->parent = submenu;
										submenu->items.push_back(item);
									}
									else
									{
										__movable_system_items.push_back(item);
									}
									items->erase(items->begin() + i--);
								}
								else
									CaptureTraceSource(item->trace, si, L"static.moveto", false, L"destination unchanged");
							}
							else
								CaptureTraceSource(item->trace, si, L"static.moveto", false, L"evaluation failed");
						}
						else if(si->moveto)
							CaptureTraceSource(item->trace, si, L"static.moveto", false, L"modification disabled");

						if(!removed)
						{
							item->native_items.push_back(si);
							CaptureTraceSource(item->trace, si, L"static.result", true, L"displayed");
						}

						if(si->invoke)
						{
							const bool invoke_match = _context.parse_invoke(si->invoke) == 0;
							CaptureTraceSource(item->trace, si, L"static.invoke", invoke_match,
								invoke_match ? L"applied" : L"not applied");
							if(invoke_match)
							{
								if(!removed && item->is_menu())
									build_main_system_menuitems(item);
								break;
							}
						}

						continue;

					skip:
						CaptureTraceSource(item->trace, si, L"static.result", false, L"skipped");
						if(item->is_menu())
							build_main_system_menuitems(item);
					}
					catch(std::exception const& ex)
					{
						_log.error(L"%S", ex.what());
					}
				}
			}
		}

		void ContextMenu::build_system_menuitems(HMENU hMenu, menuitem_t *menu,
			bool is_root, bool capture_original)
		{
			CaptureTraceScope traceScope(&_studio_capture);
			if(!capture_original)
				::SendMessageW(hwnd.owner, WM_INITMENUPOPUP, reinterpret_cast<WPARAM>(hMenu), 0xFFFFFFFF);

			auto itmes_count = ::GetMenuItemCount(hMenu);

			menu->items.reserve(itmes_count);

			for(int i = 0; i < itmes_count; i++)
			{
				int found_duplicate = 0;
				string title;
				MENUITEMINFOW mii{ sizeof(MENUITEMINFOW) };
				mii.fMask = MenuItemInfo::FMASK;
				mii.dwTypeData = title.buffer((1024));
				mii.cch = 1024;

				if(::GetMenuItemInfoW(hMenu, i, true, &mii))
				{
					std::unique_ptr<menuitem_t> item(new menuitem_t);
					auto itemPtr = item.get();
					CaptureEvidenceScope evidenceScope(&item->evidence);
					CaptureTraceNumber(item->trace, L"native.index=", i);
					item->parent = menu;
					item->wid = mii.wID;
					item->dwItemData = mii.dwItemData;
					item->native_menu = hMenu;
					item->native_index = static_cast<uint32_t>(i);
					item->is_toplevel = is_root;
					item->native_fType = mii.fType;
					item->native_fState = mii.fState;
					item->is_default = (mii.fState & MFS_DEFAULT) != 0;
					item->owner_draw = (mii.fType & MFT_OWNERDRAW) != 0;

					if(is_root)
						;
					else
					{
						string n = menu->name;
						if(!menu->path.empty())
							item->path += menu->path + L'/';
						item->path += n.tolower();
					}

					if(mii.fType & MFT_SEPARATOR)
					{
						if(_settings.modify_items.remove.separator)
						{
							CaptureTrace(item->trace, L"remove.separator", true,
								capture_original ? L"would remove; retained for original evidence" : L"removed");
							if(!capture_original)
								continue;
						}
						item->type = 2;
					}
					else
					{
						item->type = mii.hSubMenu != nullptr;
						item->disabled = mii.fState & MFS_DISABLED;
						item->checked = mii.fState & MFS_CHECKED;
						item->radio_check = mii.fType & MFT_RADIOCHECK;
						item->image = MenuItemInfo::FindImage(&mii);

						if(item->disabled && _settings.modify_items.remove.disabled)
						{
							CaptureTrace(item->trace, L"remove.disabled", true,
								capture_original ? L"would remove; retained for original evidence" : L"removed");
							if(!capture_original)
								continue;
						}
						
						if(mii.cch > 0)
						{
							item->title = title.release(mii.cch).move();
							item->hash = MenuItemInfo::normalize(item->title, &item->name, &item->tab, &item->length, &item->keys);

							item->ui = Initializer::get_muid(item->hash);

							if(!capture_original && !item->is_menu() && is_root && item->disabled)
							{
								if(item->uid() == IDENT_ID_EMPTY_RECYCLE_BIN)
								{
									item->disabled = false;
									SHQUERYRBINFO sqrbi = { sizeof(SHQUERYRBINFO) };
									DLL::Invoke<HRESULT>(L"shell32.dll", "SHQueryRecycleBinW", nullptr, &sqrbi);
									if((sqrbi.i64Size + sqrbi.i64NumItems) == 0)
										item->disabled = true;
								}
							}

							if(_settings.modify_items.remove.duplicate)
							{
								uint32_t indexof = 0;
								for(auto im : menu->items)
								{
									if(im->hash == item->hash)
									{
										if(im->type == item->type)
										{
											if(capture_original)
											{
												CaptureTrace(item->trace, L"remove.duplicate", true,
													L"would remove; retained for original evidence");
												{
													CaptureEvidenceScope priorEvidence(&im->evidence);
													CaptureTrace(im->trace, L"remove.duplicate", true,
														L"would be replaced by a later duplicate");
												}
											}
											else
											{
												found_duplicate = 1;
												if(im->disabled)
												{
													if(!item->disabled)
													{
														found_duplicate = 2;
														CaptureTrace(item->trace, L"remove.duplicate", true, L"replaced disabled duplicate");
														{
															CaptureEvidenceScope priorEvidence(&im->evidence);
															CaptureTrace(im->trace, L"remove.duplicate", true,
																L"replaced by enabled duplicate");
														}
														menu->items[indexof] = item.release();
													}
												}
											}
												break;
										}
									}
								}
								indexof++;
							}
							
							if(found_duplicate == 1)
							{
								CaptureTrace(item->trace, L"remove.duplicate", true, L"removed");
								continue;
							}
						}
						else if(mii.fType & MFT_BITMAP)
						{
							//_log.info(L"MFT_BITMAP");
						}

						if(mii.hSubMenu)
							build_system_menuitems(mii.hSubMenu, itemPtr, false, capture_original);
					}
					
					if(capture_original || found_duplicate != 2)
					{
						if(!capture_original && item->is_menu())
						{
							string sub_path = item->name;
							if(!item->path.empty())
								sub_path = item->path + L'/' + sub_path;
							__map_system_menu[sub_path.hash()] = itemPtr;
						}

						menu->items.push_back(item.release());
					}
				}
			}
		}

		bool ContextMenu::Initialize()
		{
			try
			{
				if(!Initializer::Inited()) return false;

				// Start as soon as the context object exists so a Studio request
				// can complete its handshake before native menu enumeration begins.
				// The worker never waits on this thread and produces no data until
				// it has received capture.start.
				_studio_capture.Start(hwnd.owner);

				__trace(L"ContextMenu init");

				auto initializer = Initializer::instance;

				_context.Selections = &Selected;
				_context.Application = &initializer->application;
				_context.Cache = initializer->cache;

				Selected.Window.handle = hwnd.owner;
				Selected.Window.hInstance = _window.instance();

				if(!Selected.QueryShellWindow())
				{
					__trace(L"QueryShellWindow");
					return false;
				}

				//if(is_excluded())
				//	return false;

				auto sets = &_cache->settings;
				
				/*if(auto h = hWnd_owner ? hWnd_owner : hWnd; h)
					_result = Window::class_name(h);
				*/
				if(Selected.Window.id == WINDOW_UI)
				{
					if(Selected.Window.hash != WC__STATIC)
					{
						if(hwnd.active == hwnd.focus)
						{
							//return false;
						}
					}
				}

				if(!initializer->query())
				{
					__trace(L"initializer query");
					return false;
				}

				_context.Cache = initializer->cache;
				_cache = initializer->cache;
				_context.variables.global = &_cache->variables.global;
				_context.variables.runtime = &_cache->variables.runtime;
				_context.variables.local = nullptr;

				switch(Selected.Window.id)
				{
					case WINDOW_TASKBAR:
						Selected.Types[FSO_TASKBAR] = TRUE;
						Selected.front = FSO_TASKBAR;
						Selected.Directory = Path::GetKnownFolder(FOLDERID_Desktop).move();
						break;
					default:
						if(!Selected.QuerySelected())
						{
							__trace(L"QuerySelected");
							//return false;
						}
						//::{2cc5ca98-6485-489a-920e-b3e88a6ccce3}
						//return false;
						break;
				}

				if(Selected.Directory.empty())
					Selected.Directory = Path::CurrentDirectory().move();

				_vis = _context.parse_visibility(_cache->dynamic.visibility);

				if(_vis == Visibility::Hidden)
					return false;

				if(!Selected.Preparing())
				{
					__trace(L"Selected.Preparing");
					return false;
				}

				if(is_excluded())
					return false;

				hInstance = _window.instance();

				composition.activated = ::IsCompositionActive();
				::DwmIsCompositionEnabled(reinterpret_cast<BOOL *>(&composition.DwmEnabled));
				
				init_cfg();
				
				if(!_windowSubclass.hook(hwnd.owner, WindowSubclassProc, CONTEXTMENUSUBCLASS, this))
				{
					__trace(L"WindowSubclass");
					return false;
				}
			
				Prop::Set(hwnd.owner, this);

				if(_winEventHook.hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, Initializer::HInstance,
									  ContextMenu::WinEventProc, ProcessId, ThreadId))
				{
					HookMap[_winEventHook.get()] = this;
				}

				if(_context.eval_bool(sets->screenshot.enabled))
				{
					_keyboardHook.hook(WH_KEYBOARD, KeyboardProc, ThreadId);
					_context.Eval(sets->screenshot.directory, _screenshot, true);
				}

				hCursor = ::LoadCursorW(nullptr, IDC_ARROW);

				if(hwnd.active)
				{
					Prop::Set(hwnd.active, this);
				}

				//set_prop(hWnd, ctx);

				__trace(L"ContextMenu.Initialized");

				__system_menu_tree = new menuitem_t;
				__system_menu_tree->type = 10;
				__map_system_menu[0] = __system_menu_tree;

				if(0 == ::GetPropW(hwnd.owner, UxSubclass))
				{
					build_system_menuitems(_hMenu_original, __system_menu_tree, true, false);
					_studio_real_enumeration_complete = true;
				}

				if(_settings.modify_items.enabled)
				{
					_studio_capture_active_during_static_evaluation = false;
					build_main_system_menuitems(__system_menu_tree, true);
				}
				_studio_static_evaluation_complete = true;

				// Publish only after the real tree has been statically evaluated so
				// the original getter tree can receive those retained outcomes.  A
				// late handshake reaches the same gate from the window subclass.
				publish_original_capture_if_armed();

				return true;
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
			return false;
		}

		using namespace Diagnostics;

		int ContextMenu::Uninitialize()
		{
			if(_uninitialized)
				return TRUE;

			int result = FALSE;
			try
			{
				cancel_appearance_capture_timer();
				_studio_appearance_published.clear();
				_studio_popup_order.clear();
				_studio_capture.Stop();

				if(_hMenu)
				{
					::DestroyMenu(_hMenu);
					_hMenu = nullptr;
				}

				_windowSubclass.unhook();

				if(_hTheme)
				{
					::CloseThemeData(_hTheme);
					_hTheme = nullptr;
				}

				if(_hbackground)
				{
					::DeleteObject(_hbackground);
					_hbackground = nullptr;
				}

				if(font.handle) {
					::DeleteObject(font.handle);
					font.handle = nullptr;
				}

				if(hCursor)
				{
					::DestroyCursor(hCursor);
					hCursor = nullptr;
				}

				if(_showdelay[0] != UINT32_MAX && _showdelay[1] != UINT32_MAX && _showdelay[0] != _showdelay[1])
				{
					::SystemParametersInfoW(SPI_SETMENUSHOWDELAY, _showdelay[0], nullptr, SPIF_SENDCHANGE);
					_showdelay[0] = UINT32_MAX;
				}

				_keyboardHook.unhook();
				
				if(_winEventHook)
				{
					HookMap.erase(_winEventHook.get());
					_winEventHook.unhook();
				}

				Prop::Remove(hwnd.active);
				Prop::Remove(hwnd.owner);

				_tip.destroy();

				delete __system_menu_tree;

				__trace(L"ContextMenu.Uninitialized");
				_uninitialized = true;
				result = TRUE;
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
			return result;
		}

		int ContextMenu::InvokeCommand(int id)
		{
			Uninitialize();

			invoke_item = nullptr;

			if(id != 0)
			{
				if(ident.equals(id)) 
				{
					for(auto item : _items_command)
					{
						if(item->wID == static_cast<uint32_t>(id))
						{
							invoke_item = item;
							break;
						}
					}

					id = 0;
					if(invoke_item && invoke_item->dynamic)
					{

						//Invoke(this);
						//_thread.create(Invoke, this);
						std::thread(&Invoke, this).detach();
						//thread(Invoke, this).detach();
						//thread::begin(Invoke, this);
						return id;
					}
				}

				if(keyboard.equals({ VK_LMENU, VK_LCONTROL, L'C' }))
				{
					for(auto item : _items)
					{
						if(item->is_item() && item->wID == static_cast<uint32_t>(id))
						{
							invoke_item = item;
							break;
						}
					}

					if(invoke_item)
						_log.write(L"%s CommandId %d\r\n", invoke_item->title.text.c_str(), id);
					else
						_log.write(L"CommandId %d\r\n", id);
				}
			}

			// self destroy
			delete this;

			return id;
		}

		static void set00(ContextMenu *cm)
		{
			auto ctx = &cm->_context;
			auto menu = cm->invoke_item->owner_dynamic;
			ctx->Selections = &cm->Selected;
			//ctx->variables.runtime = &menu->owner->variables;
			ctx->variables.local = &menu->owner->variables;
			ctx->Keyboard->get_keys_state(true);

			auto invoke_item = cm->invoke_item;

			this_item _this;
			_this.type = (int)invoke_item->type;
			_this.pos = static_cast<int>(invoke_item->position);
			_this.checked = invoke_item->is_checked() ? (invoke_item->is_radiocheck() ? 2 : 1) : 0;
			_this.disabled = invoke_item->is_disabled();
			_this.system = true;
			_this.id = invoke_item->hash;
			_this.length = invoke_item->length;
			_this.parent = invoke_item->id;
			_this.length = invoke_item->length;
			_this.title = invoke_item->title;
			_this.title_normalize = invoke_item->title.normalize;
			ctx->_this = &_this;
		}

		void __stdcall ContextMenu::Invoke(ContextMenu *cm)
		{
			if(!cm) return;

			__try
			{
				auto ctx = &cm->_context;
				auto menu = cm->invoke_item->owner_dynamic;
				ctx->Selections = &cm->Selected;
				//ctx->variables.runtime = &menu->owner->variables;
				ctx->variables.local = &menu->owner->variables;
				ctx->Keyboard->get_keys_state(true);
				
				set00(cm);

				if(cm->mouse_button)
				{
					ctx->Keyboard->add_key(cm->mouse_button);
				}

				for(auto cmd_prop : menu->commands)
				{
					ctx->invoked = 0;
					cm->Selected.Index = 0;
					__try
					{
						if(cmd_prop)
						{
							if(auto invoke = cm->_context.parse_invoke(cmd_prop->invoke); invoke)
							{
								for(size_t i = 0; i < ctx->Selections->Count(); i++)
								{
									ctx->Selections->Index = i;
									if(2 == cm->invoke(cmd_prop))
										__leave;

									if(ctx->Break)
									{
										ctx->Break = false;
										__leave;
									}
									
									if(invoke > 1)
										::Sleep(invoke);
									ctx->invoked++;
								}
							}
							else if(2 == cm->invoke(cmd_prop))
								__leave;
						}
					}
					except
					{
#ifdef _DEBUG
						Logger::Exception(__func__);
#endif
					}
				}
			}
			__finally
			{
				delete cm;
				//thread::end();
			}
		}

		uint32_t ContextMenu::invoke(CommandProperty *cmd_prop)
		{
			struct {
				int type = 0;
				string command;
				string arguments;
				string directory;
				string verb;
				uint32_t window = SW_SHOWNORMAL;
				uint32_t wait = 0;
				uint32_t cancel = 0;
				Privileges admin = Privileges::None;
			} cmd;

			try
			{
				bool cancel = false;
				cmd.type = cmd_prop->command.type;
				string value;

				cmd.admin = invoke_item->privileges;
				
				if(cmd_prop->admin)
					cmd.admin = _context.parse_privileges(cmd_prop);

				if(cmd.type == COMMAND_PROMPT)
					cmd.command = Environment::Variable(def_COMSPEC).move();
				else if(cmd.type == COMMAND_SHELL || cmd.type == COMMAND_EXPLORER)
					cmd.command = /*Environment::Expand(L"windir") +*/ def_EXPLORER;
				else if(cmd.type == COMMAND_POWERSHELL)
					cmd.command = def_POWERSHELL;
				else if(cmd.type == COMMAND_PWSH)
					cmd.command = def_PWSH;
				else if(_context.Eval(cmd_prop->command.expr, value))
				{
					cmd.command = value.trim().move();
					if(cmd.command.taged(L'"'))
						cmd.command.trim(L'"');
					if(cmd.command.taged(L'\''))
						cmd.command.trim(L'\'');
				}

				if(!cmd.command.empty())
				{
					if(cmd.admin == Privileges::Default)
						cmd.verb = L"runas";
					else if(_context.Eval(cmd_prop->verb, value))
						cmd.verb = value.trim().move();

					cmd.window = _context.parse_window(cmd_prop);
					auto wait = _context.parse_wait(cmd_prop);

					if(_context.Eval(cmd_prop->arguments, value))
						cmd.arguments = value.move();

					cmd.directory = Selected.Directory;

					Object obj;
					if(_context.Eval(cmd_prop->directory, obj))
					{
						if(obj.is_number() && obj.to_bool() && !cmd.command.empty())
							cmd.directory = Path::Parent(cmd.command).move();
						else if(obj.is_string())
						{
							cmd.directory = obj.to_string().move();
							Selected.Directory = cmd.directory;
						}
					}

					/*auto r = */ShellExec::Run(cmd.command, cmd.arguments, cmd.directory,
												cmd.verb, wait, cmd.window);
				}
				//	cancel = r != exit_code
				if(cancel) return 2;
				return TRUE;
			}
			catch(...)
			{
#ifdef _DEBUG
				Logger::Exception(__func__);
#endif
			}
			return FALSE;
		}
		
		bool Tip::show()
		{
			if(handle && enabled)
			{
				hide();
				if(time == UINT16_MAX)
					time = ctx ? ctx->_theme.tip.time : TIMEIN;
				::SetTimer(handle, IDT_SHOW, time, nullptr);
				return true;
			}
			return false;
		}

		bool Tip::show(Expression *e, const Rect &rc)
		{
			this->e = e;
			this->rect = rc;
			return show();
		}

		bool Tip::show(const string &text, uint8_t type, uint16_t time, const Rect &rc)
		{
			this->type = type;
			this->text = text;
			this->rect = rc;
			this->time = time;
			return show();
		}

		bool Tip::hide(bool cancel)
		{
			if(handle)
			{
				if(cancel)
				{
					killTimer();
				}

				if(handle && visible)
				{
					rect = {};
					text = {};
					visible = false;
					e = {};
					return ::ShowWindow(handle, SW_HIDE);
				}
			};
			return true;
		}

		LRESULT Tip::Proc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam)
		{
			switch(uMsg)
			{
				case WM_TIMER:
				{
					auto nIDEvent = static_cast<UINT_PTR>(wParam);
					if(nIDEvent == IDT_SHOW)
					{
						::KillTimer(hWnd, Tip::IDT_SHOW);

						if(!text.empty())
						{
							auto theme = &ctx->_theme;
							DC dc = hWnd;
							dc.set_font(ctx->font.handle);
							Rect rc = { 0, 0, ctx->dpi(theme->tip.maxwidth), ctx->dpi(Tip::MAXHEIGHT) };
							dc.measure(text, text.length<int>(), &rc, DT_EXPANDTABS | DT_WORDBREAK | DT_EDITCONTROL);

							SIZE size = {
								theme->tip.padding.width() + rc.width(),
								theme->tip.padding.height() + rc.height()
							};

							PlutoVG pluto(size.cx, size.cy);

							Color txtclr = theme->tip.normal.text;
							Color bgclr = theme->tip.normal.back;

							// 0x0D6EFD

							if(type == 1)
							{
								txtclr = theme->tip.primary.text;
								bgclr = theme->tip.primary.back;
							}
							if(type == 2)
							{
								txtclr = theme->tip.info.text;
								bgclr = theme->tip.info.back;
							}
							else if(type == 3)
							{
								txtclr = theme->tip.success.text;
								bgclr = theme->tip.success.back;
							}
							else if(type == 4)
							{
								txtclr = theme->tip.warning.text;
								bgclr = theme->tip.warning.back;
							}
							else if(type == 5)
							{
								txtclr = theme->tip.danger.text;
								bgclr = theme->tip.danger.back;
							}

							int radius = ctx->dpi(theme->tip.radius);

							pluto.rect(0, 0, size.cx, size.cy, radius).fill(0x000000, 32, false);
							pluto.rect(1, 1, size.cx - 2, size.cy - 2, radius);

							if(theme->tip.border)
								pluto.fill(theme->tip.border.to_RGB(), theme->tip.border.a);
							else
								pluto.fill(0x000000, 32, false);

							pluto.rect(2, 2, size.cx - 4, size.cy - 4, radius).clear();

							pluto.rect(2, 2, size.cx - 4, size.cy - 4, radius)
								.fill(bgclr.to_RGB(), theme->tip.opacity);
							
							auto_gdi<HBITMAP> bitmap(pluto.tobitmap());
							DC dc_layer(dc.CreateCompatibleDC(), 1);
							dc_layer.set_font(ctx->font.handle);

							dc_layer.select_bitmap(bitmap.get());
							auto top = theme->tip.padding.top - (ctx->dpi.scale() > 1.0 ? ctx->dpi(1.5) : 1);

							Rect rctxt = { theme->tip.padding.left, top,
								size.cx - theme->tip.padding.right,
								size.cy - theme->tip.padding.bottom };

							ctx->draw_string(dc_layer, ctx->font.handle,
											 &rctxt,
											 txtclr,
											 text, text.length<int>(),
											 (ctx->is_layoutRTL ? DT_RIGHT : DT_LEFT) | DT_EXPANDTABS | DT_WORDBREAK | DT_EDITCONTROL);

							long x = rect.left + (rect.width() / 2) - (size.cx / 2);
							long y = rect.top - size.cy - ctx->dpi(10);

							if(x < 0) x = 0;
							if(y < 0) y = rect.bottom + ctx->dpi(10);

							if((x + size.cx) > ctx->_rcMonitor.right)
								x = ctx->_rcMonitor.right - size.cx;

							if(::SetWindowPos(hWnd, HWND_TOPMOST, x, y, size.cx, size.cy, SWP_NOACTIVATE | SWP_SHOWWINDOW))
							{
								// use the source image's alpha channel for blending
								BLENDFUNCTION blend{ AC_SRC_OVER , 0, 0xFF, AC_SRC_ALPHA };
								POINT ptZero{ };
								// paint the window (in the right location) with the alpha-blended bitmap
								::UpdateLayeredWindow(hWnd, 0, 0, &size, dc_layer, &ptZero, 0x000000, &blend, ULW_ALPHA);
								::UpdateWindow(hWnd);
								::SetTimer(hWnd, Tip::IDT_HIDE, Tip::TIMEOUT, nullptr);
								visible = true;
							}
						}
						else
						{
							hide();
						}
					}
					else if(nIDEvent == IDT_HIDE)
					{
						hide(true);
					}
					break;
				}
			}
			return ::DefWindowProcW(hWnd, uMsg, wParam, lParam);
		}

#pragma region Class Menu Proc

		static const SIZE FixedFrame = {
			::GetSystemMetrics(SM_CXFIXEDFRAME) * 2,
			::GetSystemMetrics(SM_CYFIXEDFRAME) * 2
		};

		WND *ContextMenu::OnMenuCreate(HWND hWnd)
		{
			auto wnd = &_map[hWnd];

			wnd->ctx = this;
			wnd->handle = hWnd;
			wnd->set_prop();
			set_prop(hWnd);

			///	auto hMenu = (HMENU)::SendMessageW(hWnd, MN_GETHMENU, 0, 0);
			//	wnd->hMenu = hMenu;
			//	map_menu_wnd[hMenu] = { hMenu, hWnd };

			current.hWnd = hWnd;
			_level.push_back(wnd);
			try
			{
				_studio_popup_order.push_back(hWnd);
			}
			catch(...)
			{
				_studio_capture.Fail("CAPTURE_APPEARANCE_MEMORY",
					"The native capture could not retain the popup window order.");
			}

			Flag<ULONG_PTR> cs_style = ::GetClassLongPtrW(hWnd, GCL_STYLE);
			Flag<LONG_PTR> style = ::GetWindowLongPtrW(hWnd, GWL_STYLE);
			Flag<LONG_PTR> ex_style = ::GetWindowLongPtrW(hWnd, GWL_EXSTYLE);

			auto cs_style_old = cs_style;
			auto style_old = style;
			auto ex_style_old = ex_style;

			//RECT r = { 0 };
			//AdjustWindowRectEx(&r, (DWORD)style.value, false, (DWORD)ex_style.value);

			cs_style.remove(CS_DROPSHADOW);
			style.remove(WS_BORDER);

			ex_style.remove(WS_EX_WINDOWEDGE);
			ex_style.remove(WS_EX_DLGMODALFRAME);

			if(composition)
				ex_style.add(WS_EX_COMPOSITED);

			//ex_style.add(WS_EX_LAYERED);
			//ex_style.add(WS_EX_NOREDIRECTIONBITMAP);

			if(!cs_style.equals(cs_style_old))
				::SetClassLongPtrW(hWnd, GCL_STYLE, cs_style);

			if(!style.equals(style_old))
				::SetWindowLongPtrW(hWnd, GWL_STYLE, style);

			if(!ex_style.equals(ex_style_old))
				::SetWindowLongPtrW(hWnd, GWL_EXSTYLE, ex_style);
						
			//::SetClassLongPtrW(hWnd, GCL_STYLE, 0);
			//::SetWindowLongPtrW(hWnd, GWL_STYLE, WS_POPUP);
			//::SetWindowLongPtrW(hWnd, GWL_EXSTYLE, WS_EX_NOREDIRECTIONBITMAP);

			::PostMessageW(hWnd, WM_SETCURSOR, 0, 0);

			//int opacity = 100;
			//SetLayeredWindowAttributes(hWnd, 0, (255 * 100) / 100, LWA_ALPHA);
			//SetLayeredWindowAttributes(hWnd, 0x0ff00, 0, LWA_COLORKEY);
			if(composition)
			{
				AccentPolicy ap(hWnd);
				//ap.set(AccentPolicy::Disabled);
				//ap.set(ap.AcrylicBlurBehind, ap.AllowSetWindowRgn, _theme.background.color.to_ABGR());

				//if(_theme.transparent)
				Compositor::TransparentArea(hWnd);
			}

			BOOL ENABLED = TRUE;
			::DwmSetWindowAttribute(hWnd, DWMWA_NCRENDERING_ENABLED, &ENABLED, sizeof(BOOL));
			::DwmSetWindowAttribute(hWnd, DWMWA_ALLOW_NCPAINT, &ENABLED, sizeof(BOOL));
			::DwmSetWindowAttribute(hWnd, DWMWA_NONCLIENT_RTL_LAYOUT, &ENABLED, sizeof(BOOL));

			WindowSubclass::Set(hWnd, MenuSubClassProc, 0, this);
			//::ShowWindowAsync(hWnd, SW_HIDE);
			return wnd;
		}

		void ContextMenu::OnMenuShow(HWND hWnd, WND *wnd)
		{
			if(wnd == nullptr)
				wnd = WND::get_prop(hWnd);

			if(wnd)
			{
				wnd->show_layers();
				//Compositor::TransparentArea(hWnd);
				// must used sendmsg to invoke initmenupopup
				//if(ver->IsWindows11OrGreater())
				//	DWM(hWnd).RemoveCorner();
			}
		}

		HBITMAP crop_image(const RECT rectangle, const HBITMAP source_image)
		{
			return static_cast<HBITMAP>(CopyImage(source_image, IMAGE_BITMAP, rectangle.right - rectangle.left,
																rectangle.bottom - rectangle.top, LR_CREATEDIBSECTION));
		}

		void ContextMenu::screenshot()
		{
			if(!_level.empty()) try
			{
				auto wnd = _level.front();
				auto hwnd = wnd->handle;
				auto hdesktop = ::GetDesktopWindow();
				Rect rc_desktop = hdesktop;
				SIZE sz = { rc_desktop.right + 100, rc_desktop.bottom + 100 };

				DC dc = hwnd;
				uint8_t *bits{};
				auto_gdi<HBITMAP> hbitmap(dc.CreateDIBSection(sz.cx,sz.cy, &bits));

				if(!hbitmap)
					return;

				DC dc_dst(dc.CreateCompatibleDC(), 1);
				dc_dst.select_bitmap(hbitmap.get());

				Point pt0 = { sz.cx, sz.cy };
				Point pt1 = { 0, 0 };

				for(auto &l : _level)
				{
					Rect rc = l->handle;
					if(l->layer.hbitmap)
					{
						BITMAP bmp{}; GetObject(l->layer.hbitmap, sizeof(bmp), &bmp);
						dc_dst.draw_image(rc.point(), { bmp.bmWidth, bmp.bmHeight }, l->layer.hbitmap);
					}

					if(l->blurry.handle)
					{
						DC dc_blurry = l->blurry.handle;
						dc_dst.draw_image(rc.point(), rc.size(), dc_blurry);
					}

					dc_dst.draw_image(rc.point(50), rc.size(), l->hdc);

					pt0.x = std::min<long>(rc.left, pt0.x);
					pt0.y = std::min<long>(rc.top, pt0.y);
					pt1.x = std::max<long>(rc.right, pt1.x);
					pt1.y = std::max<long>(rc.bottom, pt1.y);
				}
				
				pt0.x = std::max<long>(0, pt0.x);
				pt0.y = std::max<long>(0, pt0.y);

				pt1.x = std::min<long>(sz.cx, pt1.x);
				pt1.y = std::min<long>(sz.cy, pt1.y);
				
				sz = { (pt1.x - pt0.x) + 100, (pt1.y - pt0.y) + 100 };

				bits = nullptr;
				auto_gdi<HBITMAP> hbitmap0(dc_dst.CreateDIBSection(sz.cx, sz.cy, &bits));
				if(bits)
				{
					DC dc0(dc_dst.CreateCompatibleDC(), 1);
					dc0.select_bitmap(hbitmap0.get());
					dc0.draw_image({ }, sz, dc_dst, pt0, sz);
				}
				
				auto w = sz.cx, h = sz.cy;
				
				auto p = bits;
				if(bits)
				{
					std::unique_ptr< unsigned char[]>flip(new unsigned char[w * h * 4]);

					auto src = p + w * h * 4 - w * 4;
					auto dst = flip.get();

					for(int y = 0; y < h; y++)
					{
						::memcpy(dst, src, static_cast<size_t>(w) * 4);
						src -= w * 4;
						dst += w * 4;
					}

					auto pi = (RGBA *)flip.get();
					for(int i = 0; i < w * h; i++)
					{
						pi[i].prem();
					}

					SYSTEMTIME lt = { };
					::GetLocalTime(&lt);

					string tf = string::TimeFormat(&lt, L"ymd_HMS").move();
					//string::NewGuid().c_str()
					// FOLDERID_Pictures, FOLDERID_SavedPictures
					string location = _screenshot;

					if(location.empty())
						location = IO::Path::GetKnownFolder(FOLDERID_Screenshots).move();
					
					if(location.empty())
						location = Initializer::instance->application.Dirctory;
					
					location = Path::Combine(location, L"screenshot_" + tf + L".png");

					plutovg_stbi_write_png(location, w, h, flip.get());
				}
			}
			catch(...) {
			}
		}

		bool ContextMenu::draw_layer(WND *wnd, SIZE size, int margin,
			bool opaqueInterior)
		{
			//Gradients - box, linear and radial
			if(!wnd || !wnd->ctx)
				return false;

			if(wnd->layer.hbitmap)
			{
				::DeleteObject(wnd->layer.hbitmap);
				wnd->layer.hbitmap = nullptr;
			}

			double radius = _theme.border.radius;
			//auto border_opacity = double(double(theme->frame.opacity) / 100.0);
			auto szborder = _theme.border.size;
			auto szborder2 = _theme.pow(szborder);

			double x = margin;
			double y = margin;
			double w = (size.cx - (margin + margin));
			double h = (size.cy - (margin + margin));

			PlutoVG pluto(size.cx, size.cy);
			// Preview composition starts transparent.  An opaque interior is a
			// local rendering choice; keep the live theme alpha untouched.
			const auto background_alpha = opaqueInterior ? uint8_t{0xFF} :
				_theme.background.color.a;
			const auto background_opacity = opaqueInterior ? uint8_t{0xFF} :
				_theme.background.opacity;

			bool need_clear = _theme.background.effect > 0;

			if(_theme.shadow.enabled)
			{
				uint8_t shadow_opacity = _theme.shadow.color.a;
				Rect shadow_rect = { 0, 0, size.cx, size.cy };

				auto clr = _theme.shadow.color.to_RGB();
				//uint8_t offset = 0;
				auto sz = _theme.shadow.size;

				uint8_t o = shadow_opacity * 20 / 100;
				need_clear = _theme.background.color.a < 0xFF;
				sz = sz > 30 ? 30 : sz;

				/*for(uint8_t i = 0; i < sz + 1; i++)
				{
					auto b = szborder + i;
					pluto.rect(x - b,
							   y - b + _theme.shadow.offset,
							   w + (b * 2),
							   h + (b * 2),
							   radius + b)
						.fill(clr, o);
				}*/

				sz += _theme.shadow.offset;

				for(uint8_t i = 0; i < sz + 1; i++)
				{
					auto b = szborder + i;
					pluto.rect((x + _theme.shadow.offset) - b,
							   (y + _theme.shadow.offset) - b + _theme.shadow.offset,
							   w + (b * 2) - _theme.shadow.offset - _theme.shadow.offset,
							   h + (b * 2) - _theme.shadow.offset - _theme.shadow.offset,
							   radius + (b / static_cast<double>(2)))
						.fill(clr, o);
				}

			}

			SIZE back = { wnd->width, wnd->height };
			Rect border_rect = { margin, margin, wnd->width, wnd->height };
			Rect back_rect = { margin + szborder , margin + szborder,
				wnd->width - szborder2, wnd->height - szborder2 };

			if(_theme.background.effect == 0)
			{
				auto xb = (szborder % 2) == 0 ? 0.0 : 0.5;
				xb += (szborder / 2.0);
				pluto.rect(margin + xb, margin + xb,
						   back.cx - xb - xb, back.cy - xb - xb, radius)
					//.fill(0x00ff00, theme->background.color.a, true)
					.fill(_theme.background.color.to_RGB(), background_alpha, true)
					.stroke_width(szborder)
					.stroke_fill(_theme.border.color.to_RGB(), _theme.border.color.a);
			}
			else
			{
				if(szborder > 0 && _theme.border.color.a > 0)
				{
					need_clear = true;// theme->border.color.a < 0xFF;
					pluto.rect(border_rect.left, border_rect.top, border_rect.right, border_rect.bottom, radius == 0 ? 0 : radius + szborder)
						.fill(_theme.border.color.to_RGB(), _theme.border.color.a);
				}

				if(need_clear)
					pluto.rect(back_rect.left, back_rect.top, back_rect.right, back_rect.bottom, radius).clear();

				if(background_opacity > 0)
				{
					pluto.save();
					pluto.rect(back_rect.left, back_rect.top, back_rect.right, back_rect.bottom, radius)
						//.set_operator(plutovg_operator_src)
						//.set_fill_rule(plutovg_fill_rule_non_zero)
						.fill(_theme.background.color.to_RGB(), background_opacity, true);
					pluto.restore();
				}
			}

			if(_theme.gradient.enabled)
			{
				auto to_size = [](double value, double size) ->double { return value * size / 100; };
				bool render = false;
				Gradient gradient;

				auto fxx = szborder;
				auto fyy = margin + szborder;

				w = back_rect.right;
				h = back_rect.bottom;

				if(_theme.gradient.linear[0] == 1.0)
				{
					double x1 = to_size(_theme.gradient.linear[1], w);
					double y1 = to_size(_theme.gradient.linear[2], h);
					double x2 = to_size(_theme.gradient.linear[3], w);
					double y2 = to_size(_theme.gradient.linear[4], h);

					if(x1 > 0) x1 += fxx * 2;
					if(x2 > 0) x2 += fxx * 2;

					if(y1 > 0) y1 += fyy * 2;
					if(y2 > 0) y2 += fyy * 2;
					
					render = x1 != 0.0 || y1 != 0.0 || x2 != 0.0 || y2 != 0.0;
					if(render)
						gradient.create_linear(x1, y1, x2, y2);
				}
				else if(_theme.gradient.radial[0] == 1.0)
				{
					double cx = to_size(_theme.gradient.radial[1], w / 2 + fxx);
					double cy = to_size(_theme.gradient.radial[2], h / 2 + fyy);
					double r  = to_size(_theme.gradient.radial[3], ((h >= w ? w : h) / 2));
					double fx = to_size(_theme.gradient.radial[4], w / 2 + fxx);
					double fy = to_size(_theme.gradient.radial[5], h / 2 + fyy);

					render = cx != 0.0 || cy != 0.0 || r != 0.0 || fx != 0.0 || fy != 0.0;
					if(render)
						gradient.create_radial(cx, cy, r, fx, fy, 0);
				}

				if(render)
				{
					for(auto &s : _theme.gradient.stpos)
						gradient.add_stop(s.offset, s.color.to_RGB(), s.color.a);
					
					pluto.rect(back_rect.left, back_rect.top, w, h, radius).fill(gradient);
				}
			}

			wnd->layer.hbitmap = pluto.tobitmap();
			return wnd->layer.hbitmap;
		}

		void ContextMenu::UpdateLayered(WND *wnd, bool update_blurry)
		{
			if(!wnd) return;

			int margin = 50;

			SIZE size = {
				wnd->width + margin + margin,
				wnd->height + margin + margin
			};

			if(wnd->layer.handle && draw_layer(wnd, size, margin))
			{
				DC dc = wnd->layer.handle;
				DC dc_layer(dc.CreateCompatibleDC(), 1);
				dc_layer.select_bitmap(wnd->layer.hbitmap);

				POINT ptZero{ };
				// use the source image's alpha channel for blending
				BLENDFUNCTION blend{ AC_SRC_OVER , 0, 0xFF, AC_SRC_ALPHA };
				// paint the window (in the right location) with the alpha-blended bitmap
				::UpdateLayeredWindow(wnd->layer.handle, nullptr, nullptr, &size, dc_layer, &ptZero, 0x000000, &blend, ULW_ALPHA);
				dc_layer.restore_bitmap();
			}

			if(wnd->blurry.handle)
			{
				if(update_blurry)
				{
					auto flags = SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOREDRAW | SWP_NOACTIVATE | SWP_SHOWWINDOW;
					SetWindowPos(wnd->blurry.handle, nullptr, wnd->x, wnd->y, wnd->width, wnd->height, flags);
				}

				if(_theme.border.radius > 0)
				{
					auto border = _theme.border.size;
					auto w = wnd->width - border - border;
					auto h = wnd->height - border - border;
					wnd->Regoin(wnd->blurry.handle, 0, 0, w + 1, h + 1, int(_theme.border.radius / 1.5));
				}
			}
		}

		bool ContextMenu::CreateLayer(WND *wnd)
		{
			if(!wnd || !wnd->ctx) return false;

			DWM dwm;
			auto layer = &wnd->layer;
			auto blurry = &wnd->blurry;
			auto border = _theme.border.size;
			int margin = 50;

			SIZE size = {
				wnd->width + margin + margin,
				wnd->height + margin + margin
			};

			auto w = wnd->width - border - border;
			auto h = wnd->height - border - border;

			// Blurry is not useful in high contrast mode
			if(composition &&
			   _theme.background.effect >= 2 &&
			   _theme.background.opacity < 0xFF)
			{
				/*
				Enable Acrylic Effects in Hyper-V VM
				August 17, 2018 (revised September 8, 2018)
				[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm]
				"ForceEffectMode"=dword:00000002
				*/

				//blurry->create(wnd->x + border - 1, wnd->y + border - 1, w + 2, h + 2, hwnd.focus, WS_EX_COMPOSITED);
				blurry->create(wnd->x + border, wnd->y + border, w, h, hwnd.focus, WS_EX_COMPOSITED /*| WS_EX_NOREDIRECTIONBITMAP*/);
				if(blurry->handle)
				{
					AccentPolicy accent(blurry->handle);
					// Blur is not useful in high contrast mode
					// Windows build version less than 16299 does not support acrylic effect
					// 1903 = 18362
					// 1803 = 17134
					// 1709 = 16299
					// 18362 	TintLuminosityOpacity
					if(_theme.background.effect >= 3)
					{
						if(ver->Major < 10 || (ver->Major == 10 && ver->Build < 17134))
							_theme.background.effect = 2;
					}

					uint32_t tintColor = _theme.background.tintcolor;

					if(_theme.background.effect == 2) // blur effect 
					{
						accent.state = accent.BlurBehind;
						if(_theme.border.radius > 0)
							accent.flags = accent.AllowSetWindowRgn;
					}
					else if(_theme.background.effect >= 3) // acrylic effect 
					{
						// Windows 10 build 17134
						accent.state = accent.AcrylicBlurBehind;
						accent.flags = accent.Luminosity;
						if(_theme.border.radius > 0)
							accent.flags |=accent.AllowSetWindowRgn;

						if(tintColor == 0)
							tintColor = 0x01000000;
					}

					dwm.handle = blurry->handle;
					//dwm.SetImmersiveDarkMode();
					//dwm.ExtendFrameIntoClientArea();

					Compositor::TransparentArea(blurry->handle);
					accent.color = tintColor;
					accent.set();
				}
			}

			layer->create(wnd->x - margin, wnd->y - margin, size.cx, size.cy, hwnd.focus, WS_EX_LAYERED);
			UpdateLayered(wnd, false);
			return wnd->show_layers();
		}

		struct ooo
		{
			int sel = 0;
			Rect rc{};
		}__o[6];
		int __sel = -1;
		Point __pt;
		int xx = 0;

		LRESULT __stdcall window_Subclass(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam,
															[[maybe_unused]] UINT_PTR uIdSubclass,
															[[maybe_unused]] DWORD_PTR dwRefData)
		{
			MESSAGE defSubclassProc = { hWnd, uMsg, wParam, lParam };
			switch(uMsg)
			{
				case WM_MOUSEMOVE:
					break;
				case WM_LBUTTONUP:
					break;
				case WM_RBUTTONUP:
					PostQuitMessage(WM_QUIT);
					break;
			}
			return defSubclassProc();
		}

		LRESULT CALLBACK MessageProc(int nCode, WPARAM wParam, LPARAM lParam)
		{
			if(nCode >= 0)
			{
				auto pMSG = (MSG *)lParam;

				//_log.info(L"0x%04x %s", pMSG->message, msg_map[pMSG->message]);
				
				switch(pMSG->message)
				{
					case WM_CREATE:
						break;
					case WM_MOUSEMOVE:
						//_log.info(L"%s", Window::class_name(GetCapture()).c_str());
						SendMessage(pMSG->hwnd, WM_MOUSEMOVE, pMSG->wParam, pMSG->lParam);
						break;
					case WM_MENUSELECT:
						// keep in mind menu handle and selected item identifier
						//m_hMenu = (HMENU)pMSG->lParam;
						//m_wMenuItemID = LOWORD(pMSG->wParam);
						break;
					case WM_LBUTTONUP:
					{
						// toggle check item
						// send command message to owner window
						//if(NULL != m_pWnd->GetSafeHwnd())
						//	m_pWnd->SendMessage(WM_COMMAND, MAKEWPARAM(m_wMenuItemID, 0), 0);

						// change to WM_NULL to prevent closing menu
						//pMSG->message = WM_NULL;
					}
					break;
					case WM_LBUTTONDBLCLK:
						// just set WM_NULL to get rid of all default processing 
						//pMSG->message = WM_NULL;
						break;
					case 485:  // HACK to handle popup menus
						//_log.info(L"485 HACK to handle popup menus");
						break;
				}
				//_log.info(L"%d, %04x", nCode, pMSG->message);
			}
			return ::CallNextHookEx(0, nCode, wParam, lParam);
		}

		HHOOK m_hHook = NULL;

		inline void fix_ugly_flicker()
		{
			LARGE_INTEGER freq, now0, now1;
			::QueryPerformanceFrequency(&freq); // hz

			// this absurd code makes Sleep() more accurate
			// - without it, Sleep() is not even +-10ms accurate
			// - with it, Sleep is around +-1.5 ms accurate
			TIMECAPS tc;

			::timeGetDevCaps(&tc, sizeof(tc));
			int ms_granularity = tc.wPeriodMin;
			::timeBeginPeriod(ms_granularity); // begin accurate Sleep() !

			::QueryPerformanceCounter(&now0);

			// ask DWM where the vertical blank falls
			DWM_TIMING_INFO dti;
			::memset(&dti, 0, sizeof(dti));
			dti.cbSize = sizeof(dti);

			::DwmGetCompositionTimingInfo(NULL, &dti);
			::QueryPerformanceCounter(&now1);

			// - DWM told us about SOME vertical blank
			//   - past or future, possibly many frames away
			// - convert that into the NEXT vertical blank
			__int64 period = (__int64)dti.qpcRefreshPeriod;
			__int64 dt = (__int64)dti.qpcVBlank - (__int64)now1.QuadPart;
			__int64 w, m;

			if(dt >= 0)
				w = dt / period;
			else // dt < 0
			{
				// reach back to previous period - so m represents consistent position within phase
				w = -1 + dt / period;
			}

			// uncomment this to see worst-case behavior
			// dt += (sint_64_t)(0.5 * period);
			m = dt - (period * w);
			assert(m >= 0);
			assert(m < period);
			double m_ms = 1000.0 * m / (double)freq.QuadPart;
			::Sleep((int)round(m_ms));
			::timeEndPeriod(ms_granularity);
		}
		int bbb = 0;
		int ixi = 0;
		LRESULT __stdcall ContextMenu::MenuSubClassProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam,
														[[maybe_unused]] UINT_PTR uIdSubclass,
														DWORD_PTR dwRefData)
		{
			MESSAGE defSubclassProc = { hWnd, uMsg, wParam, lParam };

			auto ctx = reinterpret_cast<ContextMenu *>(dwRefData);
			if(ctx == nullptr)
			{
				if(auto gti = Window::GetGUIThread(hWnd); gti.hwndActive)
					ctx = Prop::Get(gti.hwndActive);

				if(ctx == nullptr)
					return defSubclassProc();
			}

			auto wnd = WND::get_prop(hWnd);
			if(!wnd)
				return defSubclassProc();

			if(!wnd->hdc)
			{
				ixi = 0;
				wnd->hdc = ::GetWindowDC(hWnd);
			}

			LRESULT lret = FALSE;
			auto theme = &ctx->_theme;
			//_log.info(L"%04x, %s", uMsg, msg_map[uMsg]);
			switch(uMsg)
			{
				//case MN_GETPPOPUPMENU:
				//case MN_SETHMENU:
				//	break;
				case WM_DESTROY:
				{
					//wnd->hidden();
					break;
				}
				case WM_NCDESTROY:
				{
					if(ctx->_studio_appearance_timer_hwnd == hWnd)
						ctx->cancel_appearance_capture_timer();
					ctx->_studio_appearance_published.erase(hWnd);
					wnd->studio_appearance.clear();
					for(auto it = ctx->_studio_popup_order.begin();
						it != ctx->_studio_popup_order.end(); )
					{
						if(*it == hWnd)
							it = ctx->_studio_popup_order.erase(it);
						else
							++it;
					}
					for(auto it = ctx->_level.begin(); it != ctx->_level.end(); )
					{
						if(!*it || (*it)->handle == hWnd)
							it = ctx->_level.erase(it);
						else
							++it;
					}
					wnd->destroy();
					ctx->_map.erase(hWnd);
					ctx->current.zero();
					break;
				}
				case WM_SIZE:
				case WM_MOVE:
				{
					//_log.info(L"%x", GetMenu(hWnd));
					break;
				}
				case MN_SIZEWINDOW: // after WM_INITMENUPOPUP
				{
					//_log.info(L"MN_SIZEWINDOW");
					if(wParam & MNSW_DRAWFRAME)
						wParam &= ~MNSW_DRAWFRAME;
					
					lret = defSubclassProc();
					return lret;
				}
				case MN_GETHMENU:
				{
					// returns the hmenu associated with this menu window.
					lret = defSubclassProc();
					if(!wnd->hMenu)
					{
						wnd->hMenu = reinterpret_cast<HMENU>(lret);
						if(::IsMenu(wnd->hMenu))
						{
							ctx->map_menu_wnd[wnd->hMenu] = { wnd->hMenu, hWnd };
						}
					}
					break;
				}
				case WM_NCCALCSIZE:
				{
					//_log.info(L"WM_NCCALCSIZE %x", wParam);
					xx = 0;
					__pt = { -1,-1 };
					//pncc->rgrc[0] is the new rectangle
					//pncc->rgrc[1] is the old rectangle
					//pncc->rgrc[2] is the client rectangle
					//lret = DefWindowProc(hWnd, WM_NCCALCSIZE, wParam, lParam);
					auto fCalcValidRects = static_cast<BOOL>(wParam);
					// Calculate new NCCALCSIZE_PARAMS based on custom NCA inset.
					if(fCalcValidRects)
					{
						[[maybe_unused]] auto nc = reinterpret_cast<NCCALCSIZE_PARAMS *>(lParam);
						//if(++_WM_NCCALCSIZE == 1)
						{
							//nc->lppos->flags |= SWP_NOREDRAW| SWP_NOCOPYBITS;
							nc->rgrc[0].left += theme->border.size + theme->border.padding.left;
							nc->rgrc[0].top += theme->border.size + theme->border.padding.top;
							nc->rgrc[0].right += theme->border.size + theme->border.padding.right;
							nc->rgrc[0].bottom += theme->border.size + theme->border.padding.bottom;
							if(wnd->has_scroll)
							{
								nc->rgrc[0].top += ctx->dpi(10);
								nc->rgrc[0].bottom -= ctx->dpi(10);
							}
						}
					//	return WVR_VALIDRECTS;
					}
					else
					{
						//auto rect = reinterpret_cast<RECT*>(lParam);
						//r->top += 200;
						//const int cxBorder = 2;
						//const int cyBorder = 2;
						//InflateRect((LPRECT)lParam, -cxBorder, -cyBorder);
					}
					fix_ugly_flicker();
					//::DwmFlush();// wait till finished
					return lret;
				}
				//BOOL OnWindowPosChanging(HWND hwnd, LPWINDOWPOS pwp)
				case WM_WINDOWPOSCHANGING:
				{
					//_log.info(L"WM_WINDOWPOSCHANGING");
					//lret = ::DefSubclassProc(hWnd, uMsg, wParam, lParam);
					auto wp = reinterpret_cast<WINDOWPOS *>(lParam);
					Flag<uint32_t> flags = wp->flags;

					if(!flags.has(SWP_HIDEWINDOW))
					{
						if(!flags.has(SWP_NOMOVE))
						{
							if(wnd->y == wp->y && wnd->y > 0)
							{
								if(wnd->height > wp->cy)
								{
									//wp->y += (wnd->height - wp->cy);
									//wnd->y = wp->y;
									//wnd->height += wnd->height - wp->cy;
									//ctx->UpdateLayered(wnd, true);
								}
								//auto old_height = wnd->height;
								//auto old_y = wnd->width;
								//wp->y = 0;
								//_log.info(L"%d %d", wnd->height, wp->cy);
							}
						}

						auto bz = theme->pow(theme->border.size);
						if(!flags.has(SWP_NOSIZE))
						{
							// remove fixed frame
							wp->cx -= FixedFrame.cx;
							wp->cy -= FixedFrame.cy;

							wp->cx += bz + theme->border.padding.width();
							wp->cy += bz + theme->border.padding.height();

							if((wp->cy + 100) > ctx->_rcMonitor.height())
							{
								wnd->has_scroll = true;
								wp->cy -= ctx->dpi(10);
							}
							/*else
							{
								auto hMenu = (HMENU)::SendMessageW(hWnd, MN_GETHMENU, 0, 0);
								if(::IsMenu(hMenu))
								{
									auto menu = ctx->_menus[hMenu];
									if(menu.popup_height)
									{
										wnd->has_scroll = true;
									}
								}
							}*/
							auto old_height = wnd->height;
							//auto old_y = wnd->y;

							wnd->width = wp->cx;
							wnd->height = wp->cy;

							if(old_height > 0 && old_height != wp->cy)
								ctx->UpdateLayered(wnd, true);

							//if(wp->cy > 450)
								//wp->cy += 16;
							
						}
						else if(!flags.has(SWP_NOMOVE))
						{
							if(ctx->_level.size() == 1)
							{
								wp->x += 1;
								wp->y += 1;
							}
							else
							{
								auto border = &theme->border;
								auto align = theme->layout.popup.align != 0x7F;
								auto prev_window = ctx->_level[ctx->_level.size() - 2];
								auto swap_popup = wp->x < prev_window->x;
								auto fr = FixedFrame.cx / 2;
								auto x = bz + theme->border.padding.width();

								wp->x += swap_popup ? -x : x;

								if(align)
								{
									auto v = theme->layout.popup.align;
									if(swap_popup)
									{
										if(v < 0)
											wp->x += fr + (v * -1);
										else
											wp->x += fr - v;
									}
									else
										wp->x += v;
								}
								else if(swap_popup)
									wp->x += FixedFrame.cx;
								else
								{
									if(border->size >= 3)
										wp->x -= FixedFrame.cx + ((theme->border.padding.right + border->size) / 2);
									else
										wp->x -= fr;
								}
							}

							if(wnd->has_scroll)
							{
								wp->y = (ctx->_rcMonitor.height() - wnd->height) / 2;
							}
							else if((wnd->height + 100) > (ctx->_rcMonitor.height() / 2))
							{
								//wp->y -= 50;
							}
							else if((wp->y + wnd->height) > (ctx->_rcMonitor.height() - ctx->dpi(40)))
							{
								//wp->y -= ctx->dpi(40);
								//wp->y /= 2;
								//(y(237)+ cy(1227)) /2
							}

							//_log.write(L"y(%d), cy(%d)\r\n", wp->y , wnd->height);

							wnd->x = wp->x;
							wnd->y = wp->y;

							if(!wnd->layer.handle)
							{
								// fixme
								if(!ctx->composition)
									//if(!theme->transparent)
										//if(theme->background.effect == 0)
								{
									//int sh = theme->border.radius ? 1 : 0;
									auto border = theme->border.size;
									if(border > 0 || theme->border.radius > 0)
									{
										wnd->Regoin(hWnd, border, border,
													1 + wnd->width - border,
													1 + wnd->height - border,
													theme->border.radius);
									}
								}

								//if(IsWindowVisible(hWnd))
								// load layer when window is not visible
								ctx->CreateLayer(wnd);
							}
						}
					}
					//lret = ::DefSubclassProc(hWnd, uMsg, wParam, lParam);
					return lret;
				}
				case WM_WINDOWPOSCHANGED:
				{
					auto wp = reinterpret_cast<WINDOWPOS *>(lParam);
					Flag<uint32_t> flags = wp->flags;
					if(flags.has(SWP_HIDEWINDOW) || flags.has(SWP_SHOWWINDOW))
						;
					else if(!flags.has(SWP_NOZORDER))
					{
						//if(hWnd == ctx->_level.back()->handle)
						{
							/*auto s = ctx->_level.size();
							if(s > 1)
							{
								auto hRoot = ctx->_level[0]->handle;
								::DestroyWindow(hRoot);
							}*/
						}
					}
					return lret;
					//wnd->rect = { wp->x, wp->y, wp->cx, wp->cy };
					//return ::DefSubclassProc(hWnd, uMsg, wParam, lParam);
				}
				case WM_PAINT:
				{
					ctx->begin_appearance_paint(wnd);
					lret = defSubclassProc();
					//auto_gdi<HBRUSH> _hblack(CreateSolidBrush(0x000000));
					//::FillRect(wnd->hdc, &wnd->rect, _hblack.get());

					if(wnd->has_scroll)
					{
						auto_gdi<HBRUSH> hbblack(CreateSolidBrush(0x000000));

						int h = ctx->dpi(14);
						Rect rc = { 0, 0, wnd->width, h };
						::FillRect(wnd->hdc, rc, hbblack.get());
						
						rc = { 0, wnd->height - h, wnd->width, wnd->height };
						::FillRect(wnd->hdc, rc, hbblack.get());
						
						ctx->draw_scroll_arrows(wnd->hdc, wnd->width, wnd->height);

						//::ExcludeClipRect(wnd->hdc, 0, 0, wnd->width, 20);
						//::ExcludeClipRect(wnd->hdc, 0, wnd->height - 20, wnd->width, wnd->height);
					}

					// exlude menu item rectangle to prevent drawing by windows after us
					//dc.exclude_clip_rect(*rc);
					ctx->finish_appearance_paint(wnd);
					ctx->schedule_appearance_capture(hWnd);
					return lret;
				}
				case WM_NCPAINT:
					//lret = defSubclassProc();
					return lret;
				case WM_ERASEBKGND:
				{
					if(++ixi == 0)
					{
						Rect r = hWnd;
						D2D d2d;
						
						d2d.begin(wnd->hdc, { 0, 0, r.width(), r.height() });
						
						//auto z = (float)theme->border.size*2;
						D2D1_RECT_F rect = { 0.0f, 0.0f, float(r.width()), float(r.height()) };

						//d2d.render->SetDpi(96.f, 96.f);
						d2d.render->SetTransform(D2D1::Matrix3x2F::Identity());
						//d2d.render->Clear(D2D1::ColorF(0.0f, 0.0f, 0.0f, 0.f));
						if(theme->border.radius > 0)
						{
							d2d.render->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
							auto radius = float(theme->border.radius);
							d2d.brush->SetColor(theme->background.color);
							d2d.render->FillRoundedRectangle({ rect, radius, radius }, d2d.brush);
						}
						else
						{
							d2d.brush->SetColor(theme->background.color);
							d2d.render->FillRectangle(rect, d2d.brush);
						}
						d2d.end(true);
					}
					lret = TRUE;
					//lret = ::DefSubclassProc(hWnd, uMsg, wParam, lParam);
					return lret;
				}
				case WM_SETCURSOR:
				{
					if(ctx->hCursor)
					{
						::SetCursor(ctx->hCursor);
						return TRUE;
					}
					break;
				}
				case MN_SELECTITEM:
				{
					auto cmdItem = static_cast<uint32_t>(wParam);
					if(cmdItem == MFMWFP_NOITEM)
					{
						if(lParam > 0)
						{
							//InvalidateRect(hWnd, &__o[lParam].rc, 0);

							//_log.info(L"### %d %x", cmdItem, lParam);
						}
					}
					else {
						__sel = -1;
					}

					ctx->current.hWnd = hWnd;
					ctx->current.selectitem_pos = cmdItem;
					lret = defSubclassProc();
				//cmdLast
					if(cmdItem != MFMWFP_NOITEM)
					{
						if(!wnd->hdc)
						wnd->hdc = GetWindowDC(hWnd);
						if(Keyboard::IsKeyDown(VK_RBUTTON))
						{
							//SendMessage(hWnd, MN_BUTTONDOWN, wParam, 0);
							//	EnableMenuItem(wnd->hMenu, cmdItem, true);
						}
						//if(ctx->current.select_previtem)
						//::InvalidateRect(hWnd, 0, 1);
					}
					//	wParam == MFMWFP_NOITEM
						/*pMenuBuilder->SetActiveMenu((HMENU)SendMessage(pcwps->hwnd, MN_GETMENU, 0, 0));
					// Check to see if it's the up or down arrow and if so...
					if ((pcwps->wParam == MENU_DOWN) || (pcwps->wParam == MENU_UP))
					  {
						// ...send a MN_BUTTONDOWN message to the menu window with that
						// arrow
						SendMessage(pcwps->hwnd, MN_BUTTONDOWN, pcwps->wParam, 0);
						buttonDown = true;
					  }
					else if (buttonDown)
					  {
						// ...or else send a MN_BUTTONUP message or WM_CONTEXTMENU is
						// not sent in the case of a WM_RBUTTONDOWN
						SendMessage(pcwps->hwnd, MN_BUTTONUP, pcwps->wParam, 0);
						buttonDown = false;
					  }
					*/
					return lret;
				}
				case WM_PRINT:
				case WM_PRINTCLIENT: //mouse
					//lret = defSubclassProc();
					return lret;
					/*
					 * wParam - the item to select. Must be a valid index or MFMWFP_NOITEM
					 * Returns the item flags of the wParam (0 if failure)
					 */
					 //case WM_SIZING:
	 //	RedrawWindow(hWnd, NULL, NULL, RDW_INVALIDATE | RDW_NOERASE | RDW_INTERNALPAINT);
				case WM_KEYUP:
				case WM_SYSKEYUP:
				{
					//auto vkCode = LOWORD(wParam); // virtual-key code
					return defSubclassProc();
				}
				case WM_SYSKEYDOWN:
				case WM_KEYDOWN:
				{
					/*auto vkCode = LOWORD(wParam); // virtual-key code
					_log.info(L"%d", vkCode);
					if(vkCode == VK_HOME)
						SendMessageW(hWnd, MN_SELECTITEM, 0, 0);
					else if(vkCode == VK_END)
						SendMessageW(hWnd, MN_SELECTITEM, -1, 0);
					*/
					return defSubclassProc();
				}
				/*
				 * wParam is position (index) of item the button was clicked on.
				 * Must be a valid index or MFMWFP_NOITEM
				 */
				 /*
		  * Cancels all menus, unselects everything, destroys windows, and cleans
		  * everything up for this hierarchy. wParam is the command to send and
		  * lParam says if it is valid or not.
		  */
				case MN_OPENHIERARCHY:
				{
					lret = defSubclassProc();
					return lret;
				}
				case MN_CLOSEHIERARCHY:
					break;
				case MN_SHOWPOPUPWINDOW:
					break;
				case MN_CANCELMENUS:
					break;
					/*
					*wParam is position(index) of item the button was up clicked on.
					*/
				case MN_BUTTONDOWN:
				{
					if(lParam == 0 && ctx)
					{
						ctx->mouse_button = (::GetAsyncKeyState(VK_RBUTTON) & 0x8000) == 0x8000 ? VK_RBUTTON : VK_LBUTTON;
					}
					break;
				}
				case MN_BUTTONUP:
				{
					//auto hMenu = (HMENU)::SendMessageW(hWnd, MN_GETHMENU, 0, 0);
					//wParam is position(index) of item the button was up clicked on.
					MENUITEMINFOW mii = { sizeof(mii), MIIM_TYPE | MIIM_ID | MIIM_SUBMENU | MIIM_STATE };
					if(::GetMenuItemInfoW(wnd->hMenu/*_this->current.hMenu*/, (uint32_t)wParam, MF_BYPOSITION, &mii))
					{
						if(mii.hSubMenu == nullptr && !(mii.fState & MFS_DISABLED) && !(mii.fType & MFT_SEPARATOR))
						{
							ctx->selectid = mii.wID;
							ctx->current.selectid = mii.wID;
						}
					}

					int pvParam{};
					::SystemParametersInfoW(SPI_GETSELECTIONFADE, 0, &pvParam, 0);
					if(!pvParam)
					{
						// Fade out animation is disabled system-wide
						break;
					}
					// We need to prevent the system default menu fade out animation
					// and begin a re-implemented one
					// 
					// Windows does not show animation if the selection was done
					// with keyboard (i.e. Enter)

					::SystemParametersInfoW(SPI_SETSELECTIONFADE, 0, FALSE, 0);
					lret = defSubclassProc();
					::SystemParametersInfoW(SPI_SETSELECTIONFADE, 0, (PVOID)TRUE, 0);
					return lret;
				}
				break;
				case MN_DBLCLK:
					//return FALSE;
					break;
				case MN_FINDMENUWINDOWFROMPOINT://mouse
				{
					lret = defSubclassProc();
					//auto h = (HWND)ret;
					//m_log->info(L"%s", Window(h).classn_name().c_str());
					return lret;
				}
				case WM_MOUSEHWHEEL:
				case WM_MOUSEWHEEL:
				{
					int delta = GET_WHEEL_DELTA_WPARAM(wParam);
					if(delta > 0)
					{
						//Mouse Wheel Up
					}
					else
					{
						//Mouse Wheel Down
					}

					lret = defSubclassProc();
					//auto h = (HWND)ret;
					//m_log->info(L"%s", Window(h).classn_name().c_str());
					return lret;
				}
				case WM_CHAR:
				case WM_SYSCHAR:
				case WM_MENUCHAR:
					break;
				case WM_TIMER:
				{
					switch(wParam)
					{
						case IDSYS_MNSHOW:
							/*
							 * Open the window and kill the show timer.
							 *
							 * Cancel any toggle state we might have. We don't
							 * want to dismiss this on button up if shown from
							 * button down.
							 */
							break;

						case IDSYS_MNHIDE:
							break;

						case IDSYS_MNUP:
							break;
						case IDSYS_MNDOWN:
							wnd->scrolled = true;
							//if(pMenuState->fButtonDown) {
							//	xxxMNDoScroll(ppopupmenu, (UINT)wParam, FALSE);
							//}
							//else {
							//	_KillTimer(pwnd, (UINT)wParam);
							break;
						default:
							//_log.info(L"%x", wParam);
							break;
					}

					break;
				}
				case MN_SETHMENU:
					break;
					//mouse
				case MN_MOUSEMOVE:
					break;
				case WM_MOUSEACTIVATE:
				case WM_NCMOUSEMOVE:
				case WM_MOUSELEAVE:
				case WM_DEVICECHANGE:
				case WM_CREATE:
					break;
				case WM_NCHITTEST:
				case MN_SETTIMERTOOPENHIERARCHY:
				case MN_ENDMENU:
				case WM_UAHDESTROYWINDOW:
				/*case WM_UAHINITMENUPOPUP:
					//	return OnUAHDrawPopupMenuProc(hWnd, uMsg, wParam, lParam);
				case WM_UAHNCPAINTMENUPOPUP:
				case WM_UAHDRAWMENU:
					break;
				case WM_NCUAHDRAWFRAME:
					break;
				case WM_UAHMEASUREMENUITEM:
					break;
				case WM_UAHDRAWMENUITEM:*/
					break;
			/*	case WM_UAHINITMENUPOPUP:
				case WM_UAHDRAWMENU:
				case WM_UAHDRAWMENUITEM:
				case WM_UAHMEASUREMENUITEM:
				case WM_UAHNCPAINTMENUPOPUP:
						_log.info(L"%0.4x", uMsg);
						return 0;
					break;*/
					// not handle
				//	case WM_SHOWWINDOW:
				//		beep;
				default:
					break;
			}
			//_log.info(L"0x%0.4x\t%s", uMsg, msg_map[uMsg]);
			return defSubclassProc();
		}


		LRESULT __stdcall ContextMenu::KeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
		{
			int alt = static_cast<int> ((lParam >> 29) & 1);
			int key_down = static_cast<int> (!((lParam >> 30) & 1));
			int key_up = static_cast<int> (((lParam >> 31) & 1));
			int scancode = static_cast<int> ((lParam >> 16) & 0xFF);

			// Code < 0 is windows telling us 'don't process this message'.
			if(nCode != 0)  // do not process message 
				goto skip;

			//::GetWindow(GetActiveWindow(), GW_ENABLEDPOPUP))

			// If alt-F12 is pressed, release hook
			if(alt && key_down && (scancode == 88))
			{
			}

			if(key_up)
			{
				if(wParam == VK_SNAPSHOT)
				{
					::EnumThreadWindows(::GetCurrentThreadId(), [](HWND hWnd, LPARAM)->BOOL
					{
						if(auto ctx = ContextMenu::Prop::Get(hWnd); ctx)
						{
							if(Window::IsPopupMenu(hWnd))
							{
								ctx->screenshot();
								return FALSE;
							}
						}
						return TRUE;
					}, 0);
				}
			}

		skip:
			// NOTE: The first parameter is always ignored
			return WindowsHook::CallNext(nullptr, nCode, wParam, lParam);
		}

		void __stdcall ContextMenu::WinEventProc([[maybe_unused]] HWINEVENTHOOK hWinEventHook,
												 DWORD dwEvent, HWND hWnd, LONG idObject,
												 [[maybe_unused]] LONG idChild, [[maybe_unused]] DWORD idEventThread, DWORD)
		{
			if(dwEvent != EVENT_OBJECT_CREATE && dwEvent != EVENT_OBJECT_SHOW)
				return;

			if(idObject != OBJID_WINDOW || !::IsWindow(hWnd))
				return;
			//idChild == INDEXID_CONTAINER
			if(Window::IsPopupMenu(hWnd))
			{
				if(dwEvent == EVENT_OBJECT_CREATE)
				{
					if(auto ctx = HookMap[hWinEventHook]; ctx)
						ctx->OnMenuCreate(hWnd);
				}
				else if(dwEvent == EVENT_OBJECT_SHOW)
				{
					if(auto ctx = Prop::Get(hWnd); ctx)
						ctx->OnMenuShow(hWnd);
				}
			}
		}


#pragma endregion

		//WindowProc
		LRESULT __stdcall ContextMenu::WindowSubclassProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam,
														  UINT_PTR uIdSubclass,
														  DWORD_PTR dwRefData)
		{
			if(CONTEXTMENUSUBCLASS != uIdSubclass)
				return ::DefSubclassProc(hWnd, uMsg, wParam, lParam);

			__try
			{
				auto ctx = reinterpret_cast<ContextMenu *>(dwRefData);
				if(ctx == nullptr)
				{
					ctx = Prop::Get(hWnd);
					if(ctx == nullptr)
						return ::DefSubclassProc(hWnd, uMsg, wParam, lParam);
				}

				ctx->msg.hWnd = hWnd;
				ctx->msg.uMsg = uMsg;
				ctx->msg.wParam = wParam;
				ctx->msg.lParam = lParam;

				switch(uMsg)
				{
					case StudioCapture::CaptureArmedMessage:
						// A worker-thread handshake can complete after Initialize has
						// started ordinary menu construction.  Retry on the owner thread
						// so the original HMENU is never dereferenced by the worker and
						// the snapshot is immutable before it enters the IPC queue.
						if(reinterpret_cast<StudioCapture *>(wParam) == &ctx->_studio_capture)
						{
							const auto epoch = ctx->_studio_capture.ActiveEpoch();
							if(!epoch || epoch != static_cast<uint64_t>(lParam))
								return 0;
							ctx->cancel_appearance_capture_timer();
							ctx->_studio_appearance_published.clear();
							for(auto &entry : ctx->_map)
								if(entry.second.studio_appearance.epoch != epoch)
								entry.second.studio_appearance.clear();
						ctx->publish_original_capture_if_armed();
							ctx->capture_unopened_submenus(nullptr);
							// The handshake may arrive after a popup is already visible;
							// defer only the current topmost popup without reopening menus
							// or executing commands.
							if(!ctx->_studio_popup_order.empty())
								ctx->schedule_appearance_capture(ctx->_studio_popup_order.back());
						}
						return 0;
					case StudioCapture::CaptureRetiredMessage:
						if(reinterpret_cast<StudioCapture *>(wParam) == &ctx->_studio_capture)
						{
							for(auto &entry : ctx->_map)
								if(entry.second.studio_appearance.epoch == static_cast<uint64_t>(lParam))
									entry.second.studio_appearance.clear();
							if(!ctx->_studio_capture.ActiveEpoch())
							{
								ctx->cancel_appearance_capture_timer();
								ctx->_studio_appearance_published.clear();
							}
						}
						return 0;
					case WM_ENTERMENULOOP:
					{
						//_log.info(L"WM_ENTERMENULOOP");
						if(wParam == TRUE)
							return ctx->OnStart();
						break;
					}
					case WM_EXITMENULOOP:
					{
						if(wParam == TRUE)
							return ctx->OnEnd();
						break;
					}
					case WM_INITMENU:
					{
						//_log.info(L"WM_INITMENU");
						if(auto hMenu = reinterpret_cast<HMENU>(wParam); hMenu)
							return ctx->OnInitMenu(hMenu);
						break;
					}
					/*
					lParam
						The low-order word specifies the zero-based relative position of the menu item that opens the drop-down menu or submenu.
						The high-order word indicates whether the drop-down menu is the window menu. If the menu is the window menu, this parameter is TRUE; otherwise, it is FALSE.
					*/
					case WM_INITMENUPOPUP:
					{
						//_log.info(L"WM_INITMENUPOPUP");
						auto lp = HIWORD(lParam);
					//	if(lParam != 0xFFFFFFFF)
					//		_log.info(L"WM_INITMENUPOPUP %d %d %d", HIWORD(lParam), LOWORD(lParam), lParam);
						if(lp == FALSE)
						{
							//MENUINFO info{0};
							//info.cbSize = sizeof(info);
							//info.fMask = MIM_STYLE | MIM_MAXHEIGHT| MIM_MENUDATA;
							//GetMenuInfo(reinterpret_cast<HMENU>(wParam), &info);
							//_log.info(L"WM_INITMENUPOPUP %d 0x%08x 0x%08x", GetMenuItemCount(reinterpret_cast<HMENU>(wParam)), info.dwStyle, info.dwMenuData);
							if(auto hMenu = reinterpret_cast<HMENU>(wParam); hMenu)
								return ctx->OnInitMenuPopup(hMenu, LOWORD(lParam));
						}
						else if(lp == 100) // preinitmenupopup
						{
							lParam = MAKELPARAM(LOWORD(lParam), lp);
						}
						break;
					}
					//lParam
					//	The high - order word identifies the menu that was destroyed.Currently, this parameter can only be MF_SYSMENU(the window menu).
					case WM_UNINITMENUPOPUP:
					{
						if(HIWORD(lParam) == FALSE)
						{
							if(auto hMenu = reinterpret_cast<HMENU>(wParam); hMenu)
								return ctx->OnUninitMenuPopup(hMenu);
							return 0;
						}
						break;
					}
					case WM_MEASUREITEM:
					{
						//_log.info(L"WM_MEASUREITEM %x", wParam);
						auto mi = reinterpret_cast<MEASUREITEMSTRUCT *>(lParam);
						//Only process if this notification is actually for a menu.
						if(mi->CtlType == ODT_MENU/* && wParam == 0*/)
							return ctx->OnMeasureItem(mi);
						break;
					}
					case WM_DRAWITEM:
					{
						//_log.info(L"WM_DRAWITEM");
						auto di = reinterpret_cast<DRAWITEMSTRUCT *>(lParam);
						//Only process if this notification is actually for a menu.
						if(di->CtlType == ODT_MENU/* && wParam == 0*/)
							return ctx->OnDrawItem(di);
						break;
					}
					case WM_MENUSELECT:
						return ctx->OnMenuSelect(reinterpret_cast<HMENU>(lParam), LOWORD(wParam), HIWORD(wParam));
					case WM_ENTERIDLE:
						if(wParam == MSGF_MENU)
						{
							//_log.info(L"WM_ENTERIDLE %x %x", wParam, lParam);
							/*if(!ctx->HWNDMenu)
							{
								ctx->HWNDMenu = reinterpret_cast<HWND>(lParam);
							}*/
						}
						break;
					case WM_TIMER:
						if(ctx->_studio_appearance_timer_id != 0 &&
							static_cast<UINT_PTR>(wParam) == ctx->_studio_appearance_timer_id &&
							ctx->_studio_appearance_timer_owner == hWnd)
						{
							const auto target = ctx->_studio_appearance_timer_hwnd;
							ctx->cancel_appearance_capture_timer();
							ctx->publish_appearance_after_paint(target);
							return 0;
						}
						return ctx->OnTimer(static_cast<UINT_PTR>(wParam), reinterpret_cast<TIMERPROC>(lParam));
					case WM_MOUSEMOVE:
					{
						/*if(ctx->_level.size() == 1)
						{
						//	_log.info(L"**** WM_MOUSEMOVE %x %x", WM_MOUSEMOVE, wParam, lParam);
						//	PostMessage(ctx->_level[0]->handle, WM_MOUSEMOVE, wParam, lParam);
						}*/
						break;
					}
					// handled by DefWindowProc
					case WM_UAHINITMENUPOPUP:
					{
						//auto uahmenu = (UAHMENU *)lParam;
						//uahmenu->dwFlags = 0x4000401;WS_EX_UISTATEACTIVE
						//_log.info(L"WM_UAHINITMENUPOPUP %d 0x%08x 0x%08x", GetMenuItemCount(uahmenu->hmenu), uahmenu->dwFlags, wParam);
						//if(auto hMenu = reinterpret_cast<HMENU>(wParam); hMenu)
						//return ctx->OnInitMenuPopup(uahmenu->hmenu, 0);
						break;
					}
					case WM_UAHMEASUREMENUITEM:
					{
						//_log.info(L"WM_UAHMEASUREMENUITEM %08x", wParam);
						/*auto mi = reinterpret_cast<UAHMEASUREMENUITEM *>(lParam);
						//Only process if this notification is actually for a menu.
						if(mi->mis.CtlType == ODT_MENU)
							return ctx->OnMeasureItem(&mi->mis);*/
						break;
					}
					case WM_UAHDRAWMENU:
					case WM_UAHDRAWMENUITEM:
					case WM_UAHNCPAINTMENUPOPUP:
						//_log.info(L"%0.4x", uMsg);
						
						break;
					case WM_CAPTURECHANGED:
						break;
					/*case 0x04a3:
					case 0x04ad:
					case 0x04ae:
					case 0x04af:
					case 0x04b0:
						return 0;*/
				//	default:
						//_log.info(L"%0.4x %s", uMsg, msg_map[uMsg]); break;
						/*	case WM_MENUCHAR:
							case WM_NEXTMENU:
							case WM_MENUCOMMAND:
							case WM_COMMAND:
							case WM_SYSCOMMAND:
							// modify async menu item
							case WM_ERASEBKGND:
							case WM_PRINTCLIENT:
							case WM_UAHDRAWMENU:
							case WM_UAHDRAWMENUITEM:
							case WM_UAHINITMENUPOPUP:
							case WM_UAHMEASUREMENUITEM:
							case WM_UAHNCPAINTMENUPOPUP
								break;
							*/
							//_log.info(L"msg = 0x%0.4x, wParam = 0x%0.8x, lParam = 0x%0.8x, WindProc, %s", uMsg, wParam, lParam, msg_map[uMsg]);
				}
				//_log.info(L"msg = 0x%04x, wParam = 0x%08x, lParam = 0x%08x, WindProc, %s", uMsg, wParam, lParam, msg_map[uMsg]);
			}
			__except(EXCEPTION_EXECUTE_HANDLER) {}
			return ::DefSubclassProc(hWnd, uMsg, wParam, lParam);
		}
	}
}
#pragma endregion

LRESULT LayerProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam)
{
	if(uMsg == WM_WINDOWPOSCHANGED)
	{
		auto wp = reinterpret_cast<WINDOWPOS *>(lParam);
		Flag<uint32_t> flags = wp->flags;
		if(flags.has(SWP_HIDEWINDOW))
		{
		}
		else if(flags.has(SWP_SHOWWINDOW))
		{
		}
		else
		{
			if(!flags.has(SWP_NOSIZE) || flags.has(SWP_FRAMECHANGED)) {
				//UpdateShadowShape(pwnd);
			}
			else if(!flags.has(SWP_NOMOVE)) {
				//MoveShadow(pwnd);
			}

			if(!flags.has(SWP_NOZORDER))
			{
				::SetWindowPos(hWnd, nullptr,
							   0, 0, 0, 0,
							   SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
							   SWP_NOZORDER | SWP_NOOWNERZORDER |
							   SWP_NOACTIVATE | SWP_SHOWWINDOW);
			}
		}
	}

	return ::DefWindowProcW(hWnd, uMsg, wParam, lParam);
}
