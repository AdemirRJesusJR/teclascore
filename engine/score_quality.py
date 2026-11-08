"""Quality of a generated score's notation, measured on what was written (not on the MIDI).

Each check counts something a reader would trip over: chains of tiny rests, bars with no melody,
several words under one note, a staff that only repeats one or two pitches (a lyric guide note),
too many pages for the length. The result feeds the app's confidence rating and review list; it
never changes the score.
"""
from collections import Counter, defaultdict

DIV = 960


def _source(note):
    source = note.get('source')
    return None if source is None else (source.get('track'), source.get('channel'), source.get('program'))


def evaluate(report):
    notes = sorted(report.get('notes', []), key=lambda n: (n['start'], n['pitch']))
    bars = report.get('bars', [])
    analysis = report.get('melody_analysis', {}) or {}
    issues = []
    score = 100.0
    metrics = {}
    if analysis.get('chords_only') or not notes:
        return {'score': 100, 'issues': [], 'metrics': {'notes': len(notes)}}

    # Rests shorter than a sixteenth between two attacks of the same line read as clutter.
    by_line = defaultdict(list)
    for note in notes:
        by_line[_source(note) if report.get('staff_per_source') else None].append(note)
    tiny_rests = 0
    tiny_notes = 0
    for line in by_line.values():
        line.sort(key=lambda n: n['start'])
        for current, following in zip(line, line[1:]):
            gap = following['start'] - current['end']
            if 0 < gap < DIV // 4:
                tiny_rests += 1
        tiny_notes += sum(1 for n in line if n['end'] - n['start'] < DIV // 4)
    metrics.update(tiny_rests=tiny_rests, tiny_notes=tiny_notes, notes=len(notes))
    share = (tiny_rests + tiny_notes) / max(1, len(notes))
    if share > .15:
        score -= min(20, 5 + share * 40)
        issues.append(f'ritmo com muitas notas ou pausas muito curtas ({tiny_rests + tiny_notes} em {len(notes)} notas)')

    # Bars without any melody note (rests only) beyond intros and instrumental breaks.
    sounding = set()
    for note in notes:
        for index, bar in enumerate(bars):
            if bar['start'] < note['end'] and note['start'] < bar['end']:
                sounding.add(index)
    empty = len(bars) - len(sounding)
    metrics['empty_bars'] = empty
    if bars and empty / len(bars) > .3:
        score -= min(20, 8 + 30 * (empty / len(bars) - .3))
        issues.append(f'{empty} de {len(bars)} compassos sem melodia')

    lyrics = analysis.get('lyrics')
    if isinstance(lyrics, dict):
        crowded = len(lyrics.get('crowded_notes', []))
        metrics['crowded_lyric_notes'] = crowded
        if crowded >= 3:
            score -= min(20, 6 + 2 * crowded)
            issues.append(f'{crowded} notas com três ou mais palavras da letra (a pista pode não ser a voz cantada)')
        if lyrics.get('accents_restored'):
            metrics['accents_restored'] = lyrics['accents_restored']

    guides = []
    for identity, line in by_line.items():
        pitches = Counter(n['pitch'] for n in line)
        if len(line) >= 12 and len(pitches) <= 2:
            guides.append(identity)
    if not report.get('staff_per_source'):
        # One staff: look at each source of a merged melody.
        per_source = defaultdict(list)
        for note in notes:
            per_source[_source(note)].append(note['pitch'])
        guides = [identity for identity, pitches in per_source.items()
                  if identity is not None and len(pitches) >= 12 and len(set(pitches)) <= 2]
    metrics['guide_note_lines'] = len(guides)
    if guides:
        score -= 30
        issues.append('uma das pistas da melodia repete só uma ou duas notas (nota-guia da letra, não a voz)')

    pages = report.get('pages') or report.get('pages_planned') or 0
    measures = report.get('measures') or len(bars)
    metrics.update(pages=pages, measures=measures)
    if measures and pages > 2 and pages / measures * 32 > 4:
        score -= 5
        issues.append(f'{pages} páginas para {measures} compassos')

    return {'score': int(round(max(0, min(100, score)))), 'issues': issues, 'metrics': metrics}
