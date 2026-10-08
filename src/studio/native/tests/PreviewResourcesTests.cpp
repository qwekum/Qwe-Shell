#include "../PreviewResources.h"
#include "../PreviewRendering.h"
#include <bcrypt.h>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <filesystem>
#pragma comment(lib, "bcrypt.lib")

using namespace Nilesoft::Shell;
using namespace Nilesoft::Shell::StudioPreview;
namespace Json = ::ShellStudio::PreviewJson;

namespace
{
    void Require(bool condition, const char* message) { if(!condition) throw std::runtime_error(message); }
    Json::Value Parse(const std::string& text)
    {
        Json::Value result; std::string error;
        Json::Limits limits; limits.maxStringBytes = 12u * 1024u * 1024u;
        Require(Json::Parse(text, result, error, limits), error.c_str()); return result;
    }
    constexpr auto Png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAEUlEQVR4nGP4z8DQwPCf4T8ADn0Dfur2k8AAAAAASUVORK5CYII=";
    constexpr auto PngHash = "dec65bc27c73ff4397824ffde655454723f4da3df9d4a68aa135c1ecd6f33e40";
    std::string Entry(std::string path, std::string kind, std::string format, std::string content,
        std::string hash, std::size_t size, int width, int height)
    {
        return "{\"path\":" + Json::Quote(path) + ",\"kind\":" + Json::Quote(kind) + ",\"format\":" + Json::Quote(format) +
            ",\"status\":\"available\",\"content\":" + Json::Quote(content) + ",\"sha256\":" + Json::Quote(hash) +
            ",\"byteLength\":" + std::to_string(size) + ",\"width\":" + std::to_string(width) + ",\"height\":" + std::to_string(height) + '}';
    }
    std::string Hash(const std::vector<uint8_t>& bytes)
    {
        UCHAR digest[32]{};
        Require(BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), digest, 32) >= 0, "font hash failed");
        constexpr char hex[] = "0123456789abcdef"; std::string result;
        for(auto value : digest) { result += hex[value >> 4]; result += hex[value & 15]; } return result;
    }
}

int main()
{
    try
    {
        std::vector<std::string> diagnostics;
        auto report = [&](std::string_view, std::string_view message) { diagnostics.emplace_back(message); };
        const auto png = Entry("fixture.png", "png", "png", Png, PngHash, 74, 2, 1);
        const auto ico = Entry("fixture.ico", "icon", "ico", "AAABAAEAAQEAAAEAIAAwAAAAFgAAACgAAAABAAAAAgAAAAEAIAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAICAAAAAAA==",
            "1a3bd785a3c838ce37b9b7b2fb305acc7d11e66bf6bf1f72ee4a8163d97a6e56", 70, 1, 1);
        auto request = Parse("{\"resources\":[" + png + ',' + ico + "]}");
        PreviewResources resources(request, 144, report);
        Require(diagnostics.empty(), diagnostics.empty() ? "resource diagnostics" : diagnostics.front().c_str());
        NativeMenuRenderImage image;
        Require(resources.LoadImage(L"fixture.png", image) && image.kind == NativeMenuImageKind::bitmap && image.bitmap.size.cx == 2, "PNG image missing");
        DIBSECTION bitmap{};
        Require(::GetObjectW(image.bitmap.handle, sizeof(bitmap), &bitmap) == sizeof(bitmap), "PNG pixels inaccessible");
        const auto pixels = static_cast<const uint8_t*>(bitmap.dsBm.bmBits);
        Require(pixels && pixels[0] == 0 && pixels[1] == 0 && pixels[2] == 128 && pixels[3] == 128 && pixels[5] == 255, "PNG alpha was not premultiplied");
        Require(resources.LoadImage(L"fixture.ico", image) && image.bitmap.size.cx == 1, "ICO image missing");
        Require(!resources.LoadImage(L"not-supplied.png", image), "missing resource unexpectedly loaded");
        Require(Parse(resources.Dependencies()).array.size() == 2, "resource fingerprints missing");

        // The directory advertises a 2x2 frame while its embedded DIB remains
        // the valid 1x1 payload from the fixture.  The snapshot metadata must
        // not make WIC accept a frame whose decoded dimensions disagree with
        // the selected ICO directory entry.
        const auto mismatchedIco = Entry("mismatched.ico", "icon", "ico",
            "AAABAAEAAgIAAAEAIAAwAAAAFgAAACgAAAABAAAAAgAAAAEAIAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAICAAAAAAA==",
            "ccdd041473ff873e8b24133aacf59c2cdc963b282537d44521539597dd530524", 70, 2, 2);
        diagnostics.clear();
        PreviewResources rejectedIco(Parse("{\"resources\":[" + mismatchedIco + "]}"), 96, report);
        Require(!rejectedIco.LoadImage(L"mismatched.ico", image) && !diagnostics.empty() &&
            diagnostics.front().find("decoded image dimensions changed") != std::string::npos,
            "ICO directory/payload dimension mismatch was accepted");

        for(const auto& invalid : {
            Entry("bad.png", "png", "png", Png, std::string(64, '0'), 74, 2, 1),
            Entry("bad.png", "png", "png", Png, PngHash, 74, 3, 1),
            Entry("bad.png", "png", "png", Png, PngHash, 75, 2, 1),
            Entry("bad.png", "png", "png", "AA=A", PngHash, 2, 1, 1)})
        {
            diagnostics.clear();
            PreviewResources rejected(Parse("{\"resources\":[" + invalid + "]}"), 96, report);
            Require(!rejected.LoadImage(L"bad.png", image) && !diagnostics.empty(), "invalid resource was accepted");
        }
        bool duplicateRejected = false;
        try { PreviewResources duplicate(Parse("{\"resources\":[" + png + ',' + png + "]}"), 96, report); }
        catch(const std::invalid_argument&) { duplicateRejected = true; }
        Require(duplicateRejected, "ambiguous resource paths accepted");

        Require(resources.DecodeCapturedImage(Parse("{\"format\":\"Pbgra32\",\"width\":1,\"height\":1,\"pixels\":\"AACAgA==\"}"), image), "captured pixels rejected");
        Require(!resources.DecodeCapturedImage(Parse("{\"format\":\"Pbgra32\",\"width\":1,\"height\":1,\"pixels\":\"AAD/gA==\"}"), image), "non-premultiplied capture accepted");
        Require(!resources.DecodeCapturedImage(Parse("{\"format\":\"Pbgra32\",\"width\":513,\"height\":1,\"pixels\":\"AACAgA==\"}"), image), "oversized captured image accepted");

        std::vector<uint8_t> oversizedFont(44);
        oversizedFont[0] = 'w'; oversizedFont[1] = 'O'; oversizedFont[2] = 'F'; oversizedFont[3] = 'F';
        oversizedFont[11] = 44; oversizedFont[16] = 1;
        diagnostics.clear();
        PreviewResources rejectedFont(Parse("{\"resources\":[" + Entry("oversized.woff", "font", "wOFF",
            EncodePixels(oversizedFont.data(), oversizedFont.size()), Hash(oversizedFont), oversizedFont.size(), 0, 0) + "]}"), 96, report);
        Require(!diagnostics.empty() && diagnostics.front().find("declared unpacked") != std::string::npos,
            "unpacked font bound was not enforced before decompression");

        // Only the fixture harness reads this installed font. The decoder gets
        // the same memory-only request used by the broker and never sees a path
        // it can open. No installed font or registry entry is changed.
        wchar_t windows[MAX_PATH]{};
        Require(::GetWindowsDirectoryW(windows, MAX_PATH) != 0, "Windows fixture root unavailable");
        std::ifstream font(std::filesystem::path(windows) / L"Fonts" / L"segoeui.ttf", std::ios::binary);
        Require(font.good(), "system font fixture unavailable");
        std::vector<uint8_t> fontBytes((std::istreambuf_iterator<char>(font)), std::istreambuf_iterator<char>());
        diagnostics.clear();
        PreviewResources privateFont(Parse("{\"resources\":[" + Entry("font.ttf", "font", "sfnt", EncodePixels(fontBytes.data(), fontBytes.size()),
            Hash(fontBytes), fontBytes.size(), 0, 0) + "]}"), 96, report);
        Require(diagnostics.empty() && Parse(privateFont.Dependencies()).array.size() == 1, "private font failed to load");
        std::cout << "PASS native PNG/ICO, premultiplied capture, snapshot rejection, and private font checks\n";
        return 0;
    }
    catch(const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
