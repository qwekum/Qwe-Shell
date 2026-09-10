"""Extract authored identifier paths from nested verifier switch branches.

This extracts candidates, not runtime acceptance. The caller must validate each
candidate with the same native grammar and record dynamic/default namespaces.
Comments and strings are blanked before structural scanning. Original offsets
are retained for source references.
"""
from itertools import product
from pathlib import Path
import re


def hash_name(text):
    value = 5381
    for character in text.lower():
        value = (value * 33 + ord(character)) & 0xFFFFFFFF
    return value


def extract(root, documented_names):
    parser = root / "src/dll/src/Parser"
    source = (parser / "Verification.cpp").read_text(encoding="utf-8-sig")
    clean = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|L?"(?:\\.|[^"\\])*"|L?\'(?:\\.|[^\'\\])*\'',
                   lambda match: "".join("\n" if char == "\n" else " " for char in match[0]), source)
    hashes = dict((name, int(value, 16)) for name, value in re.findall(
        r"constexpr auto (\w+) = (0x[0-9a-fA-F]+)U?;", (parser / "IdentHash.h").read_text(encoding="utf-8-sig")))
    candidates = {part for name in documented_names for part in name.split(".")}
    for name in hashes:
        words = name.lower().split("_")
        for start in range(1, len(words)):
            for separator in ("", "_", "-"):
                candidates.add(separator.join(words[start:]))
    by_hash = {}
    for name in candidates:
        if re.fullmatch(r"[a-z_][a-z_0-9]*", name):
            by_hash.setdefault(hash_name(name), set()).add(name)

    def spelling(symbol):
        return sorted(by_hash.get(hashes.get(symbol), []))

    pairs, stack = {}, []
    for at, character in enumerate(clean):
        if character == "{":
            stack.append(at)
        elif character == "}" and stack:
            pairs[stack.pop()] = at
    switches = []
    for match in re.finditer(r"switch\s*\(\s*id\[(\d+)\]\s*\)\s*\{", clean):
        opening = match.end() - 1
        if opening in pairs:
            switches.append({"start": match.start(), "open": opening, "end": pairs[opening], "index": int(match[1])})
    main = max((switch for switch in switches if switch["index"] == 0), key=lambda switch: switch["end"] - switch["start"])
    records, unmapped = {}, []

    def labels(switch):
        depth, at, result = 0, switch["open"] + 1, []
        while at < switch["end"]:
            if clean[at] == "{":
                at = pairs[at] + 1
                continue
            match = re.match(r"(?:case\s+(\w+)|default)\s*:", clean[at:])
            if match:
                result.append((at, at + match.end(), match[1]))
                at += match.end()
            else:
                at += 1
        return result

    def visit(switch, prefixes):
        entries = labels(switch)
        index = 0
        while index < len(entries):
            at, body, symbol = entries[index]
            symbols = [] if symbol is None else [symbol]
            while index + 1 < len(entries) and not clean[body:entries[index + 1][0]].strip():
                index += 1
                _, body, symbol = entries[index]
                if symbol is not None:
                    symbols.append(symbol)
            end = entries[index + 1][0] if index + 1 < len(entries) else switch["end"]
            paths = []
            for symbol in symbols:
                if symbol == "IDENT_ZERO":
                    paths.extend(prefixes)
                    continue
                names = spelling(symbol)
                if not names:
                    unmapped.append({"symbol": symbol, "line": source.count("\n", 0, at) + 1})
                for prefix, name in product(prefixes, names):
                    if len(prefix) == switch["index"]:
                        paths.append((*prefix, name))
            line = source.count("\n", 0, at) + 1
            for path in paths:
                if path:
                    records.setdefault(".".join(path), set()).add(line)
            children = [child for child in switches if body <= child["start"] < end]
            direct = [child for child in children if not any(other["start"] < child["start"] < other["end"] for other in children)]
            for child in direct:
                visit(child, paths)
            index += 1

    visit(main, [()])
    # These tables are dispatched by loops rather than nested case labels.
    constants = (root / "src/dll/src/Expression/Constants.h").read_text(encoding="utf-8-sig")
    for table, roots in (("KeyTable", ("key", "keys")), ("ColorTable", ("color",))):
        definition = re.search(r'\b' + table + r'\[\]\s*=\s*\{([\s\S]*?)\n\s*\};', constants)
        if definition is None:
            raise RuntimeError("Missing source table " + table)
        at = source.index("for(auto &t : " + table + ")")
        line = source.count("\n", 0, at) + 1
        for symbol in re.findall(r'\{\s*(IDENT_\w+)\s*,', definition[1]):
            names = spelling(symbol)
            if not names:
                unmapped.append({"symbol": symbol, "table": table})
            for prefix, name in product(roots, names):
                records.setdefault(prefix + '.' + name, set()).add(line)
    # Finite branches expressed as comparisons instead of switch statements.
    theme_line = source.count('\n', 0, source.index('if(id.equals(2, { IDENT_OPACITY')) + 1
    extra = {'theme.background.opacity', 'theme.background.effect', 'theme.mode.system'}
    for member in ('back', 'text'):
        extra.add('theme.item.' + member)
        for state in ('normal', 'select'):
            extra.add('theme.item.' + member + '.' + state)
            extra.add('theme.item.' + member + '.' + state + '.disable')
    for name in extra:
        records.setdefault(name, set()).add(theme_line)
    return {name: sorted(lines) for name, lines in sorted(records.items())}, unmapped


if __name__ == "__main__":
    import json
    root = Path(__file__).resolve().parents[3]
    inventory = json.loads((root / "docs/studio/function-coverage.json").read_text())
    names = [entry["name"] for entry in inventory["functions"] + inventory["values"]]
    records, unmapped = extract(root, names)
    print(json.dumps({"candidateCount": len(records), "unmapped": unmapped, "candidates": records}, indent=2))
