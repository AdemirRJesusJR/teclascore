"""Check every C# suggestion voicing against the MIDI score's actual SVG generator.

Run Teclas.Voicing.Tests first, then pass its renderer-parity.json output here.
No MuseScore process, source music or user settings are needed.
"""
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'engine'))
from batch_score import keyboard_asset
from batch_harmony import CHORD_INTERVALS


def verify(path):
    records = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    expected = {(root, quality, inversion)
                for root in range(12) for quality in CHORD_INTERVALS for inversion in range(3)}
    seen = set()
    for record in records:
        key = (record['root'], record['quality'], record['inversion'])
        assert key not in seen, f'Duplicate voicing: {key}'
        seen.add(key)
        _, svg, pitches = keyboard_asset(*key)
        dots = sorted(int(element.attrib['data-midi']) for element in ET.fromstring(svg).iter()
                      if element.tag.endswith('circle') and 'data-midi' in element.attrib)
        assert record['pitches'] == pitches == dots, f'Voicing or actual keyboard marker mismatch: {record}'
    assert seen == expected, f'Coverage differs: missing={expected-seen}; extra={seen-expected}'
    return {'status': 'passed', 'cases': len(records), 'svg_markers_exact': True}


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('Usage: python tests/verify_voicing_renderer.py <renderer-parity.json>')
    print(json.dumps(verify(sys.argv[1])))
