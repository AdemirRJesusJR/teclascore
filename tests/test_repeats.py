"""Literal repetitions become repeat barlines (score_repeats.py), only when exactly equal and safe."""
from pathlib import Path
import io, os, struct, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, PPQ, engine, worker
import score_repeats

DIV = 960
BAR = 4 * DIV


def bars(count):
    return [{'start': i * BAR, 'end': (i + 1) * BAR, 'meter': (4, 4), 'partial': False} for i in range(count)]


def phrase(bar_index, pitches):
    return [{'start': bar_index * BAR + i * DIV, 'end': bar_index * BAR + (i + 1) * DIV, 'pitch': p} for i, p in enumerate(pitches)]


class FoldTests(unittest.TestCase):
    def material(self):
        notes = phrase(0, [60, 62, 64, 65]) + phrase(1, [67, 65, 64, 62]) + phrase(2, [60, 62, 64, 65]) + phrase(3, [67, 65, 64, 62]) \
            + phrase(4, [72, 71, 69, 67]) + phrase(5, [65, 64, 62, 60])
        chords = [{'tick': i * BAR, 'root': r, 'quality': q} for i, (r, q) in enumerate([(0, ''), (7, '7'), (0, ''), (7, '7'), (5, ''), (0, '')])]
        return bars(6), notes, chords, {beat: 4 for beat in range(24)}

    def test_two_bars_played_twice_are_written_once(self):
        b, notes, chords, grids = self.material()
        tempos = [{'tick': 0, 'bpm': 100}]
        folds = score_repeats.fold_repeats(b, notes, chords, grids, tempos, [], PPQ, 0, DIV)
        self.assertEqual(folds, [{'first_measure': 1, 'measures': 2, 'removed_quarters': 8.0}])
        self.assertEqual(len(b), 4)
        self.assertTrue(b[0]['repeat_start'] and b[1]['repeat_end'])
        self.assertEqual([n['pitch'] for n in notes if 2 * BAR <= n['start'] < 3 * BAR], [72, 71, 69, 67])
        self.assertEqual([c['root'] for c in chords], [0, 7, 5, 0])
        self.assertEqual(sorted(grids), list(range(16)))
        self.assertEqual(b[-1]['end'], 4 * BAR)

    def test_new_words_become_verse_two(self):
        b, notes, chords, grids = self.material()
        notes[0]['lyric'] = {'text': 'lua', 'syllabic': 'single'}; notes[8]['lyric'] = {'text': 'sol', 'syllabic': 'single'}
        notes[1]['lyric'] = {'text': 'no', 'syllabic': 'single'}; notes[9]['lyric'] = {'text': 'no', 'syllabic': 'single'}
        folds = score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV)
        self.assertEqual(folds[0]['verse_two_syllables'], 1)
        self.assertEqual(len(b), 4)
        self.assertEqual((notes[0]['lyric']['text'], notes[0]['lyric2']['text']), ('lua', 'sol'))
        self.assertNotIn('lyric2', notes[1])  # the same word both times stays a single line

    def test_third_pass_is_not_a_third_verse(self):
        b, notes, chords, grids = self.material()
        notes[0]['lyric2'] = {'text': 'já', 'syllabic': 'single'}; notes[8]['lyric'] = {'text': 'sol', 'syllabic': 'single'}
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV), [])

    def test_different_chord_or_tie_across_the_edge_is_not_folded(self):
        b, notes, chords, grids = self.material()
        chords[2]['quality'] = 'm'
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV), [])
        b, notes, chords, grids = self.material()
        notes[7]['end'] += DIV  # last note of the block tied into the repetition
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV), [])

    def test_tempo_change_inside_the_repetition_is_not_folded(self):
        b, notes, chords, grids = self.material()
        tempo_tick = (2 * BAR + DIV) * PPQ / DIV
        self.assertEqual(score_repeats.fold_repeats(b, notes, chords, grids, [{'tick': 0}, {'tick': tempo_tick}], [], PPQ, 0, DIV), [])


class EndingTests(unittest.TestCase):
    def test_different_last_measure_becomes_first_and_second_ending(self):
        # A B C | A B D | E : three bars repeated, the last one different the second time.
        pitches = [[60, 62, 64, 65], [67, 65, 64, 62], [60, 60, 60, 60], [60, 62, 64, 65], [67, 65, 64, 62], [55, 57, 59, 60], [72, 71, 69, 67]]
        notes = [n for i, p in enumerate(pitches) for n in phrase(i, p)]
        chords = [{'tick': i * BAR, 'root': 0, 'quality': ''} for i in range(7)]
        b, grids = bars(7), {beat: 4 for beat in range(28)}
        folds = score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV)
        self.assertEqual(folds, [{'first_measure': 1, 'measures': 3, 'endings': 1, 'removed_quarters': 8.0}])
        self.assertEqual(len(b), 5)
        self.assertTrue(b[0]['repeat_start'] and b[2]['repeat_end'])
        self.assertEqual((b[2]['ending_start'], b[2]['ending_stop'], b[3]['ending_start'], b[3]['ending_stop']), (1, 1, 2, 2))
        self.assertEqual([n['pitch'] for n in notes if 3 * BAR <= n['start'] < 4 * BAR], [55, 57, 59, 60])


X, A_, B_, C_, D_, E_, F_, G_, H_ = ([55, 57, 59, 60], [60, 62, 64, 65], [67, 65, 64, 62], [60, 64, 67, 72], [71, 67, 62, 59],
                                    [72, 71, 69, 67], [65, 64, 62, 60], [64, 65, 67, 69], [72, 72, 72, 72])
CHORD_OF = {tuple(X): (7, ''), tuple(A_): (0, ''), tuple(B_): (7, '7'), tuple(C_): (0, ''), tuple(D_): (7, ''), tuple(E_): (5, ''),
            tuple(F_): (0, ''), tuple(G_): (9, 'm'), tuple(H_): (0, '')}


def jump_material(sequence):
    notes = [n for i, p in enumerate(sequence) for n in phrase(i, p)]
    chords = [{'tick': i * BAR, 'root': CHORD_OF[tuple(p)][0], 'quality': CHORD_OF[tuple(p)][1]} for i, p in enumerate(sequence)]
    return bars(len(sequence)), notes, chords, {beat: 4 for beat in range(4 * len(sequence))}


class JumpTests(unittest.TestCase):
    def test_return_to_the_end_becomes_ds_al_fine(self):
        b, notes, chords, grids = jump_material([X, A_, B_, C_, D_, E_, F_, A_, B_, C_, D_])
        jump = score_repeats.fold_jumps(b, notes, chords, grids, [], [], PPQ, 0, DIV)
        self.assertEqual(jump, {'jump': 'D.S. al Fine', 'first_measure': 2, 'measures': 4, 'coda_measures': 0, 'removed_quarters': 16.0})
        self.assertEqual(len(b), 7)
        self.assertTrue(b[1]['segno'] and b[4]['fine'])
        self.assertEqual(b[6]['jump'], 'D.S. al Fine')
        self.assertEqual(len(notes), 28)
        self.assertEqual(sorted(grids), list(range(28)))

    def test_return_with_a_new_ending_becomes_ds_al_coda(self):
        b, notes, chords, grids = jump_material([X, A_, B_, C_, D_, E_, F_, A_, B_, C_, D_, G_, H_])
        jump = score_repeats.fold_jumps(b, notes, chords, grids, [], [], PPQ, 0, DIV)
        self.assertEqual((jump['jump'], jump['coda_measures']), ('D.S. al Coda', 2))
        self.assertEqual(len(b), 9)
        self.assertTrue(b[1]['segno'] and b[4]['to_coda'] and b[7]['coda'])
        self.assertEqual(b[6]['jump'], 'D.S. al Coda')
        self.assertEqual([n['pitch'] for n in notes if 7 * BAR <= n['start'] < 8 * BAR], G_)
        self.assertEqual([c['root'] for c in chords if c['tick'] == 7 * BAR], [9])

    def test_from_the_beginning_is_da_capo(self):
        b, notes, chords, grids = jump_material([A_, B_, C_, D_, E_, F_, A_, B_, C_, D_])
        self.assertEqual(score_repeats.fold_jumps(b, notes, chords, grids, [], [], PPQ, 0, DIV)['jump'], 'D.C. al Fine')
        self.assertNotIn('segno', b[0])

    def test_short_lyric_or_tempo_returns_stay_written(self):
        b, notes, chords, grids = jump_material([X, A_, B_, C_, E_, F_, A_, B_, C_])
        self.assertIsNone(score_repeats.fold_jumps(b, notes, chords, grids, [], [], PPQ, 0, DIV))
        b, notes, chords, grids = jump_material([X, A_, B_, C_, D_, E_, F_, A_, B_, C_, D_])
        notes[30]['lyric'] = {'text': 'la', 'syllabic': 'single'}
        self.assertIsNone(score_repeats.fold_jumps(b, notes, chords, grids, [], [], PPQ, 0, DIV))
        b, notes, chords, grids = jump_material([X, A_, B_, C_, D_, E_, F_, A_, B_, C_, D_])
        self.assertIsNone(score_repeats.fold_jumps(b, notes, chords, grids, [{'tick': 0}, {'tick': 6 * BAR * PPQ / DIV}], [], PPQ, 0, DIV))

    def test_ritornello_is_not_written_inside_a_jump(self):
        # A passage repeated literally inside the returning block stays written out after the jump.
        b, notes, chords, grids = jump_material([X, A_, B_, A_, B_, E_, F_, A_, B_, A_, B_])
        self.assertEqual(score_repeats.fold_jumps(b, notes, chords, grids, [], [], PPQ, 0, DIV)['jump'], 'D.S. al Fine')
        score_repeats.fold_repeats(b, notes, chords, grids, [], [], PPQ, 0, DIV)
        self.assertFalse(any(bar.get('repeat_start') for bar in b[1:5]))


def jump_midi(path, sequence):
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    pitches = [p for bar in sequence for p in bar]
    body = b''; previous = 0
    for index, pitch in enumerate(pitches):
        for tick, message in ((index * PPQ, bytes([0x90, pitch, 90])), (index * PPQ + PPQ - 1, bytes([0x80, pitch, 0]))):
            body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    conductor = b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 2, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + b'MTrk' + struct.pack('>I', len(body)) + body)


def ending_midi(path):
    """A B C, A B D, E: the same two bars twice with different third bars (quarter notes, 4/4)."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    pitches = [60, 62, 64, 65, 67, 65, 64, 62, 60, 64, 67, 72] + [60, 62, 64, 65, 67, 65, 64, 62, 59, 62, 65, 67] + [72, 71, 69, 67]
    body = b''; previous = 0
    for index, pitch in enumerate(pitches):
        for tick, message in ((index * PPQ, bytes([0x90, pitch, 90])), (index * PPQ + PPQ - 1, bytes([0x80, pitch, 0]))):
            body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    conductor = b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 2, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + b'MTrk' + struct.pack('>I', len(body)) + body)


def repeated_midi(path):
    """Two bars, the same two bars again, then two different bars (quarter notes, 4/4)."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    pitches = [60, 62, 64, 65, 67, 65, 64, 62] * 2 + [72, 71, 69, 67, 65, 64, 62, 60]
    events = []
    for index, pitch in enumerate(pitches):
        events += [(index * PPQ, bytes([0x90, pitch, 90])), (index * PPQ + PPQ - 1, bytes([0x80, pitch, 0]))]
    body = b''; previous = 0
    for tick, message in events: body += vlq(tick - previous) + message; previous = tick
    body += b'\x00\xff\x2f\x00'
    conductor = b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 2, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + b'MTrk' + struct.pack('>I', len(body)) + body)


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeRepeatTests(unittest.TestCase):
    def test_repeat_barlines_survive_musescore(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'ritornelo'; out.mkdir()
            path = out / 'ritornelo.mid'; repeated_midi(path); parsed = engine.parse_midi(path)
            harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=i * 4 * PPQ, root=r, quality=q, confidence=1., pitch_classes=[r])
                                                                    for i, (r, q) in enumerate([(0, ''), (7, '7'), (0, ''), (7, '7'), (5, ''), (0, '')])])
            config = worker.manual_melody_config([dict(A, track=1)]); config['fold_repeats'] = True
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony, out, 'Ritornelo')
                final = engine.convert_score(xml, report, out, 'Ritornelo')
            self.assertEqual(report['melody_analysis']['repeat_folds'][0]['measures'], 2)
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//Staff/Measure')), 4)
            self.assertEqual(len(native.findall('.//startRepeat')), 1)
            self.assertEqual(len(native.findall('.//endRepeat')), 1)


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeEndingTests(unittest.TestCase):
    def test_endings_survive_musescore(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'casas'; out.mkdir()
            path = out / 'casas.mid'; ending_midi(path); parsed = engine.parse_midi(path)
            harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=i * 4 * PPQ, root=r, quality=q, confidence=1., pitch_classes=[r])
                                                                    for i, (r, q) in enumerate([(0, ''), (7, '7'), (0, ''), (0, ''), (7, '7'), (7, '7'), (5, '')])])
            config = worker.manual_melody_config([dict(A, track=1)]); config['fold_repeats'] = True
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony, out, 'Casas')
                final = engine.convert_score(xml, report, out, 'Casas')
            self.assertEqual(report['melody_analysis']['repeat_folds'][0]['endings'], 1)
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//Staff/Measure')), 5)
            self.assertEqual(len([s for s in native.iter('Spanner') if s.get('type') == 'Volta' and s.find('Volta') is not None]), 2)



@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeJumpTests(unittest.TestCase):
    def test_ds_al_coda_survives_musescore(self):
        sequence = [X, A_, B_, C_, D_, E_, F_, A_, B_, C_, D_, G_, H_]
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'coda'; out.mkdir()
            path = out / 'coda.mid'; jump_midi(path, sequence); parsed = engine.parse_midi(path)
            _, _, chords, _ = jump_material(sequence)
            harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=c['tick'] * PPQ // DIV, root=c['root'], quality=c['quality'], confidence=1., pitch_classes=[c['root']]) for c in chords])
            config = worker.manual_melody_config([dict(A, track=1)]); config['fold_repeats'] = True
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony, out, 'Coda')
                final = engine.convert_score(xml, report, out, 'Coda')
            self.assertEqual(report['melody_analysis']['repeat_jump']['jump'], 'D.S. al Coda')
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//Staff/Measure')), 9)
            markers = sorted(m.findtext('label') or '' for m in native.iter('Marker'))
            self.assertEqual(markers, ['coda', 'codab', 'segno'], [engine.E.tostring(m) for m in native.iter('Marker')])
            jumps = list(native.iter('Jump'))
            self.assertEqual(len(jumps), 1)
            self.assertEqual((jumps[0].findtext('jumpTo'), jumps[0].findtext('playUntil'), jumps[0].findtext('continueAt')), ('segno', 'coda', 'codab'), engine.E.tostring(jumps[0]))
            # MuseScore's own playback follows the jump: every one of the 52 notes of the MIDI sounds, in order.
            midi = out / 'tocada.mid'; engine.call_musescore(['-o', str(midi), str(final)], out / 'midi.log')
            sounded = engine.parse_midi(midi)['notes']; melody = min(n['channel'] for n in sounded)  # chord symbols play on their own channel
            played = [n['pitch'] for n in sorted(sounded, key=lambda n: n['start']) if n['channel'] == melody]
            self.assertEqual(played, [p for bar in sequence for p in bar])



def verse_midi(path, pitches, words):
    """Melody on track 1 (quarter notes) and karaoke lyric events on track 2, one syllable per note."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)
    def track(events):
        body = b''; previous = 0
        for tick, message in sorted(events, key=lambda e: e[0]): body += vlq(tick - previous) + message; previous = tick
        body += b'\x00\xff\x2f\x00'; return b'MTrk' + struct.pack('>I', len(body)) + body
    melody = []; text = []
    for index, pitch in enumerate(pitches):
        melody += [(index * PPQ, bytes([0x90, pitch, 90])), (index * PPQ + PPQ - 1, bytes([0x80, pitch, 0]))]
        if index < len(words):
            data = words[index].encode('cp1252'); text.append((index * PPQ, bytes([0xff, 0x01]) + vlq(len(data)) + data))
    conductor = b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, 3, PPQ) + b'MTrk' + struct.pack('>I', len(conductor)) + conductor + track(melody) + track(text))


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS') == '1', 'Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeVerseTests(unittest.TestCase):
    def test_two_verses_survive_musescore(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder); out = base / 'versos'; out.mkdir()
            path = out / 'versos.kar'
            # Two bars sung twice with different words, then two closing bars without words.
            verse_midi(path, [60, 62, 64, 65, 67, 65, 64, 62] * 2 + [72, 71, 69, 67, 65, 64, 62, 60],
                       ['Ca', 'sa ', 'bran', 'ca ', 'do ', 'mar ', 'a', 'zul ', 'Lu', 'a ', 'do ', 'ser', 'tão ', 'que ', 'bri', 'lha '])
            parsed = engine.parse_midi(path)
            harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=i * 4 * PPQ, root=r, quality=q, confidence=1., pitch_classes=[r])
                                                                    for i, (r, q) in enumerate([(0, ''), (7, '7'), (0, ''), (7, '7'), (5, ''), (0, '')])])
            config = worker.manual_melody_config([dict(A, track=1)]); config['fold_repeats'] = True
            with patch.object(engine, 'BATCH', base), redirect_stdout(io.StringIO()):
                xml, report = engine.build_musicxml(parsed, config, harmony, out, 'Versos')
                final = engine.convert_score(xml, report, out, 'Versos')
            folds = report['melody_analysis']['repeat_folds']
            self.assertTrue(folds and folds[0].get('verse_two_syllables'), folds)
            with zipfile.ZipFile(final) as archive:
                native = engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            verses = {l.findtext('no') or '0' for l in native.iter('Lyrics')}
            self.assertEqual(verses, {'0', '1'})


if __name__ == '__main__': unittest.main()
