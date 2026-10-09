#pragma once

#include <Windows.h>
#include <cstdint>

namespace Nilesoft::Shell
{
	struct MenuItemInfo;

	// These are the already-resolved facts needed to commit Shell row effects.
	// Building them belongs to the live menu callback; rendering consumes them
	// without looking up or evaluating configuration again.  Item imagery and
	// text remain in the concrete native painter below the planning seam.
	struct ShellRowPaintFeatures
	{
		bool staticOrLabel{};
		bool hasTooltip{};
	};

	struct ShellRowPaintPlan
	{
		bool separator{};
		bool drawEntire{};
		bool selected{};
		bool disabled{};
		bool staticOrLabel{};
		bool skipDisabledStatic{};
		bool showTooltip{};
		bool exclude{};
		bool updateGeometry{};
		bool updateSelection{};
		MenuItemInfo *selectedItem{};

		static ShellRowPaintPlan Resolve(uint32_t itemId, uint32_t itemAction,
			uint32_t itemState, MenuItemInfo *item,
			const ShellRowPaintFeatures &features) noexcept
		{
			ShellRowPaintPlan plan{};
			plan.separator = itemId == UINT32_MAX;
			plan.drawEntire = (itemAction & ODA_DRAWENTIRE) != 0;
			plan.selected = (itemState & ODS_SELECTED) != 0;
			plan.disabled = (itemState & (ODS_DISABLED | ODS_GRAYED)) != 0;
			plan.staticOrLabel = features.staticOrLabel;
			plan.skipDisabledStatic = !plan.separator && !plan.drawEntire &&
				plan.disabled && plan.staticOrLabel;
			plan.showTooltip = !plan.skipDisabledStatic && plan.selected &&
				features.hasTooltip;
			plan.exclude = true;
			plan.updateGeometry = !plan.separator && !plan.skipDisabledStatic &&
				plan.drawEntire && item != nullptr;
			plan.updateSelection = !plan.separator && plan.selected &&
				!(plan.disabled && plan.staticOrLabel);
			if(plan.updateSelection)
				plan.selectedItem = item;
			return plan;
		}
	};
}
