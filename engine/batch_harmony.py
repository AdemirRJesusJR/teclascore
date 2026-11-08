"""Conservative, source-evidenced chord extraction from inspect_midi.parse_midi.

Standard library only. No harmony is inferred from a monophonic melody alone.
Ticks remain in the original MIDI timeline. Empty quality denotes a major triad.
``melody_sources`` may contain channel integers, (track, channel) tuples,
(track, channel, program) tuples, or dictionaries with those fields. A selected
monophonic melody source is excluded; genuinely polyphonic keyboard sources
are retained only when there is no other chord-bearing source.
"""
from __future__ import annotations

from bisect import bisect_left, bisect_right
from collections import Counter, defaultdict
from math import sqrt
from statistics import median


# Chord tones, in root-relative semitones. Every emitted template is fully
# evidenced; the engine never supplies a missing third. The one exception, in new
# analyses only: the PERFECT fifth of a seventh/sixth/extension chord may be
# absent (C-E-Bb is C7, as written in any chord chart); evidence says omitted_fifth.
CHORD_INTERVALS = {
    '': (0, 4, 7), 'm': (0, 3, 7), '7': (0, 4, 7, 10),
    'maj7': (0, 4, 7, 11), 'm7': (0, 3, 7, 10),
    'mMaj7': (0, 3, 7, 11), 'dim': (0, 3, 6),
    'dim7': (0, 3, 6, 9), 'm7b5': (0, 3, 6, 10),
    'aug': (0, 4, 8), 'sus2': (0, 2, 7), 'sus4': (0, 5, 7),
    '6': (0, 4, 7, 9), 'm6': (0, 3, 7, 9),
    'add9': (0, 2, 4, 7), 'madd9': (0, 2, 3, 7),
    '9': (0, 2, 4, 7, 10), 'maj9': (0, 2, 4, 7, 11),
    'm9': (0, 2, 3, 7, 10), '7sus4': (0, 5, 7, 10),
    # Added for new analyses (context_advice). m11 is 1-b3-5-b7-11 (the ninth is
    # not required, so it also names m7(11)); 69 is the major sixth with added ninth.
    '13': (0, 4, 7, 9, 10), '7b9': (0, 1, 4, 7, 10), '7#9': (0, 3, 4, 7, 10),
    'm11': (0, 3, 5, 7, 10), '69': (0, 2, 4, 7, 9),
    '7#5': (0, 4, 8, 10), '7b5': (0, 4, 6, 10),
}
# Reviewed profiles and legacy callers (context_advice=False) only ever see these.
LEGACY_QUALITIES = frozenset(('', 'm', '7', 'maj7', 'm7', 'mMaj7', 'dim', 'dim7', 'm7b5', 'aug',
                              'sus2', 'sus4', '6', 'm6', 'add9', 'madd9', '9', 'maj9', 'm9', '7sus4'))
# Five-note colours are read only from block chords, never from accumulated sustain.
EXTENDED_QUALITIES = frozenset(('13', '7b9', '7#9', 'm11', '69'))
# Chords whose PERFECT fifth may be left out by the player (1-3-7, 1-7-3, 9 without fifth).
# The third is always required; altered fifths, triads, sus and add9 chords never omit it.
OPTIONAL_FIFTH = frozenset(('7', 'maj7', 'm7', 'mMaj7', '9', 'maj9', 'm9', '6', 'm6',
                            '13', '7b9', '7#9', 'm11', '69'))
# A chord below this confidence is written but flagged for review ("cifra com dúvida");
# the same threshold is ScoreConfidence.DoubtfulChord in the app.
DOUBTFUL_CHORD = .75
# Below this a candidate change is not written; it is reported as a review point instead.
_MINIMUM_CHORD = .66

_TEMPLATES = [(root, quality, frozenset((root + n) % 12 for n in intervals))
              for root in range(12) for quality, intervals in CHORD_INTERVALS.items()]
_FIFTHS_MAJOR = {0: 0, 1: -5, 2: 2, 3: -3, 4: 4, 5: -1,
                 6: 6, 7: 1, 8: -4, 9: 3, 10: -2, 11: 5}
_FIFTHS_MINOR = {0: -3, 1: 4, 2: -1, 3: -6, 4: 1, 5: -4,
                 6: 3, 7: -2, 8: 5, 9: 0, 10: -5, 11: 2}


def chord_pitch_classes(root, quality):
    return sorted((int(root) + x) % 12 for x in CHORD_INTERVALS[quality])


def second_inversion_pitches(root, quality, lowest=53):
    """Close-position second inversion: fifth lowest, all tones above it.

    Diminished/augmented use their actual altered fifth. Suspensions use their
    actual second/fourth in place of a third. The diagram voicing is pedagogic;
    it does not claim to be the original arrangement's inversion.
    """
    intervals = CHORD_INTERVALS[quality]
    fifth = next(x for x in (7, 6, 8) if x in intervals)
    bass = int(lowest) + ((int(root) + fifth - int(lowest)) % 12)
    pitches = sorted(bass + ((interval-fifth) % 12) for interval in intervals)
    while pitches[-1] > 71:
        pitches = [pitch-12 for pitch in pitches]
    return pitches


def _source_matches(key, selections):
    for selection in selections or []:
        if isinstance(selection, int) and key[1] == selection:
            return True
        if isinstance(selection, dict):
            if all(selection.get(field, key[i]) == key[i]
                   for i, field in enumerate(('track', 'channel', 'program'))):
                return True
        elif isinstance(selection, (tuple, list)) and tuple(key[:len(selection)]) == tuple(selection):
            return True
    return False


def _clusters(notes, tolerance):
    result = []
    for note in sorted(notes, key=lambda n: (n['start'], n['pitch'])):
        if not result or note['start'] - result[-1][0] > tolerance:
            result.append([note['start'], [note]])
        else:
            result[-1][1].append(note)
    return result


def _exact_candidates(pcs):
    pcs = frozenset(pcs)
    # Source profiling (which tracks carry chords) keeps the original chord vocabulary.
    return [(root, quality, tones) for root, quality, tones in _TEMPLATES
            if pcs == tones and quality in LEGACY_QUALITIES]


def _quantized_tick(tick, ppq):
    # Only tidy small performance offsets; preserve genuine offbeats/triplets.
    options = (round(tick / (ppq / 4)) * (ppq / 4),
               round(tick / (ppq / 3)) * (ppq / 3))
    nearest = min(options, key=lambda q: abs(q - tick))
    return int(round(nearest if abs(nearest - tick) <= ppq * .055 else tick))


def _bass_context(tick, bass, bass_starts, ppq):
    lo = bisect_left(bass_starts, tick - ppq * .7)
    hi = bisect_right(bass_starts, tick + ppq * .12)
    candidates = bass[max(0, lo - 4):hi]
    candidates = [n for n in candidates if n['end'] > tick - ppq * .7]
    if not candidates:
        return None
    # Most recent beat/onset is stronger than the absolute lowest passing note.
    def weight(n):
        age = abs(tick - n['start']) / ppq
        return (1.4 if n['start'] <= tick + ppq * .06 else .4) - age * .35 + min(n['duration'] / ppq, 1) * .15
    return max(candidates, key=weight)


def _choose_candidate(pcs, weights, bass_pc, previous=None, context_advice=False, allow_extended=True):
    """Return a fully supported chord, rejecting pitch sets with substantial extras.

    New analyses (context_advice) also know the extra qualities and, only when no
    complete chord explains the notes, a seventh/sixth/extension chord whose
    perfect fifth is not played (C-E-Bb is C7). Its third is never supplied.
    """
    total = sum(weights.values()) or 1.0
    significant = frozenset(pc for pc in pcs if weights.get(pc, 0) >= .105 * max(weights.values(), default=1))

    def evaluate(omit_fifth):
        candidates = []
        for root, quality, tones in _TEMPLATES:
            if not context_advice and quality not in LEGACY_QUALITIES:
                continue
            if quality in EXTENDED_QUALITIES and not allow_extended:
                continue
            required = tones
            if omit_fifth:
                fifth = (root + 7) % 12
                if quality not in OPTIONAL_FIFTH or fifth in significant:
                    continue
                required = tones - {fifth}
            if not required.issubset(significant):
                continue
            coverage = sum(weights.get(pc, 0) for pc in tones) / total
            if coverage < .94:
                continue
            exact = significant == required
            score = .8 * coverage + .12 * exact
            if bass_pc == root:
                score += .12
            elif bass_pc in required:
                score += .015
            if previous == (root, quality):
                score += .014
            # Avoid choosing an exotic extension over an equally explicit common triad.
            if quality in ('', 'm', '7', 'm7', 'maj7'):
                score += .004
            if context_advice and quality not in LEGACY_QUALITIES:
                score -= .003
            if omit_fifth:
                score -= .03
            candidates.append((score, root, quality, tones, coverage, exact, omit_fifth, required))
        return candidates

    candidates = evaluate(False)
    if not candidates and context_advice:
        candidates = evaluate(True)
    if not candidates:
        return None
    candidates.sort(reverse=True)
    winner = candidates[0]
    margin = winner[0] - candidates[1][0] if len(candidates) > 1 else .15
    ambiguous = len(candidates) > 1 and margin < .035
    confidence = min(.98, .80 + .13 * winner[5] + .07 * (bass_pc == winner[1]))
    if winner[6]:
        # Without its fifth the reading leans on the bass: less certain unless the bass plays the root.
        confidence -= .05 + (.05 if bass_pc != winner[1] else 0)
    if ambiguous:
        confidence -= .17
    result = {'root': winner[1], 'quality': winner[2],
            'pitch_classes': sorted(winner[3]), 'confidence': confidence,
            'ambiguous': ambiguous, 'coverage': winner[4],
            'alternatives': [{'root': x[1], 'quality': x[2]} for x in candidates[1:3]
                             if winner[0] - x[0] < .05]}
    if winner[6]:
        result['omitted_fifth'] = True
    if context_advice:
        # Context may rename the SAME fully evidenced set. It must never turn
        # a chromatic chord into a diatonic one by removing/adding chord tones.
        result['context_candidates'] = [
            {'root': x[1], 'quality': x[2], 'score': x[0]}
            for x in candidates if x[7] == winner[7]]
    return result


def chord_label(root, quality, fifths=0):
    """Short human-readable evidence label; output engraving owns full spelling."""
    names = ('C', 'Db', 'D', 'Eb', 'E', 'F', 'Gb', 'G', 'Ab', 'A', 'Bb', 'B') if fifths < 0 else (
        'C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B')
    return names[root % 12] + quality


def _contextualize_events(events, key, ppq, melody_at=None):
    """Break near ties between equivalent names, retaining the ambiguity.

    Only independent, unambiguous neighboring events participate, so a guessed
    name cannot propagate through a whole sequence. MIDI/key evidence is a
    weak tie-breaker, not permission to impose a scale on played chromaticism.
    The melody note on a strong beat (melody_at) may favour one reading of the
    SAME notes (C6 x Am7); it never adds or removes a chord tone.
    Confidence is deliberately not raised by these heuristics.
    """
    stable = [(event['tick'], event['root'], event['quality'])
              if not event['evidence']['ambiguous'] else None for event in events]
    for index, event in enumerate(events):
        evidence = event['evidence']
        candidates = evidence.pop('context_candidates', [])
        winner = next((c for c in candidates if (c['root'], c['quality']) == (
            event['root'], event['quality'])), None)
        reasons = ['Todas as notas da cifra foram identificadas no acompanhamento.']
        if evidence.get('omitted_fifth'):
            reasons = ['A fundamental, a terça e a sétima ou extensão foram identificadas; '
                       'a quinta justa não soa e foi considerada omitida.']
        if evidence.get('includes_bass_pitch'):
            reasons.append('O baixo tocado completa as notas do acorde.')
        elif evidence.get('bass_pc') == event['root']:
            reasons.append('O baixo tocado apoia esta fundamental.')
        elif evidence.get('bass_pc') in event['pitch_classes']:
            reasons.append('O baixo toca outra nota do acorde; a inversão foi preservada.')
        if evidence['method'] != 'simultaneous_onset':
            reasons.append('Notas sustentadas ou de um arpejo apoiam esta leitura.')
        if winner and len(candidates) > 1 and evidence['ambiguous']:
            ranked = []
            melody_pc = melody_at(event['tick']) if melody_at else None
            for candidate in candidates:
                bonus = 0.; cues = []
                if (key.get('confidence', 0) >= .75 and
                        'midi_key_signature_conflict' not in key and
                        candidate['root'] == key.get('root') and
                        (3 in CHORD_INTERVALS[candidate['quality']]) == bool(key.get('minor'))):
                    bonus += .010
                    cues.append('A tonalidade sugerida favorece este nome entre leituras das mesmas notas.')
                for other in (index-1, index+1):
                    neighbor = stable[other] if 0 <= other < len(stable) else None
                    if neighbor is None or abs(neighbor[0]-event['tick']) > ppq*8:
                        continue
                    if candidate['root'] == neighbor[1] and candidate['quality'] == neighbor[2]:
                        bonus += .014
                        cues.append('Um acorde vizinho identificado sem ambiguidade apoia a continuidade.')
                    elif (neighbor[1]-candidate['root']) % 12 == 7:
                        bonus += .008
                        cues.append('A relação de quinta com um acorde vizinho favorece esta leitura.')
                if melody_pc is not None and candidate['root'] == melody_pc:
                    bonus += .008
                    cues.append('A melodia no tempo forte toca esta fundamental, o que favorece este nome entre leituras das mesmas notas.')
                ranked.append((candidate['score']+bonus, candidate, cues))
            ranked.sort(key=lambda row: row[0], reverse=True)
            # Equal evidence remains unresolved; changing a name needs a real
            # context preference, and never defeats a clear source/bass lead.
            if ranked[0][0]-ranked[1][0] > .006 and ranked[0][2]:
                candidate = ranked[0][1]
                if abs(candidate['score']-winner['score']) < .035:
                    evidence['context_changed_name'] = (event['root'], event['quality']) != (
                        candidate['root'], candidate['quality'])
                    event['root'], event['quality'] = candidate['root'], candidate['quality']
                    event['pitch_classes'] = chord_pitch_classes(candidate['root'], candidate['quality'])
                    reasons.extend(dict.fromkeys(ranked[0][2]))
        if candidates:
            evidence['alternatives'] = [dict(root=c['root'], quality=c['quality'])
                for c in candidates if (c['root'], c['quality']) != (event['root'], event['quality'])]
        if evidence.get('ambiguous') and evidence.get('alternatives'):
            reasons.append('Mais de um nome descreve estas notas; confira a alternativa ouvindo o trecho.')
        if evidence.get('source_disagreement'):
            reasons.append('Fontes de acompanhamento diferentes discordam neste trecho.')
        evidence['recommendation'] = ' '.join(reasons)


def _evidence_gaps(parsed, observations, notes, ppq):
    """Report long spans without newly played OR sustained harmonic support."""
    if not notes:
        return []
    first = min(n['start'] for n in notes)
    end = max(n['end'] for n in notes)
    ranges = sorted((max(first, obs['source_tick']), min(end, obs['support_end_tick']))
                    for obs in observations if obs.get('support_end_tick', 0) > obs['source_tick'])
    gaps = []; cursor = first
    for start, stop in ranges:
        if start-cursor > ppq*16:
            gaps.append({'start_tick': cursor, 'end_tick': start})
        cursor = max(cursor, stop)
    if end-cursor > ppq*16:
        gaps.append({'start_tick': cursor, 'end_tick': end})
    # A tacet/long rest needs no harmony. Only active pitched music can make
    # lack of harmonic support useful to review; percussion alone is excluded.
    return [gap for gap in gaps if any(note['start'] < gap['end_tick'] and
            note['end'] > gap['start_tick'] for note in notes)]


def _diagnostics(events, key, unsupported_gaps, notes, key_changes=()):
    result = []; last_issue = None
    for event in events:
        evidence = event['evidence']
        label = (event['root_spelling'] + event['quality'] if event.get('root_spelling')
                 else chord_label(event['root'], event['quality'], key.get('fifths', 0)))
        issue = (event['root'], event['quality'], bool(evidence.get('ambiguous')),
                 bool(evidence.get('source_disagreement')), event['confidence'] < .80)
        # Repeated strums of one ambiguous chord form one review item; a later
        # newly ambiguous occurrence after clear evidence still gets its time.
        if issue == last_issue:
            continue
        last_issue = issue
        if evidence.get('ambiguous'):
            alternatives = ', '.join(chord_label(c['root'], c['quality'], key.get('fifths', 0))
                                     for c in evidence.get('alternatives', []))
            if evidence.get('source_disagreement'):
                message = f'{label}: as fontes discordam neste trecho. Confira o acompanhamento.'
            elif alternatives:
                message = f'{label}: outra leitura possível: {alternatives}. Confira ouvindo o acompanhamento.'
            else:
                message = f'{label}: leitura harmônica incerta. Confira as notas do acompanhamento.'
            result.append({'tick': event['tick'], 'kind': 'chord_ambiguity',
                'message': message})
        elif event['confidence'] < .80:
            result.append({'tick': event['tick'], 'kind': 'chord_weak_evidence',
                'message': f'{label}: evidência harmônica limitada; confira as notas sustentadas e o baixo.'})
    for gap in unsupported_gaps:
        result.append({'tick': gap['start_tick'], 'end_tick': gap['end_tick'], 'kind': 'harmony_gap',
            'message': 'Trecho com mais de 16 tempos sem apoio de acorde completo; confira antes de prolongar a última cifra.'})
    first = min((n['start'] for n in notes), default=0)
    if not events and notes:
        result.append({'tick': first, 'kind': 'harmony_missing',
            'message': 'Não foi encontrado acompanhamento com acordes completos. A melodia isolada não determina as cifras.'})
    previous = key
    for change in key_changes:
        name = key_name(change['fifths'], change['minor'])
        step = (change['root'] - previous.get('root', 0)) % 12 if change['minor'] == previous.get('minor', False) else None
        interval = {1: ' (meio tom acima)', 11: ' (meio tom abaixo)', 2: ' (um tom acima)', 10: ' (um tom abaixo)'}.get(step, '')
        if change['source'] == 'midi_key_signature':
            result.append({'tick': change['tick'], 'kind': 'key_change',
                'message': f'Modulação para {name}{interval}: a armadura muda aqui, como gravado no MIDI.'})
        elif change.get('confidence', 0) >= DOUBTFUL_KEY_CHANGE:
            result.append({'tick': change['tick'], 'kind': 'key_change',
                'message': f'Modulação para {name}{interval}: a armadura muda aqui, inferida pelas notas. Confira ouvindo o trecho.'})
        else:
            result.append({'tick': change['tick'], 'kind': 'key_change_uncertain',
                'message': f'Possível modulação para {name}{interval}: a armadura foi trocada aqui com evidência limitada. '
                           'Confira ouvindo; se não houver modulação, mantenha a armadura anterior.'})
        previous = change
    if 'midi_key_signature_conflict' in key:
        result.append({'tick': first, 'kind': 'key_conflict',
            'message': 'A armadura gravada no MIDI difere da tonalidade sugerida pelas notas. Confira a armadura; acordes cromáticos foram preservados.'})
    elif key.get('confidence', 0) < .65 and notes:
        result.append({'tick': first, 'kind': 'key_uncertain',
            'message': 'A tonalidade sugerida tem evidência limitada. Confira a armadura antes de gerar a partitura.'})
    return result


def _key_suggestion(parsed, notes, events):
    if not notes:
        return {'fifths': 0, 'minor': False, 'root': 0, 'confidence': 0,
                'source': 'insufficient_pitched_notes'}
    chroma = [0.0] * 12
    for n in notes:
        chroma[n['pitch'] % 12] += min(n['duration'], parsed['ppq'] * 4) * (.5 + n.get('velocity', 80) / 254)
    major = (6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88)
    minor = (6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17)
    def correlation(a, b):
        am = sum(a) / 12; bm = sum(b) / 12
        numerator = sum((x-am)*(y-bm) for x,y in zip(a,b))
        denominator = sqrt(sum((x-am)**2 for x in a) * sum((y-bm)**2 for y in b))
        return numerator / denominator if denominator else 0
    roots = Counter()
    for i, event in enumerate(events):
        end = events[i+1]['tick'] if i+1 < len(events) else parsed.get('end_tick', event['tick'] + parsed['ppq'])
        roots[(event['root'], event['quality'] == 'm')] += max(0, end-event['tick'])
    root_total = sum(roots.values()) or 1
    scored = []
    for is_minor, profile in ((False, major), (True, minor)):
        for root in range(12):
            corr = correlation(chroma, [profile[(pc-root) % 12] for pc in range(12)])
            score = corr + .18 * roots[(root, is_minor)] / root_total
            if events and events[-1]['root'] == root and (events[-1]['quality'] == 'm') == is_minor:
                score += .10
            scored.append((score, corr, root, is_minor))
    scored.sort(reverse=True)
    best = scored[0]; margin = best[0] - scored[1][0]
    confidence = max(.2, min(.89, .42 + 1.8*margin + max(0, best[1])*.17))
    inferred = {'fifths': (_FIFTHS_MINOR if best[3] else _FIFTHS_MAJOR)[best[2]],
                'minor': best[3], 'root': best[2], 'confidence': round(confidence, 3),
                'source': 'inferred_duration_and_chord_cadence'}
    key_events = [e for e in parsed.get('events', []) if e.get('kind') == 'meta'
                  and e.get('type') == 89 and len(e.get('data', b'')) >= 2]
    if key_events:
        raw = key_events[0]['data']; fifths = int.from_bytes(raw[:1], 'big', signed=True)
        if -7 <= fifths <= 7:
            is_minor = bool(raw[1]); root = (7*fifths + (9 if is_minor else 0)) % 12
            meta_score = next(s[0] for s in scored if s[2] == root and s[3] == is_minor)
            if best[0] - meta_score < .13:
                return {'fifths': fifths, 'minor': is_minor, 'root': root,
                        'confidence': .94, 'source': 'midi_key_signature',
                        'changes': len(key_events)}
            inferred['midi_key_signature_conflict'] = {'fifths': fifths, 'minor': is_minor}
    return inferred


# ---------------------------------------------------------------------------
# Bars, modulations, slash basses and functional spelling (new analyses only).

_KK_MAJOR = (6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88)
_KK_MINOR = (6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17)
_LETTERS = 'CDEFGAB'
_LETTER_PC = {'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11}
_KEY_NAMES_PT = {'C': 'Dó', 'D': 'Ré', 'E': 'Mi', 'F': 'Fá', 'G': 'Sol', 'A': 'Lá', 'B': 'Si'}
_DIM_FAMILY = ('dim', 'dim7', 'm7b5')


def _pearson(a, b):
    am = sum(a) / 12; bm = sum(b) / 12
    numerator = sum((x-am)*(y-bm) for x, y in zip(a, b))
    denominator = sqrt(sum((x-am)**2 for x in a) * sum((y-bm)**2 for y in b))
    return numerator / denominator if denominator else 0


def _key_fifths(root, minor):
    return (_FIFTHS_MINOR if minor else _FIFTHS_MAJOR)[root % 12]


def bar_grid(parsed):
    """(start_tick, end_tick, (numerator, denominator)) of each score measure.

    Mirrors score_musicxml.make_bars in MIDI ticks, so an inferred modulation
    lands exactly on the bar line the score writes.
    """
    ppq = parsed['ppq']
    pitched = [n for n in parsed.get('notes', []) if n.get('channel') != 10
               and 20 <= n['pitch'] <= 108 and n.get('program', 0) < 112]
    if not pitched:
        return []
    meters = sorted((e['tick'], e['data'][0], 2 ** e['data'][1]) for e in parsed.get('events', [])
                    if e.get('kind') == 'meta' and e.get('type') == 88 and len(e.get('data', b'')) >= 2
                    and e['data'][0] > 0)
    meters = meters or [(0, 4, 4)]
    initial = meters[0][1:]
    barq = initial[0] * 4 / initial[1]
    origin = int(min(n['start'] for n in pitched) / ppq / barq) * barq
    last = max(n['end'] for n in pitched) / ppq
    changes = sorted({tick / ppq: (num, den) for tick, num, den in meters}.items())
    current = initial; start = origin; bars = []
    while start < last - 1e-9 and len(bars) < 2000:
        for tick, meter in changes:
            if tick <= start + 1e-9:
                current = meter
        size = current[0] * 4 / current[1]
        following = [tick for tick, _ in changes if tick > start + 1e-9]
        stop = min(start + size, following[0] if following else start + size)
        if stop <= start:
            break
        bars.append((int(round(start * ppq)), int(round(stop * ppq)), current))
        start = stop
    return bars


def _strong_beat_melody(parsed, melody, bars):
    """Melody pitch class sounding on a strong beat (bar start or middle of an even bar)."""
    if not melody or not bars:
        return None
    starts = [b[0] for b in bars]
    tolerance = parsed['ppq'] * .12

    def at(tick):
        index = bisect_right(starts, tick + tolerance) - 1
        if index < 0:
            return None
        start, stop, (numerator, _) = bars[index]
        offset = tick - start
        strong = abs(offset) <= tolerance or (numerator % 2 == 0 and abs(offset - (stop-start)/2) <= tolerance)
        if not strong:
            return None
        sounding = [n for n in melody if n['start'] <= tick + tolerance and n['end'] > tick + tolerance]
        return max(sounding, key=lambda n: n['pitch'])['pitch'] % 12 if sounding else None
    return at


def key_name(fifths, minor):
    """Portuguese key name of a signature (fifths, mode): 'Réb maior', 'Fá# menor'."""
    tonic = (('Cb', 'Gb', 'Db', 'Ab', 'Eb', 'Bb', 'F', 'C', 'G', 'D', 'A', 'E', 'B', 'F#', 'C#') if not minor else
             ('Ab', 'Eb', 'Bb', 'F', 'C', 'G', 'D', 'A', 'E', 'B', 'F#', 'C#', 'G#', 'D#', 'A#'))[int(fifths) + 7]
    return _KEY_NAMES_PT[tonic[0]] + tonic[1:] + (' menor' if minor else ' maior')


def _meta_key_changes(parsed, key):
    """Every later key-signature meta event (type 89) that changes the key in force."""
    if key.get('source') != 'midi_key_signature':
        return []
    raw = sorted(((e['tick'], int.from_bytes(e['data'][:1], 'big', signed=True), bool(e['data'][1]))
                  for e in parsed.get('events', []) if e.get('kind') == 'meta' and e.get('type') == 89
                  and len(e.get('data', b'')) >= 2), key=lambda x: x[0])
    changes = []; current = (key['fifths'], key['minor']); first_tick = raw[0][0] if raw else 0
    for tick, fifths, minor in raw:
        if not -7 <= fifths <= 7 or tick <= first_tick or (fifths, minor) == current:
            continue
        changes = [c for c in changes if c['tick'] != tick]
        current = (fifths, minor)
        changes.append({'tick': tick, 'fifths': fifths, 'minor': minor,
                        'root': (7*fifths + (9 if minor else 0)) % 12,
                        'confidence': .94, 'source': 'midi_key_signature'})
    return changes


def _inferred_key_changes(parsed, notes, events, bass_ids, bars):
    """Modulations from the notes when the MIDI declares none.

    The armadura is what the score writes, so the evidence is the fit of each
    key signature: per bar, note time inside its diatonic scale minus time
    outside it (the relative minor's leading tone is neutral), weighted by
    duration, bass and chord tones, over 8-bar windows with a one-bar step.
    Viterbi over the signatures with a high change penalty; changes fall on bar
    lines, each key lasts at least 4 bars and must fit clearly better than the
    previous one, so a short modal borrowing never modulates. The mode of each
    section comes from the Krumhansl-Kessler profiles of its notes. The first
    returned item (initial=True) is the opening key.
    """
    ppq = parsed['ppq']; count = len(bars)
    if count < 12:
        return []
    starts = [b[0] for b in bars]
    chroma = [[0.0] * 12 for _ in range(count)]

    def spread(start, end, pcs, weight):
        index = max(0, bisect_right(starts, start) - 1)
        while index < count and bars[index][0] < end:
            overlap = min(end, bars[index][1]) - max(start, bars[index][0])
            if overlap > 0:
                for pc in pcs:
                    chroma[index][pc] += overlap * weight
            index += 1
    for n in notes:
        spread(n['start'], min(n['end'], n['start'] + ppq*4), (n['pitch'] % 12,), 1.5 if id(n) in bass_ids else 1.0)
    for i, event in enumerate(events):
        end = events[i+1]['tick'] if i+1 < len(events) else min(bars[-1][1], event['tick'] + ppq*8)
        spread(event['tick'], end, event['pitch_classes'], .5)
    signatures = list(range(-6, 7))
    weights = []
    for fifths in signatures:
        tonic = (7 * fifths) % 12
        scale = {(tonic + x) % 12 for x in (0, 2, 4, 5, 7, 9, 11)}
        leading = (tonic + 9 + 11) % 12   # raised seventh of the relative minor
        weights.append([1. if pc in scale else 0. if pc == leading else -1. for pc in range(12)])
    local = []
    for i in range(count):
        window = [sum(chroma[j][pc] for j in range(max(0, i-3), min(count, i+5))) for pc in range(12)]
        total = sum(window)
        local.append([sum(w * x for w, x in zip(row, window)) / total if total else 0. for row in weights])
    states = len(signatures)
    score = list(local[0]); back = []
    for i in range(1, count):
        best = max(range(states), key=lambda k: score[k])
        row = []; new = []
        for k in range(states):
            stay = score[k]; move = score[best] - KEY_CHANGE_PENALTY
            row.append(k if stay >= move else best)
            new.append(max(stay, move) + local[i][k])
        back.append(row); score = new
    state = max(range(states), key=lambda k: score[k]); path = [state]
    for row in reversed(back):
        state = row[state]; path.append(state)
    path.reverse()
    segments = []
    for i, k in enumerate(path):
        if segments and segments[-1]['key'] == k:
            segments[-1]['end'] = i + 1
        else:
            segments.append({'start': i, 'end': i + 1, 'key': k})

    def fit(segment, k):
        return sum(local[i][k] for i in range(segment['start'], segment['end']))

    def merge(index, into):
        a, b = sorted((index, into))
        survivor = segments[into]
        survivor['start'] = segments[a]['start']; survivor['end'] = segments[b]['end']
        segments[a:b+1] = [survivor]
        for i in range(len(segments)-1, 0, -1):
            if segments[i]['key'] == segments[i-1]['key']:
                segments[i-1]['end'] = segments[i]['end']
                del segments[i]
    while len(segments) > 1:
        short = [i for i, s in enumerate(segments) if s['end'] - s['start'] < 4]
        if not short:
            break
        index = min(short, key=lambda i: segments[i]['end'] - segments[i]['start'])
        options = [j for j in (index-1, index+1) if 0 <= j < len(segments)]
        merge(index, max(options, key=lambda j: fit(segments[index], segments[j]['key'])))

    def margin(previous, current):
        return (fit(current, current['key']) - fit(current, previous['key'])) / (current['end'] - current['start'])
    index = 1
    while index < len(segments):
        if margin(segments[index-1], segments[index]) < KEY_CHANGE_MARGIN:
            merge(index, index-1)
            index = 1
            continue
        index += 1
    if len(segments) < 2:
        return []
    # The windows blur a boundary over a few bars: place each change on the bar
    # line that best separates the two signatures, judged bar by bar.
    bar_fit = []
    for i in range(count):
        total = sum(chroma[i])
        bar_fit.append([sum(w * x for w, x in zip(row, chroma[i])) / total if total else 0. for row in weights])
    for left, right in zip(segments, segments[1:]):
        low = max(left['start'] + 1, right['start'] - 4); high = min(right['end'] - 1, right['start'] + 4)
        if low > high:
            continue
        best = max(range(low, high + 1), key=lambda b: (
            sum(bar_fit[i][left['key']] for i in range(low - 1, b)) + sum(bar_fit[i][right['key']] for i in range(b, high + 1)),
            -abs(b - right['start'])))
        left['end'] = right['start'] = best

    def mode(segment):
        fifths = signatures[segment['key']]
        window = [sum(chroma[i][pc] for i in range(segment['start'], segment['end'])) for pc in range(12)]
        major_root = (7 * fifths) % 12; minor_root = (major_root + 9) % 12
        major = _pearson(window, [_KK_MAJOR[(pc - major_root) % 12] for pc in range(12)])
        minor = _pearson(window, [_KK_MINOR[(pc - minor_root) % 12] for pc in range(12)])
        return (minor_root, True) if minor > major else (major_root, False)
    root, minor = mode(segments[0])
    changes = [{'tick': 0, 'fifths': signatures[segments[0]['key']], 'minor': minor, 'root': root, 'initial': True}]
    for previous, current in zip(segments, segments[1:]):
        gap = margin(previous, current)
        root, minor = mode(current)
        changes.append({'tick': bars[current['start']][0], 'fifths': signatures[current['key']], 'minor': minor,
                        'root': root, 'confidence': round(min(.9, .45 + gap), 3),
                        'source': 'inferred_local_windows', 'measure': current['start'] + 1,
                        'margin': round(gap, 3)})
    return changes


# Viterbi cost of one modulation, in summed per-bar signature fit (a few bars of clear evidence).
KEY_CHANGE_PENALTY = 2.5
# A new section must fit its signature at least this much better per bar than the previous one.
KEY_CHANGE_MARGIN = .2
# Below this an inferred modulation is written but flagged for review.
DOUBTFUL_KEY_CHANGE = .75


def _key_changes(parsed, notes, events, key, bass_ids, bars):
    """(key, key_changes): MIDI key-signature changes when declared, else inferred modulations."""
    declared = _meta_key_changes(parsed, key)
    if declared:
        bar_starts = [b[0] for b in bars]
        for change in declared:
            index = bisect_right(bar_starts, change['tick']) - 1
            if index >= 0:
                change['measure'] = index + 1
        return key, declared
    if key.get('source') == 'midi_key_signature':
        return key, []
    inferred = _inferred_key_changes(parsed, notes, events, bass_ids, bars)
    if not inferred:
        return key, []
    first = inferred.pop(0)
    if first['fifths'] != key.get('fifths'):
        # With modulations the whole-song estimate mixes keys; the first section names the opening key.
        key = dict(key, fifths=first['fifths'], minor=first['minor'], root=first['root'],
                   source='inferred_local_windows', whole_song_estimate={
                       'fifths': key.get('fifths'), 'minor': key.get('minor'), 'root': key.get('root')})
    return key, inferred


def _key_scale(fifths, minor):
    alters = {letter: 0 for letter in _LETTERS}
    for letter in ('FCGDAEB'[:fifths] if fifths > 0 else 'BEADGCF'[:-fifths]):
        alters[letter] = 1 if fifths > 0 else -1
    scale = {(_LETTER_PC[letter] + alter) % 12: (letter, alter) for letter, alter in alters.items()}
    if minor:
        tonic = (9 + 7*fifths) % 12; letter, alter = scale[(tonic + 10) % 12]
        scale[(tonic + 11) % 12] = (letter, alter + 1)
    return scale


def _on_letter(letter, pc):
    alter = (pc - _LETTER_PC[letter]) % 12
    alter = alter - 12 if alter > 6 else alter
    return (letter, alter) if abs(alter) <= 1 else None


def _written(spelling):
    letter, alter = spelling
    return letter + ('#' * alter if alter > 0 else 'b' * -alter)


def functional_root_spelling(root, quality, fifths=0, minor=False, next_chord=None):
    """Chord root spelled by its function in the key in force.

    Diatonic roots follow the key signature. Chromatic roots: a chord that falls
    a fifth to the next one is spelled a fifth above it (F#7-B, C#m7-F#7), a
    diminished chord resolving up a semitone is its leading tone (C#dim-Dm), a
    dominant falling a semitone is the tritone substitute (Db7-C). Otherwise a
    natural letter wins (E in C minor, never Fb), diminished chords are raised
    (C#dim), and major or dominant chords are lowered degrees (Db, Eb, Ab, Bb).
    """
    root %= 12
    scale = _key_scale(int(fifths), bool(minor))
    if root in scale:
        return _written(scale[root])
    intervals = CHORD_INTERVALS.get(quality, (0, 4, 7))
    diminished = quality in _DIM_FAMILY
    minor_like = 3 in intervals and 4 not in intervals and not diminished
    dominant = 4 in intervals and 10 in intervals
    if next_chord is not None:
        target_root, target_quality = next_chord
        target = functional_root_spelling(target_root, target_quality, fifths, minor)[0]
        steps = None
        if (root + 5) % 12 == target_root % 12 and not diminished:
            steps = 4
        elif (root + 1) % 12 == target_root % 12 and diminished:
            steps = -1
        elif (root - 1) % 12 == target_root % 12 and dominant:
            steps = 1
        if steps is not None:
            spelled = _on_letter(_LETTERS[(_LETTERS.index(target) + steps) % 7], root)
            if spelled:
                return _written(spelled)
    below, above = scale.get((root - 1) % 12), scale.get((root + 1) % 12)
    raised = _on_letter(below[0], root) if below else None
    lowered = _on_letter(above[0], root) if above else None
    options = [option for option in (raised, lowered) if option]
    if not options:
        return chord_label(root, '', fifths)
    natural = [option for option in options if option[1] == 0]
    if natural:
        return _written(natural[0])
    if len(options) == 1:
        return _written(options[0])
    relative = (root - 7*int(fifths)) % 12   # degree over the major tonic of the signature
    if diminished:
        choice = raised
    elif minor_like:
        choice = lowered if relative in (3, 10) else raised
    else:
        choice = lowered if relative in (1, 3, 8, 10) else raised
    return _written(choice)


def spell_pitch_in_key(pc, fifths=0, minor=False):
    """A lone note (pedal bass) spelled in the key: diatonic, else as a lowered/raised degree."""
    return functional_root_spelling(pc, '', fifths, minor)


def bass_spelling(root_spelling, quality, bass_pc, fifths=0, minor=False):
    """Slash bass: a chord tone keeps its degree's letter (D/F#, Ab/C); a pedal follows the key."""
    import keyboard_svg
    root_pc = (_LETTER_PC[root_spelling[0]] + root_spelling.count('#') - root_spelling.count('b')) % 12
    intervals = CHORD_INTERVALS.get(quality, (0, 4, 7))
    if (bass_pc - root_pc) % 12 in intervals:
        names = keyboard_svg.tone_names(root_pc, intervals, root_spelling, False, 'letras')
        # Cb, Fb, E# and B# are correct degrees but read as wrong notes under a chord: use the key's name.
        if names and names.get(bass_pc % 12) and names[bass_pc % 12] not in ('Cb', 'Fb', 'E#', 'B#'):
            return names[bass_pc % 12]
    return spell_pitch_in_key(bass_pc, fifths, minor)


def active_key(key, key_changes, tick):
    current = key
    for change in key_changes or []:
        if change['tick'] <= tick:
            current = change
    return current


def spell_events(events, key, key_changes):
    """Functional root and slash-bass spelling for analysed chords, in the key in force at each one."""
    for index, event in enumerate(events):
        current = active_key(key, key_changes, event['tick'])
        following = events[index+1] if index + 1 < len(events) else None
        nxt = (following['root'], following['quality']) if following else None
        event['root_spelling'] = functional_root_spelling(event['root'], event['quality'], current.get('fifths', 0),
                                                          current.get('minor', False), nxt)
        if event.get('bass') is not None:
            event['bass_spelling'] = bass_spelling(event['root_spelling'], event['quality'], event['bass'],
                                                   current.get('fifths', 0), current.get('minor', False))


def _slash_basses(events, bass, ppq, end_tick):
    """Written slash basses: an inversion or a pedal actually sustained in the bass.

    The bass must be the first bass note of the chord and hold the chord: at
    least about a beat, or most of the bass time, with the root not dominating.
    A pedal must be the main bass of a neighbouring chord too (it holds across
    a chord change). Short passing basses never create a slash chord.
    """
    if not bass:
        return
    starts = [n['start'] for n in bass]
    profiles = []
    for index, event in enumerate(events):
        start = event['tick']
        stop = events[index+1]['tick'] if index + 1 < len(events) else end_tick
        stop = min(stop, start + ppq*8)
        profile = None
        if stop - start >= ppq*.5:
            lo = max(0, bisect_left(starts, start - ppq*16))
            sounding = [n for n in bass[lo:bisect_right(starts, stop)] if n['end'] > start + ppq*.1 and n['start'] < stop]
            first = [n for n in sounding if n['start'] <= start + ppq*.3]
            if first:
                earliest = max(n['start'] for n in first)
                lead = min((n for n in first if n['start'] == earliest), key=lambda n: n['pitch'])
                time = Counter()
                for n in sounding:
                    time[n['pitch'] % 12] += min(n['end'], stop) - max(n['start'], start)
                total = sum(time.values()) or 1
                held = min(lead['end'], stop) - max(lead['start'], start)
                profile = {'pc': lead['pitch'] % 12, 'held': held, 'share': time[lead['pitch'] % 12] / total,
                           'root_share': time[event['root']] / total}
        profiles.append(profile)
    for index, (event, profile) in enumerate(zip(events, profiles)):
        if profile is None or profile['pc'] == event['root']:
            continue
        pc = profile['pc']
        if pc in event['pitch_classes']:
            if profile['share'] < profile['root_share'] or not (profile['held'] >= ppq*.9 or profile['share'] >= .5):
                continue
            kind = 'inversion'
        else:
            neighbours = [profiles[j] for j in (index-1, index+1) if 0 <= j < len(profiles)]
            if (profile['share'] < .75 or profile['held'] < ppq*.9 or
                    not any(other and other['pc'] == pc and other['share'] >= .6 for other in neighbours)):
                continue
            kind = 'pedal'
        event['bass'] = pc
        event['evidence']['slash_bass'] = kind


def _discarded_changes(discarded, events):
    """Candidate chords left out for weak support that would have changed the chord in force.

    Weak repetitions of the sounding chord are noise; a different chord is a real doubt the
    reader must hear, so it is kept for the review instead of disappearing silently.
    """
    changes = []; previous = None
    for item in sorted(discarded, key=lambda x: x['tick']):
        active = None
        for event in events:
            if event['tick'] > item['tick']:
                break
            active = event
        chord = (item['root'], item['quality'])
        if active is not None and (active['root'], active['quality']) == chord:
            continue
        marker = (chord, None if active is None else active['tick'])
        if marker == previous:
            continue
        previous = marker
        changes.append(dict(item))
    return changes


def analyze_harmony(parsed, melody_sources=None, context_advice=False):
    """Analyze MIDI accompaniment; return JSON-serializable evidence and warnings.

    No chord event is emitted without at least three supported pitch classes.
    Priority: repeated block chords, then sustained polyphony, then coherent
    short arpeggios from an independently established accompaniment source.
    Events are changes only, not a chord repeated on every accompaniment hit.
    context_advice is opt-in: reviewed profiles and legacy callers keep their
    exact results. New analyses receive conservative tie-breaks and diagnostics.
    """
    ppq = int(parsed['ppq'])
    if ppq <= 0:
        raise ValueError('ppq must be positive')
    notes = [dict(n, duration=n['end']-n['start']) for n in parsed.get('notes', [])
             if n.get('channel') != 10 and n['end']-n['start'] >= max(1, ppq*.035)]
    groups = defaultdict(list)
    for n in notes:
        groups[(n['track'], n['channel'], n.get('program', 0))].append(n)
    profiles = []; clusters_by_source = {}
    for key, ns in groups.items():
        clusters = _clusters(ns, max(2, ppq/48)); clusters_by_source[key] = clusters
        exact = [cluster for _, cluster in clusters
                 if _exact_candidates({n['pitch'] % 12 for n in cluster})]
        sustained = 0; active_profile = []
        for tick, cluster in clusters:
            active_profile = [n for n in active_profile if n['end'] > tick+ppq*.08]
            active_profile.extend(cluster)
            if _exact_candidates({n['pitch'] % 12 for n in active_profile}):
                sustained += 1
        poly_notes = sum(len(cluster) for _, cluster in clusters
                         if len({n['pitch'] % 12 for n in cluster}) >= 3)
        pitch_median = median(n['pitch'] for n in ns)
        bass_source = 32 <= key[2] <= 39 or (pitch_median < 49 and poly_notes/len(ns) < .15)
        excluded = _source_matches(key, melody_sources)
        profile = {'track': key[0], 'channel': key[1], 'program': key[2],
                   'notes': len(ns), 'onset_clusters': len(clusters),
                   'explicit_chord_clusters': len(exact),
                   'sustained_chord_clusters': sustained,
                   'polyphonic_note_fraction': round(poly_notes/len(ns), 3),
                   'median_pitch': pitch_median, 'bass_source': bass_source,
                   'melody_excluded': excluded, 'key': key}
        supported_clusters = max(len(exact), sustained*.65)
        profile['reliability'] = min(1., supported_clusters/max(1, len(clusters)) * 1.25)
        profile['eligible'] = not bass_source and not excluded and (
            len(exact) >= 2 or (len(exact) == 1 and len(clusters) <= 8) or
            (sustained >= 4 and sustained >= len(clusters)*.25))
        profiles.append(profile)
    eligible = [p for p in profiles if p['eligible']]
    warnings = []
    if not eligible:
        # Solo keyboard input: a melody source can also contain accompaniment.
        eligible = [p for p in profiles if p['melody_excluded'] and not p['bass_source']
                    and p['explicit_chord_clusters'] >= 3 and p['polyphonic_note_fraction'] >= .5]
        if eligible:
            warnings.append('A fonte de melodia contém polifonia; cifras extraídas apenas de acordes explícitos dessa fonte.')
    if eligible:
        best_reliability = max(p['reliability'] for p in eligible)
        strong = [p for p in eligible if p['reliability'] >= max(.16, best_reliability*.42)]
        eligible = strong or eligible
    source_keys = {p['key'] for p in eligible}
    weights_by_source = {p['key']: max(.3, p['reliability']) for p in eligible}
    bass = sorted([n for p in profiles if p['bass_source'] for n in groups[p['key']]], key=lambda n:n['start'])
    bass_starts = [n['start'] for n in bass]
    observations = []; rejected = 0; discarded = []
    for key in sorted(source_keys):
        clusters = clusters_by_source[key]
        active = []; last_complete = None
        for tick, cluster in clusters:
            active = [n for n in active if n['end'] > tick + ppq*.035]
            active.extend(cluster)
            # Block onset first; active polyphony only when the onset alone
            # does not contain a complete chord (e.g. strumming/arpeggiation).
            options = [('simultaneous_onset', cluster)]
            if len({n['pitch'] % 12 for n in cluster}) < 3:
                options.append(('sustained_polyphony', active))
            bass_note = _bass_context(tick, bass, bass_starts, ppq)
            bass_pc = bass_note['pitch'] % 12 if bass_note else None
            choice = None
            for method, evidence_notes in options:
                weights = Counter()
                for note in evidence_notes:
                    overlap = max(0, min(note['end']-tick, ppq))
                    weights[note['pitch'] % 12] += overlap * (.5 + note.get('velocity', 80)/254)
                pcs = set(weights)
                if len(pcs) < 3 or len(pcs) > 6:
                    continue
                candidate = _choose_candidate(pcs, weights, bass_pc, context_advice=context_advice,
                                              allow_extended=method == 'simultaneous_onset')
                # E.g. F#-A-C in an upper part + an actually played D bass is
                # D7, not an invented D nor a freestanding F# diminished chord.
                used_bass = False
                if (bass_pc is not None and bass_pc not in pcs
                        and (candidate is None or candidate['quality'] in ('dim', 'dim7', 'aug'))):
                    joined = weights.copy()
                    joined[bass_pc] = max(weights.values())
                    with_bass = _choose_candidate(set(joined), joined, bass_pc, context_advice=context_advice,
                                                  allow_extended=method == 'simultaneous_onset')
                    if with_bass and with_bass['root'] == bass_pc:
                        candidate = with_bass; used_bass = True
                # A complete ordinary triad is not renamed on every passing
                # bass note (e.g. G + E bass need not be a new Em7 harmony).
                # A subsequent incomplete strum of the same established chord
                # is supporting evidence, not a new harmony. It may omit root.
                if (candidate and last_complete and tick-last_complete['source_tick'] <= ppq*2.5
                        and pcs < set(last_complete['pitch_classes'])
                        and bass_pc in last_complete['pitch_classes']
                        and candidate['root'] != bass_pc):
                    candidate = {field:last_complete[field] for field in
                                 ('root','quality','pitch_classes','confidence','ambiguous','coverage','alternatives')}
                    candidate['confidence'] = min(candidate['confidence'], .93)
                    if context_advice:
                        candidate['context_candidates'] = last_complete.get('context_candidates', [])
                    used_bass = False
                    method = 'partial_strum_of_previously_complete_chord'
                if candidate:
                    choice = candidate; break
            if choice is None:
                rejected += 1; continue
            actual_tick = tick
            # An offbeat guitar chord often belongs to the beat whose bass
            # already sounded its root. Move only with that concrete evidence.
            if (bass_note and bass_pc == choice['root'] and
                    0 <= tick-bass_note['start'] <= ppq*.6):
                actual_tick = bass_note['start']
            actual_tick = _quantized_tick(actual_tick, ppq)
            choice.update({'tick': max(0, actual_tick), 'source_tick': tick,
                           'source': key, 'method': method,
                           'bass_pc': bass_pc, 'bass_tick': bass_note['start'] if bass_note else None,
                           'weight': weights_by_source[key],
                           'duration': min(n['end']-tick for n in evidence_notes if n['end'] > tick),
                           'includes_bass_pitch': used_bass,
                           'observed_pitch_classes': sorted(pcs)})
            observations.append(choice)
            if context_advice:
                supported_notes = list(evidence_notes) + ([bass_note] if used_bass else [])
                ends = [max((n['end'] for n in supported_notes if n['pitch'] % 12 == pc), default=tick)
                        for pc in choice['pitch_classes']]
                choice['support_end_tick'] = min(ends, default=tick)
            if method != 'partial_strum_of_previously_complete_chord':
                last_complete = choice
    observations.sort(key=lambda x:(x['tick'], -x['weight']))
    # Vote independently played sources at nearly the same timestamp.
    buckets = []
    for observation in observations:
        if not buckets or observation['tick']-buckets[-1][0] > ppq*.15:
            buckets.append([observation['tick'], [observation]])
        else:
            buckets[-1][1].append(observation)
    selected = []
    for tick, bucket in buckets:
        votes = defaultdict(list)
        for obs in bucket:
            votes[(obs['root'], obs['quality'])].append(obs)
        def vote_score(items):
            # Repeated strums on one source cannot outvote distinct sources.
            by_source = defaultdict(float)
            for item in items:
                by_source[item['source']] = max(by_source[item['source']], item['weight']*item['confidence'])
            return sum(by_source.values())
        ranked = sorted(votes.items(), key=lambda x:vote_score(x[1]), reverse=True)
        _, supporting = ranked[0]
        winner = max(supporting, key=lambda x:x['weight']*x['confidence'])
        confidence = winner['confidence']
        if len(ranked) > 1 and vote_score(ranked[1][1]) > vote_score(supporting)*.7:
            confidence -= .13
        if winner['method'] != 'simultaneous_onset':
            confidence -= .05
        if confidence < _MINIMUM_CHORD:
            rejected += 1
            discarded.append({'tick': tick, 'root': winner['root'], 'quality': winner['quality'],
                              'confidence': round(confidence, 3)})
            continue
        evidence = {'method': winner['method'], 'source_tick': winner['source_tick'],
                    'last_support_tick': winner['source_tick'],
                    'sources': [dict(zip(('track','channel','program'), key))
                                for key in sorted({x['source'] for x in supporting})],
                    'observed_pitch_classes': winner['observed_pitch_classes'],
                    'bass_pc': winner['bass_pc'], 'bass_tick': winner['bass_tick'],
                    'coverage': round(winner['coverage'], 3),
                    'includes_bass_pitch': winner['includes_bass_pitch'],
                    'support_count': len(supporting), 'ambiguous': winner['ambiguous']}
        if winner.get('omitted_fifth'):
            evidence['omitted_fifth'] = True
        if winner['alternatives']:
            evidence['alternatives'] = winner['alternatives']
        if context_advice:
            evidence['context_candidates'] = winner.get('context_candidates', [])
            if len(ranked) > 1 and vote_score(ranked[1][1]) > vote_score(supporting)*.7:
                evidence['ambiguous'] = True
                evidence['source_disagreement'] = True
        event = {field:winner[field] for field in ('root', 'quality', 'pitch_classes')}
        event.update(tick=tick, confidence=round(confidence, 3), evidence=evidence)
        selected.append(event)
    bars = bar_grid(parsed) if context_advice else []
    if context_advice:
        melody = [n for n in notes if _source_matches((n['track'], n['channel'], n.get('program', 0)), melody_sources)]
        _contextualize_events(selected, _key_suggestion(parsed, notes, selected), ppq,
                              _strong_beat_melody(parsed, melody, bars))
    # Keep real changes. A source that returns to an established chord within
    # one half beat may be a passing ornament; omit only weak alternatives.
    smoothed = []
    for i, event in enumerate(selected):
        same = lambda a,b:(a['root'],a['quality']) == (b['root'],b['quality'])
        if (i and i+1 < len(selected) and same(selected[i-1],selected[i+1]) and
                not same(event, selected[i-1]) and selected[i+1]['tick']-event['tick'] < ppq*.5
                and event['confidence'] < .86 and not (context_advice and
                    event['evidence']['method'] == 'simultaneous_onset' and event['evidence']['coverage'] >= .94)):
            continue
        if smoothed and same(smoothed[-1], event):
            smoothed[-1]['evidence']['support_count'] += event['evidence']['support_count']
            smoothed[-1]['evidence']['last_support_tick'] = event['evidence']['last_support_tick']
            smoothed[-1]['confidence'] = max(smoothed[-1]['confidence'], event['confidence'])
        else:
            smoothed.append(event)
    uncertain_discarded = _discarded_changes(discarded, smoothed)
    if not smoothed:
        warnings.append('Nenhuma sequência de acordes suficientemente apoiada por polifonia explícita foi encontrada; cifras não inventadas.')
    ambiguous_count = sum(e['evidence']['ambiguous'] for e in smoothed)
    if ambiguous_count:
        warnings.append(f'{ambiguous_count} cifras têm outra leitura harmônica possível; conferir as alternativas no relatório.')
    span = max(1, parsed.get('end_tick', max((n['end'] for n in notes), default=1)) / ppq)
    density = len(smoothed) / span
    if uncertain_discarded:
        warnings.append(f'{len(uncertain_discarded)} possíveis trocas de acorde ficaram de fora por falta de apoio; '
                        'elas aparecem na Revisão guiada para você ouvir e decidir.')
    if density > .45:
        warnings.append('Mudanças de acorde muito frequentes; há ornamentos ou vozes internas que exigem revisão antes de diagramar cada cifra.')
    harmonic_ticks = sorted(set(o['tick'] for o in observations))
    unsupported_gaps = [{'start_tick':a, 'end_tick':b} for a,b in zip(harmonic_ticks,harmonic_ticks[1:]) if b-a > ppq*16]
    if context_advice:
        unsupported_gaps = _evidence_gaps(parsed, observations, notes, ppq)
    if unsupported_gaps:
        warnings.append(f'{len(unsupported_gaps)} trechos com mais de 16 tempos sem apoio harmônico novo; não prolongar cifras automaticamente nesses trechos.')
    key = _key_suggestion(parsed, notes, smoothed)
    key_changes = []
    if context_advice:
        _slash_basses(smoothed, bass, ppq, parsed.get('end_tick', max((n['end'] for n in notes), default=0)))
        key, key_changes = _key_changes(parsed, notes, smoothed, key, {id(n) for n in bass}, bars)
        spell_events(smoothed, key, key_changes)
        previous = key
        for change in key_changes:
            where = f" no compasso {change['measure']}" if change.get('measure') else ''
            origin = 'gravada no MIDI' if change['source'] == 'midi_key_signature' else 'inferida pelas notas'
            warnings.append(f"Mudança de tonalidade {origin}{where}: {key_name(previous['fifths'], previous.get('minor', False))} "
                            f"→ {key_name(change['fifths'], change['minor'])}.")
            previous = change
    if key['confidence'] < .65:
        warnings.append('Armadura sugerida com confiança limitada; a tonalidade precisa de conferência musical.')
    if 'midi_key_signature_conflict' in key:
        warnings.append('A armadura gravada no MIDI conflita com suas notas; sugestão calculada a partir do conteúdo.')
    review_reasons = []
    if not smoothed:
        review_reasons.append('no_supported_chords')
    if ambiguous_count:
        review_reasons.append('ambiguous_chord_names')
    if any(e['confidence'] < .80 for e in smoothed):
        review_reasons.append('low_confidence_chord_changes')
    if uncertain_discarded:
        review_reasons.append('discarded_uncertain_chords')
    if density > .45:
        review_reasons.append('dense_harmonic_changes')
    if unsupported_gaps:
        review_reasons.append('long_gaps_without_harmonic_evidence')
    if key['confidence'] < .65:
        review_reasons.append('uncertain_key_signature')
    result = {'events': smoothed, 'key': key,
            'review_required': bool(review_reasons), 'review_reasons': review_reasons,
            'statistics': {
        'pitched_notes': len(notes), 'source_count': len(groups),
        'selected_sources': [{k:v for k,v in p.items() if k != 'key'} for p in eligible],
        'source_profiles': [{k:v for k,v in p.items() if k != 'key'} for p in profiles],
        'candidate_observations': len(observations), 'rejected_observations': rejected,
        'chord_changes': len(smoothed), 'ambiguous_changes': ambiguous_count,
        'uncertain_discarded': uncertain_discarded,
        'changes_per_quarter': round(density, 3),
        'unsupported_gaps': unsupported_gaps,
        'first_evidence_tick': harmonic_ticks[0] if harmonic_ticks else None,
        'last_evidence_tick': harmonic_ticks[-1] if harmonic_ticks else None,
        'mean_confidence': round(sum(e['confidence'] for e in smoothed)/max(1,len(smoothed)),3),
    }, 'warnings': warnings}
    if context_advice:
        result['context_advice'] = True
        result['key_changes'] = key_changes
        result['statistics']['slash_chords'] = sum(e.get('bass') is not None for e in smoothed)
        result['statistics']['omitted_fifth_chords'] = sum(bool(e['evidence'].get('omitted_fifth')) for e in smoothed)
        result['diagnostics'] = _diagnostics(selected, key, unsupported_gaps, notes, key_changes)
        kinds = {'chord_ambiguity': 'ambiguous_chord_names',
                 'chord_weak_evidence': 'low_confidence_chord_changes',
                 'harmony_gap': 'long_gaps_without_harmonic_evidence',
                 'harmony_missing': 'no_supported_chords',
                 'key_conflict': 'conflicting_key_signature', 'key_uncertain': 'uncertain_key_signature',
                 'key_change': None, 'key_change_uncertain': 'uncertain_key_change'}
        for diagnostic in result['diagnostics']:
            # A modulation declared or clearly evidenced is shown in the review, but needs no decision.
            reason = kinds[diagnostic['kind']]
            if reason is None:
                continue
            result['review_required'] = True
            if reason not in result['review_reasons']:
                result['review_reasons'].append(reason)
    return result


if __name__ == '__main__':
    import argparse, json
    from inspect_midi import parse_midi
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('midi')
    parser.add_argument('--melody-channel', type=int, action='append', default=[])
    args = parser.parse_args()
    result = analyze_harmony(parse_midi(args.midi), args.melody_channel)
    print(json.dumps(result, ensure_ascii=False, indent=2))
