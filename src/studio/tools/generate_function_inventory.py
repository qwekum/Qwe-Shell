"""Regenerate the source-reviewed editor catalogue and its embedded metadata.

Run the exported insertion corpus after generation; documentation spellings
alone are not evidence that the current verifier accepts an entry.
"""
from pathlib import Path
import hashlib
import json
import os
import re
import ctypes
from extract_verifier_inventory import extract

ROOT = Path(__file__).resolve().parents[3]
os.chdir(ROOT)

CPP = Path("src/dll/src/Parser/LanguageFrontend.cpp")
FUNCTION_ARTIFACT = Path("docs/studio/function-coverage.json")


def read_capabilities():
    source = CPP.read_text(encoding="utf-8")
    match = re.search(r'R"CAPABILITY\((.*)\)CAPABILITY"', source, re.S)
    if not match:
        raise RuntimeError("capability JSON was not found")
    return source, match, json.loads(match.group(1))


def docs_inventory():
    known_roots = {
        "app", "appx", "clipboard", "color", "command", "id", "image", "icon",
        "cmd", "mode", "type", "ini", "input", "io", "key", "msg", "path",
        "process", "reg", "regex", "sel", "str", "sys", "system", "this",
        "user", "window", "wnd", "font", "package", "uwp", "svg", "svgf",
    }
    pattern = re.compile(
        r"(?<![A-Za-z0-9_])(?:[a-z][a-z0-9_-]*)(?:\.[a-z][a-z0-9_-]*)+(?![A-Za-z0-9_])",
        re.I,
    )
    result = {}
    for path in sorted(Path("docs/functions").glob("*.html")):
        for raw_name in pattern.findall(path.read_text(encoding="utf-8", errors="ignore")):
            name = raw_name.lower()
            if name.split(".")[0] not in known_roots:
                continue
            if name.startswith(("http.", "github.")) or name.endswith((".html", ".md")):
                continue
            if name.startswith(("microsoft.", "explorer.", "system.")):
                continue
            # A name can occur in its syntax, examples, and cross-reference
            # text.  Evidence records the source file once; repeated entries
            # make the catalogue look more strongly evidenced than it is.
            source_path = path.as_posix()
            if source_path not in result.setdefault(name, []):
                result[name].append(source_path)
    return result


# Runtime/source members whose docs use property spelling or omit an optional
# argument.  The range is the accepted runtime call shape, not the editor's
# placeholder count; templates use the minimum valid arity.
CALLABLE_ARITY = {
    "appx": (1, 1),
    "appx.path": (1, 1), "appx.name": (1, 1), "appx.id": (1, 1),
    "appx.family": (1, 1), "appx.version": (1, 1), "appx.shell": (1, 1),
    "package": (1, 1), "package.list": (0, 0), "package.exists": (1, 1),
    "package.path": (1, 1), "package.id": (1, 1), "package.name": (1, 1),
    "package.family": (1, 1), "package.version": (1, 1), "package.ver": (1, 1),
    "package.run": (1, 1), "package.launch": (1, 1), "package.shell": (1, 1),
    "uwp": (1, 1), "uwp.list": (0, 0), "uwp.exists": (1, 1),
    "uwp.path": (1, 1), "uwp.id": (1, 1), "uwp.name": (1, 1),
    "uwp.family": (1, 1), "uwp.version": (1, 1), "uwp.ver": (1, 1),
    "uwp.run": (1, 1), "uwp.launch": (1, 1), "uwp.shell": (1, 1),
    "clipboard.set": (1, 1),
    "color": (1, 4), "color.rgb": (3, 3), "color.rgba": (2, 4),
    "color.box": (0, 1), "color.random": (0, 2), "color.invert": (1, 1),
    "color.light": (1, 2), "color.dark": (1, 2), "color.lighten": (1, 2),
    "color.darken": (1, 2), "color.adjust": (1, 2), "color.opacity": (2, 2),
    "command.copy": (1, 1), "command.copy_to_clipboard": (1, 1),
    "command.sleep": (1, 1), "command.random": (2, 2), "command.navigate": (1, 1),
    "command.cascade_windows": (0, 0), "command.copy_to_folder": (0, 0),
    "command.customize_this_folder": (0, 0), "command.find": (0, 0),
    "command.folder_options": (0, 0), "command.invert_selection": (0, 0),
    "command.minimize_all_windows": (0, 0), "command.move_to_folder": (0, 0),
    "command.redo": (0, 0), "command.refresh": (0, 0),
    "command.restart_explorer": (0, 0), "command.restore_all_windows": (0, 0),
    "command.run": (0, 0), "command.search": (0, 0), "command.select_all": (0, 0),
    "command.select_none": (0, 0), "command.show_windows_side_by_side": (0, 0),
    "command.show_windows_stacked": (0, 0), "command.switcher": (0, 0),
    "command.toggle_desktop": (0, 0), "command.toggleext": (0, 0),
    "command.togglehidden": (0, 0), "command.undo": (0, 0),
    "font.loaded": (1, 1), "font.system": (1, 1), "font.exists": (1, 1),
    "image.glyph": (1, 4), "image.rect": (1, 4), "image.svg": (1, 1),
    "image.svgf": (1, 1), "icon.glyph": (1, 4), "icon.rect": (1, 4),
    "icon.svg": (1, 1), "icon.svgf": (1, 1), "img.glyph": (1, 4),
    "img.rect": (1, 4), "img.svg": (1, 1), "img.svgf": (1, 1),
    "svg": (1, 1), "svgf": (1, 1),
    "if": (1, 3), "for": (1, 3), "foreach": (2, 3), "while": (1, 1),
    "eval": (1, 1), "indexof": (1, 3), "length": (1, 1), "quote": (0, 1),
    "char": (1, 2), "tohex": (1, 1), "equal": (1, 32), "equals": (1, 32),
    "not": (1, 1), "greater": (2, 2), "less": (2, 2), "shl": (2, 2),
    "shr": (2, 2), "random": (2, 2), "print": (1, 1), "toint": (1, 1),
    "todouble": (1, 1), "touint": (1, 1), "tofloat": (1, 1),
    "ini.get": (3, 3), "ini.set": (4, 4), "input": (2, 3),
    "io.attributes": (1, 1), "io.copy": (2, 3), "io.move": (2, 2),
    "io.rename": (2, 2), "io.delete": (1, 1), "io.directory.create": (1, 32),
    "io.directory.exists": (1, 32), "io.directory.empty": (1, 32),
    "io.dir.create": (1, 32), "io.dir.exists": (1, 32), "io.dir.empty": (1, 32),
    "io.file.size": (1, 1), "io.file.exists": (1, 32), "io.file.read": (1, 2),
    "io.file.create": (1, 3), "io.file.make": (1, 3), "io.file.write": (2, 2),
    "io.file.append": (2, 2), "io.datetime.created": (1, 7),
    "io.datetime.modified": (1, 7), "io.datetime.accessed": (1, 7),
    "io.meta": (1, 2),
    "key": (0, 32), "keys": (0, 0), "key.send": (1, 3),
    "msg": (1, 3), "msg.beep": (0, 1),
    "path.combine": (2, 32), "path.join": (2, 32), "path.currentdirectory": (0, 1),
    "path.curdir": (0, 1), "path.directory.name": (1, 1), "path.dir.name": (1, 1),
    "path.directory.box": (0, 3), "path.dir.box": (0, 3), "path.empty": (1, 32),
    "path.exists": (1, 32), "path.full": (1, 1), "path.short": (1, 1),
    "path.name": (1, 1), "path.location": (1, 1), "path.parent": (1, 1),
    "path.location.name": (1, 1), "path.root": (1, 1), "path.title": (1, 1),
    "path.type": (1, 1), "path.file.name": (1, 1), "path.file.title": (1, 1),
    "path.file.ext": (1, 1), "path.file.box": (0, 3), "path.files": (1, 4),
    "path.isabsolute": (1, 1), "path.isrelative": (1, 1), "path.isfile": (1, 1),
    "path.isdirectory": (1, 1), "path.isdir": (1, 1), "path.isroot": (1, 1),
    "path.isdrive": (1, 1), "path.isclsid": (1, 1), "path.isnamespace": (1, 1),
    "path.isexe": (1, 1), "path.removeextension": (1, 1),
    "path.remove_extension": (1, 1), "path.lnk": (1, 1), "path.lnktarget": (1, 1),
    "path.lnk.target": (1, 1), "path.lnk.type": (1, 1), "path.lnk.dir": (1, 1),
    "path.lnk.icon": (1, 1), "path.lnk.create": (2, 8),
    "path.getknownfolder": (1, 1),
    "reg.exists": (1, 2), "reg.get": (1, 2), "reg.set": (3, 4),
    "reg.delete": (1, 2), "reg.keys": (1, 2), "reg.values": (1, 2),
    "regex.match": (2, 2), "regex.matches": (2, 2), "regex.replace": (3, 4),
    "sel": (0, 2), "sel.get": (1, 2), "sel.path.raw": (0, 1),
    "sel.length": (0, 1), "sel.readonly": (0, 1), "sel.hidden": (0, 1),
    "sel.meta": (1, 2),
    "str.get": (2, 2), "str.at": (2, 2), "str.set": (3, 3),
    "str.contains": (2, 2), "str.empty": (1, 1), "str.null": (1, 1),
    "str.start": (2, 2), "str.end": (2, 2), "str.equals": (2, 3),
    "str.not": (2, 3), "str.length": (1, 1), "str.len": (1, 1),
    "str.trim": (1, 2), "str.trimstart": (1, 2), "str.trimend": (1, 2),
    "str.find": (2, 2), "str.findlast": (2, 2), "str.lower": (1, 1),
    "str.upper": (1, 1), "str.left": (2, 2), "str.right": (2, 2),
    "str.sub": (2, 3), "str.remove": (2, 3), "str.replace": (3, 4),
    "str.padleft": (2, 3), "str.padright": (2, 3), "str.padding": (2, 3),
    "str.guid": (0, 1), "str.capitalize": (1, 1), "str.res": (1, 2),
    "str.hash": (1, 1), "str.format": (1, 32), "str.tag": (1, 2),
    "str.decode": (1, 2),
    "sys.var": (1, 1), "sys.expand": (1, 1), "sys.isorearlier": (2, 2),
    "sys.isorgreater": (2, 2), "sys.is7orgreater": (0, 0),
    "sys.is8orgreater": (0, 0), "sys.is81orgreater": (0, 0),
    "sys.is10orgreater": (0, 0), "sys.is11orgreater": (0, 0),
    "sys.is7orearlier": (0, 0), "sys.is8orearlier": (0, 0),
    "sys.is81orearlier": (0, 0), "sys.is10orearlier": (0, 0),
    "sys.is11orearlier": (0, 0), "sys.datetime": (0, 1),
    "system.datetime": (0, 1), "datetime": (0, 1), "user.expand": (1, 1),
    "window.command": (1, 2), "window.send": (4, 4), "window.post": (4, 4),
    "wnd.command": (1, 2), "wnd.send": (4, 4), "wnd.post": (4, 4),
}

# Accepted verifier shapes, including forms that the checked-in reference
# abbreviates as property-style values. These bounds do not execute functions.
CALLABLE_ARITY.update({
    "equal": (2, 2), "equals": (2, 32), "char": (1, 1), "quote": (1, 1),
    "for": (3, 3), "foreach": (3, 3), "random": (0, 2),
    "path.dir.name": (2, 2), "path.directory.name": (2, 2),
    "path.sep": (1, 2), "path.separator": (1, 2), "path.wsl": (1, 1),
    "str.set": (2, 2), "str.tag": (2, 3),
    "str.start": (2, 3), "str.end": (2, 3), "str.find": (2, 3),
    "str.findlast": (2, 3), "str.contains": (2, 3),
    "image.fluent": (1, 3), "image.mdl": (1, 3), "image.segoe": (1, 3), "image.res": (1, 2),
    "process.is_started": (1, 1), "reg.set": (1, 4), "reg.keys": (1, 1), "reg.values": (1, 1),
    "regex.replace": (3, 3), "window.send": (3, 4), "window.post": (3, 4),
    "wnd.send": (3, 4), "wnd.post": (3, 4), "window.command": (0, 2), "wnd.command": (0, 2),
    "package.list": (0, 1), "appx.list": (0, 1), "uwp.list": (0, 1),
})


SOURCE_ONLY = {
    "app.root", "app.version.major", "app.version.minor", "app.version.build",
    "app.process", "app.used", "font.text", "font.icon", "font.size", "font.scale",
    "font.segoe_fluent_icons", "font.segoe_mdl2", "font.segoe_ui_symbol",
    "font.segoe_ui", "font.segoe_ui_emoji", "font.segoe_ui_historic",
    "font.webdings", "font.wingdings", "font.wingdings2", "font.wingdings3",
    "process.is_started", "this.verb", "this.level", "this.is_uwp", "this.clsid",
    "wnd.handle", "wnd.name", "wnd.title", "wnd.owner", "wnd.parent",
    "wnd.parent.handle", "wnd.parent.name", "wnd.is_contextmenuhandler",
    "sys.expand", "sys.isorearlier", "sys.isorgreater", "sys.is7orgreater",
    "sys.is8orgreater", "sys.is81orgreater", "sys.is10orgreater", "sys.is11orgreater",
    "sys.is7orearlier", "sys.is8orearlier", "sys.is81orearlier", "sys.is10orearlier",
    "sys.is11orearlier", "user.expand", "path.lnk.create", "path.lnktarget",
    "path.lnk.target", "path.remove_extension", "io.file.make", "str.hash",
    "str.format", "str.tag", "str.decode", "svg", "svgf", "img.svg", "img.svgf",
    "icon.svg", "icon.svgf", "icon.glyph", "icon.rect",
}


COMMAND_NAMES = {
    name for name in CALLABLE_ARITY
    if name.startswith("command.")
}
COMMAND_NAMES |= {
    "io.copy", "io.move", "io.rename", "io.delete", "io.file.create", "io.file.make",
    "io.file.write", "io.file.append", "io.directory.create", "io.dir.create",
    "reg.set", "reg.delete", "ini.set", "key.send", "window.send", "window.post",
    "window.command", "wnd.send", "wnd.post", "wnd.command", "msg.beep",
    "app.reload", "app.unload", "path.lnk.create",
}


def source_paths(name, docs_sources):
    paths = list(docs_sources.get(name, []))
    paths.append("src/dll/src/Parser/Verification.cpp")
    if name.startswith(('key.', 'keys.', 'color.')):
        paths.append("src/dll/src/Expression/Constants.h")
    source = "src/dll/src/Expression/FuncExpression.cpp"
    if source not in paths:
        paths.append(source)
    return paths


def kind_for(name):
    if name == "io.attribute":
        return "namespace", "namespace", "pure"
    if name == "io.datetime":
        return "namespace", "namespace", "readOnly"
    if name in CALLABLE_ARITY:
        effect = "runtime" if name in COMMAND_NAMES else "pure"
        if name in {"appx", "package", "uwp"} or name.startswith(("appx.", "package.", "uwp.", "path.", "sys.", "user.", "process.", "sel.", "font.", "reg.", "io.", "clipboard.")):
            effect = "readOnly" if effect == "pure" else effect
        return ("command" if name in COMMAND_NAMES else "function",
                "command" if name in COMMAND_NAMES else "function", effect)
    if name.startswith(("id.", "key.", "mode.", "type.", "cmd.")):
        return "constant", "constant", "pure"
    if name.startswith(("io.attribute.", "msg.", "reg.")):
        if name in {"msg.beep"}:
            return "command", "command", "runtime"
        if name in {"reg.get", "reg.exists", "reg.keys", "reg.values"}:
            return "function", "function", "pure"
        return "constant", "constant", "pure"
    if name.startswith("color.") and name.rsplit(".", 1)[-1] not in {
        "box", "random", "rgb", "rgba", "invert", "light", "dark", "lighten",
        "darken", "adjust", "opacity",
    }:
        return "constant", "constant", "pure"
    if name == "icon.copy":
        return "constant", "constant", "pure"
    return "property", "property", "readOnly" if name.startswith((
        "app.", "appx.", "package.", "uwp.", "path.", "process.", "sel.",
        "sys.", "system.", "user.", "window.", "wnd.", "font.",
    )) else "pure"


def old_arity(old):
    arity = old.get("arity")
    if isinstance(arity, dict) and "min" in arity and "max" in arity:
        return int(arity["min"]), int(arity["max"])
    return None


def make_entry(name, old, docs_sources):
    kind, visual_kind, side_effect = kind_for(name)
    entry = {"name": name, "kind": kind, "visualKind": visual_kind}
    arity = CALLABLE_ARITY.get(name) or old_arity(old)
    if name == "io.attribute":
        entry["editor"] = {"insertTemplate": "io.attribute.hidden(null)"}
        entry["members"] = "io.attribute.<name>(path)"
    elif name == "io.datetime":
        entry["editor"] = {"insertTemplate": "io.datetime.created(null)"}
        entry["members"] = "io.datetime.<created|modified|accessed>(path)"
    elif arity is not None and kind in {"function", "command"}:
        low, high = arity
        placeholders = ", ".join("null" for _ in range(low))
        entry["arity"] = {"min": low, "max": high}
        entry["editor"] = {"insertTemplate": f"{name}({placeholders})"}
    else:
        entry["editor"] = {"insertTemplate": name}
    if name == "for":
        entry["editor"]["insertTemplate"] = "for(i=0,i<3,i)"
    elif name == "foreach":
        entry["editor"]["insertTemplate"] = "foreach($value,sel.paths,$value)"
    if old.get("returnType"):
        entry["returnType"] = old["returnType"]
    if old.get("construct"):
        entry["construct"] = old["construct"]
    entry["sideEffect"] = side_effect
    entry["evidence"] = source_paths(name, docs_sources)
    return entry


def main():
    source, match, cap = read_capabilities()
    docs_sources = docs_inventory()
    all_names = set(docs_sources) | set(CALLABLE_ARITY) | set(SOURCE_ONLY)
    all_names.update(entry["name"] for entry in cap["functions"])
    old = {entry["name"]: entry for entry in cap["functions"]}
    source_candidates, unmapped = extract(ROOT, all_names)
    all_names.update(source_candidates)
    known_roots = {name.split('.')[0] for name in source_candidates} | {"if", "for", "foreach"}
    dll_path = ROOT / "src/studio/native/bin/Release/x64/ShellStudio.Language.dll"
    dll = ctypes.CDLL(str(dll_path))
    dll.shell_studio_parse.argtypes = [ctypes.c_wchar_p, ctypes.c_size_t]
    dll.shell_studio_parse.restype = ctypes.c_void_p
    dll.shell_studio_free.argtypes = [ctypes.c_void_p]
    def accepts(expression):
        text = "item(title=" + expression + ")"
        pointer = dll.shell_studio_parse(text, len(text.encode('utf-16-le')) // 2)
        if not pointer:
            raise RuntimeError("Native parser allocation failed")
        try:
            parsed = json.loads(ctypes.string_at(pointer))
            return not any(d['severity'] == 'error' for d in parsed['diagnostics'])
        finally:
            dll.shell_studio_free(pointer)
    functions, excluded = [], []
    for name in sorted(all_names):
        if name in {'appx.title', 'package.title', 'uwp.title'}:
            excluded.append({"name": name, "reason": "Verifier accepts title but the package runtime switch has no title result branch."})
            continue
        if name.startswith('command.') and name not in source_candidates:
            excluded.append({"name": name, "reason": "No command dispatch branch; syntax-only zero-argument acceptance cannot establish a runtime command."})
            continue
        if name.split('.')[0] not in known_roots:
            excluded.append({"name": name, "reason": "No built-in verifier root; imported variables are not built-ins."})
            continue
        entry = make_entry(name, old.get(name, {}), docs_sources)
        bare = accepts(name)
        if name in {"for", "foreach"}:
            counts = [3] if accepts(entry['editor']['insertTemplate']) else []
        else:
            counts = [n for n in range(33) if accepts(name + '(' + ','.join(['null'] * n) + ')')]
        if not bare and not counts and not accepts(entry['editor']['insertTemplate']):
            excluded.append({"name": name, "reason": "Current native verifier rejects bare and 0-32 argument forms."})
            continue
        if counts and (max(counts) > 0 or not bare or name in CALLABLE_ARITY):
            entry['kind'] = 'command' if name in COMMAND_NAMES else 'function'
            entry['visualKind'] = entry['kind']
            entry['arity'] = {'min': min(counts), 'max': max(counts), 'allowed': counts, 'probeLimit': 32}
            if name not in {'for', 'foreach'}:
                entry['editor']['insertTemplate'] = name + '(' + ', '.join(['null'] * min(counts)) + ')'
        elif bare:
            entry.pop('arity', None)
            if entry['kind'] in {'function', 'command'}:
                entry['kind'] = entry['visualKind'] = 'property'
            entry['editor']['insertTemplate'] = name
        entry['acceptsBare'] = bare
        if name in source_candidates:
            entry['verifierLines'] = source_candidates[name]
        functions.append(entry)
    callables = [entry for entry in functions if entry["kind"] in {"function", "command"}]
    values = [entry for entry in functions if entry["kind"] in {"property", "constant", "namespace"}]
    unresolved = [
        {
            "name": "dynamic names and runtime-dependent identifier resolution",
            "status": "incomplete",
            "reason": "Finite switch/table coverage and syntax acceptance do not establish all runtime-dependent names or evaluation semantics. Named members use the typed identifier/call editor.",
            "evidence": ["src/dll/src/Parser/IdentHash.h", "src/dll/src/Parser/Verification.cpp"],
        }
    ]

    cap["functions"] = functions
    dynamic_families = [
        {"pattern": "loc.<name>", "example": "loc.caption", "resolution": "Localization name from configuration"},
        {"pattern": "image.<name> / icon.<name> / img.<name>", "example": "image.custom", "resolution": "Named image or runtime menu image"},
        {"pattern": "svg.<name>", "example": "svg.custom", "resolution": "Named SVG definition"},
        {"pattern": "id.<name>[.title|.name|.str|.icon]", "example": "id.copy.title", "resolution": "Runtime menu identifier map"},
        {"pattern": "title.<name>", "example": "title.copy", "resolution": "Runtime menu identifier map"},
        {"pattern": "<variable>.<member>(...)", "example": "studio_value.upper()", "resolution": "Imported variable and runtime string-member dispatch"},
    ]
    for family in dynamic_families:
        family['editor'] = 'namedMember'
        family['syntaxChecked'] = accepts(family['example'])
        if not family['syntaxChecked']:
            raise RuntimeError('Invalid dynamic family example: ' + family['example'])
    cap['dynamicFamilies'] = dynamic_families
    cap["callableCoverage"] = {
        "complete": False,
        "scope": "documented and source-dispatched callable names",
        "count": len(callables),
        "mapped": sum(1 for entry in callables if entry["editor"].get("insertTemplate")),
        "artifact": "docs/studio/function-coverage.json",
        "unresolved": unresolved,
    }
    cap["source"] = (
        "src/dll/src/Parser/IdentHash.h;src/dll/src/Parser/Verification.cpp;"
        "src/dll/src/Expression/FuncExpression.cpp;src/dll/src/Expression/Constants.h;docs/functions/*.html"
    )
    new_json = json.dumps(cap, ensure_ascii=False, separators=(",", ":"))
    CPP.write_text(source[:match.start(1)] + new_json + source[match.end(1):], encoding="utf-8", newline="\n")

    hashes = {}
    for path in (
        Path("src/dll/src/Parser/IdentHash.h"),
        Path("src/dll/src/Parser/Verification.cpp"),
        Path("src/dll/src/Expression/FuncExpression.cpp"),
        Path("src/dll/src/Expression/Constants.h"),
    ):
        hashes[path.name] = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    docs_hash = hashlib.sha256(
        b"".join(path.read_bytes() for path in sorted(Path("docs/functions").glob("*.html")))
    ).hexdigest().upper()
    hashes["docs/functions/*.html"] = docs_hash
    artifact = {
        "version": 1,
        "scope": "Runtime expression functions, commands, property-style runtime values, and documented expression constants.",
        "sourceHashes": hashes,
        "coverage": {
            "complete": False,
            "callableComplete": False,
            "callableCount": len(callables),
            "mappedCallableCount": sum(1 for entry in callables if entry["editor"].get("insertTemplate")),
            "valueCount": len(values),
            "unresolved": unresolved,
        },
        "functions": callables,
        "values": values,
        "dynamicFamilies": dynamic_families,
        "excludedCandidates": excluded,
        "sourceCandidateCount": len(source_candidates),
        "unmappedHashes": unmapped,
    }
    FUNCTION_ARTIFACT.parent.mkdir(parents=True, exist_ok=True)
    FUNCTION_ARTIFACT.write_text(json.dumps(artifact, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"functions": len(functions), "callables": len(callables), "values": len(values), "cppSha256": hashlib.sha256(CPP.read_bytes()).hexdigest().upper()}))


if __name__ == "__main__":
    main()
