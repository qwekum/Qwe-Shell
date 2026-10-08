#include <System.h>
#include "Include/NativeMenuRenderer.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <mutex>
#include <strsafe.h>

#include <dwmapi.h>
#include <uxtheme.h>
#include <vssym32.h>

#include <Library/PlutoVGWrap.h>

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
		constexpr uint32_t kMaxRendererWidth = 2048;
		constexpr uint32_t kMaxRendererHeight = 4096;
		constexpr uint32_t kMaxRendererBytes = 16U * 1024U * 1024U;

		bool ValidDpi(uint32_t dpi) noexcept
		{
			return dpi >= NativeMenuRendererMinDpi &&
				dpi <= NativeMenuRendererMaxDpi;
		}

		bool ValidRect(const RECT &rect) noexcept
		{
			return rect.right > rect.left && rect.bottom > rect.top;
		}

		long Scale96(long value, uint32_t dpi) noexcept
		{
			if(value == 0 || dpi == 96)
				return value;
			const auto scaled = (static_cast<int64_t>(value) * dpi + 48) / 96;
			if(scaled > std::numeric_limits<long>::max())
				return std::numeric_limits<long>::max();
			if(scaled < std::numeric_limits<long>::min())
				return std::numeric_limits<long>::min();
			return static_cast<long>(scaled);
		}

		RECT TranslateRect(const RECT &rect, long x, long y) noexcept
		{
			return {rect.left + x, rect.top + y, rect.right + x, rect.bottom + y};
		}

		RECT IntersectToBounds(const RECT &rect, const RECT &bounds,
			bool *clipped = nullptr) noexcept
		{
			RECT result{};
			const bool intersects = ::IntersectRect(&result, &rect, &bounds) != FALSE;
			if(clipped)
				*clipped = intersects && (result.left != rect.left ||
					result.top != rect.top || result.right != rect.right ||
					result.bottom != rect.bottom);
			return intersects ? result : RECT{};
		}

		RECT MirrorRect(const RECT &rect, const RECT &bounds) noexcept
		{
			return {
				bounds.left + bounds.right - rect.right,
				rect.top,
				bounds.left + bounds.right - rect.left,
				rect.bottom};
		}

		NativeMenuColor OpaqueIfRequested(NativeMenuColor color,
			const NativeMenuRenderTheme &theme) noexcept
		{
			if(theme.composition && !theme.opaqueInterior && color.a == 255)
				color.a = 0;
			else if(theme.opaqueInterior && color.a == 0)
				color.a = 255;
			return color;
		}

		long TextTap(const NativeMenuRenderTheme &theme) noexcept
		{
			return theme.textTap ? static_cast<long>(theme.textTap) :
				static_cast<long>(theme.imageGap);
		}

		void Premultiply(uint8_t *pixel) noexcept
		{
			if(!pixel)
				return;
			if(pixel[3] == 0)
			{
				if(pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0)
					pixel[3] = 255;
				else
					return;
			}
			const auto alpha = pixel[3];
			if(pixel[0] > alpha)
				pixel[0] = static_cast<uint8_t>((static_cast<uint32_t>(pixel[0]) * alpha + 127U) / 255U);
			if(pixel[1] > alpha)
				pixel[1] = static_cast<uint8_t>((static_cast<uint32_t>(pixel[1]) * alpha + 127U) / 255U);
			if(pixel[2] > alpha)
				pixel[2] = static_cast<uint8_t>((static_cast<uint32_t>(pixel[2]) * alpha + 127U) / 255U);
		}

		bool FillColor(HDC target, const RECT &rect, NativeMenuColor color,
			bool opaque = false) noexcept
		{
			if(!target || !ValidRect(rect) || color.a == 0)
				return color.a == 0;
			if(opaque || color.a == 255)
			{
				auto brush = ::CreateSolidBrush(color.rgb());
				if(!brush)
					return false;
				const auto result = ::FillRect(target, &rect, brush) != FALSE;
				::DeleteObject(brush);
				return result;
			}

			const auto width = rect.right - rect.left;
			const auto height = rect.bottom - rect.top;
			BITMAPINFO info{};
			info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
			info.bmiHeader.biWidth = width;
			info.bmiHeader.biHeight = -height;
			info.bmiHeader.biPlanes = 1;
			info.bmiHeader.biBitCount = 32;
			info.bmiHeader.biCompression = BI_RGB;
			uint8_t *bits{};
			auto bitmap = ::CreateDIBSection(target, &info, DIB_RGB_COLORS,
				reinterpret_cast<void **>(&bits), nullptr, 0);
			if(!bitmap || !bits)
			{
				if(bitmap)
					::DeleteObject(bitmap);
				return false;
			}
			const auto count = static_cast<size_t>(width) *
				static_cast<size_t>(height);
			const auto alpha = color.a;
			const auto b = static_cast<uint8_t>((static_cast<uint32_t>(color.b) * alpha + 127U) / 255U);
			const auto g = static_cast<uint8_t>((static_cast<uint32_t>(color.g) * alpha + 127U) / 255U);
			const auto r = static_cast<uint8_t>((static_cast<uint32_t>(color.r) * alpha + 127U) / 255U);
			for(size_t i = 0; i < count; ++i)
			{
				bits[i * 4U] = b;
				bits[i * 4U + 1U] = g;
				bits[i * 4U + 2U] = r;
				bits[i * 4U + 3U] = alpha;
			}

			auto memory = ::CreateCompatibleDC(target);
			if(!memory)
			{
				::DeleteObject(bitmap);
				return false;
			}
			auto oldBitmap = ::SelectObject(memory, bitmap);
			const BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
			const auto result = oldBitmap && oldBitmap != HGDI_ERROR &&
				::GdiAlphaBlend(target, rect.left, rect.top, width, height,
					memory, 0, 0, width, height, blend) != FALSE;
			if(oldBitmap && oldBitmap != HGDI_ERROR)
				::SelectObject(memory, oldBitmap);
			::DeleteDC(memory);
			::DeleteObject(bitmap);
			return result;
		}

		bool BlendBitmap(HDC target, const RECT &destination,
			HBITMAP bitmap, SIZE sourceSize, uint8_t opacity) noexcept;

		bool FillGradient(HDC target, const RECT &rect,
			const NativeMenuRenderTheme &theme) noexcept
		{
			if(!target || !ValidRect(rect) || !theme.gradient.enabled ||
				theme.gradient.stops.empty())
				return true;
			const auto width = rect.right - rect.left;
			const auto height = rect.bottom - rect.top;
			if(width <= 0 || height <= 0 || width > static_cast<long>(kMaxRendererWidth) ||
				height > static_cast<long>(kMaxRendererHeight))
				return false;
			PlutoVG vector(width, height);
			if(!vector)
				return false;
			plutovg_gradient_t *gradient{};
			const auto percent = [](double value, double extent) noexcept
			{
				return value * extent / 100.0;
			};
			if(theme.gradient.linear[0] == 1.0)
			{
				gradient = plutovg_gradient_create_linear(
					percent(theme.gradient.linear[1], width),
					percent(theme.gradient.linear[2], height),
					percent(theme.gradient.linear[3], width),
					percent(theme.gradient.linear[4], height));
			}
			else if(theme.gradient.radial[0] == 1.0)
			{
				const auto radiusExtent = static_cast<double>((std::min)(width, height)) / 2.0;
				gradient = plutovg_gradient_create_radial(
					percent(theme.gradient.radial[1], width / 2.0),
					percent(theme.gradient.radial[2], height / 2.0),
					percent(theme.gradient.radial[3], radiusExtent),
					percent(theme.gradient.radial[4], width / 2.0),
					percent(theme.gradient.radial[5], height / 2.0), 0.0);
			}
			if(!gradient)
				return true;
			for(const auto &stop : theme.gradient.stops)
			{
				const auto offset = std::clamp(stop.offset, 0.0, 1.0);
				plutovg_gradient_add_stop_rgba(gradient, offset,
					static_cast<double>(stop.color.r) / 255.0,
					static_cast<double>(stop.color.g) / 255.0,
					static_cast<double>(stop.color.b) / 255.0,
					static_cast<double>(stop.color.a) / 255.0);
			}
			vector.rect(0, 0, width, height).fill(gradient);
			const auto bitmap = vector.tobitmap();
			plutovg_gradient_destroy(gradient);
			if(!bitmap)
				return false;
			const auto result = BlendBitmap(target, rect, bitmap,
				{width, height}, 255);
			::DeleteObject(bitmap);
			return result;
		}

		// GDI's normal fills leave the alpha byte untouched. A composed caller
		// may reuse an opaque DIB, so clear its pixels before painting the new
		// frame. This helper is intentionally limited to 32-bit DIB sections;
		// other HDCs remain valid renderer targets and simply skip the clear.
		bool ClearDibRect(HDC target, const RECT &requested) noexcept
		{
			if(!target || !ValidRect(requested))
				return false;
			const auto bitmap = static_cast<HBITMAP>(::GetCurrentObject(
				target, OBJ_BITMAP));
			if(!bitmap)
				return false;
			DIBSECTION section{};
			if(::GetObjectW(bitmap, sizeof(section), &section) != sizeof(section) ||
				section.dsBm.bmBitsPixel != 32 || !section.dsBm.bmBits ||
				section.dsBm.bmWidth <= 0 || section.dsBm.bmHeight == 0 ||
				section.dsBm.bmWidthBytes <= 0)
				return false;
			const RECT bitmapBounds{0, 0, section.dsBm.bmWidth,
				std::abs(section.dsBm.bmHeight)};
			RECT clipped{};
			if(!::IntersectRect(&clipped, &requested, &bitmapBounds))
				return true;
			const auto stride = static_cast<size_t>(section.dsBm.bmWidthBytes);
			const auto rowBytes = static_cast<size_t>(clipped.right - clipped.left) * 4U;
			for(long y = clipped.top; y < clipped.bottom; ++y)
			{
				auto *row = static_cast<uint8_t *>(section.dsBm.bmBits) +
					static_cast<size_t>(y) * stride +
					static_cast<size_t>(clipped.left) * 4U;
				std::memset(row, 0, rowBytes);
			}
			return true;
		}

		bool BlendBitmap(HDC target, const RECT &destination,
			HBITMAP bitmap, SIZE sourceSize, uint8_t opacity = 255) noexcept
		{
			if(!target || !bitmap || !ValidRect(destination))
				return false;
			BITMAP info{};
			const auto hasInfo = ::GetObjectW(bitmap, sizeof(info), &info) ==
				sizeof(info);
			if(sourceSize.cx <= 0 || sourceSize.cy <= 0)
			{
				if(!hasInfo)
					return false;
				sourceSize = {info.bmWidth, std::abs(info.bmHeight)};
			}
			if(sourceSize.cx <= 0 || sourceSize.cy <= 0)
				return false;
			auto memory = ::CreateCompatibleDC(target);
			if(!memory)
				return false;
			auto oldBitmap = ::SelectObject(memory, bitmap);
			HBITMAP copiedBitmap{};
			SIZE drawSourceSize = sourceSize;
			if(!oldBitmap || oldBitmap == HGDI_ERROR)
			{
				// A request-owned preview image can still be selected into the
				// surface that retains its lifetime. GDI rejects selecting such a
				// bitmap into this temporary DC; copy bounded 32-bit DIB pixels so
				// the renderer remains independent of the resource owner's DC.
				DIBSECTION section{};
				const auto sectionSize = ::GetObjectW(bitmap, sizeof(section), &section);
				const auto width = section.dsBm.bmWidth;
				const auto height = std::abs(section.dsBm.bmHeight);
				const auto rowBytes = width > 0 ? static_cast<size_t>(width) * 4U : 0U;
				const auto stride = section.dsBm.bmWidthBytes > 0
					? static_cast<size_t>(section.dsBm.bmWidthBytes) : 0U;
				if(sectionSize != sizeof(section) || section.dsBm.bmBitsPixel != 32 ||
					!section.dsBm.bmBits || width <= 0 || height <= 0 ||
					width > static_cast<LONG>(kMaxRendererWidth) ||
					height > static_cast<LONG>(kMaxRendererHeight) ||
					rowBytes == 0 || stride < rowBytes)
				{
					::DeleteDC(memory);
					return false;
				}
				BITMAPINFO copyInfo{};
				copyInfo.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
				copyInfo.bmiHeader.biWidth = width;
				copyInfo.bmiHeader.biHeight = -height;
				copyInfo.bmiHeader.biPlanes = 1;
				copyInfo.bmiHeader.biBitCount = 32;
				copyInfo.bmiHeader.biCompression = BI_RGB;
				uint8_t *copyBits{};
				copiedBitmap = ::CreateDIBSection(memory, &copyInfo,
					DIB_RGB_COLORS, reinterpret_cast<void **>(&copyBits), nullptr, 0);
				if(!copiedBitmap || !copyBits)
				{
					if(copiedBitmap)
						::DeleteObject(copiedBitmap);
					::DeleteDC(memory);
					return false;
				}
				const auto *sourceBits = static_cast<const uint8_t *>(section.dsBm.bmBits);
				for(LONG y = 0; y < height; ++y)
				{
					const auto sourceY = section.dsBm.bmHeight < 0 ? y : height - 1 - y;
					std::memcpy(copyBits + static_cast<size_t>(y) * rowBytes,
						sourceBits + static_cast<size_t>(sourceY) * stride, rowBytes);
				}
				oldBitmap = ::GetCurrentObject(memory, OBJ_BITMAP);
				if(!oldBitmap || oldBitmap == HGDI_ERROR)
				{
					::DeleteObject(copiedBitmap);
					::DeleteDC(memory);
					return false;
				}
				const auto replacedBitmap = ::SelectObject(memory, copiedBitmap);
				if(!replacedBitmap || replacedBitmap == HGDI_ERROR)
				{
					::DeleteObject(copiedBitmap);
					::DeleteDC(memory);
					return false;
				}
				drawSourceSize = {width, height};
			}

			const auto is32 = hasInfo && info.bmBitsPixel == 32;
			BOOL result = FALSE;
			if(opacity == 255 && !is32)
			{
				result = ::StretchBlt(target, destination.left, destination.top,
					destination.right - destination.left, destination.bottom - destination.top,
					memory, 0, 0, drawSourceSize.cx, drawSourceSize.cy, SRCCOPY);
			}
			else
			{
				const BLENDFUNCTION blend{AC_SRC_OVER, 0, opacity, AC_SRC_ALPHA};
				result = ::GdiAlphaBlend(target, destination.left, destination.top,
					destination.right - destination.left, destination.bottom - destination.top,
					memory, 0, 0, drawSourceSize.cx, drawSourceSize.cy, blend);
			}
			::SelectObject(memory, oldBitmap);
			::DeleteDC(memory);
			if(copiedBitmap)
				::DeleteObject(copiedBitmap);
			return result != FALSE;
		}

		bool DrawRounded(HDC target, const RECT &rect, NativeMenuColor fill,
			NativeMenuColor border, uint32_t radius) noexcept
		{
			if(!target || !ValidRect(rect))
				return false;
			const auto width = rect.right - rect.left;
			const auto height = rect.bottom - rect.top;
			PlutoVG vector(width, height);
			if(!vector)
				return false;
			const auto radiusValue = static_cast<double>(std::min<uint32_t>(
				radius, static_cast<uint32_t>(std::min(width, height) / 2)));
			vector.rect(border.a ? .5 : 0.0, border.a ? .5 : 0.0,
				border.a ? width - 1.0 : width, border.a ? height - 1.0 : height,
				radiusValue);
			if(fill.a)
				vector.fill(fill.r, fill.g, fill.b, fill.a, border.a != 0);
			if(border.a)
				vector.stroke_width(1).stroke_fill(border.r, border.g, border.b, border.a);
			auto bitmap = vector.tobitmap(static_cast<uint8_t **>(nullptr));
			if(!bitmap)
				return false;
			const auto result = BlendBitmap(target, rect, bitmap,
				{width, height}, 255);
			::DeleteObject(bitmap);
			return result;
		}

		bool DrawMenuShadow(HDC target, const RECT &bounds,
			const NativeMenuRenderTheme &theme) noexcept
		{
			if(!target || !ValidRect(bounds) || !theme.shadowEnabled ||
				theme.shadowSize == 0 || theme.shadow.a == 0)
				return true;

			// The offscreen contract keeps the requested menu bounds stable so row
			// hit targets remain comparable with captured Explorer geometry.  Draw
			// the bounded shadow inside that surface; a live composed popup can still
			// provide the native outside-window shadow independently.
			const auto extent = static_cast<uint32_t>(std::min<uint32_t>(
				theme.shadowSize, 30U));
			const auto offset = static_cast<long>(std::min<uint32_t>(
				theme.shadowOffset, 30U));
			for(uint32_t layer = 0; layer <= extent; ++layer)
			{
				RECT ring = bounds;
				const auto inset = static_cast<long>(extent - layer);
				ring.left += offset + inset;
				ring.top += offset + inset;
				ring.right -= inset;
				ring.bottom -= inset;
				if(!ValidRect(ring))
					continue;

				NativeMenuColor color = theme.shadow;
				// Match the runtime's intentionally subtle shadow treatment while
				// preserving changes to both color and opacity in deterministic output.
				const auto weight = layer + 1U;
				color.a = static_cast<uint8_t>((static_cast<uint32_t>(theme.shadow.a) *
					weight + extent / 2U) / (extent + 1U));
				if(!DrawRounded(target, ring, NativeMenuColor{}, color,
					theme.frameRadius + layer / 2U))
					return false;
			}
			return true;
		}

		bool DrawTextDirect(HDC target, HTHEME theme, HFONT font,
			const std::wstring &text, RECT rect, NativeMenuColor color,
			UINT format, bool *usedTheme) noexcept
		{
			if(!target || text.empty() || !ValidRect(rect) || color.a == 0)
				return true;
			const auto oldMode = ::SetBkMode(target, TRANSPARENT);
			auto oldFont = font ? ::SelectObject(target, font) : nullptr;
			BOOL result = FALSE;
			if(theme)
			{
				DTTOPTS options{sizeof(options)};
				options.dwFlags = DTT_COMPOSITED | DTT_TEXTCOLOR;
				options.crText = color.rgb();
				result = SUCCEEDED(::DrawThemeTextEx(theme, target, 0, 0,
					text.c_str(), static_cast<int>(std::min<size_t>(text.size(),
						static_cast<size_t>(std::numeric_limits<int>::max()))),
					format, &rect, &options));
				if(result && usedTheme)
					*usedTheme = true;
			}
			if(!result)
			{
				::SetTextColor(target, color.rgb());
				result = ::DrawTextW(target, text.c_str(),
					static_cast<int>(std::min<size_t>(text.size(),
						static_cast<size_t>(std::numeric_limits<int>::max()))),
					&rect, format) != 0;
			}
			if(oldFont)
				::SelectObject(target, oldFont);
			::SetBkMode(target, oldMode);
			return result != FALSE;
		}

		bool DrawTextNative(HDC target, const NativeMenuRenderTheme &theme,
			const std::wstring &text, RECT rect, NativeMenuColor color,
			UINT format, bool *usedTheme) noexcept
		{
			// DrawThemeTextEx and DrawTextW both support the native text metrics and
			// Unicode/RTL flags.  Opaque menu text can be drawn directly, while an
			// alpha text color is rendered into a small transparent DIB first so
			// disabled themes retain their intended opacity on composed surfaces.
			if(color.a == 255)
				return DrawTextDirect(target, theme.menuTheme, theme.textFont,
					text, rect, color, format, usedTheme);

			const auto width = rect.right - rect.left;
			const auto height = rect.bottom - rect.top;
			if(width <= 0 || height <= 0 || width > static_cast<long>(kMaxRendererWidth) ||
				height > static_cast<long>(kMaxRendererHeight))
				return false;
			BITMAPINFO info{};
			info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
			info.bmiHeader.biWidth = width;
			info.bmiHeader.biHeight = -height;
			info.bmiHeader.biPlanes = 1;
			info.bmiHeader.biBitCount = 32;
			info.bmiHeader.biCompression = BI_RGB;
			uint8_t *bits{};
			auto bitmap = ::CreateDIBSection(target, &info, DIB_RGB_COLORS,
				reinterpret_cast<void **>(&bits), nullptr, 0);
			if(!bitmap || !bits)
			{
				if(bitmap)
					::DeleteObject(bitmap);
				return false;
			}
			std::memset(bits, 0, static_cast<size_t>(width) * height * 4U);
			auto memory = ::CreateCompatibleDC(target);
			if(!memory)
			{
				::DeleteObject(bitmap);
				return false;
			}
			auto oldBitmap = ::SelectObject(memory, bitmap);
		if(!oldBitmap || oldBitmap == HGDI_ERROR)
			{
				::DeleteDC(memory);
				::DeleteObject(bitmap);
				return false;
			}
			RECT local{0, 0, width, height};
			const auto result = DrawTextDirect(memory, theme.menuTheme,
				theme.textFont, text, local, color, format, usedTheme);
			if(result)
			{
				for(size_t offset = 0; offset < static_cast<size_t>(width) * height * 4U;
					offset += 4U)
				{
					if(bits[offset] || bits[offset + 1U] || bits[offset + 2U])
					{
						bits[offset + 3U] = color.a;
						bits[offset] = static_cast<uint8_t>((static_cast<uint32_t>(bits[offset]) * color.a + 127U) / 255U);
						bits[offset + 1U] = static_cast<uint8_t>((static_cast<uint32_t>(bits[offset + 1U]) * color.a + 127U) / 255U);
						bits[offset + 2U] = static_cast<uint8_t>((static_cast<uint32_t>(bits[offset + 2U]) * color.a + 127U) / 255U);
					}
				}
			}
			::SelectObject(memory, oldBitmap);
			::DeleteDC(memory);
			const auto blended = result && BlendBitmap(target, rect, bitmap,
				{width, height}, 255);
			::DeleteObject(bitmap);
			return blended;
		}

		const NativeMenuSymbol *SymbolForItem(const NativeMenuRenderMenu &menu,
			const NativeMenuRenderItem &item) noexcept
		{
			if(item.popup)
				return &menu.chevron;
			if(item.checked)
				return item.radio ? &menu.bullet : &menu.checked;
			return nullptr;
		}

		HBITMAP SelectSymbolBitmap(const NativeMenuSymbol &symbol,
			bool selected, bool disabled) noexcept
		{
			if(selected)
				return disabled ? symbol.selectedDisabled.handle : symbol.selected.handle;
			return disabled ? symbol.normalDisabled.handle : symbol.normal.handle;
		}

		const NativeMenuRenderImage *SelectItemImage(
			const NativeMenuRenderItem &item, bool selected) noexcept
		{
			if(selected && item.selectedImage.kind != NativeMenuImageKind::none)
				return &item.selectedImage;
			if(item.image.kind != NativeMenuImageKind::none)
				return &item.image;
			return nullptr;
		}

		std::wstring LeftTitle(const NativeMenuRenderItem &item)
		{
			if(item.tab > 0 && static_cast<size_t>(item.tab) < item.title.size())
				return item.title.substr(0, static_cast<size_t>(item.tab));
			return item.title;
		}

		std::wstring RightTitle(const NativeMenuRenderItem &item)
		{
			if(item.tab >= 0 && static_cast<size_t>(item.tab) < item.title.size())
				return item.title.substr(static_cast<size_t>(item.tab));
			return item.keys;
		}

		void SetDiagnostic(std::string *diagnostic, const char *message) noexcept
		{
			if(!diagnostic)
				return;
			try
			{
				*diagnostic = message ? message : "native renderer error";
			}
			catch(...)
			{
				diagnostic->clear();
			}
		}
	}

	NativeMenuRowState NativeMenuRowState::Resolve(UINT itemId, UINT itemAction,
		UINT itemState, const NativeMenuRenderItem *item) noexcept
	{
		NativeMenuRowState state{};
		state.separator = itemId == UINT_MAX || (item && item->separator);
		state.drawEntire = (itemAction & ODA_DRAWENTIRE) != 0;
		state.selected = (itemState & ODS_SELECTED) != 0;
		state.disabled = (itemState & (ODS_DISABLED | ODS_GRAYED)) != 0 ||
			(item && item->disabled);
		state.staticOrLabel = item && item->static_or_label();
		state.defaultItem = item && item->isDefault;
		state.skipDisabledStatic = !state.separator && !state.drawEntire &&
			state.disabled && state.staticOrLabel;
		return state;
	}

	bool NativeMenuRenderer::ValidateTheme(const NativeMenuRenderTheme &theme,
		std::string *diagnostic) noexcept
	{
		if(theme.version != NativeMenuRendererVersion)
		{
			SetDiagnostic(diagnostic, "unsupported native renderer theme version");
			return false;
		}
		if(!ValidDpi(theme.dpi))
		{
			SetDiagnostic(diagnostic, "native renderer DPI is outside 48..768");
			return false;
		}
		if(theme.imageSize > kMaxRendererWidth || theme.maxWidth > kMaxRendererWidth)
		{
			SetDiagnostic(diagnostic, "native renderer theme metric is too large");
			return false;
		}
		return true;
	}

	bool NativeMenuRenderer::ValidateItem(const NativeMenuRenderItem &item,
		std::string *diagnostic) noexcept
	{
		if(item.title.size() > 32768 || item.keys.size() > 32768)
		{
			SetDiagnostic(diagnostic, "native renderer item text is too large");
			return false;
		}
		if(item.preferredSize.cx < -1 || item.preferredSize.cy < -1 ||
			item.preferredSize.cx > static_cast<long>(kMaxRendererWidth) ||
			item.preferredSize.cy > static_cast<long>(kMaxRendererHeight))
		{
			SetDiagnostic(diagnostic, "native renderer item size is invalid");
			return false;
		}
		if(item.image.kind == NativeMenuImageKind::bitmap &&
			!item.image.bitmap.handle)
		{
			SetDiagnostic(diagnostic, "bitmap image has no native handle");
			return false;
		}
		return true;
	}

	bool NativeMenuRenderer::MeasureItem(HDC dc,
		const NativeMenuRenderTheme &theme, const NativeMenuRenderMenu &menu,
		const NativeMenuRenderItem &item, SIZE &size, std::string *diagnostic) noexcept
	{
		size = {};
		if(!ValidateTheme(theme, diagnostic) || !ValidateItem(item, diagnostic))
			return false;
		if(item.separator)
		{
			size.cx = 0;
			size.cy = Scale96(static_cast<long>(theme.separatorSize), theme.dpi) +
				theme.separatorMargin.height();
			return true;
		}

		long textWidth = item.preferredSize.cx;
		long textHeight = item.preferredSize.cy;
		if((textWidth < 0 || textHeight < 0) && dc && !item.title.empty())
		{
			HFONT measuredFont = theme.textFont;
			HFONT defaultFont{};
			if(item.isDefault)
			{
				LOGFONTW bold = theme.font;
				bold.lfWeight = (std::max)(bold.lfWeight, static_cast<LONG>(FW_BOLD));
				defaultFont = ::CreateFontIndirectW(&bold);
				if(defaultFont)
					measuredFont = defaultFont;
			}
			auto oldFont = measuredFont ? ::SelectObject(dc, measuredFont) : nullptr;
			SIZE measured{};
			const auto text = LeftTitle(item);
			if(::GetTextExtentPoint32W(dc, text.c_str(),
				static_cast<int>(std::min<size_t>(text.size(),
					static_cast<size_t>(std::numeric_limits<int>::max()))), &measured))
			{
				if(textWidth < 0)
					textWidth = measured.cx;
				if(textHeight < 0)
					textHeight = measured.cy;
			}
			if(oldFont)
				::SelectObject(dc, oldFont);
			if(defaultFont)
				::DeleteObject(defaultFont);
		}
		if(textWidth < 0)
			textWidth = 0;
		if(textHeight < 0)
			textHeight = std::abs(theme.font.lfHeight);
		if(textHeight <= 0)
			textHeight = Scale96(16, theme.dpi);

		const auto imageWidth = (item.title.empty() && !menu.hasColumn) ? 0L :
			static_cast<long>(theme.imageSize);
		const auto imageGap = imageWidth > 0 ? static_cast<long>(theme.imageGap) : 0L;
		const auto symbolWidth = item.popup || item.checked
			? static_cast<long>(std::max<LONG>(theme.imageSize,
				item.popup ? menu.chevron.size.cx : menu.checked.size.cx)) : 0L;
		const auto symbolGap = symbolWidth > 0 ? static_cast<long>(theme.imageGap) : 0L;
		const auto textTap = item.keys.empty() ? 0L : TextTap(theme);
		const auto keyWidth = item.keys.empty() ? 0L :
			(item.preferredSize.cx >= 0 ? static_cast<long>(item.keys.size()) *
				std::max<long>(1, std::abs(theme.font.lfHeight) / 2) : 0L);
		size.cx = theme.itemMargin.width() + theme.itemPadding.width() + textWidth +
			imageWidth + imageGap + keyWidth + textTap + symbolWidth + symbolGap;
		if(item.title.empty())
			size.cx = std::max(size.cx, static_cast<long>(theme.imageSize) +
				theme.itemMargin.width() + theme.itemPadding.width());
		if(menu.textWidth > 0)
			size.cx = std::max(size.cx, static_cast<long>(menu.textWidth) +
				theme.itemMargin.width() + theme.itemPadding.width());
		size.cy = std::max(textHeight, static_cast<long>(theme.imageSize)) +
			theme.itemMargin.height() + theme.itemPadding.height();
		if(item.staticItem && item.title.empty())
			size.cy = std::max(size.cy, Scale96(10, theme.dpi));
		if(theme.minWidth)
			size.cx = std::max(size.cx, static_cast<long>(theme.minWidth));
		if(theme.maxWidth)
			size.cx = std::min(size.cx, static_cast<long>(theme.maxWidth));
		size.cx = std::clamp<long>(size.cx, 0, kMaxRendererWidth);
		size.cy = std::clamp<long>(size.cy, 0, kMaxRendererHeight);
		return size.cx > 0 && size.cy > 0;
	}

	NativeMenuRowLayout NativeMenuRenderer::LayoutRow(
		const NativeMenuRenderTheme &theme, const NativeMenuRenderMenu &menu,
		const NativeMenuRenderItem &item, RECT row, NativeMenuRowState state) noexcept
	{
		NativeMenuRowLayout layout{};
		layout.row = row;
		if(!ValidRect(row) || state.separator)
			return layout;
		layout.content = row;
		layout.content.top += theme.itemMargin.top;
		layout.content.bottom -= theme.itemMargin.bottom;
		if(item.title.empty() && menu.hasColumn)
		{
			layout.content.left += theme.itemMargin.left + Scale96(3, theme.dpi);
			layout.content.right -= theme.itemMargin.right + Scale96(3, theme.dpi);
		}
		else
		{
			layout.content.left += theme.itemMargin.left;
			layout.content.right -= theme.itemMargin.right;
		}
		if(!ValidRect(layout.content))
			return layout;

		const auto imageSize = static_cast<long>(theme.imageSize);
		const auto height = layout.content.bottom - layout.content.top;
		layout.image = layout.content;
		layout.image.top = layout.content.top + (height - imageSize) / 2;
		layout.image.bottom = layout.image.top + imageSize;
		if(!item.title.empty() || !menu.hasColumn)
		{
			layout.image.left = layout.content.left + theme.itemPadding.left;
			layout.image.right = layout.image.left + imageSize;
		}
		layout.checkedImage = layout.image;
		layout.text = layout.content;
		if(!item.label && menu.has_alignment())
		{
			layout.text.left = layout.content.left + imageSize + theme.imageGap +
				theme.itemPadding.left;
			if(menu.drawChecks && menu.drawImages && theme.imageDisplay >= 2)
				layout.text.left += imageSize + theme.imageGap;
		}
		else
		{
			layout.text.left += theme.itemPadding.left;
		}
		layout.text.right -= theme.itemPadding.right;
		layout.keys = layout.text;
		layout.trailing = layout.content;
		if(item.popup && item.tab >= 0)
			layout.text.right -= imageSize;
		if(!item.keys.empty() || item.tab > 0)
		{
			const auto keyWidth = std::max<long>(imageSize * 2,
				static_cast<long>(item.keys.size()) *
					std::max<long>(1, std::abs(theme.font.lfHeight) / 2));
			layout.keys.left = std::max(layout.text.left,
				layout.text.right - keyWidth);
			layout.keys.right = layout.text.right;
			layout.text.right = layout.keys.left - TextTap(theme);
		}
		if(item.popup)
		{
			layout.trailing.left = layout.content.right - theme.itemPadding.right -
				static_cast<long>(std::max<LONG>(imageSize, menu.chevron.size.cx));
			layout.trailing.right = layout.content.right - theme.itemPadding.right;
		}
		if(theme.rtl || menu.rtl)
		{
			layout.image = MirrorRect(layout.image, row);
			layout.checkedImage = MirrorRect(layout.checkedImage, row);
			layout.text = MirrorRect(layout.text, row);
			layout.keys = MirrorRect(layout.keys, row);
			layout.trailing = MirrorRect(layout.trailing, row);
		}
		layout.valid = ValidRect(layout.content);
		return layout;
	}

	NativeMenuPaintResult NativeMenuRenderer::PaintRow(HDC dc,
		const NativeMenuRenderTheme &theme, const NativeMenuRenderMenu &menu,
		const NativeMenuRenderItem &item, RECT row, UINT itemAction,
		UINT itemState) noexcept
	{
		NativeMenuPaintResult result{};
		result.state = NativeMenuRowState::Resolve(item.id, itemAction, itemState, &item);
		result.state.separator = result.state.separator || item.separator;
		result.separator = result.state.separator;
		if(!dc || !ValidRect(row) || !ValidateTheme(theme) || !ValidateItem(item))
		{
			result.unavailable = true;
			return result;
		}
		if(result.state.skipDisabledStatic)
		{
			result.skipped = true;
			result.painted = true;
			return result;
		}
		if(result.separator)
		{
			const auto background = OpaqueIfRequested(theme.background, theme);
			if(!FillColor(dc, row, background, theme.opaqueInterior))
			{
				result.unavailable = true;
				return result;
			}
			RECT line = row;
			line.left += theme.separatorMargin.left;
			line.right -= theme.separatorMargin.right;
			line.top += theme.separatorMargin.top +
				(std::max<long>(0, row.bottom - row.top - theme.separatorMargin.height() -
					static_cast<long>(theme.separatorSize)) / 2);
			line.bottom = line.top + static_cast<long>(theme.separatorSize);
			if(!FillColor(dc, line, theme.separator, false))
			{
				result.unavailable = true;
				return result;
			}
			result.painted = true;
			return result;
		}

		result.layout = LayoutRow(theme, menu, item, row, result.state);
		if(!result.layout.valid)
		{
			result.unavailable = true;
			return result;
		}
		const auto background = OpaqueIfRequested(theme.background, theme);
		if(!FillColor(dc, row, background, theme.opaqueInterior))
		{
			result.unavailable = true;
			return result;
		}

		bool disabled = result.state.disabled;
		const bool staticOrLabel = result.state.staticOrLabel;
		if(disabled && staticOrLabel)
			disabled = false;
		if(!staticOrLabel)
		{
			auto fill = theme.item.resolve(result.state.selected, disabled);
			auto border = theme.itemBorder.resolve(result.state.selected, disabled);
			if(!DrawRounded(dc, result.layout.content, fill, border,
				theme.itemRadius))
			{
				result.unavailable = true;
				return result;
			}
		}

		const auto *symbol = SymbolForItem(menu, item);
		if(symbol && (!item.checked || menu.drawChecks) &&
			(!item.popup || menu.drawImages))
		{
			const auto bitmap = SelectSymbolBitmap(*symbol, result.state.selected, disabled);
			const auto target = item.popup ? result.layout.trailing : result.layout.checkedImage;
			if(bitmap)
			{
				if(!BlendBitmap(dc, target, bitmap, symbol->size,
					disabled ? 64 : 255))
				{
					result.unavailable = true;
					return result;
				}
			}
			else
			{
				std::wstring fallback;
				if(item.popup)
					fallback = (theme.rtl || menu.rtl) ? L"<" : L">";
				else if(item.radio)
					fallback = L"\u2022";
				else
					fallback = L"\u2713";
				const auto fallbackFont = theme.glyphFont ? theme.glyphFont : theme.textFont;
				const auto oldFont = theme.textFont;
				NativeMenuRenderTheme symbolTheme = theme;
				symbolTheme.textFont = fallbackFont;
				const auto &symbolColors = item.popup ? theme.symbols.chevron :
					item.radio ? theme.symbols.bullet : theme.symbols.checked;
				if(!DrawTextNative(dc, symbolTheme, fallback, target,
					symbolColors.resolve(result.state.selected, disabled),
					DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOCLIP,
					&result.usedThemeText))
				{
					result.unavailable = true;
					return result;
				}
				symbolTheme.textFont = oldFont;
			}
		}

		if(menu.drawImages && theme.imageEnabled && !item.label)
		{
			const auto *image = SelectItemImage(item, result.state.selected);
			if(image)
			{
				auto target = result.layout.image;
				if(menu.drawChecks && menu.drawImages && theme.imageDisplay >= 2)
				{
					const auto offset = static_cast<long>(theme.imageSize + theme.imageGap);
					target.left += offset;
					target.right += offset;
				}
				if(!theme.imageScale && image->kind == NativeMenuImageKind::bitmap &&
					image->bitmap.handle)
				{
					SIZE nativeSize = image->bitmap.size;
					if(nativeSize.cx <= 0 || nativeSize.cy <= 0)
					{
						BITMAP bitmap{};
						if(::GetObjectW(image->bitmap.handle, sizeof(bitmap), &bitmap) ==
							sizeof(bitmap))
							nativeSize = {bitmap.bmWidth, std::abs(bitmap.bmHeight)};
					}
					if(nativeSize.cx > 0 && nativeSize.cy > 0)
					{
						nativeSize.cx = (std::min)(nativeSize.cx,
							target.right - target.left);
						nativeSize.cy = (std::min)(nativeSize.cy,
							target.bottom - target.top);
						const auto cx = (target.left + target.right) / 2;
						const auto cy = (target.top + target.bottom) / 2;
						target = {cx - nativeSize.cx / 2, cy - nativeSize.cy / 2,
							cx - nativeSize.cx / 2 + nativeSize.cx,
							cy - nativeSize.cy / 2 + nativeSize.cy};
					}
				}
				bool drawn = false;
				switch(image->kind)
				{
					case NativeMenuImageKind::bitmap:
						drawn = BlendBitmap(dc, target, image->bitmap.handle,
							image->bitmap.size, disabled ? 64 : 255);
						break;
					case NativeMenuImageKind::shape:
					{
						const auto shapeSize = image->shape.size;
						RECT shapeRect = target;
						shapeRect.left = target.left + (target.right - target.left - shapeSize.cx) / 2;
						shapeRect.top = target.top + (target.bottom - target.top - shapeSize.cy) / 2;
						shapeRect.right = shapeRect.left + shapeSize.cx;
						shapeRect.bottom = shapeRect.top + shapeSize.cy;
						drawn = DrawRounded(dc, shapeRect, image->shape.color[0],
							image->shape.solid ? NativeMenuColor{} : image->shape.color[1],
							image->shape.radius);
						break;
					}
					case NativeMenuImageKind::glyph:
					{
						const auto glyphFont = image->glyph.font ? image->glyph.font : theme.glyphFont;
						NativeMenuRenderTheme glyphTheme = theme;
						glyphTheme.textFont = glyphFont;
						const auto glyphColor = image->glyph.color[0].a
							? image->glyph.color[0]
							: theme.imageColors[0].a ? theme.imageColors[0]
							: theme.text.resolve(result.state.selected, disabled);
						std::wstring code;
						if(image->glyph.code[0])
							code.push_back(image->glyph.code[0]);
						drawn = DrawTextNative(dc, glyphTheme, code, target, glyphColor,
							DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOCLIP,
							&result.usedThemeText);
						if(image->glyph.code[1])
						{
							code.assign(1, image->glyph.code[1]);
							drawn = drawn && DrawTextNative(dc, glyphTheme, code, target,
									image->glyph.color[1].a ? image->glyph.color[1] :
									(theme.imageColors[1].a ? theme.imageColors[1] : glyphColor),
								DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOCLIP,
								&result.usedThemeText);
						}
						break;
					}
					case NativeMenuImageKind::none:
					default:
						drawn = true;
						break;
				}
				if(!drawn)
				{
					result.unavailable = true;
					return result;
				}
			}
		}

		if(!item.title.empty())
		{
			const auto color = theme.text.resolve(result.state.selected, disabled);
			NativeMenuRenderTheme textTheme = theme;
			HFONT defaultFont{};
			if(result.state.defaultItem)
			{
				LOGFONTW bold = theme.font;
				bold.lfWeight = (std::max)(bold.lfWeight, static_cast<LONG>(FW_BOLD));
				defaultFont = ::CreateFontIndirectW(&bold);
				if(defaultFont)
				{
					textTheme.textFont = defaultFont;
					textTheme.shortcutFont = defaultFont;
				}
			}
			const auto textFormat = DT_NOCLIP | DT_SINGLELINE | DT_VCENTER |
				textTheme.textPrefix |
				((theme.rtl || menu.rtl) ? DT_RTLREADING : 0);
			const auto left = LeftTitle(item);
			const auto right = RightTitle(item);
			NativeMenuRenderTheme shortcutTheme = textTheme;
			shortcutTheme.textFont = textTheme.shortcutFont ?
				textTheme.shortcutFont : textTheme.textFont;
			if(!left.empty() && !DrawTextNative(dc, textTheme, left, result.layout.text,
				color, DT_LEFT | textFormat, &result.usedThemeText))
			{
				if(defaultFont)
					::DeleteObject(defaultFont);
				result.unavailable = true;
				return result;
			}
			if((item.tab > 0 || !item.keys.empty()) && !right.empty() &&
				!DrawTextNative(dc, shortcutTheme, right, result.layout.keys, color,
					DT_RIGHT | textFormat, &result.usedThemeText))
			{
				if(defaultFont)
					::DeleteObject(defaultFont);
				result.unavailable = true;
				return result;
			}
			if(defaultFont)
				::DeleteObject(defaultFont);
		}
		result.painted = true;
		return result;
	}

	bool NativeMenuRenderer::PaintMenu(HDC dc, RECT bounds,
		const NativeMenuRenderTheme &theme, const NativeMenuRenderMenu &menu,
		const std::vector<NativeMenuRenderRow> &rows,
		NativeMenuRenderResult *result) noexcept
	{
		NativeMenuRenderResult local{};
		local.dpi = theme.dpi;
		local.size = {bounds.right - bounds.left, bounds.bottom - bounds.top};
		local.desktopEffectsOmitted = theme.desktopEffectsOmitted;
		try
		{
			if(!dc || !ValidRect(bounds) || !ValidateTheme(theme, &local.diagnostic))
			{
				local.unavailable = true;
				if(result)
					*result = std::move(local);
				return false;
			}
			if(theme.composition)
				ClearDibRect(dc, bounds);
			const auto background = OpaqueIfRequested(theme.background, theme);
			if(!FillColor(dc, bounds, background, theme.opaqueInterior))
			{
				local.unavailable = true;
				SetDiagnostic(&local.diagnostic, "native renderer could not fill menu background");
			}
			else
			{
				if(theme.gradient.enabled &&
					!FillGradient(dc, bounds, theme))
				{
					local.unavailable = true;
					SetDiagnostic(&local.diagnostic,
						"native renderer could not draw menu gradient");
				}
				if(!local.unavailable &&
					theme.backgroundImage.kind == NativeMenuImageKind::bitmap &&
					!BlendBitmap(dc, bounds, theme.backgroundImage.bitmap.handle,
						theme.backgroundImage.bitmap.size, 255))
				{
					local.unavailable = true;
					SetDiagnostic(&local.diagnostic,
						"native renderer could not draw background image");
				}
				if(local.unavailable)
				{
					if(result)
						*result = std::move(local);
					return false;
				}
				if(!DrawMenuShadow(dc, bounds, theme))
				{
					local.unavailable = true;
					SetDiagnostic(&local.diagnostic, "native renderer could not draw menu shadow");
				}
				if(theme.frame.a)
					DrawRounded(dc, bounds, NativeMenuColor{}, theme.frame,
						theme.frameRadius);
				for(const auto &row : rows)
				{
					if(!row.item)
					{
						local.unavailable = true;
						SetDiagnostic(&local.diagnostic, "native renderer row has no item");
						break;
					}
					const auto paint = PaintRow(dc, theme, menu, *row.item, row.rect,
						row.itemAction, row.itemState);
					if(!paint.painted || paint.unavailable)
					{
						local.unavailable = true;
						SetDiagnostic(&local.diagnostic, "native renderer could not paint a menu row");
						break;
					}
					local.rowRects.push_back(row.rect);
				}
			}
			local.rendered = !local.unavailable;
		}
		catch(...)
		{
			local.unavailable = true;
			local.rendered = false;
			SetDiagnostic(&local.diagnostic, "native renderer failed while painting the menu");
		}
		if(result)
		{
			try
			{
				*result = std::move(local);
			}
			catch(...)
			{
				return false;
			}
		}
		return !local.unavailable;
	}

	bool NativeMenuRenderer::ResolveSystemTheme(HWND owner, uint32_t dpi,
		NativeMenuRenderTheme &theme, std::string *diagnostic) noexcept
	{
		theme = {};
		theme.version = NativeMenuRendererVersion;
		theme.dpi = dpi;
		if(!ValidDpi(dpi))
		{
			SetDiagnostic(diagnostic, "native renderer DPI is outside 48..768");
			return false;
		}
		NONCLIENTMETRICSW metrics{sizeof(metrics)};
		if(!::SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(metrics),
			&metrics, 0))
		{
			metrics.lfMenuFont.lfHeight = -MulDiv(9, static_cast<int>(dpi), 72);
			StringCchCopyW(metrics.lfMenuFont.lfFaceName,
				_countof(metrics.lfMenuFont.lfFaceName), L"Segoe UI");
		}
		theme.font = metrics.lfMenuFont;
		theme.font.lfHeight = MulDiv(theme.font.lfHeight, static_cast<int>(dpi), 96);
		const auto menuColor = ::GetSysColor(COLOR_MENU);
		const auto menuText = ::GetSysColor(COLOR_MENUTEXT);
		const auto highlight = ::GetSysColor(COLOR_HIGHLIGHT);
		const auto highlightText = ::GetSysColor(COLOR_HIGHLIGHTTEXT);
		const auto grayText = ::GetSysColor(COLOR_GRAYTEXT);
		auto color = [](COLORREF value, uint8_t alpha = 255) noexcept
		{
			return NativeMenuColor{GetBValue(value), GetGValue(value),
				GetRValue(value), alpha};
		};
		theme.background = color(menuColor);
		theme.text = {color(menuText), color(highlightText), color(grayText), color(grayText)};
		theme.item = {{0, 0, 0, 0}, color(highlight), {0, 0, 0, 0}, color(RGB(110, 110, 110), 180)};
		theme.itemBorder = {};
		theme.separator = color(grayText, 160);
		theme.shadow = {0, 0, 0, 80};
		theme.shadowEnabled = true;
		theme.shadowSize = Scale96(3, dpi);
		theme.shadowOffset = Scale96(2, dpi);
		theme.itemMargin = {0, Scale96(1, dpi), 0, Scale96(1, dpi)};
		theme.itemPadding = {Scale96(8, dpi), Scale96(3, dpi), Scale96(8, dpi), Scale96(3, dpi)};
		theme.separatorMargin = {Scale96(8, dpi), Scale96(4, dpi), Scale96(8, dpi), Scale96(4, dpi)};
		theme.framePadding = {Scale96(1, dpi), Scale96(1, dpi), Scale96(1, dpi), Scale96(1, dpi)};
		theme.frame = color(RGB(100, 100, 100), 160);
		theme.frameSize = 1;
		theme.frameRadius = Scale96(6, dpi);
		theme.imageSize = static_cast<uint32_t>(std::clamp<long>(
			std::abs(theme.font.lfHeight), Scale96(12, dpi), Scale96(32, dpi)));
		theme.imageGap = Scale96(6, dpi);
		theme.separatorSize = std::max<uint32_t>(1, Scale96(1, dpi));
		theme.symbols.chevron = theme.text;
		theme.symbols.checked = theme.text;
		theme.symbols.bullet = theme.text;
		theme.imageColors[0] = theme.text.normal;
		theme.imageColors[1] = theme.text.selected;
		theme.imageColors[2] = theme.text.disabled;
		theme.opaqueInterior = true;
		// The caller can supply an HTHEME after resolving the system facts.  We do
		// not return an owned HTHEME from this value-only helper.
		(void)owner;
		return true;
	}

	NativeMenuRenderSurface::NativeMenuRenderSurface(SIZE size, uint32_t dpi,
		uint64_t maxPixels) noexcept
	{
		create(size, dpi, maxPixels);
	}

	NativeMenuRenderSurface::~NativeMenuRenderSurface() noexcept
	{
		reset();
	}

	NativeMenuRenderSurface::NativeMenuRenderSurface(
		NativeMenuRenderSurface &&other) noexcept
		: dc_(other.dc_), bitmap_(other.bitmap_), oldBitmap_(other.oldBitmap_),
		bits_(other.bits_), size_(other.size_), dpi_(other.dpi_), byte_size_(other.byte_size_)
	{
		other.dc_ = nullptr;
		other.bitmap_ = nullptr;
		other.oldBitmap_ = nullptr;
		other.bits_ = nullptr;
		other.size_ = {};
		other.dpi_ = 0;
		other.byte_size_ = 0;
	}

	NativeMenuRenderSurface &NativeMenuRenderSurface::operator=(
		NativeMenuRenderSurface &&other) noexcept
	{
		if(this == &other)
			return *this;
		reset();
		dc_ = other.dc_;
		bitmap_ = other.bitmap_;
		oldBitmap_ = other.oldBitmap_;
		bits_ = other.bits_;
		size_ = other.size_;
		dpi_ = other.dpi_;
		byte_size_ = other.byte_size_;
		other.dc_ = nullptr;
		other.bitmap_ = nullptr;
		other.oldBitmap_ = nullptr;
		other.bits_ = nullptr;
		other.size_ = {};
		other.dpi_ = 0;
		other.byte_size_ = 0;
		return *this;
	}

	bool NativeMenuRenderSurface::create(SIZE size, uint32_t dpi,
		uint64_t maxPixels) noexcept
	{
		reset();
		if(size.cx <= 0 || size.cy <= 0 || !ValidDpi(dpi))
			return false;
		if(static_cast<uint32_t>(size.cx) > kMaxRendererWidth ||
			static_cast<uint32_t>(size.cy) > kMaxRendererHeight)
			return false;
		const auto pixels = static_cast<uint64_t>(size.cx) *
			static_cast<uint64_t>(size.cy);
		if(pixels == 0 || pixels > maxPixels || pixels > kMaxRendererBytes / 4U)
			return false;
		const auto bytes = static_cast<size_t>(pixels) * 4U;
		dc_ = ::CreateCompatibleDC(nullptr);
		if(!dc_)
			return false;
		BITMAPINFO info{};
		info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
		info.bmiHeader.biWidth = size.cx;
		info.bmiHeader.biHeight = -size.cy;
		info.bmiHeader.biPlanes = 1;
		info.bmiHeader.biBitCount = 32;
		info.bmiHeader.biCompression = BI_RGB;
		bitmap_ = ::CreateDIBSection(dc_, &info, DIB_RGB_COLORS,
			reinterpret_cast<void **>(&bits_), nullptr, 0);
		if(!bitmap_ || !bits_)
		{
			reset();
			return false;
		}
		oldBitmap_ = ::SelectObject(dc_, bitmap_);
		if(!oldBitmap_ || oldBitmap_ == HGDI_ERROR)
		{
			reset();
			return false;
		}
		std::memset(bits_, 0, bytes);
		size_ = size;
		dpi_ = dpi;
		byte_size_ = bytes;
		return true;
	}

	void NativeMenuRenderSurface::reset() noexcept
	{
		if(dc_ && oldBitmap_ && oldBitmap_ != HGDI_ERROR)
			::SelectObject(dc_, oldBitmap_);
		if(bitmap_)
			::DeleteObject(bitmap_);
		if(dc_)
			::DeleteDC(dc_);
		dc_ = nullptr;
		bitmap_ = nullptr;
		oldBitmap_ = nullptr;
		bits_ = nullptr;
		size_ = {};
		dpi_ = 0;
		byte_size_ = 0;
	}

	bool NativeMenuRenderSurface::clear(NativeMenuColor color) noexcept
	{
		if(!valid())
			return false;
		const auto b = static_cast<uint8_t>((static_cast<uint32_t>(color.b) * color.a + 127U) / 255U);
		const auto g = static_cast<uint8_t>((static_cast<uint32_t>(color.g) * color.a + 127U) / 255U);
		const auto r = static_cast<uint8_t>((static_cast<uint32_t>(color.r) * color.a + 127U) / 255U);
		for(size_t offset = 0; offset < byte_size_; offset += 4U)
		{
			bits_[offset] = b;
			bits_[offset + 1U] = g;
			bits_[offset + 2U] = r;
			bits_[offset + 3U] = color.a;
		}
		return true;
	}

	bool NativeMenuRenderSurface::finalize() noexcept
	{
		if(!valid())
			return false;
		::GdiFlush();
		for(size_t offset = 0; offset < byte_size_; offset += 4U)
			Premultiply(bits_ + offset);
		return true;
	}

	bool NativeMenuRenderSurface::copy_pixels(std::vector<uint8_t> &destination) const
	{
		if(!valid())
		{
			destination.clear();
			return false;
		}
		try
		{
			destination.assign(bits_, bits_ + byte_size_);
			return true;
		}
		catch(...)
		{
			destination.clear();
			return false;
		}
	}

	ATOM NativeMenuComposedWindow::RegisterClassOnce() noexcept
	{
		static std::once_flag flag;
		static ATOM atom{};
		std::call_once(flag, []
		{
			WNDCLASSEXW klass{sizeof(klass)};
			klass.hInstance = ::GetModuleHandleW(nullptr);
			klass.lpfnWndProc = &NativeMenuComposedWindow::WindowProc;
			klass.lpszClassName = L"Nilesoft.Shell.Studio.NativePreview";
			klass.hCursor = ::LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
			atom = ::RegisterClassExW(&klass);
		});
		return atom;
	}

	LRESULT CALLBACK NativeMenuComposedWindow::WindowProc(HWND window,
		UINT message, WPARAM wParam, LPARAM lParam) noexcept
	{
		if(message == WM_NCHITTEST)
			return HTTRANSPARENT;
		return ::DefWindowProcW(window, message, wParam, lParam);
	}

	NativeMenuComposedWindow::~NativeMenuComposedWindow() noexcept
	{
		destroy();
	}

	bool NativeMenuComposedWindow::create(HWND owner, POINT origin, SIZE size,
		NativeMenuBackdrop backdrop) noexcept
	{
		destroy();
		if(size.cx <= 0 || size.cy <= 0 || !RegisterClassOnce())
			return false;
		// HTTRANSPARENT only forwards hit testing within one GUI thread. The
		// composed worker and WPF host are separate processes, so the extended
		// transparent style is also required for pass-through input.
		const auto style = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
			WS_EX_TRANSPARENT;
		window_ = ::CreateWindowExW(style,
			L"Nilesoft.Shell.Studio.NativePreview", L"",
			WS_POPUP, origin.x, origin.y, size.cx, size.cy, owner, nullptr,
			::GetModuleHandleW(nullptr), nullptr);
		if(!window_)
			return false;
		size_ = size;
		if(backdrop != NativeMenuBackdrop::none && !set_backdrop(backdrop))
		{
			destroy();
			return false;
		}
		return true;
	}

	bool NativeMenuComposedWindow::update(const NativeMenuRenderSurface &surface,
		POINT origin) noexcept
	{
		if(!window_ || !surface.valid())
			return false;
		const auto size = surface.size();
		if(size.cx <= 0 || size.cy <= 0)
			return false;
		POINT source{0, 0};
		SIZE updateSize = size;
		BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
		auto screen = ::GetDC(nullptr);
		if(!screen)
			return false;
		const auto result = ::UpdateLayeredWindow(window_, screen, &origin, &updateSize,
			surface.dc(), &source, 0, &blend, ULW_ALPHA) != FALSE;
		::ReleaseDC(nullptr, screen);
		if(result)
			size_ = size;
		return result;
	}

	void NativeMenuComposedWindow::show() noexcept
	{
		if(window_)
			::ShowWindow(window_, SW_SHOWNOACTIVATE);
	}

	void NativeMenuComposedWindow::hide() noexcept
	{
		if(window_)
			::ShowWindow(window_, SW_HIDE);
	}

	void NativeMenuComposedWindow::destroy() noexcept
	{
		if(window_)
		{
			::DestroyWindow(window_);
			window_ = nullptr;
		}
		size_ = {};
	}

	bool NativeMenuComposedWindow::set_backdrop(NativeMenuBackdrop backdrop) noexcept
	{
		if(!window_)
			return false;
		if(backdrop == NativeMenuBackdrop::blur)
		{
			DWM_BLURBEHIND blur{DWM_BB_ENABLE, TRUE, nullptr, FALSE};
			return SUCCEEDED(::DwmEnableBlurBehindWindow(window_, &blur));
		}
		if(backdrop == NativeMenuBackdrop::acrylic || backdrop == NativeMenuBackdrop::mica)
		{
			// DWMWA_SYSTEMBACKDROP_TYPE is present on Windows 11.  Keep the
			// numeric value here so the renderer can still load on older SDKs.
			constexpr DWORD attribute = 38;
			const DWORD type = backdrop == NativeMenuBackdrop::mica ? 2U : 3U;
			return SUCCEEDED(::DwmSetWindowAttribute(window_, attribute, &type,
				sizeof(type)));
		}
		DWM_BLURBEHIND blur{DWM_BB_ENABLE, FALSE, nullptr, FALSE};
		::DwmEnableBlurBehindWindow(window_, &blur);
		return true;
	}
}
