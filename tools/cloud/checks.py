"""Offline syntax/structure gates. This does not build or run Shell Studio."""
from __future__ import annotations

import argparse
import ast
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import stat
import xml.etree.ElementTree as ET

BASE = "65df0e4f2ad4590c9964b9ab810bc094797b81af"
ORIGINS = {
    "https://github.com/qwekum/Qwe-Shell.git",
    "https://github.com/qwekum/Qwe-Shell",
    "git@github.com:qwekum/Qwe-Shell.git",
}
ROOT = Path(__file__).resolve().parents[2]
REQUIRED = (
    "README.md", ".gitattributes", "src/studio/build.ps1", "src/Shell.sln",
    "src/studio/ShellStudio/ShellStudio.csproj",
    "src/studio/ShellStudio.Core/NativeLanguage.cs",
    "src/studio/ShellStudio.Tests/Program.cs",
    "docs/studio/build-and-run.md", "docs/studio/function-coverage.json",
    "docs/studio/property-coverage.json", "docs/studio/cloud-development.md",
    "tools/cloud/install.sh", "tools/cloud/start.md", "tools/cloud/test_checks.py",
    "tools/cloud/checks.py", "tools/cloud/run_tests.py",
)
HELPERS = ("checks.py", "run_tests.py", "test_checks.py")
MIN_GIT = (2, 51, 1)


def git_environment() -> dict:
    # Ignore *all* ambient Git overrides, including config injection and tracing.
    # These controls are process-local; no repository/user configuration changes.
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith("GIT_")}
    env.update(GIT_OPTIONAL_LOCKS="0", GIT_NO_LAZY_FETCH="1",
               GIT_NO_REPLACE_OBJECTS="1", GIT_TERMINAL_PROMPT="0",
               GIT_ALLOW_PROTOCOL="", GIT_CONFIG_NOSYSTEM="1",
               GIT_CONFIG_GLOBAL=os.devnull)
    return env


def git(root: Path, *args: str, raw: bool = False) -> str | bytes:
    # No automatic safe.directory, credential, fetch, or configuration changes.
    result = subprocess.run(["git", "--no-pager", "--no-optional-locks",
                            "--no-lazy-fetch", "--no-replace-objects",
                            "-c", "core.fsmonitor=false", "-c", "submodule.recurse=false",
                            "-C", str(root), *args], check=True,
                            capture_output=True, encoding=None if raw else "utf-8", timeout=30,
                            env=git_environment())
    if raw:
        return result.stdout
    return result.stdout.strip() if not ({"-z", "--null"} & set(args)) else result.stdout


def verify_git_binding(root: Path) -> Path:
    marker = root / ".git"
    mode = marker.lstat().st_mode
    if stat.S_ISDIR(mode):
        directory = marker.resolve()
        common = directory
    elif stat.S_ISREG(mode):
        text = marker.read_text(encoding="utf-8").strip()
        if not text.startswith("gitdir: ") or "\n" in text:
            raise ValueError("Unsupported .git file; require an ordinary or linked worktree.")
        directory = (root / text[8:]).resolve()
        # Accept only a genuine linked-worktree binding, not a separate external
        # Git directory silently paired with this filesystem tree.
        common = (directory / (directory / "commondir").read_text().strip()).resolve()
        # Relative backpointers are based at their administrative directory.
        back = (directory / (directory / "gitdir").read_text().strip()).resolve()
        if directory.parent != common / "worktrees" or back != marker.resolve():
            raise ValueError("Linked-worktree .git binding is inconsistent.")
    else:
        raise ValueError("The .git marker must be a directory or regular linked-worktree file.")
    if Path(git(root, "rev-parse", "--absolute-git-dir")).resolve() != directory:
        raise ValueError("Git metadata differs from this checkout's .git binding.")
    if (root / git(root, "rev-parse", "--git-common-dir")).resolve() != common:
        raise ValueError("Git common directory differs from this checkout's binding.")
    if (root / git(root, "rev-parse", "--git-path", "index")).resolve() != directory / "index":
        raise ValueError("Git index differs from this checkout's binding.")
    for name in ("objects/info/alternates", "info/grafts"):
        path = common / name
        if path.exists() and path.read_bytes().strip():
            raise ValueError(f"Unsupported external object storage or grafts: {name}")
    return directory


def verify_git_policy(root: Path) -> str:
    version = git(root, "--version")
    match = re.match(r"git version (\d+)\.(\d+)\.(\d+)(?:\D|$)", version)
    if not match or tuple(map(int, match.groups())) < MIN_GIT:
        raise ValueError("Git 2.51.1+ is required for the reviewed offline controls.")
    # Reading config is non-executing. Refuse configured content filters before
    # status/diff could hash a working file and launch a clean/process helper.
    for item in git(root, "config", "--null", "--list").split("\0"):
        key, _, value = item.partition("\n")
        if re.fullmatch(r"filter\..+\.(clean|smudge|process)", key) and value:
            raise ValueError("Configured content filters are unsupported in the static lane.")
        if key in ("core.sparsecheckout", "core.sparsecheckoutcone", "index.sparse"):
            if value.lower() not in ("false", "no", "off", "0"):
                raise ValueError("Sparse checkout/index states are unsupported; preserve them in place.")
    for entry in git(root, "ls-files", "-v", "-z").split("\0"):
        if entry and (entry[0].islower() or entry[0] == "S"):
            raise ValueError("assume-unchanged/skip-worktree index flags are unsupported; preserve them in place.")
    # Never recurse into initialized donor/library worktrees. The lane needs
    # only the superproject source; refusal leaves every submodule untouched.
    for entry in git(root, "ls-files", "--stage", "-z").split("\0"):
        if entry.startswith("160000 "):
            _, name = entry.split("\t", 1)
            if os.path.lexists(root / name / ".git"):
                raise ValueError("Initialized submodules are unsupported in the static lane.")
    return version


def verify_checkout(root: Path, expected: str, require_linux: bool,
                    require_clean: bool) -> dict:
    if sys.version_info < (3, 10):
        raise ValueError("Python 3.10+ is required; provision it in the environment image.")
    if require_linux and sys.platform != "linux":
        raise ValueError("Linux execution required; Linux checks are NOTRUN on this host.")
    if not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise ValueError("Supply the reviewed full lowercase commit SHA with --expected-head.")
    if root.resolve() != Path.cwd().resolve():
        raise ValueError("Run from this script's checkout root; do not use a sibling checkout.")
    git_version = verify_git_policy(root)
    directory = verify_git_binding(root)
    if Path(git(root, "rev-parse", "--show-toplevel")).resolve() != root.resolve():
        raise ValueError("Script location and Git checkout root differ.")
    if git(root, "remote", "get-url", "origin") not in ORIGINS:
        raise ValueError("Origin must identify qwekum/Qwe-Shell; refusing another repository.")
    head = git(root, "rev-parse", "HEAD")
    if head != expected:
        raise ValueError(f"Checkout changed: expected {expected}, found {head}. Review before updating the pin.")
    # Missing history is a blocker, never an excuse to skip the feature-line check.
    git(root, "merge-base", "--is-ancestor", BASE, head)
    status = git(root, "status", "--porcelain=v1", "--untracked-files=all",
                 "--ignore-submodules=all")
    if require_clean and status:
        raise ValueError("Installation requires a clean reviewed checkout; preserve dirty work in place.")
    for name in REQUIRED:
        read_source(root, name)
    if require_clean:
        # Executed helper bytes must equal the pinned commit, regardless of
        # timestamp/cache shortcuts. Git blob reads never apply content filters.
        for name in HELPERS:
            relative = "tools/cloud/" + name
            blob = git(root, "cat-file", "blob", f"{head}:{relative}", raw=True)
            if read_source(root, relative) != blob:
                raise ValueError(f"Clean installation helper differs from pinned commit: {relative}")
    return {"head": head, "featureBase": BASE, "dirty": bool(status),
            "host": sys.platform, "python": sys.version.split()[0],
            "git": git_version, "gitDirectory": str(directory),
            "submodules": "uninitialized/excluded", "ambientGitOverrides": "ignored"}


def read_source(root: Path, name: str) -> bytes:
    path = root / name
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError(f"Source escapes checkout: {name}")
    if not path.is_file():
        raise ValueError(f"Required source is missing: {name}")
    return path.read_bytes()


def check_sources(root: Path) -> dict:
    names = sorted(set(filter(None, git(root, "ls-files", "--cached", "--others",
                                        "--exclude-standard", "-z").split("\0"))))
    counts = {"pythonSyntax": 0, "studioJsonStructure": 0, "xmlSyntax": 0}
    for name in names:
        if name.startswith(("src/studio/", "tools/cloud/")) and name.endswith(".py"):
            ast.parse(read_source(root, name), filename=name)
            counts["pythonSyntax"] += 1
        elif name.startswith("docs/studio/") and name.endswith(".json"):
            data = json.loads(read_source(root, name))
            if not isinstance(data, dict) or data.get("version") != 1:
                raise ValueError(f"Expected a version 1 Studio inventory object: {name}")
            counts["studioJsonStructure"] += 1
        elif name.startswith(("src/studio/", "src/setup/wix/")) and name.endswith(
                (".csproj", ".vcxproj", ".wixproj", ".xaml")):
            ET.fromstring(read_source(root, name))
            counts["xmlSyntax"] += 1
    # Ensure an empty/sparse checkout cannot silently pass these gates.
    if not all(counts.values()):
        raise ValueError("Expected Python, JSON, and XML source groups are absent.")
    git(root, "diff", "--no-ext-diff", "--no-textconv", "--check", "HEAD", "--")
    return counts


def main() -> int:
    if not (sys.flags.isolated and sys.flags.no_site and sys.dont_write_bytecode):
        print("BLOCKED: Invoke Python with -I -S -B to isolate standard-library imports.", file=sys.stderr)
        return 1
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-head", required=True)
    parser.add_argument("--require-linux", action="store_true")
    parser.add_argument("--require-clean", action="store_true")
    args = parser.parse_args()
    try:
        identity = verify_checkout(ROOT, args.expected_head, args.require_linux, args.require_clean)
        counts = check_sources(ROOT)
    except (ValueError, OSError, SyntaxError, ET.ParseError, subprocess.SubprocessError) as error:
        detail = str(error)
        if isinstance(error, subprocess.CalledProcessError):
            detail = (error.stderr or "").strip() or detail
            if isinstance(detail, bytes):
                detail = detail.decode("utf-8", errors="replace")
        print(f"BLOCKED: {detail}", file=sys.stderr)
        return 1
    print(json.dumps({"scope": "offline-static-only", "checkout": identity,
                      "checks": counts, "result": "PASS",
                      "linuxStaticExecution": "PASS" if sys.platform == "linux" else "NOTRUN",
                      "linuxEnvironmentQualification": "NOTRUN",
                      "managedTests": "NOTRUN", "nativeBuild": "NOTRUN",
                      "windowsRuntime": "NOTRUN", "installer": "NOTRUN",
                      "inventoryProvenanceAndCoverage": "NOTRUN"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
