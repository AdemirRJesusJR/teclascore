"""Síntese rítmica e unificação da notação (1.8.0): regras que deixam a escrita uniforme sem mudar a música.

Nenhuma regra move um ataque, troca uma altura ou altera a duração total da partitura; cada uma só decide como o
mesmo som é escrito e conta no relatório quantas vezes agiu. Todas são opções de Configurações › Aparência das
partituras e chegam ao motor na configuração do worker:

- rhythm_grid (Grade rítmica única): a grade em que a música vive (colcheias, semicolcheias ou tercinas) é
  detectada pelos ataques; os finais das notas são alinhados a ela e os tempos fora dela (uma tercina isolada,
  um trecho em semicolcheias) ficam como estão e viram pontos de revisão.
- duration_synthesis (Síntese de durações): figuras e pausas pela convenção do compasso (ver score_musicxml.pieces).
- phrase_endings (Finais de frase): nota curta seguida de pausas miúdas até o fim do tempo ou do compasso vira a
  nota completa.
- unified_tempo (Tempo e fórmula únicos): andamento arredondado, microvariações ignoradas, rallentandos mantidos.
- repeat_synthesis (Síntese de repetições): compassos idênticos em sequência viram o sinal %.
- unified_spelling (Grafia única): ver melody_spelling.unify_spellings.
"""
from collections import Counter

DIV = 960
GRID_NAMES = {'quarter': 'semínimas', 'eighth': 'colcheias', 'sixteenth': 'semicolcheias', 'triplet': 'tercinas'}
MAX_LISTED_EXCEPTIONS = 12
MIN_BEATS_FOR_REVIEW = 24      # tempos com ataques para que a grade dominante signifique algo
MAX_EXCEPTION_SHARE = .25      # acima disso a música é mista e os tempos não são exceções a listar


def review_exceptions(grid):
    """Os tempos fora da grade que valem um ponto de revisão: só em músicas longas o bastante, com uma grade clara
    e exceções em minoria."""
    if not grid or grid.get('dominant') not in ('eighth', 'sixteenth', 'triplet'): return []
    busy = sum(count for cls, count in grid.get('counts', {}).items() if cls != 'quarter')
    if grid.get('beats_with_attacks', 0) < MIN_BEATS_FOR_REVIEW or not busy: return []
    if len(grid.get('exception_beats', [])) > MAX_EXCEPTION_SHARE * busy: return []
    return grid.get('exceptions', [])
TEMPO_THRESHOLD_BPM = 3        # abaixo disso a variação é microvariação do sequenciador, não uma mudança real
TEMPO_THRESHOLD_SHARE = .025


# ---- 1. Grade rítmica única --------------------------------------------------------------------------------------

def beat_class(offsets, ternary):
    """Classe rítmica de um tempo pelos deslocamentos (em DIV) dos ataques dentro dele."""
    inner = [o for o in offsets if o > 0]
    if not inner: return 'quarter'
    if ternary: return 'triplet'
    if all(o % (DIV // 2) == 0 for o in inner): return 'eighth'
    if all(o % (DIV // 4) == 0 for o in inner): return 'sixteenth'
    return 'finer'


def dominant_grid(starts, grids, origin, is_ternary):
    """Grade dominante da música e os tempos que fogem dela.

    starts: posições dos ataques escritos (relativas à origem); grids: grade de cada tempo absoluto;
    is_ternary(grid) diz se a grade é ternária. Uma música é de semicolcheias quando pelo menos 35% dos tempos com
    contratempo as usam, de tercinas quando metade deles é ternária; senão é de colcheias (ou de semínimas, sem
    contratempo algum). As exceções são os tempos mais finos que a grade dominante ou de outra família."""
    by_beat = {}
    for s in starts:
        beat = (s + origin) // DIV
        by_beat.setdefault(beat, []).append((s + origin) - beat * DIV)
    classes = {beat: beat_class(offsets, is_ternary(grids.get(beat, 4))) for beat, offsets in by_beat.items()}
    counts = Counter(classes.values())
    busy = sum(count for cls, count in counts.items() if cls != 'quarter')
    if not busy: dominant = 'quarter'
    elif counts['sixteenth'] + counts['finer'] >= .35 * busy: dominant = 'sixteenth'
    elif counts['triplet'] >= .5 * busy: dominant = 'triplet'
    else: dominant = 'eighth'
    finer = {'quarter': ('eighth', 'sixteenth', 'finer', 'triplet'), 'eighth': ('sixteenth', 'finer', 'triplet'),
             'sixteenth': ('finer', 'triplet'), 'triplet': ('eighth', 'sixteenth', 'finer')}[dominant]
    exceptions = sorted(beat for beat, cls in classes.items() if cls in finer)
    unit = {'quarter': DIV // 2, 'eighth': DIV // 2, 'sixteenth': DIV // 4, 'triplet': DIV // 3}[dominant]
    return {'dominant': dominant, 'name': GRID_NAMES[dominant], 'unit': unit, 'beats_with_attacks': len(classes),
            'counts': dict(counts), 'exception_beats': exceptions,
            'exceptions': [{'beat': beat, 'class': classes[beat]} for beat in exceptions]}


def snap_end(start, stop, next_start, limit, unit, anchor=0):
    """Final de nota alinhado à grade dominante (unidade `unit` a partir de `anchor`), sem passar do ataque seguinte
    nem encostar no início. Um final em legato (no ataque seguinte) ou no limite da música fica como está.
    Devolve (novo final, 1 se mudou)."""
    if stop >= next_start or stop >= limit or (stop - anchor) % unit == 0: return stop, 0
    low = anchor + (stop - anchor) // unit * unit
    candidates = [c for c in (low + unit, low) if start < c <= min(next_start, limit)]
    if not candidates: return stop, 0
    best = min(candidates, key=lambda c: (abs(c - stop), -c))
    return (best, 1) if best != stop else (stop, 0)


# ---- 3. Finais de frase ------------------------------------------------------------------------------------------

def single_rests(beat_length):
    """Durações de pausa que se escrevem com uma figura só dentro de um tempo (colcheia; semínima no composto)."""
    return {DIV // 2, DIV} if beat_length == DIV * 3 // 2 else {DIV // 2}


def phrase_end(start, stop, next_start, bar_start, bar_end, beat_length, limit):
    """Nota seguida de pausas miúdas até o fim do tempo (ou do compasso) vira a nota completa até lá.

    O resto do tempo depois da nota é miúdo quando dura menos de meio tempo (semínima) ou não cabe numa pausa só
    (colcheia e semicolcheia encadeadas). Uma pausa de colcheia inteira fica escrita. Nenhum ataque muda: a nota
    só cresce até o limite do tempo, nunca até o ataque seguinte quando ele cai depois desse limite.
    Devolve (novo final, 1 se mudou)."""
    if stop >= next_start or stop >= limit or stop <= bar_start: return stop, 0
    rel = stop - bar_start
    if rel % beat_length == 0 or stop >= bar_end: return stop, 0
    boundary = min(bar_start + (rel // beat_length + 1) * beat_length, bar_end, limit)
    if boundary > next_start or boundary <= stop: return stop, 0
    remainder = boundary - stop
    if remainder < DIV // 2 or remainder not in single_rests(beat_length): return boundary, 1
    return stop, 0


# ---- 4. Tempo e fórmula únicos -----------------------------------------------------------------------------------

def unify_tempos(tempos):
    """Marcas de andamento escritas: cada bpm arredondado ao inteiro e as variações menores que 3 bpm (ou 2,5%)
    em relação à última marca escrita ignoradas. Um rallentando ou uma mudança real acumula a diferença e volta a
    ser escrito. Devolve (marcas escritas, {'tempo_marks_merged': n, 'tempo_marks_rounded': m})."""
    kept = []; merged = 0; rounded = 0; last_raw = None
    for tempo in sorted(tempos, key=lambda t: t['tick']):
        raw = float(tempo['bpm'])
        if last_raw is not None and abs(raw - last_raw) < max(TEMPO_THRESHOLD_BPM, last_raw * TEMPO_THRESHOLD_SHARE):
            merged += 1; continue
        value = int(round(raw))
        if abs(raw - value) > 1e-9: rounded += 1
        kept.append(dict(tempo, bpm=value)); last_raw = raw
    return kept, {'tempo_marks_merged': merged, 'tempo_marks_rounded': rounded, 'tempo_marks_written': len(kept)}


def dedupe_meters(meters, ppq):
    """Remove fórmulas de compasso repetidas que caem numa barra da grade vigente (não mudam nada); uma fórmula
    igual no meio de um compasso reinicia a contagem e fica. Devolve (fórmulas, removidas)."""
    result = []; removed = 0
    for meter in sorted(meters, key=lambda m: m['tick']):
        if result:
            previous = result[-1]
            if (meter['numerator'], meter['denominator']) == (previous['numerator'], previous['denominator']):
                bar = ppq * 4 * previous['numerator'] / previous['denominator']
                offset = (meter['tick'] - previous['tick']) / bar
                if abs(offset - round(offset)) < 1e-6:
                    removed += 1; continue
        result.append(meter)
    return result, removed


# ---- 5. Compassos idênticos em sequência (%) ----------------------------------------------------------------------

REPEAT_FLAGS = ('repeat_start', 'repeat_end', 'ending_start', 'ending_stop', 'segno', 'coda', 'fine', 'to_coda',
                'jump', 'in_jump', 'pickup')
# A % cannot refer to the bar before a repeat sign, an ending or a jump: the music that follows them is new.
PREVIOUS_FLAGS = ('repeat_end', 'ending_stop', 'fine', 'to_coda', 'jump')


def _chord_key(chord):
    return None if chord is None else (chord.get('root'), chord.get('quality'), chord.get('diagram_inversion'),
                                       chord.get('bass'), chord.get('root_spelling'))


def _bar_content(bar, notes, chords):
    """Notas, acorde vigente no início e trocas de acorde dentro do compasso: um compasso sem cifra escrita sob o
    mesmo acorde do anterior é a mesma leitura."""
    start, end = bar['start'], bar['end']
    tones = tuple(sorted((n['start'] - start, n['end'] - start, n['pitch'], tuple(n.get('graces', ())),
                          bool(n.get('lyric')), bool(n.get('lyric2')), bool(n.get('slash')))
                         for n in notes if start <= n['start'] < end))
    real = [c for c in chords if not c.get('reminder')]
    prior = [c for c in real if c['tick'] <= start]
    active = _chord_key(max(prior, key=lambda c: c['tick'])) if prior else None
    inner = tuple(sorted((c['tick'] - start,) + _chord_key(c) for c in real if start < c['tick'] < end))
    return tuple(bar['meter']), end - start, tones, active, inner


def measure_repeats(bars, notes, chords, excluded=()):
    """Marca com 'measure_repeat' cada compasso igual ao anterior (mesmas notas, durações, acorde vigente e trocas de
    acorde, mesmo compasso), sem letra, sem nota ligada atravessando suas barras, sem sinais de repetição e fora dos
    inícios de seção. Devolve a lista dos índices marcados."""
    marked = []
    excluded = set(excluded)
    previous = None
    for index, bar in enumerate(bars):
        content = _bar_content(bar, notes, chords)
        eligible = (index not in excluded and previous is not None and content == previous
                    and content[2] and not any(bar.get(flag) for flag in REPEAT_FLAGS)
                    and not any(bars[index - 1].get(flag) for flag in PREVIOUS_FLAGS)
                    and not bar.get('lyric_texts') and not bars[index - 1].get('lyric_texts')
                    and not bar.get('partial') and not bars[index - 1].get('partial')
                    and not any(n.get('lyric') or n.get('lyric2') for n in notes if bars[index - 1]['start'] <= n['start'] < bar['end'])
                    and not any(n['start'] < tick < n['end'] for n in notes for tick in (bar['start'], bar['end'])))
        if eligible:
            bar['measure_repeat'] = True; marked.append(index)
        previous = content
    return marked


# ---- Relatório ---------------------------------------------------------------------------------------------------

def summary_lines(analysis):
    """Frases curtas, para o relatório e a dica do card, com o que cada regra fez."""
    lines = []
    grid = analysis.get('rhythm_grid') or {}
    if grid.get('dominant') and grid['dominant'] != 'quarter':
        text = f"grade de {grid['name']}"
        if grid.get('ends_snapped'): text += f": {grid['ends_snapped']} finais de nota alinhados"
        if grid.get('exception_beats'): text += f", {len(grid['exception_beats'])} tempos fora dela"
        lines.append(text)
    durations = analysis.get('duration_synthesis') or {}
    parts = []
    if durations.get('rests_regrouped'): parts.append(f"{durations['rests_regrouped']} pausas agrupadas")
    if durations.get('notes_regrouped'): parts.append(f"{durations['notes_regrouped']} ligaduras pela convenção do compasso")
    if parts: lines.append(', '.join(parts))
    if analysis.get('phrase_endings'): lines.append(f"{analysis['phrase_endings']} finais de frase completados")
    tempo = analysis.get('unified_tempo') or {}
    if tempo.get('tempo_marks_merged') or tempo.get('tempo_marks_rounded'):
        text = f"andamento ♩ = {tempo.get('opening_bpm')}" if tempo.get('opening_bpm') else 'andamento'
        if tempo.get('tempo_marks_merged'): text += f" ({tempo['tempo_marks_merged']} microvariações ignoradas)"
        lines.append(text)
    if tempo.get('time_signatures_merged'): lines.append(f"{tempo['time_signatures_merged']} fórmulas de compasso repetidas removidas")
    repeats = analysis.get('repeat_synthesis') or {}
    parts = []
    if repeats.get('measure_repeats'): parts.append(f"{repeats['measure_repeats']} compassos com %")
    if repeats.get('folded_measures'): parts.append(f"{repeats['folded_measures']} compassos dobrados em ritornelo")
    if parts: lines.append(', '.join(parts))
    if analysis.get('unified_spellings'): lines.append(f"{analysis['unified_spellings']} grafias unificadas")
    return lines
