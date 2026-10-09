#pragma once

#include <Windows.h>
#include <uxtheme.h>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace Nilesoft::Shell
{
	// The renderer deliberately consumes resolved values.  It does not know
	// about ContextMenu, HMENU ownership, configuration expressions, or command
	// invocation.  Explorer and the Studio preview worker are adapters at this
	// seam; both use the same measurement, layout, and painting implementation.
	inline constexpr uint32_t NativeMenuRendererVersion = 2;
	inline constexpr uint64_t NativeMenuRendererDefaultMaxPixels = 600000;
	inline constexpr uint32_t NativeMenuRendererMinDpi = 48;
	inline constexpr uint32_t NativeMenuRendererMaxDpi = 768;

	struct NativeMenuColor
	{
		uint8_t b{};
		uint8_t g{};
		uint8_t r{};
		uint8_t a{};

		constexpr COLORREF rgb() const noexcept
		{
			return RGB(r, g, b);
		}

		constexpr uint32_t packed_bgra() const noexcept
		{
			return static_cast<uint32_t>(b) |
				(static_cast<uint32_t>(g) << 8U) |
				(static_cast<uint32_t>(r) << 16U) |
				(static_cast<uint32_t>(a) << 24U);
		}

		constexpr bool transparent() const noexcept { return a == 0; }
		constexpr bool opaque() const noexcept { return a == 255; }
	};

	struct NativeMenuMargin
	{
		long left{};
		long top{};
		long right{};
		long bottom{};

		constexpr long width() const noexcept { return left + right; }
		constexpr long height() const noexcept { return top + bottom; }
	};

	struct NativeMenuColorState
	{
		NativeMenuColor normal{};
		NativeMenuColor selected{};
		NativeMenuColor disabled{};
		NativeMenuColor selectedDisabled{};

		const NativeMenuColor &resolve(bool selectedState, bool disabledState) const noexcept
		{
			if(selectedState)
				return disabledState ? selectedDisabled : selected;
			return disabledState ? disabled : normal;
		}
	};

	// Theme colors for the three native menu symbols.  The live runtime stores
	// these as Theme::state_t values; keeping the state in the renderer contract
	// lets a worker use the same colors when bitmap symbol handles are absent.
	struct NativeMenuRenderSymbolColors
	{
		NativeMenuColorState chevron{};
		NativeMenuColorState checked{};
		NativeMenuColorState bullet{};
	};

	struct NativeMenuBitmap
	{
		HBITMAP handle{};
		SIZE size{};
	};

	struct NativeMenuGlyph
	{
		wchar_t code[2]{};
		HFONT font{};
		SIZE size{};
		NativeMenuColor color[2]{};
	};

	enum class NativeMenuImageKind : uint8_t
	{
		none,
		bitmap,
		shape,
		glyph
	};

	struct NativeMenuShape
	{
		bool solid = true;
		uint8_t radius{};
		uint8_t stroke{};
		SIZE size{};
		NativeMenuColor color[2]{};
	};

	struct NativeMenuRenderImage
	{
		NativeMenuImageKind kind = NativeMenuImageKind::none;
		NativeMenuBitmap bitmap{};
		NativeMenuShape shape{};
		NativeMenuGlyph glyph{};
	};

	// The runtime theme keeps gradient coordinates as percentages of the menu
	// surface.  Preserve that representation at the renderer boundary so the
	// offscreen preview and the live owner-draw path resolve the same stops.
	struct NativeMenuRenderGradientStop
	{
		double offset{};
		NativeMenuColor color{};
	};

	struct NativeMenuRenderGradient
	{
		bool enabled{};
		double linear[5]{};
		double radial[6]{};
		std::vector<NativeMenuRenderGradientStop> stops;
	};

	struct NativeMenuSymbol
	{
		NativeMenuBitmap normal{};
		NativeMenuBitmap normalDisabled{};
		NativeMenuBitmap selected{};
		NativeMenuBitmap selectedDisabled{};
		SIZE size{};

		bool available() const noexcept
		{
			return normal.handle || normalDisabled.handle || selected.handle ||
				selectedDisabled.handle;
		}
	};

	// A resolved theme includes native handles only when the caller owns them.
	// The renderer borrows all handles and never closes them.  A worker may leave
	// the handles null and still receive the same fallback system text and GDI
	// rendering behavior.
	struct NativeMenuRenderTheme
	{
		uint32_t version = NativeMenuRendererVersion;
		uint32_t dpi = 96;
		NativeMenuColor background{};
		NativeMenuColor backgroundTint{};
		NativeMenuRenderImage backgroundImage{};
		NativeMenuRenderGradient gradient{};
		NativeMenuColorState text{};
		NativeMenuColorState item{};
		NativeMenuColorState itemBorder{};
		NativeMenuColor separator{};
		NativeMenuColor shadow{};
		bool shadowEnabled{};
		uint32_t shadowSize{};
		uint32_t shadowOffset{};
		NativeMenuColor frame{};
		NativeMenuRenderSymbolColors symbols{};
		NativeMenuColor imageColors[3]{};
		bool imageEnabled = true;
		NativeMenuMargin itemMargin{};
		NativeMenuMargin itemPadding{};
		NativeMenuMargin separatorMargin{};
		NativeMenuMargin framePadding{};
		uint32_t itemRadius{};
		uint32_t frameRadius{};
		uint32_t frameSize{};
		uint32_t imageSize = 16;
		uint32_t imageGap{};
		bool imageScale = true;
		uint32_t separatorSize = 1;
		uint32_t minWidth{};
		uint32_t maxWidth{};
		uint8_t imageDisplay{};
		bool rtl{};
		bool composition{};
		bool opaqueInterior = true;
		bool desktopEffectsOmitted{};
		// `Theme::text.tap` is the gap before the shortcut column.  Keep zero as
		// an unset value for hand-authored renderer tests; callers then fall back
		// to imageGap, preserving the v2 contract for older producers.
		uint32_t textTap{};
		UINT textPrefix = DT_HIDEPREFIX;
		LOGFONTW font{};
		HFONT textFont{};
		HFONT shortcutFont{};
		HFONT glyphFont{};
		HTHEME menuTheme{};
		HBRUSH backgroundBrush{};
    };

	struct NativeMenuRenderMenu
	{
		uint32_t version = NativeMenuRendererVersion;
		uint32_t id{};
		uint32_t textWidth{};
		bool hasColumn{};
		bool drawImages = true;
		bool drawChecks = true;
		bool rtl{};
		NativeMenuSymbol chevron{};
		NativeMenuSymbol checked{};
		NativeMenuSymbol bullet{};

		bool has_alignment() const noexcept { return drawImages || drawChecks; }
	};

	struct NativeMenuRenderItem
	{
		uint32_t id{};
		std::wstring title;
		std::wstring keys;
		bool separator{};
		bool popup{};
		bool checked{};
		bool radio{};
		bool disabled{};
		bool label{};
		bool staticItem{};
		bool ownerDraw{};
		int tab = -1;
		SIZE preferredSize{-1, -1};
		NativeMenuRenderImage image{};
		NativeMenuRenderImage selectedImage{};
		// MFS_DEFAULT is a semantic state of a native command item.  The
		// renderer does not change layout for it today, but retaining the bit at
		// this boundary keeps captured and constructed rows lossless for clients
		// that need to preserve the state while applying a later edit.
		bool isDefault{};

		bool static_or_label() const noexcept { return staticItem || label; }
	};

	struct NativeMenuRenderRow
	{
		const NativeMenuRenderItem *item{};
		RECT rect{};
		UINT itemAction{};
		UINT itemState{};
	};

	struct NativeMenuRowState
	{
		bool separator{};
		bool drawEntire{};
		bool selected{};
		bool disabled{};
		bool staticOrLabel{};
		bool defaultItem{};
		bool skipDisabledStatic{};

		static NativeMenuRowState Resolve(UINT itemId, UINT itemAction,
			UINT itemState, const NativeMenuRenderItem *item) noexcept;
	};

	struct NativeMenuRowLayout
	{
		RECT row{};
		RECT content{};
		RECT image{};
		RECT checkedImage{};
		RECT text{};
		RECT keys{};
		RECT trailing{};
		bool valid{};
	};

	struct NativeMenuPaintResult
	{
		bool painted{};
		bool unavailable{};
		bool separator{};
		bool skipped{};
		bool usedThemeText{};
		bool clipped{};
		NativeMenuRowState state{};
		NativeMenuRowLayout layout{};
	};

	struct NativeMenuRenderResult
	{
		uint32_t version = NativeMenuRendererVersion;
		bool rendered{};
		bool unavailable{};
		bool desktopEffectsOmitted{};
		uint32_t dpi{};
		SIZE size{};
		std::vector<RECT> rowRects;
		std::string diagnostic;
	};

	// A bounded top-down BGRA32 DIB.  Pixels are normalized to premultiplied
	// alpha by finalize(), which is the format used by capture protocol v2 and
	// UpdateLayeredWindow.  The surface owns its DC and bitmap.
	class NativeMenuRenderSurface final
	{
	public:
		NativeMenuRenderSurface() = default;
		NativeMenuRenderSurface(SIZE size, uint32_t dpi = 96,
			uint64_t maxPixels = NativeMenuRendererDefaultMaxPixels) noexcept;
		~NativeMenuRenderSurface() noexcept;

		NativeMenuRenderSurface(const NativeMenuRenderSurface &) = delete;
		NativeMenuRenderSurface &operator=(const NativeMenuRenderSurface &) = delete;
		NativeMenuRenderSurface(NativeMenuRenderSurface &&other) noexcept;
		NativeMenuRenderSurface &operator=(NativeMenuRenderSurface &&other) noexcept;

		bool create(SIZE size, uint32_t dpi = 96,
			uint64_t maxPixels = NativeMenuRendererDefaultMaxPixels) noexcept;
		void reset() noexcept;
		bool valid() const noexcept { return dc_ && bitmap_ && bits_; }
		HDC dc() const noexcept { return dc_; }
		HBITMAP bitmap() const noexcept { return bitmap_; }
		uint8_t *pixels() const noexcept { return bits_; }
		SIZE size() const noexcept { return size_; }
		uint32_t dpi() const noexcept { return dpi_; }
		size_t byte_size() const noexcept { return byte_size_; }
		bool clear(NativeMenuColor color) noexcept;
		bool finalize() noexcept;
		bool copy_pixels(std::vector<uint8_t> &destination) const;

	private:
		HDC dc_{};
		HBITMAP bitmap_{};
		HGDIOBJ oldBitmap_{};
		uint8_t *bits_{};
		SIZE size_{};
		uint32_t dpi_{};
		size_t byte_size_{};
	};

	class NativeMenuRenderer final
	{
	public:
		static bool ValidateTheme(const NativeMenuRenderTheme &theme,
			std::string *diagnostic = nullptr) noexcept;
		static bool ValidateItem(const NativeMenuRenderItem &item,
			std::string *diagnostic = nullptr) noexcept;
		static bool MeasureItem(HDC dc, const NativeMenuRenderTheme &theme,
			const NativeMenuRenderMenu &menu, const NativeMenuRenderItem &item,
			SIZE &size, std::string *diagnostic = nullptr) noexcept;
		static NativeMenuRowLayout LayoutRow(const NativeMenuRenderTheme &theme,
			const NativeMenuRenderMenu &menu, const NativeMenuRenderItem &item,
			RECT row, NativeMenuRowState state) noexcept;
		static NativeMenuPaintResult PaintRow(HDC dc,
			const NativeMenuRenderTheme &theme, const NativeMenuRenderMenu &menu,
			const NativeMenuRenderItem &item, RECT row, UINT itemAction,
			UINT itemState) noexcept;
		static bool PaintMenu(HDC dc, RECT bounds,
			const NativeMenuRenderTheme &theme, const NativeMenuRenderMenu &menu,
			const std::vector<NativeMenuRenderRow> &rows,
			NativeMenuRenderResult *result = nullptr) noexcept;
		static bool ResolveSystemTheme(HWND owner, uint32_t dpi,
			NativeMenuRenderTheme &theme, std::string *diagnostic = nullptr) noexcept;
	};

	// A small adapter for the worker's explicit live composed preview.  It owns
	// only a tool-window HWND and uses UpdateLayeredWindow; it never changes
	// Explorer or the user's system theme.  Desktop blur/backdrop is opt-in and
	// is reported separately because it cannot be represented by an offscreen
	// bitmap alone.
	enum class NativeMenuBackdrop : uint8_t
	{
		none,
		blur,
		acrylic,
		mica
	};

	class NativeMenuComposedWindow final
	{
	public:
		NativeMenuComposedWindow() = default;
		~NativeMenuComposedWindow() noexcept;
		NativeMenuComposedWindow(const NativeMenuComposedWindow &) = delete;
		NativeMenuComposedWindow &operator=(const NativeMenuComposedWindow &) = delete;

		bool create(HWND owner, POINT origin, SIZE size,
			NativeMenuBackdrop backdrop = NativeMenuBackdrop::none) noexcept;
		bool update(const NativeMenuRenderSurface &surface, POINT origin) noexcept;
		void show() noexcept;
		void hide() noexcept;
		void destroy() noexcept;
		bool set_backdrop(NativeMenuBackdrop backdrop) noexcept;
		HWND handle() const noexcept { return window_; }

	private:
		static LRESULT CALLBACK WindowProc(HWND, UINT, WPARAM, LPARAM) noexcept;
		static ATOM RegisterClassOnce() noexcept;
		HWND window_{};
		SIZE size_{};
	};
}
