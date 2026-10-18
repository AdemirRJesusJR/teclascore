"""Repeat signs for literal repetitions: a block of 2–8 measures played twice in a row is written once between
repeat barlines; when only its last one or two measures differ the second time, they become first and second endings
(casas 1 e 2). Only exact repetitions are folded (same notes, durations, chords and diagram positions, same meter),
and never when the block carries lyrics, a tempo or key change, or a note tied across its edges. Playback in MuseScore
still plays the block twice.

Longer returns become jumps (fold_jumps): when the music goes back to an earlier passage of 4 or more measures
(not right after it) and either ends with it or then plays a new ending, the second time is replaced by D.S. al Fine
or D.S. al Coda (D.C. when the passage is the beginning), with segno, Fine, To Coda and coda signs."""

MIN_BARS, MAX_BARS = 2, 8
JUMP_MIN_BARS = 4
JUMP_FLAGS = ('segno', 'fine', 'to_coda', 'jump', 'coda', 'in_jump')


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
            if _touches_jump(bars, k, 2 * length): continue
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
        if _touches_jump(bars, k, 2 * length): continue
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


def _touches_jump(bars, k, count):
    # A passage played again by a D.S./D.C. must keep its measures written out: MuseScore does not repeat inside a jump.
    return any(bar.get(flag) for bar in bars[k:k + count] for flag in JUMP_FLAGS)


def _active_chord(chords, tick):
    prior = [c for c in chords if c['tick'] <= tick]
    if not prior: return None
    chord = max(prior, key=lambda c: c['tick'])
    return chord


def _chord_key(chord):
    return None if chord is None else (chord.get('root'), chord.get('quality'), chord.get('diagram_inversion'))


def fold_jumps(bars, notes, chords, grids, tempos, key_changes, ppq, origin, div):
    """Replaces a later return to an earlier passage by a D.S./D.C. jump, in place. Returns what was folded, or None.

    The returning passage must be equal measure by measure (notes, durations, chords, diagram positions, meter), at least
    JUMP_MIN_BARS long, separated from the original by at least one measure, without lyrics, tempo or key changes, repeat
    signs or ties across its edges, and start under the same chord. After it, the piece either ends (al Fine) or plays
    measures of its own (al Coda)."""
    count = len(bars)
    if count < 2 * JUMP_MIN_BARS + 1: return None
    ids = {}
    signatures = [ids.setdefault(_bar_signature(bar, notes, chords), len(ids)) for bar in bars]
    changes = [round(t['tick'] / ppq * div) - origin for t in tempos if t['tick'] > 0] + [round(k['tick'] / ppq * div) - origin for k in key_changes]
    flagged = [bool(bar.get('repeat_start') or bar.get('repeat_end') or bar.get('ending_start') or bar.get('ending_stop')) for bar in bars]
    best = None
    for t in range(JUMP_MIN_BARS + 1, count - JUMP_MIN_BARS + 1):
        for s in range(0, t - JUMP_MIN_BARS):
            length = 0
            while t + length < count and s + length < t - 1 and signatures[s + length] == signatures[t + length]: length += 1
            if length < JUMP_MIN_BARS or (best and length <= best[2]): continue
            if any(flagged[s:s + length]) or any(flagged[t:count]): continue
            start, block_end, tail, tail_end = bars[s]['start'], bars[s + length - 1]['end'], bars[t]['start'], bars[t + length - 1]['end']
            if (tail_end - tail) % div: continue
            if any(_crosses(notes, tick) for tick in (start, block_end, tail, tail_end)): continue
            if _has_lyrics(notes, tail, tail_end): continue
            if any(start < tick < tail_end for tick in changes): continue
            if _chord_key(_active_chord(chords, start)) != _chord_key(_active_chord(chords, tail)): continue
            best = (s, t, length)
    if best is None: return None
    s, t, length = best
    coda = count - (t + length)
    tail, tail_end = bars[t]['start'], bars[t + length - 1]['end']
    removed = tail_end - tail
    resume_chord = _active_chord(chords, tail_end) if coda else None
    notes[:] = [n for n in notes if not tail <= n['start'] < tail_end]
    chords[:] = [c for c in chords if not tail <= c['tick'] < tail_end]
    for n in notes:
        if n['start'] >= tail_end: n['start'] -= removed; n['end'] -= removed
    for c in chords:
        if c['tick'] >= tail_end: c['tick'] -= removed
    if resume_chord is not None and not any(c['tick'] == tail for c in chords):
        # After the jump the coda keeps sounding the chord of the To Coda measure; restate it where the coda begins.
        chords.append(dict(resume_chord, tick=tail, reminder=True)); chords.sort(key=lambda c: c['tick'])
    del bars[t:t + length]
    for bar in bars[t:]: bar['start'] -= removed; bar['end'] -= removed
    shifted = {}
    for beat, grid in grids.items():
        tick = beat * div
        if tail <= tick < tail_end: continue
        shifted[beat - removed // div if tick >= tail_end else beat] = grid
    grids.clear(); grids.update(shifted)
    for change in list(tempos) + list(key_changes):
        tick = round(change['tick'] / ppq * div) - origin
        if tick >= tail_end: change['tick'] = (tick - removed + origin) * ppq / div
    sign = 'D.C.' if s == 0 else 'D.S.'
    kind = sign + (' al Coda' if coda else ' al Fine')
    if s: bars[s]['segno'] = True
    for bar in bars[s:s + length]: bar['in_jump'] = True
    bars[s + length - 1]['to_coda' if coda else 'fine'] = True
    bars[t - 1]['jump'] = kind
    if coda: bars[t]['coda'] = True
    return {'jump': kind, 'first_measure': s + 1, 'measures': length, 'coda_measures': coda, 'removed_quarters': removed / div}


# MuseScore imports the MusicXML signs as plain staff text; these become its real markers and jumps, which playback follows.
MARKER_LABELS = {'segno': 'segno', 'coda': 'codab', 'Fine': 'fine', 'To Coda': 'coda'}
JUMPS = {'D.S. al Fine': ('segno', 'fine', ''), 'D.S. al Coda': ('segno', 'coda', 'codab'),
         'D.C. al Fine': ('start', 'fine', ''), 'D.C. al Coda': ('start', 'coda', 'codab')}


def native_jumps(root, E):
    """Replaces the imported staff texts of the jump signs on the top staff by MuseScore Marker and Jump elements.

    Returns how many were replaced."""
    replaced = 0
    staves = root.findall('Score/Staff')
    if not staves: return 0
    for measure in staves[0].findall('Measure'):
      for voice in measure.findall('voice'):
        for text in list(voice.findall('StaffText')):
            content = text.find('text')
            if content is None: continue
            symbol = content.find('sym')
            plain = ''.join(content.itertext()).strip()
            key = symbol.text if symbol is not None and symbol.text in ('segno', 'coda') else plain
            if key in MARKER_LABELS:
                element = E.Element('Marker')
                E.SubElement(element, 'style').text = 'repeat_left' if key in ('segno', 'coda') else 'repeat_right'
                body = E.SubElement(element, 'text')
                if key in ('segno', 'coda'): E.SubElement(body, 'sym').text = key
                else: body.text = key
                E.SubElement(element, 'label').text = MARKER_LABELS[key]
                # Without the explicit type MuseScore draws segno and coda at the end of the measure instead of its start.
                E.SubElement(element, 'markerType').text = MARKER_LABELS[key]
            elif key in JUMPS:
                jump_to, play_until, continue_at = JUMPS[key]
                element = E.Element('Jump')
                E.SubElement(element, 'text').text = key
                E.SubElement(element, 'jumpTo').text = jump_to
                E.SubElement(element, 'playUntil').text = play_until
                E.SubElement(element, 'continueAt').text = continue_at
            else: continue
            # Above the chord names and keyboard diagrams (7.5 spaces over the default marker height), never through them.
            E.SubElement(element, 'offset', x='0', y='-7.5')
            # Markers and jumps belong to the measure itself (like layout breaks), before its voices.
            voice.remove(text); measure.insert(list(measure).index(measure.find('voice')), element); replaced += 1
    return replaced
