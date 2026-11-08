"""Divisão silábica do português do Brasil para letras de música, sem dependências externas.

syllabify('coração') -> ['co', 'ra', 'ção']. As regras seguem a divisão escolar:
- dígrafos ch, lh, nh e qu/gu antes de vogal ficam juntos (fi-lho, guer-ra, quan-do);
- rr, ss, sc, sç e xc se separam (car-ro, pás-sa-ro, nas-cer, ex-ce-to);
- encontros consonantais com l ou r ficam juntos (pa-la-vra, a-bra-ço);
- ditongos ficam juntos (pai-xão, cai-xa, mui-to) e hiatos se separam (sa-í-da, ra-i-nha, ju-iz, po-e-ta);
- sequências átonas no fim de palavras com acento antes delas são ditongos crescentes (gló-ria, sé-rio, á-gua).
Pontuação junto da palavra acompanha a primeira ou a última sílaba; hífen de palavra composta separa sílabas.
"""
import re
import unicodedata

VOWELS = set('aeiouáàâãéêíóôõúüy')
STRONG = set('aeoáàâãéêóôõ')
ACCENTED = set('áàâãéêíóôõú')
# Encontros consonantais que abrem sílaba (pra, bla, tre...). "tl" e "dl" se separam (at-las).
ONSETS = {a + b for a in 'bcdfgkptv' for b in 'lr'} - {'tl', 'dl', 'vl'}
NASAL_DIPHTHONGS = {('ã', 'o'), ('ã', 'e'), ('õ', 'e'), ('ã', 'i')}
# Palavras estrangeiras comuns em letras brasileiras, com a divisão cantada.
FOREIGN = {
    'love': ['love'], 'baby': ['ba', 'by'], 'yeah': ['yeah'], 'girl': ['girl'], 'night': ['night'],
    'time': ['time'], 'happy': ['hap', 'py'], 'party': ['par', 'ty'], 'sexy': ['se', 'xy'], 'crazy': ['cra', 'zy'],
    'forever': ['fo', 're', 'ver'], 'never': ['ne', 'ver'], 'money': ['mo', 'ney'], 'honey': ['ho', 'ney'],
    'lady': ['la', 'dy'], 'body': ['bo', 'dy'], 'you': ['you'], 'my': ['my'], 'me': ['me'], 'oh': ['oh'],
    'show': ['show'], 'shake': ['shake'], 'dance': ['dance'], 'remix': ['re', 'mix'], 'hey': ['hey'],
    'okay': ['o', 'kay'], 'ok': ['ok'], 'bye': ['bye'], 'come': ['come'], 'one': ['one'], 'wow': ['wow'],
}


def _units(lower):
    """(início, fim, tipo) de cada unidade: 'V' vogal, 'C' consoante (dígrafos e qu/gu valem uma)."""
    units = []
    i = 0
    n = len(lower)
    while i < n:
        c = lower[i]
        if lower[i:i + 2] in ('ch', 'lh', 'nh'):
            units.append([i, i + 2, 'C'])
            i += 2
        elif c in 'qg' and i + 2 < n and lower[i + 1] in 'uü' and lower[i + 2] in VOWELS and lower[i + 2] not in 'uú':
            units.append([i, i + 2, 'C'])
            i += 2
        elif c in VOWELS:
            units.append([i, i + 1, 'V'])
            i += 1
        elif c.isalpha():
            units.append([i, i + 1, 'C'])
            i += 1
        else:
            # Apóstrofo ou outro sinal no meio da palavra (d'água): fica com a unidade anterior.
            if units:
                units[-1][1] = i + 1
            else:
                units.append([i, i + 1, 'C'])
            i += 1
    return units


def _letter(lower, unit):
    return lower[unit[0]]


def _hiatus(lower, units, k, accent_before):
    """True quando as vogais units[k] e units[k+1] ficam em sílabas diferentes."""
    a = _letter(lower, units[k])
    b = _letter(lower, units[k + 1])
    if b in 'íú' or a in 'íú':
        return True
    if (a, b) in NASAL_DIPHTHONGS:
        return False
    after = units[k + 2:]
    tail_consonants = all(u[2] == 'C' for u in after)
    if a in STRONG and b in STRONG:
        # Fim átono depois de sílaba acentuada: má-goa, ó-leo, ró-seo.
        if accent_before and tail_consonants and a not in ACCENTED and b not in ACCENTED and len(after) <= 1:
            return False
        return True
    if b in 'iuy' and b not in ACCENTED:
        # i/u seguido de nh, ou de l, m, n, r, z que fecham a sílaba: ra-i-nha, ju-iz, sa-ir, a-in-da.
        if after and after[0][2] == 'C':
            nxt = lower[after[0][0]:after[0][1]]
            following = after[1] if len(after) > 1 else None
            if nxt == 'nh':
                return True
            if nxt in ('l', 'm', 'n', 'r', 'z') and (following is None or following[2] == 'C'):
                if nxt == 'r' and following is not None and lower[following[0]] == 'r':
                    return False  # bair-ro
                return True
        return False
    # i/u átono seguido de vogal forte (ia, io, ua, ie): hiato, salvo no fim átono de palavra acentuada (gló-ria).
    if accent_before and tail_consonants and len(after) <= 1 and b not in ACCENTED:
        return False
    return True


def _core_syllables(text):
    lower = text.lower()
    if lower in FOREIGN:
        result = []
        position = 0
        for piece in FOREIGN[lower]:
            result.append(text[position:position + len(piece)])
            position += len(piece)
        return result
    units = _units(lower)
    vowel_positions = [k for k, u in enumerate(units) if u[2] == 'V']
    if not vowel_positions:
        return [text]
    # Grupos de vogais seguidas: decide ditongo ou hiato entre cada par.
    breaks = set()  # índice k: a fronteira fica antes de units[k]
    k = 0
    while k < len(units):
        if units[k][2] != 'V':
            k += 1
            continue
        run = [k]
        while run[-1] + 1 < len(units) and units[run[-1] + 1][2] == 'V':
            run.append(run[-1] + 1)
        joined = {}
        for a in run[:-1]:
            accent_before = any(lower[units[j][0]] in ACCENTED for j in range(a))
            joined[a] = not _hiatus(lower, units, a, accent_before)
        # Três vogais juntas não formam uma sílaba só (fora de qu/gu): sa-iu, prai-a.
        for a in run[:-2]:
            if joined[a] and joined[a + 1]:
                if lower[units[a][0]] in STRONG and lower[units[a + 1][0]] in 'iu' and lower[units[a + 2][0]] in 'iu':
                    joined[a] = False
                else:
                    joined[a + 1] = False
        for a in run[:-1]:
            if not joined[a]:
                breaks.add(a + 1)
        k = run[-1] + 1
    # Consoantes entre núcleos.
    nuclei = [k for k in range(len(units)) if units[k][2] == 'V' and (k == 0 or units[k - 1][2] != 'V' or k in breaks)]
    for left, right in zip(nuclei, nuclei[1:]):
        # Última vogal do núcleo da esquerda.
        end = left
        while end + 1 < right and units[end + 1][2] == 'V':
            end += 1
        consonants = list(range(end + 1, right))
        if not consonants:
            breaks.add(right)
            continue
        if len(consonants) == 1:
            breaks.add(consonants[0])
            continue
        last_two = ''.join(lower[units[j][0]:units[j][1]] for j in consonants[-2:])
        cluster = len(last_two) == 2 and last_two in ONSETS
        breaks.add(consonants[-2] if cluster else consonants[-1])
    pieces = []
    start = 0
    for k in sorted(breaks):
        if k <= 0 or k >= len(units):
            continue
        cut = units[k][0]
        pieces.append(text[start:cut])
        start = cut
    pieces.append(text[start:])
    return [p for p in pieces if p]


_EDGE = re.compile(r"^([^\w]*)(.*?)([^\w]*)$", re.S)


def syllabify(word):
    """Sílabas de uma palavra, com a pontuação da ponta junto da primeira ou da última sílaba.

    A concatenação das sílabas reproduz a palavra, exceto o hífen de palavras compostas, que vira fronteira.
    """
    if not word:
        return []
    match = _EDGE.match(word)
    lead, core, trail = match.group(1), match.group(2), match.group(3)
    if not core:
        return [word]
    result = []
    for part in core.split('-'):
        if part:
            result.extend(_core_syllables(part))
    if not result:
        return [word]
    result[0] = lead + result[0]
    result[-1] = result[-1] + trail
    return result


def count(word):
    return len(syllabify(word))


def is_vowel(char):
    return unicodedata.normalize('NFC', char).lower() in VOWELS
