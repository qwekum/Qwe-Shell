"""Exercise the real worker, including ownership of its composed window."""
import argparse
import concurrent.futures
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import time


def windows(pid):
    result = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]

    @callback_type
    def visit(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            result.append(int(hwnd))
        return True

    user32.EnumWindows(visit, 0)
    return result


def read_frame(pipe):
    prefix = pipe.read(4)
    if len(prefix) != 4:
        raise AssertionError("worker returned no complete frame prefix")
    length, = struct.unpack("<I", prefix)
    assert 0 < length <= 16 * 1024 * 1024
    payload = pipe.read(length)
    assert len(payload) == length, "truncated response"
    return json.loads(payload)


def run(worker, dll, operation, payload):
    process = subprocess.Popen([str(worker), "--language", str(dll)], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=1)
    try:
        request = json.dumps(dict(version=1, id="native-test", revision="revision-7",
                                  operation=operation, payload=payload)).encode()
        process.stdin.write(struct.pack("<I", len(request)) + request)
        process.stdin.flush()
        response = pool.submit(read_frame, process.stdout).result(timeout=15)
        assert response["id"] == "native-test" and response["revision"] == "revision-7"
        evidence = {"operation": operation, "status": response["status"]}
        if operation == "compose" and response["status"] == "ok":
            time.sleep(0.2)
            assert process.poll() is None, "composition worker exited prematurely"
            owned = windows(process.pid)
            assert owned, "successful compose has no visible window owned by the worker"
            # Exercise the actual window procedure while its DLL must remain loaded.
            user32 = ctypes.WinDLL("user32", use_last_error=True)
            user32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
            user32.GetWindowLongPtrW.restype = ctypes.c_ssize_t
            user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
            for hwnd in owned:
                assert user32.GetWindowLongPtrW(hwnd, -20) & 0x20, "layered worker window intercepts host mouse input"
                assert user32.PostMessageW(hwnd, 0x000F, 0, 0)  # WM_PAINT
            time.sleep(0.2)
            assert process.poll() is None and windows(process.pid), "window callback lost its native module"
            evidence["ownedVisibleWindows"] = len(owned)
        process.stdin.close()
        process.wait(timeout=5)
        assert process.returncode == 0, f"worker failed: {process.returncode}"
        assert not windows(process.pid), "worker window survived owner exit"
        evidence["ownerExitClosedWindows"] = True
        return response, evidence
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        pool.shutdown(wait=True)
        for pipe in (process.stdin, process.stdout, process.stderr):
            if pipe and not pipe.closed:
                pipe.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("worker", type=Path)
    parser.add_argument("dll", type=Path)
    parser.add_argument("--evidence", type=Path)
    args = parser.parse_args()
    worker, dll = args.worker.resolve(), args.dll.resolve()
    cases = []
    payload = dict(source="", context=dict(dpi=96, themeMode=0),
                   sampleMenu=[dict(title="Native worker"), dict(title="Checked", checked=True)],
                   viewportHeight=300)
    for operation in ("render", "compose"):
        response, case = run(worker, dll, operation, payload)
        assert response["status"] == "ok", response
        assert response["result"]["frame"]["width"] > 0
        cases.append(case)
    response, case = run(worker, dll, "compose", dict(source="item("))
    assert response["status"] != "ok", "invalid source unexpectedly composed"
    cases.append(case)
    evidence = dict(workerSha256=hashlib.sha256(worker.read_bytes()).hexdigest(),
                    dllSha256=hashlib.sha256(dll.read_bytes()).hexdigest(), cases=cases)
    if args.evidence:
        args.evidence.parent.mkdir(parents=True, exist_ok=True)
        args.evidence.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"PASS {len(cases)} real native worker checks; composition ownership and EOF cleanup verified")


if __name__ == "__main__":
    main()
