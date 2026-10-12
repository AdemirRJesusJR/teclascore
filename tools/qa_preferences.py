"""Render real WPF views and verify isolated native-window close/reopen persistence.

Build Release first. No edits to the user's settings, original MIDIs or approved
MuseScore scores are permitted; all snapshots and test sessions live under qa.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

BASE = Path(__file__).resolve().parents[1]
THEMES = ("classic", "ocean", "indigo", "graphite", "light")


def fingerprint(path: Path) -> str | None:
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def user_preferences() -> dict[str, str | None]:
    folder = Path(os.environ["LOCALAPPDATA"]) / "TeclasPartituras"
    return {str(path): fingerprint(path) for path in folder.glob("settings*")} if folder.exists() else {}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, default=BASE / "src/Teclas.App/bin/Release/net10.0-windows/Teclas.Partituras.exe")
    parser.add_argument("--project", type=Path, default=BASE.parent)
    parser.add_argument("--output", type=Path, default=BASE / "qa/preferences-v0.3.1")
    parser.add_argument("--case", help="Run a single named visual case or restart.")
    args = parser.parse_args()
    if not args.app.is_file(): parser.error(f"App not found: {args.app}")
    args.output.mkdir(parents=True, exist_ok=True)
    expected_version = ET.parse(BASE / "src/Teclas.App/Teclas.App.csproj").findtext("PropertyGroup/Version")
    midis = args.project / "midis"
    midi_paths = sorted(path for path in midis.iterdir() if path.suffix.lower() in {".mid", ".midi", ".kar"})
    scores = list((args.project / "partituras").rglob("*.mscz"))
    before = {str(path): fingerprint(path) for path in [*midi_paths, *scores]}
    before_preferences = user_preferences()
    profiles = json.loads((BASE / "engine/profiles.json").read_text(encoding="utf-8"))
    ready = [path for path in midi_paths if profiles.get(before[str(path)], {}).get("config", {}).get("status") == "ready"]
    assert ready, "A reviewed MIDI is needed to exercise the editor."
    cases = []
    for theme in THEMES:
        cases.append((f"{theme}-dashboard-expanded", theme, midis if theme == "classic" else ready[0], "dashboard", 0, 1400, 900, False))
        cases.append((f"{theme}-dashboard-collapsed", theme, ready[0], "dashboard", 0, 1100, 740, True))
    for collapsed in (False, True):
        state = "collapsed" if collapsed else "expanded"
        cases.append((f"graphite-settings-{state}", "graphite", None, "settings", 0, 1100, 740, collapsed))
        cases.append((f"graphite-melody-{state}", "graphite", ready[0], "library", 0, 1100, 740, collapsed))
        cases.append((f"graphite-about-{state}", "graphite", None, "about", 0, 1100, 740, collapsed))
    cases.append(("light-production-collapsed", "light", ready[0], "production", 0, 1100, 740, True))
    if args.case:
        cases = [case for case in cases if case[0] == args.case]
        if not cases and args.case != "restart": parser.error("Unknown case.")

    def run(command: list[str], error_file: Path, timeout: int = 90):
        completed = subprocess.run(command, capture_output=True, timeout=timeout,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if completed.returncode:
            raise AssertionError((completed.returncode, error_file.read_text(encoding="utf-8") if error_file.is_file()
                else completed.stderr.decode("utf-8", errors="replace")))

    results = []
    report_path = args.output / "report.json"
    report_path.write_text(json.dumps({"status": "running", "app": str(args.app)}), encoding="utf-8")
    try:
        for index, (name, theme, source, page, tab, width, height, collapsed) in enumerate(cases):
            output = args.output / f"{name}.png"
            command = [str(args.app), "--smoke", str(output)]
            if source: command.append(str(source))
            command.extend(["--theme", theme, "--page", page, "--tab", str(tab), "--width", str(width), "--height", str(height)])
            if collapsed: command.append("--collapsed")
            if index == 0: command.extend(["--verify-themes", "--verify-navigation"])
            start = time.monotonic()
            run(command, Path(str(output) + ".error.txt"))
            diagnostics = json.loads(Path(str(output) + ".diagnostics.json").read_text(encoding="utf-8-sig"))
            expected = len(midi_paths) if source == midis else (1 if source else 0)
            assert diagnostics["appDisplayName"] == "TeclaScore" and diagnostics["appVersion"] == expected_version, (name, "Wrong identity/version")
            assert diagnostics["themeId"] == theme and diagnostics["sidebarCollapsed"] == collapsed, (name, "Wrong appearance")
            assert diagnostics["sidebarWidth"] == (72 if collapsed else 232), (name, "Wrong sidebar width")
            assert diagnostics["importedFiles"] == expected, (name, diagnostics)
            assert not diagnostics["importFailures"], (name, diagnostics["importFailures"])
            assert not diagnostics["bindingDiagnostics"], (name, diagnostics["bindingDiagnostics"])
            if index == 0:
                checks = diagnostics["themeChecks"]
                assert all(checks[f"live-switch-{value}"] and checks[f"persist-{value}"] for value in THEMES)
                assert sum(len(checks[f"contrast-{value}"]) for value in THEMES) == 95
                assert all(diagnostics["sidebarChecks"].values())
            with Image.open(output) as image:
                assert image.size == (width, height), (name, image.size)
                assert len(image.convert("RGB").getcolors(width * height)) > 40, (name, "Blank render")
                if image.mode == "RGBA": assert image.getchannel("A").getextrema()[0] >= 250, (name, "Transparent canvas")
            results.append({"case": name, "seconds": round(time.monotonic() - start, 2), "image": str(output),
                "theme": theme, "collapsed": collapsed, "importedFiles": expected, "bindingErrors": 0,
                "themeChecks": diagnostics["themeChecks"], "sidebarChecks": diagnostics["sidebarChecks"]})
            print(f"PASS {name}: {expected} MIDIs, zero binding errors", flush=True)

        if not args.case or args.case == "restart":
            output = args.output / "native-restart.json"
            run([str(args.app), "--verify-preferences", str(output), str(ready[0])], output)
            restart = json.loads(output.read_text(encoding="utf-8-sig"))
            assert restart["status"] == "passed", restart
            results.append({"case": "restart", "diagnostics": restart})
            print(f"PASS native close/reopen: {len(restart['checks'])} checks", flush=True)

        palette_keys = {"Background", "CardBrush", "Ink", "Muted", "Accent", "AccentInk", "Line", "Soft", "SoftInk", "Selection",
            "Sidebar", "SidebarInk", "SidebarMuted", "SidebarActive", "HeroInk", "HeroMuted", "TableHeader", "TableHeaderInk"}
        for path in [BASE / "src/Teclas.App/App.xaml", BASE / "src/Teclas.App/MainWindow.xaml"]:
            fixed = set(re.findall(r"\{StaticResource\s+([^}\s]+)\}", path.read_text(encoding="utf-8"))) & palette_keys
            assert not fixed, (path.name, "Palette brush resolves only at startup", sorted(fixed))
    except BaseException as error:
        report_path.write_text(json.dumps({"status": "failed", "cases": results, "error": str(error)}, ensure_ascii=False, indent=2), encoding="utf-8")
        raise
    finally:
        assert before == {path: fingerprint(Path(path)) for path in before}, "An original MIDI or approved score changed."
        assert before_preferences == user_preferences(), "The user's actual settings or backup changed."

    report_path.write_text(json.dumps({"status": "passed", "appDllSha256": fingerprint(args.app.with_suffix(".dll")), "cases": results,
        "protectedMidis": len(midi_paths), "protectedScores": len(scores), "sourcesAndSettingsPreserved": True}, ensure_ascii=False, indent=2), encoding="utf-8")
    if not args.case:
        sheet = Image.new("RGB", (1400, 5 * 280), "#F1F3F5")
        draw = ImageDraw.Draw(sheet)
        for index, theme in enumerate(THEMES):
            draw.text((10, index * 280 + 6), theme, fill="#20334D")
            for column, state in enumerate(("expanded", "collapsed")):
                with Image.open(args.output / f"{theme}-dashboard-{state}.png") as original:
                    image = original.convert("RGB"); image.thumbnail((680, 250))
                    sheet.paste(image, (10 + column * 700, index * 280 + 25))
        sheet.save(args.output / "sidebar-theme-contact-sheet.png")
    print(f"QA passed: {len(cases)} WPF views; source files and actual user settings unchanged.", flush=True)


if __name__ == "__main__": main()
