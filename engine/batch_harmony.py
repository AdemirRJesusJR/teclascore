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
# evidenced; the engine never supplies a missing third or fifth.
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
}
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
    return [(root, quality, tones) for root, quality, tones in _TEMPLATES if pcs == tones]


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


def _choose_candidate(pcs, weights, bass_pc, previous=None):
    """Return a fully supported chord, rejecting pitch sets with substantial extras."""
    total = sum(weights.values()) or 1.0
    significant = frozenset(pc for pc in pcs if weights.get(pc, 0) >= .105 * max(weights.values(), default=1))
    candidates = []
    for root, quality, tones in _TEMPLATES:
        if not tones.issubset(significant):
            continue
        coverage = sum(weights.get(pc, 0) for pc in tones) / total
        if coverage < .94:
            continue
        exact = significant == tones
        score = .8 * coverage + .12 * exact
        if bass_pc == root:
            score += .12
        elif bass_pc in tones:
            score += .015
        if previous == (root, quality):
            score += .014
        # Avoid choosing an exotic extension over an equally explicit common triad.
        if quality in ('', 'm', '7', 'm7', 'maj7'):
            score += .004
        candidates.append((score, root, quality, tones, coverage, exact))
    if not candidates:
        return None
    candidates.sort(reverse=True)
    winner = candidates[0]
    margin = winner[0] - candidates[1][0] if len(candidates) > 1 else .15
    ambiguous = len(candidates) > 1 and margin < .035
    confidence = min(.98, .80 + .13 * winner[5] + .07 * (bass_pc == winner[1]))
    if ambiguous:
        confidence -= .17
    return {'root': winner[1], 'quality': winner[2],
            'pitch_classes': sorted(winner[3]), 'confidence': confidence,
            'ambiguous': ambiguous, 'coverage': winner[4],
            'alternatives': [{'root': x[1], 'quality': x[2]} for x in candidates[1:3]
                             if winner[0] - x[0] < .05]}


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


def analyze_harmony(parsed, melody_sources=None):
    """Analyze MIDI accompaniment; return JSON-serializable evidence and warnings.

    No chord event is emitted without at least three supported pitch classes.
    Priority: repeated block chords, then sustained polyphony, then coherent
    short arpeggios from an independently established accompaniment source.
    Events are changes only, not a chord repeated on every accompaniment hit.
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
    observations = []; rejected = 0
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
                candidate = _choose_candidate(pcs, weights, bass_pc)
                # E.g. F#-A-C in an upper part + an actually played D bass is
                # D7, not an invented D nor a freestanding F# diminished chord.
                used_bass = False
                if (bass_pc is not None and bass_pc not in pcs
                        and (candidate is None or candidate['quality'] in ('dim', 'dim7', 'aug'))):
                    joined = weights.copy()
                    joined[bass_pc] = max(weights.values())
                    with_bass = _choose_candidate(set(joined), joined, bass_pc)
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
        if confidence < .66:
            rejected += 1; continue
        evidence = {'method': winner['method'], 'source_tick': winner['source_tick'],
                    'last_support_tick': winner['source_tick'],
                    'sources': [dict(zip(('track','channel','program'), key))
                                for key in sorted({x['source'] for x in supporting})],
                    'observed_pitch_classes': winner['observed_pitch_classes'],
                    'bass_pc': winner['bass_pc'], 'bass_tick': winner['bass_tick'],
                    'coverage': round(winner['coverage'], 3),
                    'includes_bass_pitch': winner['includes_bass_pitch'],
                    'support_count': len(supporting), 'ambiguous': winner['ambiguous']}
        if winner['alternatives']:
            evidence['alternatives'] = winner['alternatives']
        event = {field:winner[field] for field in ('root', 'quality', 'pitch_classes')}
        event.update(tick=tick, confidence=round(confidence, 3), evidence=evidence)
        selected.append(event)
    # Keep real changes. A source that returns to an established chord within
    # one half beat may be a passing ornament; omit only weak alternatives.
    smoothed = []
    for i, event in enumerate(selected):
        same = lambda a,b:(a['root'],a['quality']) == (b['root'],b['quality'])
        if (i and i+1 < len(selected) and same(selected[i-1],selected[i+1]) and
                not same(event, selected[i-1]) and selected[i+1]['tick']-event['tick'] < ppq*.5
                and event['confidence'] < .86):
            continue
        if smoothed and same(smoothed[-1], event):
            smoothed[-1]['evidence']['support_count'] += event['evidence']['support_count']
            smoothed[-1]['evidence']['last_support_tick'] = event['evidence']['last_support_tick']
            smoothed[-1]['confidence'] = max(smoothed[-1]['confidence'], event['confidence'])
        else:
            smoothed.append(event)
    if not smoothed:
        warnings.append('Nenhuma sequência de acordes suficientemente apoiada por polifonia explícita foi encontrada; cifras não inventadas.')
    ambiguous_count = sum(e['evidence']['ambiguous'] for e in smoothed)
    if ambiguous_count:
        warnings.append(f'{ambiguous_count} cifras têm outra leitura harmônica possível; conferir as alternativas no relatório.')
    span = max(1, parsed.get('end_tick', max((n['end'] for n in notes), default=1)) / ppq)
    density = len(smoothed) / span
    if density > .45:
        warnings.append('Mudanças de acorde muito frequentes; há ornamentos ou vozes internas que exigem revisão antes de diagramar cada cifra.')
    harmonic_ticks = sorted(set(o['tick'] for o in observations))
    unsupported_gaps = [{'start_tick':a, 'end_tick':b} for a,b in zip(harmonic_ticks,harmonic_ticks[1:]) if b-a > ppq*16]
    if unsupported_gaps:
        warnings.append(f'{len(unsupported_gaps)} trechos com mais de 16 tempos sem apoio harmônico novo; não prolongar cifras automaticamente nesses trechos.')
    key = _key_suggestion(parsed, notes, smoothed)
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
    if density > .45:
        review_reasons.append('dense_harmonic_changes')
    if unsupported_gaps:
        review_reasons.append('long_gaps_without_harmonic_evidence')
    if key['confidence'] < .65:
        review_reasons.append('uncertain_key_signature')
    return {'events': smoothed, 'key': key,
            'review_required': bool(review_reasons), 'review_reasons': review_reasons,
            'statistics': {
        'pitched_notes': len(notes), 'source_count': len(groups),
        'selected_sources': [{k:v for k,v in p.items() if k != 'key'} for p in eligible],
        'source_profiles': [{k:v for k,v in p.items() if k != 'key'} for p in profiles],
        'candidate_observations': len(observations), 'rejected_observations': rejected,
        'chord_changes': len(smoothed), 'ambiguous_changes': ambiguous_count,
        'changes_per_quarter': round(density, 3),
        'unsupported_gaps': unsupported_gaps,
        'first_evidence_tick': harmonic_ticks[0] if harmonic_ticks else None,
        'last_evidence_tick': harmonic_ticks[-1] if harmonic_ticks else None,
        'mean_confidence': round(sum(e['confidence'] for e in smoothed)/max(1,len(smoothed)),3),
    }, 'warnings': warnings}


if __name__ == '__main__':
    import argparse, json
    from inspect_midi import parse_midi
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('midi')
    parser.add_argument('--melody-channel', type=int, action='append', default=[])
    args = parser.parse_args()
    result = analyze_harmony(parse_midi(args.midi), args.melody_channel)
    print(json.dumps(result, ensure_ascii=False, indent=2))
