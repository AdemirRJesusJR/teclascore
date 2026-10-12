"""Exercise and render the real WPF interface without opening a native window.

Build the Release app first. This test imports MIDI only; it never analyzes,
generates scores, saves settings, or modifies the musical source files.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from PIL import Image


BASE = Path(__file__).resolve().parents[1]


def fingerprint(path: Path) -> str | None:
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, default=BASE / "src/Teclas.App/bin/Release/net10.0-windows/Teclas.Partituras.exe")
    parser.add_argument("--midis", type=Path, default=BASE.parent / "midis")
    parser.add_argument("--output", type=Path, default=BASE / "qa/ui-v0.2")
    parser.add_argument("--case", help="Run a single named case instead of the full set.")
    args = parser.parse_args()
    if not args.app.is_file():
        parser.error(f"App not found: {args.app}")
    args.output.mkdir(parents=True, exist_ok=True)
    report_path = args.output / "report.json"
    report_path.write_text(json.dumps({"status": "running", "app": str(args.app)}), encoding="utf-8")
    midi_paths = sorted(p for p in args.midis.iterdir() if p.suffix.lower() in {".mid", ".midi", ".kar"})
    before = {str(path): fingerprint(path) for path in midi_paths}
    settings = Path(os.environ["LOCALAPPDATA"]) / "TeclasPartituras/settings.json"
    settings_before = fingerprint(settings)
    profiles = json.loads((BASE / "engine/profiles.json").read_text(encoding="utf-8"))
    ready_files = [p for p in midi_paths if profiles.get(before[str(p)], {}).get("config", {}).get("status") == "ready"]
    if not ready_files:
        parser.error("No known reviewed MIDI is available for the editor smoke test.")
    ready = ready_files[0]
    inventory_path = args.midis.parent / "trabalho/inventario/inventario_midis.json"
    expected_unreadable = None
    if inventory_path.is_file():
        inventory = json.loads(inventory_path.read_text(encoding="utf-8-sig"))
        if Path(inventory["source"]).resolve() == args.midis.resolve() and inventory["total_files"] == len(midi_paths):
            expected_unreadable = len(inventory["failed_files"])
    cases = [
        ("dashboard-empty", None, "dashboard", 0, 1400, 900),
        ("dashboard-library", args.midis, "dashboard", 0, 1400, 900),
        ("dashboard-compact", args.midis, "dashboard", 0, 1100, 740),
        ("library-melody", ready, "library", 0, 1400, 900),
        ("library-chords", ready, "library", 1, 1400, 900),
        ("library-preview", ready, "library", 2, 1400, 900),
        ("library-compact", args.midis, "library", 0, 1100, 740),
        ("production-library", args.midis, "production", 0, 1400, 900),
        ("settings-empty", None, "settings", 0, 1400, 900),
        ("settings-compact", None, "settings", 0, 1100, 740),
    ]
    # Reuse a previous real MuseScore conversion for nonempty production/preview.
    # The smoke app rejects it unless its source path AND source hash match an imported MIDI.
    fixture = None
    protected_results = {}
    prior_output = BASE / "qa/pipeline-csharp/output"
    prior_review = prior_output / "Norte_ForroAbertura_Show - teclado - revisao"
    validation_path = prior_review / "validacao.json"
    if validation_path.is_file():
        validation = json.loads(validation_path.read_text(encoding="utf-8"))
        score = prior_output / "Norte_ForroAbertura_Show - teclado.mscz"
        preview = prior_review / "pagina-1.png"
        source = Path(validation["source"])
        assert fingerprint(source) == validation["source_sha256"], "Historical validation refers to a changed source."
        fixture = {"source": str(source), "source_sha256": validation["source_sha256"], "scorePath": str(score), "previewPath": str(preview), "pages": validation["pages"]}
        protected_results = {str(p): fingerprint(p) for p in (score, preview, validation_path)}
        assert all(protected_results.values()), "Historical score fixture is incomplete."
        (args.output / "actual-result-fixture.json").write_text(json.dumps(fixture, ensure_ascii=False, indent=2), encoding="utf-8")
        cases.extend([
            ("production-generated", source, "production", 0, 1400, 900),
            ("preview-generated", source, "library", 2, 1400, 900),
        ])
    if args.case:
        cases = [case for case in cases if case[0] == args.case]
        if not cases:
            parser.error("Unknown case.")
    results = []
    try:
        for name, source, page, tab, width, height in cases:
            output = args.output / (name + ".png")
            cmd = [str(args.app), "--smoke", str(output)]
            if source:
                cmd.append(str(source))
            cmd.extend(["--page", page, "--tab", str(tab), "--width", str(width), "--height", str(height), "--verify-navigation"])
            generated = name.endswith("-generated")
            if generated:
                cmd.extend(["--result", str(args.output / "actual-result-fixture.json")])
            start = time.monotonic()
            process = subprocess.run(cmd, capture_output=True, timeout=90, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            if process.returncode:
                detail = Path(str(output) + ".error.txt")
                raise AssertionError((name, process.returncode, detail.read_text(encoding="utf-8") if detail.exists() else process.stderr.decode("utf-8", errors="replace")))
            diagnostics = json.loads(Path(str(output) + ".diagnostics.json").read_text(encoding="utf-8-sig"))
            assert not diagnostics["bindingDiagnostics"], (name, diagnostics["bindingDiagnostics"])
            assert not diagnostics["importFailures"], (name, diagnostics["importFailures"])
            expected_count = len(midi_paths) if source == args.midis else (1 if source else 0)
            assert diagnostics["importedFiles"] == expected_count, (name, diagnostics)
            assert bool(diagnostics["selectedFile"]) == bool(expected_count), (name, "Imported library has no selected row.")
            expected_ready = len(ready_files) if source == args.midis else (1 if source else 0)
            assert diagnostics["reviewedFiles"] == expected_ready, (name, diagnostics)
            unreadable_count = expected_unreadable if source == args.midis else 0
            if unreadable_count is not None:
                assert diagnostics["unreadableFiles"] == unreadable_count, (name, diagnostics)
            assert len(diagnostics["navigationChecks"]) >= 10, (name, "Navigation checks did not run.")
            assert diagnostics["generatedFiles"] == int(generated), (name, diagnostics)
            assert diagnostics["completedPages"] == (fixture["pages"] if generated else 0), (name, diagnostics)
            with Image.open(output) as image:
                assert image.size == (width, height), (name, image.size)
                assert len(image.convert("RGB").getcolors(width * height)) > 40, (name, "Blank image")
                if image.mode == "RGBA":
                    assert image.getchannel("A").getextrema()[0] >= 250, (name, "Transparent page background")
            results.append({"case": name, "seconds": round(time.monotonic() - start, 2), "image": str(output), "importedFiles": expected_count, "bindingErrors": 0, "navigation": diagnostics.get("navigationChecks")})
            print(f"PASS {name}: {expected_count} MIDIs, zero binding errors", flush=True)
    except BaseException as error:
        report_path.write_text(json.dumps({"status": "failed", "cases": results, "error": str(error)}, ensure_ascii=False, indent=2), encoding="utf-8")
        raise
    finally:
        assert before == {str(path): fingerprint(path) for path in midi_paths}, "MIDI sources changed during UI test."
        assert settings_before == fingerprint(settings), "Application settings changed during UI test."
        assert protected_results == {p: fingerprint(Path(p)) for p in protected_results}, "Historical score fixture changed during UI test."
    report = {"status": "passed", "appDllSha256": fingerprint(args.app.with_suffix(".dll")), "cases": results, "originalMidisPreserved": True, "settingsPreserved": True}
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"UI QA passed: {len(results)} views; original MIDIs and settings unchanged.", flush=True)


if __name__ == "__main__":
    main()
