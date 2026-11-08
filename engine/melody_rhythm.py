"""Decisões rítmicas da melodia vinda do MIDI, antes da quantização única de batch_score.melody_events.

- Voz interna da mesma fonte (skyline por fonte): um ataque grave sob uma nota mais aguda que continua soando é
  acompanhamento da própria pista, não melodia.
- Swing: colcheias tocadas longa-curta (razão perto de 2:1) são escritas retas, com a indicação "Swing".
- Grade por tempo escolhida por custo (erro em milissegundos + complexidade) com Viterbi entre tempos, com
  penalidade para trocas binária/ternária. Inclui a tercina de semínimas (3:2 em dois tempos).
- Compasso estimado pelos acentos quando o MIDI não o informa.
- Pausas curtas: limiar em milissegundos e preservação das pausas que se repetem nas frases repetidas.
Somente a biblioteca padrão: o módulo também roda sem numpy.
"""
from bisect import bisect_right
import math

# Grades por tempo (divisões da semínima). Os códigos da tercina de semínimas ocupam dois tempos e
# continuam múltiplos de 3, para que os testes "%3==0" já existentes as tratem como ternárias.
BINARY = (4, 8, 16)
TERNARY = (3, 6, 12)
PAIR_FIRST = 3003
PAIR_SECOND = 6003
COMPLEXITY = {4: 0.0, 8: 2.0, 16: 10.0, 3: 0.8, 6: 3.0, 12: 10.0}
PAIR_COMPLEXITY = 0.4          # por tempo do par
SWITCH_PENALTY = 1.0           # troca entre famílias binária e ternária
THIRDS_BONUS = 1.0             # as duas posições de tercina (1/3 e 2/3) presentes no tempo
EXACT_BONUS = 2.0              # todos os contratempos exatamente na grade (MIDI quantizado nessa grade)
INFEASIBLE = 1e6


def grid_div(value):
    """Divisão efetiva de uma grade, inclusive dos códigos da tercina de semínimas."""
    return 3 if value in (PAIR_FIRST, PAIR_SECOND) else value


def is_pair(value):
    return value in (PAIR_FIRST, PAIR_SECOND)


class TempoMap:
    """Duração da semínima em ms em cada ponto (quartos de nota absolutos do MIDI)."""
    def __init__(self, tempos, ppq):
        items = sorted((t['tick'] / ppq, float(t['bpm'])) for t in (tempos or []) if t.get('bpm'))
        if not items or items[0][0] > 0: items.insert(0, (0.0, items[0][1] if items else 120.0))
        self.quarters = [q for q, _ in items]
        self.bpm = [b for _, b in items]

    def quarter_ms(self, quarter):
        index = max(0, bisect_right(self.quarters, quarter) - 1)
        return 60000.0 / max(1.0, self.bpm[index])

    def quarters_for_ms(self, ms, quarter):
        return ms / self.quarter_ms(quarter)


class MeterMap:
    """Barras do mapa de compassos do MIDI (ancorado no tick 0), em quartos de nota absolutos."""
    def __init__(self, meters, ppq):
        self.changes = []
        for meter in sorted(meters or [{'tick': 0, 'numerator': 4, 'denominator': 4}], key=lambda m: m['tick']):
            self.changes.append((meter['tick'] / ppq, meter['numerator'], meter['denominator']))
        if self.changes[0][0] > 0: self.changes.insert(0, (0.0, self.changes[0][1], self.changes[0][2]))

    def at(self, quarter):
        """(início da barra, numerador, denominador) do compasso que contém o quarto dado."""
        active = self.changes[0]
        for change in self.changes:
            if change[0] <= quarter + 1e-9: active = change
        start, numerator, denominator = active
        length = numerator * 4 / denominator
        return start + math.floor((quarter - start) / length + 1e-9) * length, numerator, denominator


def compound(numerator, denominator):
    return denominator == 8 and numerator % 3 == 0 and numerator >= 6


# ---- 1. Voz interna da mesma fonte -------------------------------------------------------------------------------

def mask_inner_attacks(notes, ppq, ratio=.4, legato=.15, cluster=.04, held=.125):
    """Separa os ataques graves encobertos por uma nota mais aguda da mesma fonte.

    Um ataque é descartado quando uma nota mais aguda, iniciada antes dele, ainda soa por mais de `ratio` da sua
    duração. Exceção de legato: a nota aguda termina até `legato` tempo depois do ataque grave (é a nota anterior da
    própria melodia, com o note-off um pouco atrasado). A nota aguda precisa soar desde pelo menos `held` tempo antes
    do ataque grave: notas rápidas encavaladas (trinados, escalas) não são uma voz sustentada. Ataques simultâneos
    (até `cluster` tempo) ficam para o agrupamento por altura que já existe. Retorna (mantidos, descartados), cada
    um na ordem de entrada."""
    ordered = sorted(range(len(notes)), key=lambda i: (notes[i]['start'], -notes[i]['pitch']))
    active = []; masked = set()
    for index in ordered:
        note = notes[index]
        start = note['start']; duration = note['end'] - start
        active = [other for other in active if other['end'] > start]
        if duration > 0:
            for other in active:
                if other['pitch'] <= note['pitch'] or other['start'] > start - ppq * max(cluster, held): continue
                if other['end'] <= start + ppq * legato: continue
                if min(other['end'], note['end']) - start > ratio * duration:
                    masked.add(index); break
        active.append(note)
    kept = [note for i, note in enumerate(notes) if i not in masked]
    return kept, [note for i, note in enumerate(notes) if i in masked]


def source_identity(note):
    return (note.get('track'), note.get('channel'), note.get('program'))


def mask_inner_by_source(notes, ppq, **options):
    """mask_inner_attacks aplicado separadamente a cada fonte (pista, canal, programa)."""
    groups = {}
    for index, note in enumerate(notes): groups.setdefault(source_identity(note), []).append(index)
    removed = set()
    for indexes in groups.values():
        subset = [notes[i] for i in indexes]
        _, masked = mask_inner_attacks(subset, ppq, **options)
        masked_ids = {id(n) for n in masked}
        removed.update(i for i in indexes if id(notes[i]) in masked_ids)
    return [n for i, n in enumerate(notes) if i not in removed], [n for i, n in enumerate(notes) if i in removed]


# ---- 2. Swing ----------------------------------------------------------------------------------------------------

def swing_estimate(quarters, minimum_beats=8):
    """Razão de swing pelas colcheias de contratempo: em cada tempo com um único ataque interno, a fase dele.

    Considera as fases entre .4 e .8 do tempo. Há swing quando há pelo menos `minimum_beats` desses tempos, mais de
    60% caem entre .55 e .75 e perto de 2/3 (até .06), e o contratempo reto (.5 ± .04) é minoria. Retorna
    {'ratio': fase mediana do contratempo, 'beats': tempos considerados, 'swung': tempos com swing} ou None."""
    by_beat = {}
    for quarter in quarters:
        beat = math.floor(quarter + .06)
        by_beat.setdefault(beat, set()).add(round(quarter - beat, 4))
    phases = []; busy = 0
    for beat, values in by_beat.items():
        inner = [p for p in values if .12 < p < .88]
        if len(inner) == 1 and .4 <= inner[0] <= .8: phases.append(inner[0])
        elif inner: busy += 1
    if len(phases) < minimum_beats: return None
    swung = sorted(p for p in phases if .55 <= p <= .75 and abs(p - 2 / 3) <= .06)
    straight = sum(abs(p - .5) <= .04 for p in phases)
    # Tempos com semicolcheias ou outros desenhos contam contra: o swing precisa ser o desenho dominante.
    if len(swung) <= .6 * len(phases) or len(swung) <= .6 * (len(phases) + busy) or straight >= .25 * len(phases): return None
    middle = len(swung) // 2
    ratio = swung[middle] if len(swung) % 2 else (swung[middle - 1] + swung[middle]) / 2
    return {'ratio': round(ratio, 4), 'beats': len(phases), 'swung': len(swung)}


def straighten(quarter, ratio):
    """Leva a colcheia com swing (fase `ratio` do tempo) para o contratempo reto, de forma linear por partes."""
    if not ratio: return quarter
    beat = math.floor(quarter + 1e-9); phase = quarter - beat
    if phase <= ratio: return beat + phase * .5 / ratio
    return beat + .5 + (phase - ratio) * .5 / (1 - ratio)


def swing_text(ratio):
    """Frações da indicação de swing do MusicXML (<first>/<second>) para a razão estimada."""
    first = max(1, min(99, round(ratio * 100)))
    return first, 100 - first


# ---- 3/4. Grade por tempo ----------------------------------------------------------------------------------------

class TimingOffset:
    """Deslocamento local e constante da execução (tocar sistematicamente adiantado ou atrasado).

    Para cada ataque, a mediana do desvio com sinal até a semicolcheia ou tercina mais próxima entre os ataques a até
    4 tempos dele; vale só quando há pelo menos 6 ataques, a mediana passa de 0,02 tempo e 60% dos ataques desviam
    para o mesmo lado. Um MIDI quantizado não tem deslocamento. at(q) devolve o quanto somar ao quarto (o valor do
    último ataque até q) antes de escolher a grade."""
    REACH = 4

    def __init__(self, quarters):
        ordered = sorted(quarters)
        deviations = []
        for q in ordered:
            phase = q - math.floor(q)
            deviations.append(min((round(phase * 4) / 4 - phase, round(phase * 3) / 3 - phase), key=abs))
        self.quarters = []; self.values = []; self.offsets = {}
        low = high = 0
        for index, q in enumerate(ordered):
            while ordered[low] < q - self.REACH: low += 1
            while high < len(ordered) and ordered[high] <= q + self.REACH: high += 1
            values = deviations[low:high]; offset = 0.0
            if len(values) >= 6:
                middle = sorted(values)[len(values) // 2]
                same = sum(1 for v in values if v * middle > 0 and abs(v) >= .01)
                if abs(middle) >= .02 and same >= .6 * len(values): offset = max(-.1, min(.1, middle))
            self.quarters.append(q); self.values.append(offset)
            if offset: self.offsets[round(q, 4)] = offset

    def at(self, quarter):
        index = bisect_right(self.quarters, quarter + 1e-9) - 1
        return self.values[index] if index >= 0 else 0.0


def attribution_tolerance(quarter, tempo):
    """Antecipação aceita para atribuir um ataque ao tempo seguinte: 40 ms, entre 1/32 e 1/8 de tempo."""
    return min(.125, max(.03, tempo.quarters_for_ms(40, quarter)))


def attribute(quarter, tempo):
    """(tempo, fase) do ataque: um ataque logo antes do tempo pertence a ele (fase negativa pequena)."""
    beat = math.floor(quarter)
    if quarter - beat > 1 - attribution_tolerance(quarter, tempo): beat += 1
    return beat, quarter - beat


def _deviation_cost(distance, quarter_ms, sigma):
    """Custo de um desvio (em tempos) como log-verossimilhança gaussiana, com teto para um ataque isolado."""
    return min(12.0, (distance * quarter_ms / sigma) ** 2 / 2)


def _state_error(phases, div, quarter_ms, sigma):
    slots = [round(p * div) for p in phases]
    if len(set(slots)) != len(slots): return None
    return sum(_deviation_cost(abs(p - s / div), quarter_ms, sigma) for p, s in zip(phases, slots))


def _pair_error(phases, allowed, quarter_ms, sigma):
    total = 0.0
    for phase in phases:
        distance = min(abs(phase - a) for a in allowed)
        if distance > .09: return None
        total += _deviation_cost(distance, quarter_ms, sigma)
    return total


def timing_sigma(bybeat, tempo):
    """Precisão do MIDI em ms: duas vezes o desvio mediano até a semicolcheia ou a tercina mais próxima.

    Fica entre 25 e 35 ms: deslocamentos dessa ordem são a imprecisão natural da execução (ou um adiantamento
    proposital do sequenciador) e não pedem grades mais finas. Um MIDI quantizado numa grade fina é reconhecido à
    parte, pela posição exata dos ataques (EXACT_BONUS)."""
    deviations = sorted(min(abs(p - round(p * 4) / 4), abs(p - round(p * 3) / 3)) * tempo.quarter_ms(beat)
                        for beat, phases in bybeat.items() for p in phases)
    if not deviations: return 25.0
    return min(35.0, max(25.0, 2 * deviations[len(deviations) // 2]))


def _exact(phases, div):
    return all(abs(p * div - round(p * div)) < .005 * div for p in phases)


def choose_grids(bybeat, tempo, meter_map=None):
    """Grade de cada tempo por custo mínimo com Viterbi.

    bybeat: {tempo absoluto: [fases]} com atribuição ao tempo mais próximo. Estados: grades binárias (4, 8, 16),
    ternárias (3, 6, 12) e a tercina de semínimas em dois tempos. A ternária só é candidata quando seu erro fica
    abaixo de 0,6 vez o binário (um MIDI já quantizado em grade binária nunca vira tercina). Retorna
    (grids, choices, pair_starts, triplet_beats, collided): grids só para os tempos com ataques (e os dois tempos de
    cada par); collided são os tempos em que nem a grade mais fina separa os ataques."""
    if not bybeat: return {}, {}, [], [], set()
    meter_map = meter_map or MeterMap(None, 1)
    sigma = timing_sigma(bybeat, tempo)
    first, last = min(bybeat), max(bybeat)
    states = BINARY + TERNARY + ('P1', 'P2')
    family = {s: ('B' if s in BINARY else 'T') for s in states}
    beats = list(range(first, last + 1))
    emissions = []
    for beat in beats:
        phases = sorted(bybeat.get(beat, []))
        quarter_ms = tempo.quarter_ms(beat)
        bar_start, numerator, denominator = meter_map.at(beat + .5)
        is_compound = compound(numerator, denominator)
        costs = {}
        errors = {div: _state_error(phases, div, quarter_ms, sigma) for div in BINARY + TERNARY}
        # As grades de recurso (fusas/semifusas e tercinas de fusa) não entram na comparação: aproximam qualquer
        # posição e esconderiam uma tercina verdadeira.
        best_binary = min((errors[d] for d in (4, 8) if errors[d] is not None), default=None)
        best_ternary = min((errors[d] for d in (3, 6) if errors[d] is not None), default=None)
        # Ternária só quando ela explica claramente melhor os ataques (e nunca no compasso composto, cujas
        # colcheias já são posições binárias da semínima).
        thirds = any(abs(p - 1 / 3) < .05 for p in phases) and any(abs(p - 2 / 3) < .05 for p in phases)
        # Sem grade binária possível (ataques juntos demais), a ternária só entra com as duas posições de tercina.
        ternary_ok = (not is_compound and best_ternary is not None and
                      ((best_binary is None and thirds) or
                       (best_binary is not None and best_binary > .05 and best_ternary < .6 * best_binary)))
        offbeat = any(abs(p) > 1e-6 for p in phases)
        inner = [p for p in phases if abs(p) > 1e-6]
        # A grade mais grossa em que todos os contratempos caem exatamente: é a grade em que o MIDI foi escrito.
        exact = next((d for d in sorted(BINARY + TERNARY) if inner and _exact(inner, d)), None)
        for div in BINARY:
            error = errors[div]
            costs[div] = INFEASIBLE if error is None else error + (COMPLEXITY[div] if offbeat else 0) - (EXACT_BONUS if div == exact else 0)
        for div in TERNARY:
            error = errors[div]
            # Um tempo vazio ou só com o ataque no tempo não decide a família: fica livre, sem custo.
            if not offbeat: costs[div] = 0.0 if error is not None else INFEASIBLE
            else: costs[div] = INFEASIBLE if error is None or not ternary_ok else max(0.0, error + COMPLEXITY[div] - (THIRDS_BONUS if thirds else 0) - (EXACT_BONUS if div == exact else 0))
        # Tercina de semínimas: posições 0 e 2/3 no primeiro tempo, 1/3 no segundo; o par fica inteiro no compasso
        # e começa em tempo par (compassos binários) ou nos dois primeiros tempos (3/4).
        position = round(beat - bar_start)
        length = numerator * 4 / denominator
        pair_start_ok = (not is_compound and position + 2 <= length + 1e-9 and
                         (position % 2 == 0 if numerator % 3 else position in (0, 1)))
        error = _pair_error(phases, (0, 2 / 3), quarter_ms, sigma) if pair_start_ok else None
        # Num MIDI quantizado, as posições exatas do par recebem o mesmo bônus da grade exata: sem ele, duas tercinas
        # de colcheia com ligadura sobre o tempo venceriam a tercina de semínimas.
        exact_pair = bool(inner) and all(abs(p - 2 / 3) < 1e-6 for p in inner)
        costs['P1'] = INFEASIBLE if error is None else error + PAIR_COMPLEXITY - (EXACT_BONUS if exact_pair else 0)
        error = _pair_error(phases, (1 / 3,), quarter_ms, sigma)
        exact_pair = bool(phases) and all(abs(p - 1 / 3) < 1e-6 for p in phases)
        costs['P2'] = INFEASIBLE if error is None else error + PAIR_COMPLEXITY - (EXACT_BONUS if exact_pair else 0)
        emissions.append((phases, costs, errors, best_binary))
    # Viterbi.
    score = {s: emissions[0][1][s] for s in states}
    score['P2'] = INFEASIBLE * 2
    back = []
    for index in range(1, len(beats)):
        phases, costs, _, _ = emissions[index]
        new = {}; pointer = {}
        for state in states:
            options = []
            for previous in states:
                if previous == 'P1' and state != 'P2': continue
                if state == 'P2' and previous != 'P1': continue
                penalty = 0 if family[previous] == family[state] else SWITCH_PENALTY
                options.append((score[previous] + penalty, previous))
            value, previous = min(options, key=lambda item: (item[0], states.index(item[1])))
            new[state] = value + costs[state]; pointer[state] = previous
        score = new; back.append(pointer)
    candidates = [s for s in states if s != 'P1']
    state = min(candidates, key=lambda s: (score[s], states.index(s)))
    path = [state]
    for pointer in reversed(back):
        state = pointer[state]; path.append(state)
    path.reverse()
    grids = {}; choices = {}; pair_starts = []; triplet_beats = []; collided = set()
    for index, (beat, state) in enumerate(zip(beats, path)):
        phases, costs, errors, _ = emissions[index]
        if state == 'P1':
            following = emissions[index + 1][0] if index + 1 < len(beats) else []
            if any(abs(p - 2 / 3) < .09 for p in phases) or any(abs(p - 1 / 3) < .09 for p in following):
                pair_starts.append(beat)
                for b in (beat, beat + 1): grids[b] = 3; choices[b] = TERNARY
                triplet_beats.extend((beat, beat + 1))
                continue
            state = 4 if errors[4] is not None else min((d for d in BINARY if errors[d] is not None), default=16)
        if state == 'P2':
            if beat - 1 in pair_starts: continue
            state = 4 if errors[4] is not None else min((d for d in BINARY if errors[d] is not None), default=16)
        if beat not in bybeat: continue
        if state in TERNARY and not any(abs(p) > 1e-6 for p in phases): state = 4
        if state in TERNARY and errors[state] is None: state = 12
        if state in BINARY and errors[state] is None:
            state = next((d for d in BINARY if errors[d] is not None), BINARY[-1])
        if errors[state] is None: collided.add(beat)
        grids[beat] = state; choices[beat] = BINARY if state in BINARY else TERNARY
        if state in TERNARY: triplet_beats.append(beat)
    # Os tempos do par sem ataques próprios também precisam de opções para os ajustes de fronteira.
    for beat in list(grids):
        choices.setdefault(beat, BINARY if grids[beat] in BINARY else TERNARY)
    return grids, choices, pair_starts, sorted(set(triplet_beats)), collided


def floor_grids(positions, grids, div_unit):
    """Grade de cada tempo (pelo início do tempo) que representa as posições finais dos ataques.

    A escolha usa a atribuição ao tempo mais próximo; uma antecipação escrita (por exemplo uma fusa antes do tempo)
    fica no tempo anterior da partitura, que então precisa de uma grade capaz de representá-la. Retorna as grades
    atualizadas e os tempos refinados [(tempo, de, para)]."""
    by_beat = {}
    for position in positions: by_beat.setdefault(position // div_unit, []).append(position % div_unit)
    result = dict(grids); refined = []
    for beat, offsets in by_beat.items():
        current = result.get(beat, 4)
        if is_pair(current): continue
        family = TERNARY if current % 3 == 0 else BINARY
        def fits(div): return all((offset * div) % div_unit == 0 for offset in offsets)
        if fits(current): continue
        options = [d for d in family if d > current] + [d * 2 for d in family[-1:]] + [32, 64]
        chosen = next((d for d in options if fits(d)), None)
        if chosen is None:
            chosen = next((d for d in (4, 8, 16, 32, 64, 3, 6, 12, 24, 48) if fits(d)), current)
        if chosen != current:
            result[beat] = chosen; refined.append((beat, current, chosen))
    return result, refined


def encode_pairs(grids, pair_starts):
    """Marca na grade os pares ainda intactos (ambos os tempos com grade 3 depois dos ajustes)."""
    kept = []
    for beat in pair_starts:
        if grids.get(beat) == 3 and grids.get(beat + 1, 3) == 3:
            grids[beat] = PAIR_FIRST; grids[beat + 1] = PAIR_SECOND; kept.append(beat)
    return kept


# ---- 5. Compasso -------------------------------------------------------------------------------------------------

def meter_scores(notes, ppq, limit_quarters=1200):
    """Pontuação de 2/4, 3/4, 4/4 e 6/8 pela autocorrelação dos acentos em colcheias.

    Acentos: notas graves e longas, ataques de acorde (três classes de altura juntas) e bumbo do canal 10. Retorna
    {'scores': {...}, 'best': '3/4', 'family': 'binary'|'ternary', 'margin': diferença entre famílias, 'phase'}."""
    eighth = ppq / 2
    size = int(min(limit_quarters * 2, max((n['start'] for n in notes), default=0) / eighth + 2))
    if size < 48: return None
    accent = [0.0] * size; chords = {}
    for note in notes:
        position = note['start'] / eighth; slot = round(position)
        if slot >= size or abs(position - slot) > .2: continue
        if note.get('channel') == 10:
            accent[slot] += 2.0 if note['pitch'] in (35, 36) else 0.0
            continue
        length = min(2.0, max(0.0, (note['end'] - note['start']) / ppq))
        accent[slot] += length * (1.5 if note['pitch'] < 55 else .6)
        chords.setdefault(slot, set()).add(note['pitch'] % 12)
    previous = None
    for slot in sorted(chords):
        if len(chords[slot]) >= 3:
            if chords[slot] != previous: accent[slot] += 1.5
            previous = chords[slot]
    def scored(centered):
        energy = sum(c * c for c in centered)
        if energy <= 0: return None
        def r(lag): return sum(centered[k] * centered[k + lag] for k in range(size - lag)) / energy
        lags = {lag: r(lag) for lag in (2, 3, 4, 6, 8, 12)}
        return {'2/4': lags[4] + .5 * lags[2], '4/4': lags[8] + .5 * lags[4] + .25 * lags[2],
                '3/4': lags[6] + .5 * lags[2] + .25 * lags[12], '6/8': lags[6] + .5 * lags[3] + .25 * lags[12]}
    # Família (binária x ternária): cada posição (tempo ou contratempo de semínima) é centralizada pela própria
    # média, para que o pulso de semínima, comum a todos os compassos, não pese na comparação entre eles.
    means = [sum(accent[parity::2]) / len(accent[parity::2]) for parity in (0, 1)]
    scores = scored([a - means[k % 2] for k, a in enumerate(accent)])
    # 3/4 x 6/8: aí o pulso de semínima decide (tempos em colcheias 0-2-4 ou 0-3), com a centralização simples.
    mean = sum(accent) / size
    plain = scored([a - mean for a in accent])
    if scores is None or plain is None: return None
    binary = max(scores['2/4'], scores['4/4']); ternary = max(scores['3/4'], scores['6/8'])
    if ternary > binary: best = '3/4' if plain['3/4'] >= plain['6/8'] else '6/8'
    else: best = max(('2/4', '4/4'), key=scores.get)
    # Fase do primeiro tempo no compasso escolhido: a posição que concentra os acentos.
    bar = {'2/4': 4, '4/4': 8, '3/4': 6, '6/8': 6}[best]
    sums = [sum(accent[k] for k in range(phase, size, bar)) for phase in range(bar)]
    return {'scores': {k: round(v, 4) for k, v in scores.items()}, 'best': best,
            'family': 'ternary' if ternary > binary else 'binary', 'margin': round(abs(ternary - binary), 4),
            'phase': max(range(bar), key=lambda p: sums[p])}


_METER_CACHE = {}


def meter_guess(parsed, declared):
    """Compasso a usar e a dúvida a relatar. declared: lista de time_signatures do MIDI (pode ser vazia).

    Sem compasso no MIDI, adota 3/4 ou 6/8 só quando a família ternária vence com folga e o primeiro tempo cai no
    início do mapa; senão mantém 4/4. A divergência entre famílias (binária x ternária) vira ponto de revisão."""
    key = (parsed.get('sha256'), len(parsed.get('notes', [])), parsed.get('ppq'))
    if key[0] and key in _METER_CACHE: result = _METER_CACHE[key]
    else:
        result = meter_scores(parsed.get('notes', []), parsed['ppq'])
        if key[0]: _METER_CACHE[key] = result
    if result is None: return None, None
    if declared:
        first = declared[0]; numerator, denominator = first['numerator'], first['denominator']
        family = 'ternary' if numerator % 3 == 0 else 'binary'
        if family != result['family'] and result['margin'] >= .15:
            return None, {'declared': f'{numerator}/{denominator}', 'guess': result['best'], 'scores': result['scores'], 'adopted': False}
        return None, None
    adopted = result['family'] == 'ternary' and result['margin'] >= .15 and result['phase'] == 0
    if result['family'] == 'ternary' and result['margin'] >= .1:
        numerator, denominator = (3, 4) if result['best'] == '3/4' else (6, 8)
        doubt = {'declared': None, 'guess': result['best'], 'scores': result['scores'], 'adopted': adopted}
        meters = [{'tick': 0, 'numerator': numerator, 'denominator': denominator, 'guessed': True}] if adopted else None
        return meters, doubt
    return None, None


# ---- 9. Pausas curtas --------------------------------------------------------------------------------------------

def legato_fill(gap_ticks, duration_ticks, ppq, quarter_ms, threshold_ms=60, share=.25):
    """Uma pausa curta entre duas notas é preenchida quando dura menos de 60 ms ou menos de 25% da nota."""
    if gap_ticks <= 0: return True
    return gap_ticks / ppq * quarter_ms < threshold_ms or gap_ticks < share * duration_ticks


def repeated_rest_keys(items, bar_of):
    """Pausas que se repetem no mesmo ponto de frases repetidas.

    items: [(posição do ataque em DIV, altura, altura seguinte, duração escrita, pausa escrita)], só das pausas
    visíveis que o legato preencheria. A chave é a posição dentro do compasso, as alturas e a duração; quando a
    mesma chave aparece em dois compassos ou mais, a pausa é articulação da frase e fica escrita."""
    counts = {}
    for position, pitch, following, duration, rest in items:
        offset, bar = bar_of(position)
        counts.setdefault((offset, pitch, following, duration, rest), set()).add(bar)
    return {key for key, bars in counts.items() if len(bars) >= 2}
