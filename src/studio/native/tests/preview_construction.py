"""Integration contracts for unsaved native menu construction, using an explicit DLL."""
import base64
import ctypes
import hashlib
import json
from pathlib import Path
import sys


def main():
    dll = ctypes.CDLL(str(Path(sys.argv[1]).resolve()))
    dll.shell_studio_preview.argtypes = (ctypes.c_wchar_p, ctypes.c_size_t)
    dll.shell_studio_preview.restype = ctypes.c_void_p
    dll.shell_studio_free.argtypes = (ctypes.c_void_p,)
    root = str(Path(__file__).resolve().parent / "unsaved-preview.nss")
    count = 0

    def request(source, **extra):
        payload = dict(operation="render", source=source, rootPath=root, trace=True,
                       context=dict(dpi=96, themeMode=1), mode="standalone",
                       selection=dict(kind="file", paths=[r"C:\Preview\example.txt"], parentPath=r"C:\Preview"),
                       sampleMenu=[dict(id="open", title="Open"), dict(id="copy", title="Copy")])
        payload.update(extra)
        encoded = json.dumps(payload, ensure_ascii=True)
        pointer = dll.shell_studio_preview(encoded, len(encoded))
        assert pointer
        try:
            return json.loads(ctypes.string_at(pointer).decode("utf-8"))
        finally:
            dll.shell_studio_free(pointer)

    def rendered(source, **extra):
        nonlocal count
        result = request(source, **extra)
        assert result.get("available"), result
        frame = result["frame"]
        assert len(base64.b64decode(frame["pixels"], validate=True)) == frame["width"] * frame["height"] * 4
        assert len(frame["rows"]) == len(result["items"])
        count += 1
        return result

    result = rendered("item(title='Authored' cmd='never-run')")
    assert {row["title"] for row in result["items"]} == {"Open", "Copy", "Authored"}, result
    result = rendered("modify(find='Open' title='Changed')")
    assert [row["title"] for row in result["items"]] == ["Changed", "Copy"], result
    result = rendered("remove(find='Copy')")
    assert [row["title"] for row in result["items"]] == ["Open"], result
    result = rendered("item(title='Visible' where=sel.count==1) item(title='Hidden' where=false)")
    assert "Visible" in [row["title"] for row in result["items"]] and "Hidden" not in [row["title"] for row in result["items"]], result
    assert result["trace"] and all(entry["file"] == root and entry["length"] > 0 for entry in result["trace"]), result["trace"]
    visible = next(row for row in result["items"] if row["title"] == "Visible")
    assert any("where condition evaluated true" in line for line in visible["explanations"]), visible
    assert any(entry["state"] == "hidden" and "where condition evaluated false" in entry["reason"]
               for entry in result["decisions"]), result["decisions"]
    modified = rendered("modify(find='Open' title='Changed')")
    assert any("Applicable modification rule" in line for line in modified["items"][0]["explanations"]), modified["items"][0]

    # Unopened children must not evaluate their expressions. Opening the branch
    # produces an explicit denial; it must never run the child command expression.
    source = ("menu(title='Lazy') {\n"
              "  item(title=cmd('never-run') args='--sentinel')\n"
              "  item(title='No command' args='--unused')\n"
              "  $mutated='bad'\n"
              "}")
    result = rendered(source)
    popup = next(row for row in result["items"] if row["title"] == "Lazy")
    assert popup["popup"] and popup["childrenAvailable"], popup
    denied = request(source, submenuPath=[popup["id"]])
    assert denied.get("available") is False and denied.get("diagnostics"), denied
    count += 1

    # A nested item with arguments but no command remains constructible.  This
    # keeps the no-command case distinct from the denied command/mutation case
    # above and proves opening the branch does not invent an action.
    no_command_source = "menu(title='No command branch') { item(title='Label' args='--unused') }"
    result = rendered(no_command_source)
    popup = next(row for row in result["items"] if row["title"] == "No command branch")
    child = request(no_command_source, submenuPath=[popup["id"]])
    assert child.get("available") and [row["title"] for row in child["items"]] == ["Label"], child
    count += 1

    source = "menu(title='Stable') { item(title='Child') }"
    identities = [dict(filePath=root, start=0, id="studio-stable")]
    result = rendered(source, sourceIdentities=identities)
    popup = next(row for row in result["items"] if row["title"] == "Stable")
    changed = "// insertion\n" + source
    result = rendered(changed, sourceIdentities=[dict(identities[0], start=len("// insertion\n"))])
    assert popup["id"] == next(row["id"] for row in result["items"] if row["title"] == "Stable"), result
    child = rendered(changed, sourceIdentities=[dict(identities[0], start=len("// insertion\n"))], submenuPath=[popup["id"]])
    assert [row["title"] for row in child["items"]] == ["Child"], child

    captured = dict(original=[dict(id="owner", title="Provider", ownerDraw=True),
                              dict(id="lazy", title="Uncaptured", kind="menu", childrenCaptured=False)],
                    entries=[dict(title="Wrong final tree")])
    result = rendered("", mode="captured", capture=captured)
    assert result["items"][0]["disabled"] and not result["items"][1]["childrenAvailable"], result
    assert result["expectationTree"][0]["ownerDraw"] is True, result["expectationTree"]
    assert result["expectationTree"][1]["childrenCaptured"] is False, result["expectationTree"]
    assert any(item["code"] == "PREVIEW_OWNER_DRAW" for item in result["diagnostics"]), result
    duplicated = [dict(id="first", title="Duplicate", kind="menu", childrenAvailable=True, children=[dict(title="First child")]),
                  dict(id="second", title="Duplicate", kind="menu", childrenAvailable=True, children=[dict(title="Second child")])]
    result = rendered("", sampleMenu=duplicated, submenuPath=["second"])
    assert [row["title"] for row in result["items"]] == ["Second child"], result
    ambiguous = request("", sampleMenu=duplicated, submenuPath=["Duplicate"])
    assert ambiguous.get("available") is False and ambiguous.get("diagnostics"), ambiguous
    count += 1
    states = rendered("", sampleMenu=[dict(id="state", title="State", checked=True, radio=True, isDefault=True, keys="Ctrl+S")])
    assert {key: states["items"][0][key] for key in ("checked", "radio", "isDefault", "keys")} == {
        "checked": True, "radio": True, "isDefault": True, "keys": "Ctrl+S"}, states["items"]
    assert {key: states["expectationTree"][0][key] for key in ("checked", "radio", "isDefault", "keys")} == {
        "checked": True, "radio": True, "isDefault": True, "keys": "Ctrl+S"}, states["expectationTree"]
    image_bytes = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAEUlEQVR4nGP4z8DQwPCf4T8ADn0Dfur2k8AAAAAASUVORK5CYII=")
    resource = dict(path="C:/Preview/fixture.png", kind="png", format="png", status="available",
                    content=base64.b64encode(image_bytes).decode(), byteLength=len(image_bytes),
                    sha256=hashlib.sha256(image_bytes).hexdigest(), width=2, height=1)
    source = "item(title='Image' image='C:/Preview/fixture.png')"
    supplied = rendered(source, resources=[resource])
    missing = rendered(source)
    assert supplied["resourceDependencies"] and supplied["frame"]["pixels"] != missing["frame"]["pixels"], {
        "diagnostics": supplied["diagnostics"], "dependencies": supplied["resourceDependencies"],
        "problem": "Supplied image did not change rendered pixels"}
    assert any("image" in item.get("message", "").lower() for item in missing["diagnostics"]), missing["diagnostics"]
    background_source = "theme { background.image='C:/Preview/fixture.png' }"
    background = rendered(background_source, resources=[resource])
    plain = rendered("")
    assert background["frame"]["pixels"] != plain["frame"]["pixels"], "Supplied background image did not affect pixels"
    unavailable_background = request(background_source)
    assert not unavailable_background.get("available") and any(
        "image" in item.get("message", "").lower() for item in unavailable_background["diagnostics"]), unavailable_background
    print(f"PASS {count} native menu construction checks; offscreen only")


if __name__ == "__main__":
    main()
