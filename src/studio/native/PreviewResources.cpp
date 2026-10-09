#include "PreviewResources.h"
#include <wincodec.h>
#include <dwrite_3.h>
#include <wrl/client.h>
#include <bcrypt.h>
#include <filesystem>
#include <map>
#include <set>
#include <stdexcept>
#include <cmath>
#include <algorithm>
#include <cstring>
#include <cwctype>
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "dwrite.lib")

namespace Nilesoft::Shell::StudioPreview
{
    namespace
    {
        namespace Json = ::ShellStudio::PreviewJson;
        using Microsoft::WRL::ComPtr;
        constexpr std::size_t MaxResourceBytes = 8u * 1024u * 1024u;
        constexpr std::size_t MaxTotalBytes = 12u * 1024u * 1024u;
        constexpr std::size_t MaxPixelBytes = 64u * 1024u * 1024u;

        std::wstring ResourcePath(std::wstring path)
        {
            path = std::filesystem::path(path).lexically_normal().wstring();
            for(auto& character : path)
            { if(character == L'/') character = L'\\'; character = std::towlower(character); }
            return path;
        }

        std::wstring ResourceWide(std::string_view value)
        {
            if(value.empty() || value.size() > 32768 || value.find('\0') != std::string_view::npos)
                throw std::invalid_argument("The resource path is invalid.");
            const auto size = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
            if(size <= 0) throw std::invalid_argument("The resource path is not UTF-8.");
            std::wstring result(size, L'\0');
            ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size);
            return result;
        }

        std::size_t ResourceNumber(const Json::Value& value, std::string_view key, std::size_t maximum)
        {
            const auto field = value.Find(key);
            if(!field || field->kind != Json::Kind::Number) throw std::invalid_argument("Resource size metadata is missing.");
            const double number = std::stod(field->text);
            if(!std::isfinite(number) || number < 0 || number > static_cast<double>(maximum) || std::floor(number) != number)
                throw std::invalid_argument("Resource size metadata is invalid.");
            return static_cast<std::size_t>(number);
        }

        std::vector<uint8_t> DecodeBase64(std::string_view text, std::size_t maximum)
        {
            if(text.empty() || text.size() % 4 || text.size() > ((maximum + 2) / 3) * 4)
                throw std::invalid_argument("Resource encoding exceeds its limit or is invalid.");
            auto digit = [](char c) -> int
            {
                if(c >= 'A' && c <= 'Z') return c - 'A';
                if(c >= 'a' && c <= 'z') return c - 'a' + 26;
                if(c >= '0' && c <= '9') return c - '0' + 52;
                return c == '+' ? 62 : c == '/' ? 63 : -1;
            };
            std::vector<uint8_t> result;
            result.reserve(text.size() / 4 * 3);
            for(std::size_t offset = 0; offset < text.size(); offset += 4)
            {
                const int a = digit(text[offset]), b = digit(text[offset + 1]);
                const bool pad2 = text[offset + 2] == '=', pad3 = text[offset + 3] == '=';
                const int c = pad2 ? 0 : digit(text[offset + 2]), d = pad3 ? 0 : digit(text[offset + 3]);
                if(a < 0 || b < 0 || c < 0 || d < 0 || (pad2 && !pad3) ||
                    ((pad2 || pad3) && offset + 4 != text.size()) || (pad2 && (b & 15)) || (pad3 && !pad2 && (c & 3)))
                    throw std::invalid_argument("Resource base64 is not canonical.");
                const uint32_t value = static_cast<uint32_t>(a << 18 | b << 12 | c << 6 | d);
                result.push_back(static_cast<uint8_t>(value >> 16));
                if(!pad2) result.push_back(static_cast<uint8_t>(value >> 8));
                if(!pad3) result.push_back(static_cast<uint8_t>(value));
            }
            if(result.size() > maximum) throw std::invalid_argument("The decoded resource exceeds its limit.");
            return result;
        }

        std::string ResourceHash(const std::vector<uint8_t>& bytes)
        {
            UCHAR digest[32]{};
            if(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(bytes.data()),
                static_cast<ULONG>(bytes.size()), digest, sizeof(digest)) < 0)
                throw std::runtime_error("The resource fingerprint could not be computed.");
            constexpr char hex[] = "0123456789ABCDEF";
            std::string result;
            for(auto byte : digest) { result += hex[byte >> 4]; result += hex[byte & 15]; }
            return result;
        }

        uint32_t Big32(const uint8_t* bytes)
        { return uint32_t(bytes[0]) << 24 | uint32_t(bytes[1]) << 16 | uint32_t(bytes[2]) << 8 | bytes[3]; }
        uint16_t Little16(const uint8_t* bytes) { return uint16_t(bytes[0]) | uint16_t(bytes[1]) << 8; }
        uint32_t Little32(const uint8_t* bytes)
        { return uint32_t(bytes[0]) | uint32_t(bytes[1]) << 8 | uint32_t(bytes[2]) << 16 | uint32_t(bytes[3]) << 24; }

        void CheckDimensions(std::size_t width, std::size_t height, std::size_t limit = 4096)
        {
            if(!width || !height || width > limit || height > limit || width * height > 4u * 1024u * 1024u)
                throw std::invalid_argument("Resource dimensions exceed the preview limit.");
        }

        std::vector<uint8_t> FontBytes(std::vector<uint8_t> bytes)
        {
            if(bytes.size() < 12) throw std::invalid_argument("The font header is truncated.");
            const auto tag = Big32(bytes.data());
            if(tag == 0x774f4646 || tag == 0x774f4632)
            {
                if(bytes.size() < 20 || Big32(bytes.data() + 8) != bytes.size() ||
                    !Big32(bytes.data() + 16) || Big32(bytes.data() + 16) > MaxResourceBytes)
                    throw std::invalid_argument("The declared unpacked font exceeds its preview limit.");
                ComPtr<IDWriteFactory5> factory;
                if(FAILED(::DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory5),
                    reinterpret_cast<IUnknown**>(factory.GetAddressOf()))))
                    throw std::invalid_argument("This Windows version cannot unpack the supplied web font.");
                ComPtr<IDWriteFontFileStream> stream;
                if(FAILED(factory->UnpackFontFile(tag == 0x774f4646 ? DWRITE_CONTAINER_TYPE_WOFF : DWRITE_CONTAINER_TYPE_WOFF2,
                    bytes.data(), static_cast<UINT32>(bytes.size()), &stream)))
                    throw std::invalid_argument("The supplied web font could not be unpacked.");
                UINT64 length{};
                if(FAILED(stream->GetFileSize(&length)) || !length || length > MaxResourceBytes)
                    throw std::invalid_argument("The unpacked font exceeds its preview limit.");
                const void* fragment{}; void* context{};
                if(FAILED(stream->ReadFileFragment(&fragment, 0, length, &context)))
                    throw std::invalid_argument("The unpacked font is unavailable.");
                try { bytes.assign(static_cast<const uint8_t*>(fragment), static_cast<const uint8_t*>(fragment) + static_cast<std::size_t>(length)); }
                catch(...) { stream->ReleaseFileFragment(context); throw; }
                stream->ReleaseFileFragment(context);
            }
            if(bytes.size() < 12 || (Big32(bytes.data()) != 0x00010000 && Big32(bytes.data()) != 0x4f54544f && Big32(bytes.data()) != 0x74727565))
                throw std::invalid_argument("Only a bounded sfnt font can enter the private font collection.");
            const std::size_t tables = (uint16_t(bytes[4]) << 8) | bytes[5];
            if(!tables || tables > 4096 || 12u + std::size_t(tables) * 16u > bytes.size())
                throw std::invalid_argument("The font table directory is invalid.");
            for(std::size_t index = 0; index < tables; ++index)
            {
                const auto table = bytes.data() + 12 + index * 16;
                const auto offset = Big32(table + 8), size = Big32(table + 12);
                if(offset > bytes.size() || size > bytes.size() - offset)
                    throw std::invalid_argument("A font table exceeds the supplied bytes.");
            }
            return bytes;
        }
    }

    struct PreviewResources::State
    {
        DiagnosticSink diagnostics;
        uint32_t dpi;
        bool uninitialize{};
        std::map<std::wstring, NativeMenuRenderImage> images;
        std::vector<NativeMenuRenderSurface> surfaces;
        std::vector<HANDLE> fonts;
        std::vector<std::string> dependencies;
        std::size_t pixelBytes{};

        State(uint32_t value, DiagnosticSink sink) : diagnostics(std::move(sink)), dpi(value)
        {
            const auto status = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            uninitialize = SUCCEEDED(status);
            if(FAILED(status) && status != RPC_E_CHANGED_MODE) throw std::runtime_error("Native resource decoding could not initialize COM.");
        }
        ~State()
        {
            for(auto font : fonts) ::RemoveFontMemResourceEx(font);
            if(uninitialize) ::CoUninitialize();
        }

        NativeMenuRenderImage Keep(NativeMenuRenderSurface surface)
        {
            if(!surface.valid() || surface.byte_size() > MaxPixelBytes - pixelBytes)
                throw std::invalid_argument("The request's decoded images exceed their memory limit.");
            NativeMenuRenderImage image;
            image.kind = NativeMenuImageKind::bitmap;
            image.bitmap = {surface.bitmap(), surface.size()};
            pixelBytes += surface.byte_size();
            surfaces.push_back(std::move(surface));
            return image;
        }

        NativeMenuRenderImage DecodeImage(std::vector<uint8_t>& bytes, bool icon, std::size_t metadataWidth, std::size_t metadataHeight)
        {
            UINT frameIndex = 0;
            std::size_t expectedWidth = metadataWidth, expectedHeight = metadataHeight;
            if(icon)
            {
                if(bytes.size() < 6 || Little16(bytes.data()) != 0 || Little16(bytes.data() + 2) != 1)
                    throw std::invalid_argument("The icon header is invalid.");
                const auto count = Little16(bytes.data() + 4);
                if(!count || count > 256 || 6u + std::size_t(count) * 16 > bytes.size())
                    throw std::invalid_argument("The icon directory is invalid.");
                std::size_t maxWidth = 0, maxHeight = 0, best = SIZE_MAX;
                const auto target = (std::max)(16u, dpi / 6u);
                for(UINT index = 0; index < count; ++index)
                {
                    const auto item = bytes.data() + 6 + index * 16;
                    const std::size_t width = item[0] ? item[0] : 256, height = item[1] ? item[1] : 256;
                    const auto length = Little32(item + 8), offset = Little32(item + 12);
                    if(!length || offset < 6u + count * 16u || offset > bytes.size() || length > bytes.size() - offset)
                        throw std::invalid_argument("An icon image exceeds the supplied bytes.");
                    maxWidth = (std::max)(maxWidth, width); maxHeight = (std::max)(maxHeight, height);
                    const auto difference = width >= target ? width - target : target - width + 256u;
                    if(difference < best) { best = difference; frameIndex = index; expectedWidth = width; expectedHeight = height; }
                }
                if(metadataWidth != maxWidth || metadataHeight != maxHeight)
                    throw std::invalid_argument("Icon dimensions do not match the supplied snapshot.");
            }
            else
            {
                constexpr uint8_t signature[] = {137, 80, 78, 71, 13, 10, 26, 10};
                if(bytes.size() < 33 || std::memcmp(bytes.data(), signature, 8) ||
                    Big32(bytes.data() + 8) != 13 || std::memcmp(bytes.data() + 12, "IHDR", 4) ||
                    metadataWidth != Big32(bytes.data() + 16) || metadataHeight != Big32(bytes.data() + 20))
                    throw std::invalid_argument("PNG dimensions do not match the supplied snapshot.");
            }
            CheckDimensions(metadataWidth, metadataHeight);
            ComPtr<IWICImagingFactory> factory;
            ComPtr<IWICStream> stream;
            ComPtr<IWICBitmapDecoder> decoder;
            ComPtr<IWICBitmapFrameDecode> frame;
            ComPtr<IWICFormatConverter> converter;
            // Instantiate only Windows' built-in decoders, never codec discovery.
            if(FAILED(::CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))) ||
                FAILED(factory->CreateStream(&stream)) || FAILED(stream->InitializeFromMemory(bytes.data(), static_cast<DWORD>(bytes.size()))) ||
                FAILED(::CoCreateInstance(icon ? CLSID_WICIcoDecoder : CLSID_WICPngDecoder, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&decoder))) ||
                FAILED(decoder->Initialize(stream.Get(), WICDecodeMetadataCacheOnDemand)) || FAILED(decoder->GetFrame(frameIndex, &frame)))
                throw std::invalid_argument("The native image decoder rejected this resource.");
            UINT width{}, height{};
            if(FAILED(frame->GetSize(&width, &height))) throw std::invalid_argument("The decoded image has no dimensions.");
            CheckDimensions(width, height);
            if(width != expectedWidth || height != expectedHeight) throw std::invalid_argument("The decoded image dimensions changed.");
            if(std::size_t(width) * height * 4 > MaxPixelBytes - pixelBytes)
                throw std::invalid_argument("The request's decoded images exceed their memory limit.");
            NativeMenuRenderSurface surface({static_cast<LONG>(width), static_cast<LONG>(height)}, dpi, 4u * 1024u * 1024u);
            if(!surface.valid() || FAILED(factory->CreateFormatConverter(&converter)) ||
                FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)) ||
                FAILED(converter->CopyPixels(nullptr, width * 4, static_cast<UINT>(surface.byte_size()), surface.pixels())))
                throw std::invalid_argument("The image could not be converted into native preview pixels.");
            return Keep(std::move(surface));
        }
    };

    PreviewResources::PreviewResources(const Json::Value& request, uint32_t dpi, DiagnosticSink diagnostics)
        : state(std::make_unique<State>(dpi, std::move(diagnostics)))
    {
        const auto resources = request.Find("resources");
        if(!resources) return;
        if(resources->kind != Json::Kind::Array || resources->array.size() > 128)
            throw std::invalid_argument("The resource snapshot exceeds its item limit.");
        std::size_t bytesRead = 0;
        std::set<std::wstring> seen;
        for(const auto& resource : resources->array)
        {
            const auto path = Json::StringMember(resource, "path");
            const auto key = ResourcePath(ResourceWide(path));
            if(!seen.insert(key).second) throw std::invalid_argument("A resource path is ambiguous.");
            try
            {
                if(Json::StringMember(resource, "status") != "available") throw std::invalid_argument("An explicitly scoped resource was unavailable at snapshot time.");
                auto bytes = DecodeBase64(Json::StringMember(resource, "content"), MaxResourceBytes);
                if(bytes.size() != ResourceNumber(resource, "byteLength", MaxResourceBytes) || bytes.size() > MaxTotalBytes - bytesRead)
                    throw std::invalid_argument("Resource bytes do not match their size limit or snapshot.");
                bytesRead += bytes.size();
                auto hash = Json::StringMember(resource, "sha256");
                for(auto& letter : hash) if(letter >= 'a' && letter <= 'f') letter -= 'a' - 'A';
                if(hash != ResourceHash(bytes)) throw std::invalid_argument("Resource bytes do not match their snapshot fingerprint.");
                const auto kind = Json::StringMember(resource, "kind"), format = Json::StringMember(resource, "format");
                if(kind == "font")
                {
                    if(state->fonts.size() >= 16) throw std::invalid_argument("The request exceeds its private font limit.");
                    bytes = FontBytes(std::move(bytes));
                    DWORD count{};
                    const auto handle = ::AddFontMemResourceEx(bytes.data(), static_cast<DWORD>(bytes.size()), nullptr, &count);
                    if(!handle || !count) { if(handle) ::RemoveFontMemResourceEx(handle); throw std::invalid_argument("The supplied font could not be added to the worker's private collection."); }
                    try { state->fonts.push_back(handle); } catch(...) { ::RemoveFontMemResourceEx(handle); throw; }
                }
                else if((kind == "png" && format == "png") || (kind == "icon" && format == "ico"))
                    state->images.emplace(key, state->DecodeImage(bytes, kind == "icon", ResourceNumber(resource, "width", 4096), ResourceNumber(resource, "height", 4096)));
                else throw std::invalid_argument("The resource kind or format is unsupported.");
                state->dependencies.push_back("{\"path\":" + Json::Quote(path) + ",\"sha256\":" + Json::Quote(hash) + '}');
            }
            catch(const std::exception& error)
            { if(state->diagnostics) state->diagnostics("PREVIEW_RESOURCE", error.what()); }
        }
    }

    PreviewResources::~PreviewResources() = default;

    bool PreviewResources::LoadImage(const std::wstring& path, NativeMenuRenderImage& image) const
    {
        const auto found = state->images.find(ResourcePath(path));
        if(found == state->images.end()) { image = {}; return false; }
        image = found->second; return true;
    }

    bool PreviewResources::DecodeCapturedImage(const Json::Value& value, NativeMenuRenderImage& image)
    {
        image = {};
        try
        {
            if(Json::StringMember(value, "format") != "Pbgra32") return false;
            const auto width = ResourceNumber(value, "width", 512), height = ResourceNumber(value, "height", 512);
            CheckDimensions(width, height, 512);
            auto pixels = DecodeBase64(Json::StringMember(value, "pixels"), 512u * 512u * 4u);
            if(pixels.size() != width * height * 4 || pixels.size() > MaxPixelBytes - state->pixelBytes) return false;
            for(std::size_t index = 0; index < pixels.size(); index += 4)
                if(pixels[index] > pixels[index + 3] || pixels[index + 1] > pixels[index + 3] || pixels[index + 2] > pixels[index + 3]) return false;
            NativeMenuRenderSurface surface({static_cast<LONG>(width), static_cast<LONG>(height)}, state->dpi);
            if(!surface.valid()) return false;
            std::memcpy(surface.pixels(), pixels.data(), pixels.size());
            image = state->Keep(std::move(surface));
            return true;
        }
        catch(const std::exception&) { return false; }
    }

    std::string PreviewResources::Dependencies() const
    {
        std::string result = "[";
        for(const auto& dependency : state->dependencies) { if(result.size() > 1) result += ','; result += dependency; }
        return result + ']';
    }
}
