"""Chord chart for a MIDI without a complete melody: slashes on the beats, chords and diagrams, no invented notes."""
from pathlib import Path
import io, os, struct, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import PPQ, engine, worker
import score_musicxml


def accompaniment_midi(path, bars=8, numerator=4, denominator=4):
    """Block chords only (C, F, G, C...), one per bar; no melody track."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    length = PPQ * 4 * numerator // denominator
    events = []
    for bar in range(bars):
        chord = [[48, 52, 55], [53, 57, 60], [55, 59, 62], [48, 52, 55]][bar % 4]
        for pitch in chord: events += [(bar * length, bytes([0x90, pitch, 80])), (bar * length + length - 10, bytes([0x80, pitch, 0]))]
    body = b''; previous = 0
    for tick, message in sorted(events, key=lambda e: e[0]): body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    conductor = b'\x00\xff\x58\x04' + bytes([numerator, {4: 2, 8: 3}[denominator]]) + b'\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 2, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + b'MTrk' + struct.pack('>I', len(body)) + body)


CHART_CONFIG = {'index': 0, 'status': 'ready', 'melody_sources': [], 'solo_sources': [], 'reserve_high_melody': False, 'chords_only': True}
HARMONY = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=bar * 4 * PPQ, root=r, quality='', confidence=1., pitch_classes=[r])
                                                         for bar, r in enumerate([0, 5, 7, 0] * 2)])


class ChordChartTests(unittest.TestCase):
    def test_slashes_follow_the_beats_and_chord_changes(self):
        bars = [{'start': 0, 'end': 3840, 'meter': (4, 4)}, {'start': 3840, 'end': 6720, 'meter': (6, 8)}]
        slashes = score_musicxml.chord_slashes(bars, [{'tick': 0}, {'tick': 480}, {'tick': 3840}])
        self.assertEqual([(s['start'], s['end']) for s in slashes[:3]], [(0, 480), (480, 960), (960, 1920)])
        self.assertEqual(len(slashes), 5 + 2)
        self.assertTrue(all(s['pitch'] == score_musicxml.SLASH_PITCH for s in slashes))

    def test_chart_musicxml_has_slashes_and_chords(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'acompanhamento.mid'; accompaniment_midi(path); parsed = engine.parse_midi(path)
            xml, report = engine.build_musicxml(parsed, dict(CHART_CONFIG), dict(HARMONY), Path(folder), 'Cifras')
            root = engine.E.parse(xml).getroot()
            self.assertEqual(len(root.findall('.//note')), 32)
            self.assertEqual({n.findtext('notehead') for n in root.iter('note')}, {'slash'})
            self.assertEqual(report['chord_changes'], 8)
            self.assertTrue(report['melody_analysis']['chords_only'])


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeChordChartTests(unittest.TestCase):
    def test_chart_survives_musescore_and_slashes_are_silent(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'cifras'; out.mkdir()
            path = out / 'acompanhamento.mid'; accompaniment_midi(path); parsed = engine.parse_midi(path)
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, dict(CHART_CONFIG), dict(HARMONY), out, 'Cifras')
                final = engine.convert_score(xml, report, out, 'Cifras')
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            notes = list(native.iter('Note'))
            self.assertEqual(len(notes), 32)
            self.assertTrue(all(n.findtext('play') == '0' for n in notes))
            self.assertEqual({n.findtext('head') for n in notes}, {'slash'})
            self.assertEqual(len(native.findall('.//Image')), 8)
            midi = out / 'tocada.mid'; engine.call_musescore(['-o', str(midi), str(final)], out / 'midi.log')
            sounded = engine.parse_midi(midi)['notes']
            # Only the chord symbols sound, on their own channel; the 32 slashes stay silent.
            self.assertEqual(len({n['channel'] for n in sounded}), 1)
            self.assertLess(sum(n['pitch'] == score_musicxml.SLASH_PITCH for n in sounded), 32)


if __name__ == '__main__': unittest.main()
