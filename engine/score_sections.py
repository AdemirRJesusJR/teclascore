"""Section marks: where the parts of the song start, found only in what the score already contains.

Two kinds of evidence are used, nothing is guessed from style or title:
- the lyrics: in a song with words, three or more bars without any sung syllable are an instrumental part
  ("Intro" at the start, "Final" at the end, "Instrumental" in the middle; two bars are enough at the edges);
- repetition: a passage of 4 or more bars that comes back later (same chords beat by beat and nearly the same
  melody) starts and ends a section. Passages with the same music get the same letter (A, B, C…). When the
  most repeated passage also repeats its words, it is the chorus ("Refrão").

A song without repetition or instrumental parts gets no marks. The marks only label the written score: notes,
chords and bars never change. Each mark starts a new system, so the systems follow the song's parts.
"""
import re
from collections import Counter

MIN_SECTION = 4      # bars of the shortest section
MIN_RUN = 8          # bars of the shortest repeated passage that makes sections (4 in short songs)
MAX_LETTERS = 5      # more different parts than this is not a structure a reader can use
UNSUNG_MIDDLE = 3    # bars without words inside the song that make an instrumental part
UNSUNG_EDGE = 2      # the same at the start or end of the song
MATCH = .7           # bar similarity (chords and melody) that counts as the same music
LYRIC_SONG = 16      # sung syllables needed to use the words at all


def _words(text):
    return [w for w in re.findall(r'\w+', (text or '').lower())]


def _bar_profiles(bars, notes, chords, div, sung_ticks=None):
    """Per bar: the chord sounding on each beat, the melody attacks (offset, pitch) and the sung words."""
    ordered = sorted((c for c in chords if not c.get('reminder')), key=lambda c: c['tick'])
    profiles = []
    index = 0
    active = None
    for bar in bars:
        beats = []
        for tick in range(bar['start'], bar['end'], div):
            while index < len(ordered) and ordered[index]['tick'] <= tick:
                active = (ordered[index]['root'], ordered[index]['quality'])
                index += 1
            beats.append(active)
        # Chord changes inside a beat also count (two chords on one beat).
        inner = tuple((c['tick'] - bar['start'], c['root'], c['quality']) for c in ordered
                      if bar['start'] <= c['tick'] < bar['end'] and (c['tick'] - bar['start']) % div)
        melody = {(round((n['start'] - bar['start']) * 8 / div), n['pitch'])
                  for n in notes if bar['start'] <= n['start'] < bar['end'] and not n.get('slash')}
        sung = [n for n in notes if bar['start'] <= n['start'] < bar['end'] and n.get('lyric')]
        words = []
        for n in sung: words.extend(_words(n['lyric'].get('text')))
        heard = sorted(t - bar['start'] for t in (sung_ticks or ()) if bar['start'] <= t < bar['end'])
        profiles.append({'length': bar['end'] - bar['start'], 'beats': tuple(beats), 'inner': inner,
                         'melody': melody, 'words': words, 'sung': bool(words or heard),
                         'first_syllable': min([n['start'] - bar['start'] for n in sung] + heard, default=None)})
    return profiles


def _similarity(a, b):
    if a['length'] != b['length']: return 0.
    chords = sum(x == y for x, y in zip(a['beats'], b['beats'])) / max(1, len(a['beats']))
    if a['inner'] != b['inner']: chords *= .75
    if not a['melody'] and not b['melody']: melody = 1.
    elif not a['melody'] or not b['melody']: melody = 0.
    else:
        # A sung line repeated with other words keeps its notes but shifts some attacks: compare the pitches
        # (as a multiset) and the attacks on an eighth-note grid, not exact positions.
        pa, pb = Counter(p for _, p in a['melody']), Counter(p for _, p in b['melody'])
        pitches = 2 * sum((pa & pb).values()) / (sum(pa.values()) + sum(pb.values()))
        oa, ob = {o // 4 for o, _ in a['melody']}, {o // 4 for o, _ in b['melody']}
        melody = .5 * pitches + .5 * len(oa & ob) / len(oa | ob)
    return .4 * chords + .6 * melody


def _unsung_blocks(profiles, n):
    """(start, end) runs of bars without words, long enough to be a part of their own."""
    sung = [p['sung'] for p in profiles]
    blocks = []
    start = None
    for i in range(n + 1):
        if i < n and not sung[i]:
            if start is None: start = i
            continue
        if start is not None:
            edge = start == 0 or i == n
            if i - start >= (UNSUNG_EDGE if edge else UNSUNG_MIDDLE): blocks.append([start, i])
            start = None
    # A phrase that starts in the second half of the bar before the words (a pickup) belongs to the next part:
    # the mark goes on the downbeat after it.
    for block in blocks:
        end = block[1]
        if end + 1 < n and profiles[end]['first_syllable'] is not None and sung[end + 1]:
            if profiles[end]['first_syllable'] * 2 >= profiles[end]['length']: block[1] = end + 1
    return [tuple(b) for b in blocks]


def detect_sections(bars, notes, chords, div=960, sung_ticks=None):
    """[{'bar': index, 'label': text}] for the start of each part, or [] when the song shows no clear parts.

    sung_ticks: when each syllable of the MIDI lyrics is sung (score ticks), including the ones that are not
    under a melody note; without it, the words written under the notes are used."""
    n = len(bars)
    if n < 2 * MIN_SECTION: return []
    profiles = _bar_profiles(bars, notes, chords, div, sung_ticks)
    lyric_song = max(len(sung_ticks or ()), sum(1 for note in notes if note.get('lyric'))) >= LYRIC_SONG
    unsung = _unsung_blocks(profiles, n) if lyric_song else []
    inside_unsung = [any(a <= i < b for a, b in unsung) for i in range(n)]

    same = {}
    def match(i, j):
        key = (i, j)
        if key not in same: same[key] = _similarity(profiles[i], profiles[j]) >= MATCH
        return same[key]

    # Maximal repeated runs: bars i.. and j.. match for k bars without overlapping.
    min_run = MIN_RUN if n >= 48 else MIN_SECTION
    runs = []
    for i in range(n):
        if inside_unsung[i]: continue
        for j in range(i + MIN_SECTION, n):
            if inside_unsung[j] or (i and j and match(i - 1, j - 1) and not inside_unsung[i - 1]): continue
            k = 0
            while j + k < n and i + k < j and not inside_unsung[i + k] and not inside_unsung[j + k] and match(i + k, j + k):
                k += 1
            if k >= min_run: runs.append((k, i, j))
    boundaries = {0, n}
    for a, b in unsung: boundaries.update((a, b))
    # Longest repetitions first; a boundary closer than a section to another one is left out
    # (the song's first and last bars only need two bars of distance).
    for k, i, j in sorted(runs, key=lambda run: (-run[0], run[1], run[2])):
        for b in (i, i + k, j, j + k):
            if b in boundaries or b < UNSUNG_EDGE or n - b < UNSUNG_EDGE: continue
            if all(abs(b - e) >= MIN_SECTION for e in boundaries if e not in (0, n)):
                boundaries.add(b)
    edges = sorted(boundaries)
    segments = [(a, b) for a, b in zip(edges, edges[1:]) if b > a]
    if len(segments) < 2: return []

    def segment_kind(a, b):
        return 'unsung' if all(inside_unsung[i] for i in range(a, b)) else 'music'

    def same_music(s, t):
        # Parts of nearly the same length (a bar or two of pickup or tail) are compared over the shorter one.
        if abs((s[1] - s[0]) - (t[1] - t[0])) > 2: return False
        values = [_similarity(profiles[s[0] + x], profiles[t[0] + x]) for x in range(min(s[1] - s[0], t[1] - t[0]))]
        return sum(values) / len(values) >= MATCH

    groups = []   # [segment indices]
    group_of = {}
    for index, (a, b) in enumerate(segments):
        if segment_kind(a, b) == 'unsung': continue
        for number, group in enumerate(groups):
            if same_music(segments[group[0]], (a, b)):
                group.append(index); group_of[index] = number; break
        else:
            group_of[index] = len(groups); groups.append([index])

    chorus = None
    if lyric_song:
        candidates = []
        for number, group in enumerate(groups):
            if len(group) < 2: continue
            first, second = (set(w for i in range(*segments[g]) for w in profiles[i]['words']) for g in group[:2])
            if first and second and len(first & second) / len(first | second) >= .6:
                candidates.append((len(group), segments[group[0]][1] - segments[group[0]][0], -number, number))
        if candidates: chorus = max(candidates)[3]

    letters = {}
    labels = []
    for index, (a, b) in enumerate(segments):
        if index not in group_of:
            label = 'Intro' if a == 0 else ('Final' if b == n else 'Instrumental')
        elif group_of[index] == chorus:
            label = 'Refrão'
        else:
            number = group_of[index]
            if number not in letters: letters[number] = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'[min(len(letters), 25)]
            label = letters[number]
        if labels and labels[-1]['label'] == label:
            labels[-1]['bars'] += b - a  # the same part twice in a row keeps one mark
        else:
            labels.append({'bar': a, 'label': label, 'bars': b - a})
    if len(letters) > MAX_LETTERS:
        # Too fragmented to help: keep only the instrumental parts the words show.
        labels = [item for item in labels if item['label'] in ('Intro', 'Instrumental', 'Final')]
    if len({item['label'] for item in labels}) < 2: return []
    return labels
