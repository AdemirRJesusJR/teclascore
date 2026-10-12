"""Source-evidenced one-staff score preparation with embedded keyboard diagrams."""
from pathlib import Path
from collections import defaultdict, Counter
from bisect import bisect_right
from fractions import Fraction
from statistics import median
import xml.etree.ElementTree as E
import ast, struct, math, json, zipfile, subprocess, hashlib, re, copy, os, tempfile
from PIL import Image, ImageDraw
from inspect_midi import parse_midi, summarize
from batch_harmony import analyze_harmony, CHORD_INTERVALS
from batch_svg_qa import analyze as analyze_svg

WORK=Path(__file__).resolve().parent
BATCH=Path(os.environ.get('PARTITURAS_JOB',str(Path(tempfile.gettempdir())/'TeclasPartituras')));BATCH.mkdir(parents=True,exist_ok=True)
EXE=r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe'
DIV=960
module=ast.parse((WORK/'build_keyboard_diagrams.py').read_text(encoding='utf8'))
exec(compile(ast.Module(body=[n for n in module.body if isinstance(n,ast.FunctionDef) and n.name in ('md4','svg_keyboard')],type_ignores=[]),'diagram_helpers','exec'))
NS='{http://www.w3.org/2000/svg}';E.register_namespace('',NS[1:-1])

class NeedsReview(Exception):pass

def sub(parent,tag,value=None,**attrs):
    e=E.SubElement(parent,tag,{k.replace('_','-'):str(v) for k,v in attrs.items()})
    if value is not None:e.text=str(value)
    return e

def source_matches(note,sources):
    return any(all(note[f]==s[f] for f in ('track','channel','program') if f in s) for s in sources)

def spellings(fifths,minor=False):
    naturals={'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11};alts={k:0 for k in naturals}
    for letter in ('FCGDAEB'[:fifths] if fifths>0 else 'BEADGCF'[:-fifths]):alts[letter]=1 if fifths>0 else -1
    result={((pc+alts[letter])%12):(letter,alts[letter]) for letter,pc in naturals.items()}
    default={0:('C',0),1:('C',1),2:('D',0),3:('E',-1),4:('E',0),5:('F',0),6:('F',1),7:('G',0),8:('A',-1),9:('A',0),10:('B',-1),11:('B',0)}
    if fifths>=3:default.update({3:('D',1),8:('G',1),10:('A',1)})
    if fifths<=-3:default.update({1:('D',-1),6:('G',-1)})
    for pc,v in default.items():result.setdefault(pc,v)
    # A minor-key leading tone keeps its correct diatonic spelling.
    if minor:
        tonic=(9+7*fifths)%12;seventh=(tonic+10)%12
        if seventh in result:
            letter,alter=result[seventh];result[(tonic+11)%12]=(letter,alter+1)
    return result

def pitch_xml(parent,pitch,pitchmap):
    step,alter=pitchmap[pitch%12];base={'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}[step]
    octave=(pitch-base-alter)//12-1
    p=sub(parent,'pitch');sub(p,'step',step)
    if alter:sub(p,'alter',alter)
    sub(p,'octave',octave)

def meta(parsed):
    summary=summarize(parsed)
    meters=summary['time_signatures'] or [{'tick':0,'numerator':4,'denominator':4}]
    tempos=summary['tempo_changes'] or [{'tick':0,'bpm':120}]
    return summary,meters,tempos

def melody_events(parsed,config,origin,end):
    ppq=parsed['ppq'];sources=config['melody_sources']
    selected=sorted([n for n in parsed['notes'] if (not config.get('velocity_filter') or n['velocity'] in config['velocity_filter']) and any(source_matches(n,[s]) and s.get('start_quarter',-1)<=n['start']/ppq<s.get('end_quarter',float('inf')) and s.get('min_pitch',0)<=n['pitch']<=s.get('max_pitch',127) for s in sources)],key=lambda n:(n['start'],n['pitch']))
    if len(selected)<20:raise NeedsReview('Poucas notas na fonte de melodia selecionada.')
    main_count=len(selected);solo_count=0
    grace_notes=config.get('grace_notes',[])
    selected=[n for n in selected if not any(abs(n['start']/ppq-g['start_quarter'])<.001 and n['pitch']==g['pitch'] for g in grace_notes)]
    if config.get('solo_sources'):
        covered=[]
        for n in selected:
            if covered and n['start']<=covered[-1][1]:covered[-1][1]=max(covered[-1][1],n['end'])
            else:covered.append([n['start'],n['end']])
        gaps=[];cursor=origin/DIV*ppq
        for a,b in covered:
            if a-cursor>=ppq:gaps.append((cursor,a))
            cursor=max(cursor,b)
        if end/DIV*ppq-cursor>=ppq:gaps.append((cursor,end/DIV*ppq))
        for n in parsed['notes']:
            matches=[s for s in config['solo_sources'] if source_matches(n,[s]) and s.get('start_quarter',-1)<=n['start']/ppq<s.get('end_quarter',float('inf'))]
            if not matches:continue
            gap=next(((a,b) for a,b in gaps if a<=n['start']<b),None)
            if gap:
                added=dict(n,end=min(n['end'],gap[1]),_role='solo')
                if added['end']>added['start']:selected.append(added);solo_count+=1
        selected.sort(key=lambda n:(n['start'],n['pitch']))
    # Co-sounding notes in a lead source are reduced to its upper melodic line.
    groups=[]
    for n in selected:
        if not groups or n['start']-groups[-1][0]['start']>ppq*config.get('onset_cluster_quarters',.04):groups.append([n])
        else:groups[-1].append(n)
    top=[max(g,key=lambda n:n['pitch']) for g in groups]
    bybeat=defaultdict(list)
    for n in top:bybeat[int(n['start']//ppq)].append(n['start']/ppq%1)
    grids={};triplet_beats=[]
    for beat,phases in bybeat.items():
        binary=sum(abs(p-round(p*4)/4) for p in phases)
        ternary=sum(abs(p-round(p*3)/3) for p in phases)
        has1=any(abs(p-1/3)<.06 for p in phases);has2=any(abs(p-2/3)<.06 for p in phases)
        if has1 and has2 and ternary+.055<binary:
            choices=(3,6,12);triplet_beats.append(beat)
        elif any(abs(p-round(p*8)/8)<.025 and abs(p-round(p*4)/4)>.08 for p in phases):choices=(8,16)
        else:choices=(4,8,16)
        grids[beat]=choices[-1]
        for div in choices:
            if len({round(p*div) for p in phases})==len(phases):grids[beat]=div;break
    def snap(t):
        q=t/ppq;beat=math.floor(q);div=grids.get(beat,4)
        # Near a beat boundary use the shared endpoint.
        return round((beat+round((q-beat)*div)/div)*DIV)
    onsetmap={};collision=0
    for n in top:
        s=snap(n['start'])-origin
        if not 0<=s<end-origin:continue
        if s in onsetmap:collision+=1
        if s not in onsetmap or n['velocity']>onsetmap[s]['velocity']:onsetmap[s]=n
    if collision>max(3,len(top)*.008):raise NeedsReview(f'{collision} ataques colidem após quantização; requer revisão rítmica individual.')
    events=[];errors=[];onsets=sorted(onsetmap)
    for i,s in enumerate(onsets):
        n=onsetmap[s];next_s=onsets[i+1] if i+1<len(onsets) else end-origin
        stop=snap(n['end'])-origin
        if i+1<len(onsets) and onsetmap[next_s]['start']-n['end']<=ppq*.28:stop=next_s
        beat=int((s+origin)//DIV);minimum=DIV//grids.get(beat,4)
        stop=min(next_s,end-origin,max(s+minimum,stop))
        if stop>s:
            event={'start':s,'end':stop,'pitch':n['pitch']}
            gs=[g['pitch'] for g in grace_notes if abs(n['start']/ppq-g['target_quarter'])<.001]
            if gs:event['graces']=gs
            events.append(event)
        errors.append(abs(s+origin-n['start']/ppq*DIV)/DIV)
    if len({n['pitch'] for n in events})<3:raise NeedsReview('Fonte selecionada não contém alturas suficientes para uma melodia identificável.')
    assert sum(len(n.get('graces',[])) for n in events)==len(grace_notes),'Grace target lost during grouping'
    return events,{b-int(origin//DIV):v for b,v in grids.items()}, {'raw_notes':len(selected),'principal_source_notes':main_count,'grace_notes_preserved':len(grace_notes),'solo_notes_added':solo_count,'simultaneous_notes_reduced':len(selected)-len(top),'melody_attacks':len(events),'quantized_attack_collisions':collision,'triplet_beats_original':triplet_beats,'onset_adjustment_max_quarters':max(errors),'onset_adjustment_mean_quarters':sum(errors)/len(errors)}

def make_bars(parsed,meters):
    ppq=parsed['ppq'];pitched=[n for n in parsed['notes'] if n['channel']!=10 and 20<=n['pitch']<=108 and n['program']<112]
    if not pitched:raise NeedsReview('Sem notas melódicas válidas.')
    initial=meters[0];barq=initial['numerator']*4/initial['denominator']
    origin=math.floor(min(n['start'] for n in pitched)/ppq/barq)*round(barq*DIV)
    last=math.ceil(max(n['end'] for n in pitched)/ppq*DIV)
    changes=sorted({round(m['tick']/ppq*DIV):(m['numerator'],m['denominator']) for m in meters}.items())
    current=(initial['numerator'],initial['denominator']);bars=[];start=origin
    while start<last:
        for t,m in changes:
            if t<=start:current=m
        size=round(current[0]*4/current[1]*DIV)
        nextchange=min([t for t,m in changes if t>start] or [last+size])
        stop=min(start+size,nextchange)
        if stop<=start:raise NeedsReview('Mudança métrica inválida.')
        bars.append({'start':start-origin,'end':stop-origin,'meter':current,'partial':stop-start!=size})
        start=stop
    if len(bars)>700:raise NeedsReview('Duração excessiva ou nota sem término; requer verificação.')
    return origin,start,bars

TYPES={3840:('whole',0),2880:('half',1),1920:('half',0),1440:('quarter',1),960:('quarter',0),720:('eighth',1),480:('eighth',0),360:('16th',1),240:('16th',0),120:('32nd',0),60:('64th',0)}

def pieces(start,end,grids):
    t=start
    while t<end:
        beat=t//DIV;beatend=(beat+1)*DIV;mode=grids.get(beat,4)
        if mode%3==0:
            d=max([x for x in (640,320,160,80) if x<=min(end,beatend)-t] or [0])
            if not d:raise NeedsReview(f'Duração de tercina indefinida no tempo {t/DIV:.3f}.')
            yield t,d,True;t+=d;continue
        triplenext=min([b*DIV for b,v in grids.items() if v%3==0 and b*DIV>t] or [end])
        limit=min(end,triplenext)
        if t%DIV:limit=min(limit,beatend)
        candidates=[d for d in TYPES if d<=limit-t]
        if not candidates:raise NeedsReview(f'Duração irregular de {limit-t} unidades.')
        d=max(candidates);yield t,d,False;t+=d

def emit_note(measure,start,dur,triplet,event,ps,full_rest=False):
    if event is not None and start==event['start']:
        for pitch in event.get('graces',[]):
            g=sub(measure,'note');sub(g,'grace',slash='yes');pitch_xml(g,pitch,ps);sub(g,'voice',1);sub(g,'type','eighth')
    n=sub(measure,'note')
    if event is None:sub(n,'rest',**({'measure':'yes'} if full_rest else {}))
    else:pitch_xml(n,event['pitch'],ps)
    sub(n,'duration',dur)
    tiein=event is not None and start>event['start'];tieout=event is not None and start+dur<event['end']
    if tiein:sub(n,'tie',type='stop')
    if tieout:sub(n,'tie',type='start')
    sub(n,'voice',1)
    if not full_rest:
        typ,dots=({640:('quarter',0),320:('eighth',0),160:('16th',0),80:('32nd',0)}[dur] if triplet else TYPES[dur])
        sub(n,'type',typ)
        for _ in range(dots):sub(n,'dot')
    if triplet:
        tm=sub(n,'time-modification');sub(tm,'actual-notes',3);sub(tm,'normal-notes',2);sub(tm,'normal-type','eighth')
    if tiein or tieout or triplet:
        nt=sub(n,'notations')
        if tiein:sub(nt,'tied',type='stop')
        if tieout:sub(nt,'tied',type='start')
        if triplet and start%DIV==0:sub(nt,'tuplet',type='start',number=1,bracket='no')
        if triplet and (start+dur)%DIV==0:sub(nt,'tuplet',type='stop',number=1)

KINDS={'':'major','m':'minor','7':'dominant','maj7':'major-seventh','m7':'minor-seventh','mMaj7':'major-minor','dim':'diminished','dim7':'diminished-seventh','m7b5':'half-diminished','aug':'augmented','sus2':'suspended-second','sus4':'suspended-fourth','6':'major-sixth','m6':'minor-sixth','9':'dominant-ninth','maj9':'major-ninth','m9':'minor-ninth','7sus4':'dominant','add9':'major','madd9':'minor'}

def emit_harmony(measure,event,barstart,ps):
    h=sub(measure,'harmony',placement='above');rt=sub(h,'root');step,alt=ps[event['root']]
    if event.get('root_spelling'):
        spelling=event['root_spelling'];step=spelling[0];alt=spelling.count('#')-spelling.count('b')
    sub(rt,'root-step',step)
    if alt:sub(rt,'root-alter',alt)
    q=event['quality'];sub(h,'kind',KINDS[q],text=q)
    if q in ('add9','madd9'):
        d=sub(h,'degree');sub(d,'degree-value',9);sub(d,'degree-alter',0);sub(d,'degree-type','add')
    if q=='7sus4':
        d=sub(h,'degree');sub(d,'degree-value',3);sub(d,'degree-alter',0);sub(d,'degree-type','subtract')
        d=sub(h,'degree');sub(d,'degree-value',4);sub(d,'degree-alter',0);sub(d,'degree-type','add')
    offset=event['tick']-barstart
    if offset:sub(h,'offset',offset)

def layout_bars(bars,notes,chords,max_chords=3):
    lines=[];current=[];notecount=chordcount=quarters=0
    for i,b in enumerate(bars):
        nc=sum(b['start']<=n['start']<b['end'] for n in notes)
        cc=sum(b['start']<=c['tick']<b['end'] for c in chords)
        q=(b['end']-b['start'])/DIV
        if current and (len(current)>=6 or quarters+q>12 or notecount+nc>38 or chordcount+cc>max_chords):
            lines.append(current);current=[];notecount=chordcount=quarters=0
        current.append(i);notecount+=nc;chordcount+=cc;quarters+=q
    if current:lines.append(current)
    pages=math.ceil(len(lines)/7);base,extra=divmod(len(lines),pages)
    starts={};cursor=0
    for page in range(pages):
        for j in range(base+(page<extra)):
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
    summary,meters,tempos=meta(parsed);origin,end,bars=make_bars(parsed,meters)
    notes,grids,note_report=melody_events(parsed,config,origin,end)
    transpose=config.get('melody_transpose_semitones',0)
    assert transpose%12==0,'Only octave placement may change without changing the key'
    if transpose:
        for n in notes:
            n['pitch']+=transpose
            if 'graces' in n:n['graces']=[p+transpose for p in n['graces']]
        note_report['octave_transposition_semitones']=transpose
        assert all(0<=n['pitch']<=127 for n in notes)
    key=harmony.get('key') or {'fifths':0,'minor':False};fifths=key.get('fifths',0);ps=spellings(fifths,key.get('minor',False))
    chords=[]
    for ev in harmony['events']:
        tick=round(ev['tick']/parsed['ppq']*DIV)-origin
        beat=tick//DIV;grid=grids.get(beat,4);snapped=round(beat*DIV+round((tick-beat*DIV)/(DIV/grid))*DIV/grid)
        adjustment=(snapped-tick)/DIV;tick=snapped
        if 0<=tick<end-origin:chords.append(dict(ev,tick=tick,placement_adjustment_quarters=adjustment))
    unique_chords={c['tick']:c for c in chords};chords=sorted(unique_chords.values(),key=lambda c:c['tick'])
    if not chords:raise NeedsReview('Nenhuma cifra suficientemente apoiada pelas notas do acompanhamento.')
    if len(chords)>len(bars)*3:raise NeedsReview('Harmonia excessivamente densa; revisar detecção de acordes.')
    starts,lines=layout_bars(bars,notes,chords,config.get('max_chords_per_system',3))
    # Re-state the active chord at each new system, as in the approved pilot.
    for index in starts:
        tick=bars[index]['start'];prior=[c for c in chords if c['tick']<=tick]
        if prior and not any(c['tick']==tick for c in chords):chords.append(dict(max(prior,key=lambda c:c['tick']),tick=tick,reminder=True))
    chords.sort(key=lambda c:c['tick'])
    root=E.Element('score-partwise',version='4.0');work=sub(root,'work');sub(work,'work-title',title)
    ident=sub(root,'identification');enc=sub(ident,'encoding');sub(enc,'software','Preparação de melodia e cifras — MuseScore Studio')
    defaults=sub(root,'defaults');scale=sub(defaults,'scaling');sub(scale,'millimeters',7);sub(scale,'tenths',40)
    pl=sub(defaults,'page-layout');sub(pl,'page-height',1697.14);sub(pl,'page-width',1200)
    margins=sub(pl,'page-margins',type='both')
    for tag,val in [('left-margin',80),('right-margin',80),('top-margin',70),('bottom-margin',70)]:sub(margins,tag,val)
    printed_title=title
    if len(title)>68:
        spaces=[m.start() for m in re.finditer(' ',title)]
        split=min(spaces,key=lambda i:abs(i-len(title)/2)) if spaces else len(title)//2
        printed_title=title[:split]+'\n'+title[split+1:]
    for y,txt,size in [(1615,printed_title,20 if len(title)<60 else 16),(1575,'Melodia, cifras e diagramas de teclado • 2ª inversão',10)]:
        cr=sub(root,'credit',page=1);sub(cr,'credit-words',txt,default_x=600,default_y=y,justify='center',valign='top',font_size=size)
    parts=sub(root,'part-list');p=sub(parts,'score-part',id='P1');sub(p,'part-name','Teclado');sub(p,'part-abbreviation','')
    si=sub(p,'score-instrument',id='I1');sub(si,'instrument-name','Piano');mi=sub(p,'midi-instrument',id='I1');sub(mi,'midi-channel',1);sub(mi,'midi-program',1)
    part=sub(root,'part',id='P1');lastmeter=None;printedtempo=None;lastkey=None
    for i,b in enumerate(bars):
        measure=sub(part,'measure',number=i+1,**({'implicit':'yes'} if b['partial'] else {}))
        if i in starts:sub(measure,'print',**({'new-page':'yes'} if starts[i]=='page' else ({'new-system':'yes'} if i else {})))
        activekey=key
        for kc in harmony.get('key_changes',[]):
            if round(kc['tick']/parsed['ppq']*DIV)<=origin+b['start']:activekey=kc
        currentkey=(activekey['fifths'],activekey.get('minor',False));ps=spellings(*currentkey)
        if i==0 or b['meter']!=lastmeter or currentkey!=lastkey:
            at=sub(measure,'attributes')
            if i==0:sub(at,'divisions',DIV)
            if currentkey!=lastkey:
                k=sub(at,'key');sub(k,'fifths',currentkey[0]);sub(k,'mode','minor' if currentkey[1] else 'major')
            if b['meter']!=lastmeter:
                ts=sub(at,'time');sub(ts,'beats',b['meter'][0]);sub(ts,'beat-type',b['meter'][1])
            if i==0:
                sub(at,'staves',1);cl=sub(at,'clef');sub(cl,'sign','G');sub(cl,'line',2)
        lastmeter=b['meter']
        lastkey=currentkey
        active_tempo=max([t for t in tempos if round(t['tick']/parsed['ppq']*DIV)<=origin+b['start']] or [tempos[0]],key=lambda t:t['tick'])
        ts=[dict(active_tempo,tick=origin+b['start'])] if i==0 else []
        ts.extend(t for t in tempos if origin+b['start']<=round(t['tick']/parsed['ppq']*DIV)<origin+b['end'] and not (i==0 and round(t['tick']/parsed['ppq']*DIV)==origin))
        for t in ts:
            bpm=round(t['bpm'],2);di=sub(measure,'direction',placement='above')
            if printedtempo is None or abs(bpm-printedtempo)>=3:
                dt=sub(di,'direction-type');mt=sub(dt,'metronome');sub(mt,'beat-unit','quarter');sub(mt,'per-minute',round(bpm) if abs(bpm-round(bpm))<.1 else bpm);printedtempo=bpm
            else:dt=sub(di,'direction-type');sub(dt,'words','',print_object='no')
            offset=round(t['tick']/parsed['ppq']*DIV)-origin-b['start'] if i else 0
            if offset:sub(di,'offset',offset)
            sub(di,'sound',tempo=bpm)
        for c in chords:
            if b['start']<=c['tick']<b['end']:emit_harmony(measure,c,b['start'],ps)
        selected=[n for n in notes if n['end']>b['start'] and n['start']<b['end']];sequence=[];cursor=b['start']
        for n in selected:
            a=max(n['start'],b['start']);z=min(n['end'],b['end'])
            if a>cursor:sequence.append((cursor,a,None))
            sequence.append((a,z,n));cursor=z
        if cursor<b['end']:sequence.append((cursor,b['end'],None))
        for a,z,n in sequence:
            inner_chords=sorted({c['tick'] for c in chords if a<c['tick']<z})
            if n is None and a==b['start'] and z==b['end'] and not inner_chords:emit_note(measure,a,z-a,False,None,ps,True)
            else:
                bounds=[a]+inner_chords+[z]
                for left,right in zip(bounds,bounds[1:]):
                    for start,dur,tr in pieces(left,right,grids):emit_note(measure,start,dur,tr,n,ps)
        assert sum(int(n.findtext('duration','0')) for n in measure.findall('note'))==b['end']-b['start']
        if i+1 in config.get('tuplet_bracket_measures',[]):
            # Explicit brackets keep adjacent triplet labels centered over their
            # separate groups after a very wide measure is respaced by MuseScore.
            for tuplet in measure.findall('.//tuplet'):
                if tuplet.get('type')=='start':tuplet.set('bracket','yes');tuplet.set('placement','above')
        if i==len(bars)-1:bl=sub(measure,'barline',location='right');sub(bl,'bar-style','light-heavy')
    E.indent(root);xml=out/'melodia.musicxml';E.ElementTree(root).write(xml,encoding='utf-8',xml_declaration=True)
    report={'source':parsed['path'],'source_sha256':parsed['sha256'],'title':title,'origin_removed_quarters':origin/DIV,'measures':len(bars),'systems':len(lines),'pages_planned':math.ceil(len(lines)/7),'key':key,'melody_sources':config['melody_sources'],'chord_changes':sum(not c.get('reminder') for c in chords),'chord_symbols':len(chords),'melody_analysis':note_report,'notes':notes,'chords':chords,'bars':bars,'system_starts':starts,'harmonic_warnings':harmony.get('warnings',[])}
    if config.get('reserve_high_melody'):
        report['high_melody_layout']=high_melody_layout(part,lines)
        report['tuplet_bracket_measures']=config.get('tuplet_bracket_measures',[])
    return xml,report

def keyboard_asset(rootpc,quality):
    intervals=CHORD_INTERVALS[quality];fifth=next(x for x in (7,6,8) if x in intervals)
    bass=53+((rootpc+fifth-53)%12)
    pitches=sorted(bass+(rootpc+x-bass)%12 for x in intervals)
    if max(pitches)>71:pitches=[p-12 for p in pitches]
    assert 48<=min(pitches)<max(pitches)<=71
    label=str(rootpc)+quality;svg=E.fromstring(svg_keyboard(label,pitches))
    svg.find(NS+'title').text='Acorde em segunda inversão'
    svg.find(NS+'desc').text='Quinta no grave; notas MIDI: '+','.join(map(str,pitches))
    data=E.tostring(svg,encoding='utf-8',xml_declaration=True)
    return md4(data)+'.svg',data,pitches

def call_musescore(args,log,timeout=90):
    print(json.dumps({'kind':'progress','message':'MuseScore: '+log.stem.replace('_',' ')+'…'},ensure_ascii=False),flush=True)
    p=subprocess.run([EXE]+args,capture_output=True,text=True,errors='replace',timeout=timeout)
    log.write_text(p.stdout+'\n'+p.stderr,encoding='utf8')
    if p.returncode:raise NeedsReview(f'MuseScore retornou {p.returncode}; veja {log.name}.')

def page_order(path):return int(path.stem.rsplit('-',1)[-1])

def repair_spacing(final,report,out):
    """Use the rendered geometry to reserve space, preserving musical content."""
    history=[]
    for attempt in range(10):
        pages=[analyze_svg(p) for p in sorted(out.glob('qa-*.svg'),key=page_order)]
        offset=0;image_add=defaultdict(float);end_add=defaultdict(float);move_image=defaultdict(float);problems=0
        for page in pages:
            if page['warnings']:raise NeedsReview('Geometria de cifras não pôde ser pareada: '+str(page['warnings']))
            diagrams=page['diagrams'];harmonies=page['harmonies']
            for overlap in page['overlaps']:
                problems+=1
                if overlap['type']=='harmony-image':
                    h=harmonies[overlap['harmony']];d=diagrams[overlap['diagram']];scale=(d['bbox'][2]-d['bbox'][0])/14
                    if overlap.get('own_pair'):
                        move_image[offset+overlap['diagram']]=max(move_image[offset+overlap['diagram']],(h['bbox'][2]-d['bbox'][0])/scale+1)
                    else:
                        target=h.get('diagram_index')
                        if target is None:raise NeedsReview('Cifra sem diagrama correspondente na verificação geométrica.')
                        image_add[offset+target]=max(image_add[offset+target],(d['bbox'][2]-h['bbox'][0])/scale+1.2)
                else:
                    first,second=sorted((overlap['a'],overlap['b']));d=diagrams[first];scale=(d['bbox'][2]-d['bbox'][0])/14
                    image_add[offset+second]=max(image_add[offset+second],overlap['width']/scale+1.2)
            for clip in page['clipped']:
                if clip['type']!='image' or clip['edge']!='staff_right':continue
                problems+=1;idx=offset+clip['index'];d=diagrams[clip['index']];scale=(d['bbox'][2]-d['bbox'][0])/14
                tick=report['chords'][idx]['tick'];measure_index=next(i for i,b in enumerate(report['bars']) if b['start']<=tick<b['end'])
                end_add[measure_index]=max(end_add[measure_index],clip['overflow']/scale+1.2)
            offset+=len(diagrams)
        assert offset==report['chord_symbols'],('Rendered diagram count changed',offset,report['chord_symbols'])
        history.append({'pass':attempt,'overlaps':sum(len(p['overlaps']) for p in pages),'margin_overflows':sum(len(p['clipped']) for p in pages)})
        if not problems:
            report['geometric_layout_qa']={'passed':True,'passes':history,'pages':len(pages)}
            (out/'geometria_verificada.json').write_text(json.dumps(pages,ensure_ascii=False),encoding='utf8')
            return
        if attempt==9:raise NeedsReview('Sobreposição residual após ajuste de diagrama: '+json.dumps(history))
        with zipfile.ZipFile(final) as z:contents={n:z.read(n) for n in z.namelist()}
        name=next(n for n in contents if n.endswith('.mscx'));root=E.fromstring(contents[name])
        for i,amount in image_add.items():report['image_spacing'][i]+=amount
        for i,amount in end_add.items():report['end_spacing'][i]=report['end_spacing'].get(i,0)+amount
        count=0
        for mi,m in enumerate(root.findall('Score/Staff/Measure')):
            for voice in m.findall('voice'):
                for seg in list(voice.findall('Segment')):voice.remove(seg)
                for im in list(voice.findall('Image')):
                    if count in move_image:
                        off=im.find('offset');off.set('x',str(float(off.get('x','0'))+move_image[count]))
                    if report['image_spacing'][count]>0:
                        seg=E.Element('Segment');sub(seg,'leadingSpace',report['image_spacing'][count]);voice.insert(list(voice).index(im)+1,seg)
                    count+=1
            if mi in report['end_spacing']:
                voice=m.find('voice')
                for bl in list(voice.findall('BarLine')):voice.remove(bl)
                div=int(root.findtext('Score/Division','480'));sub(voice,'tick',round(report['bars'][mi]['end']/DIV*div))
                bl=sub(voice,'BarLine');sub(bl,'subtype','end' if mi+1==len(report['bars']) else 'normal')
                seg=sub(voice,'Segment');sub(seg,'leadingSpace',report['end_spacing'][mi])
        contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
        pre=out/'espacamento_preparado.mscz'
        with zipfile.ZipFile(pre,'w',zipfile.ZIP_DEFLATED) as z:
            for name,data in contents.items():z.writestr(name,data)
        call_musescore(['-o',str(final),str(pre)],out/'ajuste_espacamento.log')
        for pattern in ('pagina-*.png','qa-*.svg'):
            for old in out.glob(pattern):old.unlink()
        call_musescore(['-r','120','-j',str(out/'render.json')],out/'render.log',150)

def convert_score(xml,report,out,filename):
    style=E.parse(WORK/'garoto_melodia_cifras'/'melodia-cifras.mss').getroot()
    vertical=report.get('high_melody_layout',[])
    vertical_by_measure={i:row for row in vertical for i in range(row['first_measure_index'],row['last_measure_index']+1)}
    style_values={'Spatium':'1.65','minSystemDistance':'12','maxSystemDistance':'20','frameSystemDistance':'11','createMultiMeasureRests':'0'}
    if vertical:
        top=max(-row['image_y_spatium'] for row in vertical)
        # Image offsets do not reserve a staff skyline in MuseScore. Keep an
        # explicit inter-system and page-top allowance for their raised positions.
        distance=max(12,top+3)
        style_values.update(minSystemDistance=str(distance),maxSystemDistance=str(max(20,distance+4)),staffUpperBorder=str(max(7,top+1)))
        report['high_melody_spacing_style']={k:style_values[k] for k in ('minSystemDistance','maxSystemDistance','staffUpperBorder')}
    for tag,val in style_values.items():
        n=style.find('Style/'+tag)
        if n is None:n=sub(style.find('Style'),tag)
        n.text=val
    st=out/'estilo.mss';E.ElementTree(style).write(st,encoding='utf-8',xml_declaration=True)
    initial=out/'melodia_importada.mscz';call_musescore(['-S',str(st),'-o',str(initial),str(xml)],out/'importacao.log')
    with zipfile.ZipFile(initial) as z:contents={n:z.read(n) for n in z.namelist()}
    name=next(n for n in contents if n.endswith('.mscx'));root=E.fromstring(contents[name]);harmonies=root.findall('.//Harmony')
    if len(harmonies)!=len(report['chords']):raise NeedsReview(f'MuseScore importou {len(harmonies)} cifras de {len(report["chords"])}.')
    assets={};asset_manifest={};events=iter(report['chords']);images=0;previous_chord=None
    report['image_spacing']=[0.0]*len(report['chords']);report['end_spacing']={}
    for measure_index,measure in enumerate(root.findall('Score/Staff/Measure')):
        placement=vertical_by_measure.get(measure_index)
        lift=placement['lift_spatium'] if placement else 0
        if measure_index in report['system_starts']:previous_chord=None
        for voice in measure.findall('voice'):
            for harmony in list(voice.findall('Harmony')):
                c=next(events);path,data,pitches=keyboard_asset(c['root'],c['quality']);assets['Pictures/'+path]=data
                asset_manifest[path]={'root':c['root'],'quality':c['quality'],'pitches':pitches}
                im=E.Element('Image');E.SubElement(im,'offset',x=str(4.5+max(0,len(c['quality'])-1)*.8),y=str(-6.7-lift));sub(im,'path',path);sub(im,'autoScale',0);E.SubElement(im,'size',w='14',h=str(14*84/282));sub(im,'lockAspectRatio',1);sub(im,'sizeIsSpatium',1)
                if placement:
                    # One shared row keeps each chord symbol aligned with its
                    # keyboard instead of letting harmony autoplace independently.
                    off=harmony.find('offset')
                    if off is None:off=E.SubElement(harmony,'offset')
                    off.set('x',off.get('x','0'));off.set('y',str(placement['harmony_offset_y_spatium']))
                    for element in (harmony,im):
                        auto=element.find('autoplace')
                        if auto is None:auto=E.SubElement(element,'autoplace')
                        auto.text='0'
                voice.insert(list(voice).index(harmony)+1,im);images+=1
                if previous_chord is not None:
                    distance=(c['tick']-previous_chord['tick'])/DIV
                    if distance<3:
                        segment=E.Element('Segment');sub(segment,'leadingSpace',round((3-distance)*6,3))
                        voice.insert(list(voice).index(im)+1,segment)
                        report['image_spacing'][images-1]=round((3-distance)*6,3)
                previous_chord=c
            for tempo in voice.findall('Tempo'):
                off=tempo.find('offset')
                if off is None:off=E.SubElement(tempo,'offset')
                off.set('x','0');off.set('y',str(-8.5-lift));sub(tempo,'autoplace',0)
        # The last chord's diagram must have real room before the system edge.
        last_on_line=(measure_index+1 in report['system_starts'] or measure_index+1==len(report['bars']))
        if last_on_line and previous_chord is not None:
            endtick=report['bars'][measure_index]['end'];distance=(endtick-previous_chord['tick'])/DIV
            if distance<3:
                voice=measure.find('voice')
                for barline in list(voice.findall('BarLine')):voice.remove(barline)
                filediv=int(root.findtext('Score/Division','480'))
                sub(voice,'tick',round(endtick/DIV*filediv))
                bl=sub(voice,'BarLine');sub(bl,'subtype','end' if measure_index+1==len(report['bars']) else 'normal')
                segment=sub(voice,'Segment');sub(segment,'leadingSpace',18)
                report['end_spacing'][measure_index]=18.0
    vbox=root.find('Score/Staff/VBox')
    if vbox is not None:
        gap=vbox.find('bottomGap')
        if gap is None:gap=sub(vbox,'bottomGap')
        gap.text=str(11+(vertical[0]['lift_spatium'] if vertical else 0))
    contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
    container=E.fromstring(contents['META-INF/container.xml'])
    for path in assets:sub(container.find('rootfiles'),'rootfile',**{'full-path':path})
    contents['META-INF/container.xml']=E.tostring(container,encoding='utf-8',xml_declaration=True);contents.update(assets)
    pre=out/'com_diagramas_preparado.mscz'
    with zipfile.ZipFile(pre,'w',zipfile.ZIP_DEFLATED) as z:
        for name,data in contents.items():z.writestr(name,data)
    final=out/(filename+'.mscz');call_musescore(['-o',str(final),str(pre)],out/'gravacao.log')
    assert out.resolve().is_relative_to(BATCH.resolve())
    for pattern in ('pagina-*.png','qa-*.svg'):
        for old in out.glob(pattern):old.unlink()
    job=[{'in':str(final),'out':[str(out/'pagina.png'),str(out/'qa.svg'),str(out/'reaberto.musicxml')]}]
    (out/'render.json').write_text(json.dumps(job),encoding='utf8');call_musescore(['-r','120','-j',str(out/'render.json')],out/'render.log',150)
    repair_spacing(final,report,out)
    with zipfile.ZipFile(final) as z:
        native=E.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.mscx'))))
        assert len(native.findall('Score/Staff'))==1
        assert len(native.findall('.//Image'))==images
        for im in native.findall('.//Image'):
            path='Pictures/'+Path(im.findtext('path')).name;assert path in z.namelist();assert Path(path).stem==md4(z.read(path))
    reopened=E.parse(out/'reaberto.musicxml');allnotes=[];cursor=0
    # Compare the realized melody after native save/reopen, collapsing tie continuations.
    for m,b in zip(reopened.findall('part/measure'),report['bars']):
        div=m.findtext('attributes/divisions')
        if div is not None:reopened_div=int(div)
        local=0
        for n in m.findall('note'):
            if n.find('grace') is not None:continue
            dur=round(int(n.findtext('duration','0'))/reopened_div*DIV)
            if n.find('pitch') is not None and not any(t.get('type')=='stop' for t in n.findall('tie')):
                step=n.findtext('pitch/step');alt=int(n.findtext('pitch/alter','0'));octave=int(n.findtext('pitch/octave'))
                pitch=(octave+1)*12+{'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}[step]+alt
                allnotes.append((b['start']+local,pitch))
            if n.find('chord') is None:local+=dur
        assert local==b['end']-b['start'],('measure length changed',local,b)
    assert len(reopened.findall('part/measure'))==report['measures']
    assert allnotes==[(n['start'],n['pitch']) for n in report['notes']],'Melody changed during MuseScore import'
    assert len(reopened.findall('.//grace'))==report['melody_analysis']['grace_notes_preserved']
    pages=sorted(out.glob('pagina-*.png'),key=page_order);assert pages
    columns=min(3,len(pages));rows=math.ceil(len(pages)/columns)
    canvas=Image.new('RGB',(columns*350,rows*525),'#dedede');draw=ImageDraw.Draw(canvas)
    for i,path in enumerate(pages):
        im=Image.open(path).convert('RGB');im.thumbnail((330,480));x=10+i%columns*350;y=28+i//columns*525;canvas.paste(im,(x,y));draw.text((x,y-20),str(i+1),fill='black')
    canvas.save(out/'previa-geral.png')
    report.update(final_path=str(final),images=images,diagram_assets=asset_manifest,pages=len(pages),native_reopened=True,melody_attacks_verified=len(allnotes),final_sha256=hashlib.sha256(final.read_bytes()).hexdigest())
    return final

def prepare(config):
    title=re.sub(r'[_]+',' ',Path(config['filename']).stem);title=re.sub(r'\s+',' ',title).strip()
    index=config['index'];stem=f'{index+1:03d} - '+re.sub(r'[<>:"/\\|?*]','',title)[:83].rstrip(' .')
    out=BATCH/f'{index+1:03d}';out.mkdir(exist_ok=True)
    parsed=parse_midi(config['source_path'])
    harmony=analyze_harmony(parsed,config['melody_sources'])
    for overridefile in WORK.glob('batch_harmony_overrides*.json'):
        entries=json.loads(overridefile.read_text(encoding='utf8'))
        if isinstance(entries,dict):
            entries=entries.get('entries',entries.get('files',list(entries.values())))
        override=next((x for x in entries if x['index']==index),None)
        if override is None:continue
        if override.get('deliverable') is False:raise NeedsReview(override.get('reason','Harmonia requer revisão específica.'))
        if override.get('key'):harmony['key']=override['key']
        if override.get('key_changes'):harmony['key_changes']=override['key_changes']
        if override.get('events'):harmony['events']=override['events']
        for change in ([] if override.get('events') else override.get('event_overrides',[])):
            found=[c for c in harmony['events'] if c['tick']==change['tick']]
            if change.get('add') and not found:
                c={'tick':change['tick'],'root':change['root'],'quality':change['quality'],'confidence':.85 if change.get('context_inferred') else 1.0,'evidence':{'method':'manual_context_review','reason':change.get('reason','')}}
                harmony['events'].append(c);found=[c]
            for c in found:
                if change.get('omit'):harmony['events'].remove(c);continue
                for field in ('root','quality'):c[field]=change.get(field,c[field])
                if 'root_spelling' in change:c['root_spelling']=change['root_spelling']
                if 'new_tick' in change:c['tick']=change['new_tick']
                c['pitch_classes']=sorted((c['root']+x)%12 for x in CHORD_INTERVALS[c['quality']])
                c['manual_review']=change.get('reason','')
        collapsed=[]
        for c in sorted(harmony['events'],key=lambda c:c['tick']):
            if collapsed and (c['root'],c['quality'])==(collapsed[-1]['root'],collapsed[-1]['quality']):continue
            collapsed.append(c)
        harmony['events']=collapsed;harmony['manual_review']=override
        harmony['review_required']=not override.get('review_complete',False)
    (out/'harmonia_fonte.json').write_text(json.dumps(harmony,ensure_ascii=False,indent=2),encoding='utf8')
    xml,report=build_musicxml(parsed,config,harmony,out,title)
    report['triage']=config
    final=convert_score(xml,report,out,stem+' - cifras 2a inversao')
    (out/'validacao.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
    return {'index':index,'filename':config['filename'],'status':'generated_pending_visual_review','path':str(final),'preview':str(out/'previa-geral.png'),'pages':report['pages'],'measures':report['measures'],'notes':report['melody_attacks_verified'],'harmony_warnings':harmony.get('warnings',[])}

if __name__=='__main__':
    import sys
    config=json.loads(Path(sys.argv[1]).read_text(encoding='utf8'))
    if isinstance(config,dict):config=config.get('entries',config.get('files',[config]))
    requested=set(map(int,sys.argv[2:])) if len(sys.argv)>2 else None
    results=[]
    for item in config:
        if item.get('status')!='ready' or (requested is not None and item['index'] not in requested):continue
        try:result=prepare(item)
        except Exception as e:
            import traceback
            result={'index':item['index'],'filename':item['filename'],'status':'needs_review','reason':str(e),'traceback':traceback.format_exc()}
        results.append(result);print(json.dumps({k:v for k,v in result.items() if k!='traceback'},ensure_ascii=True),flush=True)
        (BATCH/'ultima_execucao.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf8')
