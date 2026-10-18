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


def make_bars(parsed,meters):
    ppq=parsed['ppq'];pitched=[n for n in parsed['notes'] if n['channel']!=10 and 20<=n['pitch']<=108 and n['program']<112]
    if not pitched:raise core.NeedsReview('Sem notas melódicas válidas.')
    initial=meters[0];barq=initial['numerator']*4/initial['denominator']
    origin=math.floor(min(n['start'] for n in pitched)/ppq/barq)*round(barq*core.DIV)
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
    return origin,start,bars

TYPES={3840:('whole',0),2880:('half',1),1920:('half',0),1440:('quarter',1),960:('quarter',0),720:('eighth',1),480:('eighth',0),360:('16th',1),240:('16th',0),120:('32nd',0),60:('64th',0),30:('128th',0)}

def pieces(start,end,grids):
    t=start
    while t<end:
        beat=t//core.DIV;beatend=(beat+1)*core.DIV;mode=grids.get(beat,4)
        if mode%3==0:
            d=max([x for x in (640,320,160,80,40) if x<=min(end,beatend)-t] or [0])
            if not d:raise core.NeedsReview(f'Duração de tercina indefinida no tempo {t/core.DIV:.3f}.')
            yield t,d,True;t+=d;continue
        triplenext=min([b*core.DIV for b,v in grids.items() if v%3==0 and b*core.DIV>t] or [end])
        limit=min(end,triplenext)
        if t%core.DIV:limit=min(limit,beatend)
        candidates=[d for d in core.TYPES if d<=limit-t]
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
    else:core.pitch_xml(n,event['pitch'],ps)
    core.sub(n,'duration',dur)
    tiein=event is not None and start>event['start'];tieout=event is not None and start+dur<event['end']
    if tiein:core.sub(n,'tie',type='stop')
    if tieout:core.sub(n,'tie',type='start')
    core.sub(n,'voice',1)
    if not full_rest:
        typ,dots=({640:('quarter',0),320:('eighth',0),160:('16th',0),80:('32nd',0),40:('64th',0)}[dur] if triplet else core.TYPES[dur])
        core.sub(n,'type',typ)
        for _ in range(dots):core.sub(n,'dot')
    if triplet:
        tm=core.sub(n,'time-modification');core.sub(tm,'actual-notes',3);core.sub(tm,'normal-notes',2);core.sub(tm,'normal-type','eighth')
    if event is not None and event.get('slash'):core.sub(n,'stem','none');core.sub(n,'notehead','slash')
    if tiein or tieout or triplet:
        nt=core.sub(n,'notations')
        if tiein:core.sub(nt,'tied',type='stop')
        if tieout:core.sub(nt,'tied',type='start')
        if triplet and start%core.DIV==0:core.sub(nt,'tuplet',type='start',number=1,bracket='no')
        if triplet and (start+dur)%core.DIV==0:core.sub(nt,'tuplet',type='stop',number=1)
    if event is not None and start==event['start'] and event.get('lyric') and not event.get('chord'):
        lyric=core.sub(n,'lyric',number='1');core.sub(lyric,'syllabic',event['lyric']['syllabic']);core.sub(lyric,'text',event['lyric']['text'])
    if event is not None and start==event['start'] and event.get('lyric2'):
        # Second verse of a repeated passage, under the first.
        lyric=core.sub(n,'lyric',number='2');core.sub(lyric,'syllabic',event['lyric2']['syllabic']);core.sub(lyric,'text',event['lyric2']['text'])

KINDS={'':'major','m':'minor','7':'dominant','maj7':'major-seventh','m7':'minor-seventh','mMaj7':'major-minor','dim':'diminished','dim7':'diminished-seventh','m7b5':'half-diminished','aug':'augmented','sus2':'suspended-second','sus4':'suspended-fourth','6':'major-sixth','m6':'minor-sixth','9':'dominant-ninth','maj9':'major-ninth','m9':'minor-ninth','7sus4':'dominant','add9':'major','madd9':'minor'}

def emit_harmony(measure,event,barstart,ps):
    h=core.sub(measure,'harmony',placement='above');rt=core.sub(h,'root');step,alt=ps[event['root']]
    if event.get('root_spelling'):
        spelling=event['root_spelling'];step=spelling[0];alt=spelling.count('#')-spelling.count('b')
    core.sub(rt,'root-step',step)
    if alt:core.sub(rt,'root-alter',alt)
    q=event['quality'];core.sub(h,'kind',core.KINDS[q],text=q)
    if event.get('bass_spelling'):
        # Slash chord (G/B): the bass note is written after the symbol; the diagram keeps the chord itself.
        spelling=event['bass_spelling'];b=core.sub(h,'bass');core.sub(b,'bass-step',spelling[0])
        alter=spelling.count('#')-spelling.count('b')
        if alter:core.sub(b,'bass-alter',alter)
    if q in ('add9','madd9'):
        d=core.sub(h,'degree');core.sub(d,'degree-value',9);core.sub(d,'degree-alter',0);core.sub(d,'degree-type','add')
    if q=='7sus4':
        d=core.sub(h,'degree');core.sub(d,'degree-value',3);core.sub(d,'degree-alter',0);core.sub(d,'degree-type','subtract')
        d=core.sub(h,'degree');core.sub(d,'degree-value',4);core.sub(d,'degree-alter',0);core.sub(d,'degree-type','add')
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

def layout_bars(bars,notes,chords,max_chords=3,first_page_capacity=7):
    lines=[];current=[];notecount=chordcount=quarters=0
    for i,b in enumerate(bars):
        nc=sum(b['start']<=n['start']<b['end'] for n in notes)
        cc=sum(b['start']<=c['tick']<b['end'] for c in chords)
        q=(b['end']-b['start'])/core.DIV
        if current and (len(current)>=6 or quarters+q>12 or notecount+nc>38 or chordcount+cc>max_chords):
            lines.append(current);current=[];notecount=chordcount=quarters=0
        current.append(i);notecount+=nc;chordcount+=cc;quarters+=q
    if current:lines.append(current)
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

def build_musicxml(parsed,config,harmony,out,title):
    core.progress('Organizando a melodia e conferindo a duração das notas…')
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
    if output_transpose and not chords_only:
        for note in notes:
            note['pitch']+=output_transpose
            if 'graces' in note:note['graces']=[pitch+output_transpose for pitch in note['graces']]
        if any(not 0<=pitch<=127 for note in notes for pitch in [note['pitch']]+note.get('graces',[])):
            raise core.NeedsReview('A transposição ultrapassa as alturas permitidas pelo MIDI. Escolha um intervalo menor.',kind='transpose_range')
        harmony=core.transpose_harmony(harmony,output_transpose)
        note_report['output_transposition_semitones']=output_transpose
    # MIDI lyrics (karaoke files) go under the melody unless the user turned them off.
    note_report['lyrics']=core.attach_lyrics(parsed,notes,origin) if not chords_only and config.get('include_lyrics',True) else 0
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
    chords=[]
    for ev in harmony['events']:
        tick=round(ev['tick']/parsed['ppq']*core.DIV)-origin
        beat=tick//core.DIV;grid=grids.get(beat,4);snapped=round(beat*core.DIV+round((tick-beat*core.DIV)/(core.DIV/grid))*core.DIV/grid)
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
    starts,lines=core.layout_bars(bars,notes,chords,config.get('max_chords_per_system',3),first_capacity)
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
            for c in (chords if primary else []):
                if b['start']<=c['tick']<b['end']:core.emit_harmony(measure,c,b['start'],ps)
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
                        for start,dur,tr in core.pieces(left,right,grids):core.emit_note(measure,start,dur,tr,n,ps)
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
    if labels:report['instrument_labels']=labels
    if staff_groups:report['staff_per_source']={'staves':len(staff_groups),'names':[group['name'] for group in staff_groups],
        'notes_per_staff':[len(group['notes']) for group in staff_groups]}
    if config.get('reserve_high_melody'):
        report['high_melody_layout']=core.high_melody_layout(primary_part,lines)
        report['tuplet_bracket_measures']=config.get('tuplet_bracket_measures',[])
    return xml,report
