#include "../../dll/src/pch.h"
#include "../../dll/src/Parser/Parser.h"
#include "PreviewService.h"
#include "PreviewRendering.h"
#include "PreviewMenuInput.h"
#include "PreviewSourceIdentities.h"
#include "PreviewResources.h"
#include "../../dll/src/Include/NativeMenuRendererAdapter.h"

namespace Nilesoft::Shell::StudioPreview
{
    namespace
    {
        struct CompositionState
        {
            NativeMenuComposedWindow window;
            NativeMenuRenderSurface surface;
        };
        thread_local CompositionState composition;

        long Integer(const Json::Value& value, std::string_view name, long fallback, long minimum, long maximum)
        {
            const double number = NumberMember(value, name, fallback);
            if(number < minimum || number > maximum || number != std::floor(number))
                throw std::invalid_argument("A preview layout value is out of bounds.");
            return static_cast<long>(number);
        }

        const Json::Value* InputRows(Session& session)
        {
            const Json::Value* rows = nullptr;
            if(Json::StringMember(session.request, "mode") == "captured")
            {
                const auto capture = session.request.Find("capture");
                if(capture) rows = capture->Find("original");
            }
            else rows = session.request.Find("sampleMenu");
            return rows;
        }

        std::string ExplanationsJson(const NativeMenuPreviewNode& node)
        {
            std::string result = "[";
            for(std::size_t index = 0; index < node.explanations.size(); ++index)
            {
                if(index) result += ',';
                result += Json::Quote(Utf8(node.explanations[index]));
            }
            return result + ']';
        }

        std::string PreviewNodeJson(const NativeMenuPreviewNode& node,
            const PreviewSourceIdentities& identities)
        {
            const auto& item = node.value;
            std::string result = "{\"id\":" + Json::Quote(Utf8(node.capturedId)) +
                ",\"title\":" + Json::Quote(Utf8(item.title)) +
                ",\"kind\":" + Json::Quote(item.separator ? "separator" : item.popup ? "menu" : "item") +
                ",\"origin\":" + Json::Quote(node.system ? "system" : "authored") +
                ",\"stableId\":" + Json::Quote(Utf8(node.capturedId)) +
                ",\"matchTitle\":" + Json::Quote(Utf8(node.matchTitle)) +
                ",\"disabled\":" + (item.disabled ? "true" : "false") +
                ",\"checked\":" + (item.checked ? "true" : "false") +
                ",\"radio\":" + (item.radio ? "true" : "false") +
                ",\"isDefault\":" + (item.isDefault ? "true" : "false") +
                ",\"ownerDraw\":" + ((node.ownerDraw || item.ownerDraw) ? "true" : "false") +
                ",\"keys\":" + Json::Quote(Utf8(item.keys)) +
                ",\"trace\":" + ExplanationsJson(node) +
                ",\"childrenCaptured\":" + ((!item.popup || node.childrenAvailable) ? "true" : "false");
            if(node.source)
            {
                const auto file = std::wstring(node.source->source_file.c_str());
                const auto nativeId = std::wstring(node.source->source_node_id.c_str());
                const auto start = nativeId.size() > 1 && nativeId.front() == L'n' ? std::stoi(nativeId.substr(1)) : 0;
                const auto stableId = identities.Resolve(file, start);
                result += ",\"sourceFile\":" + Json::Quote(Utf8(file)) +
                    ",\"sourceStart\":" + std::to_string(start) +
                    ",\"sourceNodeId\":" + Json::Quote(Utf8(stableId.empty() ? nativeId : stableId));
            }
            result += ",\"children\":[";
            for(std::size_t index = 0; index < node.children.size(); ++index)
            {
                if(index) result += ',';
                result += PreviewNodeJson(node.children[index], identities);
            }
            return result + "]}";
        }

        std::string PreviewTreeJson(const NativeMenuPreviewTree& tree,
            const PreviewSourceIdentities& identities)
        {
            std::string result = "[";
            for(std::size_t index = 0; index < tree.size(); ++index)
            {
                if(index) result += ',';
                result += PreviewNodeJson(tree[index], identities);
            }
            return result + ']';
        }
    }

    std::string Render(Session& session, bool composed)
    {
        composition.window.destroy(); composition.surface.reset();
        if(!session.Load()) return "{\"version\":1,\"available\":false,\"diagnostics\":" + session.Diagnostics() + '}';
        auto& cache = *session.parser->context.Cache;
        if(!session.selection.available && !cache.dynamic.items.empty())
            return FailureJson("PREVIEW_SELECTION", "Authored menu construction requires a supplied selection snapshot.");
        PreviewResources resources(session.request, session.dpi.val,
            [&](std::string_view code, std::string_view message) { session.diagnostics.push_back(DiagnosticJson(code, message)); });
        NativeMenuRenderTheme theme;
        std::string diagnostic;
        if(!NativeMenuRendererAdapter::ConfigureTheme(session.parser->context, session.dpi,
            session.theme, theme, composed, nullptr, nullptr, nullptr, nullptr, {}, &diagnostic,
            [&](const std::wstring& path, NativeMenuRenderImage& image) { return resources.LoadImage(path, image); }) || session.policy.failed)
        {
            session.RecordFailure();
            if(!diagnostic.empty()) session.diagnostics.push_back(DiagnosticJson("PREVIEW_THEME", diagnostic));
            return "{\"version\":1,\"available\":false,\"diagnostics\":" + session.Diagnostics() + '}';
        }
        NativeMenuRenderOwnedFonts fonts;
        if(!fonts.create(theme.font, theme.dpi)) return FailureJson("PREVIEW_FONT", "The configured native font could not be created.");
        if(theme.font.lfFaceName[0])
        {
            wchar_t actualFace[LF_FACESIZE]{};
            if(const auto dc = ::CreateCompatibleDC(nullptr))
            {
                const auto previous = ::SelectObject(dc, fonts.text());
                if(previous && previous != HGDI_ERROR)
                {
                    ::GetTextFaceW(dc, LF_FACESIZE, actualFace);
                    ::SelectObject(dc, previous);
                }
                ::DeleteDC(dc);
            }
            if(actualFace[0] && ::CompareStringOrdinal(theme.font.lfFaceName, -1, actualFace, -1, TRUE) != CSTR_EQUAL)
                session.diagnostics.push_back(DiagnosticJson("PREVIEW_FONT_SUBSTITUTION",
                    "The requested font is unavailable; Windows selected " + Utf8(actualFace) + '.'));
        }
        theme.textFont = fonts.text(); theme.shortcutFont = fonts.text(); theme.glyphFont = fonts.glyph();
        const auto inputRows = InputRows(session);
        if(!inputRows)
            return FailureJson("PREVIEW_MENU_INPUT", "The selected original or sample menu is unavailable.");
        auto base = ReadPreviewMenuInput(*inputRows, session.diagnostics,
            [&](const Json::Value& image, NativeMenuRenderImage& output) { return resources.DecodeCapturedImage(image, output); });
        NativeMenuConstruction::BuildOptions options;
        options.selection = {session.selection.available ? &session.selection.value : nullptr, true};
        options.evaluateRules = true;
        if(const auto path = session.request.Find("submenuPath"))
        {
            if(path->kind != Json::Kind::Array || path->array.size() > 32)
                return FailureJson("PREVIEW_MENU_PATH", "The requested submenu path is invalid.");
            for(const auto& part : path->array)
            {
                if(part.kind != Json::Kind::String || part.text.empty() || part.text.size() > 4096)
                    return FailureJson("PREVIEW_MENU_PATH", "The requested submenu identity is invalid.");
                options.requestedPath.push_back(Wide(part.text));
            }
        }
        PreviewSourceIdentities identities(session.request);
        options.sourceIdentity = [&](std::wstring_view file, std::size_t start)
        { return start <= static_cast<std::size_t>((std::numeric_limits<int>::max)()) ? identities.Resolve(file, static_cast<int>(start)) : std::wstring{}; };
        std::vector<std::string> decisions;
        options.decisionSink = [&](const NativeMenu& source,
            std::wstring_view state, std::wstring_view reason)
        {
            if(decisions.size() >= 1024) return;
            const auto file = std::wstring(source.source_file.c_str());
            const auto nativeId = std::wstring(source.source_node_id.c_str());
            int start = 0;
            if(nativeId.size() > 1 && nativeId.front() == L'n')
            {
                try { start = std::stoi(nativeId.substr(1)); }
                catch(...) { start = 0; }
            }
            const auto stableId = identities.Resolve(file, start);
            decisions.push_back("{\"file\":" + Json::Quote(Utf8(file)) +
                ",\"start\":" + std::to_string(start) +
                ",\"nodeId\":" + Json::Quote(Utf8(stableId.empty() ? nativeId : stableId)) +
                ",\"state\":" + Json::Quote(Utf8(state)) +
                ",\"reason\":" + Json::Quote(Utf8(reason)) + '}');
        };
        NativeMenuPreviewTree tree;
        const auto report = [&](const std::wstring& message)
        { session.diagnostics.push_back(DiagnosticJson("PREVIEW_CONSTRUCTION", Utf8(message))); };
        if(!NativeMenuRendererAdapter::BuildPreview(session.parser->context, cache, base, options, tree,
            [&](const std::wstring& path, NativeMenuRenderImage& image) { return resources.LoadImage(path, image); }, report, fonts.glyph()) || session.policy.failed)
        {
            session.RecordFailure();
            if(session.diagnostics.empty()) report(L"The native menu could not be constructed from this context.");
            return "{\"version\":1,\"available\":false,\"diagnostics\":" + session.Diagnostics() + '}';
        }
        const NativeMenuPreviewTree* rows = nullptr;
        NativeMenuRenderMenu menu;
        std::vector<NativeMenuRenderItem> items;
        if(!NativeMenuRendererAdapter::SelectPreviewPath(tree, options.requestedPath, rows) || !rows ||
            !NativeMenuRendererAdapter::FlattenPreviewTree(*rows, menu, items))
            return FailureJson("PREVIEW_MENU_PATH", "The requested submenu is unavailable in this revision.");
        menu.rtl = theme.rtl;
        std::string metadata = "[";
        for(std::size_t index = 0; index < rows->size(); ++index)
        {
            const auto& node = (*rows)[index];
            const auto& item = items[index];
            if(metadata.size() > 1) metadata += ',';
            metadata += "{\"id\":" + Json::Quote(Utf8(node.capturedId)) +
                ",\"title\":" + Json::Quote(Utf8(item.title)) + ",\"disabled\":" + (item.disabled ? "true" : "false") +
                ",\"checked\":" + (item.checked ? "true" : "false") + ",\"popup\":" + (item.popup ? "true" : "false") +
                ",\"radio\":" + (item.radio ? "true" : "false") + ",\"isDefault\":" + (item.isDefault ? "true" : "false") +
                ",\"keys\":" + Json::Quote(Utf8(item.keys)) +
                ",\"explanations\":" + ExplanationsJson(node) +
                ",\"childrenAvailable\":" + (node.childrenAvailable ? "true" : "false");
            if(node.source)
            {
                const auto file = std::wstring(node.source->source_file.c_str());
                const auto nativeId = std::wstring(node.source->source_node_id.c_str());
                const auto start = nativeId.size() > 1 && nativeId.front() == L'n' ? std::stoi(nativeId.substr(1)) : 0;
                const auto stableId = identities.Resolve(file, start);
                metadata += ",\"sourceFile\":" + Json::Quote(Utf8(file)) + ",\"sourceStart\":" + std::to_string(start) +
                    ",\"sourceNodeId\":" + Json::Quote(Utf8(stableId.empty() ? nativeId : stableId));
            }
            metadata += '}';
        }
        metadata += ']';
        const auto expectationTree = PreviewTreeJson(tree, identities);
        RenderedPreview frame;
        if(!frame.Render(theme, menu, items,
            Integer(session.request, "viewportHeight", 720, 32, 4096),
            Integer(session.request, "scrollOffset", 0, 0, 1000000),
            Integer(session.request, "selectedIndex", -1, -1, 4096)))
            return FailureJson("PREVIEW_RENDER", frame.error);
        if(composed)
        {
            const auto context = session.request.Find("context");
            const POINT origin{context ? Integer(*context, "originX", 120, -1000000, 1000000) : 120,
                context ? Integer(*context, "originY", 120, -1000000, 1000000) : 120};
            const auto backdrop = session.theme.background.effect == 2 ? NativeMenuBackdrop::acrylic :
                session.theme.background.effect > 0 ? NativeMenuBackdrop::blur : NativeMenuBackdrop::none;
            if(!composition.window.create(nullptr, origin, frame.surface.size(), backdrop) ||
                !composition.window.update(frame.surface, origin))
                return FailureJson("PREVIEW_COMPOSITION", "The native composition window could not be created.");
            composition.window.show();
        }
        std::string decisionJson = "[";
        for(std::size_t index = 0; index < decisions.size(); ++index)
        { if(index) decisionJson += ','; decisionJson += decisions[index]; }
        decisionJson += ']';
        std::string result = "{\"version\":1,\"available\":true,\"frame\":" + frame.Json() + ",\"items\":" + metadata +
            ",\"expectationTree\":" + expectationTree +
            ",\"composed\":" + (composed ? "true" : "false") + ",\"dependencies\":" + session.Dependencies() +
            ",\"resourceDependencies\":" + resources.Dependencies() + ",\"trace\":" + session.Trace() +
            ",\"decisions\":" + decisionJson + ",\"diagnostics\":" + session.Diagnostics() + '}';
        if(composed) composition.surface = std::move(frame.surface);
        return result;
    }
}
