"""Optional second voice: a chosen counter-melody gets its own staff below the melody."""
from pathlib import Path
import io, os, struct, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, PPQ, engine, worker


def duet_midi(path, low=False):
    """Melody on track 1 and a counter-melody on track 2 (quarter notes, 4/4, four bars)."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    def track(channel, pitches):
        body = b''; previous = 0
        for index, pitch in enumerate(pitches):
            for tick, message in ((index * PPQ, bytes([0x90 | channel, pitch, 90])), (index * PPQ + PPQ - 1, bytes([0x80 | channel, pitch, 0]))):
                body += vlq(tick - previous) + message; previous = tick
        body += b'\x00\xff\x2f\x00'
        return b'MTrk' + struct.pack('>I', len(body)) + body
    melody = [72, 74, 76, 77, 79, 77, 76, 74, 72, 76, 79, 84, 79, 76, 74, 72, 71, 72, 74, 76, 77, 76, 74, 72]
    counter = [p - (24 if low else 4) for p in melody]
    conductor = b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 3, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + track(0, melody) + track(1, counter))


HARMONY = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=i * 4 * PPQ, root=r, quality='', confidence=1., pitch_classes=[r]) for i, r in enumerate([0, 7, 0, 5])])


class SecondVoiceTests(unittest.TestCase):
    def build(self, low=False):
        with tempfile.TemporaryDirectory() as folder:
            out = Path(folder); path = out / 'dueto.mid'; duet_midi(path, low); parsed = engine.parse_midi(path)
            config = worker.manual_melody_config([dict(A, track=1)])
            config['second_voice_sources'] = [dict(track=2, channel=2, program=0)]
            with patch.object(engine, 'BATCH', out), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, HARMONY, out, 'Dueto')
            return engine.E.parse(xml).getroot(), report

    def test_second_voice_gets_its_own_staff(self):
        root, report = self.build()
        parts = root.findall('part')
        self.assertEqual(len(parts), 2)
        self.assertEqual(report['melody_analysis']['second_voice_notes'], 24)
        self.assertTrue(parts[0].findall('.//harmony') and not parts[1].findall('.//harmony'))
        self.assertEqual([e.findtext('part-name') for e in root.iter('score-part')], ['Melodia', 'Segunda voz'])

    def test_low_second_voice_uses_bass_clef(self):
        root, _ = self.build(low=True)
        self.assertEqual(root.findall('part')[1].find('.//clef/sign').text, 'F')
        self.assertEqual(root.findall('part')[0].find('.//clef/sign').text, 'G')


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeSecondVoiceTests(unittest.TestCase):
    def test_two_staves_survive_musescore(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'dueto'; out.mkdir()
            path = out / 'dueto.mid'; duet_midi(path); parsed = engine.parse_midi(path)
            config = worker.manual_melody_config([dict(A, track=1)]); config['second_voice_sources'] = [dict(track=2, channel=2, program=0)]
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, HARMONY, out, 'Dueto')
                final = engine.convert_score(xml, report, out, 'Dueto')
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('Score/Staff')), 2)
            self.assertTrue(report['geometric_layout_qa']['passed'])


if __name__ == '__main__': unittest.main()
