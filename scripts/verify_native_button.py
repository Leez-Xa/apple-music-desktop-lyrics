"""Run ctypes-only native-button checks under hidden, own-process parents.

Usage: python -B scripts/verify_native_button.py
No HWND argument or visible mode is provided: this verifier cannot attach to
Apple Music, activate windows or switch virtual desktops. No dependencies.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import sys
import unittest

# Also suppress bytecode in the dynamically imported test/module files.
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
sys.path.insert(0, str(ROOT / "tests"))


def main() -> int:
    if os.name != "nt":
        print(json.dumps({"status": "unsupported", "reason": "Windows required"}))
        return 2
    suite = unittest.defaultTestLoader.loadTestsFromName("test_native_button")
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    from amlyrics import native_button as native
    passed = result.wasSuccessful() and not result.skipped
    print(json.dumps({
        "status": "passed" if passed else "failed",
        "tests_run": result.testsRun,
        "failures": len(result.failures), "errors": len(result.errors),
        "skipped": len(result.skipped),
        "parent_mode": "hidden-only", "parent_process": os.getpid(),
        "foreign_parent_tested": False,
        "live_native_instances": len(native._live),
        "pending_native_instances": len(native._pending),
        "show_window_called": False,
    }, ensure_ascii=False))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
