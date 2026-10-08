#pragma once

#include "PreviewService.h"
#include "../../dll/src/Include/NativeMenuRendererAdapter.h"
#include <charconv>
#include <functional>
#include <set>

namespace Nilesoft::Shell::StudioPreview
{
    using CapturedImageDecoder = std::function<bool(const Json::Value&, NativeMenuRenderImage&)>;

    inline bool InputBoolean(const Json::Value& value, std::string_view name, bool fallback = false)
    {
        const auto field = value.Find(name);
        if(!field) return fallback;
        if(field->kind != Json::Kind::Boolean) throw std::invalid_argument("A menu state is not a boolean.");
        return field->boolean;
    }

    inline std::wstring InputText(const Json::Value& value, std::string_view name)
    {
        const auto field = value.Find(name);
        if(!field || field->kind == Json::Kind::Null) return {};
        if(field->kind != Json::Kind::String || field->text.size() > 65536 || field->text.find('\0') != std::string::npos)
            throw std::invalid_argument("A menu label or identity is invalid.");
        auto text = Wide(field->text);
        if(text.size() > 16384) throw std::invalid_argument("A menu label or identity exceeds its limit.");
        return text;
    }

    inline uint32_t InputIdentity(const Json::Value& value, const std::wstring& normalizedTitle)
    {
        const auto stable = Json::StringMember(value, "stableId");
        const auto separator = stable.find(':');
        if(separator != std::string::npos &&
            (stable.substr(0, separator) == "shell.muid" || stable.substr(0, separator) == "shell.title"))
        {
            const auto text = std::string_view(stable).substr(separator + 1);
            uint32_t result{};
            const auto parsed = std::from_chars(text.data(), text.data() + text.size(), result, 16);
            if(!text.empty() && text.size() <= 8 && parsed.ec == std::errc{} && parsed.ptr == text.data() + text.size()) return result;
            throw std::invalid_argument("A captured menu identity is invalid.");
        }
        return string(normalizedTitle.c_str()).hash();
    }

    inline std::vector<NativeMenuPreviewNode> ReadPreviewMenuInput(const Json::Value& rows,
        std::vector<std::string>& diagnostics, const CapturedImageDecoder& decodeImage = {},
        unsigned depth = 0, std::size_t* total = nullptr, std::wstring prefix = L"sample")
    {
        std::size_t rootTotal{};
        if(!total) total = &rootTotal;
        if(depth > 32 || rows.kind != Json::Kind::Array || rows.array.size() > 4096)
            throw std::invalid_argument("The supplied menu tree exceeds its structural limit.");
        std::vector<NativeMenuPreviewNode> nodes;
        std::set<std::wstring> identities;
        nodes.reserve(rows.array.size());
        for(const auto& input : rows.array)
        {
            if(input.kind != Json::Kind::Object || ++*total > 4096)
                throw std::invalid_argument("The supplied menu tree exceeds its item limit.");
            NativeMenuPreviewNode node;
            node.capturedId = InputText(input, "id");
            if(node.capturedId.empty()) node.capturedId = prefix + L"-" + std::to_wstring(nodes.size());
            if(node.capturedId.size() > 1024 || !identities.insert(node.capturedId).second)
                throw std::invalid_argument("A supplied menu identity is ambiguous or exceeds its limit.");
            node.value.title = InputText(input, "title");
            node.matchTitle = InputText(input, "matchTitle");
            if(node.matchTitle.empty()) node.matchTitle = node.value.title;
            node.value.id = InputIdentity(input, node.matchTitle);
            const auto kind = Json::StringMember(input, "kind");
            node.value.separator = kind == "separator" || InputBoolean(input, "separator");
            node.value.popup = kind == "menu" || InputBoolean(input, "popup");
            node.value.checked = InputBoolean(input, "checked");
            node.value.radio = InputBoolean(input, "radio");
            node.value.isDefault = InputBoolean(input, "isDefault");
            node.value.disabled = InputBoolean(input, "disabled");
            node.value.keys = InputText(input, "keys");
            node.ownerDraw = node.value.ownerDraw = InputBoolean(input, "ownerDraw");
            node.explanations.push_back(L"Captured system entry supplied by the original menu snapshot.");
            node.childrenAvailable = InputBoolean(input, "childrenCaptured", InputBoolean(input, "childrenAvailable", !node.value.popup));
            if(node.value.separator) node.value.id = UINT_MAX;
            if(node.ownerDraw)
            {
                node.value.disabled = true;
                if(node.value.title.empty()) node.value.title = L"[Owner-drawn item]";
                diagnostics.push_back(DiagnosticJson("PREVIEW_OWNER_DRAW", "Third-party owner-drawn content is unavailable; no callback was invoked."));
            }
            if(const auto image = input.Find("image"); image && image->kind != Json::Kind::Null)
                if(!decodeImage || !decodeImage(*image, node.value.image))
                    diagnostics.push_back(DiagnosticJson("PREVIEW_CAPTURE_IMAGE", "A captured menu image is unavailable or invalid."));
            if(const auto children = input.Find("children"))
            {
                if(node.childrenAvailable)
                    node.children = ReadPreviewMenuInput(*children, diagnostics, decodeImage, depth + 1, total, node.capturedId);
            }
            nodes.push_back(std::move(node));
        }
        return nodes;
    }
}
