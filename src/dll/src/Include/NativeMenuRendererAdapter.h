#pragma once

// Adapters at the renderer boundary.  The renderer itself consumes only
// resolved value types; these helpers translate the native runtime's resolved
// Theme/MenuItemInfo objects without giving the renderer ownership of any
// Explorer or menu handles.

#include "NativeMenuRenderer.h"
#include "Theme.h"
#include "Cache.h"
#include "Menu.h"
#include "MenuItem.h"
#include "NativeMenuConstruction.h"
#include "../Expression/Context.h"

#include <algorithm>
#include <cctype>
#include <cwctype>
#include <cstdint>
#include <cstring>
#include <functional>
#include <iterator>
#include <limits>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace Nilesoft::Shell
{
    // Resource loading is deliberately injected at this boundary.  The
    // value-only construction path can therefore render a caller-owned
    // bitmap, decode a captured image, or report an unavailable path without
    // consulting Explorer or touching the process-wide image cache.
    using NativeMenuRenderResourceLoader = std::function<bool(
        const std::wstring &, NativeMenuRenderImage &)>;
    using NativeMenuRenderDiagnosticSink = std::function<void(
        const std::wstring &)>;

    struct NativeMenuPreviewNode
    {
        NativeMenuRenderItem value{};
        std::vector<NativeMenuPreviewNode> children;
        bool childrenAvailable{};
        std::wstring capturedId;
        std::wstring matchTitle;
        const NativeMenu *source{};
        bool ownerDraw{};
        // Evaluation explanations are captured while the preview constructs
        // the row. They describe the decisions already made; consumers must
        // never re-evaluate expressions to populate an explanation view.
        std::vector<std::wstring> explanations;

        // `path` is the normalized parent path of this entry.  It is kept
        // separate from capturedId because stable capture ids are opaque and
        // may change when a source document is edited.  `system` identifies
        // rows supplied by the captured native menu; authored rows have it
        // cleared when they are constructed from a NativeMenu definition.
        std::wstring path;
        bool system = true;

        // Static rules can request a row-level separator and position.  These
        // values are construction metadata and are intentionally not folded
        // into NativeMenuRenderItem, whose fields describe only one painted
        // row.
        Separator separator = Separator::None;
        Position position = Position::Auto;
        std::wstring indexOf;
        int indexOfPosition{};
        int indexOfDefault = -1;
        bool removed{};

        bool valid() const noexcept
        {
            return NativeMenuRenderer::ValidateItem(value) &&
                (!value.popup || childrenAvailable || children.empty());
        }
    };

    using NativeMenuPreviewTree = std::vector<NativeMenuPreviewNode>;

    struct NativeMenuRenderSymbolHandles
    {
        NativeMenuSymbol chevron{};
        NativeMenuSymbol checked{};
        NativeMenuSymbol bullet{};
    };

    // Owns only fonts it creates.  All bitmap, theme, and brush handles in the
    // resulting value remain borrowed from the caller and are never destroyed.
    class NativeMenuRenderOwnedFonts final
    {
    public:
        NativeMenuRenderOwnedFonts() = default;
        ~NativeMenuRenderOwnedFonts() noexcept { reset(); }

        NativeMenuRenderOwnedFonts(const NativeMenuRenderOwnedFonts &) = delete;
        NativeMenuRenderOwnedFonts &operator=(const NativeMenuRenderOwnedFonts &) = delete;

        NativeMenuRenderOwnedFonts(NativeMenuRenderOwnedFonts &&other) noexcept
            : text_(other.text_), glyph_(other.glyph_)
        {
            other.text_ = nullptr;
            other.glyph_ = nullptr;
        }

        NativeMenuRenderOwnedFonts &operator=(NativeMenuRenderOwnedFonts &&other) noexcept
        {
            if(this != &other)
            {
                reset();
                text_ = other.text_;
                glyph_ = other.glyph_;
                other.text_ = nullptr;
                other.glyph_ = nullptr;
            }
            return *this;
        }

        void reset() noexcept
        {
            if(glyph_ && glyph_ != text_)
                ::DeleteObject(glyph_);
            if(text_)
                ::DeleteObject(text_);
            text_ = nullptr;
            glyph_ = nullptr;
        }

        HFONT text() const noexcept { return text_; }
        HFONT glyph() const noexcept { return glyph_; }

        bool create(const LOGFONTW &source, uint32_t dpi,
            const wchar_t *fallbackName = L"Segoe UI") noexcept
        {
            reset();
            LOGFONTW font = source;
            if(font.lfHeight == 0)
                font.lfHeight = -MulDiv(9, static_cast<int>(dpi), 72);
            if(font.lfFaceName[0] == L'\0')
            {
                ::StringCchCopyW(font.lfFaceName, _countof(font.lfFaceName),
                    fallbackName ? fallbackName : L"Segoe UI");
            }
            text_ = ::CreateFontIndirectW(&font);
            if(!text_)
                return false;

            // The native menu renderer uses the text font for fallback symbols.
            // A caller can still replace value.glyphFont with a borrowed icon
            // font after this method returns.
            glyph_ = text_;
            return true;
        }

        HFONT text_{};
        HFONT glyph_{};
    };

    class NativeMenuRendererAdapter final
    {
    public:
        static NativeMenuColor Color(const Drawing::Color &source) noexcept
        {
            // Drawing::Color keeps the logical red/green/blue channels in its
            // BGRA storage because its COLORREF helpers pass the bytes back to
            // Win32 in that order.  The renderer contract is an actual DIB
            // pixel (B,G,R,A), so reverse the two outer accessors here.  A
            // direct copy makes asymmetric authored colors such as #1e1e2e
            // render as #2e1e1e in Pbgra output.
            return {source.r(), source.g(), source.b(), source.get_a()};
        }

        static NativeMenuMargin Margin(const ::Nilesoft::Margin &source) noexcept
        {
            return {source.left, source.top, source.right, source.bottom};
        }

        static NativeMenuColorState State(const Theme::state_t &source) noexcept
        {
            return {Color(source.nor), Color(source.sel), Color(source.nor_dis),
                Color(source.sel_dis)};
        }

        // Translate an already resolved runtime theme.  The native extension
        // calls this after ContextMenu::init_cfg has evaluated all settings;
        // the preview worker can use the same method after resolving its
        // request-owned Theme value.
        static bool ThemeValue(const Shell::Theme &source,
            NativeMenuRenderTheme &destination, uint32_t dpi,
            HFONT textFont = nullptr, HFONT glyphFont = nullptr,
            HTHEME menuTheme = nullptr, HBRUSH backgroundBrush = nullptr,
            NativeMenuRenderSymbolHandles symbols = {},
            bool composition = false,
            bool desktopEffectsOmitted = false) noexcept
        {
            (void)symbols;
            if(dpi < NativeMenuRendererMinDpi || dpi > NativeMenuRendererMaxDpi)
                return false;
            destination = {};
            destination.version = NativeMenuRendererVersion;
            destination.dpi = dpi;
            destination.background = Color(source.background.color);
            destination.backgroundTint = Color(source.background.tintcolor);
            destination.gradient.enabled = source.gradient.enabled;
            std::copy(std::begin(source.gradient.linear),
                std::end(source.gradient.linear), std::begin(destination.gradient.linear));
            std::copy(std::begin(source.gradient.radial),
                std::end(source.gradient.radial), std::begin(destination.gradient.radial));
            destination.gradient.stops.reserve(source.gradient.stpos.size());
            for(const auto &stop : source.gradient.stpos)
                destination.gradient.stops.push_back({stop.offset, Color(stop.color)});
            destination.text = State(source.text.color);
            destination.textTap = source.text.tap > 0 ?
                static_cast<uint32_t>(source.text.tap) : 0U;
            destination.textPrefix = source.text.prefix == 0xFFFFFFFFU ?
                static_cast<UINT>(DT_HIDEPREFIX) : source.text.prefix;
            destination.item = State(source.back.color);
            // `Theme::back.opacity` is the opacity of the item surface.  The
            // runtime applies it while painting; fold it into the resolved
            // renderer colors so preview and live owner-draw use the same
            // alpha contract.
            if(source.back.opacity != 255)
            {
                auto apply_opacity = [opacity = source.back.opacity](
                    NativeMenuColor &color) noexcept
                {
                    color.a = static_cast<uint8_t>(
                        (static_cast<uint32_t>(color.a) * opacity + 127U) /
                        255U);
                };
                apply_opacity(destination.item.normal);
                apply_opacity(destination.item.selected);
                apply_opacity(destination.item.disabled);
                apply_opacity(destination.item.selectedDisabled);
            }
            destination.itemBorder = State(source.back.border);
            destination.separator = Color(source.separator.color);
            destination.shadow = Color(source.shadow.color);
            destination.shadowEnabled = source.shadow.enabled &&
                source.shadow.size > 0 && destination.shadow.a != 0;
            destination.shadowSize = source.shadow.size;
            destination.shadowOffset = source.shadow.offset;
            destination.frame = Color(source.border.color);
            destination.symbols.chevron = State(source.symbols.chevron);
            destination.symbols.checked = State(source.symbols.checked);
            destination.symbols.bullet = State(source.symbols.bullet);
            for(size_t index = 0; index < 3; ++index)
                destination.imageColors[index] = Color(source.image.color[index]);
            destination.imageEnabled = source.image.enabled;
            destination.itemMargin = Margin(source.back.margin);
            destination.itemPadding = Margin(source.back.padding);
            destination.separatorMargin = Margin(source.separator.margin);
            destination.framePadding = Margin(source.border.padding);
            destination.itemRadius = source.back.radius;
            destination.frameRadius = source.border.radius;
            destination.frameSize = source.border.size;
            destination.imageSize = source.image.size;
            destination.imageGap = source.image.gap;
            destination.imageScale = source.image.scale;
            destination.separatorSize = source.separator.size;
            destination.minWidth = source.layout.min_width;
            destination.maxWidth = source.layout.max_width;
            destination.imageDisplay = static_cast<uint8_t>(
                std::clamp(source.image.display, 0, 2));
            destination.rtl = source.layout.rtl != 0;
            destination.composition = composition || source.background.effect >= 2;
            destination.opaqueInterior = !destination.composition &&
                destination.background.a == 255;
            destination.desktopEffectsOmitted = desktopEffectsOmitted ||
                (destination.composition && source.background.opacity < 255);
            destination.font = source.font;
            destination.textFont = textFont;
            destination.shortcutFont = textFont;
            destination.glyphFont = glyphFont ? glyphFont : textFont;
            destination.menuTheme = menuTheme;
            destination.backgroundBrush = backgroundBrush;
            return NativeMenuRenderer::ValidateTheme(destination);
        }

        // Resolve the parsed settings.theme expressions into the same Theme
        // value that ContextMenu::init_cfg hands to its painter.  The adapter
        // deliberately has no HWND, HMENU, selection, or command side effects;
        // Context::Preview remains the authority for unavailable expressions.
        // `destination` carries the caller's requested preview mode (0 light,
        // 1 dark, 2 high contrast) and is replaced with a fully initialized
        // value on success.
        static bool ConfigureTheme(Context &context, DPI &dpi,
            Theme &destination, NativeMenuRenderTheme &render,
            bool composition = false, HFONT textFont = nullptr,
            HFONT glyphFont = nullptr, HTHEME menuTheme = nullptr,
            HBRUSH backgroundBrush = nullptr,
            NativeMenuRenderSymbolHandles symbols = {},
            std::string *diagnostic = nullptr,
            NativeMenuRenderResourceLoader resourceLoader = {}) noexcept
        {
            try
            {
                if(!context.Cache || dpi.val < NativeMenuRendererMinDpi ||
                    dpi.val > NativeMenuRendererMaxDpi)
                {
                    SetDiagnostic(diagnostic, "preview theme context is unavailable");
                    return false;
                }

                auto *settings = &context.Cache->settings.theme;
                const auto requestedMode = destination.mode > 2 ? 0 : destination.mode;
                bool dark = requestedMode == 1;
                ThemeType type = requestedMode == 2 ? ThemeType::HighContrast :
                    ThemeType::Auto;

                context.dpi = &dpi;
                context.theme = &destination;

                Object value;
                if(context.Eval(settings->dark, value) && value.not_default())
                    dark = value.to_bool();

                if(context.Eval(settings->name, value) && value.not_default())
                {
                    if(value.is_number())
                    {
                        const auto candidate = value.to_number<ThemeType>();
                        if(candidate >= ThemeType::Auto &&
                            candidate <= ThemeType::Custom)
                            type = candidate;
                    }
                    else if(value.is_string())
                    {
                        const auto name = value.to_string().trim().hash();
                        type = ThemeTypeFromHash(name, type);
                    }
                }

                // Keep preview mode authoritative when the caller supplies
                // high contrast.  A source `theme.name` may still choose any
                // concrete light/dark family for modes 0 and 1.
                if(requestedMode == 2)
                    type = ThemeType::HighContrast;

                bool transparent = composition;
                int8_t effect = composition ? 2 : 0;
                if(context.Eval(settings->background.effect, value) &&
                    value.not_default())
                {
                    if(value.is_number())
                        effect = static_cast<int8_t>(std::clamp<int>(
                            value.to_number<int>(), 0, 5));
                    else if(value.is_string())
                    {
                        const auto effectName = value.to_string().trim().hash();
                        effect = EffectFromHash(effectName, effect);
                    }
                }

                // Runtime `effect=auto` selects a desktop effect only when
                // the caller opted into a composed surface.  Resolve that
                // sentinel before choosing the factory so the render contract
                // never carries the unrepresentable -1 value.
                if(effect < 0)
                    effect = composition ? 2 : 0;
                transparent = effect != 0;

                switch(type)
                {
                    case ThemeType::HighContrast:
                        destination = Theme::HighContrast();
                        break;
                    case ThemeType::White:
                        destination = Theme::White(transparent ? 1 : 0);
                        break;
                    case ThemeType::Black:
                        destination = Theme::Black(transparent ? 1 : 0);
                        break;
                    case ThemeType::Modern:
                        destination = Theme::Modern(
                            dark ? ThemeType::Dark : ThemeType::Light,
                            dark ? 1 : 0, transparent ? 3 : 0);
                        break;
                    case ThemeType::Dark:
                        destination = Theme::Dark(false, transparent ? 1 : 0);
                        break;
                    case ThemeType::Light:
                    case ThemeType::Edge:
                    case ThemeType::Classic:
                    case ThemeType::System:
                    case ThemeType::Auto:
                    case ThemeType::Custom:
                    default:
                        destination = dark ? Theme::Dark(false,
                            transparent ? 1 : 0) : Theme::Light(false,
                            transparent ? 1 : 0);
                        break;
                }

                destination.Type = type;
                destination.mode = type == ThemeType::HighContrast ? 2 :
                    static_cast<uint8_t>(dark ? 1 : 0);
                destination.dpi = &dpi;
                destination.system.mode = destination.mode;
                destination.system.transparency = transparent;
                destination.enableTransparency = transparent;
                destination.systemUsesLightTheme = !dark;
                destination.appsUseLightTheme = !dark;
                destination.isHighContrast = type == ThemeType::HighContrast;
                destination.font = {};
                Theme::GetFont(&destination.font, dpi.val);
                context.theme = &destination;

                ApplySettings(context, *settings, destination, value);

                destination.background.effect = effect;
                destination.background.opacity = destination.background.color.a;
                if(effect == 0)
                    destination.background.color.a = 255;
                destination.enableTransparency = destination.background.color.a != 255 ||
                    effect != 0;
                destination.system.transparency = destination.enableTransparency;
                destination.dpi = &dpi;
                destination.scale();

                const auto ok = ThemeValue(destination, render, dpi.val,
                    textFont, glyphFont, menuTheme, backgroundBrush, symbols,
                    composition || effect >= 2,
                    (composition || effect >= 2) &&
                        destination.background.opacity < 255);
				if(ok && !destination.background.image.empty())
                {
                    if(!resourceLoader)
                    {
                        SetDiagnostic(diagnostic,
                            "background image requires a preview resource loader");
                        return false;
                    }
                    NativeMenuRenderImage image;
					if(!resourceLoader(ToWide(destination.background.image), image) ||
                        !NativeMenuRenderer::ValidateItem(
                            NativeMenuRenderItem{.image = image}))
                    {
                        SetDiagnostic(diagnostic,
                            "configured background image is unavailable");
                        return false;
                    }
                    render.backgroundImage = std::move(image);
                }
                if(!ok)
                    SetDiagnostic(diagnostic, "resolved preview theme is invalid");
                return ok;
            }
            catch(...)
            {
                SetDiagnostic(diagnostic, "preview theme evaluation failed");
                return false;
            }
        }

        static bool MenuValue(const std::vector<MenuItemInfo *> &source,
            NativeMenuRenderMenu &destination,
            NativeMenuRenderSymbolHandles symbols = {},
            bool drawImages = true, bool drawChecks = true,
            bool rtl = false) noexcept
        {
            destination = {};
            destination.version = NativeMenuRendererVersion;
            destination.drawImages = drawImages;
            destination.drawChecks = drawChecks;
            destination.rtl = rtl;
            destination.chevron = symbols.chevron;
            destination.checked = symbols.checked;
            destination.bullet = symbols.bullet;
            try
            {
                for(const auto *entry : source)
                {
                    if(!entry)
                        return false;
                    destination.hasColumn = destination.hasColumn || entry->column != 0;
                    destination.textWidth = (std::max)(destination.textWidth,
                        entry->size.cx > 0 ? static_cast<uint32_t>(entry->size.cx) : 0U);
                }
            }
            catch(...)
            {
                return false;
            }
            return true;
        }

        static bool ItemValue(const MenuItemInfo &source,
            NativeMenuRenderItem &destination, bool includeNativeBitmap = true) noexcept
        {
            destination = {};
            destination.id = source.wID == UINT_MAX ? source.id : source.wID;
            destination.title = ToWide(source.title.text);
            destination.keys = ToWide(source.keys);
            destination.separator = source.is_separator();
            destination.popup = source.is_popup();
            destination.checked = source.is_checked();
            destination.radio = source.is_radiocheck();
            destination.disabled = source.is_disabled();
            destination.label = source.is_label();
            destination.staticItem = source.is_static();
            destination.ownerDraw = source.is_ownerdraw();
            destination.tab = source.tab;
            destination.preferredSize = source.size;
            destination.isDefault = (source.fState & MFS_DEFAULT) != 0;

            if(includeNativeBitmap)
            {
                // FindImage handles the documented system bitmap locations in
                // dwItemData and filters sentinel HBMMENU values.
                const auto bitmap = source.image.hbitmap ? source.image.hbitmap :
                    MenuItemInfo::FindImage(const_cast<MenuItemInfo *>(
                        &source));
                if(bitmap)
                {
                    destination.image.kind = NativeMenuImageKind::bitmap;
                    destination.image.bitmap.handle = bitmap;
                    destination.image.bitmap.size = source.image.size;
                    if(destination.image.bitmap.size.cx <= 0 ||
                        destination.image.bitmap.size.cy <= 0)
                        destination.image.bitmap.size = {0, 0};
                }
            }
            if(destination.image.kind == NativeMenuImageKind::none)
                DrawValue(source.image.draw, destination.image);
            DrawValue(source.image_select.draw, destination.selectedImage);
            if(source.image_select.hbitmap)
            {
                destination.selectedImage.kind = NativeMenuImageKind::bitmap;
                destination.selectedImage.bitmap.handle = source.image_select.hbitmap;
                destination.selectedImage.bitmap.size = source.image_select.size;
            }
            return NativeMenuRenderer::ValidateItem(destination);
        }

        static bool ItemsValue(const std::vector<MenuItemInfo *> &source,
            std::vector<NativeMenuRenderItem> &destination,
            bool includeNativeBitmap = true) noexcept
        {
            destination.clear();
            try
            {
                if(source.size() > 4096)
                    return false;
                destination.reserve(source.size());
                for(const auto *entry : source)
                {
                    if(!entry)
                        return false;
                    NativeMenuRenderItem value;
                    if(!ItemValue(*entry, value, includeNativeBitmap))
                    {
                        destination.clear();
                        return false;
                    }
                    destination.push_back(std::move(value));
                }
                return true;
            }
            catch(...)
            {
                destination.clear();
                return false;
            }
        }

        // Build preview rows from the parser's authored NativeMenu tree.  This
        // is intentionally a value-only adapter: it evaluates expressions in
        // the menu's local scope, but never creates an HMENU, consults
        // ContextMenu/Explorer state, invokes a command, or loads an image.
        // A worker can therefore use the same row contract as the live
        // MenuItemInfo path without importing the Explorer construction phase.
        static bool BuildMenu(Context &context, const NativeMenu &source,
            NativeMenuRenderMenu &menu, std::vector<NativeMenuRenderItem> &items,
            NativeMenuRenderSymbolHandles symbols = {}, bool drawImages = true,
            bool drawChecks = true, bool rtl = false,
            const NativeMenuConstruction::BuildOptions &options = {},
            std::vector<const NativeMenu *> *sources = nullptr,
            NativeMenuRenderResourceLoader resourceLoader = {},
            NativeMenuRenderDiagnosticSink diagnosticSink = {},
            HFONT glyphFont = nullptr) noexcept
        {
            menu = {};
            items.clear();
            if(sources)
                sources->clear();
            menu.version = NativeMenuRendererVersion;
            menu.drawImages = drawImages;
            menu.drawChecks = drawChecks;
            menu.rtl = rtl;
            menu.chevron = symbols.chevron;
            menu.checked = symbols.checked;
            menu.bullet = symbols.bullet;

            try
            {
                if(source.items.size() > 4096)
                    return false;
                items.reserve((std::min)(source.items.size(), size_t(4096)));
                if(sources)
                    sources->reserve((std::min)(source.items.size(), size_t(4096)));

                const auto append_separator = [&]() -> bool
                {
                    NativeMenuRenderItem separator;
                    separator.id = UINT_MAX;
                    separator.separator = true;
                    if(!NativeMenuRenderer::ValidateItem(separator))
                        return false;
                    items.push_back(std::move(separator));
                    if(sources)
                        sources->push_back(nullptr);
                    return true;
                };

                // A root menu is represented by its children.  Callers can
                // still pass an authored item or separator and receive one
                // row, which is useful for focused preview probes.
                const auto append_one = [&](const NativeMenu *item,
                    auto &&append_one_ref, unsigned depth) -> bool
                {
                    if(!item || depth > 64 || items.size() >= 4096)
                        return false;

                    struct RestoreContext
                    {
                        Context &context;
                        Scope *local;
                        this_item *current;
                        ~RestoreContext() noexcept
                        {
                            context.variables.local = local;
                            context._this = current;
                        }
                    } restore{context, context.variables.local, context._this};

                    context.variables.local = const_cast<Scope *>(&item->variables);
                    this_item current{};
                    current.type = item->is_separator() ? 0 :
                        (item->is_menu() ? 2 : 1);
                    current.pos = static_cast<int>(items.size());
                    context._this = &current;

                    if(item->is_separator())
                        return append_separator();

                    const auto visibility = context.parse_visibility(item->visibility);
                    if(context.Preview && context.Preview->failed)
                        return false;
                    if(!NativeMenuConstruction::visible(visibility))
                        return true;

                    if(options.evaluateRules)
                    {
                        if(!NativeMenuConstruction::dynamic_types_match(
                            options.selection, *item))
                            return true;

                        const auto mode = context.parse_mode(item->mode,
                            options.inheritedMode);
                        if(!NativeMenuConstruction::dynamic_mode_match(
                            options.selection, mode))
                            return true;

                        if(item->where && !context.eval_bool(item->where))
                            return true;
                        if(context.Preview && context.Preview->failed)
                            return false;
                    }

                    string title;
                    if(item->title)
                        context.Eval(item->title, title, false);
                    if(context.Preview && context.Preview->failed)
                        return false;
                    if(!NativeMenuConstruction::title_or_image(*item, title))
                        return true;
                    current.title = title.c_str();
                    current.title_normalize = title.c_str();
                    current.length = title.length<uint32_t>();

                    if(options.evaluateRules && item->find)
                    {
                        string pattern;
                        const bool evaluated = context.Eval(item->find,
                            pattern, true);
                        if(context.Preview && context.Preview->failed)
                            return false;
                        if(evaluated && !pattern.empty() &&
                            !options.selection.matches_find(pattern))
                            return true;
                    }

                    // Expanded authored menus are inserted into the current
                    // menu by the runtime.  Preserve that behavior while
                    // keeping ordinary menus as popup rows.
                    bool expanded = false;
                    if(item->is_menu() && item->expanded)
                    {
                        expanded = context.eval_bool(item->expanded);
                        if(context.Preview && context.Preview->failed)
                            return false;
                    }
                    if(item->is_menu() && expanded)
                    {
                        if(item->items.size() > 4096 - items.size())
                            return false;
                        for(const auto *child : item->items)
                            if(!append_one_ref(child, append_one_ref, depth + 1))
                                return false;
                        return true;
                    }

                    Separator itemSeparator = Separator::None;
                    if(item->separator)
                    {
                        itemSeparator = context.parse_separator(item->separator);
                        if(context.Preview && context.Preview->failed)
                            return false;
                    }

                    if(NativeMenuConstruction::separator_top(itemSeparator) &&
                        !append_separator())
                        return false;

                    NativeMenuRenderItem value;
                    value.id = static_cast<uint32_t>(items.size() + 1);
                    if(item->explicit_id)
                    {
                        Object explicitId = context.Eval(item->explicit_id).move();
                        if(context.Preview && context.Preview->failed)
                            return false;
                        if(explicitId.is_number())
                        {
                            const auto number = explicitId.to_number<int64_t>();
                            if(number >= 0 && number <=
                                static_cast<int64_t>((std::numeric_limits<uint32_t>::max)()))
                                value.id = static_cast<uint32_t>(number);
                        }
                        else if(explicitId.is_string())
                        {
                            value.id = explicitId.to_string().trim().hash();
                        }
                    }
                    value.title = ToWide(title);
                    value.separator = false;
                    value.popup = item->is_menu();
                    value.label = NativeMenuConstruction::label(visibility);
                    value.staticItem = NativeMenuConstruction::static_item(visibility);
                    value.disabled = NativeMenuConstruction::disabled(visibility);

                    string keys;
                    if(item->keys)
                        context.Eval(item->keys, keys, false);
                    if(context.Preview && context.Preview->failed)
                        return false;
                    value.keys = ToWide(keys);

                    // Authored image expressions are evaluated through the
                    // side-effect-free value adapter.  Path-backed resources
                    // go through the injected loader; no Context::Image or
                    // process-wide image cache is touched here.
                    if(drawImages && item->image.enabled() && item->image.expr)
                    {
                        if(!RenderImage(context, item->image.expr, value.image,
                            resourceLoader, diagnosticSink, glyphFont, 16))
                        {
                            if(context.Preview && context.Preview->failed)
                                return false;
                        }
                    }
                    if(drawImages && item->images.select)
                    {
                        if(!RenderImage(context, item->images.select,
                            value.selectedImage, resourceLoader, diagnosticSink,
                            glyphFont, 16) && context.Preview &&
                            context.Preview->failed)
                            return false;
                    }

                    if(item->checked)
                    {
                        Object checked = context.Eval(item->checked).move();
                        if(context.Preview && context.Preview->failed)
                            return false;
                        if(checked.not_default())
                        {
                            const auto state = checked.to_number<int>();
                            value.checked = state > 0;
                            value.radio = state == 2;
                        }
                    }

                    if(item->column)
                    {
                        Object column = context.Eval(item->column).move();
                        if(context.Preview && context.Preview->failed)
                            return false;
                        if(column.not_default())
                            menu.hasColumn = menu.hasColumn ||
                                column.to_number<int>() != 0;
                    }

                    menu.textWidth = (std::max)(menu.textWidth,
                        static_cast<uint32_t>(value.title.size()));
                    if(!NativeMenuRenderer::ValidateItem(value))
                        return false;
                    items.push_back(std::move(value));
                    if(sources)
                        sources->push_back(item);

                    // `separator=top/bottom/both` is a row-level decoration
                    // in the runtime.  Add the corresponding rows around the
                    // authored item without evaluating any command property.
                    if(NativeMenuConstruction::separator_bottom(itemSeparator) &&
                        !append_separator())
                        return false;
                    return true;
                };

                if(source.is_menu())
                {
                    for(const auto *item : source.items)
                        if(!append_one(item, append_one, 0))
                            return false;
                }
                else if(!append_one(&source, append_one, 0))
                    return false;

                return true;
            }
            catch(...)
            {
                items.clear();
                if(sources)
                    sources->clear();
                menu = {};
                return false;
            }
        }

        // Keep the menu naming parallel with MenuValue(vector<MenuItemInfo*>)
        // so callers selecting either live or authored input do not need a
        // second renderer assembly path.
        static bool MenuValue(Context &context, const NativeMenu &source,
            NativeMenuRenderMenu &menu, std::vector<NativeMenuRenderItem> &items,
            NativeMenuRenderSymbolHandles symbols = {}, bool drawImages = true,
            bool drawChecks = true, bool rtl = false,
            const NativeMenuConstruction::BuildOptions &options = {},
            std::vector<const NativeMenu *> *sources = nullptr,
            NativeMenuRenderResourceLoader resourceLoader = {},
            NativeMenuRenderDiagnosticSink diagnosticSink = {},
            HFONT glyphFont = nullptr) noexcept
        {
            return BuildMenu(context, source, menu, items, symbols,
                drawImages, drawChecks, rtl, options, sources,
                std::move(resourceLoader), std::move(diagnosticSink), glyphFont);
        }

        // Construct the displayed preview tree from a captured/sample native
        // menu and the parser cache.  `baseRoot` contains only value data from
        // the native capture; authored NativeMenu nodes are consulted through
        // the cache and retained as optional `source` pointers in output.
        //
        // The implementation deliberately keeps this operation value-only:
        // it never creates HMENU/HWND state, runs a command, or calls the
        // runtime image loader.  The resource callback is the sole path for a
        // path-backed image.  A lazy empty `requestedPath` leaves authored
        // popup children unmaterialized, matching WM_INITMENUPOPUP behavior.
        static bool BuildPreview(Context &context, const CACHE &cache,
            const NativeMenuPreviewTree &baseRoot,
            const NativeMenuConstruction::BuildOptions &options,
            NativeMenuPreviewTree &output,
            NativeMenuRenderResourceLoader resourceLoader = {},
            NativeMenuRenderDiagnosticSink diagnosticSink = {},
            HFONT glyphFont = nullptr) noexcept
        {
            output.clear();
            if(baseRoot.size() > kPreviewMaxItems)
                return false;

            struct RestoreContext
            {
                Context &context;
                CACHE *cache;
                Scope *local;
                this_item *current;
                ~RestoreContext() noexcept
                {
                    context.Cache = cache;
                    context.variables.local = local;
                    context._this = current;
                }
            } restore{context, context.Cache, context.variables.local,
                context._this};

            try
            {
                context.Cache = const_cast<CACHE *>(&cache);
                auto resolved = options;
                if(!ResolveBuildOptions(context, cache, resolved))
                    return false;

                output = baseRoot;
                std::size_t count = 0;
                for(auto &node : output)
                    if(!NormalizeBaseNode(node, {}, 0, count))
                        return false;

                if(resolved.applyStatic && resolved.modifyEnabled)
                {
                    for(const auto *rule : cache.statics)
                    {
                        if(!rule || rule->has_clsid)
                            continue;
                        if(!ApplyStaticRule(context, cache, *rule, output,
                            resolved, resourceLoader, diagnosticSink,
                            glyphFont))
                            return false;
                        if(context.Preview && context.Preview->failed)
                            return false;
                    }
                    if(!RehomePreviewRows(output, output, {}))
                        return false;
                    for(auto &node : output)
                        if(!ReorderTree(node))
                            return false;
                    ReorderList(output);
                    ApplySystemRemovals(output, resolved);
                }

                if(resolved.applyDynamic && resolved.newItemsEnabled)
                {
                    std::vector<PreviewDynamicCandidate> candidates;
                    std::map<std::wstring, std::size_t> authoredIds;
                    for(const auto *source : cache.dynamic.items)
                    {
                        if(!BuildDynamicCandidate(context, cache, source,
                            {}, true, 0, resolved, candidates, authoredIds,
                            resourceLoader, diagnosticSink, glyphFont))
                            return false;
                    }
                    if(!InsertDynamicCandidates(context, cache, output,
                        resolved, candidates, diagnosticSink))
                        return false;
                }

                if(!MaterializePreviewPath(context, cache, output,
                    resolved.requestedPath, resolved, resourceLoader,
                    diagnosticSink, glyphFont))
                    return false;
                if(!ApplyDecorations(output))
                    return false;
                return ValidatePreviewTree(output, 0, count = 0);
            }
            catch(...)
            {
                output.clear();
                return false;
            }
        }

        // Convenience overload for callers that want to replace a tree in
        // place after a successful construction pass.
        static bool BuildPreview(Context &context, const CACHE &cache,
            NativeMenuPreviewTree &tree,
            const NativeMenuConstruction::BuildOptions &options,
            NativeMenuRenderResourceLoader resourceLoader = {},
            NativeMenuRenderDiagnosticSink diagnosticSink = {},
            HFONT glyphFont = nullptr) noexcept
        {
            NativeMenuPreviewTree built;
            if(!BuildPreview(context, cache, tree, options, built,
                std::move(resourceLoader), std::move(diagnosticSink),
                glyphFont))
                return false;
            tree = std::move(built);
            return true;
        }

        // Locate a captured or authored popup by its stable id or normalized
        // title.  The returned row vector is suitable for FlattenPreviewTree.
        static bool SelectPreviewPath(const NativeMenuPreviewTree &tree,
            const std::vector<std::wstring> &path,
            const NativeMenuPreviewTree *&rows) noexcept
        {
            rows = &tree;
            try
            {
                for(const auto &part : path)
                {
                    const NativeMenuPreviewNode *selected = nullptr;
                    for(const auto &node : *rows)
                    {
                        if(PreviewPathPartMatches(node, part))
                        {
                            selected = &node;
                            break;
                        }
                    }
                    if(!selected || !selected->childrenAvailable)
                    {
                        rows = nullptr;
                        return false;
                    }
                    rows = &selected->children;
                }
                return true;
            }
            catch(...)
            {
                rows = nullptr;
                return false;
            }
        }

        // Flatten one tree level into the renderer's row contract.  Children
        // remain in NativeMenuPreviewNode and are selected explicitly through
        // SelectPreviewPath, so root rendering never evaluates unopened rows.
        static bool FlattenPreviewTree(const NativeMenuPreviewTree &source,
            NativeMenuRenderMenu &menu,
            std::vector<NativeMenuRenderItem> &items,
            std::vector<const NativeMenu *> *sources = nullptr) noexcept
        {
            menu = {};
            items.clear();
            if(sources)
                sources->clear();
            menu.version = NativeMenuRendererVersion;
            try
            {
                if(source.size() > kPreviewMaxItems)
                    return false;
                items.reserve(source.size());
                if(sources)
                    sources->reserve(source.size());
                for(const auto &node : source)
                {
                    auto value = node.value;
                    if(node.ownerDraw)
                    {
                        value.ownerDraw = true;
                        value.disabled = true;
                    }
                    if(value.separator)
                        value.id = UINT_MAX;
                    if(!NativeMenuRenderer::ValidateItem(value))
                    {
                        items.clear();
                        if(sources)
                            sources->clear();
                        return false;
                    }
                    menu.textWidth = (std::max)(menu.textWidth,
                        static_cast<uint32_t>(value.title.size()));
                    items.push_back(std::move(value));
                    if(sources)
                        sources->push_back(node.source);
                }
                menu.hasColumn = std::any_of(source.begin(), source.end(),
                    [](const auto &node) { return node.value.tab >= 0; });
                menu.drawImages = true;
                menu.drawChecks = true;
                return true;
            }
            catch(...)
            {
                menu = {};
                items.clear();
                if(sources)
                    sources->clear();
                return false;
            }
        }

        static bool FlattenPreviewPath(const NativeMenuPreviewTree &tree,
            const std::vector<std::wstring> &path,
            NativeMenuRenderMenu &menu,
            std::vector<NativeMenuRenderItem> &items,
            std::vector<const NativeMenu *> *sources = nullptr) noexcept
        {
            const NativeMenuPreviewTree *rows = nullptr;
            return SelectPreviewPath(tree, path, rows) && rows &&
                FlattenPreviewTree(*rows, menu, items, sources);
        }

    private:
        static constexpr std::size_t kPreviewMaxItems = 4096;
        static constexpr unsigned kPreviewMaxDepth = 64;

        struct PreviewDynamicCandidate
        {
            NativeMenuPreviewNode node;
            std::wstring targetPath;
            bool explicitPosition{};
        };

        static std::wstring NormalizePreviewPart(std::wstring_view value)
        {
            // Use the same normalization as MenuItemInfo for paths and
            // duplicate matching.  The runtime removes accelerator markers,
            // folds punctuation runs, and retains the display text's case;
            // paths are compared case-insensitively below.
            string text(std::wstring(value).c_str());
            string normalized;
            MenuItemInfo::normalize(text, &normalized);
            return ToWide(normalized);
        }

        static bool PreviewTextEquals(std::wstring_view left,
            std::wstring_view right) noexcept
        {
            if(left.size() != right.size())
                return false;
            for(std::size_t index = 0; index < left.size(); ++index)
                if(std::towlower(left[index]) != std::towlower(right[index]))
                    return false;
            return true;
        }

        static std::wstring JoinPreviewPath(std::wstring_view parent,
            std::wstring_view title)
        {
            auto child = NormalizePreviewPart(title);
            if(parent.empty())
                return child;
            if(child.empty())
                return std::wstring(parent);
            std::wstring result(parent);
            result.push_back(L'/');
            result += child;
            return result;
        }

        static bool PreviewPathEquals(std::wstring_view left,
            std::wstring_view right) noexcept
        {
            return PreviewTextEquals(left, right) ||
                PreviewTextEquals(NormalizePreviewPart(left),
                    NormalizePreviewPart(right));
        }

        static std::size_t SourceOffset(const NativeMenu &source) noexcept
        {
            if(source.source_node_id.size() < 2 ||
                source.source_node_id.front() != L'n')
                return (std::numeric_limits<std::size_t>::max)();

            std::size_t result = 0;
            for(std::size_t index = 1; index < source.source_node_id.size();
                ++index)
            {
                const auto digit = source.source_node_id[index];
                if(digit < L'0' || digit > L'9')
                    return (std::numeric_limits<std::size_t>::max)();
                const auto value = static_cast<std::size_t>(digit - L'0');
                if(result > ((std::numeric_limits<std::size_t>::max)() - value) /
                    10U)
                    return (std::numeric_limits<std::size_t>::max)();
                result = result * 10U + value;
            }
            return result;
        }

        static std::wstring AuthoredIdentity(const NativeMenu &source,
            const NativeMenuConstruction::BuildOptions &options)
        {
            const auto offset = SourceOffset(source);
            if(options.sourceIdentity &&
                offset != (std::numeric_limits<std::size_t>::max)())
            {
                auto resolved = options.sourceIdentity(
                    std::wstring_view(source.source_file.c_str(),
                        source.source_file.length()), offset);
                if(!resolved.empty())
                    return resolved;
            }
            if(!source.source_file.empty() && !source.source_node_id.empty())
                return ToWide(source.source_file) + L"#" + source.source_node_id;
            if(!source.source_node_id.empty())
                return source.source_node_id;
            if(offset != (std::numeric_limits<std::size_t>::max)())
                return L"source@" + std::to_wstring(offset);
            return L"source";
        }

        static void AddExplanation(NativeMenuPreviewNode &node,
            std::wstring_view text) noexcept
        {
            try
            {
                if(node.explanations.size() < 64 && text.size() <= 1024)
                    node.explanations.emplace_back(text);
            }
            catch(...) { }
        }

        static void AddRuleExplanation(NativeMenuPreviewNode &node,
            const NativeMenu &rule,
            const NativeMenuConstruction::BuildOptions &options) noexcept
        {
            try
            {
                AddExplanation(node, L"Applicable modification rule: " +
                    AuthoredIdentity(rule, options));
            }
            catch(...) { AddExplanation(node, L"An applicable modification rule was evaluated."); }
        }

        static void RecordDecision(
            const NativeMenuConstruction::BuildOptions &options,
            const NativeMenu &source, std::wstring_view state,
            std::wstring_view reason) noexcept
        {
            if(!options.decisionSink) return;
            try { options.decisionSink(source, state, reason); }
            catch(...) { }
        }

        static bool PreviewPathPartMatches(const NativeMenuPreviewNode &node,
            std::wstring_view part) noexcept
        {
            if(part.empty())
                return false;
            if(!node.capturedId.empty() && node.capturedId == part)
                return true;
            if(!node.matchTitle.empty() &&
                (node.matchTitle == part ||
                    PreviewTextEquals(node.matchTitle, part)))
                return true;
            if(!node.value.title.empty() &&
                (node.value.title == part ||
                    PreviewTextEquals(node.value.title, part)))
                return true;
            const auto normalized = NormalizePreviewPart(part);
            return !normalized.empty() &&
                ((!node.matchTitle.empty() &&
                    PreviewTextEquals(NormalizePreviewPart(node.matchTitle),
                        normalized)) ||
                 (!node.value.title.empty() &&
                    PreviewTextEquals(NormalizePreviewPart(node.value.title),
                        normalized)));
        }

        static void SetPreviewCurrent(Context &context,
            const NativeMenuPreviewNode &node, std::size_t index,
            unsigned depth, this_item &current) noexcept
        {
            current = {};
            current.type = node.value.separator ? 0 :
                (node.value.popup ? 2 : 1);
            current.pos = static_cast<int>((std::min)(index,
                static_cast<std::size_t>((std::numeric_limits<int>::max)())));
            current.checked = node.value.radio ? 2 :
                (node.value.checked ? 1 : 0);
            current.disabled = node.value.disabled;
            current.system = node.system;
            current.id = node.value.id;
            current.length = static_cast<uint32_t>((std::min)(node.value.title.size(),
                static_cast<std::size_t>((std::numeric_limits<uint32_t>::max)())));
            current.title = node.value.title.c_str();
            current.title_normalize = node.matchTitle.empty()
                ? node.value.title.c_str() : node.matchTitle.c_str();
            current.vis = node.value.disabled ? static_cast<int>(Visibility::Disabled) :
                static_cast<int>(Visibility::Enabled);
            current.sep = static_cast<int>(node.separator);
            current.level = static_cast<int>((std::min)(depth,
                static_cast<unsigned>((std::numeric_limits<int>::max)())));
            context._this = &current;
        }

        static bool EvalString(Context &context, Expression *expression,
            std::wstring &destination, bool trim = true) noexcept
        {
            destination.clear();
            if(!expression)
                return false;
            string value;
            if(!context.Eval(expression, value, trim))
                return false;
            destination = ToWide(value);
            return !destination.empty();
        }

        static bool ParsePreviewPosition(Context &context, Expression *expression,
            Position &position, std::wstring &indexOf, int &indexOfPosition,
            int &indexOfDefault) noexcept
        {
            position = Position::Auto;
            indexOf.clear();
            indexOfPosition = 0;
            indexOfDefault = -1;
            if(!expression)
                return false;

            Object value = context.Eval(expression).move();
            if(value.is_array(true) && value.Value.Pointer)
            {
                auto *values = value.get_pointer();
                const auto count = values[0].to_number<int>();
                if(count >= 3 && values[1].to_number<uint32_t>() == IDENT_INDEXOF)
                {
                    // Keep the native string long enough to compute the same
                    // identifier hash as ContextMenu, then convert the
                    // display value at the renderer boundary.  `indexOf` is
                    // intentionally std::wstring metadata and cannot use
                    // Nilesoft::Text::string's trim/hash helpers directly.
                    auto target = values[2].to_string().trim().move();
                    indexOf = ToWide(target);
                    indexOfPosition = values[3].to_number<int>();
                    if(count >= 4)
                        indexOfDefault = static_cast<int>(context.parse_pos(
                            values[4], Position::Auto));
                    if(!indexOf.empty())
                        position = static_cast<Position>(target.hash());
                    return true;
                }
            }
            if(!value.is_null())
                position = context.parse_pos(value, Position::Auto);
            return true;
        }

        static bool RenderGlyphFromObject(const Object &object,
            NativeMenuRenderImage &destination, HFONT glyphFont,
            long defaultSize) noexcept
        {
            wchar_t glyph = 0;
            Drawing::Color color{};
            long size = defaultSize;

            auto readGlyph = [&](const Object &value) -> bool
            {
                if(value.is_number())
                    glyph = static_cast<wchar_t>(std::clamp<long long>(
                        value.to_number<long long>(), 0, 0xffff));
                else if(value.is_string())
                {
                    const auto text = value.to_string().trim();
                    if(!text.empty()) glyph = text[0];
                }
                return glyph != 0;
            };

            if(object.is_number() || object.is_string())
            {
                if(!readGlyph(object))
                    return false;
            }
            else if(object.is_array(true) && object.Value.Pointer)
            {
                auto *values = object.get_pointer();
                const auto count = values[0].to_number<int>();
                if(count < 1 || !readGlyph(values[1]))
                    return false;
                for(int index = 2; index <= count; ++index)
                {
                    if(values[index].is_color())
                        color = values[index].to_color();
                    else if(values[index].is_number())
                        size = values[index].to_number<long>();
                }
            }
            else
                return false;

            size = std::clamp<long>(size, 2, 256);
            destination = {};
            destination.kind = NativeMenuImageKind::glyph;
            destination.glyph.code[0] = glyph;
            destination.glyph.font = glyphFont;
            destination.glyph.size = {size, size};
            destination.glyph.color[0] = Color(color);
            return true;
        }

        static bool RenderImage(Context &context, Expression *expression,
            NativeMenuRenderImage &destination,
            const NativeMenuRenderResourceLoader &resourceLoader,
            const NativeMenuRenderDiagnosticSink &diagnosticSink,
            HFONT glyphFont, long defaultSize) noexcept
        {
            destination = {};
            if(!expression)
                return false;
            try
            {
                Object object;
                if(!context.Eval(expression, object) || object.is_null())
                    return false;
                if(context.Preview && context.Preview->failed)
                    return false;

                if(object.is_color())
                {
                    destination.kind = NativeMenuImageKind::shape;
                    destination.shape.solid = true;
                    destination.shape.size = {defaultSize, defaultSize};
                    destination.shape.color[0] = Color(object.to_color());
                    return true;
                }
                if(object.is_string())
                {
                    const auto path = ToWide(object.to_string().trim());
                    // Context::Image treats strings longer than two
                    // characters as path/SVG values.  Keep the short-string
                    // glyph forms available for `image='*'` and let the
                    // structured glyph parser handle them below.
                    if(path.size() > 2)
                    {
                        NativeMenuRenderImage loaded;
                        if(resourceLoader && resourceLoader(path, loaded) &&
                            NativeMenuRenderer::ValidateItem(NativeMenuRenderItem{
                                .image = loaded}))
                        {
                            destination = loaded;
                            return true;
                        }
                        if(diagnosticSink)
                            diagnosticSink(L"Preview image resource is unavailable: " +
                                path);
                        return false;
                    }
                }
                // Plain strings with more than one character are paths in the
                // runtime image contract.  Try glyph decoding only after that
                // path branch so an authored resource such as
                // `C:/Preview/fixture.png` cannot be reduced to the first
                // character and silently bypass the injected loader.
                if(RenderGlyphFromObject(object, destination, glyphFont,
                    defaultSize))
                    return true;
            }
            catch(...)
            {
                if(diagnosticSink)
                    diagnosticSink(L"Preview image expression could not be evaluated.");
            }
            return false;
        }

        static bool NormalizeBaseNode(NativeMenuPreviewNode &node,
            std::wstring_view parentPath, unsigned depth,
            std::size_t &count) noexcept
        {
            if(depth > kPreviewMaxDepth || ++count > kPreviewMaxItems)
                return false;
            node.path = std::wstring(parentPath);
            if(node.matchTitle.empty())
                node.matchTitle = node.value.title;
            node.system = true;
            node.removed = false;
            if(node.value.separator)
            {
                node.value.id = UINT_MAX;
                node.value.popup = false;
                node.children.clear();
                node.childrenAvailable = false;
            }
            else if(!node.value.popup)
            {
                node.children.clear();
                node.childrenAvailable = false;
            }
            else
            {
                node.ownerDraw = node.ownerDraw || node.value.ownerDraw;
                if(node.ownerDraw)
                    node.value.disabled = true;
                node.childrenAvailable = node.childrenAvailable ||
                    !node.children.empty();
            }
            const auto childPath = JoinPreviewPath(parentPath,
                node.matchTitle.empty() ? node.value.title : node.matchTitle);
            std::size_t childCount = 0;
            for(auto &child : node.children)
                if(!NormalizeBaseNode(child, childPath, depth + 1, count))
                    return false;
            (void)childCount;
            return NativeMenuRenderer::ValidateItem(node.value);
        }

        static bool StaticRuleMatches(Context &context,
            const NativeMenuConstruction::BuildOptions &options,
            const NativeMenu &rule, const NativeMenuPreviewNode &node,
            std::size_t index, unsigned depth) noexcept
        {
            this_item current;
            SetPreviewCurrent(context, node, index, depth, current);
            if(options.evaluateRules)
            {
                if(!NativeMenuConstruction::static_types_match(
                    options.selection, rule))
                    return false;
                const auto mode = context.parse_mode(rule.mode,
                    options.inheritedMode);
                if(!NativeMenuConstruction::static_mode_match(
                    options.selection, mode))
                    return false;
            }
            if(rule.location)
            {
                std::wstring location;
                if(!EvalString(context, rule.location, location))
                    return false;
                if(!NativeMenuConstruction::location_matches(node.path.empty(),
                    string(location.c_str()), string(node.path.c_str())))
                    return false;
            }
            else if(!node.path.empty())
                return false;

            if(options.evaluateRules && rule.where &&
                !context.eval_bool(rule.where))
                return false;
            if(options.evaluateRules && rule.find)
            {
                string pattern;
                if(!context.Eval(rule.find, pattern, true))
                    return false;
                pattern.trim().tolower();
                if(pattern.empty())
                    return rule.where != nullptr;
                if(node.value.separator || !NativeMenuConstruction::static_find_match(
                    pattern, string((node.matchTitle.empty() ?
                        node.value.title : node.matchTitle).c_str())))
                    return false;
            }
            else if(!node.value.separator && !rule.where)
                return false;
            if(node.value.separator && (!rule.where || rule.find))
                return false;
            return true;
        }

        static bool ApplyStaticRuleToNode(Context &context,
            const NativeMenu &rule, NativeMenuPreviewNode &node,
            const NativeMenuConstruction::BuildOptions &options,
            std::size_t index, unsigned depth,
            const NativeMenuRenderResourceLoader &resourceLoader,
            const NativeMenuRenderDiagnosticSink &diagnosticSink,
            HFONT glyphFont) noexcept
        {
            if(!StaticRuleMatches(context, options, rule, node, index, depth))
                return true;
            if(context.Preview && context.Preview->failed)
                return false;

            AddRuleExplanation(node, rule, options);
            const auto visibility = rule.visibility && options.modifyVisibility
                ? context.parse_visibility(rule.visibility) : Visibility::Enabled;
            if(visibility == Visibility::Hidden)
            {
                node.value.disabled = true;
                node.value.title.clear();
                node.removed = true;
                AddExplanation(node, L"The rule hid this entry, so it was removed from the evaluated preview.");
                RecordDecision(options, rule, L"hidden",
                    L"An applicable modification rule hid the matched entry.");
                return true;
            }
            if(options.modifyVisibility && rule.visibility &&
                !node.value.separator)
            {
                node.value.disabled = visibility == Visibility::Disabled;
                node.value.label = visibility == Visibility::Label;
                node.value.staticItem = visibility == Visibility::Static;
                AddExplanation(node, visibility == Visibility::Disabled ?
                    L"The rule disabled this entry." : visibility == Visibility::Label ?
                    L"The rule displayed this entry as a label." : visibility == Visibility::Static ?
                    L"The rule displayed this entry as static content." :
                    L"The rule left this entry enabled.");
            }

            this_item current;
            SetPreviewCurrent(context, node, index, depth, current);
            if(rule.checked && !node.value.separator && !node.value.popup)
            {
                const auto checked = context.eval_number<int>(rule.checked, 0);
                node.value.checked = checked > 0;
                node.value.radio = checked == 2;
            }
            if(options.modifySeparator && rule.separator)
                node.separator = context.parse_separator(rule.separator);
            if(options.modifyPosition && rule.position)
            {
                ParsePreviewPosition(context, rule.position, node.position,
                    node.indexOf, node.indexOfPosition, node.indexOfDefault);
            }
            if(options.modifyTitle && rule.title && !node.value.separator)
            {
                string title;
                if(context.Eval(rule.title, title, false) && !title.empty())
                {
                    node.value.title = ToWide(title);
                    AddExplanation(node, L"The rule renamed this entry.");
                }
            }
            if(options.modifyKeys && rule.keys && !node.value.separator)
            {
                string keys;
                if(context.Eval(rule.keys, keys, true))
                    node.value.keys = ToWide(keys);
            }
            if(options.modifyImage && rule.images.normal &&
                !node.value.separator)
            {
                RenderImage(context, rule.images.normal, node.value.image,
                    resourceLoader, diagnosticSink, glyphFont, 16);
            }
            if(options.modifyImage && rule.images.select &&
                !node.value.separator)
            {
                RenderImage(context, rule.images.select,
                    node.value.selectedImage, resourceLoader, diagnosticSink,
                    glyphFont, 16);
            }
            if(context.Preview && context.Preview->failed)
                return false;
            if(options.modifyParent && rule.moveto)
            {
                std::wstring target;
                if(EvalString(context, rule.moveto, target))
                {
                    target = NormalizePreviewPart(target);
                    if(target != node.path)
                    {
                        node.path = target;
                        AddExplanation(node, L"The rule moved this entry to another menu path.");
                    }
                }
            }
            RecordDecision(options, rule, L"applied",
                L"The modification rule matched and its enabled properties were applied.");
            return NativeMenuRenderer::ValidateItem(node.value);
        }

        static bool ApplyStaticRule(Context &context, const CACHE &cache,
            const NativeMenu &rule, NativeMenuPreviewTree &rows,
            const NativeMenuConstruction::BuildOptions &options,
            const NativeMenuRenderResourceLoader &resourceLoader,
            const NativeMenuRenderDiagnosticSink &diagnosticSink,
            HFONT glyphFont, unsigned depth = 0) noexcept
        {
            if(depth > kPreviewMaxDepth)
                return false;
            struct Restore
            {
                Context &context;
                Scope *local;
                this_item *current;
                ~Restore() noexcept
                {
                    context.variables.local = local;
                    context._this = current;
                }
            } restore{context, context.variables.local, context._this};
            context.variables.local = const_cast<Scope *>(&rule.variables);

            for(std::size_t index = 0; index < rows.size(); ++index)
            {
                auto &node = rows[index];
                if(!ApplyStaticRuleToNode(context, rule, node, options, index,
                    depth, resourceLoader, diagnosticSink, glyphFont))
                    return false;
                if(node.removed)
                {
                    rows.erase(rows.begin() + static_cast<std::ptrdiff_t>(index--));
                    continue;
                }
                if(node.value.popup && !node.children.empty())
                    if(!ApplyStaticRule(context, cache, rule, node.children,
                        options, resourceLoader, diagnosticSink, glyphFont,
                        depth + 1))
                        return false;
            }
            return true;
        }

        static bool ResolveBuildOptions(Context &context, const CACHE &cache,
            NativeMenuConstruction::BuildOptions &options) noexcept
        {
            try
            {
                const auto &settings = cache.settings;
                Object value;
                auto evalNumber = [&](Expression *expression,
                    auto &&apply) -> bool
                {
                    if(!expression || !context.Eval(expression, value) ||
                        value.is_null() || !value.not_default())
                        return false;
                    apply(value);
                    return true;
                };

                evalNumber(settings.modify_items.enabled,
                    [&](const Object &v) { options.modifyEnabled = v.to_bool(); });
                if(options.modifyEnabled)
                {
                    evalNumber(settings.modify_items.title,
                        [&](const Object &v) { options.modifyTitle = v.to_bool(); });
                    evalNumber(settings.modify_items.visibility,
                        [&](const Object &v) { options.modifyVisibility = v.to_bool(); });
                    evalNumber(settings.modify_items.parent,
                        [&](const Object &v) { options.modifyParent = v.to_bool(); });
                    evalNumber(settings.modify_items.separator,
                        [&](const Object &v) { options.modifySeparator = v.to_bool(); });
                    evalNumber(settings.modify_items.keys,
                        [&](const Object &v) { options.modifyKeys = v.to_bool(); });
                    evalNumber(settings.modify_items.position,
                        [&](const Object &v) { options.modifyPosition = v.to_number<int>(); });
                    evalNumber(settings.modify_items.remove.duplicate,
                        [&](const Object &v) { options.removeDuplicate = v.to_bool(); });
                    evalNumber(settings.modify_items.remove.disabled,
                        [&](const Object &v) { options.removeDisabled = v.to_bool(); });
                    evalNumber(settings.modify_items.remove.separator,
                        [&](const Object &v) { options.removeSeparator = v.to_bool(); });
                    evalNumber(settings.modify_items.auto_image_group,
                        [&](const Object &v)
                        {
                            if(v.to_bool())
                            {
                                options.modifyPosition = 2;
                                options.modifyImage = 2;
                            }
                        });
                    evalNumber(settings.modify_items.image,
                        [&](const Object &v) { options.modifyImage =
                            std::clamp(v.to_number<int>(), 0, 2); });
                }
                else
                {
                    options.modifyImage = 0;
                    options.modifyPosition = 0;
                    options.modifyTitle = false;
                    options.modifyVisibility = false;
                    options.modifyParent = false;
                    options.modifySeparator = false;
                    options.modifyKeys = false;
                    options.removeDuplicate = false;
                    options.removeDisabled = false;
                    options.removeSeparator = false;
                }

                evalNumber(settings.new_items.enabled,
                    [&](const Object &v) { options.newItemsEnabled = v.to_bool(); });
                if(options.newItemsEnabled)
                {
                    evalNumber(settings.new_items.image,
                        [&](const Object &v) { options.newItemsImage = v.to_bool(); });
                    evalNumber(settings.new_items.keys,
                        [&](const Object &v) { options.newItemsKeys = v.to_bool(); });
                }
                if(context.Preview && context.Preview->failed)
                    return false;
                return true;
            }
            catch(...)
            {
                return false;
            }
        }

        static bool SplitPreviewPath(std::wstring_view value,
            std::vector<std::wstring> &parts) noexcept
        {
            parts.clear();
            try
            {
                std::size_t begin = 0;
                while(begin <= value.size())
                {
                    const auto end = value.find(L'/', begin);
                    const auto length = end == std::wstring_view::npos
                        ? value.size() - begin : end - begin;
                    if(length > 0)
                    {
                        auto part = NormalizePreviewPart(value.substr(begin,
                            length));
                        if(!part.empty()) parts.push_back(std::move(part));
                    }
                    if(end == std::wstring_view::npos)
                        break;
                    begin = end + 1;
                }
                return parts.size() <= kPreviewMaxDepth;
            }
            catch(...)
            {
                parts.clear();
                return false;
            }
        }

        static std::wstring NormalizePreviewPath(std::wstring_view value)
        {
            std::vector<std::wstring> parts;
            if(!SplitPreviewPath(value, parts))
                return {};
            std::wstring result;
            for(const auto &part : parts)
            {
                if(!result.empty()) result.push_back(L'/');
                result += part;
            }
            return result;
        }

        static NativeMenuPreviewNode *FindUniquePreviewNode(
            NativeMenuPreviewTree &rows, std::wstring_view part) noexcept
        {
            NativeMenuPreviewNode *selected = nullptr;
            bool exactIdentity = false;
            for(auto &node : rows)
            {
                if(!node.capturedId.empty() && node.capturedId == part)
                {
                    // A stable identity must identify exactly one row.  A
                    // duplicate is ambiguous even when both rows carry the
                    // same id, so fail closed instead of silently choosing
                    // the last match.
                    if(exactIdentity)
                        return nullptr;
                    selected = &node;
                    exactIdentity = true;
                    continue;
                }
                if(exactIdentity || !PreviewPathPartMatches(node, part))
                    continue;
                if(selected)
                    return nullptr;
                selected = &node;
            }
            return selected;
        }

        static const NativeMenuPreviewNode *FindUniquePreviewNode(
            const NativeMenuPreviewTree &rows, std::wstring_view part) noexcept
        {
            const NativeMenuPreviewNode *selected = nullptr;
            bool exactIdentity = false;
            for(const auto &node : rows)
            {
                if(!node.capturedId.empty() && node.capturedId == part)
                {
                    if(exactIdentity)
                        return nullptr;
                    selected = &node;
                    exactIdentity = true;
                    continue;
                }
                if(exactIdentity || !PreviewPathPartMatches(node, part))
                    continue;
                if(selected)
                    return nullptr;
                selected = &node;
            }
            return selected;
        }

        static bool FindPreviewContainer(NativeMenuPreviewTree &root,
            std::wstring_view path, NativeMenuPreviewTree *&destination) noexcept
        {
            destination = &root;
            if(path.empty()) return true;
            std::vector<std::wstring> parts;
            if(!SplitPreviewPath(path, parts)) return false;
            for(const auto &part : parts)
            {
                auto *node = FindUniquePreviewNode(*destination, part);
                if(!node || !node->value.popup || !node->childrenAvailable)
                    return false;
                destination = &node->children;
            }
            return true;
        }

        static void UpdatePreviewNodePaths(NativeMenuPreviewNode &node,
            std::wstring_view parentPath) noexcept
        {
            node.path = std::wstring(parentPath);
            const auto childPath = JoinPreviewPath(parentPath,
                node.matchTitle.empty() ? node.value.title : node.matchTitle);
            for(auto &child : node.children)
                UpdatePreviewNodePaths(child, childPath);
        }

        static bool RehomePreviewRows(NativeMenuPreviewTree &root,
            NativeMenuPreviewTree &rows, std::wstring_view parentPath,
            unsigned depth = 0) noexcept
        {
            if(depth > kPreviewMaxDepth || rows.size() > kPreviewMaxItems)
                return false;
            std::vector<NativeMenuPreviewNode> pending;
            for(std::size_t index = 0; index < rows.size(); ++index)
            {
                auto &node = rows[index];
                const auto currentParent = node.path.empty() ?
                    std::wstring(parentPath) : node.path;
                const auto childPath = JoinPreviewPath(currentParent,
                    node.matchTitle.empty() ? node.value.title : node.matchTitle);
                if(!node.children.empty() && !RehomePreviewRows(root,
                    node.children, childPath, depth + 1))
                    return false;
                if(!PreviewPathEquals(node.path, parentPath))
                {
                    pending.push_back(std::move(node));
                    rows.erase(rows.begin() + static_cast<std::ptrdiff_t>(index--));
                }
            }
            for(auto &node : pending)
            {
                NativeMenuPreviewTree *destination = nullptr;
                if(!FindPreviewContainer(root, node.path, destination))
                    continue;
                UpdatePreviewNodePaths(node, node.path);
                destination->push_back(std::move(node));
                if(destination->size() > kPreviewMaxItems)
                    return false;
            }
            return true;
        }

        static bool ReorderList(NativeMenuPreviewTree &rows) noexcept
        {
            if(rows.size() > kPreviewMaxItems) return false;
            try
            {
                NativeMenuPreviewTree top, middle, bottom, automatic, custom;
                top.reserve(rows.size()); middle.reserve(rows.size());
                bottom.reserve(rows.size()); automatic.reserve(rows.size());
                custom.reserve(rows.size());
                for(auto &node : rows)
                {
                    switch(node.position)
                    {
                        case Position::Top: top.push_back(std::move(node)); break;
                        case Position::Middle: middle.push_back(std::move(node)); break;
                        case Position::Bottom: bottom.push_back(std::move(node)); break;
                        case Position::Auto:
                        case Position::None: automatic.push_back(std::move(node)); break;
                        default: custom.push_back(std::move(node)); break;
                    }
                }
                rows.clear();
                rows.reserve(top.size() + middle.size() + bottom.size() +
                    automatic.size() + custom.size());
                rows.insert(rows.end(), std::make_move_iterator(top.begin()),
                    std::make_move_iterator(top.end()));
                rows.insert(rows.end(), std::make_move_iterator(automatic.begin()),
                    std::make_move_iterator(automatic.end()));
                const auto middleAt = rows.size() / 2;
                rows.insert(rows.begin() + static_cast<std::ptrdiff_t>(middleAt),
                    std::make_move_iterator(middle.begin()),
                    std::make_move_iterator(middle.end()));
                rows.insert(rows.end(), std::make_move_iterator(bottom.begin()),
                    std::make_move_iterator(bottom.end()));

                // A custom index without indexOf is an insertion position.  A
                // position outside the current menu follows the runtime's
                // append fallback.
                for(auto &node : custom)
                {
                    const auto position = static_cast<long long>(
                        static_cast<int>(node.position));
                    const auto at = position < 0 || position >
                        static_cast<long long>(rows.size())
                        ? rows.size() : static_cast<std::size_t>(position);
                    rows.insert(rows.begin() + static_cast<std::ptrdiff_t>(at),
                        std::move(node));
                }

                // indexOf is resolved against the normalized title after the
                // ordinary position buckets have been assembled.
                for(std::size_t index = 0; index < rows.size(); ++index)
                {
                    auto &node = rows[index];
                    if(node.indexOf.empty()) continue;
                    NativeMenuPreviewNode moving = std::move(node);
                    rows.erase(rows.begin() + static_cast<std::ptrdiff_t>(index));
                    std::size_t at = rows.size();
                    std::vector<std::wstring> patterns;
                    if(SplitPreviewPath(moving.indexOf, patterns))
                    {
                        const auto pattern = patterns.empty() ?
                            NormalizePreviewPart(moving.indexOf) : patterns.back();
                        for(std::size_t candidate = 0; candidate < rows.size();
                            ++candidate)
                            if(PreviewPathPartMatches(rows[candidate], pattern))
                            {
                                const auto relative = static_cast<long long>(
                                    moving.indexOfPosition);
                                const auto base = static_cast<long long>(candidate);
                                const auto resolved = base + relative;
                                at = resolved < 0 ? 0 : resolved >
                                    static_cast<long long>(rows.size()) ? rows.size() :
                                    static_cast<std::size_t>(resolved);
                                break;
                            }
                    }
                    if(at == rows.size() && moving.indexOfDefault == -2)
                        at = 0;
                    else if(at == rows.size() && moving.indexOfDefault == -3)
                        at = rows.size() / 2;
                    rows.insert(rows.begin() + static_cast<std::ptrdiff_t>(at),
                        std::move(moving));
                }
                return rows.size() <= kPreviewMaxItems;
            }
            catch(...)
            {
                return false;
            }
        }

        static bool ReorderTree(NativeMenuPreviewNode &node) noexcept
        {
            if(node.children.size() > kPreviewMaxItems)
                return false;
            for(auto &child : node.children)
                if(!ReorderTree(child)) return false;
            return ReorderList(node.children);
        }

        static void ApplySystemRemovals(NativeMenuPreviewTree &rows,
            const NativeMenuConstruction::BuildOptions &options) noexcept
        {
            for(std::size_t index = 0; index < rows.size(); ++index)
            {
                auto &node = rows[index];
                if(!node.value.separator && node.value.popup &&
                    !node.children.empty())
                    ApplySystemRemovals(node.children, options);
                if((node.value.separator && options.removeSeparator) ||
                    (node.value.disabled && options.removeDisabled))
                {
                    rows.erase(rows.begin() + static_cast<std::ptrdiff_t>(index--));
                    continue;
                }
            }
            if(!options.removeDuplicate) return;
            for(std::size_t current = 0; current < rows.size(); ++current)
            {
                if(rows[current].value.separator) continue;
                for(std::size_t previous = 0; previous < current; ++previous)
                {
                    if(rows[previous].value.separator ||
                        rows[previous].value.popup != rows[current].value.popup ||
                        !PreviewTextEquals(NormalizePreviewPart(
                            rows[previous].value.title), NormalizePreviewPart(
                            rows[current].value.title)))
                        continue;
                    if(rows[previous].value.disabled &&
                        !rows[current].value.disabled)
                        rows[previous] = std::move(rows[current]);
                    rows.erase(rows.begin() + static_cast<std::ptrdiff_t>(current));
                    --current;
                    break;
                }
            }
        }

        static bool BuildDynamicCandidate(Context &context, const CACHE &cache,
            const NativeMenu *source, std::wstring_view parentPath,
            bool materialize, unsigned depth,
            const NativeMenuConstruction::BuildOptions &options,
            std::vector<PreviewDynamicCandidate> &candidates,
            std::map<std::wstring, std::size_t> &authoredIds,
            const NativeMenuRenderResourceLoader &resourceLoader,
            const NativeMenuRenderDiagnosticSink &diagnosticSink,
            HFONT glyphFont) noexcept
        {
            if(!source || depth > kPreviewMaxDepth)
                return source == nullptr;
            struct Restore
            {
                Context &context;
                Scope *local;
                this_item *current;
                ~Restore() noexcept
                {
                    context.variables.local = local;
                    context._this = current;
                }
            } restore{context, context.variables.local, context._this};
            context.Cache = const_cast<CACHE *>(&cache);
            context.variables.local = const_cast<Scope *>(&source->variables);
            try
            {
                std::vector<std::wstring> explanations;
                explanations.push_back(L"Authored entry evaluated from the current unsaved workspace revision.");
                if(source->properties == 0 && source->is_separator())
                {
                    PreviewDynamicCandidate candidate;
                    candidate.node.value.id = UINT_MAX;
                    candidate.node.value.separator = true;
                    candidate.node.system = false;
                    candidate.node.source = source;
                    candidate.node.path = std::wstring(parentPath);
                    candidate.node.capturedId = AuthoredIdentity(source, options);
                    candidate.node.matchTitle.clear();
                    candidate.node.explanations = std::move(explanations);
                    AddExplanation(candidate.node, L"The authored separator was displayed.");
                    candidate.targetPath = std::wstring(parentPath);
                    candidates.push_back(std::move(candidate));
                    RecordDecision(options, *source, L"displayed",
                        L"The authored separator was displayed.");
                    return candidates.size() <= kPreviewMaxItems;
                }

                if(options.evaluateRules)
                {
                    if(!NativeMenuConstruction::dynamic_types_match(
                        options.selection, *source))
                    {
                        RecordDecision(options, *source, L"hidden",
                            L"Selection-type rules did not match.");
                        return true;
                    }
                    explanations.push_back(L"Selection-type rules matched.");
                    if(!NativeMenuConstruction::dynamic_mode_match(
                        options.selection, context.parse_mode(source->mode,
                            options.inheritedMode)))
                    {
                        RecordDecision(options, *source, L"hidden",
                            L"Selection-count mode did not match.");
                        return true;
                    }
                    explanations.push_back(L"Selection-count mode matched.");
                    if(source->where && !context.eval_bool(source->where))
                    {
                        RecordDecision(options, *source, L"hidden",
                            L"The where condition evaluated false.");
                        return true;
                    }
                    if(source->where)
                        explanations.push_back(L"The where condition evaluated true.");
                    if(source->find)
                    {
                        string pattern;
                        if(context.Eval(source->find, pattern, true) &&
                            !pattern.empty() &&
                            !options.selection.matches_find(pattern))
                        {
                            RecordDecision(options, *source, L"hidden",
                                L"The find rule did not match the supplied selection.");
                            return true;
                        }
                        if(!pattern.empty())
                            explanations.push_back(L"The find rule matched the supplied selection.");
                    }
                }

                const auto visibility = context.parse_visibility(source->visibility);
                if(!NativeMenuConstruction::visible(visibility))
                {
                    RecordDecision(options, *source, L"hidden",
                        L"Visibility evaluation hid the entry.");
                    return true;
                }
                string title;
                if(source->title)
                    context.Eval(source->title, title, false);
                if(!NativeMenuConstruction::title_or_image(*source, title))
                {
                    RecordDecision(options, *source, L"hidden",
                        L"The entry produced neither a title nor an image.");
                    return true;
                }
                explanations.push_back(visibility == Visibility::Disabled ?
                    L"Visibility evaluation displayed the entry disabled." :
                    visibility == Visibility::Label ? L"Visibility evaluation displayed the entry as a label." :
                    visibility == Visibility::Static ? L"Visibility evaluation displayed the entry as static content." :
                    L"Visibility evaluation displayed the entry enabled.");
                explanations.push_back(L"The displayed title was produced by the original evaluation.");

                bool expanded = false;
                if(source->is_menu() && source->expanded)
                    expanded = context.eval_bool(source->expanded);
                if(context.Preview && context.Preview->failed)
                    return false;

                std::wstring target(parentPath);
                if(source->moveto)
                {
                    std::wstring moved;
                    if(EvalString(context, source->moveto, moved))
                    {
                        target = NormalizePreviewPath(moved);
                        explanations.push_back(target == parentPath ?
                            L"The move rule kept the entry in its current menu." :
                            L"The move rule placed the entry in another menu.");
                    }
                }

                if(expanded)
                {
                    for(const auto *child : source->items)
                        if(!BuildDynamicCandidate(context, cache, child, target,
                            materialize, depth + 1, options, candidates,
                            authoredIds, resourceLoader, diagnosticSink,
                            glyphFont))
                            return false;
                    RecordDecision(options, *source, L"expanded",
                        L"The authored menu expanded its children into the current menu.");
                    return true;
                }

                PreviewDynamicCandidate candidate;
                auto &node = candidate.node;
                node.source = source;
                node.system = false;
                node.explanations = std::move(explanations);
                node.value.id = static_cast<uint32_t>(
                    string(title.c_str()).trim().hash());
                if(source->explicit_id)
                {
                    Object explicitId = context.Eval(source->explicit_id).move();
                    if(explicitId.is_number())
                    {
                        const auto id = explicitId.to_number<int64_t>();
                        if(id >= 0 && id <=
                            static_cast<int64_t>((std::numeric_limits<uint32_t>::max)()))
                            node.value.id = static_cast<uint32_t>(id);
                    }
                    else if(explicitId.is_string())
                        node.value.id = explicitId.to_string().trim().hash();
                }
                if(node.value.id == 0)
                    node.value.id = static_cast<uint32_t>(depth + 1);
                node.value.title = ToWide(title);
                node.value.popup = source->is_menu();
                node.value.disabled = NativeMenuConstruction::disabled(visibility);
                node.value.label = NativeMenuConstruction::label(visibility);
                node.value.staticItem = NativeMenuConstruction::static_item(visibility);
                node.matchTitle = node.value.title;
                node.capturedId = AuthoredIdentity(*source, options);
                auto &occurrence = authoredIds[node.capturedId];
                if(occurrence++ > 0)
                    node.capturedId += L"#" + std::to_wstring(occurrence);
                node.path = target;
                node.childrenAvailable = node.value.popup && !source->items.empty();
                candidate.targetPath = target;
                candidate.explicitPosition = false;

                if(source->keys && options.newItemsKeys)
                {
                    string keys;
                    if(context.Eval(source->keys, keys, false))
                        node.value.keys = ToWide(keys);
                }
                if(source->checked && !node.value.popup)
                {
                    Object checked;
                    if(context.Eval(source->checked, checked) && checked.not_default())
                    {
                        const auto state = checked.to_number<int>();
                        node.value.checked = state > 0;
                        node.value.radio = state == 2;
                    }
                }
                if(source->column)
                {
                    Object column;
                    if(context.Eval(source->column, column) && column.not_default())
                        node.value.tab = column.to_number<int>();
                }
                if(source->separator)
                    node.separator = context.parse_separator(source->separator);
                if(source->position)
                {
                    candidate.explicitPosition = true;
                    ParsePreviewPosition(context, source->position,
                        node.position, node.indexOf, node.indexOfPosition,
                        node.indexOfDefault);
                }
                if(options.newItemsImage && source->image.enabled() &&
                    source->image.expr)
                    RenderImage(context, source->image.expr, node.value.image,
                        resourceLoader, diagnosticSink, glyphFont, 16);
                if(options.newItemsImage && source->images.select)
                    RenderImage(context, source->images.select,
                        node.value.selectedImage, resourceLoader, diagnosticSink,
                        glyphFont, 16);
                if(context.Preview && context.Preview->failed)
                    return false;
                candidates.push_back(std::move(candidate));
                RecordDecision(options, *source,
                    visibility == Visibility::Disabled ? L"disabled" : L"displayed",
                    visibility == Visibility::Disabled ?
                        L"The authored entry was displayed disabled." :
                        L"The authored entry passed its rules and was displayed.");
                return candidates.size() <= kPreviewMaxItems;
            }
            catch(...)
            {
                return false;
            }
        }

        static bool InsertDynamicCandidates(Context &context, const CACHE &cache,
            NativeMenuPreviewTree &root,
            const NativeMenuConstruction::BuildOptions &options,
            std::vector<PreviewDynamicCandidate> &candidates,
            const NativeMenuRenderDiagnosticSink &diagnosticSink) noexcept
        {
            (void)context;
            (void)cache;
            (void)options;
            try
            {
                for(auto &candidate : candidates)
                {
                    NativeMenuPreviewTree *destination = nullptr;
                    if(!FindPreviewContainer(root, candidate.targetPath,
                        destination))
                    {
                        if(diagnosticSink && !candidate.targetPath.empty())
                            diagnosticSink(L"Preview menu destination is unavailable: " +
                                candidate.targetPath);
                        continue;
                    }
                    candidate.node.path = candidate.targetPath;
                    destination->push_back(std::move(candidate.node));
                    if(destination->size() > kPreviewMaxItems)
                        return false;
                }
                // Position rules are local to each destination.  Reapplying
                // the stable bucket operation after all candidates have been
                // inserted preserves authored order within each bucket.
                if(!ReorderList(root))
                    return false;
                std::vector<std::wstring> paths;
                std::function<void(const NativeMenuPreviewTree &)> collect =
                    [&](const NativeMenuPreviewTree &rows)
                    {
                        for(const auto &node : rows)
                        {
                            if(node.value.popup)
                                paths.push_back(JoinPreviewPath(node.path,
                                    node.matchTitle.empty() ? node.value.title :
                                        node.matchTitle));
                            if(!node.children.empty()) collect(node.children);
                        }
                    };
                collect(root);
                for(const auto &path : paths)
                {
                    NativeMenuPreviewTree *rows = nullptr;
                    if(FindPreviewContainer(root, path, rows) && rows &&
                        !ReorderList(*rows))
                        return false;
                }
                return true;
            }
            catch(...)
            {
                return false;
            }
        }

        static bool MaterializePreviewPath(Context &context, const CACHE &cache,
            NativeMenuPreviewTree &root,
            const std::vector<std::wstring> &requestedPath,
            const NativeMenuConstruction::BuildOptions &options,
            const NativeMenuRenderResourceLoader &resourceLoader,
            const NativeMenuRenderDiagnosticSink &diagnosticSink,
            HFONT glyphFont) noexcept
        {
            if(requestedPath.empty())
                return true;
            try
            {
                NativeMenuPreviewTree *rows = &root;
                for(const auto &part : requestedPath)
                {
                    auto *selected = FindUniquePreviewNode(*rows, part);
                    if(!selected || !selected->value.popup ||
                        !selected->childrenAvailable)
                        return false;
                    const auto branchPath = JoinPreviewPath(selected->path,
                        selected->matchTitle.empty() ? selected->value.title :
                            selected->matchTitle);
                    if(selected->source && selected->children.empty())
                    {
                        std::vector<PreviewDynamicCandidate> candidates;
                        std::map<std::wstring, std::size_t> authoredIds;
                        for(const auto *child : selected->source->items)
                            if(!BuildDynamicCandidate(context, cache, child,
                                branchPath, true, 0, options, candidates,
                                authoredIds, resourceLoader, diagnosticSink,
                                glyphFont))
                                return false;
                        if(!InsertDynamicCandidates(context, cache, root,
                            options, candidates, diagnosticSink))
                            return false;
                        NativeMenuPreviewTree *branch = nullptr;
                        if(!FindPreviewContainer(root, branchPath, branch) ||
                            !branch)
                            return false;
                        rows = branch;
                    }
                    else
                    {
                        rows = &selected->children;
                    }
                }
                return true;
            }
            catch(...)
            {
                return false;
            }
        }

        static NativeMenuPreviewNode SeparatorNode(
            const NativeMenuPreviewNode *owner = nullptr) noexcept
        {
            NativeMenuPreviewNode separator;
            separator.value.id = UINT_MAX;
            separator.value.separator = true;
            separator.system = owner ? owner->system : true;
            separator.source = owner ? owner->source : nullptr;
            separator.path = owner ? owner->path : std::wstring{};
            return separator;
        }

        static bool ApplyDecorations(NativeMenuPreviewTree &rows) noexcept
        {
            try
            {
                for(auto &node : rows)
                    if(node.value.popup && !node.children.empty() &&
                        !ApplyDecorations(node.children))
                        return false;

                NativeMenuPreviewTree decorated;
                decorated.reserve(rows.size() + rows.size() / 2);
                bool previousSeparator = false;
                for(auto &node : rows)
                {
                    if(NativeMenuConstruction::separator_top(node.separator) &&
                        !previousSeparator)
                    {
                        decorated.push_back(SeparatorNode(&node));
                        previousSeparator = true;
                    }
                    if(node.value.separator)
                    {
                        if(previousSeparator)
                            continue;
                        previousSeparator = true;
                        decorated.push_back(std::move(node));
                        continue;
                    }
                    decorated.push_back(std::move(node));
                    previousSeparator = false;
                    if(NativeMenuConstruction::separator_bottom(
                        decorated.back().separator))
                    {
                        decorated.push_back(SeparatorNode(&decorated.back()));
                        previousSeparator = true;
                    }
                }
                rows = std::move(decorated);
                return rows.size() <= kPreviewMaxItems;
            }
            catch(...)
            {
                return false;
            }
        }

        static bool ValidatePreviewTree(const NativeMenuPreviewTree &rows,
            unsigned depth, std::size_t &count) noexcept
        {
            if(depth > kPreviewMaxDepth || rows.size() > kPreviewMaxItems)
                return false;
            try
            {
                for(const auto &node : rows)
                {
                    if(++count > kPreviewMaxItems || node.removed ||
                        !NativeMenuRenderer::ValidateItem(node.value))
                        return false;
                    if(!node.value.popup &&
                        (node.childrenAvailable || !node.children.empty()))
                        return false;
                    if(node.value.popup && !node.children.empty() &&
                        !node.childrenAvailable)
                        return false;
                    if(!ValidatePreviewTree(node.children, depth + 1, count))
                        return false;
                }
                return true;
            }
            catch(...)
            {
                return false;
            }
        }
        static void SetDiagnostic(std::string *diagnostic,
            const char *message) noexcept
        {
            if(!diagnostic)
                return;
            try
            {
                *diagnostic = message ? message : "preview theme error";
            }
            catch(...)
            {
                diagnostic->clear();
            }
        }

        static ThemeType ThemeTypeFromHash(uint32_t hash,
            ThemeType fallback) noexcept
        {
            if(hash == IDENT_THEME_MODERN) return ThemeType::Modern;
            if(hash == IDENT_THEME_WHITE) return ThemeType::White;
            if(hash == IDENT_THEME_BLACK) return ThemeType::Black;
            if(hash == IDENT_THEME_CLASSIC) return ThemeType::Classic;
            if(hash == IDENT_THEME_SYSTEM) return ThemeType::System;
            if(hash == IDENT_THEME_AUTO) return ThemeType::Auto;
            if(hash == IDENT_THEME_HIGHCONTRAST) return ThemeType::HighContrast;
            if(hash == IDENT_THEME_DARK) return ThemeType::Dark;
            if(hash == IDENT_THEME_LIGHT) return ThemeType::Light;
            return fallback;
        }

        static int8_t EffectFromHash(uint32_t hash, int8_t fallback) noexcept
        {
            if(hash == IDENT_NONE) return 0;
            if(hash == IDENT_EFFECT_TRANSPARENT) return 1;
            if(hash == IDENT_EFFECT_BLUR) return 2;
            if(hash == IDENT_EFFECT_ACRYLIC) return 3;
            if(hash == IDENT_AUTO) return -1;
            return fallback;
        }

        static long Bounded(long value) noexcept
        {
            return std::clamp<long>(value, -4096, 4096);
        }

        static void SetRect(Object *value, ::Nilesoft::Margin &destination) noexcept
        {
            if(!value)
                return;
            if(value->is_array(true) && value->Value.Pointer)
            {
                auto *values = value->get_pointer();
                const auto count = values[0].to_number<int>();
                if(count == 2)
                {
                    if(values[1].not_default())
                    {
                        const auto side = Bounded(values[1].to_number<long>());
                        destination.left = destination.right = side;
                    }
                    if(values[2].not_default())
                    {
                        const auto side = Bounded(values[2].to_number<long>());
                        destination.top = destination.bottom = side;
                    }
                }
                else
                {
                    if(count >= 1 && values[1].not_default())
                        destination.left = Bounded(values[1].to_number<long>());
                    if(count >= 2 && values[2].not_default())
                        destination.top = Bounded(values[2].to_number<long>());
                    if(count >= 3 && values[3].not_default())
                        destination.right = Bounded(values[3].to_number<long>());
                    if(count >= 4 && values[4].not_default())
                        destination.bottom = Bounded(values[4].to_number<long>());
                }
            }
            else if(value->not_default())
            {
                const auto scalar = value->to_number<long>();
                if(scalar >= 0 && scalar <= 400)
                    destination = {scalar, scalar, scalar, scalar};
            }
        }

        static void ApplyMargin(Context &context, const Settings::MARGIN &source,
            ::Nilesoft::Margin &destination, Object &scratch) noexcept
        {
            if(context.Eval(source.value, scratch))
                SetRect(&scratch, destination);
            auto eval = [&](const auto_expr &expressionHolder, long &target)
            {
                Object value;
                Expression *expression = expressionHolder;
                if(context.eval_number(expression, value))
                    target = Bounded(value.to_number<long>());
            };
            eval(source.left, destination.left);
            eval(source.top, destination.top);
            eval(source.right, destination.right);
            eval(source.bottom, destination.bottom);
        }

        static void ApplyState(Context &context, const Settings::COLOR &source,
            Theme::state_t &destination) noexcept
        {
            context.eval_color(source.value, &destination.sel);
            if(source.value)
                destination.nor = destination.nor_dis = destination.sel_dis =
                    destination.sel;
            context.eval_color(source.normal, &destination.nor);
            context.eval_color(source.select, &destination.sel);
            context.eval_color(source.normal_disabled, &destination.nor_dis);
            context.eval_color(source.select_disabled, &destination.sel_dis);
        }

        static void ApplySymbols(Context &context, const Settings::COLOR &source,
            Theme::state_t &destination) noexcept
        {
            ApplyState(context, source, destination);
        }

        static bool ApplyNumber(Context &context, const auto_expr &expression,
            long &destination) noexcept
        {
            Object value;
            if(!context.eval_number(expression, value))
                return false;
            destination = Bounded(value.to_number<long>());
            return true;
        }

        static bool ApplyBoolean(Context &context, const auto_expr &expression,
            bool &destination) noexcept
        {
            Object value;
            if(!context.eval_number(expression, value))
                return false;
            destination = value.to_bool();
            return true;
        }

        template<typename ThemeSettings>
        static void ApplySettings(Context &context, ThemeSettings &source,
            Theme &destination, Object &scratch) noexcept
        {
            if(context.eval_color(source.background.color,
                &destination.background.color))
            {
                // Keep the base factory's alpha until an explicit opacity or
                // effect setting is evaluated below.
            }
            if(context.eval_number(source.background.opacity, scratch))
                destination.background.color.a = Theme::opacity(static_cast<uint8_t>(
                    std::clamp<int>(scratch.to_number<int>(), 0, 100)));
            context.eval_color(source.background.tintcolor,
                &destination.background.tintcolor);
            if(context.Eval(source.background.image, scratch) &&
                !scratch.is_default())
                destination.background.image = scratch.to_string().trim().move();

            // Runtime init_cfg resets the normal item surface to the resolved
            // menu background before applying optional item.back overrides.
            // Preserve that default here so an omitted item.back.normal does
            // not leak the Modern factory fallback into preview rows.
            destination.back.color.nor = destination.background.color;
            ApplyState(context, source.item.text.color, destination.text.color);
            ApplyState(context, source.item.back, destination.back.color);
            ApplyState(context, source.item.border, destination.back.border);
            // `symbol.color` is the shared symbol palette used by the
            // published Catppuccin fixtures.  Specific checkmark, bullet, and
            // chevron blocks below override it just as ContextMenu::init_cfg
            // does after setting the common palette.
            ApplySymbols(context, source.symbol.color,
                destination.symbols.chevron);
            destination.symbols.checked = destination.symbols.chevron;
            destination.symbols.bullet = destination.symbols.chevron;
            ApplySymbols(context, source.symbol.chevron, destination.symbols.chevron);
            ApplySymbols(context, source.symbol.bullet, destination.symbols.bullet);
            ApplySymbols(context, source.symbol.checkmark, destination.symbols.checked);

            if(context.eval_number(source.shadow.size, scratch))
                destination.shadow.size = static_cast<uint8_t>(std::clamp<int>(
                    scratch.to_number<int>(), 0, 30));
            if(destination.shadow.size > 0)
            {
                if(context.eval_number(source.shadow.enabled, scratch) &&
                    !scratch.to_bool())
                    destination.shadow.size = 0;
                if(destination.shadow.size > 0)
                {
                    context.eval_color(source.shadow.color,
                        &destination.shadow.color);
                    if(context.eval_number(source.shadow.opacity, scratch))
                        destination.shadow.color.a = Theme::opacity(static_cast<uint8_t>(
                            std::clamp<int>(scratch.to_number<int>(), 0, 100)));
                    if(destination.shadow.color.a == 0)
                        destination.shadow.size = 0;
                    else if(context.eval_number(source.shadow.offset, scratch))
                        destination.shadow.offset = static_cast<uint8_t>(std::clamp<int>(
                            scratch.to_number<int>(), 0, 30));
                }
            }
            destination.shadow.enabled = destination.shadow.size > 0 &&
                destination.shadow.color.a != 0;

            if(context.eval_number(source.item.opacity, scratch))
                destination.back.opacity = Theme::opacity(static_cast<uint8_t>(
                    std::clamp<int>(scratch.to_number<int>(), 0, 100)));
            if(context.eval_number(source.item.radius, scratch))
                destination.back.radius = Theme::radius(static_cast<uint8_t>(
                    std::clamp<int>(scratch.to_number<int>(), 0, 4)));
            if(context.eval_number(source.item.prefix, scratch))
            {
                const auto prefix = scratch.to_number<int>();
                destination.text.prefix = prefix == 1 ? 0 :
                    prefix == 2 ? DT_NOPREFIX : DT_HIDEPREFIX;
            }
            ApplyMargin(context, source.item.padding, destination.back.padding,
                scratch);
            ApplyMargin(context, source.item.margin, destination.back.margin,
                scratch);

            if(context.eval_number(source.border.size, scratch))
                destination.border.size = static_cast<uint8_t>(std::clamp<int>(
                    scratch.to_number<int>(), 0, 10));
            if(context.eval_number(source.border.enabled, scratch) &&
                !scratch.to_bool())
                destination.border.size = 0;
            context.eval_color(source.border.color, &destination.border.color);
            if(context.eval_number(source.border.opacity, scratch))
                destination.border.color.a = Theme::opacity(static_cast<uint8_t>(
                    std::clamp<int>(scratch.to_number<int>(), 0, 100)));
            if(context.eval_number(source.border.radius, scratch))
                destination.border.radius = Theme::radius(static_cast<uint8_t>(
                    std::clamp<int>(scratch.to_number<int>(), 0, 4)));
            ApplyMargin(context, source.border.padding, destination.border.padding,
                scratch);

            if(context.eval_number(source.separator.size, scratch))
                destination.separator.size = static_cast<uint8_t>(std::clamp<int>(
                    scratch.to_number<int>(), 0, 40));
            context.eval_color(source.separator.color, &destination.separator.color);
            if(context.eval_number(source.separator.opacity, scratch))
                destination.separator.color.a = Theme::opacity(static_cast<uint8_t>(
                    std::clamp<int>(scratch.to_number<int>(), 0, 100)));
            ApplyMargin(context, source.separator.margin, destination.separator.margin,
                scratch);

            if(context.eval_number(source.image.enabled, scratch))
                destination.image.enabled = scratch.to_bool();
            if(context.Eval(source.image.color, scratch))
            {
                if(scratch.is_array(true) && scratch.Value.Pointer)
                {
                    auto *values = scratch.get_pointer();
                    const auto count = std::clamp<int>(
                        values[0].to_number<int>(), 0, 3);
                    for(int index = 0; index < count; ++index)
                    {
						Drawing::Color color;
                        if(context.to_color(values[index + 1], &color))
                            destination.image.color[index] = color;
                    }
                }
                else
                {
					Drawing::Color color;
                    if(context.to_color(scratch, &color))
                        destination.image.color[0] = color;
                }
            }
            if(context.eval_number(source.image.size, scratch))
                destination.image.size = static_cast<uint32_t>(std::clamp<long>(
                    scratch.to_number<long>(), 0, 256));
            if(context.eval_number(source.image.gap, scratch))
                destination.image.gap = static_cast<uint8_t>(std::clamp<int>(
                    scratch.to_number<int>(), 0, 128));
            if(context.eval_number(source.image.scale, scratch))
                destination.image.scale = scratch.to_bool();
            if(context.eval_number(source.image.display, scratch))
                destination.image.display = std::clamp<int>(
                    scratch.to_number<int>(), 0, 2);

            // Resolve the same percentage based gradient representation used by
            // ContextMenu::init_cfg.  The renderer consumes the resolved values;
            // it must not evaluate expressions while painting.
            destination.gradient = {};
            if(context.eval_number(source.gradient.enabled, scratch) &&
                scratch.to_bool())
            {
                if(context.Eval(source.gradient.linear, scratch) &&
                    scratch.is_array() && scratch.Value.Pointer)
                {
                    auto *values = scratch.get_pointer();
                    const auto count = values[0].to_number<int>();
                    for(int index = 0; index < 4 && index < count; ++index)
                        destination.gradient.linear[index + 1] =
                            values[index + 1].to_number<double>();
                    if(destination.gradient.linear[1] > 0.0 ||
                        destination.gradient.linear[2] > 0.0 ||
                        destination.gradient.linear[3] > 0.0 ||
                        destination.gradient.linear[4] > 0.0)
                    {
                        destination.gradient.linear[0] = 1.0;
                        destination.gradient.enabled = true;
                    }
                }
                if(!destination.gradient.enabled &&
                    context.Eval(source.gradient.radial, scratch))
                {
                    if(scratch.is_array() && scratch.Value.Pointer)
                    {
                        auto *values = scratch.get_pointer();
                        const auto count = values[0].to_number<int>();
                        destination.gradient.radial[1] = count >= 1 ?
                            values[1].to_number<double>() : 100.0;
                        destination.gradient.radial[2] = count >= 2 ?
                            values[2].to_number<double>() : 100.0;
                        destination.gradient.radial[3] = count >= 3 ?
                            values[3].to_number<double>() : 50.0;
                        destination.gradient.radial[4] = count >= 4 ?
                            values[4].to_number<double>() :
                            destination.gradient.radial[1];
                        destination.gradient.radial[5] = count >= 5 ?
                            values[5].to_number<double>() :
                            destination.gradient.radial[2];
                    }
                    else if(scratch.not_default())
                    {
                        destination.gradient.radial[1] = 100.0;
                        destination.gradient.radial[2] = 100.0;
                        destination.gradient.radial[3] = scratch.to_number<double>();
                        destination.gradient.radial[4] = 100.0;
                        destination.gradient.radial[5] = 100.0;
                    }
                    if(destination.gradient.radial[1] > 0.0 ||
                        destination.gradient.radial[2] > 0.0 ||
                        destination.gradient.radial[3] > 0.0 ||
                        destination.gradient.radial[4] > 0.0 ||
                        destination.gradient.radial[5] > 0.0)
                    {
                        destination.gradient.radial[0] = 1.0;
                        destination.gradient.enabled = true;
                    }
                }
                if(destination.gradient.enabled &&
                    context.Eval(source.gradient.stop, scratch) &&
                    scratch.is_array() && scratch.Value.Pointer)
                {
                    auto *values = scratch.get_pointer();
                    const auto count = std::clamp<int>(values[0].to_number<int>(),
                        0, 64);
                    for(int index = 1; index <= count; ++index)
                    {
                        auto *stop = &values[index];
                        if(!stop->is_array() || !stop->Value.Pointer)
                            continue;
                        auto *parts = stop->get_pointer();
                        const auto partCount = parts[0].to_number<int>();
                        if(partCount < 1 || partCount > 3)
                            continue;
                        Theme::gradientstop_t resolved{};
                        resolved.offset = std::clamp(
                            parts[1].to_number<double>(), 0.0, 1.0);
                        resolved.color = destination.background.color;
                        if(partCount >= 2)
                        {
                            Drawing::Color color;
                            if(!context.to_color(parts[2], &color))
                                continue;
                            resolved.color = color;
                        }
                        if(partCount == 3 && parts[3].not_default())
                            resolved.color.opacity(static_cast<uint8_t>(
                                std::clamp<int>(parts[3].to_number<int>(), 0, 100)));
                        destination.gradient.stpos.push_back(resolved);
                    }
                    destination.gradient.enabled = !destination.gradient.stpos.empty();
                }
            }

            if(context.Eval(source.layout.width, scratch))
            {
                if(scratch.not_default() && !scratch.is_array(true))
                    destination.layout.min_width = static_cast<uint32_t>(
                        std::clamp<long>(scratch.to_number<long>(), 0, 2048));
                else if(scratch.is_array(true) && scratch.Value.Pointer)
                {
                    auto *values = scratch.get_pointer();
                    const auto count = values[0].to_number<int>();
                    if(count >= 1 && values[1].not_default())
                        destination.layout.min_width = static_cast<uint32_t>(
                            std::clamp<long>(values[1].to_number<long>(), 0, 2048));
                    if(count >= 2 && values[2].not_default())
                        destination.layout.max_width = static_cast<uint32_t>(
                            std::clamp<long>(values[2].to_number<long>(), 0, 2048));
                }
            }
            if(context.eval_number(source.layout.rtl, scratch))
                destination.layout.rtl = scratch.to_bool();

            if(context.eval_number(source.font.size, scratch))
                destination.font.lfHeight = static_cast<LONG>(std::clamp<long>(
                    scratch.to_number<long>(), -200, 200));
            if(context.eval_number(source.font.weight, scratch))
                destination.font.lfWeight = static_cast<LONG>(std::clamp<long>(
                    scratch.to_number<long>() * 100, 100, 900));
            if(context.eval_number(source.font.italic, scratch))
                destination.font.lfItalic = scratch.to_bool() ? TRUE : FALSE;
            if(context.Eval(source.font.name, scratch) && scratch.is_string())
            {
                const auto name = scratch.to_string();
                if(!name.empty())
                    ::StringCchCopyW(destination.font.lfFaceName,
                        _countof(destination.font.lfFaceName), name.c_str());
            }
        }

        static std::wstring ToWide(const string &value)
        {
            return value.empty() ? std::wstring() :
                std::wstring(value.c_str(), value.length());
        }

        static void DrawValue(const MenuItemInfo::IMAGE::draw_t &source,
            NativeMenuRenderImage &destination) noexcept
        {
            switch(source.type)
            {
                case MenuItemInfo::IMAGE::draw_t::DT_SHAPE:
                    destination.kind = NativeMenuImageKind::shape;
                    destination.shape.solid = source.shape.solid;
                    destination.shape.radius = source.shape.radius;
                    destination.shape.stroke = source.shape.stroke < 0 ? 0 :
                        static_cast<uint8_t>((std::min)(source.shape.stroke, 255L));
                    destination.shape.size = source.shape.size;
                    destination.shape.color[0] = Color(source.shape.color[0]);
                    destination.shape.color[1] = Color(source.shape.color[1]);
                    break;
                case MenuItemInfo::IMAGE::draw_t::DT_GLYPH:
                    destination.kind = NativeMenuImageKind::glyph;
                    destination.glyph.code[0] = source.glyph.code[0];
                    destination.glyph.code[1] = source.glyph.code[1];
                    destination.glyph.font = source.glyph.font;
                    destination.glyph.size = source.glyph.size;
                    destination.glyph.color[0] = Color(source.glyph.color[0]);
                    destination.glyph.color[1] = Color(source.glyph.color[1]);
                    break;
                case MenuItemInfo::IMAGE::draw_t::DT_NONE:
                default:
                    destination = {};
                    break;
            }
        }
    };
}
