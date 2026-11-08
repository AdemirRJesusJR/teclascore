"""Métricas de precisão: compara a partitura gerada (MusicXML) com o gabarito de um caso.

A leitura do MusicXML é tolerante: ignora elementos e atributos desconhecidos, aceita vozes com
<backup>/<forward>, várias partes, compassos incompletos (anacruse) e qualidades de cifra novas.
Todas as posições são em semínimas. O gabarito usa a linha do tempo do MIDI; a partitura começa no
seu primeiro compasso, e o deslocamento entre as duas é procurado (com a origem informada pelo
motor como primeira candidata).

Todas as métricas vão de 0 a 1 e "maior é melhor", exceto as listadas em LOWER_IS_BETTER.
Uma métrica que não se aplica ao caso vale None e fica fora das médias.
"""
from collections import Counter
from difflib import SequenceMatcher
from fractions import Fraction
import re
import unicodedata
import xml.etree.ElementTree as ET

ONSET_TOLERANCE = 0.25        # 1/16 (semicolcheia), em semínimas: |erro| estritamente menor
EXACT_TOLERANCE = 1 / 96      # escrita exata do ritmo (tercinas, swing)
DURATION_TOLERANCE = 0.125    # fusa
CHORD_STEP = 0.125            # amostragem das cifras no tempo
STEPS = {'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11}

METRICS = (
    'onset_f1', 'onset_exact_f1', 'pitch', 'pitch_class', 'octave', 'duration', 'spelling',
    'key', 'key_changes', 'meter', 'pickup',
    'chord_root', 'chord_quality', 'chord_bass', 'chord_symbol', 'chord_root_spelling',
    'lyrics_syllables', 'lyrics_words', 'lyrics_dropped', 'lyrics_spoken_off',
)
LOWER_IS_BETTER = {'lyrics_dropped'}
DESCRIPTIONS = {
    'onset_f1': 'F1 dos ataques da melodia (tolerância < 1/16)',
    'onset_exact_f1': 'F1 dos ataques com a escrita rítmica exata (tercinas, swing)',
    'pitch': 'notas pareadas com a altura MIDI exata',
    'pitch_class': 'notas pareadas com a classe de altura certa (qualquer oitava)',
    'octave': 'entre as de classe certa, as que estão na oitava certa',
    'duration': 'notas pareadas com a duração escrita certa (tolerância 1/32)',
    'spelling': 'notas de classe certa com a grafia certa (letra e alteração)',
    'key': 'tonalidade inicial (armadura e modo)',
    'key_changes': 'F1 das mudanças de tonalidade (mesma armadura, até 1 compasso de distância)',
    'meter': 'fórmula de compasso inicial',
    'pickup': 'anacruse com a duração certa (ou ausência dela)',
    'chord_root': 'tempo com a fundamental da cifra certa',
    'chord_quality': 'tempo com a qualidade da cifra certa',
    'chord_bass': 'tempo com o baixo certo onde ele importa (cifra com baixo no gabarito ou na saída)',
    'chord_symbol': 'tempo com fundamental, qualidade e baixo certos',
    'chord_root_spelling': 'tempo com a grafia da fundamental certa (Db x C#)',
    'lyrics_syllables': 'sílabas cantadas na nota certa',
    'lyrics_words': 'palavras começando na nota certa',
    'lyrics_dropped': 'sílabas cantadas ausentes da partitura (menor é melhor)',
    'lyrics_spoken_off': 'sílabas faladas que ficaram fora das notas da melodia',
}

QUALITY_ALIASES = {
    '': '', 'M': '', 'maj': '', 'major': '', 'm': 'm', 'min': 'm', '-': 'm', 'minor': 'm',
    '7': '7', 'dom7': '7', 'maj7': 'maj7', '7M': 'maj7', 'M7': 'maj7', 'Maj7': 'maj7', 'Δ': 'maj7', 'Δ7': 'maj7',
    'm7': 'm7', 'min7': 'm7', '-7': 'm7', 'mMaj7': 'mMaj7', 'm(maj7)': 'mMaj7', 'm7M': 'mMaj7', 'mM7': 'mMaj7',
    'dim': 'dim', '°': 'dim', 'o': 'dim', 'dim7': 'dim7', '°7': 'dim7', 'o7': 'dim7',
    'm7b5': 'm7b5', 'ø': 'm7b5', 'ø7': 'm7b5', 'm7(b5)': 'm7b5', 'aug': 'aug', '+': 'aug', '5+': 'aug', '(#5)': 'aug',
    'sus2': 'sus2', 'sus4': 'sus4', 'sus': 'sus4', '6': '6', 'm6': 'm6', '69': '69', '6/9': '69', '6(9)': '69',
    'm69': 'm69', 'm6/9': 'm69', '9': '9', '7(9)': '9', 'maj9': 'maj9', '7M(9)': 'maj9', 'm9': 'm9', 'm7(9)': 'm9',
    'add9': 'add9', '(add9)': 'add9', 'madd9': 'madd9', 'm(add9)': 'madd9', '7sus4': '7sus4', '7(sus4)': '7sus4',
    '7b9': '7b9', '7(b9)': '7b9', '7#9': '7#9', '7(#9)': '7#9', '13': '13', '7(13)': '13', '9(13)': '13',
    'm11': 'm11', 'm7(11)': 'm11', '11': '11', '7#5': '7#5', '7(#5)': '7#5', '7+5': '7#5', 'aug7': '7#5',
    '7b5': '7b5', '7(b5)': '7b5', '7-5': '7b5', '7b13': '7b13', '7(b13)': '7b13', 'maj7#11': 'maj7#11',
}
KIND_QUALITY = {
    'major': '', 'minor': 'm', 'dominant': '7', 'major-seventh': 'maj7', 'minor-seventh': 'm7',
    'major-minor': 'mMaj7', 'diminished': 'dim', 'diminished-seventh': 'dim7', 'half-diminished': 'm7b5',
    'augmented': 'aug', 'augmented-seventh': '7#5', 'suspended-second': 'sus2', 'suspended-fourth': 'sus4',
    'major-sixth': '6', 'minor-sixth': 'm6', 'dominant-ninth': '9', 'major-ninth': 'maj9', 'minor-ninth': 'm9',
    'dominant-11th': '11', 'minor-11th': 'm11', 'dominant-13th': '13', 'power': '5',
}


# ----------------------------------------------------------------------------- leitura do MusicXML
def _local(tag):
    return tag.rsplit('}', 1)[-1] if isinstance(tag, str) else ''


def _child(element, name):
    for item in element:
        if _local(item.tag) == name:
            return item
    return None


def _children(element, name):
    return [item for item in element if _local(item.tag) == name]


def _text(element, name, default=None):
    item = _child(element, name) if element is not None else None
    return item.text.strip() if item is not None and item.text is not None else default


def _number(value, default=0.0):
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def canonical_quality(text=None, kind=None, degrees=()):
    """Qualidade normalizada a partir do texto da cifra, do tipo MusicXML e dos graus."""
    if text is not None:
        cleaned = text.strip().replace('♭', 'b').replace('♯', '#').replace(' ', '')
        if cleaned in QUALITY_ALIASES:
            return QUALITY_ALIASES[cleaned]
        flat = cleaned.replace('(', '').replace(')', '')
        if flat in QUALITY_ALIASES:
            return QUALITY_ALIASES[flat]
        if cleaned and kind is None:
            return flat
    base = KIND_QUALITY.get((kind or '').strip(), (kind or '').strip())
    added = {(value, alter, kind_) for value, alter, kind_ in degrees}
    if base == '' and (9, 0, 'add') in added:
        return 'add9'
    if base == 'm' and (9, 0, 'add') in added:
        return 'madd9'
    if base == '7' and (4, 0, 'add') in added:
        return '7sus4'
    if base == '7' and (9, -1, 'add') in added:
        return '7b9'
    if base == '7' and (9, 1, 'add') in added:
        return '7#9'
    if base == '6' and (9, 0, 'add') in added:
        return '69'
    if text is not None and text.strip():
        flat = text.strip().replace('(', '').replace(')', '')
        return QUALITY_ALIASES.get(flat, flat)
    return base


def _pitch_of(step, alter, octave):
    return 12 * (octave + 1) + STEPS[step] + alter


def read_musicxml(path_or_text):
    """Notas da melodia, cifras, armaduras, fórmulas de compasso e compassos de uma partitura."""
    text = path_or_text
    if not isinstance(text, str) or not text.lstrip().startswith('<'):
        with open(path_or_text, 'rb') as stream:
            root = ET.fromstring(stream.read())
    else:
        root = ET.fromstring(text.encode('utf-8'))
    names = {}
    part_list = _child(root, 'part-list')
    if part_list is not None:
        for part in _children(part_list, 'score-part'):
            names[part.get('id')] = _text(part, 'part-name', '') or ''
    parts = _children(root, 'part')
    result = {'notes': [], 'harmonies': [], 'keys': [], 'times': [], 'measures': [], 'parts': []}
    for part_index, part in enumerate(parts):
        name = names.get(part.get('id'), '')
        # Pautas auxiliares (segunda voz, pauta de acordes) não são a melodia.
        auxiliary = name.strip().lower() in ('segunda voz', 'acordes', 'second voice', 'chords')
        result['parts'].append({'id': part.get('id'), 'name': name, 'auxiliary': auxiliary})
        divisions = 1.0
        measure_start = 0.0
        open_ties = {}
        for measure in _children(part, 'measure'):
            cursor = 0.0
            length = 0.0
            last_onset = 0.0
            for element in measure:
                tag = _local(element.tag)
                if tag == 'attributes':
                    divisions = _number(_text(element, 'divisions'), divisions) or divisions
                    if part_index == 0:
                        key = _child(element, 'key')
                        if key is not None and _text(key, 'fifths') is not None:
                            result['keys'].append({'quarter': measure_start + cursor,
                                                   'fifths': int(_number(_text(key, 'fifths'))),
                                                   'minor': (_text(key, 'mode', 'major') or '').lower() == 'minor'})
                        time = _child(element, 'time')
                        if time is not None and _text(time, 'beats') is not None:
                            beats = _text(time, 'beats')
                            result['times'].append({'quarter': measure_start + cursor,
                                                    'beats': int(_number(beats.split('+')[0])) if '+' not in beats else
                                                    sum(int(_number(x)) for x in beats.split('+')),
                                                    'beat_type': int(_number(_text(time, 'beat-type'), 4))})
                elif tag == 'backup':
                    cursor -= _number(_text(element, 'duration')) / divisions
                elif tag == 'forward':
                    cursor += _number(_text(element, 'duration')) / divisions
                    length = max(length, cursor)
                elif tag == 'harmony' and part_index == 0:
                    offset = _number(_text(element, 'offset')) / divisions
                    root_element = _child(element, 'root')
                    if root_element is None:
                        continue
                    step = _text(root_element, 'root-step', 'C')
                    alter = int(round(_number(_text(root_element, 'root-alter'))))
                    kind = _child(element, 'kind')
                    degrees = []
                    for degree in _children(element, 'degree'):
                        degrees.append((int(_number(_text(degree, 'degree-value'))),
                                        int(round(_number(_text(degree, 'degree-alter')))),
                                        _text(degree, 'degree-type', 'add')))
                    bass = _child(element, 'bass')
                    bass_value = None
                    if bass is not None and _text(bass, 'bass-step'):
                        bass_step = _text(bass, 'bass-step')
                        bass_alter = int(round(_number(_text(bass, 'bass-alter'))))
                        bass_value = {'step': bass_step, 'alter': bass_alter, 'pc': (STEPS[bass_step] + bass_alter) % 12}
                    quality = canonical_quality(kind.get('text') if kind is not None else None,
                                                kind.text if kind is not None else None, degrees)
                    result['harmonies'].append({'quarter': measure_start + cursor + offset, 'step': step, 'alter': alter,
                                                'pc': (STEPS.get(step, 0) + alter) % 12, 'quality': quality,
                                                'bass': bass_value})
                elif tag == 'note':
                    if _child(element, 'grace') is not None or _child(element, 'cue') is not None:
                        continue
                    duration = _number(_text(element, 'duration')) / divisions
                    chord = _child(element, 'chord') is not None
                    onset = last_onset if chord else cursor
                    if not chord:
                        cursor += duration
                        length = max(length, cursor)
                        last_onset = onset
                    pitch = _child(element, 'pitch')
                    if pitch is None or auxiliary:
                        continue
                    notehead = _text(element, 'notehead')
                    if notehead == 'slash':
                        continue
                    step = _text(pitch, 'step', 'C')
                    alter = int(round(_number(_text(pitch, 'alter'))))
                    octave = int(_number(_text(pitch, 'octave'), 4))
                    midi = _pitch_of(step, alter, octave)
                    absolute = measure_start + onset
                    ties = {item.get('type') for item in _children(element, 'tie')}
                    notations = _child(element, 'notations')
                    if notations is not None:
                        ties |= {item.get('type') for item in _children(notations, 'tied')}
                    voice = _text(element, 'voice', '1')
                    identity = (part_index, voice, midi)
                    lyrics = []
                    for lyric in _children(element, 'lyric'):
                        pieces = [item.text or '' for item in lyric if _local(item.tag) == 'text']
                        if pieces:
                            lyrics.append({'number': lyric.get('number', '1'), 'text': ' '.join(pieces).strip(),
                                           'syllabic': _text(lyric, 'syllabic', 'single'),
                                           'elision': _child(lyric, 'elision') is not None})
                    if 'stop' in ties and identity in open_ties:
                        held = open_ties[identity]
                        held['duration'] = absolute + duration - held['onset']
                        if 'start' not in ties:
                            del open_ties[identity]
                        continue
                    note = {'onset': absolute, 'duration': duration, 'pitch': midi, 'step': step, 'alter': alter,
                            'octave': octave, 'part': part_index, 'chord': chord, 'lyrics': lyrics}
                    result['notes'].append(note)
                    if 'start' in ties:
                        open_ties[identity] = note
            if part_index == 0:
                result['measures'].append({'start': measure_start, 'length': length,
                                           'implicit': measure.get('implicit') == 'yes'})
            measure_start += length
    # Uma linha melódica: no mesmo ataque, só a nota mais aguda.
    by_onset = {}
    for note in result['notes']:
        key = round(note['onset'], 6)
        if key not in by_onset or note['pitch'] > by_onset[key]['pitch']:
            if key in by_onset:
                note['lyrics'] = note['lyrics'] or by_onset[key]['lyrics']
            by_onset[key] = note
    result['notes'] = [by_onset[key] for key in sorted(by_onset)]
    result['harmonies'].sort(key=lambda item: item['quarter'])
    return result


# ----------------------------------------------------------------------------- pareamento
def match_notes(truth, output, tolerance, offset=0.0):
    """Pareamento 1:1 guloso pela menor distância de ataque (empate: mesma altura)."""
    pairs = []
    for i, expected in enumerate(truth):
        for j, written in enumerate(output):
            distance = abs(written['onset'] + offset - expected['onset'])
            if distance < tolerance:
                pairs.append((distance, written['pitch'] != expected['pitch'], i, j))
    pairs.sort()
    used_truth, used_output, matched = set(), set(), []
    for _, _, i, j in pairs:
        if i in used_truth or j in used_output:
            continue
        used_truth.add(i)
        used_output.add(j)
        matched.append((truth[i], output[j]))
    return matched


def f1(matches, expected, written):
    if expected == 0 and written == 0:
        return None
    return 2 * matches / (expected + written) if expected + written else 0.0


def ratio(good, total):
    return good / total if total else None


def choose_offset(truth, output, origin=None):
    """Deslocamento partitura -> MIDI com mais ataques pareados (a origem do motor vence empates)."""
    candidates = []
    if origin is not None:
        candidates.append(float(origin))
    candidates.append(0.0)
    if truth and output:
        candidates.append(truth[0]['onset'] - output[0]['onset'])
    best = None
    for candidate in candidates:
        score = len(match_notes(truth, output, EXACT_TOLERANCE, candidate)) * 2 + len(match_notes(truth, output, ONSET_TOLERANCE, candidate))
        if best is None or score > best[0]:
            best = (score, candidate)
    return best[1] if best else 0.0


# ----------------------------------------------------------------------------- letras
def _normalize(text):
    text = unicodedata.normalize('NFC', text or '').lower()
    return re.sub(r"[^\w]", '', text)


def _tokens(notes, offset):
    """Sílabas escritas, em ordem: (início, texto) e palavras reconstruídas pelo 'syllabic'."""
    syllables, words = [], []
    current = None
    for note in notes:
        for lyric in note['lyrics'][:1]:
            parts = [p for p in re.split(r'[\s‿_]+', lyric['text']) if _normalize(p)]
            onset = note['onset'] + offset
            for index, part in enumerate(parts):
                syllables.append((onset, _normalize(part)))
            syllabic = lyric.get('syllabic', 'single')
            for index, part in enumerate(parts):
                last = index == len(parts) - 1
                if index == 0 and syllabic in ('middle', 'end') and current is not None:
                    current[1] += _normalize(part)
                else:
                    if current is not None:
                        words.append(tuple(current))
                    current = [onset, _normalize(part)]
                if last and syllabic in ('single', 'end'):
                    words.append(tuple(current))
                    current = None
    if current is not None:
        words.append(tuple(current))
    return syllables, words


def _align(expected, written):
    """Pareia sequências de textos em ordem (SequenceMatcher), devolvendo pares de índices."""
    matcher = SequenceMatcher(None, [text for _, text in expected], [text for _, text in written], autojunk=False)
    pairs = []
    for block in matcher.get_matching_blocks():
        for k in range(block.size):
            pairs.append((block.a + k, block.b + k))
    return pairs


def lyric_metrics(truth_lyrics, notes, offset):
    if not truth_lyrics:
        return {}
    written_syllables, written_words = _tokens(notes, offset)
    expected = [(item['onset'], _normalize(item['text'])) for item in truth_lyrics]
    sung = [i for i, item in enumerate(truth_lyrics) if not item.get('spoken')]
    spoken = [i for i, item in enumerate(truth_lyrics) if item.get('spoken')]
    pairs = dict(_align(expected, written_syllables))
    placed = sum(1 for i in sung if i in pairs and abs(written_syllables[pairs[i]][0] - expected[i][0]) < ONSET_TOLERANCE)
    present = sum(1 for i in sung if i in pairs)
    words = []
    for item in truth_lyrics:
        if item.get('spoken'):
            continue
        if item['word_start'] or not words:
            words.append([item['onset'], _normalize(item['text'])])
        else:
            words[-1][1] += _normalize(item['text'])
    word_pairs = _align(words, written_words)
    words_placed = sum(1 for i, j in word_pairs if abs(written_words[j][0] - words[i][0]) < ONSET_TOLERANCE)
    result = {'lyrics_syllables': ratio(placed, len(sung)), 'lyrics_words': ratio(words_placed, len(words)),
              'lyrics_dropped': ratio(len(sung) - present, len(sung))}
    if spoken:
        result['lyrics_spoken_off'] = ratio(sum(1 for i in spoken if i not in pairs), len(spoken))
    return result


# ----------------------------------------------------------------------------- cifras
def _active(harmonies, time, offset):
    current = None
    for item in harmonies:
        if item['quarter'] + offset <= time + 1e-9:
            current = item
        else:
            break
    return current


def chord_metrics(truth_chords, harmonies, offset):
    if not truth_chords:
        return {}
    totals = Counter()
    for expected in truth_chords:
        time = expected['onset']
        while time < expected['end'] - 1e-9:
            written = _active(harmonies, time + CHORD_STEP / 2, offset)
            totals['time'] += 1
            expected_bass = expected['bass_pc'] if expected.get('bass_pc') is not None else expected['root_pc']
            if written is not None:
                written_bass = written['bass']['pc'] if written.get('bass') else written['pc']
                root_ok = written['pc'] == expected['root_pc']
                quality_ok = written['quality'] == expected['quality']
                bass_ok = written_bass == expected_bass
                spelled = written['step'] + ('#' * written['alter'] if written['alter'] > 0 else 'b' * -written['alter'])
                totals['root'] += root_ok
                totals['quality'] += quality_ok
                totals['symbol'] += root_ok and quality_ok and bass_ok
                totals['spelling'] += spelled == expected['root']
                relevant = expected.get('bass_pc') is not None or bool(written.get('bass'))
            else:
                bass_ok = False
                relevant = expected.get('bass_pc') is not None
            if relevant:
                totals['bass_time'] += 1
                totals['bass'] += bass_ok
            time += CHORD_STEP
    return {'chord_root': ratio(totals['root'], totals['time']), 'chord_quality': ratio(totals['quality'], totals['time']),
            'chord_bass': ratio(totals['bass'], totals['bass_time']), 'chord_symbol': ratio(totals['symbol'], totals['time']),
            'chord_root_spelling': ratio(totals['spelling'], totals['time'])}


# ----------------------------------------------------------------------------- tonalidade e compasso
def key_metrics(truth, score, offset):
    keys = score['keys']
    if not keys:
        return {'key': 0.0, 'key_changes': 0.0 if truth.get('key_changes') else None}
    initial = keys[0]
    expected = truth['key']
    result = {'key': float(initial['fifths'] == expected['fifths'] and initial['minor'] == bool(expected.get('minor')))}
    written_changes = []
    previous = (initial['fifths'], initial['minor'])
    for item in keys[1:]:
        value = (item['fifths'], item['minor'])
        if value != previous:
            written_changes.append({'quarter': item['quarter'] + offset, 'fifths': item['fifths']})
        previous = value
    expected_changes = truth.get('key_changes', [])
    if not expected_changes and not written_changes:
        result['key_changes'] = None
        return result
    bar = float(truth.get('bar_quarters', 4))
    used = set()
    hits = 0
    for change in expected_changes:
        for index, written in enumerate(written_changes):
            if index not in used and written['fifths'] == change['fifths'] and abs(written['quarter'] - change['quarter']) <= bar + 1e-9:
                used.add(index)
                hits += 1
                break
    result['key_changes'] = f1(hits, len(expected_changes), len(written_changes))
    return result


def meter_metrics(truth, score):
    times = score['times']
    expected = truth['meter']
    result = {'meter': float(bool(times) and times[0]['beats'] == expected['beats'] and times[0]['beat_type'] == expected['beat_type'])}
    measures = score['measures']
    if not measures:
        result['pickup'] = 0.0
        return result
    pickup = 0.0
    if times:
        full = times[0]['beats'] * 4 / times[0]['beat_type']
        first = measures[0]['length']
        if first < full - 1e-6:
            pickup = first
    result['pickup'] = float(abs(pickup - float(truth.get('pickup_quarters', 0))) < 1e-6)
    return result


# ----------------------------------------------------------------------------- avaliação
def empty_score():
    return {'notes': [], 'harmonies': [], 'keys': [], 'times': [], 'measures': [], 'parts': []}


def evaluate(truth, score=None, origin=None):
    """Métricas de um caso. Sem partitura (o motor falhou), tudo que se aplica vale o pior valor."""
    if score is None:
        metrics, details = evaluate(truth, empty_score(), origin)
        worst = {name: (None if value is None else 1.0 if name in LOWER_IS_BETTER else 0.0)
                 for name, value in metrics.items()}
        return worst, dict(details, failed=True)
    expected = sorted(truth.get('melody', []), key=lambda n: n['onset'])
    written = [note for note in score['notes']]
    offset = choose_offset(expected, written, origin)
    matched = match_notes(expected, written, ONSET_TOLERANCE, offset)
    exact = match_notes(expected, written, EXACT_TOLERANCE, offset)
    same_class = [(e, w) for e, w in matched if (e['pitch'] - w['pitch']) % 12 == 0]
    metrics = dict.fromkeys(METRICS)
    metrics.update({
        'onset_f1': f1(len(matched), len(expected), len(written)),
        'onset_exact_f1': f1(len(exact), len(expected), len(written)),
        'pitch': ratio(sum(e['pitch'] == w['pitch'] for e, w in matched), len(matched)) if expected else None,
        'pitch_class': ratio(len(same_class), len(matched)) if expected else None,
        'octave': ratio(sum(e['pitch'] == w['pitch'] for e, w in same_class), len(same_class)) if expected else None,
        'duration': ratio(sum(abs(w['duration'] - e['duration']) <= DURATION_TOLERANCE + 1e-9 for e, w in matched),
                          len(matched)) if expected else None,
        'spelling': ratio(sum((e['step'], e['alter']) == (w['step'], w['alter']) for e, w in same_class),
                          len(same_class)) if expected else None,
    })
    # Sem nenhuma nota pareada a melodia falhou por inteiro: as razões por nota valem 0, não "não se aplica".
    if expected and not matched:
        for name in ('pitch', 'pitch_class', 'octave', 'duration', 'spelling'):
            metrics[name] = 0.0
    if expected and matched and not same_class:
        metrics['octave'] = metrics['spelling'] = 0.0
    metrics.update(key_metrics(truth, score, offset))
    metrics.update(meter_metrics(truth, score))
    metrics.update(chord_metrics(truth.get('chords', []), score['harmonies'], offset))
    metrics.update(lyric_metrics(truth.get('lyrics', []), score['notes'], offset))
    details = {'offset_quarters': offset, 'expected_notes': len(expected), 'written_notes': len(written),
               'matched_notes': len(matched), 'exact_notes': len(exact), 'written_chords': len(score['harmonies']),
               'written_keys': [(round(k['quarter'] + offset, 3), k['fifths'], k['minor']) for k in score['keys']],
               'written_time': score['times'][0] if score['times'] else None}
    return {name: (round(value, 4) if isinstance(value, float) else value) for name, value in metrics.items()}, details


def averages(per_case):
    """Média de cada métrica entre os casos em que ela se aplica."""
    result = {}
    for name in METRICS:
        values = [case[name] for case in per_case if case.get(name) is not None]
        result[name] = round(sum(values) / len(values), 4) if values else None
    return result


def compare(baseline, current, tolerance=0.02):
    """Pioras (além da tolerância) e melhoras por caso e nas médias. Métrica que some conta como piora."""
    regressions, improvements = [], []

    def check(scope, name, old, new):
        if old is None:
            if new is not None:
                improvements.append({'scope': scope, 'metric': name, 'baseline': None, 'current': new})
            return
        if new is None:
            regressions.append({'scope': scope, 'metric': name, 'baseline': old, 'current': None})
            return
        delta = (old - new) if name in LOWER_IS_BETTER else (new - old)
        item = {'scope': scope, 'metric': name, 'baseline': old, 'current': new, 'delta': round(delta, 4)}
        if delta < -tolerance:
            regressions.append(item)
        elif delta > tolerance:
            improvements.append(item)

    for case, old_metrics in baseline.get('cases', {}).items():
        new_metrics = current.get('cases', {}).get(case)
        if new_metrics is None:
            regressions.append({'scope': case, 'metric': '*', 'baseline': 'presente', 'current': 'caso ausente'})
            continue
        for name in METRICS:
            check(case, name, old_metrics.get('metrics', {}).get(name), new_metrics.get('metrics', {}).get(name))
    for name in METRICS:
        check('media', name, baseline.get('averages', {}).get(name), current.get('averages', {}).get(name))
    return regressions, improvements
