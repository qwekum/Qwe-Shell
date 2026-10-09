#include "../../../dll/src/pch.h"
#include "../../../dll/src/Include/ContextMenu.h"
#include <iostream>
#include <stdexcept>

using namespace Nilesoft::Shell;

namespace
{
    int commandEvaluations = 0;

    // Construction must retain command syntax without evaluating it. A real
    // evaluator call fails this assertion even if its exception is swallowed.
    class CommandSentinel final : public Expression
    {
        ExpressionType Type() const override { return ExpressionType::String; }
        Expression *Copy() override { return new CommandSentinel; }
        Object Eval(Context *) override
        {
            ++commandEvaluations;
            throw std::runtime_error("Command evaluated during construction");
        }
    };

    void Require(bool condition, const char *message)
    {
        if(!condition) throw std::runtime_error(message);
    }

    NativeMenu *Definition(NativeMenu &parent, NativeMenuType type, const wchar_t *title)
    {
        auto item = new NativeMenu(&parent);
        item->type = type;
        item->properties = 1;
        item->title = new StringExpression(title);
        item->fso.any_types = true;
        parent.items.push_back(item);
        return item;
    }

    void CheckConstruction(bool capture)
    {
        Initializer initializer;
        initializer.cache = new CACHE;
        initializer.dpi = Theme::GetDpi(Point{}, nullptr);
        ContextMenu context(nullptr, nullptr, Point{});
        context._settings.new_items.image = false;
        context.Selected.Mode = SelectionMode::Single;
        context._context.Selections = &context.Selected;

        NativeMenu source;
        source.fso.any_types = true;
        auto action = Definition(source, NativeMenuType::Item, L"Action \u03a9");
        action->cmd->command.expr = new CommandSentinel;
        action->cmd->arguments = new CommandSentinel;
        Definition(source, NativeMenuType::Item, L"Actionless label");
        auto nested = Definition(source, NativeMenuType::Menu, L"Nested");
        Definition(*nested, NativeMenuType::Item, L"Nested child");

        ContextMenu::menu_t popup;
        popup.parent = &source;
        popup.dynamics = source.items;
        Require(popup.std_items == nullptr, "Dynamic submenu must have no system vector");

        Context requestContext = context._context;
        PreviewPolicy policy;
        policy.allowAssignments = false;
        requestContext.Runtime = false;
        requestContext.Preview = &policy;
        GC<MenuItemInfo> requestGc;
        std::unordered_map<HMENU, ContextMenu::menu_t> requestMenus;
        std::vector<MenuItemInfo *> requestMoved;
        size_t remaining = 32;
        bool exhausted = false;
        if(capture)
        {
            // These are the same request-owned dependencies redirected by
            // capture_unopened_submenus; construction itself stays production.
            context._studio_construction_context = &requestContext;
            context._studio_construction_gc = &requestGc;
            context._studio_construction_menus = &requestMenus;
            context._studio_construction_moved_dynamics = &requestMoved;
            context._studio_construction_item_budget = &remaining;
            context._studio_construction_budget_exhausted = &exhausted;
        }

        std::vector<MenuItemInfo *> rows;
        Require(context.construct_popup_entries(&popup, rows, capture),
            "Valid dynamic-only popup construction failed");
        Require(rows.size() == 3, "Dynamic-only popup lost configured rows");
        Require(rows[0]->title.text.equals(L"Action \u03a9") &&
            rows[1]->title.text.equals(L"Actionless label"), "Order or Unicode changed");
        Require(rows[0]->owner_dynamic == action && rows[2]->is_popup(),
            "Dynamic source or nested popup lost");
        auto &menus = context.construction_menus();
        auto child = menus.find(rows[2]->hSubMenu);
        Require(child != menus.end() && child->second.std_items == nullptr,
            "Production submenu retained an unexpected system vector");
        std::vector<MenuItemInfo *> children;
        Require(context.construct_popup_entries(&child->second, children, capture) &&
            children.size() == 1 && children[0]->title.text.equals(L"Nested child"),
            "Nested dynamic-only child failed construction");
        std::cout << "PASS " << (capture ? "capture" : "ordinary")
            << " dynamic-only nested/actionless/order/Unicode construction\n";

        context._settings.new_items.enabled = false;
        Require(context.construct_popup_entries(&popup, rows, capture) && rows.empty(),
            "Disabled new-items gate failed");
        std::cout << "PASS " << (capture ? "capture" : "ordinary") << " disabled dynamics gate\n";
        context._settings.new_items.enabled = true;
        MenuItemInfo disabledOwner;
        disabledOwner.fState = MFS_DISABLED;
        popup.owner = &disabledOwner;
        Require(context.construct_popup_entries(&popup, rows, capture) && rows.empty(),
            "Disabled parent gate failed");
        popup.owner = nullptr;
        std::cout << "PASS " << (capture ? "capture" : "ordinary") << " disabled parent gate\n";

        Require(!context.construct_popup_entries(nullptr, rows, capture), "Null popup accepted");
        ContextMenu::PositionList systemRows;
        Require(!context.prepare_system_items(systemRows, nullptr), "Null system source accepted");
        std::cout << "PASS " << (capture ? "capture" : "ordinary") << " invalid null popup\n";

        if(capture)
        {
            remaining = 0;
            Require(context.construct_popup_entries(&popup, rows, true) && rows.empty() && exhausted,
                "Construction item bound failed");
            std::cout << "PASS capture item bound\n";
        }
        // No window/subclass/hook is initialized, and no leaf is invoked.
        // Delete only the ordinary popup handles created by this context.
        if(!capture)
            for(const auto &[handle, menu] : context._menus)
                if(handle) ::DestroyMenu(handle);
        context._menus.clear();
        context._studio_construction_context = nullptr;
        context._studio_construction_gc = nullptr;
        context._studio_construction_menus = nullptr;
        context._studio_construction_moved_dynamics = nullptr;
        context._studio_construction_item_budget = nullptr;
        context._studio_construction_budget_exhausted = nullptr;
    }
}

int main()
{
    try
    {
        CheckConstruction(false);
        CheckConstruction(true);
        Require(commandEvaluations == 0, "Command or arguments evaluated during construction");
        std::cout << "PASS retained commands and arguments never evaluated\n"
            << "10 production construction cases passed.\n";
        return 0;
    }
    catch(const std::exception &error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
