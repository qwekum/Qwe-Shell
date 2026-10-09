#include "../../../dll/src/Include/ContextMenuPaint.h"
#include <array>
#include <cstring>
#include <iostream>
#include <stdexcept>

using Nilesoft::Shell::MenuItemInfo;
using Nilesoft::Shell::ShellRowPaintFeatures;
using Nilesoft::Shell::ShellRowPaintPlan;

// These checks cover the shared state/effect boundary. They do not claim to
// rasterize text or icons; the Sandbox harness checks actual rendered pixels.
int main()
{
    try
    {
        struct Case
        {
            const char *name;
            uint32_t id, action, state;
            bool staticOrLabel, tooltip, geometry, selection, showTooltip, skip;
        };
        const Case cases[] = {
            {"normal full paint", 42, ODA_DRAWENTIRE, 0, false, false, true, false, false, false},
            {"selected full paint", 42, ODA_DRAWENTIRE, ODS_SELECTED, false, true, true, true, true, false},
            {"selected partial paint", 42, ODA_SELECT, ODS_SELECTED, false, true, false, true, true, false},
            {"selection cleared", 42, ODA_SELECT, 0, false, true, false, false, false, false},
            {"disabled normal", 42, ODA_DRAWENTIRE, ODS_DISABLED, false, true, true, false, false, false},
            {"grayed selected", 42, ODA_SELECT, ODS_GRAYED | ODS_SELECTED, false, true, false, true, true, false},
            {"disabled static partial", 42, ODA_SELECT, ODS_DISABLED | ODS_SELECTED, true, true, false, false, false, true},
            {"disabled static full", 42, ODA_DRAWENTIRE, ODS_DISABLED | ODS_SELECTED, true, true, true, false, true, false},
            {"separator", UINT32_MAX, ODA_DRAWENTIRE, 0, false, false, false, false, false, false}
        };
        std::array<unsigned char, 128> sentinel;
        sentinel.fill(0xA5);
        const auto original = sentinel;
        auto item = reinterpret_cast<MenuItemInfo *>(sentinel.data());
        for(const auto &test : cases)
        {
            ShellRowPaintFeatures features{};
            features.staticOrLabel = test.staticOrLabel;
            features.hasTooltip = test.tooltip;
            const auto plan = ShellRowPaintPlan::Resolve(test.id, test.action, test.state, item, features);
            if(plan.updateGeometry != test.geometry || plan.updateSelection != test.selection ||
                plan.showTooltip != test.showTooltip || plan.skipDisabledStatic != test.skip ||
                plan.selectedItem != (test.selection ? item : nullptr) || sentinel != original)
                throw std::runtime_error(test.name);
            std::cout << "PASS " << test.name << '\n';
        }
        const auto nullItem = ShellRowPaintPlan::Resolve(42, ODA_DRAWENTIRE, 0, nullptr, {});
        if(nullItem.updateGeometry || nullItem.selectedItem)
            throw std::runtime_error("absent item must not produce item effects");
        std::cout << "PASS absent item\n10 state/effect cases passed; rasterization is checked separately.\n";
        return 0;
    }
    catch(const std::exception &error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
}
