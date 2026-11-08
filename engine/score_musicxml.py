"""MusicXML writer: measures, note and rest pieces, chord symbols with their diagram images, lyrics and system layout.
Moved from batch_score.py; engine helpers are reached through ``core`` so tests and callers that replace batch_score
functions keep working."""
from pathlib import Path
from collections import defaultdict, Counter
from bisect import bisect_right
from fractions import Fraction
from statistics import median
import xml.etree.ElementTree as E
import ast, struct, math, json, zipfile, subprocess, hashlib, re, copy, os, tempfile, textwrap, shutil
import batch_svg_qa as svg_geometry
import compact_score_layout
import diagram_lanes
import batch_score as core
import melody_rhythm


def make_bars(parsed,meters):
    ppq=parsed['ppq'];pitched=[n for n in parsed['notes'] if n['channel']!=10 and 20<=n['pitch']<=108 and n['program']<112]
    if not pitched:raise core.NeedsReview('Sem notas melódicas válidas.')
    initial=meters[0];barq=initial['numerator']*4/initial['denominator']
    origin=math.floor(min(n['start'] for n in pitched)/ppq/barq)*round(barq*core.DIV)
    pickup=_pickup_origin(pitched,ppq,barq,origin)
    last=math.ceil(max(n['end'] for n in pitched)/ppq*core.DIV)
    changes=sorted({round(m['tick']/ppq*core.DIV):(m['numerator'],m['denominator']) for m in meters}.items())
    current=(initial['numerator'],initial['denominator']);bars=[];start=origin
    while start<last:
        for t,m in changes:
            if t<=start:current=m
        size=round(current[0]*4/current[1]*core.DIV)
        nextchange=min([t for t,m in changes if t>start] or [last+size])
        stop=min(start+size,nextchange)
        if stop<=start:raise core.NeedsReview('Mudança métrica inválida.')
        bars.append({'start':start-origin,'end':stop-origin,'meter':current,'partial':stop-start!=size})
        start=stop
    if len(bars)>700:raise core.NeedsReview('Duração excessiva ou nota sem término; requer verificação.')
    if pickup is not None and bars and bars[0]['end']>pickup-origin:
        # Anacruse: o primeiro compasso começa no tempo da primeira nota e é incompleto (implicit no MusicXML).
        shift=pickup-origin
        bars[0]=dict(bars[0],start=0,end=bars[0]['end']-shift,partial=True,pickup=True)
        for bar in bars[1:]:bar['start']-=shift;bar['end']-=shift
        origin=pickup
    return origin,start,bars

def _pickup_origin(pitched,ppq,barq,origin):
    """Início do compasso de anacruse, ou None.

    Só a melodia (uma única pista e canal) soa antes da primeira barra cheia, a partir da metade do compasso, e
    outra fonte entra no compasso seguinte. O início fica no tempo (semínima) da primeira nota."""
    first=min(n['start'] for n in pitched)/ppq;bar_start=origin/core.DIV;line=bar_start+barq
    if first<=bar_start+1e-9 or first-bar_start<barq/2-1e-9:return None
    before=[n for n in pitched if n['start']/ppq<line-1e-9]
    if len({(n['track'],n['channel']) for n in before})!=1:return None
    source=(before[0]['track'],before[0]['channel'])
    entering=[n for n in pitched if line-1e-9<=n['start']/ppq<line+barq and (n['track'],n['channel'])!=source]
    if not entering:return None
    start=math.floor(first+1e-9)*core.DIV
    return start if origin<start<round(line*core.DIV) else None

TYPES={3840:('whole',0),2880:('half',1),1920:('half',0),1440:('quarter',1),960:('quarter',0),720:('eighth',1),480:('eighth',0),360:('16th',1),240:('16th',0),120:('32nd',0),60:('64th',0),30:('128th',0)}

def metric_structure(bar):
    """(tamanho do compasso cheio, unidade de tempo, limites dos grupos) em unidades DIV, pelo início métrico.

    4/4: tempos de semínima, metades do compasso como grupos. x/8 composto: tempo de semínima pontuada. 5/8 e 7/8:
    colcheias em grupos 3+2 e 2+2+3. Os demais compassos formam um único grupo."""
    numerator,denominator=bar['meter'];full=round(numerator*4/denominator*core.DIV)
    if denominator==8 and numerator%3==0 and numerator>=6:
        beat=core.DIV*3//2;bounds=list(range(0,full+1,2*beat)) if numerator==12 else [0,full]
    elif denominator==8:
        beat=core.DIV//2
        groups={5:(3,2),7:(2,2,3)}.get(numerator,(numerator,))
        bounds=[0]
        for size in groups:bounds.append(bounds[-1]+size*beat)
    else:
        beat=core.DIV*4//denominator
        bounds=[0,full//2,full] if numerator==4 else [0,full]
    return full,beat,bounds

def _metric_allowed(rel,d,full,beat,bounds):
    """Uma figura de duração d começando em rel cabe sem ligadura pela estrutura do compasso."""
    if rel+d>full:return False
    # No início do compasso qualquer figura serve (mínima pontuada no 4/4), exceto nos compassos de grupos
    # desiguais (5/8, 7/8), em que a figura não atravessa o primeiro grupo.
    sizes={b-a for a,b in zip(bounds,bounds[1:])}
    if rel==0 and len(sizes)==1:return True
    group_end=min(b for b in bounds if b>rel)
    if rel+d>group_end:return False
    if rel//beat==(rel+d-1)//beat or rel%beat==0:return True
    # Síncope colcheia-semínima-colcheia (compassos simples): a semínima no contratempo fica sem ligadura.
    return beat==core.DIV and d==core.DIV and rel%beat==beat//2

def pieces(start,end,grids,bar=None):
    """Figuras (início, duração, tercina) que escrevem [start, end). Com o compasso (bar), a divisão segue a
    estrutura métrica: semínima pontuada no x/8 composto, metade do compasso no 4/4 e síncope sem ligadura.
    tercina é True (tercina de colcheias num tempo) ou ('pair', início) para a tercina de semínimas em dois tempos."""
    t=start
    if bar is not None:
        full,beat_unit,bounds=metric_structure(bar)
        anchor=bar['start']-((full-(bar['end']-bar['start'])) if bar.get('pickup') else 0)
    while t<end:
        beat=t//core.DIV;beatend=(beat+1)*core.DIV;mode=grids.get(beat,4)
        if melody_rhythm.is_pair(mode):
            group=beat*core.DIV if mode==melody_rhythm.PAIR_FIRST else (beat-1)*core.DIV
            room=min(end,group+2*core.DIV)-t
            d=max([x for x in (1280,640,320,160,80) if x<=room and (t-group)%x==0] or
                  [x for x in (1280,640,320,160,80,40) if x<=room] or [0])
            if not d:raise core.NeedsReview(f'Duração de tercina indefinida no tempo {t/core.DIV:.3f}.')
            yield t,d,('pair',group);t+=d;continue
        if mode%3==0:
            if t%core.DIV==0 and min(end,beatend)-t>=core.DIV:
                # Um tempo inteiro numa grade ternária é uma semínima comum, não três colcheias de tercina ligadas.
                yield t,core.DIV,False;t+=core.DIV;continue
            d=max([x for x in (640,320,160,80,40) if x<=min(end,beatend)-t] or [0])
            if not d:raise core.NeedsReview(f'Duração de tercina indefinida no tempo {t/core.DIV:.3f}.')
            yield t,d,True;t+=d;continue
        triplenext=min([b*core.DIV for b,v in grids.items() if v%3==0 and b*core.DIV>t] or [end])
        limit=min(end,triplenext)
        if bar is None:
            if t%core.DIV:limit=min(limit,beatend)
            candidates=[d for d in core.TYPES if d<=limit-t]
        else:
            candidates=[d for d in core.TYPES if d<=limit-t and _metric_allowed(t-anchor,d,full,beat_unit,bounds)]
        if not candidates:raise core.NeedsReview(f'Duração irregular de {limit-t} unidades.')
        d=max(candidates);yield t,d,False;t+=d

SLASH_PITCH=71  # B4, the middle line of the treble staff

def chord_slashes(bars,chords):
    """One slash per beat (dotted quarter in 6/8, 9/8 and 12/8), also split where a chord changes inside a beat."""
    slashes=[]
    for bar in bars:
        numerator,denominator=bar['meter']
        beat=core.DIV*3//2 if denominator==8 and numerator%3==0 else core.DIV*4//denominator
        bounds={tick for tick in range(bar['start'],bar['end'],beat)}|{c['tick'] for c in chords if bar['start']<c['tick']<bar['end']}
        bounds=sorted(bounds|{bar['end']})
        for start,stop in zip(bounds,bounds[1:]):
            slashes.append({'start':start,'end':stop,'pitch':SLASH_PITCH,'slash':True})
    return slashes

def emit_note(measure,start,dur,triplet,event,ps,full_rest=False):
    if event is not None and event.get('pitches'):
        # A chord of the chord staff: the keyboard-diagram notes stacked on one stem.
        for index,pitch in enumerate(event['pitches']):
            emit_note(measure,start,dur,triplet,{key:value for key,value in event.items() if key!='pitches'}|{'pitch':pitch,'chord':index>0},ps)
        return
    if event is not None and start==event['start'] and not event.get('chord'):
        for pitch in event.get('graces',[]):
            g=core.sub(measure,'note');core.sub(g,'grace',slash='yes');core.pitch_xml(g,pitch,ps);core.sub(g,'voice',1);core.sub(g,'type','eighth')
    n=core.sub(measure,'note')
    if event is not None and event.get('chord'):core.sub(n,'chord')
    if event is None:core.sub(n,'rest',**({'measure':'yes'} if full_rest else {}))
    else:core.pitch_xml(n,event['pitch'],{**ps,event['pitch']%12:tuple(event['spelling'])} if event.get('spelling') else ps)
    core.sub(n,'duration',dur)
    tiein=event is not None and start>event['start'];tieout=event is not None and start+dur<event['end']
    if tiein:core.sub(n,'tie',type='stop')
    if tieout:core.sub(n,'tie',type='start')
    core.sub(n,'voice',1)
    pair=isinstance(triplet,tuple)
    if not full_rest:
        if pair:typ,dots={1280:('half',0),640:('quarter',0),320:('eighth',0),160:('16th',0),80:('32nd',0),40:('64th',0)}[dur]
        else:typ,dots=({640:('quarter',0),320:('eighth',0),160:('16th',0),80:('32nd',0),40:('64th',0)}[dur] if triplet else core.TYPES[dur])
        core.sub(n,'type',typ)
        for _ in range(dots):core.sub(n,'dot')
        if event is not None and event.get('courtesy') and start==event['start'] and not event.get('chord'):
            core.sub(n,'accidental',event['courtesy'],cautionary='yes')
    if triplet:
        # Tercina de semínimas (dois tempos): 3:2 com unidade normal de semínima.
        tm=core.sub(n,'time-modification');core.sub(tm,'actual-notes',3);core.sub(tm,'normal-notes',2);core.sub(tm,'normal-type','quarter' if pair else 'eighth')
    if event is not None and event.get('slash'):core.sub(n,'stem','none');core.sub(n,'notehead','slash')
    if tiein or tieout or triplet:
        nt=core.sub(n,'notations')
        if tiein:core.sub(nt,'tied',type='stop')
        if tieout:core.sub(nt,'tied',type='start')
        if pair:
            if start==triplet[1]:core.sub(nt,'tuplet',type='start',number=1,bracket='yes')
            if start+dur==triplet[1]+2*core.DIV:core.sub(nt,'tuplet',type='stop',number=1)
        else:
            if triplet and start%core.DIV==0:core.sub(nt,'tuplet',type='start',number=1,bracket='no')
            if triplet and (start+dur)%core.DIV==0:core.sub(nt,'tuplet',type='stop',number=1)
    if event is not None and start==event['start'] and event.get('lyric') and not event.get('chord'):
        lyric=core.sub(n,'lyric',number='1');core.sub(lyric,'syllabic',event['lyric']['syllabic']);core.sub(lyric,'text',event['lyric']['text'])
        # A word's last syllable held over the following notes (melisma): extender line up to the last of them.
        if event['lyric'].get('extend'):core.sub(lyric,'extend',type='start')
    elif event is not None and start==event['start'] and event.get('lyric_extend_stop') and not event.get('chord'):
        core.sub(core.sub(n,'lyric',number='1'),'extend',type='stop')
    if event is not None and start==event['start'] and event.get('lyric2'):
        # Second verse of a repeated passage, under the first.
        lyric=core.sub(n,'lyric',number='2');core.sub(lyric,'syllabic',event['lyric2']['syllabic']);core.sub(lyric,'text',event['lyric2']['text'])

KINDS={'':'major','m':'minor','7':'dominant','maj7':'major-seventh','m7':'minor-seventh','mMaj7':'major-minor','dim':'diminished','dim7':'diminished-seventh','m7b5':'half-diminished','aug':'augmented','sus2':'suspended-second','sus4':'suspended-fourth','6':'major-sixth','m6':'minor-sixth','9':'dominant-ninth','maj9':'major-ninth','m9':'minor-ninth','7sus4':'dominant','add9':'major','madd9':'minor',
       '13':'dominant-13th','7b9':'dominant','7#9':'dominant','m11':'minor-11th','69':'major-sixth','7#5':'dominant','7b5':'dominant'}
# Added, removed or altered degrees written after the kind, so MuseScore reads the exact chord: (value, alter, type).
DEGREES={'add9':((9,0,'add'),),'madd9':((9,0,'add'),),'7sus4':((3,0,'subtract'),(4,0,'add')),
         '7b9':((9,-1,'add'),),'7#9':((9,1,'add'),),'69':((9,0,'add'),),'7b5':((5,-1,'alter'),),'7#5':((5,1,'alter'),)}

# How chord symbols are written, chosen by the user: the suffix shown for each quality (e.g. 7M instead of maj7) and
# whether altered roots are written with flats or sharps ('auto' keeps the spelling of the key). Set per score.
CHORD_TEXT={}
CHORD_ACCIDENTALS='auto'
STEP_PC={'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}
FLAT_NAMES=['C','Db','D','Eb','E','F','Gb','G','Ab','A','Bb','B'];SHARP_NAMES=['C','C#','D','D#','E','F','F#','G','G#','A','A#','B']

def set_chord_notation(notation):
    global CHORD_TEXT,CHORD_ACCIDENTALS
    notation=notation if isinstance(notation,dict) else {}
    texts=notation.get('texts') if isinstance(notation.get('texts'),dict) else {}
    CHORD_TEXT={str(q):str(t)[:12] for q,t in texts.items() if q in KINDS and isinstance(t,str)}
    CHORD_ACCIDENTALS=notation.get('accidentals') if notation.get('accidentals') in ('flats','sharps') else 'auto'

def respell(step,alt):
    """An altered note written with flats or sharps as preferred; natural notes and 'auto' keep their spelling."""
    if not alt or CHORD_ACCIDENTALS=='auto':return step,alt
    name=(FLAT_NAMES if CHORD_ACCIDENTALS=='flats' else SHARP_NAMES)[(STEP_PC[step]+alt)%12]
    return name[0],name.count('#')-name.count('b')

def emit_harmony(measure,event,barstart,ps):
    h=core.sub(measure,'harmony',placement='above');rt=core.sub(h,'root');step,alt=ps[event['root']]
    if event.get('root_spelling'):
        spelling=event['root_spelling'];step=spelling[0];alt=spelling.count('#')-spelling.count('b')
    step,alt=respell(step,alt)
    core.sub(rt,'root-step',step)
    if alt:core.sub(rt,'root-alter',alt)
    q=event['quality'];core.sub(h,'kind',core.KINDS[q],text=CHORD_TEXT.get(q,q))
    if event.get('bass_spelling'):
        # Slash chord (G/B): the bass note is written after the symbol; the diagram keeps the chord itself.
        spelling=event['bass_spelling'];bstep,alter=respell(spelling[0],spelling.count('#')-spelling.count('b'))
        b=core.sub(h,'bass');core.sub(b,'bass-step',bstep)
        if alter:core.sub(b,'bass-alter',alter)
    for value,alter,kind in DEGREES.get(q,()):
        d=core.sub(h,'degree');core.sub(d,'degree-value',value);core.sub(d,'degree-alter',alter);core.sub(d,'degree-type',kind)
    offset=event['tick']-barstart
    if offset:core.sub(h,'offset',offset)

def emit_marker(measure,kind,text=None):
    """Segno and coda signs at the start of a measure; Fine, To Coda and D.S./D.C. at its end (score_repeats.fold_jumps)."""
    at_end=kind in ('fine','to_coda','jump')
    d=core.sub(measure,'direction',placement='above');dt=core.sub(d,'direction-type')
    if kind=='segno':core.sub(dt,'segno');core.sub(d,'sound',segno='segno')
    elif kind=='coda':core.sub(dt,'coda');core.sub(d,'sound',coda='coda')
    elif kind=='fine':core.sub(dt,'words','Fine',justify='right',font_style='italic');core.sub(d,'sound',fine='yes')
    elif kind=='to_coda':core.sub(dt,'words','To Coda',justify='right',font_style='italic');core.sub(d,'sound',tocoda='coda')
    else:
        core.sub(dt,'words',text,justify='right',font_style='italic')
        core.sub(d,'sound',**({'dacapo':'yes'} if text.startswith('D.C.') else {'dalsegno':'segno'}))
    return at_end

def emit_lyric_text(measure,item):
    """A line of the lyric written as text below the staff (speech, or a sung line the melody does not play).
    Italic words below the staff become an Expression text in MuseScore, which never widens the measure."""
    d=core.sub(measure,'direction',placement='below');dt=core.sub(d,'direction-type')
    # A fixed height between the staff and the syllables: autoplace alone let a long line run into the next notes.
    core.sub(dt,'words',item['text'],default_y=-100,font_style='italic')
    if item.get('offset'):core.sub(d,'offset',item['offset'])

def lyric_text_width(bar):
    """Room, in staff spaces, the lyric lines written as text under a bar need. The text does not widen the measure
    in MuseScore, so the bar is planned at least this wide; otherwise a long line at the end of a system runs past
    the right margin."""
    import lyrics_width
    return max((lyrics_width.text_width(item['text'])*1.12+item.get('offset',0)/core.DIV*2.4
                for item in bar.get('lyric_texts',())),default=0)

def verse_bars(lyrics,bars):
    """Bars where a sung line of the karaoke starts (a pickup in the second half of a bar counts for the next bar)."""
    if not isinstance(lyrics,dict):return set()
    result=set()
    for line in lyrics.get('lines',[]):
        if line.get('placement')!='notes' or not line.get('measure'):continue
        index=line['measure']-1
        if line.get('measure_position',0)>=.5 and index+1<len(bars):index+=1
        result.add(index)
    return result

def verse_break_penalty(start,verse_starts):
    """Small preference for systems that start where a sung line of the lyric starts. Smaller than the phrase-grid
    penalties of layout_bars, so it only chooses among layouts that fit the four-bar grid equally well."""
    return .08 if verse_starts and start not in verse_starts else 0

BARS_PER_SYSTEM=4     # at most a phrase of four bars per system, as in lead sheets
MAX_CHORDS_PER_SYSTEM=4
# Staff spaces of music a system holds after the clef and key signature. Calibrated on rendered scores: lines
# estimated up to this width were never broken again by MuseScore (a broken line loses its restated chord).
SYSTEM_WIDTH=98

def lyric_width(text):
    """Estimated width, in staff spaces, of one syllable under the staff: the text measured in a font with the metrics
    of MuseScore's lyric font (lyrics_width.py, 10 pt Edwin) plus the least distance between syllables
    (lyricsMinDistance 1.2). The 20% margin and the 1.8 constant keep SYSTEM_WIDTH=98 valid: with them no system of
    Galanteador, Sexta-feira rotineira or Mamãe que é mamãe was broken again by MuseScore 4.7."""
    import lyrics_width
    return lyrics_width.text_width(text)*1.2+1.8

def note_width(note):
    """Estimated width, in staff spaces, of one melody attack: its syllable (with room for the hyphen inside a word)
    or the note itself."""
    lyric=note.get('lyric')
    if not lyric or not lyric.get('text'):return 2.4
    return max(2.4,lyric_width(lyric['text'])+(.6 if lyric.get('syllabic') in ('begin','middle') else 0))

def chord_widths(chords):
    """(width, leading space) each chord symbol with its keyboard diagram needs, in staff spaces, by tick. Chords
    closer than three beats get the extra leading space convert_score gives them (none at a system start)."""
    widths={};previous=None
    for c in sorted(chords,key=lambda c:c['tick']):
        name=len(CHORD_TEXT.get(c.get('quality',''),c.get('quality','')))+1
        distance=(c['tick']-previous['tick'])/core.DIV if previous else 3
        widths[c['tick']]=(21+max(0,name-2)*.8,max(0,(3-distance)*6))
        previous=c
    return widths

def layout_bars(bars,notes,chords,max_chords=MAX_CHORDS_PER_SYSTEM,first_page_capacity=7,section_starts=(),bars_per_system=BARS_PER_SYSTEM,verse_starts=()):
    """Systems with as few lines as fit, each section starting a new one; among equally short layouts, lines end on
    the phrase grid (every two and four bars from the section start). A bar's width is the larger of its notes
    (with their words, or a lyric line written as text under it) and its chord symbols with keyboard diagrams."""
    widths=chord_widths(chords);note_widths=[];chord_ticks=[]
    for b in bars:
        note_widths.append(max(sum(note_width(n) for n in notes if b['start']<=n['start']<b['end'])+2,lyric_text_width(b)))
        chord_ticks.append(sorted(t for t in widths if b['start']<=t<b['end']))
    def fits(group):
        if len(group)>bars_per_system or sum((bars[i]['end']-bars[i]['start'])/core.DIV for i in group)>16:return False
        if sum(len(chord_ticks[i]) for i in group)>max_chords:return False
        total=0;first=True
        for index in group:
            chord=0
            if index==group[0] and (not chord_ticks[index] or chord_ticks[index][0]!=bars[index]['start']):
                chord+=21  # the chord sounding at the system start is written again
            for tick in chord_ticks[index]:
                chord+=widths[tick][0]+(0 if first else widths[tick][1]);first=False
            total+=max(note_widths[index],chord)
        return total<=SYSTEM_WIDTH
    def section_lines(first,stop):
        # Fewest systems first (pages matter most); among layouts with as many systems, the one whose lines end
        # on the phrase grid (every two and four bars from the section start).
        best={first:(0.,[])}
        for end in range(first+1,stop+1):
            options=[]
            for size in range(1,bars_per_system+1):
                start=end-size
                if start<first or start not in best:continue
                group=list(range(start,end))
                if size>1 and not fits(group):continue
                offset=end-first
                penalty=0 if end==stop else (.3 if offset%2 else 0)+(.1 if offset%4 else 0)+verse_break_penalty(end,verse_starts)
                options.append((best[start][0]+1+penalty,best[start][1]+[group]))
            best[end]=min(options,key=lambda item:item[0])
        return best[stop][1]
    edges=sorted({0,len(bars)}|{int(s) for s in section_starts if 0<int(s)<len(bars)})
    lines=[]
    for first,stop in zip(edges,edges[1:]):lines.extend(section_lines(first,stop))
    pages=math.ceil((len(lines)+7-first_page_capacity)/7);base,extra=divmod(len(lines),pages)
    counts=[base+(page<extra) for page in range(pages)]
    while counts[0]>first_page_capacity:
        page=min(range(1,pages),key=lambda p:counts[p])
        counts[0]-=1;counts[page]+=1
    starts={};cursor=0
    for page in range(pages):
        for j in range(counts[page]):
            starts[lines[cursor][0]]='page' if page and j==0 else 'line';cursor+=1
    return starts,lines

def high_melody_layout(part,lines):
    """Reserve each system's written pitch envelope without transposing its notes."""
    measures=part.findall('measure');result=[];image_height=14*84/282
    for line in lines:
        pitches=[];music_top=0.0
        for index in line:
            # MusicXML repeats tied pitches across measures, so continuations and
            # grace notes both contribute even when their attack precedes this line.
            for note in measures[index].findall('note'):
                pitch=note.find('pitch')
                if pitch is None:continue
                step=pitch.findtext('step');octave=int(pitch.findtext('octave'))
                diatonic=octave*7+'CDEFGAB'.index(step)
                note_y=(38-diatonic)/2  # F5 is the upper treble staff line.
                clearance=3.5 if note.find('grace') is not None else 1.3
                music_top=min(music_top,note_y-clearance)
                pitches.append(diatonic)
        lift=max(0.0,(-6.7+image_height)-(music_top-.8))
        # Round upward so floating point rounding never consumes the clearance.
        lift=math.ceil(lift*1000)/1000
        image_y=-6.7-lift
        result.append({'first_measure_index':line[0],'last_measure_index':line[-1],
                       'highest_diatonic_pitch':max(pitches) if pitches else None,
                       'music_top_spatium':music_top,'lift_spatium':lift,
                       'image_y_spatium':image_y,'harmony_offset_y_spatium':-2.5-lift,
                       'minimum_clearance_spatium':.8})
        if pitches:assert image_y+image_height<=music_top-.8+1e-8
    return result

def spell_melody(notes,chords,harmony,key,bars,ppq,origin):
    """Aplica melody_spelling às notas (posições relativas à origem) e devolve os contadores do relatório."""
    import melody_spelling
    from batch_harmony import CHORD_INTERVALS
    changes=sorted(((round(kc['tick']/ppq*core.DIV)-origin,kc) for kc in harmony.get('key_changes',[])),key=lambda item:item[0])
    def active(position):
        current=key
        for tick,change in changes:
            if tick<=position:current=change
        return current
    def key_at(position):
        current=active(position);return current.get('fifths',0),current.get('minor',False)
    cache={}
    def key_map_at(position):
        signature=key_at(position)
        if signature not in cache:cache[signature]=core.spellings(*signature)
        return cache[signature]
    usable=[c for c in chords if c.get('quality') in CHORD_INTERVALS]
    respelled=melody_spelling.spell_notes(notes,usable,key_at,key_map_at,lambda q:CHORD_INTERVALS[q])
    courtesy=melody_spelling.courtesy_accidentals(notes,bars,lambda position:key_at(position)[0])
    result={}
    if respelled:result['contextual_spellings']=respelled
    if courtesy:result['courtesy_accidentals']=courtesy
    return result

def build_musicxml(parsed,config,harmony,out,title):
    core.progress('Organizando a melodia e conferindo a duração das notas…')
    set_chord_notation(config.get('chord_notation'))
    inversion=core.diagram_inversion(config.get('diagram_inversion'))
    event_inversions=[core.chord_diagram_inversion(chord,inversion) for chord in harmony['events']]
    custom_diagrams=any(value!=inversion for value in event_inversions)
    metadata=core.score_metadata(config,title)
    title=metadata['title'];header=core._header_layout(metadata)
    summary,meters,tempos=core.meta(parsed);origin,end,bars=core.make_bars(parsed,meters)
    chords_only=bool(config.get('chords_only'))
    if chords_only:
        # Chord chart for a MIDI without a complete melody: rhythm slashes carry the chords; no note is invented.
        notes,grids,note_report=[],{},{'chords_only':True,'grace_notes_preserved':0,'quantized_attack_collisions':0}
    else:
        try:notes,grids,note_report=core.melody_events(parsed,config,origin,end)
        except core.NeedsReview as error:
            error.review_points=core.rhythm_failure_points(error,parsed,config,origin,bars)
            raise
    transpose=config.get('melody_transpose_semitones',0)
    assert transpose%12==0,'Only octave placement may change without changing the key'
    if transpose:
        for n in notes:
            n['pitch']+=transpose
            if 'graces' in n:n['graces']=[p+transpose for p in n['graces']]
        note_report['octave_transposition_semitones']=transpose
        assert all(0<=n['pitch']<=127 for n in notes)
    output_transpose=core.transpose_semitones(config.get('transpose_semitones'))
    original_key=harmony.get('key') or {'fifths':0,'minor':False}
    if output_transpose and not chords_only:
        for note in notes:
            note['pitch']+=output_transpose
            if 'graces' in note:note['graces']=[pitch+output_transpose for pitch in note['graces']]
        if any(not 0<=pitch<=127 for note in notes for pitch in [note['pitch']]+note.get('graces',[])):
            raise core.NeedsReview('A transposição ultrapassa as alturas permitidas pelo MIDI. Escolha um intervalo menor.',kind='transpose_range')
        harmony=core.transpose_harmony(harmony,output_transpose)
        note_report['output_transposition_semitones']=output_transpose
    # MIDI lyrics (karaoke files) go under the melody unless the user turned them off.
    note_report['lyrics']=core.attach_lyrics(parsed,notes,origin,bars,config) if not chords_only and config.get('include_lyrics',True) else 0
    # When each syllable is sung (also the ones without a note of their own): used to find the parts, not reported.
    sung_ticks=note_report['lyrics'].pop('sung_ticks',None) if isinstance(note_report['lyrics'],dict) else None
    second=[]
    if config.get('second_voice_sources') and not chords_only:
        # Optional second voice (counter-melody or duet) on its own staff, with the same time grid and transposition.
        second_config={key:value for key,value in config.items() if key not in ('melody_sources','merge_mode','melody_sections','solo_sources','second_voice_sources','melody_source_priority')}
        second_config.update(melody_sources=config['second_voice_sources'],solo_sources=[],reserve_high_melody=False)
        second,_,_=core.melody_events(parsed,second_config,origin,end)
        for note in second:
            note['pitch']+=output_transpose
            note.pop('lyric',None)
        if any(not 0<=note['pitch']<=127 for note in second):raise core.NeedsReview('A transposição ultrapassa as alturas da segunda voz.',kind='transpose_range')
        note_report['second_voice_notes']=len(second)
    key=harmony.get('key') or {'fifths':0,'minor':False};fifths=key.get('fifths',0);ps=core.spellings(fifths,key.get('minor',False))
    if config.get('header_info'):
        # Key, time signature and tempo under the title; the first tempo mark is then hidden in the score
        # (it still sets the playback tempo). Later tempo changes stay in the music.
        first_tempo=max([t for t in tempos if round(t['tick']/parsed['ppq']*core.DIV)<=origin] or [tempos[0]],key=lambda t:t['tick'])
        metadata['info']=core.header_info(key,bars[0]['meter'],first_tempo['bpm'],original_key if output_transpose else None,
                                          len(harmony.get('key_changes',[])))
        header=core._header_layout(metadata)
    chords=[]
    for ev in harmony['events']:
        quarter=ev['tick']/parsed['ppq']
        # Com swing, as cifras seguem as colcheias escritas retas.
        if note_report.get('swing'):quarter=melody_rhythm.straighten(quarter,note_report['swing']['ratio'])
        tick=round(quarter*core.DIV)-origin
        beat=tick//core.DIV;grid=melody_rhythm.grid_div(grids.get(beat,4));snapped=round(beat*core.DIV+round((tick-beat*core.DIV)/(core.DIV/grid))*core.DIV/grid)
        adjustment=(snapped-tick)/core.DIV;tick=snapped
        if 0<=tick<end-origin:chords.append(dict(ev,tick=tick,placement_adjustment_quarters=adjustment))
    unique_chords={}
    for chord in chords:
        previous=unique_chords.get(chord['tick'])
        if previous is not None and any(item.get('diagram_inversion') is not None for item in (previous,chord)):
            quarter=(chord['tick']+origin)/core.DIV
            error=core.NeedsReview('Duas cifras com posições de diagramas personalizadas coincidem após a quantização. Afaste suas posições e gere novamente.',[quarter],kind='diagram_collision')
            point=core.locate_review_point(quarter,origin,bars,error.kind,str(error),True)
            error.review_points=[point] if point is not None else []
            raise error
        unique_chords[chord['tick']]=chord
    chords=sorted(unique_chords.values(),key=lambda c:c['tick'])
    if not chords:raise core.NeedsReview('Nenhuma cifra suficientemente apoiada pelas notas do acompanhamento.')
    if len(chords)>len(bars)*3:raise core.NeedsReview('Harmonia excessivamente densa; revisar detecção de acordes.')
    if chords_only:
        notes[:]=chord_slashes(bars,chords)
        # Slashes sit on the middle line without an accidental: Bb in flat keys, also after a modulation.
        for bar in bars:
            active=key
            for change in harmony.get('key_changes',[]):
                if round(change['tick']/parsed['ppq']*core.DIV)<=origin+bar['start']:active=change
            for slash in notes:
                if bar['start']<=slash['start']<bar['end'] and active.get('fifths',0)<0:slash['pitch']=SLASH_PITCH-1
        note_report['chord_slashes']=len(notes)
        # Chord sheet pasted as text: the words under each chord go on the first slash of its measure.
        words={int(index):text for index,text in (config.get('sheet_lyrics') or {}).items() if str(text).strip()}
        for index,text in words.items():
            first=next((n for n in notes if n['start']==bars[index]['start']),None) if index<len(bars) else None
            if first is not None:first['lyric']={'text':str(text).strip(),'syllabic':'single'}
        if words:note_report['lyrics']={'syllables':len(words),'attached':sum('lyric' in n for n in notes)}
    if config.get('fold_repeats') and not second:
        # Optional: literal repetitions are written once between repeat barlines (score_repeats.py).
        import score_repeats
        jump=score_repeats.fold_jumps(bars,notes,chords,grids,tempos,harmony.get('key_changes',[]),parsed['ppq'],origin,core.DIV)
        if jump:note_report['repeat_jump']=jump
        note_report['repeat_folds']=score_repeats.fold_repeats(bars,notes,chords,grids,tempos,harmony.get('key_changes',[]),parsed['ppq'],origin,core.DIV)
    chord_staff=[]
    if config.get('chord_staff') and not chords_only:
        # Optional chord staff below the melody: each chord written with the notes of its keyboard diagram, held until
        # the next chord. Nothing is invented: these are the same keys the diagrams show.
        inversion_default=core.diagram_inversion(config.get('diagram_inversion'))
        ordered=sorted(chords,key=lambda c:c['tick'])
        for index,chord in enumerate(ordered):
            stop=ordered[index+1]['tick'] if index+1<len(ordered) else bars[-1]['end']
            if stop<=chord['tick']:continue
            _,_,pitches=core.keyboard_asset(chord['root'],chord['quality'],core.chord_diagram_inversion(chord,inversion_default),core.C_LABEL)
            chord_staff.append({'start':chord['tick'],'end':stop,'pitches':pitches,'pitch':pitches[len(pitches)//2],'chord_staff':True})
        note_report['chord_staff_chords']=len(chord_staff)
    core.progress('Distribuindo notas e cifras pelos compassos e páginas…')
    # A large custom heading consumes first-page space. Rebalance page breaks,
    # retaining the same systems and events, instead of compressing the music.
    first_capacity=max(3,7-max(0,math.ceil((header['height']-16)/16)))
    sections=[]
    if config.get('section_marks') and not config.get('line_starts'):
        # Where the song's parts start (Intro, A, Refrão…), found in its words and repetitions (score_sections.py).
        import score_sections
        # Folded repeats move the bars after them; the words on the written notes are used then.
        sung=sung_ticks if not note_report.get('repeat_folds') and not note_report.get('repeat_jump') else None
        sections=score_sections.detect_sections(bars,notes+second,chords,core.DIV,sung)
        if sections:note_report['sections']=sections
    starts,lines=core.layout_bars(bars,notes,chords,config.get('max_chords_per_system',MAX_CHORDS_PER_SYSTEM),first_capacity,
                                  [item['bar'] for item in sections],verse_starts=verse_bars(note_report.get('lyrics'),bars))
    if config.get('line_starts'):
        # A chord sheet keeps its own lines: each one starts a system (MuseScore adds page breaks as needed).
        first_bars=sorted({int(index) for index in config['line_starts'] if 0<int(index)<len(bars)})
        starts={index:'line' for index in first_bars}
        edges=[0]+first_bars+[len(bars)];lines=[list(range(a,b)) for a,b in zip(edges,edges[1:]) if b>a]
    # Re-state the active chord at each new system, as in the approved pilot.
    for index in starts:
        tick=bars[index]['start'];prior=[c for c in chords if c['tick']<=tick]
        if prior and not any(c['tick']==tick for c in chords):chords.append(dict(max(prior,key=lambda c:c['tick']),tick=tick,reminder=True))
    chords.sort(key=lambda c:c['tick'])
    if not chords_only and config.get('contextual_spelling',True):
        # Grafia das notas pela cifra vigente e pelo contexto (G# sobre E7 em Dó maior), com cortesias.
        note_report.update(spell_melody(notes+second,chords,harmony,key,bars,parsed['ppq'],origin))
    root=E.Element('score-partwise',version='4.0');work=core.sub(root,'work');core.sub(work,'work-title',title)
    ident=core.sub(root,'identification')
    if metadata['composer']:core.sub(ident,'creator',metadata['composer'],type='composer')
    if metadata['artist']:core.sub(ident,'creator',metadata['artist'],type='performer')
    enc=core.sub(ident,'encoding');core.sub(enc,'software','Preparação de melodia e cifras — MuseScore Studio')
    # Staff height in millimeters: 4 spaces of the chosen size (MuseScore reads it as the spatium on import).
    spatium=float(core.score_style_values(config.get('score_style')).get('spatium','1.75'))
    defaults=core.sub(root,'defaults');scale=core.sub(defaults,'scaling');core.sub(scale,'millimeters',round(spatium*4,3));core.sub(scale,'tenths',40)
    pl=core.sub(defaults,'page-layout');core.sub(pl,'page-height',1697.14);core.sub(pl,'page-width',1200)
    margins=core.sub(pl,'page-margins',type='both')
    for tag,val in [('left-margin',80),('right-margin',80),('top-margin',70),('bottom-margin',70)]:core.sub(margins,tag,val)
    subtitle_y=1575-max(0,len(header['title'].splitlines())-1)*header['title_size']*2.2
    composer_y=subtitle_y-len(header['subtitle'].splitlines())*24-8
    for role,y,txt,size,x,alignment in [
        ('title',1615,header['title'],header['title_size'],600,'center'),
        ('subtitle',subtitle_y,header['subtitle'],10,600,'center'),
        ('composer',composer_y,header['composer'],10,1120,'right')]:
        if not txt:continue
        cr=core.sub(root,'credit',page=1);core.sub(cr,'credit-type',role)
        core.sub(cr,'credit-words',txt,default_x=x,default_y=y,justify=alignment,valign='top',font_size=size)
    labels=core.instrument_labels(notes,origin) if note_report.get('melody_merge') else []
    staff_groups=None
    if config.get('staff_per_source') and note_report.get('melody_merge'):
        names=core.instrument_names();staff_groups=[]
        for n in notes:
            source=n.get('source');identity=None if source is None else tuple(source[k] for k in ('track','channel','program'))
            group=next((g for g in staff_groups if g['identity']==identity),None)
            if group is None:
                program=source['program'] if source else -1
                full,short=names[program] if type(program) is int and 0<=program<len(names) else ('Melodia','Mel.')
                group={'identity':identity,'name':full,'short':short,'notes':[]}
                staff_groups.append(group)
            group['notes'].append(n)
        if len(staff_groups)<2:staff_groups=None
        else:
            # The busiest instrument goes on top: it carries chords and diagrams, so fewer of its
            # rests are split at chord positions. Ties keep the order of first entry.
            staff_groups.sort(key=lambda group:-len(group['notes']))
            shorts=[group['short'] for group in staff_groups]
            for group in staff_groups:
                if shorts.count(group['short'])>1:group['short']=group['name'] if len(group['name'])<=14 else group['name'][:12].rstrip()+'.'
    if second:
        # Melody on top (chords and diagrams), second voice below; MuseScore paginates freely with two staves.
        groups=staff_groups or [{'name':'Melodia','short':'Mel.','notes':notes}]
        staff_groups=groups+[{'name':'Segunda voz','short':'2ª v.','notes':second}]
    if chord_staff:
        groups=staff_groups or [{'name':'Melodia','short':'Mel.','notes':notes}]
        staff_groups=groups+[{'name':'Acordes','short':'Ac.','notes':chord_staff}]
    if staff_groups:
        # One staff per instrument: each name stays in the system margin, so no captions are
        # needed inside the staff. With more staves per system, MuseScore paginates freely.
        labels=[]
        starts={index:('line' if kind=='page' else kind) for index,kind in starts.items()}
        part_specs=[(f'P{index+1}',group['name'],group['notes'],group['short']) for index,group in enumerate(staff_groups)]
    else:part_specs=[('P1','Teclado',notes,'')]
    parts=core.sub(root,'part-list')
    for pid,name,_,short in part_specs:
        p=core.sub(parts,'score-part',id=pid);core.sub(p,'part-name',name);core.sub(p,'part-abbreviation',short)
        instrument='I1' if pid=='P1' else pid+'-I1'
        si=core.sub(p,'score-instrument',id=instrument);core.sub(si,'instrument-name','Piano');mi=core.sub(p,'midi-instrument',id=instrument);core.sub(mi,'midi-channel',1);core.sub(mi,'midi-program',1)
    primary_part=None
    for part_index,(pid,name,part_notes,_) in enumerate(part_specs):
        primary=part_index==0
        part=core.sub(root,'part',id=pid);lastmeter=None;printedtempo=None;lastkey=None
        if primary:primary_part=part
        for i,b in enumerate(bars):
            measure=core.sub(part,'measure',number=i+1,**({'implicit':'yes'} if b['partial'] else {}))
            if primary and i in starts:core.sub(measure,'print',**({'new-page':'yes'} if starts[i]=='page' else ({'new-system':'yes'} if i else {})))
            activekey=key
            for kc in harmony.get('key_changes',[]):
                if round(kc['tick']/parsed['ppq']*core.DIV)<=origin+b['start']:activekey=kc
            currentkey=(activekey['fifths'],activekey.get('minor',False));ps=core.spellings(*currentkey)
            if i==0 or b['meter']!=lastmeter or currentkey!=lastkey:
                at=core.sub(measure,'attributes')
                if i==0:core.sub(at,'divisions',core.DIV)
                if currentkey!=lastkey:
                    k=core.sub(at,'key');core.sub(k,'fifths',currentkey[0]);core.sub(k,'mode','minor' if currentkey[1] else 'major')
                if b['meter']!=lastmeter:
                    ts=core.sub(at,'time');core.sub(ts,'beats',b['meter'][0]);core.sub(ts,'beat-type',b['meter'][1])
                if i==0:
                    # A low second voice reads better in the bass clef; the melody keeps the treble clef.
                    low=not primary and part_notes and sorted(n['pitch'] for n in part_notes)[len(part_notes)//2]<(57 if part_notes[0].get('chord_staff') else 55)
                    core.sub(at,'staves',1);cl=core.sub(at,'clef');core.sub(cl,'sign','F' if low else 'G');core.sub(cl,'line',4 if low else 2)
            lastmeter=b['meter']
            lastkey=currentkey
            if b.get('repeat_start') or b.get('ending_start'):
                bl=core.sub(measure,'barline',location='left')
                if b.get('repeat_start'):core.sub(bl,'bar-style','heavy-light')
                if b.get('ending_start'):core.sub(bl,'ending',f"{b['ending_start']}.",number=str(b['ending_start']),type='start')
                if b.get('repeat_start'):core.sub(bl,'repeat',direction='forward')
            if primary and b.get('segno'):emit_marker(measure,'segno')
            if primary and b.get('coda'):emit_marker(measure,'coda')
            for section in (sections if primary else []):
                if section['bar']==i:
                    d=core.sub(measure,'direction',placement='above');core.sub(core.sub(d,'direction-type'),'rehearsal',section['label'])
            active_tempo=max([t for t in tempos if round(t['tick']/parsed['ppq']*core.DIV)<=origin+b['start']] or [tempos[0]],key=lambda t:t['tick'])
            ts=[dict(active_tempo,tick=origin+b['start'])] if i==0 else []
            ts.extend(t for t in tempos if origin+b['start']<=round(t['tick']/parsed['ppq']*core.DIV)<origin+b['end'] and not (i==0 and round(t['tick']/parsed['ppq']*core.DIV)==origin))
            for t in (ts if primary else []):
                bpm=round(t['bpm'],2);di=core.sub(measure,'direction',placement='above')
                if printedtempo is None or abs(bpm-printedtempo)>=3:
                    dt=core.sub(di,'direction-type');mt=core.sub(dt,'metronome');core.sub(mt,'beat-unit','quarter');core.sub(mt,'per-minute',round(bpm) if abs(bpm-round(bpm))<.1 else bpm);printedtempo=bpm
                else:dt=core.sub(di,'direction-type');core.sub(dt,'words','',print_object='no')
                offset=round(t['tick']/parsed['ppq']*core.DIV)-origin-b['start'] if i else 0
                if offset:core.sub(di,'offset',offset)
                core.sub(di,'sound',tempo=bpm)
            if primary and i==0 and note_report.get('swing'):
                # Colcheias escritas retas, tocadas longa-curta: a indicação de texto e a razão para a reprodução.
                first,second_part=melody_rhythm.swing_text(note_report['swing']['ratio'])
                di=core.sub(measure,'direction',placement='above');dt=core.sub(di,'direction-type')
                core.sub(dt,'words','Swing',font_weight='bold')
                sound=core.sub(di,'sound');swing=core.sub(sound,'swing')
                core.sub(swing,'first',first);core.sub(swing,'second',second_part);core.sub(swing,'swing-type','eighth')
            for c in (chords if primary else []):
                if b['start']<=c['tick']<b['end']:core.emit_harmony(measure,c,b['start'],ps)
            for item in (b.get('lyric_texts',()) if primary else ()):emit_lyric_text(measure,item)
            for label in labels:
                if b['start']<=label['tick']<b['end']:
                    direction=core.sub(measure,'direction',placement='above')
                    words=core.sub(core.sub(direction,'direction-type'),'words',label['text'],font_size=9,font_style='italic')
                    if label['tick']>b['start']:core.sub(direction,'offset',label['tick']-b['start'])
                    core.sub(direction,'staff',1)
            selected=[n for n in part_notes if n['end']>b['start'] and n['start']<b['end']];sequence=[];cursor=b['start']
            for n in selected:
                a=max(n['start'],b['start']);z=min(n['end'],b['end'])
                if a>cursor:sequence.append((cursor,a,None))
                sequence.append((a,z,n));cursor=z
            if cursor<b['end']:sequence.append((cursor,b['end'],None))
            for a,z,n in sequence:
                inner_chords=sorted({c['tick'] for c in chords if a<c['tick']<z}) if primary else []
                if n is None and a==b['start'] and z==b['end'] and not inner_chords:core.emit_note(measure,a,z-a,False,None,ps,True)
                else:
                    bounds=[a]+inner_chords+[z]
                    for left,right in zip(bounds,bounds[1:]):
                        for start,dur,tr in core.pieces(left,right,grids,b):core.emit_note(measure,start,dur,tr,n,ps)
            assert sum(int(n.findtext('duration','0')) for n in measure.findall('note') if n.find('chord') is None)==b['end']-b['start']
            if primary and i+1 in config.get('tuplet_bracket_measures',[]):
                # Explicit brackets keep adjacent triplet labels centered over their
                # separate groups after a very wide measure is respaced by MuseScore.
                for tuplet in measure.findall('.//tuplet'):
                    if tuplet.get('type')=='start':tuplet.set('bracket','yes');tuplet.set('placement','above')
            if primary:
                if b.get('fine'):emit_marker(measure,'fine')
                if b.get('to_coda'):emit_marker(measure,'to_coda')
                if b.get('jump'):emit_marker(measure,'jump',b['jump'])
            if b.get('repeat_end') or b.get('ending_stop'):
                bl=core.sub(measure,'barline',location='right')
                core.sub(bl,'bar-style','light-heavy' if b.get('repeat_end') or i==len(bars)-1 else 'regular')
                if b.get('ending_stop'):core.sub(bl,'ending',number=str(b['ending_stop']),type='stop' if b.get('repeat_end') else 'discontinue')
                if b.get('repeat_end'):core.sub(bl,'repeat',direction='backward')
            elif i==len(bars)-1 or b.get('fine'):bl=core.sub(measure,'barline',location='right');core.sub(bl,'bar-style','light-heavy')
            elif b.get('jump') and i+1<len(bars) and bars[i+1].get('coda'):bl=core.sub(measure,'barline',location='right');core.sub(bl,'bar-style','light-light')
    E.indent(root);xml=out/'melodia.musicxml';E.ElementTree(root).write(xml,encoding='utf-8',xml_declaration=True)
    report={'source':parsed['path'],'source_sha256':parsed['sha256'],'title':title,'origin_removed_quarters':origin/core.DIV,'measures':len(bars),'systems':len(lines),'pages_planned':1+sum(kind=='page' for kind in starts.values()),'key':key,'melody_sources':config['melody_sources'],'chord_changes':sum(not c.get('reminder') for c in chords),'chord_symbols':len(chords),'melody_analysis':note_report,'notes':notes,'chords':chords,'bars':bars,'system_starts':starts,'harmonic_warnings':harmony.get('warnings',[])}
    report.update(score_metadata=metadata,diagram_inversion=inversion,custom_diagram_inversions=custom_diagrams,transpose_semitones=output_transpose)
    if config.get('score_style'):report['score_style']=dict(config['score_style'])
    if second:report['second_voice']=second
    if chord_staff:report['chord_staff_notes']=[{'start':event['start'],'pitch':pitch} for event in chord_staff for pitch in event['pitches']]
    report['note_names']='solfejo' if config.get('note_names')=='solfejo' else 'letras'
    report['diagram_style']=config.get('diagram_style','classico')
    report['page_footer']=bool(config.get('page_footer',False))
    report['chord_texts']=dict(CHORD_TEXT)
    if labels:report['instrument_labels']=labels
    if staff_groups:report['staff_per_source']={'staves':len(staff_groups),'names':[group['name'] for group in staff_groups],
        'notes_per_staff':[len(group['notes']) for group in staff_groups]}
    if config.get('reserve_high_melody'):
        report['high_melody_layout']=core.high_melody_layout(primary_part,lines)
        report['tuplet_bracket_measures']=config.get('tuplet_bracket_measures',[])
    return xml,report
