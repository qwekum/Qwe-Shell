#pragma once
#include "../../dll/src/Include/NativeMenuRenderer.h"
#include "PreviewJson.h"
#include <algorithm>

namespace Nilesoft::Shell::StudioPreview
{
    inline std::string EncodePixels(const uint8_t* bytes, std::size_t size)
    {
        static constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        std::string result;
        result.reserve((size + 2) / 3 * 4);
        for(std::size_t i = 0; i < size; i += 3)
        {
            const uint32_t value = static_cast<uint32_t>(bytes[i]) << 16 |
                (i + 1 < size ? static_cast<uint32_t>(bytes[i + 1]) << 8 : 0) |
                (i + 2 < size ? bytes[i + 2] : 0);
            result += alphabet[value >> 18]; result += alphabet[(value >> 12) & 63];
            result += i + 1 < size ? alphabet[(value >> 6) & 63] : '=';
            result += i + 2 < size ? alphabet[value & 63] : '=';
        }
        return result;
    }

    struct RenderedPreview
    {
        NativeMenuRenderSurface surface;
        NativeMenuRenderResult result;
        std::vector<RECT> rows;
        long contentHeight = 0;
        long scrollOffset = 0;
        std::string error;

        bool Render(const NativeMenuRenderTheme& theme, NativeMenuRenderMenu menu,
            const std::vector<NativeMenuRenderItem>& items, long viewportHeight, long scroll, int selected)
        {
            surface.reset(); result = {}; rows.clear(); contentHeight = 0; scrollOffset = 0; error.clear();
            if(items.size() > 4096 || viewportHeight < 32 || viewportHeight > 4096)
            { error = "The preview exceeds its row or viewport limit."; return false; }
            NativeMenuRenderSurface measurement({1, 1}, theme.dpi);
            if(!measurement.valid()) { error = "The native measurement surface is unavailable."; return false; }
            long width = 32;
            std::vector<SIZE> sizes;
            for(const auto& item : items)
            {
                SIZE size{};
                if(!NativeMenuRenderer::MeasureItem(measurement.dc(), theme, menu, item, size, &error)) return false;
                width = (std::max)(width, size.cx);
                contentHeight += size.cy;
                if(contentHeight > 1000000) { error = "The preview content exceeds its height limit."; return false; }
                sizes.push_back(size);
            }
            width += theme.framePadding.width() + 2 * theme.frameSize;
            contentHeight += theme.framePadding.height() + 2 * theme.frameSize;
            long height = (std::min)((std::max)(32L, contentHeight), viewportHeight);
            scrollOffset = (std::clamp)(scroll, 0L, (std::max)(0L, contentHeight - height));
            if(!surface.create({width, height}, theme.dpi)) { error = "The preview bitmap exceeds its pixel limit."; return false; }
            surface.clear(theme.background);
            std::vector<NativeMenuRenderRow> paint;
            long top = theme.framePadding.top + theme.frameSize - scrollOffset;
            for(std::size_t i = 0; i < items.size(); ++i)
            {
                RECT row{theme.framePadding.left + static_cast<long>(theme.frameSize), top,
                    width - theme.framePadding.right - static_cast<long>(theme.frameSize), top + sizes[i].cy};
                rows.push_back(row);
                UINT state = items[i].disabled ? ODS_DISABLED : 0;
                if(items[i].checked) state |= ODS_CHECKED;
                if(static_cast<int>(i) == selected) state |= ODS_SELECTED;
                if(row.bottom > 0 && row.top < height) paint.push_back({&items[i], row, ODA_DRAWENTIRE, state});
                top += sizes[i].cy;
            }
            if(!NativeMenuRenderer::PaintMenu(surface.dc(), {0, 0, width, height}, theme, menu, paint, &result))
            { error = result.diagnostic; return false; }
            if(!surface.finalize()) { error = "The native bitmap could not be finalized."; return false; }
            return true;
        }

        std::string Json() const
        {
            std::string rectangles = "[";
            for(std::size_t i = 0; i < rows.size(); ++i)
            {
                if(i) rectangles += ',';
                const auto& row = rows[i];
                rectangles += "{\"left\":" + std::to_string(row.left) + ",\"top\":" + std::to_string(row.top) +
                    ",\"right\":" + std::to_string(row.right) + ",\"bottom\":" + std::to_string(row.bottom) + '}';
            }
            return "{\"width\":" + std::to_string(surface.size().cx) + ",\"height\":" + std::to_string(surface.size().cy) +
                ",\"dpi\":" + std::to_string(surface.dpi()) + ",\"format\":\"Pbgra32\",\"pixels\":" +
                ::ShellStudio::PreviewJson::Quote(EncodePixels(surface.pixels(), surface.byte_size())) +
                ",\"contentHeight\":" + std::to_string(contentHeight) + ",\"scrollOffset\":" + std::to_string(scrollOffset) +
                ",\"desktopEffectsOmitted\":" + (result.desktopEffectsOmitted ? "true" : "false") + ",\"rows\":" + rectangles + "]}";
        }
    };
}
