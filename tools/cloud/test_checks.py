"""Small regression suite for refusal paths and non-executing syntax gates."""
import contextlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import checks


class CloudChecksTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.previous = Path.cwd()
        os.chdir(self.root)
        self.addCleanup(os.chdir, self.previous)
        for name in checks.REQUIRED:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("", encoding="utf-8")
        (self.root / ".git").mkdir()
        self.status = ""
        self.head = checks.BASE
        self.origin = "https://github.com/qwekum/Qwe-Shell.git"

    def git(self, root, *args, raw=False):
        if raw:
            return b""
        if args == ("--version",):
            return "git version 2.51.1"
        if args in (("rev-parse", "--absolute-git-dir"),
                    ("rev-parse", "--git-common-dir")):
            return str(self.root / ".git")
        if args == ("rev-parse", "--git-path", "index"):
            return str(self.root / ".git/index")
        if args == ("rev-parse", "--show-toplevel"):
            return str(self.root)
        if args == ("remote", "get-url", "origin"):
            return self.origin
        if args == ("rev-parse", "HEAD"):
            return self.head
        if args[0] == "status":
            return self.status
        return ""

    def verify(self, **kwargs):
        with patch.object(checks, "git", side_effect=self.git):
            return checks.verify_checkout(self.root, checks.BASE, False, **kwargs)

    def test_exact_checkout_passes_without_mutation(self):
        self.assertEqual(self.verify(require_clean=True)["head"], checks.BASE)
        self.assertEqual((self.root / "README.md").read_bytes(), b"")

    def test_dirty_install_is_rejected_but_static_development_is_allowed(self):
        self.status = " M README.md"
        with self.assertRaisesRegex(ValueError, "clean"):
            self.verify(require_clean=True)
        self.assertTrue(self.verify(require_clean=False)["dirty"])

    def test_wrong_origin_rejected(self):
        self.origin = "https://github.com/moudey/Shell.git"
        with self.assertRaisesRegex(ValueError, "Origin"):
            self.verify(require_clean=False)

    def test_moved_head_rejected(self):
        self.head = "a" * 40
        with self.assertRaisesRegex(ValueError, "Checkout changed"):
            self.verify(require_clean=False)

    def test_missing_feature_history_rejected(self):
        original = self.git
        def without_history(root, *args):
            if args[0] == "merge-base":
                raise checks.subprocess.CalledProcessError(1, ["git", *args], stderr="missing feature base")
            return original(root, *args)
        with patch.object(checks, "git", side_effect=without_history):
            with self.assertRaises(checks.subprocess.CalledProcessError):
                checks.verify_checkout(self.root, checks.BASE, False, False)

    def test_wrong_cwd_and_missing_source_rejected(self):
        with self.assertRaisesRegex(ValueError, "checkout root"):
            checks.verify_checkout(self.root / "src", checks.BASE, False, False)
        (self.root / "README.md").unlink()
        with self.assertRaisesRegex(ValueError, "missing"):
            self.verify(require_clean=False)

    def test_revision_is_not_a_symbolic_ref(self):
        with self.assertRaisesRegex(ValueError, "full lowercase"):
            checks.verify_checkout(self.root, "main", False, False)

    def test_windows_cannot_claim_linux_validation(self):
        with patch.object(checks.sys, "platform", "win32"):
            with self.assertRaisesRegex(ValueError, "NOTRUN"):
                checks.verify_checkout(self.root, checks.BASE, True, False)

    def test_read_cannot_escape_checkout(self):
        with self.assertRaisesRegex(ValueError, "escapes"):
            checks.read_source(self.root, "../external.py")

    def syntax_fixture(self, python="raise RuntimeError('must not execute')\n", json='{"version":1}', xml="<Project />"):
        sources = {"tools/cloud/fixture.py": python,
                   "docs/studio/fixture.json": json,
                   "src/studio/fixture.csproj": xml}
        for name, value in sources.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(value, encoding="utf-8")
        return patch.object(checks, "git", side_effect=lambda root, *args:
                            "\0".join(sources) + "\0" if args[0] == "ls-files" else "")

    def test_python_is_parsed_without_running_code(self):
        with self.syntax_fixture():
            self.assertEqual(checks.check_sources(self.root),
                             {"pythonSyntax": 1, "studioJsonStructure": 1, "xmlSyntax": 1})

    def test_bad_python_json_and_xml_fail(self):
        for options, error in (({"python": "def :"}, SyntaxError),
                               ({"json": "{"}, ValueError),
                               ({"xml": "<Project>"}, checks.ET.ParseError)):
            with self.subTest(options=options), self.syntax_fixture(**options):
                with self.assertRaises(error):
                    checks.check_sources(self.root)

    def test_empty_source_groups_fail(self):
        with patch.object(checks, "git", return_value=""):
            with self.assertRaisesRegex(ValueError, "absent"):
                checks.check_sources(self.root)

    def test_git_root_mismatch_is_refused(self):
        original = self.git
        def redirected(root, *args, **kwargs):
            return str(self.root / "other") if args == ("rev-parse", "--show-toplevel") else original(root, *args, **kwargs)
        with patch.object(checks, "git", side_effect=redirected):
            with self.assertRaisesRegex(ValueError, "checkout root differ"):
                checks.verify_checkout(self.root, checks.BASE, False, False)

    def test_git_metadata_mismatch_is_refused(self):
        original = self.git
        def redirected(root, *args, **kwargs):
            return str(self.root / "other") if args == ("rev-parse", "--absolute-git-dir") else original(root, *args, **kwargs)
        with patch.object(checks, "git", side_effect=redirected):
            with self.assertRaisesRegex(ValueError, "metadata differs"):
                checks.verify_checkout(self.root, checks.BASE, False, False)
        # Owned plain-file layouts exercise R1 without launching Git. The real
        # --relative-paths fixture remains a separate admitted Linux gate.
        common = self.root / ".git"
        for mode in ("absolute", "relative", "mismatched-relative"):
            with self.subTest(binding=mode):
                linked = self.root / ("linked-" + mode)
                linked.mkdir()
                directory = common / "worktrees" / linked.name
                directory.mkdir(parents=True)
                marker = linked / ".git"
                target = str(directory) if mode == "absolute" else os.path.relpath(directory, linked)
                marker.write_text("gitdir: " + target + "\n", encoding="utf-8")
                (directory / "commondir").write_text("../..\n", encoding="utf-8")
                pointer = marker if mode != "mismatched-relative" else self.root / "unrelated/.git"
                back = str(pointer) if mode == "absolute" else os.path.relpath(pointer, directory)
                (directory / "gitdir").write_text(back + "\n", encoding="utf-8")
                answers = {("rev-parse", "--absolute-git-dir"): str(directory),
                           ("rev-parse", "--git-common-dir"): os.path.relpath(common, linked),
                           ("rev-parse", "--git-path", "index"): str(directory / "index")}
                before = snapshot(self.root)
                os.chdir(linked)
                try:
                    with patch.object(checks, "git", side_effect=lambda root, *args: answers[args]) as query:
                        if mode == "mismatched-relative":
                            with self.assertRaisesRegex(ValueError, "binding is inconsistent"):
                                checks.verify_git_binding(linked)
                            query.assert_not_called()
                        else:
                            self.assertEqual(checks.verify_git_binding(linked), directory)
                    self.assertEqual(snapshot(self.root), before)
                finally:
                    os.chdir(self.root)

    def test_clean_helper_bytes_must_match_pinned_commit(self):
        (self.root / "tools/cloud/test_checks.py").write_bytes(b"\n")
        with self.assertRaisesRegex(ValueError, "differs from pinned commit"):
            self.verify(require_clean=True)

    def test_receipt_labels_match_host_without_qualification_claim(self):
        for host, expected in (("linux", "PASS"), ("win32", "NOTRUN")):
            output = io.StringIO()
            with patch.object(checks.sys, "argv", ["checks.py", "--expected-head", checks.BASE]), \
                 patch.object(checks.sys, "platform", host), \
                 patch.object(checks, "verify_checkout", return_value={"head": checks.BASE, "dirty": True}), \
                 patch.object(checks, "check_sources", return_value={"pythonSyntax": 1}), \
                 contextlib.redirect_stdout(output):
                self.assertEqual(checks.main(), 0)
            receipt = json.loads(output.getvalue())
            self.assertEqual(receipt["linuxStaticExecution"], expected)
            self.assertEqual(receipt["scope"], "offline-static-only")
            for key in ("linuxEnvironmentQualification", "managedTests", "nativeBuild", "windowsRuntime",
                        "installer", "inventoryProvenanceAndCoverage"):
                self.assertEqual(receipt[key], "NOTRUN")

    def test_main_failure_returns_nonzero_without_pass_receipt(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.object(checks.sys, "argv", ["checks.py", "--expected-head", checks.BASE]), \
             patch.object(checks, "verify_checkout", side_effect=ValueError("fixture refusal")), \
             contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            self.assertEqual(checks.main(), 1)
        self.assertEqual(stdout.getvalue(), "")
        self.assertIn("BLOCKED: fixture refusal", stderr.getvalue())

    def test_old_git_is_refused(self):
        with patch.object(checks, "git", return_value="git version 2.35.1"):
            with self.assertRaisesRegex(ValueError, "Git 2.51.1"):
                checks.verify_git_policy(self.root)

    def test_git_environment_removes_all_ambient_git_controls(self):
        with patch.dict(os.environ, {"GIT_DIR": "external", "GIT_INDEX_FILE": "alternate",
                                    "GIT_CONFIG_COUNT": "1", "GIT_TRACE": "external-log"}):
            env = checks.git_environment()
        self.assertNotIn("GIT_DIR", env)
        self.assertNotIn("GIT_INDEX_FILE", env)
        self.assertNotIn("GIT_CONFIG_COUNT", env)
        self.assertNotIn("GIT_TRACE", env)
        self.assertEqual(env["GIT_NO_LAZY_FETCH"], "1")
        self.assertEqual(env["GIT_NO_REPLACE_OBJECTS"], "1")
        self.assertEqual(env["GIT_OPTIONAL_LOCKS"], "0")
        self.assertEqual(env["GIT_ALLOW_PROTOCOL"], "")

    def test_nonisolated_entrypoints_are_refused(self):
        with patch.object(sys, "flags", SimpleNamespace(isolated=0, no_site=0)), \
             contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(checks.main(), 1)
            self.assertEqual(runner.main(), 1)


def fixture_git(root, *args, env=None):
    """Mutations are confined to test-owned temporary repositories, never checkout ROOT."""
    controlled = checks.git_environment()
    if env:
        controlled.update(env)
    result = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
                             "-C", str(root), *args], capture_output=True, text=True,
                            check=True, timeout=30, env=controlled)
    return result.stdout.strip()


def snapshot(root):
    """Source, objects, refs, index and config bytes; never follow directory links."""
    return {str(path.relative_to(root)): path.read_bytes()
            for directory, _, files in os.walk(root, followlinks=False)
            for name in files for path in [Path(directory) / name] if not path.is_symlink()}


class RealGitTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.area = Path(self.temporary.name).resolve()
        self.root, self.head = self.repository("first")
        self.previous = Path.cwd()
        os.chdir(self.root)
        self.addCleanup(os.chdir, self.previous)
        # Synthetic identity is only an owned regression fixture. It never
        # substitutes for a real feature checkout or Linux acceptance receipt.
        pin = patch.object(checks, "BASE", self.head)
        pin.start()
        self.addCleanup(pin.stop)
        self.marker = self.area / "executed.marker"

    def repository(self, name):
        root = self.area / name
        root.mkdir()
        fixture_git(root, "init", "--quiet")
        fixture_git(root, "config", "user.name", "Owned regression fixture")
        fixture_git(root, "config", "user.email", "fixture@example.invalid")
        fixture_git(root, "config", "core.autocrlf", "false")
        fixture_git(root, "remote", "add", "origin", "https://github.com/qwekum/Qwe-Shell.git")
        for relative in checks.REQUIRED:
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            value = '{"version":1}\n' if path.suffix == ".json" else (
                "<Project />\n" if path.suffix == ".csproj" else
                "VALUE = 1\n" if path.suffix == ".py" else "")
            path.write_text(value, encoding="utf-8")
        (root / "README.md").write_text(name, encoding="utf-8")
        fixture_git(root, "add", ".")
        fixture_git(root, "commit", "--quiet", "-m", "Owned regression fixture")
        return root, fixture_git(root, "rev-parse", "HEAD")

    def verify(self, clean=False, root=None):
        return checks.verify_checkout(root or self.root, self.head, False, clean)

    def assert_refusal_preserves(self, message, clean=False):
        before = snapshot(self.root)
        with self.assertRaisesRegex(ValueError, message):
            self.verify(clean)
        self.assertEqual(snapshot(self.root), before)
        self.assertFalse(self.marker.exists())

    def configure_fsmonitor(self):
        hook = self.area / "fsmonitor.sh"
        hook.write_text("#!/bin/sh\nprintf invoked > '" + self.marker.as_posix() + "'\n", encoding="utf-8")
        hook.chmod(0o700)
        fixture_git(self.root, "config", "core.fsmonitor", str(hook))

    def test_fsmonitor_passing_gate_does_not_execute_or_mutate(self):
        self.configure_fsmonitor()
        before = snapshot(self.root)
        self.assertFalse(self.verify(clean=True)["dirty"])
        self.assertTrue(all(checks.check_sources(self.root).values()))
        self.assertFalse(self.marker.exists())
        self.assertEqual(snapshot(self.root), before)

    def test_fsmonitor_dirty_install_refusal_does_not_execute_or_mutate(self):
        self.configure_fsmonitor()
        (self.root / "README.md").write_text("dirty fixture", encoding="utf-8")
        self.assert_refusal_preserves("clean", clean=True)

    def test_missing_promisor_blob_has_no_transport_or_mutation(self):
        blob = fixture_git(self.root, "rev-parse", "HEAD:README.md")
        transport = self.area / "git-remote-marker"
        transport.write_text("#!/bin/sh\nprintf invoked > '" + self.marker.as_posix() + "'\nexit 1\n", encoding="utf-8")
        transport.chmod(0o700)
        fixture_git(self.root, "config", "remote.origin.url", "marker::owned-fixture")
        fixture_git(self.root, "config", "remote.origin.promisor", "true")
        fixture_git(self.root, "config", "remote.origin.partialclonefilter", "blob:none")
        fixture_git(self.root, "config", "extensions.partialClone", "origin")
        missing = self.root / ".git/objects" / blob[:2] / blob[2:]
        self.assertTrue(missing.resolve().is_relative_to(self.root / ".git/objects"))
        missing.unlink()  # Owned loose fixture object only; no user object is touched.
        before = snapshot(self.root)
        with patch.dict(os.environ, {"PATH": str(self.area) + os.pathsep + os.environ["PATH"]}):
            with self.assertRaises(subprocess.CalledProcessError):
                checks.git(self.root, "cat-file", "blob", blob, raw=True)
        self.assertFalse(self.marker.exists())
        self.assertEqual(snapshot(self.root), before)

    def test_ambient_repo_and_config_redirection_is_ignored(self):
        other, other_head = self.repository("second")
        fixture_git(other, "remote", "set-url", "origin", "https://example.invalid/other")
        before, other_before = snapshot(self.root), snapshot(other)
        self.assertNotEqual(self.head, other_head)
        with patch.dict(os.environ, {"GIT_DIR": str(other / ".git"), "GIT_WORK_TREE": str(other),
                                    "GIT_COMMON_DIR": str(other / ".git"),
                                    "GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "core.worktree",
                                    "GIT_CONFIG_VALUE_0": str(other),
                                    "GIT_OBJECT_DIRECTORY": str(other / ".git/objects"),
                                    "GIT_ALTERNATE_OBJECT_DIRECTORIES": str(other / ".git/objects")}):
            identity = self.verify(clean=True)
        self.assertEqual(identity["head"], self.head)
        self.assertEqual(Path(identity["gitDirectory"]), self.root / ".git")
        self.assertEqual(snapshot(self.root), before)
        self.assertEqual(snapshot(other), other_before)

    def test_alternate_index_is_ignored(self):
        alternate = self.area / "alternate-index"
        alternate.write_bytes(b"invalid external index")
        (self.root / "README.md").write_text("dirty fixture", encoding="utf-8")
        before = snapshot(self.root)
        with patch.dict(os.environ, {"GIT_INDEX_FILE": str(alternate)}):
            self.assertTrue(self.verify()["dirty"])
            with self.assertRaisesRegex(ValueError, "clean"):
                self.verify(clean=True)
        self.assertEqual(alternate.read_bytes(), b"invalid external index")
        self.assertEqual(snapshot(self.root), before)

    def test_linked_worktree_binding_passes(self):
        for mode, options in (("absolute", ()), ("relative", ("--relative-paths",)),
                              ("mismatched-relative", ("--relative-paths",))):
            with self.subTest(binding=mode):
                os.chdir(self.root)
                linked = self.area / ("linked-" + mode)
                fixture_git(self.root, "worktree", "add", "--detach", *options, str(linked), self.head)
                directory = self.root / ".git/worktrees" / linked.name
                back_file = directory / "gitdir"
                self.assertEqual(Path(back_file.read_text().strip()).is_absolute(), mode == "absolute")
                if mode == "mismatched-relative":
                    # Deliberately corrupt only the owned regression fixture.
                    back_file.write_text(os.path.relpath(self.area / "unrelated/.git", directory) + "\n", encoding="utf-8")
                before, linked_before = snapshot(self.root), snapshot(linked)
                os.chdir(linked)
                if mode == "mismatched-relative":
                    with self.assertRaisesRegex(ValueError, "binding is inconsistent"):
                        checks.verify_git_binding(linked)
                else:
                    identity = self.verify(clean=True, root=linked)
                    self.assertEqual(identity["head"], self.head)
                    self.assertEqual(Path(identity["gitDirectory"]), directory)
                self.assertEqual(snapshot(self.root), before)
                self.assertEqual(snapshot(linked), linked_before)

    def test_assume_unchanged_modified_helper_refused_without_mutation(self):
        fixture_git(self.root, "update-index", "--assume-unchanged", "tools/cloud/test_checks.py")
        (self.root / "tools/cloud/test_checks.py").write_text("VALUE = 2\n", encoding="utf-8")
        self.assert_refusal_preserves("assume-unchanged", clean=True)

    def test_skip_worktree_refused_without_mutation(self):
        fixture_git(self.root, "update-index", "--skip-worktree", "tools/cloud/test_checks.py")
        self.assert_refusal_preserves("skip-worktree", clean=True)
        (self.root / "tools/cloud/test_checks.py").write_text("VALUE = 2\n", encoding="utf-8")
        self.assert_refusal_preserves("skip-worktree", clean=True)

    def test_sparse_state_refused_without_mutation(self):
        fixture_git(self.root, "config", "core.sparseCheckout", "true")
        self.assert_refusal_preserves("Sparse", clean=True)

    def test_content_filter_refused_without_execution(self):
        fixture_git(self.root, "config", "filter.marker.clean", "touch '" + self.marker.as_posix() + "'")
        (self.root / ".gitattributes").write_text("*.py filter=marker\n", encoding="utf-8")
        self.assert_refusal_preserves("content filters")

    def test_initialized_submodule_refused_without_execution(self):
        fixture_git(self.root, "update-index", "--add", "--cacheinfo", "160000," + self.head + ",donor")
        donor = self.root / "donor"
        donor.mkdir()
        (donor / ".git").write_text("gitdir: " + str(self.area / "external") + "\n", encoding="utf-8")
        self.assert_refusal_preserves("Initialized submodules")

    def test_external_alternates_refused_without_mutation(self):
        (self.root / ".git/objects/info/alternates").write_text(str(self.area / "external") + "\n", encoding="utf-8")
        self.assert_refusal_preserves("external object storage")


class BoundedRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.area = Path(self.temporary.name).resolve()
        self.root = self.area / "checkout"
        self.cloud = self.root / "tools/cloud"
        self.cloud.mkdir(parents=True)
        self.marker = self.area / "unexpected.marker"
        self.load_marker = self.area / "load.marker"
        # Run a one-test owned copy to exercise loader admission without
        # recursively executing the full production regression suite.
        runner_source = Path(runner.__file__).read_text(encoding="utf-8")
        (self.cloud / "run_tests.py").write_text(
            runner_source.replace("EXPECTED_TESTS = 38", "EXPECTED_TESTS = 1"), encoding="utf-8")
        shutil.copyfile(checks.__file__, self.cloud / "checks.py")
        (self.cloud / "test_checks.py").write_text(
            "import unittest\nimport checks\nclass Smoke(unittest.TestCase):\n"
            "    def test_intended(self):\n        self.assertEqual(checks.git_environment()['GIT_NO_LAZY_FETCH'], '1')\n",
            encoding="utf-8")
        for name in ("install.sh", "start.md"):
            (self.cloud / name).write_text("", encoding="utf-8")

    def marker_source(self, marker=None):
        return "from pathlib import Path\nPath(" + repr(str(marker or self.marker)) + ").write_text('executed')\n"

    def child(self, env=None):
        return subprocess.run([sys.executable, "-I", "-S", "-B", "tools/cloud/run_tests.py"],
                              cwd=self.root, capture_output=True, text=True, timeout=30,
                              env={**os.environ, **(env or {})})

    def assert_refused_before_load(self):
        (self.cloud / "test_checks.py").write_text(self.marker_source(self.load_marker), encoding="utf-8")
        before = snapshot(self.root)
        result = self.child()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("BLOCKED:", result.stderr)
        self.assertFalse(self.marker.exists())
        self.assertFalse(self.load_marker.exists())
        self.assertEqual(snapshot(self.root), before)

    def test_root_and_environment_module_shadowing_cannot_execute(self):
        for name in ("unittest.py", "argparse.py", "checks.py", "sitecustomize.py"):
            (self.root / name).write_text(self.marker_source(), encoding="utf-8")
        external = self.area / "external"
        external.mkdir()
        (external / "unittest.py").write_text(self.marker_source(), encoding="utf-8")
        before = snapshot(self.root)
        result = self.child({"PYTHONPATH": str(external)})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Ran 1 test", result.stderr)
        self.assertFalse(self.marker.exists())
        self.assertEqual(snapshot(self.root), before)

    def test_valid_extra_module_is_refused_before_loading(self):
        (self.cloud / "test_extra.py").write_text(self.marker_source(), encoding="utf-8")
        self.assert_refused_before_load()

    def test_package_initializer_is_refused_before_loading(self):
        package = self.cloud / "package"
        package.mkdir()
        (package / "__init__.py").write_text(self.marker_source(), encoding="utf-8")
        self.assert_refused_before_load()

    def test_ignored_python_input_is_refused_before_loading(self):
        (self.root / ".gitignore").write_text("tools/cloud/test_ignored.py\n", encoding="utf-8")
        (self.cloud / "test_ignored.py").write_text(self.marker_source(), encoding="utf-8")
        self.assert_refused_before_load()

    def test_directory_and_file_symlinks_are_refused_before_loading(self):
        external = self.area / "external"
        external.mkdir()
        (external / "__init__.py").write_text(self.marker_source(), encoding="utf-8")
        (self.cloud / "package").symlink_to(external, target_is_directory=True)
        self.assert_refused_before_load()
        (self.cloud / "package").unlink()  # Owned fixture link only.
        (self.cloud / "checks.py").unlink()
        (self.cloud / "checks.py").symlink_to(external / "__init__.py")
        self.assert_refused_before_load()

    def test_intended_suite_has_expected_nonzero_count(self):
        suite = unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__])
        self.assertGreater(suite.countTestCases(), 0)
        self.assertEqual(suite.countTestCases(), runner.EXPECTED_TESTS)


if __name__ == "__main__":
    unittest.main()
