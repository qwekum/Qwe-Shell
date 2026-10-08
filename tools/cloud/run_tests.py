"""Run only the reviewed cloud helper regression module; never discover tests."""
import argparse
import importlib.util
import json
from pathlib import Path
import stat
import sys
import unittest

ALLOWED = frozenset(("checks.py", "run_tests.py", "test_checks.py", "install.sh", "start.md"))
EXPECTED_TESTS = 38
PREPARATION_TESTS = 20


def helper_paths(root: Path) -> dict:
    root = root.absolute()
    if root.resolve() != Path.cwd().resolve():
        raise ValueError("Run the bounded runner from its checkout root.")
    for directory in (root / "tools", root / "tools/cloud"):
        if not stat.S_ISDIR(directory.lstat().st_mode):
            raise ValueError("Helper directories must be real directories without symlinks.")
    paths = {}
    for path in (root / "tools/cloud").iterdir():
        if path.name not in ALLOWED or not stat.S_ISREG(path.lstat().st_mode):
            raise ValueError(f"Unexpected helper input or link: {path.name}; preserve and review it.")
        if not path.resolve().is_relative_to(root.resolve()):
            raise ValueError("Helper input escapes checkout.")
        paths[path.name] = path
    if set(paths) != ALLOWED:
        raise ValueError("The complete reviewed helper file set is required.")
    return paths


def load_source(name: str, path: Path):
    # Compile the exact validated source bytes, never a stale/unchecked .pyc.
    # Do not add the checkout or helper directory to sys.path.
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(path.read_bytes(), str(path), "exec"), module.__dict__)
    return module


def main() -> int:
    if not (sys.flags.isolated and sys.flags.no_site and sys.dont_write_bytecode):
        print("BLOCKED: Invoke Python with -I -S -B to isolate standard-library imports.", file=sys.stderr)
        return 1
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true",
                        help="Run only 20 mocked/pure-Python guards; no Git or child interpreters.")
    args = parser.parse_args()
    if not args.prepare_only and sys.platform != "linux":
        print("BLOCKED: Full helper suite requires Linux; use --prepare-only for limited preparation.", file=sys.stderr)
        return 1
    try:
        root = Path(__file__).absolute().parents[2]
        paths = helper_paths(root)  # Validate *all* entries before loading code.
        load_source("checks", paths["checks.py"])
        tests = load_source("cloud_helper_tests", paths["test_checks.py"])
        tests.runner = sys.modules[__name__]
        suite = unittest.defaultTestLoader.loadTestsFromModule(tests)
        if suite.countTestCases() != EXPECTED_TESTS:
            raise ValueError(f"Expected exactly {EXPECTED_TESTS} reviewed helper tests.")
        expected = EXPECTED_TESTS
        if args.prepare_only:
            suite = unittest.defaultTestLoader.loadTestsFromTestCase(tests.CloudChecksTests)
            expected = PREPARATION_TESTS
            if suite.countTestCases() != expected:
                raise ValueError(f"Expected exactly {expected} preparation tests.")
    except (OSError, ValueError, SyntaxError) as error:
        print(f"BLOCKED: {error}", file=sys.stderr)
        return 1
    result = unittest.TextTestRunner(verbosity=1, failfast=True).run(suite)
    if result.skipped or result.testsRun != expected:
        print("BLOCKED: Every reviewed helper test must execute; skips are not qualification.", file=sys.stderr)
        return 1
    passed = result.wasSuccessful()
    print(json.dumps({"scope": "helper-preparation-only" if args.prepare_only else "helper-regressions-only",
                      "result": "PASS" if passed else "FAIL", "host": sys.platform,
                      "testsRun": result.testsRun, "skips": len(result.skipped),
                      "fullLinuxHelperSuite": "NOTRUN" if args.prepare_only else ("PASS" if passed else "FAIL"),
                      "linuxEnvironmentQualification": "NOTRUN", "windowsRuntime": "NOTRUN"}))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
