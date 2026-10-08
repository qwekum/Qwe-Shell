"""Adversarial native-preview contract checks.

The DLL path is explicit so a probe cannot accidentally exercise a stale
copy shipped beside a different Studio build.  All documents and brokered
read values are request-owned snapshots.  The temporary sentinel lets the
probe detect an unexpected native write without granting the preview worker
access to the host filesystem.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import tempfile
from typing import Any


class NativePreview:
    def __init__(self, dll_path: Path) -> None:
        self.dll_path = dll_path.resolve()
        if not self.dll_path.is_file():
            raise FileNotFoundError(f"Native preview DLL does not exist: {self.dll_path}")
        self.dll = ctypes.CDLL(str(self.dll_path))
        self.dll.shell_studio_preview.argtypes = (ctypes.c_wchar_p, ctypes.c_size_t)
        self.dll.shell_studio_preview.restype = ctypes.c_void_p
        self.dll.shell_studio_free.argtypes = (ctypes.c_void_p,)
        self.dll.shell_studio_free.restype = None

    def query(self, request: dict[str, Any]) -> dict[str, Any]:
        # ensure_ascii makes the byte/code-unit count unambiguous for the
        # native UTF-16 entry point while preserving the JSON value semantics.
        payload = json.dumps(request, ensure_ascii=True, separators=(",", ":"))
        pointer = self.dll.shell_studio_preview(payload, len(payload))
        if not pointer:
            raise AssertionError("shell_studio_preview returned a null result")
        try:
            return json.loads(ctypes.string_at(pointer).decode("utf-8"))
        finally:
            self.dll.shell_studio_free(pointer)


def describe(label: str, result: dict[str, Any]) -> str:
    return f"{label}: {json.dumps(result, sort_keys=True)}"


def expect_available(
    preview: NativePreview,
    cases: list[dict[str, Any]],
    label: str,
    expression: str,
    expected: Any,
    **request: Any,
) -> dict[str, Any]:
    result = preview.query({"operation": "evaluate", "expression": expression, **request})
    if not result.get("available") or result.get("value") != expected:
        raise AssertionError(describe(label, result) + f"; expected value {expected!r}")
    cases.append({"label": label, "result": result})
    return result


def expect_unavailable(
    preview: NativePreview,
    cases: list[dict[str, Any]],
    label: str,
    expression: str,
    **request: Any,
) -> dict[str, Any]:
    result = preview.query({"operation": "evaluate", "expression": expression, **request})
    if result.get("available") is not False or not result.get("diagnostics"):
        raise AssertionError(describe(label, result) + "; expected a diagnostic unavailable result")
    cases.append({"label": label, "result": result})
    return result


def diagnostic_codes(result: dict[str, Any]) -> set[str]:
    return {str(item.get("code")) for item in result.get("diagnostics", [])}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dll", type=Path, help="Explicit ShellStudio.Language.dll to probe")
    parser.add_argument(
        "--evidence",
        type=Path,
        help="Optional JSON path for the request results and sentinel hashes",
    )
    args = parser.parse_args()
    preview = NativePreview(args.dll)
    cases: list[dict[str, Any]] = []

    with tempfile.TemporaryDirectory(prefix="shellstudio-preview-adversarial-") as directory:
        temp_root = Path(directory)
        root = temp_root / "root.nss"
        child = temp_root / "child.nss"
        grandchild = temp_root / "grandchild.nss"
        sentinel = temp_root / "preview-sentinel.txt"
        sentinel_before = b"sentinel-before\n"
        sentinel.write_bytes(sentinel_before)

        # The imported value is evaluated through two import edges, a native
        # nested string call, and an explicitly supplied environment value.
        root_source = "import 'child.nss'\n"
        child_source = "import 'grandchild.nss'\n$child_value = str.upper(deep_value)\n$looped = for(index=0,index<4,if(index==2,'X',index))\n$unsafe = str.upper(io.file.write('preview-sentinel.txt','CHANGED'))\n"
        grandchild_source = "$deep_value = 'deep-%AUDIT_PREVIEW_ENV%'\n$missing_value = 'missing-%AUDIT_PREVIEW_UNSET_7F9C%'\n"
        documents = [
            {"path": str(root), "text": root_source},
            {"path": str(child), "text": child_source},
            {"path": str(grandchild), "text": grandchild_source},
        ]
        common = {
            "rootPath": str(root),
            "documents": documents,
            "environment": {"AUDIT_PREVIEW_ENV": "supplied"},
        }

        for effect in ("sel.tojson", "sel.tofile", "cmd"):
            expect_unavailable(preview, cases, "supplied facts cannot disguise " + effect,
                               effect + "()", facts={effect: "synthetic-success"})

        imported = expect_available(
            preview,
            cases,
            "transitive import + interpolation + nested call",
            "child_value",
            "DEEP-SUPPLIED",
            **common,
        )
        dependency_hashes = {
            str(document["path"]): hashlib.sha256(str(document["text"]).encode("utf-8")).hexdigest()
            for document in documents
        }
        actual_dependencies = {
            str(item.get("path")): str(item.get("sha256Utf8"))
            for item in imported.get("dependencies", [])
        }
        if actual_dependencies != dependency_hashes:
            raise AssertionError(
                f"transitive dependencies were not fingerprinted exactly: {actual_dependencies!r}"
            )

        expect_available(
            preview,
            cases,
            "imported interpolation value",
            "deep_value",
            "deep-supplied",
            **common,
        )
        missing = expect_unavailable(
            preview,
            cases,
            "unsupplied environment remains unavailable through import",
            "missing_value",
            **common,
        )
        if "PREVIEW_ENVIRONMENT" not in diagnostic_codes(missing):
            raise AssertionError(describe("missing environment diagnostic", missing))

        expect_available(
            preview,
            cases,
            "nested pure calls",
            "str.upper(str.lower('MiXeD'))",
            "MIXED",
            rootPath=str(root),
            source="",
        )
        expect_available(
            preview,
            cases,
            "lazy branch does not evaluate side effect",
            "if(false, io.file.write('preview-sentinel.txt','CHANGED'), 'safe')",
            "safe",
            rootPath=str(root),
            source="",
        )

        expect_available(
            preview,
            cases,
            "bounded loop",
            "for(index=0,index<4,if(index==2,'X',index))",
            "01X3",
            rootPath=str(root),
            source="",
        )
        loop_limit = expect_unavailable(
            preview,
            cases,
            "constant-condition loop hits preview limit",
            "for(index=0,1,'x')",
            rootPath=str(root),
            source="",
        )
        if "PREVIEW_LIMIT" not in diagnostic_codes(loop_limit):
            raise AssertionError(describe("loop limit diagnostic", loop_limit))

        # Brokered reads are request values.  A nested call may consume a
        # supplied read, but the real sentinel must remain byte-identical.
        read_request = {
            "rootPath": str(root),
            "source": "",
            "reads": [
                {"function": "io.file.exists", "arguments": ["preview-sentinel.txt"], "value": True},
                {"function": "io.file.read", "arguments": ["preview-sentinel.txt"], "value": "snapshot-value"},
            ],
        }
        expect_available(
            preview,
            cases,
            "brokered exists read",
            "io.file.exists('preview-sentinel.txt')",
            True,
            **read_request,
        )
        expect_available(
            preview,
            cases,
            "nested brokered read",
            "str.upper(io.file.read('preview-sentinel.txt'))",
            "SNAPSHOT-VALUE",
            **read_request,
        )
        mismatched = expect_unavailable(
            preview,
            cases,
            "unlisted read is unavailable",
            "io.file.read('other.txt')",
            **read_request,
        )
        if "PREVIEW_UNAVAILABLE" not in diagnostic_codes(mismatched):
            raise AssertionError(describe("unlisted read diagnostic", mismatched))

        # The imported unsafe value exercises an indirect write path.  It must
        # fail closed before touching the sentinel.
        unsafe = expect_unavailable(
            preview,
            cases,
            "transitive imported write remains unavailable",
            "unsafe",
            **common,
        )
        if "PREVIEW_UNAVAILABLE" not in diagnostic_codes(unsafe):
            raise AssertionError(describe("unsafe write diagnostic", unsafe))
        if sentinel.read_bytes() != sentinel_before:
            raise AssertionError("preview evaluation modified the sentinel file")

        evidence = {
            "version": 1,
            "dll": str(preview.dll_path),
            "dllSha256": hashlib.sha256(preview.dll_path.read_bytes()).hexdigest(),
            "caseCount": len(cases),
            "cases": cases,
            "sentinel": {
                "sha256Before": hashlib.sha256(sentinel_before).hexdigest(),
                "sha256After": hashlib.sha256(sentinel.read_bytes()).hexdigest(),
                "unchanged": sentinel.read_bytes() == sentinel_before,
            },
        }
        if args.evidence:
            args.evidence.parent.mkdir(parents=True, exist_ok=True)
            args.evidence.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")

        print(
            f"PASS {len(cases)} native preview adversarial checks; "
            f"sentinel unchanged; DLL SHA256 {evidence['dllSha256']}"
        )


if __name__ == "__main__":
    main()
