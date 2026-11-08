"""Precisão de notas, ritmo e grafia da melodia (frente B). Casos sintéticos, sem MIDIs do usuário."""
from pathlib import Path
import io, os, sys, tempfile, unittest
import xml.etree.ElementTree as E
from contextlib import redirect_stdout

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScoreMelodyAccuracy'))
import batch_score as engine
import melody_rhythm as rhythm
import melody_spelling
import score_musicxml

PPQ = 480
DIV = engine.DIV
VOICE = dict(track=1, channel=1, program=0)
BASS = dict(track=2, channel=2, program=33)


def note(quarter, pitch, duration=.9, source=VOICE, velocity=90):
    return dict(source, start=round(quarter * PPQ), end=round((quarter + duration) * PPQ),
                duration=round(duration * PPQ), pitch=pitch, velocity=velocity)


def tempo_event(bpm):
    return dict(tick=0, track=0, kind='meta', type=81, data=round(60000000 / bpm).to_bytes(3, 'big'), order=0)


def meter_event(numerator, denominator, tick=0):
    return dict(tick=tick, track=0, kind='meta', type=88, data=bytes([numerator, denominator.bit_length() - 1, 24, 8]), order=1)


def document(notes, bpm=120, meter=(4, 4)):
    events = [tempo_event(bpm)] + ([meter_event(*meter)] if meter else [])
    return dict(path='sintetico.mid', sha256=None, ppq=PPQ, notes=notes, format=1, tracks=[], warnings=[], events=events,
                end_tick=max(n['end'] for n in notes))


def melody(parsed, config=None):
    config = dict(config or {}, melody_sources=(config or {}).get('melody_sources', [VOICE]))
    _, meters, _ = engine.meta(parsed); origin, end, _ = engine.make_bars(parsed, meters)
    events, grids, report = engine.melody_events(parsed, config, origin, end)
    return events, grids, report, origin


def scale(start, count, step=1.0, base=60, duration=.9):
    return [note(start + i * step, base + (i % 5) * 2, duration) for i in range(count)]


class InnerVoiceTests(unittest.TestCase):
    def test_lower_offbeat_eighths_under_held_upper_notes_are_masked(self):
        # Mínimas em C5 com colcheias em C4 nos contratempos, na mesma pista.
        notes = []
        for bar in range(12):
            notes.append(note(bar * 2, 72 + bar % 3, 2))
            notes += [note(bar * 2 + .5, 60, .5), note(bar * 2 + 1.5, 60, .5)]
        events, _, report, origin = melody(document(notes))
        self.assertEqual({e['pitch'] for e in events}, {72, 73, 74})
        self.assertEqual(report['masked_inner_attacks'], 24)
        self.assertTrue(all(e['end'] - e['start'] == 2 * DIV for e in events[:-1]))

    def test_legato_overlap_and_fast_runs_are_kept(self):
        # A nota aguda anterior termina logo depois do ataque grave (note-off atrasado): não é voz interna.
        notes = [note(i * .5, 76 - (i % 4), .58) for i in range(32)]
        events, _, report, _ = melody(document(notes))
        self.assertEqual(len(events), 32)
        self.assertNotIn('masked_inner_attacks', report)

    def test_other_sources_are_not_masked(self):
        notes = [note(i, 60 + i % 3) for i in range(24)] + [note(0, 84, 24, source=BASS)]
        kept, masked = rhythm.mask_inner_by_source(notes, PPQ)
        self.assertEqual(masked, [])


class SwingTests(unittest.TestCase):
    def swung(self):
        return [note(b + f, 67 + (i % 6), .3) for i, (b, f) in enumerate((b, f) for b in range(24) for f in (0, 2 / 3))]

    def test_swing_ratio_is_estimated_and_eighths_are_written_straight(self):
        events, grids, report, _ = melody(document(self.swung()))
        self.assertAlmostEqual(report['swing']['ratio'], 2 / 3, places=2)
        self.assertEqual(sorted({e['start'] % DIV for e in events}), [0, DIV // 2])
        self.assertFalse(report['triplet_beats_original'])

    def test_sixteenths_are_not_swing(self):
        quarters = [b + f for b in range(24) for f in ((0, .75) if b % 2 else (0, .25, .5, .75))]
        self.assertIsNone(rhythm.swing_estimate(quarters))

    def test_musicxml_has_swing_text_and_playback_ratio(self):
        parsed = document(self.swung())
        harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=0, root=0, quality='', confidence=1., pitch_classes=[0, 4, 7])])
        with tempfile.TemporaryDirectory() as folder, redirect_stdout(io.StringIO()):
            path, report = engine.build_musicxml(parsed, {'melody_sources': [VOICE]}, harmony, Path(folder), 'Swing')
            tree = E.parse(path)
        self.assertIn('Swing', [w.text for w in tree.iter('words')])
        swing = tree.find('.//sound/swing')
        self.assertEqual((swing.findtext('first'), swing.findtext('second'), swing.findtext('swing-type')), ('67', '33', 'eighth'))


class GridTests(unittest.TestCase):
    def test_anticipation_belongs_to_the_next_beat(self):
        tempo = rhythm.TempoMap([{'tick': 0, 'bpm': 120}], PPQ)
        self.assertEqual(rhythm.attribute(4.95, tempo)[0], 5)       # 25 ms antes do tempo 5
        self.assertEqual(rhythm.attribute(4.80, tempo)[0], 4)
        # Andamento lento: a tolerância de 40 ms vale uma fração menor do tempo (50 ms antes fica no tempo 4).
        slow = rhythm.TempoMap([{'tick': 0, 'bpm': 60}], PPQ)
        self.assertEqual(rhythm.attribute(4.95, slow)[0], 4)
        self.assertEqual(rhythm.attribute(4.97, slow)[0], 5)

    def test_live_jitter_does_not_create_fine_grids_or_isolated_triplets(self):
        # Semicolcheias tocadas ao vivo com até 45 ms de desvio a 90 BPM.
        jitter = [0, .07, -.06, .05, -.04, .06, -.07, .03]
        notes = [note(b + f + jitter[(b * 4 + k) % 8] * .1 * 1.5, 60 + k, .2) for b in range(16) for k, f in enumerate((0, .25, .5, .75))]
        events, grids, report, _ = melody(document(notes, bpm=90))
        self.assertTrue(all(g == 4 for g in grids.values()), grids)
        self.assertEqual(report['triplet_beats_original'], [])

    def test_quantized_triplet_with_both_thirds_is_kept_in_binary_music(self):
        notes = scale(0, 8, .5, duration=.4) + [note(4 + k / 3, 70 + k, .3) for k in range(3)] + scale(5, 12, .5, duration=.4)
        events, grids, report, origin = melody(document(notes))
        self.assertIn(4, report['triplet_beats_original'])
        self.assertIn(4 * DIV + 320 - origin, [e['start'] for e in events])

    def test_partial_triplet_is_accepted_only_when_clearly_better(self):
        tempo = rhythm.TempoMap([{'tick': 0, 'bpm': 100}], PPQ)
        grids, *_ = rhythm.choose_grids({0: [0, .5], 1: [0, 2 / 3], 2: [0, .5]}, tempo)
        self.assertEqual(grids[1], 3)       # 2/3 exato: tercina (só a segunda colcheia de tercina)
        grids, *_ = rhythm.choose_grids({0: [0, .5], 1: [0, .75], 2: [0, .5]}, tempo)
        self.assertEqual(grids[1], 4)       # binário exato: nunca tercina

    def test_quarter_note_triplet_over_two_beats(self):
        notes = scale(0, 8, duration=.9) + [note(8 + k * 2 / 3, 72 + k, .6) for k in range(3)] + scale(10, 14, duration=.9)
        events, grids, report, origin = melody(document(notes))
        self.assertEqual(report['quarter_triplet_groups'], [8 - origin // DIV])
        first = 8 - origin // DIV
        self.assertEqual((grids[first], grids[first + 1]), (rhythm.PAIR_FIRST, rhythm.PAIR_SECOND))
        written = list(score_musicxml.pieces(first * DIV, first * DIV + 640, grids))
        self.assertEqual(written, [(first * DIV, 640, ('pair', first * DIV))])
        measure = E.Element('measure')
        event = {'start': first * DIV, 'end': first * DIV + 640, 'pitch': 72}
        for start, dur, tr in written: score_musicxml.emit_note(measure, start, dur, tr, event, engine.spellings(0))
        self.assertEqual(measure.findtext('.//normal-type'), 'quarter')
        self.assertEqual(measure.find('.//tuplet').get('type'), 'start')

    def test_systematically_early_playing_is_compensated(self):
        notes = [note(b + f - .07, 60 + b % 4 + k, .2) for b in range(1, 17) for k, f in enumerate((0, .5))]
        events, grids, report, origin = melody(document(notes, bpm=92))
        self.assertTrue(report.get('timing_offset_windows'))
        self.assertEqual(sorted({(e['start'] + origin) % DIV for e in events}), [0, DIV // 2])


class MeterTests(unittest.TestCase):
    def waltz(self):
        notes = []
        for bar in range(24):
            notes.append(note(bar * 3, 40 + bar % 2, 2.8, source=BASS))
            notes += [note(bar * 3 + k, 64 + k, .9) for k in range(3)]
            notes += [note(bar * 3 + k, 55 + 3 * j, .5, source=dict(track=3, channel=3, program=0)) for k in (1, 2) for j in range(3)]
        return notes

    def test_missing_time_signature_is_estimated_from_accents(self):
        parsed = document(self.waltz(), meter=None)
        _, meters, _ = engine.meta(parsed)
        self.assertEqual((meters[0]['numerator'], meters[0]['denominator']), (3, 4))
        points, ready, _ = engine.review_points(parsed, {'melody_sources': [VOICE]}, {'events': []})
        self.assertIn('meter_guess', [p['kind'] for p in points])

    def test_declared_binary_meter_against_ternary_accents_is_flagged(self):
        parsed = document(self.waltz(), meter=(4, 4))
        _, meters, _ = engine.meta(parsed)
        self.assertEqual(meters[0]['numerator'], 4)       # o compasso declarado é mantido
        points, _, _ = engine.review_points(parsed, {'melody_sources': [VOICE]}, {'events': []})
        self.assertIn('meter_guess', [p['kind'] for p in points])

    def test_plain_quarter_notes_keep_four_four(self):
        _, meters, _ = engine.meta(document(scale(0, 64), meter=None))
        self.assertEqual(meters[0]['numerator'], 4)


class MetricDivisionTests(unittest.TestCase):
    def split(self, start, end, meter):
        bar = {'start': 0, 'end': round(meter[0] * 4 / meter[1] * DIV), 'meter': meter, 'partial': False}
        return [(t, d) for t, d, _ in score_musicxml.pieces(start, end, {}, bar)]

    def test_dotted_quarter_on_the_second_beat_of_six_eight(self):
        self.assertEqual(self.split(1440, 2880, (6, 8)), [(1440, 1440)])
        self.assertEqual(self.split(0, 2880, (6, 8)), [(0, 2880)])

    def test_syncopation_eighth_quarter_eighth_without_tie(self):
        self.assertEqual(self.split(480, 1440, (4, 4)), [(480, 960)])
        self.assertEqual(self.split(480, 1440, (2, 4)), [(480, 960)])

    def test_four_four_splits_at_the_middle(self):
        self.assertEqual(self.split(960, 2880, (4, 4)), [(960, 960), (1920, 960)])
        self.assertEqual(self.split(0, 2880, (4, 4)), [(0, 2880)])
        self.assertEqual(self.split(0, 1440, (4, 4)), [(0, 1440)])

    def test_odd_meters_follow_their_groups(self):
        self.assertEqual(self.split(0, 2400, (5, 8)), [(0, 1440), (1440, 960)])
        self.assertEqual(self.split(1920, 3360, (7, 8)), [(1920, 1440)])

    def test_legacy_callers_keep_old_division(self):
        self.assertEqual([(t, d) for t, d, _ in score_musicxml.pieces(1440, 2880, {})], [(1440, 480), (1920, 960)])


class PickupTests(unittest.TestCase):
    def test_melody_alone_before_the_first_full_bar_starts_a_pickup_measure(self):
        notes = [note(2, 67, .9), note(3, 69, .9)]
        notes += [note(4 + i, 72 + i % 3) for i in range(28)]
        notes += [note(4 + 2 * i, 48, 1.9, source=BASS) for i in range(14)]
        parsed = document(notes)
        _, meters, _ = engine.meta(parsed)
        origin, end, bars = engine.make_bars(parsed, meters)
        self.assertEqual(origin, 2 * DIV)
        self.assertEqual((bars[0]['start'], bars[0]['end'], bars[0].get('pickup')), (0, 2 * DIV, True))
        self.assertEqual(bars[1]['start'], 2 * DIV)
        harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=4 * PPQ, root=0, quality='', confidence=1., pitch_classes=[0, 4, 7])])
        with tempfile.TemporaryDirectory() as folder, redirect_stdout(io.StringIO()):
            path, report = engine.build_musicxml(parsed, {'melody_sources': [VOICE]}, harmony, Path(folder), 'Anacruse')
            first = E.parse(path).find('.//part/measure')
        self.assertEqual(first.get('implicit'), 'yes')
        self.assertEqual(sum(int(n.findtext('duration')) for n in first.findall('note')), 2 * DIV)

    def test_accompaniment_before_the_bar_line_keeps_full_measures(self):
        notes = [note(2, 67), note(2, 48, source=BASS), note(3, 69)] + [note(4 + i, 72 + i % 3) for i in range(28)]
        _, meters, _ = engine.meta(document(notes))
        origin, _, bars = engine.make_bars(document(notes), meters)
        self.assertEqual(origin, 0)
        self.assertFalse(bars[0].get('pickup'))


class ProgramChangeTests(unittest.TestCase):
    def test_patch_change_on_the_melody_track_keeps_the_notes(self):
        notes = [note(i, 64 + i % 5) for i in range(24)] + [note(24 + i, 76 + i % 3, source=dict(VOICE, program=73)) for i in range(12)]
        events, _, report, _ = melody(document(notes))
        self.assertEqual(len(events), 36)
        self.assertEqual(report['program_changes_followed'][0]['to_program'], 73)

    def test_claimed_or_disabled_program_is_not_followed(self):
        notes = [note(i, 64 + i % 5) for i in range(24)] + [note(24 + i, 76 + i % 3, source=dict(VOICE, program=73)) for i in range(12)]
        events, _, report, _ = melody(document(notes), {'follow_program_changes': False})
        self.assertEqual(len(events), 24)
        self.assertNotIn('program_changes_followed', report)


class LegatoTests(unittest.TestCase):
    def test_threshold_is_in_milliseconds_or_share_of_the_note(self):
        self.assertTrue(rhythm.legato_fill(20, 480, PPQ, 500))      # ~21 ms
        self.assertTrue(rhythm.legato_fill(100, 480, PPQ, 500))     # 21% da nota
        self.assertFalse(rhythm.legato_fill(150, 480, PPQ, 500))    # 156 ms e 31% da nota

    def test_short_gap_is_filled_and_repeated_rest_is_preserved(self):
        # Semínimas com uma pausa de 0,15 tempo (150 ms, que o legato preencheria) (pausa visível de semicolcheia) só no mesmo ponto de cada compasso.
        notes = []
        for bar in range(8):
            notes += [note(bar * 4, 64, .85), note(bar * 4 + 1, 65, .97), note(bar * 4 + 2, 67, .97), note(bar * 4 + 3, 69, .97)]
        events, _, report, origin = melody(document(notes, bpm=60))
        first = [e for e in events if e['pitch'] == 64]
        self.assertTrue(all(e['end'] - e['start'] == 720 for e in first), [e['end'] - e['start'] for e in first])
        self.assertTrue(all(e['end'] - e['start'] == DIV for e in events if e['pitch'] == 65))
        self.assertEqual(report['preserved_rests'], 8)


class SpellingTests(unittest.TestCase):
    def spell(self, pitches, chord=None, fifths=0, minor=False):
        notes = [{'start': i * DIV, 'end': (i + 1) * DIV, 'pitch': p} for i, p in enumerate(pitches)]
        chords = [dict(chord, tick=0)] if chord else []
        from batch_harmony import CHORD_INTERVALS
        melody_spelling.spell_notes(notes, chords, lambda _: (fifths, minor), lambda _: engine.spellings(fifths, minor),
                                    lambda q: CHORD_INTERVALS[q])
        return [n['spelling'] for n in notes]

    def test_chord_tones_follow_the_chord(self):
        self.assertEqual(self.spell([68], dict(root=4, quality='7'))[0], ('G', 1))            # Dó maior, E7
        self.assertEqual(self.spell([63], dict(root=11, quality='7'), fifths=1)[0], ('D', 1))  # Sol maior, B7
        self.assertEqual(self.spell([70], dict(root=10, quality='', root_spelling='Bb'))[0], ('B', -1))
        self.assertEqual(self.spell([68], dict(root=0, quality='aug'))[0], ('G', 1))

    def test_chromatic_passing_notes_follow_the_direction(self):
        self.assertEqual(self.spell([60, 61, 62])[1], ('C', 1))
        self.assertEqual(self.spell([62, 61, 60])[1], ('D', -1))

    def test_non_chord_tones_stay_near_the_key(self):
        self.assertEqual(self.spell([66], fifths=-3)[0], ('G', -1))    # Mib maior: Solb, não Fá#
        self.assertEqual(self.spell([66], fifths=2)[0], ('F', 1))

    def test_courtesy_accidental_after_an_altered_bar(self):
        notes = [{'start': 0, 'end': DIV, 'pitch': 68, 'spelling': ('G', 1)},
                 {'start': 4 * DIV, 'end': 5 * DIV, 'pitch': 67, 'spelling': ('G', 0)},
                 {'start': 8 * DIV, 'end': 9 * DIV, 'pitch': 67, 'spelling': ('G', 0)}]
        bars = [{'start': i * 4 * DIV, 'end': (i + 1) * 4 * DIV} for i in range(3)]
        self.assertEqual(melody_spelling.courtesy_accidentals(notes, bars, lambda _: 0), 1)
        self.assertEqual([n.get('courtesy') for n in notes], [None, 'natural', None])

    def test_musicxml_writes_the_contextual_spelling(self):
        notes = [note(i, [64, 68, 71, 74][i % 4]) for i in range(24)]
        harmony = dict(key={'fifths': 0, 'minor': False}, events=[dict(tick=0, root=4, quality='7', confidence=1., pitch_classes=[4, 8, 11, 2])])
        with tempfile.TemporaryDirectory() as folder, redirect_stdout(io.StringIO()):
            path, report = engine.build_musicxml(document(notes), {'melody_sources': [VOICE]}, harmony, Path(folder), 'Grafia')
            steps = {(p.findtext('step'), p.findtext('alter')) for p in E.parse(path).iter('pitch')}
        self.assertIn(('G', '1'), steps)
        self.assertNotIn(('A', '-1'), steps)


class AudioTests(unittest.TestCase):
    def test_short_notes_need_strength_and_continuity(self):
        import audio_transcription as at
        events = [(0, 30, 60, .6), (20, 27, 62, .6), (100, 107, 80, .6), (200, 207, 64, .2), (210, 240, 65, .6)]
        self.assertEqual(at.filter_short_notes(events), [(0, 30, 60, .6), (20, 27, 62, .6), (210, 240, 65, .6)])

    def test_doubled_melody_note_stays_when_it_continues_the_line(self):
        import numpy as np
        import audio_melody_align as align
        voice = [(0.0, .4, 64, .8), (.5, .9, 65, .8), (1.0, 1.4, 67, .8), (1.5, 1.9, 84, .8), (2.0, 2.4, 65, .8)]
        midi = (np.array([.5, 1.5]), np.array([.9, 1.9]), np.array([65, 84]), None)
        kept, removed = align.without_accompaniment(voice, midi)
        self.assertEqual(removed, 1)
        self.assertEqual([n[2] for n in kept], [64, 65, 67, 65])

    def test_recorded_melody_is_written_without_quantization(self):
        import audio_melody_align as align
        class Tempo:
            def to_ticks(self, seconds): return seconds * 960
        notes = align.melody_ticks([(0.0, .4, 60, .5), (.53, .9, 62, .5)], Tempo(), 480, 10_000)
        self.assertEqual([(n[0], n[1]) for n in notes], [(0, 384), (509, 864)])


if __name__ == '__main__':
    unittest.main(verbosity=2)
