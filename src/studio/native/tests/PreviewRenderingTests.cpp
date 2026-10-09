#include "../PreviewRendering.h"
#include <iostream>
#include <stdexcept>

using namespace Nilesoft::Shell;
using namespace Nilesoft::Shell::StudioPreview;

int main()
{
    try
    {
        for(uint32_t dpi : {96u, 144u, 192u})
        {
            NativeMenuRenderTheme theme;
            std::string diagnostic;
            if(!NativeMenuRenderer::ResolveSystemTheme(nullptr, dpi, theme, &diagnostic)) throw std::runtime_error(diagnostic);
            NativeMenuRenderMenu menu;
            std::vector<NativeMenuRenderItem> items;
            for(uint32_t i = 0; i < 80; ++i)
            {
                NativeMenuRenderItem item;
                item.id = i + 1; item.title = L"&Unicode \u03bb menu " + std::to_wstring(i);
                item.disabled = i == 2; item.checked = i == 3; item.popup = i == 4;
                items.push_back(item);
            }
            RenderedPreview preview;
            if(!preview.Render(theme, menu, items, 300, 100, 3)) throw std::runtime_error(preview.error);
            if(preview.rows.size() != 80 || preview.scrollOffset != 100 || preview.surface.size().cy != 300)
                throw std::runtime_error("Native scrolling did not preserve the row/frame contract.");
            ::ShellStudio::PreviewJson::Value value;
            if(!::ShellStudio::PreviewJson::Parse(preview.Json(), value, diagnostic)) throw std::runtime_error(diagnostic);
            if(!value.Find("pixels") || !value.Find("rows") || value.Find("rows")->array.size() != 80)
                throw std::runtime_error("The frame JSON lost its pixels or row correspondence.");
            items.resize(1);
            if(!preview.Render(theme, menu, items, 300, 100, 0) || preview.rows.size() != 1 || preview.scrollOffset != 0)
                throw std::runtime_error("Reusing a preview retained rows or scroll from the previous frame.");
            std::cout << "PASS native frame JSON/reuse/scroll dpi=" << dpi << '\n';
        }
        return 0;
    }
    catch(const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
