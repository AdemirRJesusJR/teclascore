"""Encaixe da letra do karaokê nas notas da melodia escolhida.

plan(events, attacks, ...) recebe as sílabas do MIDI já convertidas para a escala da partitura e as notas da melodia
e devolve onde cada sílaba fica, sem tocar nas notas. As etapas:
1. compensa um deslocamento global (mediana da distância de cada sílaba ao ataque mais próximo);
2. separa os versos que a melodia não canta: fala sobre a introdução instrumental vira texto "(falado: …)" e um
   verso cantado onde a melodia escolhida pausa vira texto sob o compasso, em vez de ir para notas erradas;
3. encaixa as demais sílabas por programação dinâmica monotônica (custo = distância no tempo; nota sem sílaba é
   livre, sílaba sem nota custa caro; duas sílabas na mesma nota têm um custo extra);
4. quando o karaokê marca palavra por palavra, divide a palavra em sílabas (lyrics_syllables) pelas notas até a
   próxima palavra; a última sílaba se estende pelas notas que sobram (melisma com linha de prolongamento);
5. duas palavras que precisam ficar na mesma nota são unidas pelo sinal de elisão "‿" (que‿eu).
Quando menos de 40% das sílabas casam com a melodia, a letra vai inteira por versos (modo 'lines').
"""
from bisect import bisect_left, bisect_right
from statistics import median

import lyrics_syllables

ELISION = '‿'
MATCH = 1 / 4        # tolerância de encaixe, em tempos (uma semicolcheia)
MAX_DISTANCE = 1.5   # sílaba a mais de 1,5 tempo de qualquer nota não entra
DROP_COST = 1.6
SHARE_WORD = .5      # duas sílabas da mesma palavra na mesma nota
SHARE_ELISION = .85  # duas palavras na mesma nota (elisão)
LOW_SHARE = .4


def _nearest(starts, position):
    index = bisect_left(starts, position)
    best = None
    for j in (index - 1, index):
        if 0 <= j < len(starts) and (best is None or abs(starts[j] - position) < abs(best)):
            best = starts[j] - position
    return best


def estimate_offset(positions, starts, div):
    """Deslocamento global (em unidades da partitura) que aproxima as sílabas dos ataques; 0 sem evidência."""
    distances = [d for d in (_nearest(starts, p) for p in positions) if d is not None and abs(d) <= div / 2]
    if len(distances) < 8:
        return 0
    offset = median(distances)
    offset = round(offset) if abs(offset) >= div / 32 else 0
    # A delay close to a whole note value (e.g. 0.45 of a quarter over eighth notes) makes the nearest attack the
    # NEXT note, so the median points the wrong way. Every candidate shift up to half a beat is scored by how many
    # syllables then land on a distinct attack; a different shift wins only with clearly more matches.
    def matches(shift):
        used = set(); hits = 0
        for p in positions:
            d = _nearest(starts, p + shift)
            if d is not None and abs(d) <= div / 16:
                key = p + shift + d
                if key not in used:
                    used.add(key); hits += 1
        return hits
    base = matches(offset)
    step = max(1, div // 48)
    best_shift, best_hits = offset, base
    for shift in range(-(div // 2), div // 2 + 1, step):
        hits = matches(shift)
        if hits > best_hits or (hits == best_hits and abs(shift) < abs(best_shift)):
            best_shift, best_hits = shift, hits
    if best_shift != offset and best_hits >= base + max(2, .1 * len(positions)):
        return best_shift if abs(best_shift) >= div / 32 else 0
    return offset


def group_lines(events, div):
    """Índices das sílabas de cada verso: marcas do karaokê ou, sem elas, pausas de mais de dois tempos."""
    marked = any(e.get('line_start') for e in events[1:])
    lines = []
    for index, event in enumerate(events):
        gap = index and event['pos'] - events[index - 1]['pos'] > 2 * div
        new = not lines or (event.get('line_start') if marked else gap)
        if new:
            lines.append([index])
        else:
            lines[-1].append(index)
    return lines, marked


def line_text(events, indices):
    words = []
    for index in indices:
        event = events[index]
        if event['word_start'] or not words:
            words.append(event['text'])
        else:
            words[-1] += event['text']
    return ' '.join(words)


def _bar_index(bars, position):
    for index, bar in enumerate(bars):
        if bar['start'] <= position < bar['end']:
            return index
    return None


def _bar_profile(bar, notes, div):
    return {(round((n['start'] - bar['start']) * 8 / div), n['pitch']) for n in notes if bar['start'] <= n['start'] < bar['end']}


def _repeated_without_words(first, last, bars, notes, worded_bars, div):
    """True quando a melodia dos compassos first..last reaparece noutro trecho sem letra (riff instrumental)."""
    size = last - first + 1
    profiles = [_bar_profile(bar, notes, div) for bar in bars]
    if not any(profiles[first:last + 1]):
        return False
    for start in range(len(bars) - size + 1):
        if first - size < start < last + 1 or any(i in worded_bars for i in range(start, start + size)):
            continue
        scores = []
        for k in range(size):
            a, b = profiles[first + k], profiles[start + k]
            if a or b:
                scores.append(len(a & b) / len(a | b))
        if scores and sum(scores) / len(scores) >= .6:
            return True
    return False


def classify_lines(events, lines, attacks, bars, div, solo_positions=()):
    """'sung', 'spoken' (fala sobre a parte instrumental) ou 'text' (verso cantado sem a melodia escolhida)."""
    starts = [n['start'] for n in attacks]
    kinds = []
    worded_bars = {_bar_index(bars, e['pos']) for e in events} if bars else set()
    solo = sorted(solo_positions)
    for number, indices in enumerate(lines):
        positions = [events[i]['pos'] for i in indices]
        matched = sum(1 for p in positions if (d := _nearest(starts, p)) is not None and abs(d) <= MATCH * div)
        lo = bisect_left(starts, positions[0] - div / 4)
        hi = bisect_right(starts, positions[-1] + div)
        notes_in_span = hi - lo
        kind = 'sung'
        if len(indices) >= 3:
            if matched / len(indices) < LOW_SHARE:
                # A melodia escolhida não acompanha este verso: ele vai como texto, sem rótulo de fala.
                kind = 'text'
            elif solo and notes_in_span:
                in_solo = sum(1 for n in attacks[lo:hi] if (d := _nearest(solo, n['start'])) is not None and abs(d) <= div / 8)
                if in_solo / notes_in_span >= .7:
                    kind = 'spoken'
            if kind == 'sung' and bars and number < 2 and number + 1 < len(lines):
                # Fala de abertura ("Isso é Barões da Pisadinha…") sobre um riff que volta depois sem letra.
                following = events[lines[number + 1][0]]['pos']
                bar_size = bars[0]['end'] - bars[0]['start']
                first, last = _bar_index(bars, positions[0]), _bar_index(bars, positions[-1])
                if (following - positions[-1] >= 2 * bar_size and first is not None and last is not None
                        and _repeated_without_words(first, last, bars, attacks, worded_bars - set(range(first, last + 1)), div)):
                    kind = 'spoken'
        kinds.append(kind)
    return kinds


def align(events, attacks, div):
    """Nota escolhida para cada sílaba (índice em attacks) ou None, por programação dinâmica monotônica."""
    starts = [n['start'] for n in attacks]
    count = len(starts)
    infinite = float('inf')
    best = [0.0] + [infinite] * count  # estado s: última nota usada = s-1 (0 = nenhuma)
    history = []
    for e_index, event in enumerate(events):
        position = event['pos']
        new = [value + DROP_COST for value in best]
        back = {}
        lo = bisect_left(starts, position - MAX_DISTANCE * div)
        hi = bisect_right(starts, position + MAX_DISTANCE * div)
        if lo < hi:
            # Melhor estado anterior a cada nota da janela (prefixo mínimo).
            running = infinite
            arg = 0
            prefix = {}
            for state in range(0, hi + 1):
                if state - 1 >= lo:
                    prefix[state] = (running, arg)
                if best[state] < running:
                    running, arg = best[state], state
            share = SHARE_WORD if not event['word_start'] else SHARE_ELISION
            for k in range(lo, hi):
                state = k + 1
                cost = abs(starts[k] - position) / div
                before, origin = prefix[state]
                options = [(before + cost, origin)]
                if e_index and best[state] < infinite:
                    options.append((best[state] + cost + share, state))
                value, origin = min(options)
                if value < new[state]:
                    new[state] = value
                    back[state] = origin
        history.append(back)
        best = new
    state = min(range(count + 1), key=lambda s: best[s])
    result = [None] * len(events)
    for e_index in range(len(events) - 1, -1, -1):
        back = history[e_index]
        if state in back:
            result[e_index] = state - 1
            state = back[state]
    return result


def _melisma(attacks, k, run, div):
    """Linha de prolongamento só para um melisma de verdade: duas notas ou mais, ou uma nota que soma um tempo."""
    return len(run) >= 2 or attacks[run[-1]].get('end', attacks[run[-1]]['start']) - attacks[k]['start'] >= div


def plan(events, attacks, div, *, bars=None, mode='auto', solo_positions=()):
    """events: [{'pos','text','word_start','line_start','paragraph_start'}] em ordem; attacks: notas da melodia.

    Devolve {'placements': {índice da nota: [peças]}, 'extend_stop': [...], 'texts': [...], 'report': {...}}.
    Cada peça é {'text','word_start','word_end','event'}. Nada é alterado nas notas.
    """
    attacks = sorted(attacks, key=lambda n: n['start'])
    starts = [n['start'] for n in attacks]
    offset = estimate_offset([e['pos'] for e in events], starts, div) if starts else 0
    events = [dict(e, pos=e['pos'] + offset, source_pos=e['pos']) for e in events]
    words = []
    for index, event in enumerate(events):
        if event['word_start'] or not words:
            words.append([index])
        else:
            words[-1].append(index)
    word_of = {index: number for number, word in enumerate(words) for index in word}
    lines, marked = group_lines(events, div)
    line_of = {index: number for number, line in enumerate(lines) for index in line}
    kinds = classify_lines(events, lines, attacks, bars or [], div, solo_positions) if starts else ['text'] * len(lines)
    matchable = [i for i, e in enumerate(events) if kinds[line_of[i]] == 'sung']
    matched = sum(1 for i in matchable if (d := _nearest(starts, events[i]['pos'])) is not None and abs(d) <= MATCH * div)
    estimate = matched / max(1, len(events) - sum(len(lines[n]) for n, k in enumerate(kinds) if k == 'spoken'))
    used_mode = mode if mode in ('notes', 'lines') else ('lines' if estimate < LOW_SHARE or not starts else 'notes')
    if used_mode == 'lines':
        kinds = ['spoken' if kind == 'spoken' else 'text' for kind in kinds]
    sung = [i for i in range(len(events)) if kinds[line_of[i]] == 'sung']
    chosen = align([events[i] for i in sung], attacks, div) if sung and starts else []
    note_of = {i: k for i, k in zip(sung, chosen)}
    placements = {}
    dropped = [i for i in sung if note_of.get(i) is None]

    extend_stop = []
    next_pos = {}
    for index in sung:
        later = [events[j]['pos'] for j in range(index + 1, len(events))]
        next_pos[index] = later[0] if later else float('inf')

    def free_run(k, index):
        """Notas seguintes livres para a mesma palavra: antes da próxima sílaba, sem pausa no meio."""
        run = []
        last_assigned = min([note_of[j] for j in sung if j > index and note_of.get(j) is not None] or [len(attacks)])
        j = k + 1
        while j < last_assigned and j < len(attacks):
            note, previous = attacks[j], attacks[j - 1]
            if note['start'] >= next_pos[index] - div / 8 or note['start'] - previous.get('end', previous['start']) > div / 4:
                break
            if note['start'] - attacks[k]['start'] > 2 * div:
                break
            run.append(j)
            j += 1
        return run

    for index in sung:
        k = note_of.get(index)
        if k is None:
            continue
        event = events[index]
        word = words[word_of[index]]
        word_end = index == word[-1]
        whole_word = len(word) == 1
        pieces = lyrics_syllables.syllabify(event['text']) if whole_word else [event['text']]
        if len(pieces) > 1:
            run = free_run(k, index)
            slots = [k] + run
            if len(slots) < len(pieces):
                pieces = pieces[:len(slots) - 1] + [''.join(pieces[len(slots) - 1:])]
            for number, piece in enumerate(pieces):
                placements.setdefault(slots[number], []).append(
                    {'text': piece, 'word_start': number == 0, 'word_end': number == len(pieces) - 1, 'event': index,
                     'split': True})
            rest = slots[len(pieces):]
            if rest and _melisma(attacks, slots[len(pieces) - 1], rest, div):
                placements[slots[len(pieces) - 1]][-1]['extend'] = True
                extend_stop.append(rest[-1])
            continue
        placements.setdefault(k, []).append({'text': event['text'], 'word_start': event['word_start'], 'word_end': word_end,
                                             'event': index})
        if word_end:
            run = free_run(k, index)
            if run and _melisma(attacks, k, run, div):
                placements[k][-1]['extend'] = True
                extend_stop.append(run[-1])
    # Texto dos versos que não vão para as notas.
    texts = []
    for number, kind in enumerate(kinds):
        if kind == 'sung':
            continue
        indices = lines[number]
        texts.append({'pos': events[indices[0]]['pos'], 'text': line_text(events, indices), 'kind': kind, 'line': number})
    report = {'offset': offset, 'mode': used_mode, 'match_estimate': round(estimate, 3), 'line_marks': marked,
              'kinds': kinds, 'lines': lines, 'dropped': dropped, 'events': events}
    return {'placements': placements, 'extend_stop': extend_stop, 'texts': texts, 'report': report, 'attacks': attacks}


def note_lyric(pieces):
    """Texto e silábico de uma nota a partir das peças que ela recebeu (elisão entre palavras)."""
    text = ''
    for number, piece in enumerate(pieces):
        if number and piece['word_start']:
            text += ELISION
        text += piece['text']
    first, last = pieces[0], pieces[-1]
    starts, ends = first['word_start'], last['word_end']
    syllabic = 'single' if starts and ends else 'begin' if starts else 'end' if ends else 'middle'
    lyric = {'text': text, 'syllabic': syllabic}
    if last.get('extend'):
        lyric['extend'] = True
    words = 1 + sum(1 for piece in pieces[1:] if piece['word_start'])
    return lyric, words
