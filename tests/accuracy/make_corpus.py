"""Corpus sintético e determinístico para medir a precisão do motor.

Cada caso gera um MIDI e, ao lado, a resposta esperada (gabarito) em JSON: melodia como deve ser
escrita (altura, grafia, início e duração em semínimas na linha do tempo do MIDI), cifras com
fundamental, qualidade e baixo, tonalidade e mudanças, compasso, anacruse e sílabas da letra.
Nada aqui lê arquivos do usuário. Rodar duas vezes produz bytes idênticos.

    python tests/accuracy/make_corpus.py <pasta>
"""
from fractions import Fraction as F
from pathlib import Path
import argparse
import hashlib
import json
import struct

PPQ = 480
SCHEMA = 1
NATURALS = {'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11}
LETTERS = 'CDEFGAB'
# Notas do acorde, em semitons acima da fundamental (vocabulário próprio do corpus, independente do motor).
TONES = {
    '': (0, 4, 7), 'm': (0, 3, 7), '7': (0, 4, 7, 10), 'maj7': (0, 4, 7, 11), 'm7': (0, 3, 7, 10),
    'dim': (0, 3, 6), 'm7b5': (0, 3, 6, 10), '6': (0, 4, 7, 9), 'm6': (0, 3, 7, 9), '9': (0, 4, 7, 10, 14),
    'm9': (0, 3, 7, 10, 14), '69': (0, 4, 7, 9, 14), '7b9': (0, 4, 7, 10, 13), '13': (0, 4, 10, 14, 21),
    'sus4': (0, 5, 7), '7sus4': (0, 5, 7, 10),
}
MELODY = {'track': 1, 'channel': 1, 'program': 0}
CHORDS_CHANNEL, CHORDS_PROGRAM = 2, 4
BASS_CHANNEL, BASS_PROGRAM = 3, 33


# ----------------------------------------------------------------------------- notas e acordes
def parse_name(name):
    """'G#4' -> (pitch, step, alter, octave)."""
    step = name[0]
    rest = name[1:]
    alter = 0
    while rest and rest[0] in '#b':
        alter += 1 if rest[0] == '#' else -1
        rest = rest[1:]
    octave = int(rest)
    return 12 * (octave + 1) + NATURALS[step] + alter, step, alter, octave


def name_of(step, alter, octave):
    return step + ('#' * alter if alter > 0 else 'b' * -alter) + str(octave)


def transpose_name(name, letters, semitones):
    """Transposição com grafia: avança `letters` letras e `semitones` semitons."""
    pitch, step, alter, octave = parse_name(name)
    index = LETTERS.index(step) + letters
    new_step = LETTERS[index % 7]
    new_octave = octave + index // 7
    natural = 12 * (new_octave + 1) + NATURALS[new_step]
    return name_of(new_step, pitch + semitones - natural, new_octave)


def melody(text, start=0):
    """'E4:1 r:1/2 G#4:1/2' -> notas com início e duração escritos (semínimas)."""
    notes = []
    cursor = F(start)
    for token in text.split():
        name, duration = token.split(':')
        duration = F(duration)
        if name != 'r':
            pitch, step, alter, octave = parse_name(name)
            notes.append({'onset': cursor, 'duration': duration, 'pitch': pitch,
                          'step': step, 'alter': alter, 'octave': octave})
        cursor += duration
    return notes


def parse_root(text):
    step = text[0]
    alter = text[1:].count('#') - text[1:].count('b')
    return step, alter, (NATURALS[step] + alter) % 12


def chord(symbol):
    """'D/F#', 'A7b9', 'C69' -> fundamental, qualidade e baixo."""
    body, _, bass = symbol.partition('/')
    root_length = 2 if len(body) > 1 and body[1] in '#b' else 1
    root, quality = body[:root_length], body[root_length:]
    if quality not in TONES:
        raise ValueError('qualidade desconhecida no corpus: ' + symbol)
    step, alter, pc = parse_root(root)
    result = {'symbol': symbol, 'root': root, 'root_pc': pc, 'quality': quality, 'bass': None, 'bass_pc': None}
    if bass:
        result['bass'] = bass
        result['bass_pc'] = parse_root(bass)[2]
    return result


def progression(items, start=0):
    """[('C', 4), ('G/B', 4, voicing)] -> acordes com início e fim (semínimas)."""
    result = []
    cursor = F(start)
    for item in items:
        symbol, duration = item[0], F(item[1])
        value = chord(symbol)
        value.update(onset=cursor, end=cursor + duration)
        if len(item) > 2:
            value['voicing'] = list(item[2])
        result.append(value)
        cursor += duration
    return result


def transpose_chord(value, letters, semitones):
    def moved(root):
        return transpose_name(root + '4', letters, semitones)[:-1]
    symbol = moved(value['root']) + value['quality'] + ('/' + moved(value['bass']) if value['bass'] else '')
    result = chord(symbol)
    result.update(onset=value['onset'], end=value['end'])
    if 'voicing' in value:
        result['voicing'] = [pitch + semitones for pitch in value['voicing']]
    return result


def default_voicing(value):
    """Acorde em posição fechada com a fundamental entre C3 e B3 (grave reforçado pelo baixo)."""
    base = 48 + value['root_pc']
    return [base + interval for interval in TONES[value['quality']]]


# ----------------------------------------------------------------------------- acompanhamento
def accompaniment(chords, bar, hit, start=0, pedal=None, bass_every_bar=True):
    """Blocos no `hit` (semínimas) a partir de `start`; baixo sustentado na fundamental ou no baixo da cifra."""
    upper, bass = [], []
    for value in chords:
        voicing = value.get('voicing') or default_voicing(value)
        t = value['onset']
        hits = [t]
        position = start + ((t - start) // hit + 1) * hit
        while position < value['end']:
            hits.append(position)
            position += hit
        for index, onset in enumerate(hits):
            stop = hits[index + 1] if index + 1 < len(hits) else value['end']
            for pitch in voicing:
                upper.append((onset, onset + (stop - onset) * F(95, 100), pitch, 70))
        if pedal is None:
            pc = value['bass_pc'] if value['bass_pc'] is not None else value['root_pc']
            pitch = 36 + pc
            attacks = [t]
            if bass_every_bar:
                position = start + ((t - start) // bar + 1) * bar
                while position < value['end']:
                    attacks.append(position)
                    position += bar
            for index, onset in enumerate(attacks):
                stop = attacks[index + 1] if index + 1 < len(attacks) else value['end']
                bass.append((onset, onset + (stop - onset) * F(98, 100), pitch, 85))
    if pedal is not None:
        first, last = chords[0]['onset'], chords[-1]['end']
        position = first
        while position < last:
            stop = min(last, position + bar)
            bass.append((position, position + (stop - position) * F(98, 100), pedal, 85))
            position = stop
    return upper, bass


def waltz(chords, start=0):
    """3/4: baixo no 1º tempo, acorde nos tempos 2 e 3 (sem meta de compasso, a métrica vem do padrão)."""
    upper, bass = [], []
    for value in chords:
        voicing = value.get('voicing') or default_voicing(value)
        pc = value['bass_pc'] if value['bass_pc'] is not None else value['root_pc']
        position = value['onset']
        while position < value['end']:
            beat = (position - start) % 3
            if beat == 0:
                bass.append((position, position + F(9, 10), 36 + pc, 90))
            else:
                for pitch in voicing:
                    upper.append((position, position + F(8, 10), pitch, 64))
            position += 1
    return upper, bass


# ----------------------------------------------------------------------------- execução da melodia
def perform(notes, articulation=F(9, 10), swing=False, shift=0):
    """Notas tocadas: duração encurtada (articulação) e, com swing, colcheias do contratempo atrasadas para 2/3."""
    played = []
    for note in notes:
        onset, end = note['onset'], note['onset'] + note['duration']
        if swing:
            def swung(t):
                beat = t - t % 1
                return beat + F(2, 3) if t % 1 == F(1, 2) else t
            onset, end = swung(onset), swung(end)
        stop = onset + (end - onset) * articulation
        played.append((onset + shift, stop + shift, note['pitch'], 96))
    return played


# ----------------------------------------------------------------------------- letras
def syllables(text):
    """'Eu vou can-tar que_eu' -> [(sílaba, início de palavra, mesma nota da anterior)]."""
    result = []
    for word in text.split():
        for group_index, group in enumerate(word.split('_')):
            for index, piece in enumerate(group.split('-')):
                result.append((piece, index == 0, group_index > 0 and index == 0))
    return result


def lay_lyrics(notes, text, start_note=0):
    """Distribui as sílabas pelas notas, em ordem; '_' junta duas sílabas na mesma nota."""
    placed = []
    note_index = start_note - 1
    for syllable, word_start, same_note in syllables(text):
        if not same_note:
            note_index += 1
        note = notes[note_index]
        placed.append({'onset': note['onset'], 'text': syllable, 'word_start': word_start, 'spoken': False})
    return placed


def lyric_meta_events(placed, delay=0, encoding='utf-8'):
    """Eventos de letra (meta 5) no padrão com hífen: 'can-' seguido de 'tar'."""
    events = []
    for index, item in enumerate(placed):
        following = placed[index + 1] if index + 1 < len(placed) else None
        continues = following is not None and not following['word_start']
        text = item['text'] + ('-' if continues else ' ')
        events.append((item['onset'] + delay, 5, text.encode(encoding)))
    return events


def karaoke_word_events(placed, lines, encoding='cp1252'):
    """Karaokê palavra por palavra (meta 1): '\\' abre parágrafo, '/' abre linha, espaço separa palavras."""
    events = [(F(0), 1, b'@KMIDI KARAOKE FILE'), (F(0), 1, b'@T Corpus sintetico')]
    words = []
    for item in placed:
        if item['word_start']:
            words.append([item['onset'], item['text']])
        else:
            words[-1][1] += item['text']
    for index, (onset, word) in enumerate(words):
        prefix = '\\' if index == 0 or index in lines.get('paragraphs', ()) else '/' if index in lines.get('lines', ()) else ' '
        events.append((onset, 1, (prefix + word).encode(encoding)))
    return events


# ----------------------------------------------------------------------------- escrita do MIDI
def vlq(value):
    data = [value & 127]
    value >>= 7
    while value:
        data.insert(0, (value & 127) | 128)
        value >>= 7
    return bytes(data)


def ticks(quarters):
    return int(round(F(quarters) * PPQ))


def track_bytes(events):
    """events: (tick, ordem, bytes). Desligamentos antes de ligamentos no mesmo tick."""
    body = b''
    previous = 0
    for tick, _, message in sorted(events, key=lambda e: (e[0], e[1])):
        body += vlq(tick - previous) + message
        previous = tick
    body += b'\x00\xff\x2f\x00'
    return b'MTrk' + struct.pack('>I', len(body)) + body


def meta(kind, data):
    return b'\xff' + bytes([kind]) + vlq(len(data)) + data


def note_events(notes, channel):
    events = []
    for onset, stop, pitch, velocity in notes:
        events.append((ticks(onset), 2, bytes([0x90 | (channel - 1), pitch, velocity])))
        events.append((ticks(stop), 1, bytes([0x80 | (channel - 1), pitch, 0])))
    return events


def key_signature_bytes(fifths, minor):
    return struct.pack('>bB', fifths, 1 if minor else 0)


def write_midi(path, case):
    conductor = [(0, 0, meta(0x51, (60_000_000 // case.get('bpm', 100)).to_bytes(3, 'big')))]
    if case.get('write_time_signature', True):
        beats, beat_type = case['meter']
        conductor.append((0, 0, meta(0x58, bytes([beats, beat_type.bit_length() - 1, 24, 8]))))
    for quarter, fifths, minor in case.get('key_metas', []):
        conductor.append((ticks(quarter), 0, meta(0x59, key_signature_bytes(fifths, minor))))
    melody_track = [(0, 0, meta(0x03, b'Melodia')), (0, 0, bytes([0xC0 | (MELODY['channel'] - 1), MELODY['program']]))]
    for quarter, program in case.get('program_changes', []):
        melody_track.append((ticks(quarter), 0, bytes([0xC0 | (MELODY['channel'] - 1), program])))
    melody_track += note_events(case['played'], MELODY['channel'])
    for quarter, kind, data in case.get('lyric_events', []):
        melody_track.append((ticks(quarter), 0, meta(kind, data)))
    chords_track = [(0, 0, meta(0x03, b'Acordes')), (0, 0, bytes([0xC0 | (CHORDS_CHANNEL - 1), CHORDS_PROGRAM]))]
    chords_track += note_events(case['upper'], CHORDS_CHANNEL)
    bass_track = [(0, 0, meta(0x03, b'Baixo')), (0, 0, bytes([0xC0 | (BASS_CHANNEL - 1), BASS_PROGRAM]))]
    bass_track += note_events(case['bass'], BASS_CHANNEL)
    tracks = [conductor, melody_track, chords_track, bass_track]
    data = b'MThd' + struct.pack('>IHHH', 6, 1, len(tracks), PPQ) + b''.join(track_bytes(t) for t in tracks)
    path.write_bytes(data)
    return data


# ----------------------------------------------------------------------------- casos
def number(value):
    value = F(value)
    return int(value) if value.denominator == 1 else round(float(value), 6)


def base_case(identifier, description, focus, notes, chords, meter=(4, 4), key=(0, False), **extra):
    case = {'id': identifier, 'description': description, 'focus': focus, 'notes': notes, 'chords': chords,
            'meter': meter, 'key': key, 'key_changes': [], 'pickup': F(0), 'lyrics': []}
    case.update(extra)
    bar = F(meter[0] * 4, meter[1])
    if 'upper' not in case:
        hit = F(3, 2) if meter[1] == 8 and meter[0] % 3 == 0 else F(2) if meter[0] == 4 else bar
        case['upper'], case['bass'] = accompaniment(chords, bar, hit, start=case.get('accompaniment_start', 0),
                                                    pedal=case.get('pedal'))
    if 'played' not in case:
        case['played'] = perform(notes)
    return case


TUNE_A = ('E4:1 D4:1/2 E4:1/2 G4:1 E4:1 | A4:1 G4:1/2 E4:1/2 C4:2 | F4:1 A4:1 C5:1 A4:1 | G4:3/2 F4:1/2 D4:2 | '
          'E4:1 G4:1 C5:3/2 B4:1/2 | A4:1 E4:1 A4:2 | F4:1 D4:1 G4:1/2 A4:1/2 B4:1 | C5:3 r:1')
PROGRESSION_A = [('C', 4), ('Am', 4), ('F', 4), ('G', 4), ('C', 4), ('Am', 4), ('Dm', 2), ('G7', 2), ('C', 4)]


def tune(text, start=0):
    return melody(text.replace('|', ' '), start)


def cases():
    result = []

    # 1. Melodia reta em 4/4 com acompanhamento em blocos: referência do que já deve funcionar.
    result.append(base_case('reta_44_blocos', 'Melodia simples em 4/4 com acordes em blocos e baixo.',
                            ['onsets', 'altura', 'duracao', 'cifra', 'tonalidade'],
                            tune(TUNE_A), progression(PROGRESSION_A)))

    # 2. Swing: colcheias tocadas em 2/3 do tempo; a escrita esperada é de colcheias retas.
    notes = tune('C4:1/2 D4:1/2 E4:1/2 G4:1/2 A4:1 G4:1 | E4:1/2 G4:1/2 A4:1/2 C5:1/2 A4:2 | '
                 'G4:1/2 A4:1/2 G4:1/2 E4:1/2 D4:1 C4:1 | D4:1/2 E4:1/2 D4:1/2 C4:1/2 A3:2 | '
                 'C4:1/2 D4:1/2 E4:1/2 G4:1/2 A4:1 G4:1 | C5:1/2 A4:1/2 G4:1/2 E4:1/2 G4:2 | '
                 'A4:1/2 G4:1/2 E4:1/2 D4:1/2 E4:1 D4:1 | C4:3 r:1')
    result.append(base_case('swing', 'Colcheias em swing (contratempo em 2/3 do tempo), escritas retas.',
                            ['onsets_exatos', 'duracao'], notes,
                            progression([('C', 4), ('Am7', 4), ('Dm7', 2), ('G7', 2), ('Am7', 4), ('C', 4),
                                         ('Am7', 4), ('Dm7', 2), ('G7', 2), ('C', 4)]),
                            played=perform(notes, swing=True), bpm=120))

    # 3. Tercinas de colcheia completas.
    t = '1/3'
    notes = tune(f'C4:{t} D4:{t} E4:{t} F4:1 G4:{t} A4:{t} G4:{t} E4:1 | F4:{t} G4:{t} A4:{t} C5:1 A4:2 | '
                 f'G4:{t} F4:{t} E4:{t} D4:1 E4:{t} F4:{t} E4:{t} D4:1 | C4:{t} D4:{t} E4:{t} G4:1 C5:2 | '
                 f'B4:{t} A4:{t} G4:{t} A4:1 F4:2 | E4:{t} F4:{t} G4:{t} C4:1 E4:2 | '
                 f'D4:{t} E4:{t} F4:{t} B4:1 G4:2 | C5:3 r:1')
    result.append(base_case('tercinas_colcheia', 'Tercinas de colcheia completas (três ataques por tempo).',
                            ['onsets_exatos', 'duracao'], notes,
                            progression([('C', 4), ('F', 4), ('G', 4), ('C', 4), ('F', 4), ('C', 4), ('G7', 4), ('C', 4)])))

    # 4. Tercinas parciais: semínima + colcheia de tercina, pausa + duas colcheias de tercina.
    notes = tune('C4:2/3 D4:1/3 E4:1 G4:2/3 E4:1/3 C4:1 | r:1/3 D4:1/3 E4:1/3 F4:1 A4:2 | '
                 'G4:2/3 F4:1/3 E4:1 D4:2/3 E4:1/3 F4:1 | E4:2 r:1/3 E4:1/3 G4:1/3 C5:1 | '
                 'A4:2/3 G4:1/3 F4:1 A4:2/3 G4:1/3 F4:1 | E4:1 r:1/3 C4:1/3 E4:1/3 G4:2 | '
                 'F4:2/3 E4:1/3 D4:1 B3:2/3 C4:1/3 D4:1 | C4:3 r:1')
    result.append(base_case('tercinas_parciais', 'Tercinas incompletas: ataques só em 0 e 2/3, ou em 1/3 e 2/3 do tempo.',
                            ['onsets_exatos', 'duracao'], notes,
                            progression([('C', 4), ('F', 4), ('G', 4), ('C', 4), ('F', 4), ('C', 4), ('G7', 4), ('C', 4)])))

    # 5. Tercinas de semínima: três notas em dois tempos.
    s = '2/3'
    notes = tune(f'C4:{s} D4:{s} E4:{s} G4:2 | A4:{s} G4:{s} E4:{s} C4:2 | F4:{s} G4:{s} A4:{s} C5:2 | '
                 f'B4:{s} A4:{s} G4:{s} D4:2 | E4:{s} F4:{s} G4:{s} E4:1 C4:1 | A4:{s} G4:{s} F4:{s} A4:2 | '
                 f'G4:{s} F4:{s} D4:{s} B3:1 D4:1 | C4:3 r:1')
    result.append(base_case('tercinas_seminima', 'Tercinas de semínima (três notas em dois tempos).',
                            ['onsets_exatos', 'duracao'], notes,
                            progression([('C', 4), ('Am', 4), ('F', 4), ('G', 4), ('C', 4), ('F', 4), ('G7', 4), ('C', 4)])))

    # 6. Voz interna na mesma pista: notas graves que atacam enquanto a melodia sustenta.
    notes = tune(TUNE_A)
    # (início, duração, nota): duas soam junto com a melodia; as demais atacam durante notas longas dela.
    inner = [(0, 1, 'C4'), (7, 1, 'G3'), (8, 2, 'C4'), (13, F(1, 2), 'D4'), (15, 1, 'B3'),
             (F(37, 2), 1, 'E4'), (23, 1, 'C4'), (29, 1, 'E4'), (30, 1, 'G4')]
    played = perform(notes) + [(F(q), F(q) + F(d) * F(9, 10), parse_name(n)[0], 70) for q, d, n in inner]
    result.append(base_case('voz_interna', 'Voz interna na pista da melodia, atacando enquanto a voz principal sustenta.',
                            ['onsets', 'altura'], notes, progression(PROGRESSION_A), played=played))

    # 7. 3/4 sem meta de compasso: a métrica deve vir do padrão (baixo no 1, acorde no 2 e 3).
    notes = tune('E4:2 G4:1 | C5:3 | B4:2 A4:1 | G4:3 | A4:2 B4:1 | C5:1 B4:1 A4:1 | G4:2 E4:1 | D4:3 | '
                 'E4:2 G4:1 | C5:2 E5:1 | D5:2 C5:1 | B4:3 | A4:1 B4:1 C5:1 | D5:2 B4:1 | C5:3 | r:3')
    chords = progression([('C', 3), ('C', 3), ('G', 3), ('G', 3), ('F', 3), ('F', 3), ('C', 3), ('G', 3),
                          ('C', 3), ('C', 3), ('G', 3), ('G', 3), ('F', 3), ('G', 3), ('C', 3), ('C', 3)])
    upper, bass = waltz(chords)
    result.append(base_case('compasso_34_sem_meta', 'Valsa em 3/4 sem meta de fórmula de compasso no MIDI.',
                            ['compasso', 'onsets'], notes, chords, meter=(3, 4), write_time_signature=False,
                            upper=upper, bass=bass))

    # 8. 6/8 com meta: colcheias em grupos de três.
    notes = tune('C4:1/2 D4:1/2 E4:1/2 F4:1 E4:1/2 | G4:3/2 E4:3/2 | A4:1/2 G4:1/2 F4:1/2 E4:1 D4:1/2 | E4:3/2 C4:3/2 | '
                 'D4:1/2 E4:1/2 F4:1/2 G4:1 A4:1/2 | B4:1 A4:1/2 G4:3/2 | A4:1/2 B4:1/2 C5:1/2 D5:1 B4:1/2 | C5:3/2 r:3/2')
    result.append(base_case('compasso_68', 'Compasso 6/8 com meta, colcheias agrupadas de três em três.',
                            ['compasso', 'onsets', 'duracao'], notes,
                            progression([('C', 3), ('C', 3), ('F', 3), ('C', 3), ('G', 3), ('G', 3), ('G7', 3), ('C', 3)]),
                            meter=(6, 8)))

    # 9. Anacruse de um tempo: o primeiro compasso do MIDI só tem o último tempo.
    notes = tune('G4:1 | ' + TUNE_A, start=3)
    chords = progression(PROGRESSION_A, start=4)
    result.append(base_case('anacruse', 'Anacruse de um tempo antes do primeiro compasso completo.',
                            ['anacruse', 'onsets', 'compasso'], notes, chords, pickup=F(1), accompaniment_start=4))

    # 10. Troca de programa no meio da pista da melodia (piano -> flauta no compasso 9).
    notes = tune(TUNE_A) + tune(TUNE_A, 32)
    result.append(base_case('troca_programa', 'A pista da melodia troca de instrumento (programa 0 -> 73) no compasso 9; '
                            'a seleção traz só o primeiro instrumento, como na lista de pistas.',
                            ['onsets', 'cobertura'], notes, progression(PROGRESSION_A + PROGRESSION_A),
                            program_changes=[(32, 73)]))

    # 11-12. Modulação de meio tom no último refrão (Dó -> Réb), com e sem meta de armadura.
    verse = 'C4:1 E4:1 G4:1 E4:1 | F4:1 A4:1 C5:2 | B4:1 A4:1 G4:1 F4:1 | E4:3 r:1 | C4:1 E4:1 G4:1 C5:1 | A4:1 F4:1 A4:2 | G4:1 F4:1 D4:1 B3:1 | C4:3 r:1'
    chorus = 'G4:1 A4:1 G4:1 E4:1 | A4:2 C5:2 | D5:1 C5:1 A4:1 F4:1 | G4:3 r:1 | E4:1 G4:1 C5:1 G4:1 | A4:1 C5:1 F4:2 | D4:1 E4:1 F4:1 B4:1 | C5:3 r:1'
    verse_chords = [('C', 4), ('F', 4), ('G', 4), ('C', 4), ('C', 4), ('F', 4), ('G7', 4), ('C', 4)]
    chorus_chords = [('C', 4), ('F', 4), ('Dm', 4), ('G', 4), ('C', 4), ('F', 4), ('G7', 4), ('C', 4)]
    first = tune(verse) + tune(chorus, 32)
    last_c = tune(chorus, 64)
    moved = []
    for note in last_c:
        name = transpose_name(name_of(note['step'], note['alter'], note['octave']), 1, 1)
        pitch, step, alter, octave = parse_name(name)
        moved.append(dict(note, pitch=pitch, step=step, alter=alter, octave=octave))
    chords = progression(verse_chords + chorus_chords) + [transpose_chord(c, 1, 1) for c in progression(chorus_chords, 64)]
    for identifier, metas, label in (('modulacao_meio_tom_meta', [(0, 0, False), (64, -5, False)], 'com'),
                                     ('modulacao_meio_tom_sem_meta', [], 'sem')):
        result.append(base_case(identifier, f'Último refrão meio tom acima (Dó -> Réb), {label} meta 89 de armadura.',
                                ['tonalidade', 'modulacao', 'grafia', 'cifra'], first + moved, chords,
                                key_changes=[(F(64), -5, False)], key_metas=metas))

    # 13. Dominantes secundárias com cromatismo na melodia: G# sobre E7, C# sobre A7, F# sobre D7.
    notes = tune('E4:1 G4:1 C5:2 | B4:1 G#4:1 E4:1 G#4:1 | A4:1 C5:1 E5:2 | E4:1 C#5:1 A4:1 G4:1 | '
                 'F4:1 A4:1 D5:2 | F#4:1 A4:1 C5:1 D5:1 | B4:1 G4:1 F4:1 D4:1 | C4:3 r:1')
    result.append(base_case('dominantes_secundarias', 'Dominantes secundárias em Dó com notas cromáticas na melodia.',
                            ['grafia', 'cifra'], notes,
                            progression([('C', 4), ('E7', 4), ('Am', 4), ('A7', 4), ('Dm', 4), ('D7', 4), ('G7', 4), ('C', 4)])))

    # 14. Voicings sem quinta: C7 = C-E-Bb, Cmaj7 = C-E-B, G7 = G-B-F.
    chords = progression([('C7', 4, (48, 52, 58)), ('F', 4), ('Cmaj7', 4, (48, 52, 59)), ('Am', 4),
                          ('Dm7', 4, (50, 53, 60)), ('G7', 4, (55, 59, 65)), ('Cmaj7', 4, (48, 52, 59)), ('C', 4)])
    result.append(base_case('acordes_sem_quinta', 'Tétrades tocadas sem a quinta (C7, Cmaj7, Dm7 e G7).',
                            ['cifra'], tune(TUNE_A), chords))

    # 15. Baixo invertido sustentado: G/B, Am/G, C/E, D/F#.
    chords = progression([('C', 4), ('G/B', 4), ('Am', 4), ('Am/G', 4), ('F', 4), ('C/E', 4), ('D/F#', 4), ('G', 4)])
    notes = tune('E4:1 G4:1 C5:2 | D5:1 B4:1 G4:2 | C5:1 A4:1 E4:2 | E4:1 G4:1 A4:2 | '
                 'F4:1 A4:1 C5:2 | G4:1 E4:1 C4:2 | D4:1 F#4:1 A4:2 | G4:3 r:1')
    result.append(base_case('baixo_invertido', 'Cifras com baixo (G/B, Am/G, C/E, D/F#) e baixo sustentado.',
                            ['cifra_baixo', 'cifra'], notes, chords))

    # 16. Pedal no baixo: Dó sustentado sob F/C e G/C.
    chords = progression([('C', 4), ('F/C', 4), ('G/C', 4), ('C', 4), ('Dm/C', 4), ('G/C', 4), ('F/C', 4), ('C', 4)])
    notes = tune('E4:1 G4:1 C5:2 | C5:1 A4:1 F4:2 | D4:1 G4:1 B4:2 | C5:3 r:1 | '
                 'D4:1 F4:1 A4:2 | B4:1 G4:1 D4:2 | F4:1 A4:1 C5:2 | C5:3 r:1')
    result.append(base_case('pedal_baixo', 'Pedal de Dó no baixo sob acordes que mudam (F/C, G/C, Dm/C).',
                            ['cifra_baixo', 'cifra'], notes, chords, pedal=36))

    # 17. Acordes 13, 7(b9) e 6/9.
    chords = progression([('C69', 4, (52, 57, 62, 67)), ('A7b9', 4, (49, 55, 58, 64)), ('Dm7', 4), ('G13', 4, (53, 59, 64, 69)),
                          ('C69', 4, (52, 57, 62, 67)), ('E7b9', 4, (56, 62, 65, 71)), ('Am7', 4), ('G13', 4, (53, 59, 64, 69))])
    notes = tune('E5:1 D5:1 A4:2 | G4:1 E4:1 C#5:2 | D5:1 C5:1 A4:2 | B4:1 A4:1 E5:2 | '
                 'D5:1 E5:1 G5:2 | G#4:1 B4:1 D5:2 | C5:1 E5:1 A4:2 | B4:3 r:1')
    result.append(base_case('acordes_estendidos', 'Acordes 6/9, 7(b9) e 13 em blocos.',
                            ['cifra', 'grafia'], notes, chords))

    # 18. Karaokê com sílabas (meta 5, hífen entre sílabas).
    notes = tune(TUNE_A)
    text = 'Eu vou can-tar a can-ção do mar e a lu-a vai bri-lhar no céu de pra-ta quan-do a noi-te vem'
    placed = lay_lyrics(notes, text)
    result.append(base_case('karaoke_silabas', 'Letra em eventos de sílaba (meta 5) com hífen.',
                            ['letra'], notes, progression(PROGRESSION_A), lyrics=placed,
                            lyric_events=lyric_meta_events(placed)))

    # 19. Karaokê palavra por palavra (meta 1 com '/' e '\'): a sílaba de cada nota precisa ser separada.
    text = 'Eu vou can-tar a can-ção do mar e a lu-a vai bri-lhar no céu de pra-ta quan-do a noi-te vem'
    placed = lay_lyrics(notes, text)
    result.append(base_case('karaoke_palavras', 'Karaokê .kar com uma palavra por evento de texto (meta 1, "/" e "\\").',
                            ['letra', 'silabacao'], notes, progression(PROGRESSION_A), lyrics=placed,
                            lyric_events=karaoke_word_events(placed, {'lines': (8,), 'paragraphs': (16,)})))

    # 20. Fala antes da entrada da voz: palavras faladas na introdução instrumental.
    notes = tune(TUNE_A, start=8)
    text = 'Eu vou can-tar a can-ção do mar e a lu-a vai bri-lhar no céu de pra-ta quan-do a noi-te vem'
    placed = lay_lyrics(notes, text)
    spoken = [{'onset': F(q), 'text': s, 'word_start': w, 'spoken': True} for q, s, w in
              ((F(1), 'A', True), (F(3, 2), 'lô', False), (F(2), 'gen', True), (F(5, 2), 'te', False),
               (F(4), 'bo', True), (F(9, 2), 'ni', False), (F(5), 'ta', False))]
    chords = progression([('C', 4), ('G7', 4)] + PROGRESSION_A)
    everything = spoken + placed
    result.append(base_case('fala_introducao', 'Palavras faladas na introdução, antes da entrada da voz.',
                            ['letra', 'fala'], notes, chords, lyrics=everything,
                            lyric_events=lyric_meta_events(everything)))

    # 21. Letra com deslocamento global de ~1/8 (atrasada 0,45 semínima em relação às notas).
    notes = tune('E4:1/2 D4:1/2 E4:1/2 G4:1/2 E4:1 r:1 | A4:1/2 G4:1/2 E4:1/2 C4:1/2 D4:2 | '
                 'F4:1/2 A4:1/2 C5:1/2 A4:1/2 G4:2 | G4:1/2 F4:1/2 E4:1/2 D4:1/2 C4:2 | '
                 'E4:1/2 G4:1/2 C5:1/2 B4:1/2 A4:2 | A4:1/2 E4:1/2 A4:1/2 C5:1/2 B4:2 | '
                 'F4:1/2 D4:1/2 G4:1/2 A4:1/2 B4:2 | C5:3 r:1')
    text = ('Ma-ri-a vem ver a lu-a no-va so-bre o mar a-zul do nor-te on-de a gen-te vai can-tar '
            'tu-do de no-vo a-té o sol')
    placed = lay_lyrics(notes, text)
    result.append(base_case('letra_deslocada', 'Sílabas atrasadas cerca de 1/8 (0,45 semínima) em relação às notas.',
                            ['letra', 'alinhamento'], notes, progression(PROGRESSION_A), lyrics=placed,
                            lyric_events=lyric_meta_events(placed, delay=F(45, 100))))

    # 22. Duas sílabas na mesma nota (elisão): 'que_eu', 'de_a'.
    notes = tune(TUNE_A)
    text = 'Eu sei que_eu vou te a-mar de_a-go-ra_em di-an-te por to-da_a vi-da ca-da di-a'
    placed = lay_lyrics(notes, text)
    result.append(base_case('duas_silabas_mesma_nota', 'Duas sílabas cantadas na mesma nota (elisão).',
                            ['letra', 'elisao'], notes, progression(PROGRESSION_A), lyrics=placed,
                            lyric_events=lyric_meta_events(placed)))
    return result


def ground_truth(case, midi_name, digest):
    beats, beat_type = case['meter']
    return {
        'schema': SCHEMA, 'id': case['id'], 'description': case['description'], 'focus': case['focus'],
        'midi': midi_name, 'midi_sha256': digest, 'ppq': PPQ,
        'melody_sources': [dict(MELODY)],
        'meter': {'beats': beats, 'beat_type': beat_type}, 'pickup_quarters': number(case['pickup']),
        'bar_quarters': number(F(beats * 4, beat_type)),
        'key': {'fifths': case['key'][0], 'minor': case['key'][1]},
        'key_changes': [{'quarter': number(q), 'fifths': f, 'minor': m} for q, f, m in case['key_changes']],
        'melody': [{'onset': number(n['onset']), 'duration': number(n['duration']), 'pitch': n['pitch'],
                    'step': n['step'], 'alter': n['alter'], 'octave': n['octave']} for n in case['notes']],
        'chords': [{'onset': number(c['onset']), 'end': number(c['end']), 'symbol': c['symbol'], 'root': c['root'],
                    'root_pc': c['root_pc'], 'quality': c['quality'], 'bass': c['bass'], 'bass_pc': c['bass_pc']}
                   for c in case['chords']],
        'lyrics': [{'onset': number(item['onset']), 'text': item['text'], 'word_start': item['word_start'],
                    'spoken': item['spoken']} for item in case['lyrics']],
    }


def create(destination, only=None):
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    manifest = {'schema': SCHEMA, 'origin': 'tests/accuracy/make_corpus.py', 'personal_files': False, 'cases': {}}
    for case in cases():
        if only and case['id'] not in only:
            continue
        midi = destination / (case['id'] + '.mid')
        data = write_midi(midi, case)
        digest = hashlib.sha256(data).hexdigest()
        truth = ground_truth(case, midi.name, digest)
        text = json.dumps(truth, ensure_ascii=False, indent=1)
        (destination / (case['id'] + '.json')).write_text(text, encoding='utf-8')
        manifest['cases'][case['id']] = {'midi_sha256': digest,
                                         'truth_sha256': hashlib.sha256(text.encode('utf-8')).hexdigest()}
    (destination / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding='utf-8')
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--case', action='append', help='gera só este caso (pode repetir)')
    arguments = parser.parse_args()
    result = create(arguments.destination.resolve(), set(arguments.case or []))
    print(json.dumps({'cases': len(result['cases']), 'destination': str(arguments.destination.resolve())}, ensure_ascii=False))
