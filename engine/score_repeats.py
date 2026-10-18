"""Repeat signs for literal repetitions: a block of 2–8 measures played twice in a row is written once between
repeat barlines; when only its last one or two measures differ the second time, they become first and second endings
(casas 1 e 2). Only exact repetitions are folded (same notes, durations, chords and diagram positions, same meter),
and never when the block carries lyrics, a tempo or key change, or a note tied across its edges. Playback in MuseScore
still plays the block twice. D.S./coda jumps are not inferred."""

MIN_BARS, MAX_BARS = 2, 8


def _bar_signature(bar, notes, chords):
    start, end = bar['start'], bar['end']
    tones = tuple(sorted((n['start'] - start, n['end'] - start, n['pitch'], tuple(n.get('graces', ())))
                         for n in notes if start <= n['start'] < end))
    harmony = tuple(sorted((c['tick'] - start, c.get('root'), c.get('quality'), c.get('diagram_inversion'))
                           for c in chords if start <= c['tick'] < end))
    return (tuple(bar['meter']), end - start, bool(bar.get('partial')), tones, harmony)


def _crosses(notes, tick):
    return any(n['start'] < tick < n['end'] for n in notes)


def _has_lyrics(notes, start, end):
    return any(n.get('lyric') for n in notes if start <= n['start'] < end)


def fold_repeats(bars, notes, chords, grids, tempos, key_changes, ppq, origin, div):
    """Folds repeated blocks in place (bars, notes, chords, grids and the tempo/key change ticks) and returns what was folded.

    Ticks after a removed block move back by its length, so the written score stays continuous.
    """
    folds = []
    def tempo_ticks():
        return [round(t['tick'] / ppq * div) - origin for t in tempos] + [round(k['tick'] / ppq * div) - origin for k in key_changes]
    k = 0
    while k < len(bars) - 2 * MIN_BARS + 1:
        signatures = [_bar_signature(bar, notes, chords) for bar in bars]
        found = None
        for length in range(min(MAX_BARS, (len(bars) - k) // 2), MIN_BARS - 1, -1):
            if signatures[k:k + length] != signatures[k + length:k + 2 * length]:
                ending = _ending(signatures, bars, notes, k, length, tempo_ticks())
                if ending:
                    found = ('ending', length, ending); break
                continue
            first, second, after = bars[k]['start'], bars[k + length]['start'], bars[k + 2 * length - 1]['end']
            if any(_crosses(notes, tick) for tick in (first, second, after)): continue
            if _has_lyrics(notes, first, after): continue
            if any(second < tick < after for tick in tempo_ticks()) or any(first < tick < second for tick in tempo_ticks()): continue
            if any(bar.get('repeat_start') or bar.get('repeat_end') for bar in bars[k:k + 2 * length]): continue
            found = (length, first, second, after); break
        if found is None:
            k += 1; continue
        if found[0] == 'ending':
            _, length, ending = found
            fold = _fold_with_endings(bars, notes, chords, grids, tempos, key_changes, ppq, origin, div, k, length, ending)
            if fold is None: k += 1; continue
            folds.append(fold); k += length + ending
            continue
        length, first, second, after = found
        removed = after - second
        if removed % div:  # beat grids are kept per quarter; odd meters stay written out
            k += 1; continue
        notes[:] = [n for n in notes if not second <= n['start'] < after]
        chords[:] = [c for c in chords if not second <= c['tick'] < after]
        for n in notes:
            if n['start'] >= after: n['start'] -= removed; n['end'] -= removed
        for c in chords:
            if c['tick'] >= after: c['tick'] -= removed
        del bars[k + length:k + 2 * length]
        for bar in bars[k + length:]: bar['start'] -= removed; bar['end'] -= removed
        bars[k]['repeat_start'] = True; bars[k + length - 1]['repeat_end'] = True
        shifted = {}
        for beat, grid in grids.items():
            tick = beat * div
            if second <= tick < after: continue
            shifted[beat - removed // div if tick >= after else beat] = grid
        grids.clear(); grids.update(shifted)
        for change in list(tempos) + list(key_changes):
            tick = round(change['tick'] / ppq * div) - origin
            if tick >= after: change['tick'] = (tick - removed + origin) * ppq / div
        folds.append({'first_measure': k + 1, 'measures': length, 'removed_quarters': removed / div})
        k += length
    return folds


def _ending(signatures, bars, notes, k, length, tempo_ticks):
    """Number of final measures (1 or 2) that differ between the two passes, when everything before them is equal."""
    for ending in (1, 2):
        common = length - ending
        if common < MIN_BARS or k + 2 * length > len(bars): continue
        if signatures[k:k + common] != signatures[k + length:k + length + common]: continue
        if signatures[k + common:k + length] == signatures[k + length + common:k + 2 * length]: continue
        if any(tuple(bars[i]['meter']) != tuple(bars[k]['meter']) for i in range(k, k + 2 * length)): continue
        first, second, after = bars[k]['start'], bars[k + length]['start'], bars[k + 2 * length - 1]['end']
        repeat_end = bars[k + length + common]['start']
        if any(_crosses(notes, tick) for tick in (first, bars[k + common]['start'], second, repeat_end, after)): continue
        if _has_lyrics(notes, first, after): continue
        if any(first < tick < after for tick in tempo_ticks): continue
        return ending
    return 0


def _fold_with_endings(bars, notes, chords, grids, tempos, key_changes, ppq, origin, div, k, length, ending):
    """Keeps pass 1 (with its last measures as ending 1), drops the repeated common part of pass 2 and keeps its last
    measures as ending 2."""
    common = length - ending
    second, resume = bars[k + length]['start'], bars[k + length + common]['start']
    removed = resume - second
    if removed % div: return None
    notes[:] = [n for n in notes if not second <= n['start'] < resume]
    chords[:] = [c for c in chords if not second <= c['tick'] < resume]
    for n in notes:
        if n['start'] >= resume: n['start'] -= removed; n['end'] -= removed
    for c in chords:
        if c['tick'] >= resume: c['tick'] -= removed
    del bars[k + length:k + length + common]
    for bar in bars[k + length:]: bar['start'] -= removed; bar['end'] -= removed
    bars[k]['repeat_start'] = True
    bars[k + common]['ending_start'] = 1; bars[k + length - 1]['ending_stop'] = 1; bars[k + length - 1]['repeat_end'] = True
    bars[k + length]['ending_start'] = 2; bars[k + length + ending - 1]['ending_stop'] = 2
    shifted = {}
    for beat, grid in grids.items():
        tick = beat * div
        if second <= tick < resume: continue
        shifted[beat - removed // div if tick >= resume else beat] = grid
    grids.clear(); grids.update(shifted)
    for change in list(tempos) + list(key_changes):
        tick = round(change['tick'] / ppq * div) - origin
        if tick >= resume: change['tick'] = (tick - removed + origin) * ppq / div
    return {'first_measure': k + 1, 'measures': length, 'endings': ending, 'removed_quarters': removed / div}
