"""Chord accuracy: chords without their fifth, new qualities, automatic slash basses, modulations and
functional root spelling. Synthetic MIDI files are written and read back like a user's MIDI."""
from pathlib import Path
import copy
import json
import os
import struct
import sys
import tempfile
import unittest
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreHarmonyAccuracy'))
import batch_harmony as harmony
import batch_score as engine
import score_musicxml
from inspect_midi import parse_midi

PPQ = 480
CHORDS, BASS, MELODY = (1, 1, 0), (2, 2, 33), (3, 3, 73)


def write_midi(path, notes, metas=((0, 0x58, bytes([4, 2, 24, 8])),)):
    """Format-1 MIDI: a conductor track with the metas, then one track per (track, channel, program)."""
    def vlq(value):
        data = [value & 127]; value >>= 7
        while value: data.insert(0, (value & 127) | 128); value >>= 7
        return bytes(data)

    def chunk(events):
        body = b''; previous = 0
        for tick, message in sorted(events, key=lambda e: (e[0], e[1][0] & 0xF0 == 0x90)):
            body += vlq(tick - previous) + message; previous = tick
        body += b'\x00\xff\x2f\x00'
        return b'MTrk' + struct.pack('>I', len(body)) + body
    conductor = [(tick, bytes([0xff, kind, len(data)]) + data) for tick, kind, data in metas]
    tracks = {}
    for quarter, pitch, duration, (track, channel, program) in notes:
        events = tracks.setdefault((track, channel, program), [(0, bytes([0xC0 | (channel - 1), program]))])
        start = round(quarter * PPQ); end = round((quarter + duration) * PPQ) - 1
        events += [(start, bytes([0x90 | (channel - 1), pitch, 90])), (end, bytes([0x80 | (channel - 1), pitch, 0]))]
    # The file track index is the declared track, so melody sources can name it.
    data = [chunk(conductor)] + [chunk([e for key in tracks if key[0] == index for e in tracks[key]])
                                 for index in range(1, max(key[0] for key in tracks) + 1)]
    path.write_bytes(b'MThd' + struct.pack('>IHHH', 6, 1, len(data), PPQ) + b''.join(data))


def block(quarter, pitches, duration=4):
    return [(quarter, pitch, duration, CHORDS) for pitch in pitches]


def bass(quarter, pitch, duration=4):
    return [(quarter, pitch, duration, BASS)]


def analyze(notes, metas=None, context=True, melody=None):
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / 'teste.mid'
        write_midi(path, notes, *([metas] if metas else []))
        parsed = parse_midi(path)
    return parsed, harmony.analyze_harmony(parsed, melody, context_advice=context)


def in_context(quarter, pitches, low):
    """A chord after two complete triads, so the source is recognized as an accompaniment."""
    return block(0, [60, 64, 67]) + bass(0, 36) + block(4, [65, 69, 72]) + bass(4, 41) + block(quarter, pitches) + bass(quarter, low)


def event_at(result, quarter):
    return next(((e['root'], e['quality']) for e in result['events'] if e['tick'] == quarter * PPQ), None)


def names(result):
    return [(e['tick'] // PPQ, e.get('root_spelling', e['root']), e['quality'], e.get('bass_spelling')) for e in result['events']]


class OmittedFifthTests(unittest.TestCase):
    def test_root_third_seventh_is_a_seventh_chord_with_the_fifth_recorded_as_omitted(self):
        notes = block(0, [48, 52, 58]) + block(4, [53, 57, 60]) + bass(0, 36) + bass(4, 41)
        _, result = analyze(notes)
        first = result['events'][0]
        self.assertEqual((first['root'], first['quality']), (0, '7'))
        self.assertTrue(first['evidence']['omitted_fifth'])
        self.assertIn('quinta justa não soa', first['evidence']['recommendation'])
        # The diagram still shows the whole chord; the third was played, never supplied.
        self.assertEqual(first['pitch_classes'], [0, 4, 7, 10])
        self.assertLess(first['confidence'], .98)
        self.assertEqual(result['statistics']['omitted_fifth_chords'], 1)

    def test_seventh_on_top_and_ninth_without_fifth(self):
        _, result = analyze(in_context(8, [50, 60, 66], 38) + block(12, [55, 59, 65, 69]) + bass(12, 43))
        self.assertEqual(event_at(result, 8), (2, '7'))
        self.assertEqual(event_at(result, 12), (7, '9'))

    def test_complete_chord_always_wins_and_third_is_never_invented(self):
        # C-E-A is A minor (complete), not C6 without its fifth, even over a C bass.
        _, result = analyze(block(0, [57, 60, 64]) + bass(0, 45))
        self.assertEqual([(e['root'], e['quality']) for e in result['events']], [(9, 'm')])
        # Root, fifth and seventh without a third stay without a chord.
        _, result = analyze(block(0, [48, 55, 58]) + block(4, [48, 55, 58]) + bass(0, 36, 8))
        self.assertEqual(result['events'], [])

    def test_altered_fifths_and_triads_are_never_completed(self):
        # C-Eb-Bb is Cm7 without its fifth, never Cm7b5 (a diminished fifth is never assumed).
        choice = harmony._choose_candidate({0, 3, 10}, {0: 1, 3: 1, 10: 1}, 0, context_advice=True)
        self.assertEqual((choice['quality'], choice['omitted_fifth']), ('m7', True))
        # A triad needs its three notes.
        self.assertIsNone(harmony._choose_candidate({0, 4}, {0: 1, 4: 1}, 0, context_advice=True))
        # C-E-Bb never reads as C7b5/C7#5 (altered fifths are not optional).
        choice = harmony._choose_candidate({0, 4, 10}, {0: 1, 4: 1, 10: 1}, 0, context_advice=True)
        self.assertEqual((choice['root'], choice['quality']), (0, '7'))

    def test_bass_breaks_ties_and_a_real_tie_is_ambiguous(self):
        # E-G-D: Em7 without fifth? No: the third is G, fifth B absent; C9 would need C and E...
        # Eb-G-Db could be Eb7 (no fifth); with a Bb bass it is still Eb7 over its fifth.
        choice = harmony._choose_candidate({3, 7, 1}, {3: 1, 7: 1, 1: 1}, 3, context_advice=True)
        self.assertEqual((choice['root'], choice['quality']), (3, '7'))
        self.assertFalse(choice['ambiguous'])

    def test_legacy_profiles_never_omit_the_fifth(self):
        notes = block(0, [48, 52, 58]) + block(4, [48, 52, 58]) + bass(0, 36, 8)
        _, legacy = analyze(notes, context=False)
        self.assertEqual(legacy['events'], [])
        self.assertNotIn('key_changes', legacy)


class NewQualityTests(unittest.TestCase):
    def test_new_qualities_are_read_from_block_chords(self):
        cases = {'13': [48, 52, 55, 57, 58], '7b9': [48, 52, 55, 58, 61], '7#9': [48, 52, 55, 58, 63],
                 'm11': [48, 51, 53, 55, 58], '69': [48, 50, 52, 55, 57], '7#5': [48, 52, 56, 58], '7b5': [48, 52, 54, 58]}
        for quality, pitches in cases.items():
            with self.subTest(quality=quality):
                _, result = analyze(in_context(8, pitches, 36))
                self.assertEqual(event_at(result, 8), (0, quality))

    def test_same_notes_keep_the_common_reading(self):
        # C-E-G-A is C6 or Am7 (both common); never a new exotic name.
        choice = harmony._choose_candidate({0, 4, 7, 9}, {0: 1, 4: 1, 7: 1, 9: 1}, 0, context_advice=True)
        self.assertEqual((choice['root'], choice['quality']), (0, '6'))
        # C 6/9 and A m11 share notes: the bass names it, and without a bass it stays ambiguous.
        pcs = {0, 2, 4, 7, 9}
        self.assertEqual(harmony._choose_candidate(pcs, {p: 1 for p in pcs}, 9, context_advice=True)['quality'], 'm11')
        self.assertTrue(harmony._choose_candidate(pcs, {p: 1 for p in pcs}, None, context_advice=True)['ambiguous'])

    def test_five_note_colours_are_not_read_from_accumulated_sustain(self):
        weights = {0: 1, 4: 1, 7: 1, 9: 1, 10: 1}
        self.assertIsNone(harmony._choose_candidate(set(weights), weights, 0, context_advice=True, allow_extended=False))
        self.assertIsNone(harmony._choose_candidate(set(weights), weights, 0, context_advice=False))

    def test_musicxml_kinds_and_degrees_for_musescore(self):
        expected = {'13': ('dominant-13th', []), '7b9': ('dominant', [('9', '-1', 'add')]),
                    '7#9': ('dominant', [('9', '1', 'add')]), 'm11': ('minor-11th', []),
                    '69': ('major-sixth', [('9', '0', 'add')]), '7#5': ('dominant', [('5', '1', 'alter')]),
                    '7b5': ('dominant', [('5', '-1', 'alter')])}
        for quality, (kind, degrees) in expected.items():
            with self.subTest(quality=quality):
                measure = E.Element('measure')
                score_musicxml.emit_harmony(measure, {'tick': 0, 'root': 7, 'quality': quality}, 0, engine.spellings(0))
                h = measure.find('harmony')
                self.assertEqual((h.findtext('kind'), h.find('kind').get('text')), (kind, quality))
                self.assertEqual([(d.findtext('degree-value'), d.findtext('degree-alter'), d.findtext('degree-type'))
                                  for d in h.findall('degree')], degrees)

    def test_diagrams_of_new_qualities_are_second_inversion_with_every_tone(self):
        voicings = {'13': [55, 57, 58, 60, 64], '7b9': [55, 58, 60, 61, 64], '7#9': [55, 58, 60, 63, 64],
                    'm11': [55, 58, 60, 63, 65], '69': [55, 57, 60, 62, 64], '7#5': [56, 58, 60, 64], '7b5': [54, 58, 60, 64]}
        for quality, pitches in voicings.items():
            with self.subTest(quality=quality):
                self.assertEqual(engine.keyboard_asset(0, quality, 2)[2], pitches)
                self.assertEqual(harmony.second_inversion_pitches(0, quality), pitches)
        # 7#9 in first inversion stands on the major third, not on the #9.
        self.assertEqual(engine.keyboard_asset(0, '7#9', 1)[2][0] % 12, 4)


class SlashBassTests(unittest.TestCase):
    def test_sustained_inversion_writes_the_slash_bass(self):
        notes = (block(0, [60, 64, 67]) + bass(0, 36) + block(4, [55, 59, 62]) + bass(4, 35) +
                 block(8, [57, 60, 64]) + bass(8, 33) + block(12, [62, 66, 69]) + bass(12, 42))
        _, result = analyze(notes, ((0, 0x58, bytes([4, 2, 24, 8])), (0, 0x59, bytes([1, 0]))))
        self.assertEqual(names(result), [(0, 'C', '', None), (4, 'G', '', 'B'), (8, 'A', 'm', None), (12, 'D', '', 'F#')])
        self.assertEqual(result['events'][1]['evidence']['slash_bass'], 'inversion')
        self.assertEqual(result['events'][1]['bass'], 11)

    def test_short_passing_bass_does_not_create_a_slash(self):
        # The root holds most of the chord; a quick third before it is a passing note.
        notes = block(0, [60, 64, 67], 4) + bass(0, 40, .5) + bass(.5, 36, 3.5) + block(4, [65, 69, 72]) + bass(4, 41)
        _, result = analyze(notes)
        self.assertIsNone(result['events'][0].get('bass'))
        # A walking bass that starts on the root never becomes a slash chord.
        notes = block(0, [60, 64, 67], 4) + bass(0, 36, 1) + bass(1, 40, 1) + bass(2, 43, 1) + bass(3, 40, 1)
        _, result = analyze(notes)
        self.assertIsNone(result['events'][0].get('bass'))

    def test_pedal_held_under_chord_changes(self):
        notes = block(0, [60, 64, 67]) + block(4, [65, 69, 72]) + block(8, [62, 67, 71]) + block(12, [60, 64, 67]) + bass(0, 36, 16)
        _, result = analyze(notes)
        self.assertEqual(names(result), [(0, 'C', '', None), (4, 'F', '', 'C'), (8, 'G', '', 'C'), (12, 'C', '', None)])
        self.assertEqual(result['events'][1]['evidence']['slash_bass'], 'inversion')
        self.assertEqual(result['events'][2]['evidence']['slash_bass'], 'pedal')

    def test_transposition_moves_the_bass_and_keeps_the_diagram_on_the_chord(self):
        source = {'key': {'fifths': 1, 'minor': False}, 'key_changes': [], 'context_advice': True,
                  'events': [dict(tick=0, root=2, quality='', pitch_classes=[2, 6, 9], bass=6, bass_spelling='F#', root_spelling='D')]}
        moved = engine.transpose_harmony(source, 2)
        chord = moved['events'][0]
        self.assertEqual((chord['root_spelling'], chord['bass'], chord['bass_spelling']), ('E', 8, 'G#'))
        self.assertEqual(engine.keyboard_asset(chord['root'], chord['quality'])[2], engine.keyboard_asset(4, '')[2])
        # Legacy (reviewed) harmonies also move a slash bass edited by the user.
        legacy = engine.transpose_harmony(dict(source, context_advice=False), -2)
        self.assertEqual((legacy['events'][0]['root_spelling'], legacy['events'][0]['bass_spelling']), ('C', 'E'))

    def test_slash_is_written_in_musicxml_and_repeats_keep_it_apart(self):
        measure = E.Element('measure')
        score_musicxml.emit_harmony(measure, {'tick': 0, 'root': 7, 'quality': '7', 'root_spelling': 'G', 'bass_spelling': 'B'}, 0, engine.spellings(0))
        self.assertEqual(measure.findtext('harmony/bass/bass-step'), 'B')
        import score_repeats
        self.assertNotEqual(score_repeats._chord_key({'root': 7, 'quality': ''}), score_repeats._chord_key({'root': 7, 'quality': '', 'bass': 11}))


class KeyChangeTests(unittest.TestCase):
    PROGRESSION_C = ([60, 64, 67], [65, 69, 72], [67, 71, 74], [60, 64, 67])

    def song(self, shift_from_bar=None, shift=1, bars=32):
        notes = []
        for bar in range(bars):
            moved = shift if shift_from_bar is not None and bar >= shift_from_bar else 0
            chord = [p + moved for p in self.PROGRESSION_C[bar % 4]]
            notes += block(bar * 4, chord)
            notes += bass(bar * 4, chord[0] - 24)
            notes += [(bar * 4 + i, chord[i % 3] + 12, 1, MELODY) for i in range(4)]
        return notes

    def test_all_key_signature_metas_become_key_changes(self):
        metas = ((0, 0x58, bytes([4, 2, 24, 8])), (0, 0x59, bytes([0, 0])), (96 * PPQ, 0x59, bytes([256 - 5, 0])))
        _, result = analyze(self.song(24, 1), metas)
        self.assertEqual(result['key']['fifths'], 0)
        self.assertEqual([(c['tick'], c['fifths'], c['minor'], c['source']) for c in result['key_changes']],
                         [(96 * PPQ, -5, False, 'midi_key_signature')])
        self.assertEqual(result['key_changes'][0]['measure'], 25)
        self.assertIn('key_change', [d['kind'] for d in result['diagnostics']])

    def test_half_step_modulation_in_the_last_chorus_is_inferred_on_the_bar_line(self):
        _, result = analyze(self.song(24, 1))
        self.assertEqual(result['key']['fifths'], 0)
        changes = result['key_changes']
        self.assertEqual(len(changes), 1)
        self.assertEqual((changes[0]['tick'], changes[0]['fifths'], changes[0]['minor']), (96 * PPQ, -5, False))
        self.assertGreaterEqual(changes[0]['confidence'], harmony.DOUBTFUL_KEY_CHANGE)
        self.assertEqual(changes[0]['source'], 'inferred_local_windows')
        self.assertTrue(any('Réb maior' in w and 'compasso 25' in w for w in result['warnings']))
        # The chords after the modulation are spelled in the new key (Db, Gb, Ab), not C#, F#, G#.
        after = [e['root_spelling'] for e in result['events'] if e['tick'] >= 96 * PPQ]
        self.assertEqual(sorted(set(after)), ['Ab', 'Db', 'Gb'])

    def test_short_borrowing_does_not_modulate(self):
        notes = self.song()
        # Two bars of Ab and Bb (borrowed from C minor) in the middle.
        notes = [n for n in notes if not 40 <= n[0] < 48 or n[3] != CHORDS]
        notes += block(40, [56, 60, 63]) + block(44, [58, 62, 65])
        _, result = analyze(notes)
        self.assertEqual(result['key_changes'], [])

    def test_steady_song_and_legacy_have_no_key_changes(self):
        self.assertEqual(analyze(self.song())[1]['key_changes'], [])
        self.assertNotIn('key_changes', analyze(self.song(24, 1), context=False)[1])

    def test_generation_writes_the_new_key_signature(self):
        parsed, result = analyze(self.song(24, 1))
        config = dict(melody_sources=[dict(track=3, channel=3, program=73)], diagram_inversion=2, filename='teste.mid')
        with tempfile.TemporaryDirectory() as directory:
            xml, report = engine.build_musicxml(parsed, config, copy.deepcopy(result), Path(directory), 'Teste')
            tree = E.parse(xml)
        keys = [(int(m.get('number')), int(k.findtext('fifths'))) for m in tree.iter('measure') for k in m.iter('key')]
        self.assertEqual(keys, [(1, 0), (25, -5)])
        roots = [(h.findtext('root/root-step'), h.findtext('root/root-alter')) for h in tree.iter('harmony')]
        self.assertIn(('D', '-1'), roots)
        self.assertNotIn(('C', '1'), roots)

    def test_review_point_for_the_modulation(self):
        parsed, result = analyze(self.song(24, 1), melody=[dict(track=3, channel=3, program=73)])
        config = dict(melody_sources=[dict(track=3, channel=3, program=73)], solo_sources=[], filename='teste.mid')
        points, _, _ = engine.review_points(parsed, config, result)
        point = next(p for p in points if p['kind'] in ('key_change', 'key_change_uncertain'))
        self.assertEqual(point['measure'], 25)
        self.assertIn('Réb maior (meio tom acima)', point['message'])


class FunctionalSpellingTests(unittest.TestCase):
    def spell(self, root, quality, fifths=0, minor=False, following=None):
        return harmony.functional_root_spelling(root, quality, fifths, minor, following)

    def test_chromatic_roots_follow_their_function(self):
        self.assertEqual(self.spell(1, ''), 'Db')            # Neapolitan in C
        self.assertEqual(self.spell(1, 'dim7'), 'C#')        # leading tone to D
        self.assertEqual(self.spell(1, 'dim', following=(2, 'm')), 'C#')
        self.assertEqual(self.spell(4, '7', following=(9, 'm')), 'E')   # diatonic E7 in C, never Fb7
        self.assertEqual(self.spell(6, '7', following=(11, 'm')), 'F#')  # V/iii
        self.assertEqual(self.spell(6, 'm7', following=(11, '7')), 'F#')  # ii of V/iii
        self.assertEqual(self.spell(1, '7', following=(0, '')), 'Db')    # tritone substitute
        self.assertEqual(self.spell(8, ''), 'Ab'); self.assertEqual(self.spell(10, ''), 'Bb'); self.assertEqual(self.spell(3, ''), 'Eb')
        self.assertEqual(self.spell(8, '7', following=(1, '')), 'Ab')    # V of the Db (bII)

    def test_natural_letter_and_key_signature_win(self):
        self.assertEqual(self.spell(4, '', -3, True), 'E')     # E in C minor, never Fb
        self.assertEqual(self.spell(11, '7', -3, True), 'B')   # leading tone of C minor
        self.assertEqual(self.spell(1, 'm', 2), 'C#')           # C# in D major follows the signature
        self.assertEqual(self.spell(10, '', -1), 'Bb')          # Bb in F major
        self.assertEqual(self.spell(0, '', 4), 'C')             # bVI of E major is C, never B#
        self.assertEqual(self.spell(5, '', 4), 'F')             # Neapolitan of E is F

    def test_score_uses_the_spelling_and_the_user_preference_on_top(self):
        parsed, result = analyze(block(0, [60, 64, 67]) + block(4, [61, 65, 68]) + block(8, [60, 64, 67]) + bass(0, 36, 12),
                                 ((0, 0x58, bytes([4, 2, 24, 8])), (0, 0x59, bytes([0, 0]))))
        self.assertEqual([e['root_spelling'] for e in result['events']], ['C', 'Db', 'C'])
        measure = E.Element('measure')
        score_musicxml.set_chord_notation({'accidentals': 'sharps'})
        try:
            score_musicxml.emit_harmony(measure, result['events'][1], 0, engine.spellings(0))
        finally:
            score_musicxml.set_chord_notation(None)
        self.assertEqual((measure.findtext('harmony/root/root-step'), measure.findtext('harmony/root/root-alter')), ('C', '1'))

    def test_transposition_respells_functionally(self):
        source = {'key': {'fifths': 0, 'minor': False}, 'key_changes': [], 'context_advice': True,
                  'events': [dict(tick=0, root=1, quality='dim7', pitch_classes=[1, 4, 7, 10]), dict(tick=PPQ, root=2, quality='m', pitch_classes=[2, 5, 9])]}
        moved = engine.transpose_harmony(source, 5)   # to F major: Ddim7? no: F#dim7 → Gm
        self.assertEqual([c['root_spelling'] for c in moved['events']], ['F#', 'G'])


class MelodyTieBreakTests(unittest.TestCase):
    def test_strong_beat_melody_breaks_a_tie_between_readings_of_the_same_notes(self):
        notes = block(0, [60, 64, 67, 69], 4) + block(4, [60, 64, 67, 69], 4)
        melody = [(0, 81, 2, MELODY), (2, 79, 2, MELODY), (4, 81, 2, MELODY), (6, 79, 2, MELODY)]
        source = [dict(track=3, channel=3, program=73)]
        _, plain = analyze(notes)
        _, guided = analyze(notes + melody, melody=source)
        self.assertTrue(guided['events'][0]['evidence']['ambiguous'])
        self.assertEqual(guided['events'][0]['pitch_classes'], plain['events'][0]['pitch_classes'])
        self.assertEqual((guided['events'][0]['root'], guided['events'][0]['quality']), (9, 'm7'))
        self.assertIn('melodia no tempo forte', guided['events'][0]['evidence']['recommendation'])

    def test_melody_never_adds_or_removes_chord_tones(self):
        notes = block(0, [60, 64, 67], 4) + block(4, [60, 64, 67], 4)
        melody = [(0, 81, 4, MELODY), (4, 81, 4, MELODY)]
        _, guided = analyze(notes + melody, melody=[dict(track=3, channel=3, program=73)])
        self.assertEqual([(e['root'], e['quality']) for e in guided['events']], [(0, '')])


class WorkerAnalysisTests(unittest.TestCase):
    def test_analysis_sends_slash_bass_and_key_changes_to_the_app(self):
        import subprocess
        notes = KeyChangeTests().song(24, 1)
        notes = [n for n in notes if not (n[3] == BASS and 4 <= n[0] < 8)] + bass(4, 45)  # F chord over A
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            midi = directory / 'modula.mid'; write_midi(midi, notes)
            request = directory / 'request.json'
            request.write_text(json.dumps({'mode': 'analyze', 'workspace': str(directory / 'w'), 'inputMidi': str(midi),
                                           'melodySources': [dict(track=3, channel=3, program=73)]}), encoding='utf8')
            output = subprocess.run([sys.executable, '-B', str(ROOT / 'engine' / 'worker.py'), str(request)],
                                    capture_output=True, text=True, encoding='utf8', timeout=120)
            result = [json.loads(line) for line in output.stdout.splitlines() if line.startswith('{')][-1]
        self.assertEqual(result['kind'], 'result', output.stderr)
        data = result['data']
        self.assertEqual([(c['measure'], c['fifths']) for c in data['keyChanges']], [(25, -5)])
        self.assertIn({'quarter': 4.0, 'root': 'F', 'bass': 'A'}, [{k: c.get(k) for k in ('quarter', 'root', 'bass')} for c in data['chords']])
        self.assertTrue(any(p['kind'].startswith('key_change') for p in data['reviewPoints']))


if __name__ == '__main__':
    unittest.main()
