#pragma once
#include "../../dll/src/Expression/Constants.h"

// Reviewed native dispatch paths that only calculate values. The default is
// unavailable, including newly added built-ins, rather than silently granting
// them the worker's operating-system privileges.
namespace Nilesoft::Shell::StudioPreview
{
    inline bool SuppliedNativeFact(const FuncExpression& function)
    {
        if(!function.Arguments.empty() || function.ischild || function.extented) return false;
        const auto& id = function.Id;
        if(id.length() != 2) return false;
        if(id[0] == IDENT_SEL)
        {
            switch(id[1])
            {
            case IDENT_PATH: case IDENT_PATHS: case IDENT_COUNT:
            case IDENT_PARENT: case IDENT_NAME: case IDENT_TITLE: case IDENT_EXT:
                return true;
            default: return false;
            }
        }
        if(id[0] == IDENT_APP) return id[1] == IDENT_CFG || id[1] == IDENT_DIR;
        if(id[0] == IDENT_COLOR)
        {
            switch(id[1])
            {
            case IDENT_COLOR_ACCENT: case IDENT_COLOR_ACCENT_LIGHT1:
            case IDENT_COLOR_ACCENT_LIGHT2: case IDENT_COLOR_ACCENT_LIGHT3:
            case IDENT_COLOR_ACCENT_DARK1: case IDENT_COLOR_ACCENT_DARK2:
            case IDENT_COLOR_ACCENT_DARK3: return true;
            default: return false;
            }
        }
        return false;
    }

    inline bool PureStringMember(uint32_t id)
    {
        switch(id)
        {
        case IDENT_LEN: case IDENT_LENGTH: case IDENT_NULL: case IDENT_EMPTY:
        case IDENT_UPPER: case IDENT_LOWER: case IDENT_CAPITALIZE: case IDENT_HASH:
        case IDENT_TRIM: case IDENT_TRIMSTART: case IDENT_TRIMEND:
        case IDENT_SET: case IDENT_CHAR: case IDENT_GET: case IDENT_AT:
        case IDENT_SUB: case IDENT_LEFT: case IDENT_RIGHT: case IDENT_PADDING:
        case IDENT_PADLEFT: case IDENT_PADRIGHT: case IDENT_NOT: case IDENT_EQ:
        case IDENT_EQUALS: case IDENT_START: case IDENT_END: case IDENT_FIND:
        case IDENT_FINDLAST: case IDENT_CONTAINS: case IDENT_REPLACE: case IDENT_REMOVE:
        case IDENT_JOIN: case IDENT_SPLIT: case IDENT_TAG: case IDENT_FORMAT:
        case IDENT_DECODE:
            return true;
        default: return false;
        }
    }

    inline bool PureNativeFunction(FuncExpression& function, Context& context)
    {
        const auto& id = function.Id;
        if(function.ischild || function.extented) return PureStringMember(id[0]);
        switch(id[0])
        {
        case IDENT_SEL:
            if(!context.Selections) return false;
            if(id.length() == 1) return true;
            switch(id[1])
            {
            case IDENT_COUNT: case IDENT_READONLY: case IDENT_HIDDEN:
            case IDENT_BACK: case IDENT_LEN: case IDENT_LENGTH:
            case IDENT_RAW: case IDENT_WORKDIR: case IDENT_CURDIR:
            case IDENT_FULL: case IDENT_PATH: case IDENT_PATHS:
            case IDENT_PARENT: case IDENT_LOCATION: case IDENT_ROOT:
            case IDENT_ITEM: case IDENT_NAME: case IDENT_TITLE: case IDENT_EXT:
            case IDENT_FILE: case IDENT_FILES: case IDENT_DIR: case IDENT_DIRECTORY:
            case IDENT_DIRS: case IDENT_DIRECTORIES: case IDENT_ROOTS: case IDENT_DRIVERS:
            case IDENT_NAMESPACES: case IDENT_TITLES: case IDENT_NAMES: case IDENT_EXTS:
            case IDENT_TYPES: case IDENT_INDEX: case IDENT_I: case IDENT_GET:
            case IDENT_MODE: return true;
            // The legacy indexed type overload does not validate its explicit
            // argument before dereferencing. Only the no-argument snapshot
            // observation is allowed until that runtime overload is repaired.
            case IDENT_TYPE: return function.Arguments.empty();
            default: return false;
            }
        case IDENT_STR: return PureStringMember(id[1]);
        case IDENT_PATH:
            switch(id[1])
            {
            case IDENT_ROOT: case IDENT_NAME: case IDENT_TITLE: case IDENT_PARENT:
            case IDENT_LOCATION: case IDENT_JOIN: case IDENT_COMBINE: case IDENT_SEP:
            case IDENT_SEPARATOR: case IDENT_ISABSOLUTE: case IDENT_ISRELATIVE:
            case IDENT_ISROOT: case IDENT_ISDRIVE: case IDENT_ISCLSID:
            case IDENT_ISNAMESPACE: case IDENT_REMOVEEXTENSION: case IDENT_EXT:
                return true;
            case IDENT_FILE: case IDENT_DIR: case IDENT_DIRECTORY:
                return id[2] == IDENT_NAME || id[2] == IDENT_TITLE || id[2] == IDENT_EXT;
            default: return false;
            }
        case IDENT_COLOR:
            // Dialogs, random generation, and OS accent discovery are separate
            // capabilities. Accent entries in ColorTable are dynamic sentinels,
            // not constant colors, and must resolve through supplied facts.
            switch(id[1])
            {
            case IDENT_COLOR_ACCENT: case IDENT_COLOR_ACCENT_LIGHT1:
            case IDENT_COLOR_ACCENT_LIGHT2: case IDENT_COLOR_ACCENT_LIGHT3:
            case IDENT_COLOR_ACCENT_DARK1: case IDENT_COLOR_ACCENT_DARK2:
            case IDENT_COLOR_ACCENT_DARK3: return false;
            case IDENT_ZERO: case IDENT_COLOR_RGB: case IDENT_COLOR_RGBA:
            case IDENT_COLOR_INVERT: case IDENT_COLOR_LIGHT: case IDENT_COLOR_DARK:
            case IDENT_COLOR_LIGHTEN: case IDENT_COLOR_DARKEN: case IDENT_COLOR_ADJUST:
            case IDENT_OPACITY: return true;
            default:
                for(const auto& color : ColorTable)
                    if(std::get<0>(color) == id[1]) return true;
                return false;
            }
        case IDENT_FOREACH:
        {
            // The runtime reads this selector directly rather than evaluating
            // it, so its capability check cannot rely on recursive dispatch.
            if(!context.Selections || function.Arguments.size() != 3 ||
                !function.Arguments[0] || !function.Arguments[0]->ident() ||
                !function.Arguments[1] || !function.Arguments[1]->ident()) return false;
            const auto& selector = function.Arguments[1]->ident()->Id;
            if(selector.length() != 2 || selector[0] != IDENT_SEL) return false;
            switch(selector[1])
            {
            case IDENT_PATHS: case IDENT_NAMES: case IDENT_TITLES: case IDENT_FILES:
            case IDENT_EXTS: case IDENT_DIRS: case IDENT_DIRECTORIES:
            case IDENT_DRIVERS: case IDENT_ROOTS: return true;
            default: return false;
            }
        }
        case IDENT_IF: case IDENT_NOT: case IDENT_TOHEX: case IDENT_CHAR:
        case IDENT_SHR: case IDENT_SHL: case IDENT_EQUALS: case IDENT_EQUAL:
        case IDENT_GREATER: case IDENT_LESS: case IDENT_TOINT: case IDENT_TODOUBLE:
        case IDENT_TOUINT: case IDENT_TOFLOAT: case IDENT_LENGTH: case IDENT_LEN:
        case IDENT_QUOTE: case IDENT_NULL: case IDENT_NIL:
        case IDENT_OK: case IDENT_YES: case IDENT_TRUE: case IDENT_NO: case IDENT_FALSE:
        case IDENT_DEFAULT: case IDENT_AUTO: case IDENT_ENABLE: case IDENT_DISABLE:
        case IDENT_ENABLED: case IDENT_DISABLED: case IDENT_INHERIT: case IDENT_NONE:
        case IDENT_BOTH: case IDENT_TOP: case IDENT_BOTTOM: case IDENT_MIDDLE:
        case IDENT_BEFORE: case IDENT_AFTER: case IDENT_REMOVE:
        case IDENT_MODE_SINGLE: case IDENT_MODE_UNIQUE: case IDENT_MODE_MULTIPLE:
        case IDENT_MODE_MULTI_SINGLE: case IDENT_MODE_MULTI_UNIQUE: case IDENT_MODE_MULTI:
        case IDENT_VIS_STATIC: case IDENT_VIS_LABEL: case IDENT_INDEXOF:
        case IDENT_BREAK: case IDENT_CONTINUE: case IDENT_FOR:
        case IDENT_LOC: case IDENT_VAR:
            return true;
        case IDENT_VIEW: case IDENT_TYPE: case IDENT_POS:
            return !function.Arguments.size() && id.length() > 1;
        case IDENT_MODE: case IDENT_VIS: case IDENT_VISIBILITY:
        case IDENT_SEP: case IDENT_SEPARATOR:
            return id.length() > 1 || (context._this && id[0] != IDENT_MODE);
        case IDENT_THEME:
            switch(id[1])
            {
            case IDENT_AUTO: case IDENT_THEME_SYSTEM: case IDENT_THEME_CLASSIC:
            case IDENT_THEME_LIGHT: case IDENT_THEME_DARK: case IDENT_THEME_HIGHCONTRAST:
            case IDENT_THEME_BLACK: case IDENT_THEME_WHITE: case IDENT_THEME_MODERN:
            case IDENT_THEME_CUSTOM: return true;
            default: return context.theme != nullptr && context.dpi != nullptr;
            }
        case IDENT_EFFECT: return id.length() > 1 || context.theme != nullptr;
        case IDENT_THIS: return context._this != nullptr;
        default:
            // Runtime variables and user functions resolve through the same
            // native scope lookup and recursively encounter this policy again.
            return id.length() == 1 && context.get_variable(id[0], &function) != nullptr;
        }
    }
}
