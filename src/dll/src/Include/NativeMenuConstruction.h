#pragma once

// Shared, side-effect-free decisions used by the live ContextMenu builder and
// the value-only preview adapter.  The callers remain responsible for native
// handles, expression evaluation, image loading, capture traces, and command
// execution.  Keeping these predicates here prevents the two construction
// paths from silently acquiring different selection and rule semantics.

#include "Menu.h"
#include "MenuItem.h"
#include "Selections.h"
#include "FindPattern.h"

#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>
#include <vector>

namespace Nilesoft::Shell::NativeMenuConstruction
{
    // The live capture path and focused native construction tests share this
    // coordinator.  It owns only traversal state; callers provide the
    // side-effectful child construction and the state update for each entry.
    // In particular, a failed callback is committed as an incomplete branch
    // rather than being silently treated as an empty, complete popup.
    struct AutomaticCaptureCallbacks
    {
        std::function<bool()> canContinue;
        std::function<HMENU(const MenuItemInfo *)> submenu;
        std::function<uint32_t(const MenuItemInfo *)> entryId;
        std::function<bool(MenuItemInfo *, size_t,
            const std::vector<uint32_t> &, std::vector<MenuItemInfo *> &)> construct;
        std::function<void(MenuItemInfo *, std::vector<MenuItemInfo *> &&, bool)> commit;
        std::function<void(std::vector<MenuItemInfo *> &, std::wstring_view)> markUnavailable;
        std::function<void(MenuItemInfo *, std::wstring_view)> markIncomplete;
    };

    inline void MaterializeAutomaticPopups(
        std::vector<MenuItemInfo *> &entries, size_t depth,
        const std::vector<uint32_t> &ancestors, size_t maxDepth,
        std::unordered_set<HMENU> &activeMenus,
        const AutomaticCaptureCallbacks &callbacks)
    {
        if(callbacks.canContinue && !callbacks.canContinue())
        {
            if(callbacks.markUnavailable)
                callbacks.markUnavailable(entries,
                    L"PREVIEW_UNAVAILABLE: automatic capture stopped before this branch.");
            return;
        }

        for(auto *entry : entries)
        {
            if(!entry || !callbacks.submenu)
                continue;
            const auto handle = callbacks.submenu(entry);
            if(handle == nullptr)
                continue;

            if(callbacks.canContinue && !callbacks.canContinue())
            {
                if(callbacks.markUnavailable)
                    callbacks.markUnavailable(entries,
                        L"PREVIEW_UNAVAILABLE: automatic capture stopped before this branch.");
                return;
            }

            if(depth >= maxDepth)
            {
                if(callbacks.markIncomplete)
                    callbacks.markIncomplete(entry,
                        L"CAPTURE_DEPTH_LIMIT: submenu depth exceeded the automatic capture limit.");
                continue;
            }

            if(activeMenus.find(handle) != activeMenus.end())
            {
                if(callbacks.markIncomplete)
                    callbacks.markIncomplete(entry,
                        L"CAPTURE_CYCLE: submenu identity was already being materialized.");
                continue;
            }

            std::vector<uint32_t> childAncestors = ancestors;
            childAncestors.push_back(callbacks.entryId
                ? callbacks.entryId(entry) : entry->id);
            std::vector<MenuItemInfo *> childEntries;
            activeMenus.insert(handle);
            bool constructed = false;
            try
            {
                if(callbacks.construct)
                    constructed = callbacks.construct(entry, depth,
                        childAncestors, childEntries);
            }
            catch(...)
            {
                constructed = false;
            }
            activeMenus.erase(handle);

            if(callbacks.commit)
                callbacks.commit(entry, std::move(childEntries), constructed);
            if(constructed)
                MaterializeAutomaticPopups(entry->items, depth + 1,
                    childAncestors, maxDepth, activeMenus, callbacks);
        }
    }

    struct SelectionInput
    {
        const Selections *value{};
        // A preview can deliberately omit a selection.  When this flag is
        // true, an omitted selection is a failed match rather than an implicit
        // "all selections" value.  Runtime callers always supply a selection.
        bool require{};
        bool bypassTaskbar = true;

        bool matches_types(const FileSystemObjects &fso) const noexcept
        {
            return value ? value->verify_types(fso) : !require;
        }

        bool matches_mode(SelectionMode mode) const noexcept
        {
            if(!value)
                return !require;
            if(bypassTaskbar && value->Window.id <= WINDOW_TASKBAR)
                return true;
            return value->verify_mode(mode);
        }

        // Match a dynamic `find` expression against every selected item.  This
        // intentionally follows the runtime rule: all selected items must
        // satisfy the split pattern and at least one item must be present.
        bool matches_find(const string &pattern) const noexcept
        {
            if(pattern.empty() || !value)
                return pattern.empty() || !require;
            if(value->Items.empty())
                return false;

            FindPattern find;
            if(!find.split(pattern, L'|'))
                return false;

            std::size_t found = 0;
            for(const auto *selected : value->Items)
            {
                if(!selected)
                    return false;
                string extension = selected->Extension.substr(1).move();
                if(!find(&selected->Title,
                    selected->IsFile() ? &extension : nullptr,
                    &selected->Path))
                    return false;
                ++found;
            }
            return found != 0;
        }
    };

    struct BuildOptions
    {
        SelectionInput selection{};
        SelectionMode inheritedMode = SelectionMode::Single;
        // Evaluate authored where/find/mode/FSO rules when true.  Runtime
        // callers set this implicitly through the supplied selection input;
        // previews opt in explicitly when they have a read-only selection
        // snapshot.
        bool evaluateRules{};

        // The live builder has two independent phases.  Keeping the switches
        // explicit lets a preview render the captured system tree even when a
        // caller only wants to inspect authored rows, while the normal preview
        // path enables both phases.
        bool applyStatic = true;
        bool applyDynamic = true;

        // A popup path is a sequence of captured ids or normalized titles.  A
        // non-empty path asks the adapter to materialize only that authored
        // submenu branch; an empty path preserves lazy popup children.  This
        // mirrors the live menu, which evaluates a popup on WM_INITMENUPOPUP.
        std::vector<std::wstring> requestedPath;
        bool lazyChildren = true;

        // Resolve settings expressions when the request supplies them.  The
        // defaults match ContextMenu's resolved _settings values and keep a
        // zero-expression cache useful to focused native probes.
        bool modifyEnabled = true;
        bool modifyTitle = true;
        bool modifyVisibility = true;
        bool modifyParent = true;
        bool modifySeparator = true;
        bool modifyKeys = true;
        int modifyImage = 1;
        int modifyPosition = 1;
        bool removeDuplicate = false;
        bool removeDisabled = false;
        bool removeSeparator = false;
        bool newItemsEnabled = true;
        bool newItemsImage = true;
        bool newItemsKeys = true;

        // Optional source identity resolver supplied by Studio.  The native
        // parser's n<offset> identity is useful for diagnostics but is not
        // stable across edits; callers can map it to their lossless node id.
        std::function<std::wstring(std::wstring_view, std::size_t)> sourceIdentity;
        // Optional preview-only decision log. The adapter invokes this while
        // evaluating a rule so hidden entries remain explainable without a
        // second evaluation. Live Explorer construction leaves it empty.
        std::function<void(const NativeMenu &, std::wstring_view,
            std::wstring_view)> decisionSink;
    };

    inline bool visible(Visibility value) noexcept
    {
        return value != Visibility::Hidden;
    }

    inline bool disabled(Visibility value) noexcept
    {
        return value == Visibility::Disabled;
    }

    inline bool label(Visibility value) noexcept
    {
        return value == Visibility::Label;
    }

    inline bool static_item(Visibility value) noexcept
    {
        return value == Visibility::Static;
    }

    inline bool separator_top(Separator value) noexcept
    {
        return (static_cast<int>(value) & static_cast<int>(Separator::Top)) != 0;
    }

    inline bool separator_bottom(Separator value) noexcept
    {
        return (static_cast<int>(value) & static_cast<int>(Separator::Bottom)) != 0;
    }

    inline bool title_or_image(const NativeMenu &item,
        const string &title) noexcept
    {
        return !title.empty() || item.image.defined;
    }

    inline bool checked(int value) noexcept
    {
        return value > 0;
    }

    inline bool radio_checked(int value) noexcept
    {
        return value == 2;
    }

    inline bool location_matches(bool root, string location,
        const string &path) noexcept
    {
        location.trim().trim(L'/');
        if(location.empty())
            return root;
        if(location.starts_with(L"**", false))
            location.remove(0, 1);
        else if(location.equals(L"*", false))
            return true;
        if(root || path.empty())
            return false;
        return path.equals(location);
    }

    inline bool dynamic_types_match(const SelectionInput &selection,
        const NativeMenu &item) noexcept
    {
        return selection.matches_types(item.fso);
    }

    inline bool dynamic_mode_match(const SelectionInput &selection,
        SelectionMode mode) noexcept
    {
        return selection.matches_mode(mode);
    }

    inline bool static_types_match(const SelectionInput &selection,
        const NativeMenu &rule) noexcept
    {
        return selection.matches_types(rule.fso);
    }

    inline bool static_mode_match(const SelectionInput &selection,
        SelectionMode mode) noexcept
    {
        return selection.matches_mode(mode);
    }

    inline bool static_find_match(const string &pattern,
        const string &name) noexcept
    {
        if(pattern.empty())
            return false;
        FindPattern find;
        if(!find.split(pattern, L'|'))
            return false;
        return find(&name);
    }
}
