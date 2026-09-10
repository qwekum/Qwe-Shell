"""Generate the reviewed menu-property table and extract actual SETTING trees.

No compiler constants are assumed to be property names: spellings are checked
against their runtime DJB2 hashes. The hand-reviewed dispatch groups below are
kept separate from the mechanically extracted theme/settings declarations.
"""
from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PARSER = ROOT / "src/dll/src/Parser"


def text_hash(value: str) -> int:
    result = 5381
    for ch in value:
        result = ((result * 33) + ord(ch)) & 0xFFFFFFFF
    return result


hashes = {
    name: int(value, 16)
    for name, value in re.findall(r"constexpr auto (\w+) = (0x[0-9a-fA-F]+)U?;", (PARSER / "IdentHash.h").read_text(encoding="utf-8-sig"))
}


def spelling(symbol: str) -> str:
    suffix = symbol.split("_", 1)[1].lower()
    candidates = [suffix, suffix.split("_")[-1], suffix.replace("_", ""), suffix.replace("_", "-")]
    for value in dict.fromkeys(candidates):
        if text_hash(value) == hashes[symbol]:
            return value
    raise ValueError(f"No verified spelling for {symbol}")


@dataclass
class Initializer:
    values: list
    offset: int


def extract_settings(source: str, method: str, root_name: str) -> list[dict]:
    start = source.index(f"void Parser::{method}()")
    end = source.find("\n\t\tvoid Parser::", start + 1)
    if method == "parse_settings":
        end = source.index("uint32_t Parser::parse_image_ident", start)
    body = source[start:end if end >= 0 else len(source)]
    declarations: dict[str, Initializer] = {}
    token_pattern = re.compile(r"&[\w.>\-]+|[A-Za-z_]\w*|[{},]")
    for declaration in re.finditer(r"SETTING\s+(_\w+)\s*=\s*", body):
        tokens = [(m.group(), start + m.start()) for m in token_pattern.finditer(body, declaration.end())]
        position = 0

        def read() -> Initializer:
            nonlocal position
            opening, offset = tokens[position]
            if opening != "{":
                raise ValueError("Expected SETTING initializer")
            position += 1
            values = []
            while tokens[position][0] != "}":
                token = tokens[position][0]
                if token == "{":
                    values.append(read())
                else:
                    if token != ",":
                        values.append(token)
                    position += 1
            position += 1
            return Initializer(values, offset)

        declarations[declaration.group(1)] = read()

    results = []

    def walk(item: Initializer | str, parent: str = "") -> None:
        if isinstance(item, str):
            item = declarations[item]
        symbol, destination, *children = item.values
        name = ".".join(filter(None, [parent, spelling(symbol)]))
        assignable = destination != "nullptr"
        results.append({
            "name": name, "group": root_name, "symbol": symbol,
            "runtimeTarget": destination, "expression": assignable,
            "hasChildren": bool(children), "visualKind": "expression" if assignable else "settingsGroup",
            "editor": {"insertTemplate": name + ("=null" if assignable else " {}")},
            "source": {"file": "src/dll/src/Parser/Parser.cpp", "line": source.count("\n", 0, item.offset) + 1},
        })
        if children:
            for child in children[0].values:
                walk(child, name)

    walk(declarations["_" + root_name])
    return results


def menu_properties() -> list[dict]:
    result: dict[str, dict] = {}

    def add(names: str, contexts: str, assignment: str = "required", *, kind: str = "expression", source: str = "Verification.cpp:104-285;Properties.cpp:618-800", value: str = "null") -> None:
        for name in names.split():
            record = result.setdefault(name, {"name": name, "contexts": {}, "visualKind": kind, "expression": kind == "expression", "evidence": []})
            for context in contexts.split():
                record["contexts"][context] = assignment
            if source not in record["evidence"]:
                record["evidence"].append(source)
            record["editor"] = {"insertTemplate": name + " { cmd=\"\" }" if kind == "commandSequence" else name if assignment == "forbidden" else name + "=" + value}

    add("find where condition vis visibility pos position", "root menu item separator")
    add("sel mode", "menu item separator")
    add("type", "menu item separator modify remove", kind="typeSelector", value='"file"')
    add("text title menu move parent sub tip", "menu item")
    add("sep separator dir directory admin column col", "menu item", "optional")
    add("admin", "root", "optional")
    add("expanded", "menu", "optional")
    add("args arguments wait invoke checked", "item", "optional")
    add("arg argument verb keys window", "item")
    add("cmd command", "item command", "optional", source="Verification.cpp:34-101,238-259;Properties.cpp:449-539")
    for prefix in ("cmd", "command"):
        add(" ".join(prefix + "." + suffix for suffix in ("line", "prompt", "shell", "explorer", "powershell", "ps", "pwsh")), "item command", source="Verification.cpp:34-101,238-259;Properties.cpp:449-539")
    add("admin wait invoke args arguments dir directory", "command", "optional", source="Verification.cpp:34-101;Properties.cpp:449-539")
    add("arg argument window verb", "command", source="Verification.cpp:34-101;Properties.cpp:449-539")
    add("commands", "item", "forbidden", kind="commandSequence", source="Verification.cpp:234-237;Properties.cpp:593-616,673-678")
    add("image icon", "root menu item", "optional")
    for prefix in ("image", "icon"):
        add(prefix + ".enabled " + prefix + ".disabled", "root menu item", "forbidden", kind="flag")
        add(" ".join(prefix + "." + suffix for suffix in ("inherit", "parent", "cmd", "none", "null", "nil")), "menu item", "forbidden", kind="flag")
        add(prefix + ".sel " + prefix + ".select", "menu item")
    add("where condition find vis visibility sel mode text title tip sub menu move parent pos position keys checked invoke image icon image.sel image.select icon.sel icon.select", "modify remove", source="Properties.cpp:853-999")
    add("path location in", "modify remove", source="Properties.cpp:898-902")
    add("clsid", "modify remove", kind="classIdSelector", value='"{00000000-0000-0000-0000-000000000000}"', source="Properties.cpp:808-851,891-897")
    add("sep separator", "modify remove", "optional", source="Properties.cpp:930-941")
    for record in result.values():
        record["allowedOn"] = list(record["contexts"])
    return sorted(result.values(), key=lambda item: item["name"])


def main() -> None:
    source = (PARSER / "Parser.cpp").read_text(encoding="utf-8-sig")
    settings = extract_settings(source, "parse_theme", "theme") + extract_settings(source, "parse_settings", "settings")
    properties = menu_properties()
    inventory = {
        "version": 1,
        "scope": "Runtime menu properties and declarative SETTING trees; function inventory is separate.",
        "sourceHashes": {name: hashlib.sha256((PARSER / name).read_bytes()).hexdigest().upper() for name in ("Properties.cpp", "Verification.cpp", "Parser.cpp", "IdentHash.h")},
        "properties": properties, "settings": settings,
        "reviewNotes": [
            "MENU_ID is accepted by verify(menu) but has no parse_properties/parse_properties_command handler; it is not offered as a working property.",
            "MENU_CMDS is accepted by verify(menu) but parse_properties dispatch checks only MENU_COMMANDS; cmds is not offered as a working alias.",
            "The root context records internal NativeMenuType::Main verification, not an independently authored root declaration; the current parse_config entry point creates menu/item/separator nodes.",
            "Modify/remove parse their own property switch rather than verify(menu). Removal parses but discards styling/movement values.",
            "Modify without CLSID requires a find/where selector and at least two effective properties; remove inserts visibility automatically.",
            "Property identifiers accept dotted and hyphenated separators through parse_property_ident; the inventory uses dotted canonical forms.",
            "Settings records come directly from SETTING initializers and preserve aliases pointing to the same runtime field. Expression does not imply evaluation during editing.",
            "Source mappings and parser acceptance need validation with the exported DLL; this inventory alone does not establish runtime semantic equivalence or UI completion.",
        ],
    }
    destination = ROOT / "docs/studio/property-coverage.json"
    destination.write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(properties)} menu-property forms and {len(settings)} theme/settings paths to {destination}")


if __name__ == "__main__":
    main()
