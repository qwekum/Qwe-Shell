#pragma once

#include "PreviewJson.h"
#include <cmath>
#include <memory>
#include <stdexcept>

namespace Nilesoft::Shell::StudioPreview
{
    inline std::wstring Wide(std::string_view value);

    // Request-owned values only. Never call Preparing/QuerySelected, resolve
    // links, or probe a path to guess its filesystem kind in the preview host.
    class PreviewSelection
    {
        using Value = ::ShellStudio::PreviewJson::Value;
        using Kind = ::ShellStudio::PreviewJson::Kind;
        bool started{};

        void Begin()
        {
            available = false;
            if(started) throw std::logic_error("A preview selection snapshot can only be loaded once.");
            started = true;
        }

        static int Integer(const Value& object, std::string_view key, int minimum, int maximum)
        {
            const auto field = object.Find(key);
            if(!field || field->kind != Kind::Number) throw std::invalid_argument("Missing selection integer.");
            const auto number = std::stod(field->text);
            if(!std::isfinite(number) || number != std::floor(number) || number < minimum || number > maximum)
                throw std::invalid_argument("Selection integer is out of bounds.");
            return static_cast<int>(number);
        }

        static bool Boolean(const Value& object, std::string_view key)
        {
            const auto field = object.Find(key);
            if(!field || field->kind != Kind::Boolean) throw std::invalid_argument("Missing selection boolean.");
            return field->boolean;
        }

        static string Text(const Value& object, std::string_view key)
        {
            const auto field = object.Find(key);
            if(!field || field->kind != Kind::String || field->text.size() > 131072 || field->text.find('\0') != std::string::npos)
                throw std::invalid_argument("Invalid selection string.");
            const auto wide = Wide(field->text);
            if(wide.size() > 32760) throw std::invalid_argument("Selection string exceeds its limit.");
            return string(wide.c_str());
        }

        void Finish()
        {
            value.Front = value.Items.empty() ? nullptr : value.Items.front();
            available = true;
        }

    public:
        Selections value;
        bool available{};

        void LoadCaptured(const Value& snapshot)
        {
            Begin();
            if(Integer(snapshot, "version", 1, 1) != 1) return;
            value.Background = Boolean(snapshot, "background");
            value.Window.id = Integer(snapshot, "windowId", WINDOW_NONE, WINDOW_QUICK_ACCESS);
            value.Window.desktop = Boolean(snapshot, "windowDesktop");
            value.Window.explorer = Boolean(snapshot, "windowExplorer");
            value.Window.explorer_tree = Boolean(snapshot, "windowExplorerTree");
            value.Mode = static_cast<SelectionMode>(Integer(snapshot, "mode", 0, static_cast<int>(SelectionMode::Multiple)));
            value.Parent = Text(snapshot, "parent");
            value.ParentRaw = Text(snapshot, "parentRaw");
            value.front = Integer(snapshot, "front", -1, FSO_MAX - 1);
            value.Directory = Text(snapshot, "directory");
            const auto types = snapshot.Find("types");
            const auto items = snapshot.Find("items");
            if(!types || types->kind != Kind::Array || types->array.size() != FSO_MAX ||
                !items || items->kind != Kind::Array || items->array.size() > 4096)
                throw std::invalid_argument("Invalid captured selection arrays.");
            for(std::size_t index = 0; index < types->array.size(); ++index)
            {
                const auto& type = types->array[index];
                if(type.kind != Kind::Number)
                    throw std::invalid_argument("Invalid captured selection type flag.");
                const auto number = std::stod(type.text);
                if(!std::isfinite(number) || number != std::floor(number) || number < 0 ||
                    number > (index == FSO_COUNT ? FSO_MAX : 1))
                    throw std::invalid_argument("Invalid captured selection type flag.");
                value.Types[index] = static_cast<int>(number);
            }
            for(const auto& input : items->array)
            {
                auto item = std::make_unique<Selections::PathItem>();
                item->Path = Text(input, "path"); item->Raw = Text(input, "raw");
                item->Name = Text(input, "name"); item->Title = Text(input, "title");
                item->Extension = Text(input, "extension");
                item->Type = static_cast<PathType>(Integer(input, "type", -1, static_cast<int>(PathType::Recyclebin)));
                item->Group = static_cast<PathType>(Integer(input, "group", -1, static_cast<int>(PathType::Recyclebin)));
                item->ReadOnly = Boolean(input, "readOnly"); item->Hidden = Boolean(input, "hidden");
                item->IsLink = Boolean(input, "isLink");
                switch(item->Group)
                {
                case PathType::File: ++value.count.FILE; break;
                case PathType::Directory: ++value.count.DIRECTORY; break;
                case PathType::Drive: ++value.count.DRIVE; break;
                case PathType::Namespace: ++value.count.NAMESPACE; break;
                default: break;
                }
                value.Items.push_back(item.get()); item.release();
            }
            Finish();
        }

        void LoadSample(const Value& snapshot)
        {
            Begin();
            const auto kind = Text(snapshot, "kind");
            const bool noPaths = kind.equals(L"ui") || kind.equals(L"system") || kind.equals(L"edit") || kind.equals(L"start") || kind.equals(L"taskbar");
            const bool pathKind = kind.equals(L"file") || kind.equals(L"dir") || kind.equals(L"dir.back") ||
                kind.equals(L"drive") || kind.equals(L"drive.back") || kind.equals(L"desktop") ||
                kind.equals(L"namespace") || kind.equals(L"namespace.back");
            if(!noPaths && !pathKind) throw std::invalid_argument("Unsupported sample selection kind.");
            value.Background = kind.equals(L"dir.back") || kind.equals(L"drive.back") || kind.equals(L"desktop") || kind.equals(L"namespace.back");
            value.Window.id = kind.equals(L"desktop") ? WINDOW_DESKTOP : WINDOW_EXPLORER;
            value.Window.desktop = kind.equals(L"desktop");
            value.Window.explorer = !value.Window.desktop;
            value.Parent = Text(snapshot, "parentPath");
            const auto paths = snapshot.Find("paths");
            if(!paths || paths->kind != Kind::Array || paths->array.size() > 4096)
                throw std::invalid_argument("Invalid sample selection paths.");
            if((noPaths && !paths->array.empty()) || (pathKind && paths->array.empty()) ||
                (value.Background && paths->array.size() != 1))
                throw std::invalid_argument("Sample selection cardinality does not match its context.");
            if(noPaths)
            {
                value.Window.explorer = false;
                if(kind.equals(L"taskbar")) { value.Window.id = WINDOW_TASKBAR; value.Types[FSO_TASKBAR] = true; }
                else if(kind.equals(L"start")) { value.Window.id = WINDOW_START; value.Types[FSO_START] = true; }
                else if(kind.equals(L"edit")) { value.Window.id = WINDOW_EDIT; value.Types[FSO_EDIT] = true; }
                else if(kind.equals(L"system")) { value.Window.id = WINDOW_SYSMENU; value.Types[FSO_TITLEBAR] = true; }
                else value.Window.id = WINDOW_UI;
            }
            for(const auto& input : paths->array)
            {
                if(input.kind != Kind::String || input.text.size() > 131072 || input.text.find('\0') != std::string::npos)
                    throw std::invalid_argument("Invalid sample path.");
                const auto wide = Wide(input.text);
                if(wide.size() < 3 || wide.size() > 32760) throw std::invalid_argument("Sample path length is invalid.");
                const string path(wide.c_str());
                if(kind.equals(L"drive") || kind.equals(L"drive.back"))
                {
                    // An unspecified sample drive remains generic. Its media
                    // type must never be inferred from the worker machine.
                    value.Types[value.Background ? FSO_BACK_DRIVE : FSO_DRIVE] = true;
                    ++value.count.DRIVE;
                    value.Add(PathType::Drive, PathType::Drive, path);
                }
                else
                {
                    // Native Parse(FileProperties) has a drive-probing branch
                    // for length three. Exclude that branch before calling it.
                    if(wide.size() == 3) throw std::invalid_argument("Choose drive kind for a drive-root sample.");
                    FileProperties properties;
                    properties.Path = properties.PathRaw = path;
                    properties.IsFile = kind.equals(L"file");
                    properties.IsDir = kind.equals(L"dir") || kind.equals(L"dir.back") || kind.equals(L"desktop");
                    properties.IsNamespace = kind.equals(L"namespace") || kind.equals(L"namespace.back");
                    if(!properties.IsFile && !properties.IsDir && !properties.IsNamespace)
                        throw std::invalid_argument("Unsupported sample selection kind.");
                    if(!value.Parse(&properties)) throw std::invalid_argument("The native sample selection could not be constructed.");
                }
            }
            if(value.Parent.empty() && !value.Items.empty())
                value.Parent = value.Background ? value.Items.front()->Path : Path::Parent(value.Items.front()->Path).move();
            value.Directory = value.Background && !value.Items.empty() ? value.Items.front()->Path : value.Parent;
            value.ParentRaw = value.Parent;
            value.Types[FSO_BACK] = value.Background;
            for(int index = 0; index < FSO_BACK; ++index) value.Types[FSO_COUNT] += value.Types[index];
            if(!value.Items.empty())
            {
                switch(value.Items.front()->Group)
                {
                case PathType::File: value.front = FSO_FILE; break;
                case PathType::Directory: value.front = FSO_DIRECTORY; break;
                case PathType::Drive: value.front = FSO_DRIVE; break;
                case PathType::Namespace: value.front = FSO_NAMESPACE; break;
                default: break;
                }
            }
            value.QuerySelectionMode();
            Finish();
        }
    };
}
