#pragma once

#include "PreviewJson.h"
#include "../../dll/src/Include/NativeMenuRenderer.h"
#include <functional>
#include <memory>
#include <string>
#include <string_view>

namespace Nilesoft::Shell::StudioPreview
{
    // Owns request-local decoded assets. Paths are lookup keys only: this class
    // never opens a file, loads a module, or asks a shell provider for an icon.
    class PreviewResources final
    {
        struct State;
        std::unique_ptr<State> state;
    public:
        using DiagnosticSink = std::function<void(std::string_view, std::string_view)>;
        PreviewResources(const ::ShellStudio::PreviewJson::Value& request, uint32_t dpi,
            DiagnosticSink diagnostics);
        ~PreviewResources();
        PreviewResources(const PreviewResources&) = delete;
        PreviewResources& operator=(const PreviewResources&) = delete;
        bool LoadImage(const std::wstring& path, NativeMenuRenderImage& image) const;
        bool DecodeCapturedImage(const ::ShellStudio::PreviewJson::Value& value, NativeMenuRenderImage& image);
        std::string Dependencies() const;
    };
}
