"""Grafia das notas da melodia (Dó# ou Réb) pela cifra vigente e pelo contexto tonal.

- Notas do acorde vigente: a grafia vem da fundamental escrita na cifra (root_spelling, quando existe) e da
  qualidade do acorde (terça maior = duas letras acima, quinta diminuta = quatro letras acima etc.).
- Demais notas: linha de quintas (Temperley; ps13 de Meredith), escolhendo a grafia mais próxima do centro tonal
  e da janela local (cerca de dois compassos), com penalidade de condução: cromatismo ascendente com sustenido,
  descendente com bemol, e duas notas vizinhas a um semitom nunca com a mesma letra.
- Acidentes de cortesia: uma nota que volta à armadura no compasso seguinte a uma alteração recebe o acidente.
Posição na linha de quintas: F=-1, C=0, G=1, D=2, A=3, E=4, B=5; cada sustenido soma 7 e cada bemol subtrai 7.
"""
LETTERS = 'FCGDAEB'
STEP_PC = {'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11}
# Intervalo do acorde (semitons) → deslocamento na linha de quintas.
INTERVAL_LOF = {0: 0, 1: -5, 2: 2, 3: -3, 4: 4, 5: -1, 6: -6, 7: 1, 8: -4, 9: 3, 10: -2, 11: 5}
QUALITY_LOF = {'aug': {8: 8}, 'dim7': {9: -9}}
UNUSUAL = {11, 12, -7, -8}          # E#, B#, Cb, Fb
CHROMATIC = 4.0                     # condução cromática contrária (Ré-Dó#-Dó descendo); vence a proximidade da armadura


def lof_of(letter, alter):
    return LETTERS.index(letter) - 1 + 7 * alter


def name_of(lof):
    """(letra, alteração) de uma posição da linha de quintas."""
    return LETTERS[(lof + 1) % 7], (lof + 1) // 7


def pc_of(lof):
    return (lof * 7) % 12


def _spelled(text):
    if not text: return None
    letter = text[0].upper()
    if letter not in LETTERS: return None
    return lof_of(letter, text.count('#') - text.count('b'))


def chord_tones(chord, key_map, intervals):
    """{classe de altura: posição na linha de quintas} das notas do acorde."""
    root = _spelled(chord.get('root_spelling'))
    if root is None:
        letter, alter = key_map[chord['root'] % 12]; root = lof_of(letter, alter)
    special = QUALITY_LOF.get(chord.get('quality'), {})
    result = {}
    for interval in intervals:
        offset = special.get(interval % 12, INTERVAL_LOF[interval % 12])
        result[(chord['root'] + interval) % 12] = root + offset
    return result


def _candidates(pc):
    return [q for q in range(-8, 13) if pc_of(q) == pc]


def spell_notes(notes, chords, key_at, key_map_at, intervals_of, window=8 * 960):
    """Grava em cada nota 'spelling' = (letra, alteração).

    notes: eventos com 'start' e 'pitch' (posições em DIV); chords: cifras com 'tick', 'root', 'quality' e
    'root_spelling' opcional; key_at(pos) → (fifths, minor); key_map_at(pos) → mapa {pc: (letra, alteração)} da
    armadura; intervals_of(quality) → intervalos do acorde. Retorna quantas notas mudaram em relação ao mapa fixo."""
    ordered = sorted((n for n in notes if 'pitch' in n and not n.get('slash')), key=lambda n: n['start'])
    chords = sorted(chords, key=lambda c: c['tick'])
    ticks = [c['tick'] for c in chords]
    history = []; changed = 0
    from bisect import bisect_right
    def active_chord(position):
        index = bisect_right(ticks, position) - 1
        return chords[index] if index >= 0 else None
    def default(note):
        letter, alter = key_map_at(note['start'])[note['pitch'] % 12]
        chord = active_chord(note['start'])
        if chord is not None:
            tones = chord_tones(chord, key_map_at(note['start']), intervals_of(chord['quality']))
            if note['pitch'] % 12 in tones: return tones[note['pitch'] % 12]
        return lof_of(letter, alter)
    for index, note in enumerate(ordered):
        pc = note['pitch'] % 12; position = note['start']
        fifths, minor = key_at(position)
        key_map = key_map_at(position)
        chord = active_chord(position)
        tones = chord_tones(chord, key_map, intervals_of(chord['quality'])) if chord is not None else {}
        letter, alter = key_map[pc]; fixed = lof_of(letter, alter)
        if pc in tones:
            choice = tones[pc]
        else:
            center = fifths + 2 + (1 if minor else 0)
            recent = [q for p, q in history if position - p <= window]
            recent += list(tones.values())
            local = sum(recent) / len(recent) if recent else center
            previous = ordered[index - 1] if index else None
            following = ordered[index + 1] if index + 1 < len(ordered) else None
            previous_lof = history[-1][1] if history else None
            following_lof = default(following) if following is not None else None
            costs = []
            for q in _candidates(pc):
                cost = abs(q - center) + .7 * abs(q - local) + (2 if q in UNUSUAL else 0)
                name = name_of(q)[0]
                for other, other_lof in ((previous, previous_lof), (following, following_lof)):
                    if other is None or other_lof is None: continue
                    if abs(other['pitch'] - note['pitch']) == 1 and name_of(other_lof)[0] == name: cost += 3
                # Condução cromática: subindo por semitom, sustenido (posição maior); descendo, bemol.
                rising = (previous is not None and note['pitch'] - previous['pitch'] == 1) or \
                         (following is not None and following['pitch'] - note['pitch'] == 1)
                falling = (previous is not None and previous['pitch'] - note['pitch'] == 1) or \
                          (following is not None and note['pitch'] - following['pitch'] == 1)
                if rising and not falling: cost += 0 if q >= fixed and q > 0 else CHROMATIC
                if falling and not rising: cost += 0 if q <= fixed and q < 6 else CHROMATIC
                costs.append((cost, abs(q - fixed), q))
            choice = min(costs)[2] if costs else fixed
        note['spelling'] = name_of(choice)
        if choice != fixed: changed += 1
        history.append((position, choice))
    return changed


def key_signature(fifths):
    """{letra: alteração} da armadura."""
    result = {letter: 0 for letter in LETTERS}
    for letter in ('FCGDAEB'[:fifths] if fifths > 0 else 'BEADGCF'[:-fifths]): result[letter] = 1 if fifths > 0 else -1
    return result


def courtesy_accidentals(notes, bars, fifths_at):
    """Acidente de cortesia: a primeira nota de um compasso que volta à armadura depois de uma alteração da mesma
    letra e oitava no compasso anterior. Grava 'courtesy' (sharp, flat, natural) na nota. Retorna a contagem."""
    names = {-2: 'flat-flat', -1: 'flat', 0: 'natural', 1: 'sharp', 2: 'double-sharp'}
    previous = {}; count = 0
    ordered = sorted((n for n in notes if n.get('spelling')), key=lambda n: n['start'])
    index = 0
    for bar in bars:
        current = {}
        while index < len(ordered) and ordered[index]['start'] < bar['end']:
            note = ordered[index]; index += 1
            if note['start'] < bar['start']: continue
            letter, alter = note['spelling']
            octave = (note['pitch'] - STEP_PC[letter] - alter) // 12 - 1
            key_alter = key_signature(fifths_at(note['start']))[letter]
            slot = (letter, octave)
            if slot not in current:
                before = previous.get(slot)
                if before is not None and before != key_alter and alter == key_alter:
                    note['courtesy'] = names[alter]; count += 1
            current[slot] = alter
        # Uma nota ligada que atravessa a barra mantém a alteração no compasso seguinte.
        previous = current
    return count
