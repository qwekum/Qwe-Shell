#include "../../../dll/src/Include/NativeMenuRenderer.h"

#include <Windows.h>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

namespace
{
	using Nilesoft::Shell::NativeMenuColor;
	using Nilesoft::Shell::NativeMenuRenderItem;
	using Nilesoft::Shell::NativeMenuRenderMenu;
	using Nilesoft::Shell::NativeMenuRenderResult;
	using Nilesoft::Shell::NativeMenuRenderRow;
	using Nilesoft::Shell::NativeMenuRenderSurface;
	using Nilesoft::Shell::NativeMenuRenderTheme;
	using Nilesoft::Shell::NativeMenuRenderer;

	void Require(bool condition, const char *message)
	{
		if(!condition)
			throw std::runtime_error(message);
	}

	NativeMenuColor Color(uint8_t r, uint8_t g, uint8_t b, uint8_t a = 255)
	{
		return {b, g, r, a};
	}

	void TestResolveSystemTheme()
	{
		NativeMenuRenderTheme theme;
		std::string diagnostic;
		Require(NativeMenuRenderer::ResolveSystemTheme(nullptr, 96, theme,
			&diagnostic), diagnostic.empty() ? "system theme resolution failed" : diagnostic.c_str());
		Require(theme.version == Nilesoft::Shell::NativeMenuRendererVersion,
			"resolved theme has the wrong renderer version");
		Require(theme.dpi == 96 && theme.imageSize > 0 && theme.separatorSize > 0,
			"resolved theme omitted native menu metrics");
		Require(NativeMenuRenderer::ValidateTheme(theme, &diagnostic),
			diagnostic.empty() ? "resolved theme did not validate" : diagnostic.c_str());
		const auto resolvedImageSize = theme.imageSize;
		Require(!NativeMenuRenderer::ResolveSystemTheme(nullptr, 0, theme, &diagnostic),
			"invalid DPI was accepted by system theme resolution");
		std::cout << "PASS resolve-system-theme dpi=96 image=" << resolvedImageSize << "\n";
	}

	NativeMenuRenderTheme TestTheme(bool composition, HFONT font)
	{
		NativeMenuRenderTheme theme;
		theme.dpi = 96;
		theme.background = Color(composition ? 8 : 30, composition ? 12 : 40,
			composition ? 18 : 52, 255);
		theme.text = {Color(242, 244, 248), Color(255, 255, 255),
			Color(135, 142, 155), Color(176, 180, 188)};
		theme.item = {Color(0, 0, 0, 0), Color(52, 102, 170, composition ? 170 : 255),
			Color(0, 0, 0, 0), Color(92, 92, 92, composition ? 170 : 255)};
		theme.itemBorder = {Color(0, 0, 0, 0), Color(108, 160, 220, composition ? 180 : 255),
			Color(0, 0, 0, 0), Color(140, 140, 140, composition ? 180 : 255)};
		theme.separator = Color(120, 128, 142, composition ? 180 : 255);
		theme.frame = Color(90, 100, 118, composition ? 150 : 255);
		theme.itemMargin = {0, 1, 0, 1};
		theme.itemPadding = {10, 4, 10, 4};
		theme.separatorMargin = {10, 4, 10, 4};
		theme.framePadding = {1, 1, 1, 1};
		theme.frameRadius = 6;
		theme.itemRadius = 4;
		theme.frameSize = 1;
		theme.imageSize = 16;
		theme.imageGap = 6;
		theme.separatorSize = 1;
		theme.imageDisplay = 1;
		theme.composition = composition;
		theme.opaqueInterior = !composition;
		theme.desktopEffectsOmitted = composition;
		theme.font.lfHeight = -16;
		theme.textFont = font;
		theme.glyphFont = font;
		return theme;
	}

	std::uint64_t HashPixels(const NativeMenuRenderSurface &surface)
	{
		std::uint64_t hash = 1469598103934665603ULL;
		for(size_t index = 0; index < surface.byte_size(); ++index)
		{
			hash ^= surface.pixels()[index];
			hash *= 1099511628211ULL;
		}
		return hash;
	}

	NativeMenuRenderResult Render(bool composition, HFONT font,
		std::uint64_t &hash, bool &allOpaque)
	{
		NativeMenuRenderSurface surface({520, 190});
		Require(surface.valid(), "render surface creation failed");
		NativeMenuRenderTheme theme = TestTheme(composition, font);
		// GDI fills preserve the DIB alpha byte. Initialize the bounded surface
		// first so the evidence checks the renderer's pixels, including black
		// interior pixels, rather than an uninitialized alpha channel.
		Require(surface.clear(Color(0, 0, 0, 255)), "render surface clear failed");
		NativeMenuRenderMenu menu;
		menu.drawImages = false;
		menu.drawChecks = true;
		menu.hasColumn = true;
		std::vector<NativeMenuRenderItem> items;
		items.reserve(4);
		items.push_back({100, L"Normal", L"Ctrl+N"});
		items.push_back({101, L"Checked", {}, false, false, true});
		items.push_back({102, {}, {}, true});
		items.push_back({103, L"Disabled", {}, false, false, false, false, true});
		std::vector<NativeMenuRenderRow> rows;
		rows.push_back({&items[0], {4, 4, 516, 44}, ODA_DRAWENTIRE, 0});
		rows.push_back({&items[1], {4, 44, 516, 84}, ODA_DRAWENTIRE, ODS_SELECTED});
		rows.push_back({&items[2], {4, 84, 516, 106}, ODA_DRAWENTIRE, 0});
		rows.push_back({&items[3], {4, 106, 516, 146}, ODA_DRAWENTIRE, ODS_DISABLED});
		NativeMenuRenderResult result;
		Require(NativeMenuRenderer::PaintMenu(surface.dc(), {0, 0, 520, 190},
			theme, menu, rows, &result),
			result.diagnostic.empty() ? "menu render failed" : result.diagnostic.c_str());
		Require(result.rendered && !result.unavailable && result.rowRects.size() == rows.size(),
			"menu render result did not retain all rows");
		Require(result.desktopEffectsOmitted == composition,
			"composition mode did not report desktop effect omission");
		Require(surface.finalize(), "render surface finalization failed");
		hash = HashPixels(surface);
		allOpaque = true;
		size_t transparent = 0;
		for(size_t index = 3; index < surface.byte_size(); index += 4)
		{
			allOpaque = allOpaque && surface.pixels()[index] == 255;
			if(surface.pixels()[index] != 255)
				++transparent;
		}
		// Opaque mode must retain an opaque menu interior. Composition mode uses
		// the layered surface's transparent interior so a live backdrop can show
		// through; both are intentional and are checked independently here.
		allOpaque = composition
			? transparent > (surface.byte_size() / 8U)
			: transparent < (surface.byte_size() / 16U);
		return result;
	}

	void TestOpaqueAndCompositionRenders()
	{
		LOGFONTW logFont{};
		logFont.lfHeight = -16;
		logFont.lfWeight = FW_NORMAL;
		::lstrcpynW(logFont.lfFaceName, L"Segoe UI", LF_FACESIZE);
		auto font = ::CreateFontIndirectW(&logFont);
		Require(font != nullptr, "test font creation failed");
		std::uint64_t opaqueHash{}, compositionHash{};
		bool opaquePixels = false, compositionPixels = false;
		const auto opaque = Render(false, font, opaqueHash, opaquePixels);
		const auto composition = Render(true, font, compositionHash, compositionPixels);
		::DeleteObject(font);
		Require(opaquePixels && compositionPixels, "rendered menu contains nonopaque pixels");
		Require(opaqueHash != compositionHash, "opaque and composition renders were identical");
		std::cout << "PASS render opaque rendered=" << opaque.rendered
			<< " hash=" << opaqueHash << "\n";
		std::cout << "PASS render composition rendered=" << composition.rendered
			<< " hash=" << compositionHash << " effects-omitted="
			<< composition.desktopEffectsOmitted << "\n";
	}

	void TestSelectedBitmapCanRender()
	{
		NativeMenuRenderSurface source({2, 1});
		NativeMenuRenderSurface target({180, 44});
		Require(source.valid() && target.valid(), "bitmap regression surfaces failed");
		Require(source.clear(Color(235, 64, 52, 255)), "source bitmap clear failed");
		Require(target.clear(Color(12, 18, 26, 255)), "target bitmap clear failed");
		const auto before = HashPixels(target);

		NativeMenuRenderTheme theme = TestTheme(false, nullptr);
		theme.imageSize = 16;
		NativeMenuRenderMenu menu;
		menu.drawImages = true;
		menu.drawChecks = false;
		NativeMenuRenderItem item{200, L"Selected bitmap"};
		item.image.kind = Nilesoft::Shell::NativeMenuImageKind::bitmap;
		item.image.bitmap = {source.bitmap(), source.size()};
		std::vector<NativeMenuRenderRow> rows{{&item, {0, 0, 180, 44}, ODA_DRAWENTIRE, 0}};
		NativeMenuRenderResult result;
		Require(NativeMenuRenderer::PaintMenu(target.dc(), {0, 0, 180, 44},
			theme, menu, rows, &result), result.diagnostic.empty()
				? "selected bitmap render failed" : result.diagnostic.c_str());
		Require(result.rendered && !result.unavailable,
			"selected bitmap render was reported unavailable");
		Require(HashPixels(target) != before, "selected bitmap did not affect target pixels");
		std::cout << "PASS render bitmap selected in owner DC\n";
	}
}

int main()
{
	try
	{
		TestResolveSystemTheme();
		TestOpaqueAndCompositionRenders();
		TestSelectedBitmapCanRender();
		std::cout << "4 native renderer cases passed.\n";
		return 0;
	}
	catch(const std::exception &error)
	{
		std::cerr << "FAIL " << error.what() << '\n';
		return 1;
	}
}
