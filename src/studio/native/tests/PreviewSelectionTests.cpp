#include "../../../dll/src/pch.h"
#include "../PreviewSelection.h"

#include <Windows.h>

#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>

namespace Nilesoft::Shell::StudioPreview
{
    inline std::wstring Wide(std::string_view value)
    {
        if(value.empty()) return {};
        const int length = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
            value.data(), static_cast<int>(value.size()), nullptr, 0);
        if(length <= 0) throw std::invalid_argument("invalid UTF-8 test value");
        std::wstring result(static_cast<std::size_t>(length), L'\0');
        if(::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
            static_cast<int>(value.size()), result.data(), length) != length)
            throw std::invalid_argument("invalid UTF-8 test value");
        return result;
    }
}

namespace
{
    using Nilesoft::Shell::FSO_COUNT;
    using Nilesoft::Shell::FSO_FILE;
    using Nilesoft::Shell::FSO_MAX;
    using Nilesoft::Shell::FSO_TASKBAR;
    using Nilesoft::Shell::PathType;
    using Nilesoft::Shell::SelectionMode;
    using Nilesoft::Shell::StudioPreview::PreviewSelection;
    using JsonValue = ShellStudio::PreviewJson::Value;

    void Require(bool condition, const char *message)
    {
        if(!condition) throw std::runtime_error(message);
    }

    template<typename Function>
    void RequireThrows(Function &&function, const char *message)
    {
        bool threw = false;
        try { function(); }
        catch(const std::exception &) { threw = true; }
        Require(threw, message);
    }

    JsonValue Parse(std::string_view json)
    {
        JsonValue value;
        std::string error;
        ShellStudio::PreviewJson::Limits limits;
        limits.maxBytes = 1024U * 1024U;
        limits.maxStringBytes = 256U * 1024U;
        Require(ShellStudio::PreviewJson::Parser(json, limits).Parse(value, error),
            error.empty() ? "test JSON did not parse" : error.c_str());
        return value;
    }

    JsonValue Sample(std::string_view kind, std::string_view paths)
    {
        return Parse(std::string("{\"kind\":\"") + std::string(kind) +
            "\",\"parentPath\":\"C:\\\\preview\",\"paths\":[" +
            std::string(paths) + "]}");
    }

    std::string CapturedJson()
    {
        std::string types = "[";
        for(int index = 0; index < FSO_MAX; ++index)
        {
            if(index != 0) types += ',';
            types += std::to_string(index == FSO_FILE ? 1 : index == FSO_COUNT ? 1 : 0);
        }
        types += ']';
        return R"({"version":1,"background":false,"windowId":7,"windowDesktop":false,"windowExplorer":true,"windowExplorerTree":false,"mode":1,"parent":"C:\\preview","parentRaw":"C:\\preview","front":6,"directory":"C:\\preview","types":)" +
            types + R"(,"items":[{"path":"C:\\preview\\alpha.txt","raw":"C:\\preview\\alpha.txt","name":"alpha.txt","title":"alpha","extension":".txt","type":1,"group":1,"readOnly":false,"hidden":false,"isLink":false}]})";
    }

    void TestMixedExtensions()
    {
        PreviewSelection selection;
        selection.LoadSample(Sample("file", R"("C:\\preview\\alpha.txt","C:\\preview\\beta.md")"));
        Require(selection.available, "mixed-extension sample was unavailable");
        Require(selection.value.Items.size() == 2U, "mixed-extension sample lost an item");
        Require(selection.value.count.FILE == 2U, "mixed-extension sample count is wrong");
        Require(selection.value.Mode == SelectionMode::MultiSingle,
            "mixed-extension sample did not preserve native selection mode");
        Require(selection.value.Types[FSO_FILE] == 1 && selection.value.Types[FSO_COUNT] == 1,
            "mixed-extension sample types are wrong");
        Require(selection.value.Items[0]->Extension.equals(L".txt") &&
            selection.value.Items[1]->Extension.equals(L".md"),
            "Parse(FileProperties) did not derive file extensions");
        std::cout << "PASS preview-selection-mixed-extensions\n";
    }

    void TestExplicitDriveDoesNotProbe()
    {
        PreviewSelection selection;
        selection.LoadSample(Sample("drive", R"("Z:\\")"));
        Require(selection.available && selection.value.Items.size() == 1U,
            "drive sample was unavailable");
        const auto *item = selection.value.Items.front();
        Require(item != nullptr && item->Type == PathType::Drive && item->Group == PathType::Drive,
            "explicit drive sample was classified using host media state");
        Require(selection.value.Types[Nilesoft::Shell::FSO_DRIVE] == 1,
            "explicit drive sample did not set the generic drive type");
        std::cout << "PASS preview-selection-explicit-drive\n";
    }

    void TestNoPathTaskbar()
    {
        PreviewSelection selection;
        selection.LoadSample(Sample("taskbar", ""));
        Require(selection.available && selection.value.Items.empty(),
            "taskbar sample unexpectedly created a path item");
        Require(selection.value.Window.id == Nilesoft::Shell::WINDOW_TASKBAR &&
            selection.value.Types[FSO_TASKBAR] == 1 && selection.value.Types[FSO_COUNT] == 1,
            "taskbar sample lost its context type");
        Require(selection.value.Mode == SelectionMode::None,
            "taskbar sample changed the empty selection mode");
        std::cout << "PASS preview-selection-taskbar\n";
    }

    void TestInputValidation()
    {
        PreviewSelection invalidKind;
        RequireThrows([&] { invalidKind.LoadSample(Sample("unknown", "")); },
            "unknown sample kind was accepted");
        Require(!invalidKind.available, "invalid sample kind became available");

        PreviewSelection emptyFile;
        RequireThrows([&] { emptyFile.LoadSample(Sample("file", "\"\"")); },
            "empty file sample was accepted");
        Require(!emptyFile.available, "empty file sample became available");

        PreviewSelection noPathForFile;
        RequireThrows([&] { noPathForFile.LoadSample(Sample("file", "")); },
            "file sample without a path was accepted");

        PreviewSelection secondLoad;
        secondLoad.LoadSample(Sample("taskbar", ""));
        RequireThrows([&] { secondLoad.LoadSample(Sample("ui", "")); },
            "selection snapshot was reusable after its first load");
        Require(!secondLoad.available, "failed second load left stale availability");
        std::cout << "PASS preview-selection-validation\n";
    }

    void TestCapturedReconstruction()
    {
        PreviewSelection selection;
        selection.LoadCaptured(Parse(CapturedJson()));
        Require(selection.available && selection.value.Items.size() == 1U,
            "captured selection was not reconstructed");
        Require(selection.value.Window.id == Nilesoft::Shell::WINDOW_EXPLORER &&
            selection.value.Window.explorer && !selection.value.Background,
            "captured window metadata was changed");
        Require(selection.value.front == FSO_FILE && selection.value.Front == selection.value.Items.front(),
            "captured front selection was not reconstructed");
        Require(selection.value.Types[FSO_FILE] == 1 && selection.value.Types[FSO_COUNT] == 1,
            "captured type flags were not reconstructed");
        Require(selection.value.count.FILE == 1U,
            "captured item counters were not rebuilt");
        const auto *item = selection.value.Items.front();
        Require(item->Path.equals(L"C:\\preview\\alpha.txt") && item->Raw.equals(L"C:\\preview\\alpha.txt") &&
            item->Name.equals(L"alpha.txt") && item->Title.equals(L"alpha") &&
            item->Extension.equals(L".txt") && item->Type == PathType::File && item->Group == PathType::File,
            "captured item fields were not copied exactly");
        std::cout << "PASS preview-selection-captured\n";
    }
}

int main()
{
    try
    {
        TestMixedExtensions();
        TestExplicitDriveDoesNotProbe();
        TestNoPathTaskbar();
        TestInputValidation();
        TestCapturedReconstruction();
        std::cout << "Preview selection tests passed\n";
        return 0;
    }
    catch(const std::exception &error)
    {
        std::cerr << "Preview selection tests failed: " << error.what() << "\n";
        return 1;
    }
}
