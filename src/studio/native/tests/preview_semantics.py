"""Contract tests for the native preview export; run against an explicit DLL."""
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
    count = 0

    def query(expression, source="", **extra):
        request = dict(operation="evaluate", expression=expression, source=source)
        request.update(extra)
        data = json.dumps(request, ensure_ascii=True)
        pointer = dll.shell_studio_preview(data, len(data))
        assert pointer, "missing native result"
        try:
            return json.loads(ctypes.string_at(pointer).decode("utf-8"))
        finally:
            dll.shell_studio_free(pointer)

    def available(expression, expected, source="", **extra):
        nonlocal count
        result = query(expression, source, **extra)
        assert result.get("available"), (expression, result)
        assert result.get("value") == expected, (expression, expected, result)
        count += 1

    def unavailable(expression, source="", **extra):
        nonlocal count
        result = query(expression, source, **extra)
        assert result.get("available") is False and result.get("diagnostics"), (expression, result)
        count += 1

    available("2 + 3 * 4", 14)
    available("answer + 1", 43, "$answer = 42")
    fingerprinted = query("answer", "$answer = 42")
    assert fingerprinted["dependencies"][0]["sha256Utf8"] == hashlib.sha256(b"$answer = 42").hexdigest(), fingerprinted
    count += 1
    available("str.upper('hello')", "HELLO")
    available("false && cmd('never-run')", 0)
    available("if(true, 'yes', cmd('never-run'))", "yes")
    unavailable("if()")
    unavailable("if(true,1,2,3)")
    unavailable("0", "item(title='Unsupported' cmd=command.random)")
    for alias in ("package", "appx", "uwp"):
        unavailable("0", f"item(title={alias}.title('missing-package'))")
    traced_expression = "if(true, str.upper('yes'), cmd('never-run'))"
    traced = query(traced_expression, trace=True)
    assert traced["available"] and traced["value"] == "YES" and traced["trace"], traced
    assert all(step["start"] < traced_expression.index("cmd(") for step in traced["trace"]), traced
    count += 1
    available("view.small", 2)
    spaced_expression = "2 + 3 /* trailing trivia */  "
    spaced = query(spaced_expression, trace=True)
    assert spaced["available"] and spaced["value"] == 5, spaced
    assert all(step["start"] + step["length"] <= 5 for step in spaced["trace"]), spaced
    count += 1
    available("path.ext('example.txt')", ".txt")
    available("path.file.ext('example.txt')", ".txt")
    available("path.ext('folder.name/example')", "")
    available("path.ext('folder/.hidden')", "")
    available("path.ext('folder/Unicode-\u03bb.tar.gz')", ".gz")
    available("path.root('')", "")
    available("path.root('C')", "")
    available("path.root('C:')", "")
    available("path.isclsid('')", False)
    available("path.isclsid(':')", False)
    available("path.isclsid('::')", False)
    unavailable("color.accent")
    available("color.accent", 123, facts={"color.accent": 123})
    unavailable("theme.isdark")
    available("theme.isdark", 1, context={"dpi": 144, "themeMode": 1})
    selected = dict(kind="file", paths=[r"C:\Preview\one.txt", r"C:\Preview\two.txt"], parentPath=r"C:\Preview")
    available("sel.count", 2, selection=selected)
    unavailable("sel.index()", selection=selected)
    unavailable("sel.index(0,0,0)", selection=selected)
    available("sel.paths", r"C:\Preview\one.txt C:\Preview\two.txt", selection=selected)
    available("sel.parent", r"C:\Preview", selection=selected)
    available("sel.mode", 2, selection=selected)
    available("sel.mode", 3, selection=dict(selected, paths=[r"C:\Preview\one.txt", r"C:\Preview\two.png"]))
    unavailable("sel.tojson()", selection=selected)
    unavailable("sel.tofile()", selection=selected)
    unavailable("sel.lnktarget", selection=selected)
    unavailable("sel.short", selection=selected)
    available("foreach($loopitem,sel.names,'x')", "xx", selection=selected)
    unavailable("foreach($loopitem,sel.shorts,'x')", selection=selected)
    unavailable("foreach($loopitem,sel.names,'x')")
    available("[1, 2 + 3]", [1, 5])
    available("'%PREVIEW_TEST%'", "supplied", environment={"PREVIEW_TEST": "supplied"})
    unavailable("'%PREVIEW_TEST%'")
    available("io.file.exists('preview.txt')", True,
              reads=[dict(function="io.file.exists", arguments=["preview.txt"], value=True)])
    available("io.file.read('preview.txt')", "snapshot value",
              reads=[dict(function="io.file.read", arguments=["preview.txt"], value="snapshot value")])
    unavailable("io.file.read('other.txt')",
                reads=[dict(function="io.file.read", arguments=["preview.txt"], value="snapshot value")])
    unavailable("cmd('never-run')")
    unavailable("str.res('never-load')")
    unavailable("color.box()")
    unavailable("unknown_value")
    unavailable("bad", "$bad = cmd('never-run')")
    unavailable("str.upper(bad)", "$bad = cmd('never-run')")
    unavailable("recursive", "$recursive = recursive")
    unavailable("reg.get('HKCU\\Software\\PreviewTest','value')")
    available("reg.exists('HKCU\\Software\\PreviewTest')", True,
              reads=[dict(function="reg.exists", arguments=[r"HKCU\Software\PreviewTest"], value=True)])
    available("reg.exists('HKCU\\Software\\PreviewTest','value')", True,
              reads=[dict(function="reg.exists", arguments=[r"HKCU\Software\PreviewTest", "value"], value=True)])
    available("reg.get('HKCU\\Software\\PreviewTest','value')", "snapshot value",
              reads=[dict(function="reg.get", arguments=[r"HKCU\Software\PreviewTest", "value"], value="snapshot value")])
    available("reg('HKCU\\Software\\PreviewTest')", "default snapshot",
              reads=[dict(function="reg.get", arguments=[r"HKCU\Software\PreviewTest"], value="default snapshot")])
    available("reg('HKCU\\Software\\PreviewTest','value')", "snapshot value",
              reads=[dict(function="reg.get", arguments=[r"HKCU\Software\PreviewTest", "value"], value="snapshot value")])
    # Unused command expressions remain inert during analysis/evaluation.
    available("1", 1, "item(title='safe' cmd=cmd('never-run'))")
    available("1", 1, "item(title='safe' id=123)")
    available("1", 1, "item(title='safe' cmds { cmd='never-run' })")
    unavailable("1", "remove(find='x' title='discarded')")
    unavailable("1", "remove(find='x' sep)")
    root = str(Path.cwd() / "preview-root.nss")
    child = str(Path.cwd() / "preview-child.nss")
    prefix = "$name='preview-child.nss'\nimport name\nimport 'not-loaded.nss'"
    available("name", "preview-child.nss", prefix, rootPath=root,
              resolveAt={"filePath": root, "position": prefix.index("import"), "occurrenceIndex": 0})
    prefix_scope = "$x=1\nmenu(title='scope') { $x=2\nimport 'not-loaded.nss' }"
    available("x", 2, prefix_scope, rootPath=root,
              resolveAt={"filePath": root, "position": prefix_scope.index("import"), "occurrenceIndex": 0})
    repeated = "menu(title='one') { $x=1\nimport 'preview-child.nss' }\nmenu(title='two') { $x=2\nimport 'preview-child.nss' }"
    available("x", 2, rootPath=root, documents=[
        {"path": root, "text": repeated}, {"path": child, "text": "item(title=x)"}],
        resolveAt={"filePath": child, "position": 0, "occurrenceIndex": 1})
    available("imported", "child", rootPath=root, documents=[
        {"path": root, "text": "import 'preview-child.nss'"},
        {"path": child, "text": "$imported = 'child'"},
    ])
    unavailable("1", rootPath=root, documents=[
        {"path": root, "text": "import 'preview-child.nss'"},
        {"path": child, "text": "import 'preview-root.nss'"},
    ])
    scope_source = "$x=1\nmenu(title='local') { $x=2\nitem(title=x) }\nitem(title=x)"
    available("x", 2, scope_source, rootPath=root, filePath=root, position=scope_source.index("item(title=x)"))
    available("x", 1, scope_source, rootPath=root, filePath=root, position=len(scope_source) - 2)
    print(f"PASS {count} native preview semantic checks")


if __name__ == "__main__":
    main()
