"""Run every unittest, reporting real failures and explicit native skips as JSON."""
from pathlib import Path
import argparse
import json
import os
import subprocess
import sys
import unittest


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--native", action="store_true", help="also run the tests that render in MuseScore")
parser.add_argument("--skip-accuracy", action="store_true", help="skip the synthetic accuracy corpus")
arguments = parser.parse_args()
root = Path(__file__).resolve().parents[2]
# Native tests are a separate opt-in stage (--native). An inherited shell preference must
# never silently start native rendering while running the managed CI suite.
if arguments.native: os.environ["TECLAS_TEST_NATIVE_LABELS"] = "1"
else: os.environ.pop("TECLAS_TEST_NATIVE_LABELS", None)
sys.path.insert(0, str(root / "tests"))
suite = unittest.defaultTestLoader.discover(str(root / "tests"), pattern="test_*.py")
result = unittest.TextTestRunner(verbosity=2).run(suite)
report = {
    "status": "passed" if result.wasSuccessful() else "failed",
    "tests": result.testsRun,
    "failures": [{"test": str(test), "trace": trace} for test, trace in result.failures],
    "errors": [{"test": str(test), "trace": trace} for test, trace in result.errors],
    "skipped": [{"test": str(test), "reason": reason} for test, reason in result.skipped],
    "unexpectedSuccesses": [str(test) for test in result.unexpectedSuccesses],
}
# Synthetic accuracy corpus against its baseline: fails only when a metric gets worse. It runs the
# real worker with the MuseScore step replaced, so it needs no MuseScore and takes a few seconds.
accuracy_ok = True
if not arguments.skip_accuracy:
    accuracy_report = arguments.output.with_name("accuracy-report.json")
    command = [sys.executable, "-B", str(root / "tests" / "accuracy" / "run_accuracy.py"),
               "--baseline", str(root / "tests" / "accuracy" / "baseline.json"), "--output", str(accuracy_report)]
    process = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(process.stdout, end="")
    print(process.stderr, end="", file=sys.stderr)
    lines = [line for line in process.stdout.splitlines() if line.startswith("{")]
    accuracy_ok = process.returncode == 0
    report["accuracy"] = {"status": "passed" if accuracy_ok else "failed", "report": str(accuracy_report),
                          "summary": json.loads(lines[-1]) if lines else None}
    if not accuracy_ok:
        report["status"] = "failed"
arguments.output.parent.mkdir(parents=True, exist_ok=True)
arguments.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
raise SystemExit(0 if result.wasSuccessful() and result.testsRun and accuracy_ok else 1)
