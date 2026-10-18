"""Source-evidenced one-staff score preparation with embedded keyboard diagrams."""
from pathlib import Path
from collections import defaultdict, Counter
from bisect import bisect_right
from fractions import Fraction
from statistics import median
import xml.etree.ElementTree as E
import ast, struct, math, json, zipfile, subprocess, hashlib, re, copy, os, tempfile, textwrap, shutil
from PIL import Image, ImageDraw
from inspect_midi import parse_midi, summarize
from batch_harmony import analyze_harmony, CHORD_INTERVALS
from batch_svg_qa import analyze as analyze_svg
import batch_svg_qa as svg_geometry
import compact_score_layout
import diagram_lanes

WORK=Path(__file__).resolve().parent
BATCH=Path(os.environ.get('PARTITURAS_JOB',str(Path(tempfile.gettempdir())/'TeclasPartituras')));BATCH.mkdir(parents=True,exist_ok=True)
EXE=r'C:\Program Files\MuseScore 4\bin\MuseScore4.exe'
DIV=960
module=ast.parse((WORK/'build_keyboard_diagrams.py').read_text(encoding='utf8'))
exec(compile(ast.Module(body=[n for n in module.body if isinstance(n,ast.FunctionDef) and n.name in ('md4','svg_keyboard')],type_ignores=[]),'diagram_helpers','exec'))
NS='{http://www.w3.org/2000/svg}';E.register_namespace('',NS[1:-1])

def progress(message):
    print(json.dumps({'kind':'progress','message':message},ensure_ascii=False),flush=True)

class NeedsReview(Exception):
    def __init__(self,message,quarters=None,kind='rhythm_collision'):
        super().__init__(message)
        self.quarters=list(quarters or [])
        self.kind=kind
        self.review_points=[]

def transpose_semitones(value=0):
    if value is None:return 0
    if type(value) is not int or not -12<=value<=12:
        raise ValueError('A transposição deve ser um número inteiro entre -12 e 12 semitons.')
    return value

def transpose_harmony(harmony,semitones):
    """Transpose an output copy; evidence and MIDI analysis retain source pitches."""
    semitones=transpose_semitones(semitones)
    result=copy.deepcopy(harmony)
    if semitones%12==0:return result
    def key_transposed(key):
        changed=copy.deepcopy(key)
        old=key.get('fifths',0);target=(old*7+semitones)%12
        candidates=[f for f in range(-7,8) if f*7%12==target]
        fifths=min(candidates,key=lambda f:(abs(f),bool(old and f and (old>0)!=(f>0)),abs(f-old)))
        changed['fifths']=fifths
        changed['root']=(fifths*7+(9 if changed.get('minor',False) else 0))%12
        return changed
    result['key']=key_transposed(harmony.get('key') or {'fifths':0,'minor':False})
    result['key_changes']=[key_transposed(key) for key in harmony.get('key_changes',[])]
    for chord in result['events']:
        chord['root']=(chord['root']+semitones)%12
        chord['pitch_classes']=sorted((chord['root']+interval)%12 for interval in CHORD_INTERVALS[chord['quality']])
        active=result['key']
        for change in result['key_changes']:
            if change['tick']<=chord['tick']:active=change
        letter,alter=spellings(active['fifths'],active.get('minor',False))[chord['root']]
        chord['root_spelling']=letter+('#'*alter if alter>0 else 'b'*(-alter))
    return result

def locate_review_point(quarter,origin,bars,kind,message,blocking=False):
    position=quarter*DIV-origin
    found=next(((i,bar) for i,bar in enumerate(bars) if bar['start']<=position<bar['end']),None)
    if found is None:return None
    index,bar=found
    return {'quarter':round(quarter,6),'measure':index+1,
            'beat':round(1+(position-bar['start'])/DIV/(4/bar['meter'][1]),4),
            'measureStartQuarter':(bar['start']+origin)/DIV,
            'measureEndQuarter':(bar['end']+origin)/DIV,
            'kind':kind,'message':message,'blocking':blocking}

def rhythm_failure_points(error,parsed,config,origin,bars):
    """Carry the actual source attacks into both analysis and generation errors."""
    from melody_sections import all_sources
    sources=all_sources(config)+config.get('solo_sources',[])
    points=[]
    for quarter in error.quarters:
        point=locate_review_point(quarter,origin,bars,error.kind,str(error),True)
        if point is None:continue
        notes=[n for n in parsed['notes'] if abs(n['start']/parsed['ppq']-quarter)<1e-7
               and source_matches(n,sources)]
        point['notes']=[{'pitch':n['pitch'],'track':n['track'],'channel':n['channel'],'program':n['program'],
                         'quarter':n['start']/parsed['ppq'],'durationQuarters':(n['end']-n['start'])/parsed['ppq']}
                        for n in notes]
        if notes:
            names=('C','C#','D','D#','E','F','F#','G','G#','A','A#','B')
            evidence=', '.join(f"{names[n['pitch']%12]}{n['pitch']//12-1} (MIDI {n['pitch']}, pista {n['track']+1}, canal {n['channel']})" for n in notes)
            point['message']+=f' Ataque original: {evidence}. Ouça o compasso e escolha uma fonte por trecho, se necessário.'
        points.append(point)
    return points

def review_points(parsed,config,harmony,*,include_melody_report=False):
    """Locate review evidence with the exact bar origin used by score generation."""
    points=[];message='';ready=True
    try:
        _,meters,_=meta(parsed);origin,end,bars=make_bars(parsed,meters)
    except NeedsReview as error:
        result=([],False,str(error))
        return (*result,{}) if include_melody_report else result
    def add(quarter,kind,text,blocking=False):
        point=locate_review_point(quarter,origin,bars,kind,text,blocking)
        if point is not None:points.append(point)
    for chord in harmony.get('events',[]):
        if chord.get('confidence',0)<.65:
            add(chord['tick']/parsed['ppq'],'chord_low_confidence',
                f"Acorde com confiança de {chord.get('confidence',0):.0%}. Confira a cifra ouvindo o acompanhamento.")
    if harmony.get('context_advice'):
        for diagnostic in harmony.get('diagnostics',[]):
            add(diagnostic['tick']/parsed['ppq'],diagnostic['kind'],diagnostic['message'])
    report={}
    try:
        _,_,report=melody_events(parsed,config,origin,end)
        for quarter in report.get('collision_original_quarters',[]):
            add(quarter,'rhythm_collision','Ataques próximos agrupados na grade; confira as notas e os ornamentos.')
        for item in report.get('boundary_grid_refinements',[]):
            add(item['beat_original'],'rhythm_refined',
                'Ritmo refinado automaticamente para separar ataques próximos. Confira o trecho com velocidade reduzida.')
        for item in report.get('attack_position_adjustments',[]):
            point=locate_review_point(item['original_quarter'],origin,bars,'rhythm_refined',
                'Ataque preservado com ajuste local de ritmo. Confira o trecho com velocidade reduzida.')
            if point is not None:
                point.update(originalQuarter=item['original_quarter'],quantizedQuarter=item['quantized_quarter'],
                    adjustmentQuarters=item['adjustment_quarters'])
                points.append(point)
        # Each affected bar gets one actionable warning, even when a long tail
        # crosses several bars. Identical doubled notes are not conflicts.
        overlaps=report.get('melody_merge',{}).get('overlaps',[])
        for bar in bars:
            start=(bar['start']+origin)/DIV;stop=(bar['end']+origin)/DIV
            affected=[item for item in overlaps if item['startQuarter']<stop and item['endQuarter']>start]
            if affected:
                suppressed=sum(item['action']=='suppressed' for item in affected)
                trimmed=sum(item['action']=='trimmed' for item in affected)
                add(max(start,min(item['startQuarter'] for item in affected)),'melody_source_overlap',
                    f'Combinação por prioridade: {suppressed} ataques omitidos e {trimmed} caudas encurtadas neste compasso. Confira a ordem das pistas e ouça o trecho.')
    except NeedsReview as error:
        ready=False;message=str(error)
        points.extend(rhythm_failure_points(error,parsed,config,origin,bars))
    unique={(point['quarter'],point['kind']):point for point in points}
    result=(sorted(unique.values(),key=lambda point:(point['quarter'],point['kind'])),ready,message)
    return (*result,report) if include_melody_report else result

DEFAULT_SCORE_SUBTITLE=''

def default_score_title(filename):
    title=re.sub(r'[_]+',' ',Path(filename).stem)
    return re.sub(r'\s+',' ',title).strip() or 'Partitura'

def score_metadata(values,default_title):
    result={}
    diagram_inversion(values.get('diagram_inversion'))
    for field,key,default,limit,label in (
        ('scoreTitle','title',default_title,160,'Título'),
        ('scoreSubtitle','subtitle',DEFAULT_SCORE_SUBTITLE,200,'Subtítulo'),
        ('scoreArtist','artist','',120,'Artista'),
        ('scoreComposer','composer','',120,'Autor')):
        value=values.get(field)
        if value is None:
            result[key]=default;continue
        if not isinstance(value,str):raise ValueError(f'{label} deve ser um texto.')
        if any(not (ord(c) in (9,10,13) or 32<=ord(c)<=0xd7ff or 0xe000<=ord(c)<=0xfffd or 0x10000<=ord(c)<=0x10ffff) for c in value):
            raise ValueError(f'{label} contém um caractere de controle inválido.')
        value=re.sub(r'\s+',' ',value).strip()
        if len(value)>limit:raise ValueError(f'{label} deve ter no máximo {limit} caracteres.')
        result[key]=(value or default_title) if key=='title' else value
    return result

def diagram_inversion(value=2):
    if value is None:return 2
    if type(value) is not int or value not in (0,1,2):
        raise ValueError('A inversão dos diagramas deve ser 0 (fundamental), 1 (primeira) ou 2 (segunda).')
    return value

def chord_diagram_inversion(chord,default=2):
    """An occurrence override may be zero; only null/missing inherits the score."""
    value=chord.get('diagram_inversion')
    return diagram_inversion(default if value is None else value)

def _header_lines(text,width):
    return '\n'.join(textwrap.wrap(text,width=width,break_long_words=True,break_on_hyphens=False))

def _header_layout(metadata):
    title=_header_lines(metadata['title'],68)
    subtitle=_header_lines(metadata['subtitle'],100)
    artist=metadata.get('artist','')
    composer=('\n'.join(_header_lines(label+value,70) for label,value in
        (('Intérprete: ',artist),('Autor: ',metadata['composer'])) if value)
        if artist else _header_lines(metadata['composer'],70))
    title_size=20 if len(metadata['title'])<60 else 16
    lines=lambda text:len(text.splitlines()) if text else 0
    # Frame text uses absolute millimetre offsets, unlike staff-attached text.
    # A multiline title must push the subtitle below all its lines; MuseScore's
    # fixed subtitle offset does not do this automatically on MusicXML import.
    line_mm=lambda text,size:lines(text)*size*25.4/72*1.2
    subtitle_offset=max(10,line_mm(title,title_size)+1)
    content_bottom=subtitle_offset+line_mm(subtitle,10) if subtitle else line_mm(title,title_size)
    height=max(10,(content_bottom+(line_mm(composer,10)+3 if composer else 1))/1.65)
    return dict(title=title,subtitle=subtitle,composer=composer,title_size=title_size,
                subtitle_offset=round(subtitle_offset,3),composer_offset=round(content_bottom+2,3),height=round(height,3))

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

def _refine_boundary_grids(bybeat,grids,choices,origin,end):
    """Separate distinct attacks meeting at a beat boundary without discarding either.

    The initial grid test only sees notes inside one beat. Its last attack can
    therefore round onto the next beat's first attack. Refine only those local
    grids, keeping binary and ternary subdivisions in their original families.
    """
    original=dict(grids)
    def positions(beat,div):
        return [round((beat+round(phase*div)/div)*DIV) for phase in bybeat[beat]]
    for beat in sorted(bybeat):
        following=beat+1;boundary=following*DIV
        if following not in bybeat or not origin<=boundary<end:continue
        left=grids[beat];right=grids[following]
        if max(positions(beat,left))!=min(positions(following,right)):continue
        same_family=(choices[beat][0]%3==0)==(choices[following][0]%3==0)
        shared_resolution=max(left,right)
        candidates=[]
        for left_div in choices[beat]:
            if left_div<left:continue
            left_positions=positions(beat,left_div)
            if len(set(left_positions))!=len(left_positions):continue
            for right_div in choices[following]:
                if right_div<right:continue
                right_positions=positions(following,right_div)
                if len(set(right_positions))!=len(right_positions):continue
                if max(left_positions)>=min(right_positions):continue
                # Reuse the detail already required by the neighbouring phrase
                # before choosing a coarser separation that moves its ornament.
                shared=not same_family or min(left_div,right_div)>=shared_resolution
                changed=(left_div!=left)+(right_div!=right)
                refinement=left_div/left+right_div/right
                error=sum(abs(t/DIV-beat-phase) for t,phase in zip(left_positions,bybeat[beat]))
                error+=sum(abs(t/DIV-following-phase) for t,phase in zip(right_positions,bybeat[following]))
                candidates.append(((not shared,changed,refinement,error,left_div,right_div),left_div,right_div))
        if not candidates:
            raise NeedsReview(f'Ataques próximos ao tempo {following} não podem ser separados nas grades rítmicas suportadas; requer revisão rítmica individual.',
                [beat+max(bybeat[beat]),following+min(bybeat[following])])
        _,grids[beat],grids[following]=min(candidates)
    return [{'beat_original':beat,'from':original[beat],'to':div}
            for beat,div in sorted(grids.items()) if div!=original[beat]]

def _separate_quantized_attacks(bybeat,grids,choices,origin,end):
    """Resolve a failed nearest-grid quantization without losing distinct attacks.

    Only beats containing a collision may change. Their finest supported grid
    offers adjacent positions at most one subdivision from each source onset.
    A monotonic minimum-error assignment preserves every attack and its order;
    dense passages that cannot fit within that bound still require review.
    """
    records=[]
    for beat in sorted(bybeat):
        for phase in sorted(bybeat[beat]):
            quarter=beat+phase
            position=round((beat+round(phase*grids[beat])/grids[beat])*DIV)
            if origin<=position<end:records.append((beat,quarter,position))
    affected=set()
    for previous,current in zip(records,records[1:]):
        if previous[2]>=current[2]:affected.update((previous[0],current[0]))
    if not affected:return {},[],[]
    refined=dict(grids)
    for beat in affected:refined[beat]=max(choices[beat])
    # Keep every already supported passage unchanged. Only a failed local
    # assignment may use one further subdivision (128ths / 64th triplets).
    # The displacement bound also shrinks with that finer grid: this is not
    # permission to stretch or discard an arbitrarily dense ornament.
    while True:
        layers=[];states={None:(0.,0)}
        for index,(beat,quarter,nearest) in enumerate(records):
            if beat in affected:
                div=refined[beat];step=DIV//div;source=quarter*DIV
                candidates=[beat*DIV+slot*step for slot in range(div+1)
                    if origin<=beat*DIV+slot*step<end
                    and abs(beat*DIV+slot*step-source)<=step+1e-7]
            else:candidates=[nearest]
            layer={}
            for position in candidates:
                options=[((cost[0]+(position-quarter*DIV)**2,
                           cost[1]+abs(position-nearest)),previous)
                         for previous,cost in states.items() if previous is None or previous<position]
                if options:layer[position]=min(options,key=lambda item:item[0])
            if not layer:break
            layers.append(layer);states={position:item[0] for position,item in layer.items()}
        else:break
        local={beat,records[max(0,index-1)][0]} & affected
        finer=[b for b in local if refined[b]==max(choices[b])]
        if not finer:
            raise NeedsReview(f'Ataques próximos ao tempo {quarter:.3f} excedem a resolução rítmica suportada; requer revisão rítmica individual.',
                [record[1] for record in records[:index+1] if quarter-record[1]<=2/max(choices[beat])])
        for b in finer:refined[b]=2*max(choices[b])
    position=min(states,key=states.get);assigned=[]
    for layer in reversed(layers):
        assigned.append(position);position=layer[position][1]
    assigned.reverse()
    refinements=[{'beat_original':beat,'from':grids[beat],'to':refined[beat]}
        for beat in sorted(affected) if grids[beat]!=refined[beat]]
    adjustments=[{'original_quarter':quarter,'quantized_quarter':position/DIV,
                  'adjustment_quarters':position/DIV-quarter,'grid':refined[beat]}
        for (beat,quarter,nearest),position in zip(records,assigned)
        if position!=nearest]
    grids.update(refined)
    return {quarter:position for (_,quarter,_),position in zip(records,assigned)},refinements,adjustments


def _source_accepts(note,source,ppq):
    return (source_matches(note,[source]) and source.get('start_quarter',-1)<=note['start']/ppq<source.get('end_quarter',float('inf'))
            and source.get('min_pitch',0)<=note['pitch']<=source.get('max_pitch',127))


def priority_melody_notes(selected,sources,ppq,onset_cluster_quarters=.04):
    """Choose only evidenced attacks; never resume an already masked lower tail.

    Upper-line reduction is local to each source. A lower source may contribute
    its attack only while higher sources are silent, and ends at their next
    attack. All timing decisions precede the single, global quantization pass.
    """
    result=[];higher=[];seen_attacks=set();statistics=[];overlaps=[]
    for priority,source in enumerate(sources):
        original=[note for note in selected if _source_accepts(note,source,ppq)]
        row={key:source[key] for key in ('track','channel','program') if key in source}
        row.update(priority=priority+1,inputNotes=len(original),candidateAttacks=0,keptAttacks=0,
                   suppressedAttacks=0,duplicateAttacks=0,trimmedNotes=0,quantizedAttacks=0)
        statistics.append(row)
        unique={}
        for note in original:
            identity=(note['start'],note['end'],note['pitch'])
            if identity in unique:
                row['duplicateAttacks']+=1
                if note['velocity']>unique[identity]['velocity']:unique[identity]=note
            else:unique[identity]=note
        groups=[]
        for note in sorted(unique.values(),key=lambda n:(n['start'],n['pitch'],n['end'],n['velocity'])):
            if not groups or note['start']-groups[-1][0]['start']>ppq*onset_cluster_quarters:groups.append([note])
            else:groups[-1].append(note)
        top=[max(group,key=lambda n:(n['pitch'],n['end'],n['velocity'])) for group in groups]
        top.sort(key=lambda n:(n['start'],n['pitch'],n['end']))
        row['candidateAttacks']=len(top)
        active=[]
        for index,note in enumerate(top):
            limit=top[index+1]['start'] if index+1<len(top) else note['end']
            stop=min(note['end'],limit)
            if stop<=note['start']:continue
            current=dict(note,end=stop,duration=stop-note['start'],_merge_source=priority)
            active.append(current)
            identity=(note['start'],note['end'],note['pitch'])
            if identity in seen_attacks:
                row['duplicateAttacks']+=1;continue
            conflicts=[]
            for higher_notes,higher_ends in higher:
                position=bisect_right(higher_ends,note['start'])
                if position<len(higher_notes) and higher_notes[position]['start']<stop:conflicts.append(higher_notes[position])
            first=min(conflicts,key=lambda n:(max(note['start'],n['start']),n['_merge_source'])) if conflicts else None
            if first is not None:
                cut=max(note['start'],first['start'])
                action='suppressed' if cut==note['start'] else 'trimmed'
                row['suppressedAttacks' if action=='suppressed' else 'trimmedNotes']+=1
                overlaps.append(dict(startQuarter=cut/ppq,endQuarter=stop/ppq,sourcePriority=priority+1,
                                     preferredPriority=first['_merge_source']+1,action=action))
                if action=='suppressed':continue
                current=dict(current,end=cut,duration=cut-note['start'])
            result.append(current);row['keptAttacks']+=1
        higher.append((active,[note['end'] for note in active]))
        seen_attacks.update((note['start'],note['end'],note['pitch']) for note in top)
    result.sort(key=lambda n:(n['start'],n['_merge_source'],n['pitch']))
    summary=dict(mode='priority',sources=statistics,overlaps=overlaps)
    for field in ('inputNotes','candidateAttacks','keptAttacks','suppressedAttacks','duplicateAttacks','trimmedNotes','quantizedAttacks'):
        summary[field]=sum(row[field] for row in statistics)
    summary['message']=(f"{len(sources)} fontes combinadas por prioridade: {summary['keptAttacks']} ataques selecionados, "
                        f"{summary['suppressedAttacks']} omitidos em sobreposição e {summary['duplicateAttacks']} duplicados removidos.")
    return result,summary


def melody_events(parsed,config,origin,end):
    ppq=parsed['ppq'];sources=config['melody_sources']
    selected=sorted([n for n in parsed['notes'] if (not config.get('velocity_filter') or n['velocity'] in config['velocity_filter']) and any(source_matches(n,[s]) and s.get('start_quarter',-1)<=n['start']/ppq<s.get('end_quarter',float('inf')) and s.get('min_pitch',0)<=n['pitch']<=s.get('max_pitch',127) for s in sources)],key=lambda n:(n['start'],n['pitch']))
    sections=config.get('melody_sections',[])
    if sections:
        import sys
        from melody_sections import selected_notes
        selected,sources,sections=selected_notes(parsed,config,sys.modules[__name__],origin,end)
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
    merge_report=None
    if sections or config.get('merge_mode')=='priority' and len(sources)>1:
        top,merge_report=priority_melody_notes(selected,sources,ppq,config.get('onset_cluster_quarters',.04))
    else:
        # Reviewed profiles and single-source selections retain their exact path.
        groups=[]
        for n in selected:
            if not groups or n['start']-groups[-1][0]['start']>ppq*config.get('onset_cluster_quarters',.04):groups.append([n])
            else:groups[-1].append(n)
        top=[max(g,key=lambda n:n['pitch']) for g in groups]
    bybeat=defaultdict(list)
    for n in top:bybeat[int(n['start']//ppq)].append(n['start']/ppq%1)
    grids={};grid_choices={};triplet_beats=[]
    for beat,phases in bybeat.items():
        binary=sum(abs(p-round(p*4)/4) for p in phases)
        ternary=sum(abs(p-round(p*3)/3) for p in phases)
        has1=any(abs(p-1/3)<.06 for p in phases);has2=any(abs(p-2/3)<.06 for p in phases)
        if has1 and has2 and ternary+.055<binary:
            choices=(3,6,12);triplet_beats.append(beat)
        elif any(abs(p-round(p*8)/8)<.025 and abs(p-round(p*4)/4)>.08 for p in phases):choices=(8,16)
        else:choices=(4,8,16)
        grid_choices[beat]=choices
        grids[beat]=choices[-1]
        for div in choices:
            if len({round(p*div) for p in phases})==len(phases):grids[beat]=div;break
    initial_grids=dict(grids);attack_positions={};attack_adjustments=[]
    try:boundary_refinements=_refine_boundary_grids(bybeat,grids,grid_choices,origin,end)
    except NeedsReview:
        grids=initial_grids
        attack_positions,boundary_refinements,attack_adjustments=_separate_quantized_attacks(bybeat,grids,grid_choices,origin,end)
    def snap(t):
        q=t/ppq;beat=math.floor(q);div=grids.get(beat,4)
        # Near a beat boundary use the shared endpoint.
        return round((beat+round((q-beat)*div)/div)*DIV)
    if not attack_positions:
        nearest=[snap(n['start']) for n in top if origin<=snap(n['start'])<end]
        if len(nearest)-len(set(nearest))>(0 if merge_report is not None else max(3,len(top)*.008)):
            attack_positions,_,attack_adjustments=_separate_quantized_attacks(bybeat,grids,grid_choices,origin,end)
            boundary_refinements=[{'beat_original':beat,'from':initial_grids[beat],'to':div}
                for beat,div in sorted(grids.items()) if div!=initial_grids[beat]]
    onsetmap={};collision=0;collision_quarters=[]
    for n in top:
        s=attack_positions.get(n['start']/ppq,snap(n['start']))-origin
        if sections and not n['_section_start']<=s+origin<n['_section_end']:
            raise NeedsReview('Um ataque próximo à troca de trecho ultrapassa o limite após quantização; confira este compasso antes de gerar.',[n['start']/ppq])
        if not 0<=s<end-origin:continue
        if s in onsetmap:
            collision+=1
            collision_quarters.extend((n['start']/ppq,onsetmap[s]['start']/ppq))
        if s not in onsetmap or n['velocity']>onsetmap[s]['velocity']:onsetmap[s]=n
    if collision>max(3,len(top)*.008):raise NeedsReview(f'{collision} ataques colidem após quantização; requer revisão rítmica individual.',sorted(set(collision_quarters)))
    events=[];errors=[];onsets=sorted(onsetmap)
    for i,s in enumerate(onsets):
        n=onsetmap[s];next_s=onsets[i+1] if i+1<len(onsets) else end-origin
        stop=snap(n['end'])-origin
        if i+1<len(onsets) and onsetmap[next_s]['start']-n['end']<=ppq*.28:stop=next_s
        beat=int((s+origin)//DIV);minimum=DIV//grids.get(beat,4)
        stop=min(next_s,end-origin,max(s+minimum,stop))
        if sections:stop=min(stop,n['_section_end']-origin)
        if stop>s:
            event={'start':s,'end':stop,'pitch':n['pitch']}
            gs=[g['pitch'] for g in grace_notes if abs(n['start']/ppq-g['target_quarter'])<.001]
            if gs:event['graces']=gs
            events.append(event)
            if merge_report is not None:
                merge_report['sources'][n['_merge_source']]['quantizedAttacks']+=1
                # Carry the winning attack's real MIDI identity through notation,
                # rather than reconstructing it from pitch or pre-quantized input.
                event['source']={key:n[key] for key in ('track','channel','program')}
                event['source']['priority']=n['_merge_source']+1
        errors.append(abs(s+origin-n['start']/ppq*DIV)/DIV)
    if len({n['pitch'] for n in events})<3:raise NeedsReview('Fonte selecionada não contém alturas suficientes para uma melodia identificável.')
    assert sum(len(n.get('graces',[])) for n in events)==len(grace_notes),'Grace target lost during grouping'
    report={'raw_notes':len(selected),'principal_source_notes':main_count,'grace_notes_preserved':len(grace_notes),'solo_notes_added':solo_count,'simultaneous_notes_reduced':len(selected)-len(top),'melody_attacks':len(events),'quantized_attack_collisions':collision,'triplet_beats_original':triplet_beats,'onset_adjustment_max_quarters':max(errors),'onset_adjustment_mean_quarters':sum(errors)/len(errors)}
    if boundary_refinements:report['boundary_grid_refinements']=boundary_refinements
    if attack_adjustments:report['attack_position_adjustments']=attack_adjustments
    if collision_quarters:report['collision_original_quarters']=sorted(set(collision_quarters))
    if merge_report is not None:
        merge_report['quantizedAttacks']=len(events)
        if sections:
            merge_report['sections']=sections
            merge_report['message']=f"{len(sections)} trechos exclusivos; fora deles, " + merge_report['message']
        report['melody_merge']=merge_report
    return events,{b-int(origin//DIV):v for b,v in grids.items()},report

def _lyric_text(data):
    """Keeps leading spaces and slashes: in karaoke files they mark new words and lines."""
    try:text=data.decode('utf-8')
    except UnicodeDecodeError:text=data.decode('cp1252',errors='replace')
    return text.strip('\x00\r\n')

def karaoke_syllables(parsed):
    """Syllables of the song text: lyric events (type 5), else karaoke text events (type 1, @ headers skipped).

    Fewer than 16 syllables usually means credits or comments, not lyrics, so nothing is returned.
    """
    metas=[e for e in parsed.get('events',[]) if e.get('kind')=='meta']
    source=[e for e in metas if e['type']==5]
    karaoke=len(source)<16
    if karaoke:source=[e for e in metas if e['type']==1 and not _lyric_text(e['data']).lstrip().startswith('@')]
    syllables=[];previous=None
    for event in sorted(source,key=lambda e:(e['tick'],e['order'])):
        text=_lyric_text(event['data'])
        if karaoke:starts=not syllables or text[:1] in (' ','/','\\')
        else:starts=previous is None or not previous.rstrip().endswith('-')
        clean=text.lstrip('/\\').strip()
        if clean.endswith('-'):clean=clean[:-1].rstrip()
        previous=text
        # Credits mixed into the lyrics (e-mail, web address) are not sung.
        if not clean or re.search(r'@|https?:|www\.|\.com\b|\.br\b',clean,re.I):continue
        syllables.append({'tick':event['tick'],'text':clean,'word_start':starts})
    return syllables if len(syllables)>=16 else []

def attach_lyrics(parsed,notes,origin):
    """Puts each syllable on the melody note starting with it (same time conversion as chords).

    A syllable more than one beat away from any attack is left out; several on one note are joined.
    """
    syllables=karaoke_syllables(parsed)
    attacks=sorted(notes,key=lambda n:n['start'])
    if not syllables or not attacks:return {'syllables':len(syllables),'attached':0}
    assigned=[];index=0
    for syllable in syllables:
        tick=round(syllable['tick']/parsed['ppq']*DIV)-origin
        while index+1<len(attacks) and attacks[index+1]['start']<=tick+DIV//4:index+=1
        note=attacks[index]
        if abs(note['start']-tick)>DIV:continue
        if assigned and assigned[-1]['note'] is note:
            assigned[-1]['text']+=('' if not syllable['word_start'] else ' ')+syllable['text'];assigned[-1]['last_word_start']=syllable['word_start']
        else:assigned.append({'note':note,'text':syllable['text'],'word_start':syllable['word_start'],'last_word_start':syllable['word_start']})
    for position,item in enumerate(assigned):
        following=assigned[position+1]['word_start'] if position+1<len(assigned) else True
        syllabic=('single' if following else 'begin') if item['word_start'] else ('end' if following else 'middle')
        item['note']['lyric']={'text':item['text'],'syllabic':syllabic}
    return {'syllables':len(syllables),'attached':len(assigned)}

def instrument_names():
    """Portuguese General MIDI names: (full name, staff abbreviation) for each program."""
    table=WORK/'gm_pt.json'
    if table.exists():return [tuple(item) for item in json.loads(table.read_text(encoding='utf8'))]
    return [(name,name) for name in json.loads((WORK/'gm.json').read_text(encoding='utf8'))]

def instrument_labels(notes,origin=0):
    """First entry and source changes only; rests and tied pieces aren't entries."""
    names=[full for full,_ in instrument_names()]
    labels=[];previous=None
    for note in notes:
        source=note.get('source')
        if source is None:continue
        identity=tuple(source[key] for key in ('track','channel','program'))
        if identity!=previous:
            program=source['program']
            if type(program) is not int or not 0<=program<len(names):
                raise NeedsReview('Instrumento de origem inválido; selecione novamente as pistas.',kind='instrument_source')
            labels.append({'tick':note['start'],'quarter':note['start']/DIV,
                           'original_quarter':(note['start']+origin)/DIV,
                           'text':names[program],'source':dict(source)})
        previous=identity
    return labels

# The MusicXML writer lives in score_musicxml.py; re-exported for callers and tests.
from score_musicxml import make_bars, TYPES, pieces, emit_note, KINDS, emit_harmony, layout_bars, high_melody_layout, build_musicxml

C_LABEL='C'  # name printed under the two C keys of each keyboard diagram ('Dó' with solfège note names)

def keyboard_asset(rootpc,quality,inversion=2,c_label='C'):
    inversion=diagram_inversion(inversion)
    intervals=CHORD_INTERVALS[quality]
    bass_interval=0 if inversion==0 else next(x for x in ((3,4,2,5) if inversion==1 else (7,6,8)) if x in intervals)
    bass=53+((rootpc+bass_interval-53)%12)
    pitches=sorted(bass+(rootpc+x-bass)%12 for x in intervals)
    if max(pitches)>71:pitches=[p-12 for p in pitches]
    assert 48<=min(pitches)<max(pitches)<=71
    label=str(rootpc)+quality;svg=E.fromstring(svg_keyboard(label,pitches))
    svg.find(NS+'title').text=('Acorde em posição fundamental','Acorde em primeira inversão','Acorde em segunda inversão')[inversion]
    label=('Fundamental no grave','Terça ou nota suspensa no grave','Quinta no grave')[inversion]
    svg.find(NS+'desc').text=label+'; notas MIDI: '+','.join(map(str,pitches))
    if c_label!='C':
        for text in svg.iter(NS+'text'):text.text=c_label
    data=E.tostring(svg,encoding='utf-8',xml_declaration=True)
    return md4(data)+'.svg',data,pitches

def call_musescore(args,log,timeout=90):
    messages={
        'importacao':'Criando a partitura no formato MuseScore…',
        'gravacao':'Salvando a partitura com os diagramas de teclado…',
        'render':'Gerando as páginas da prévia para conferência…',
        'ajuste_paginacao':'Reorganizando as quebras de página…',
        'ajuste_espacamento':'Aplicando ajustes de espaço entre cifras e diagramas…',
    }
    progress(messages.get(log.stem,'Processando a partitura no MuseScore…'))
    p=subprocess.run([EXE]+args,capture_output=True,text=True,errors='replace',timeout=timeout)
    log.write_text(p.stdout+'\n'+p.stderr,encoding='utf8')
    if p.returncode:raise NeedsReview(f'MuseScore retornou {p.returncode}; veja {log.name}.')

def page_order(path):return int(path.stem.rsplit('-',1)[-1])

# Rendered-layout repairs live in score_layout.py; re-exported for callers and tests.
from score_layout import repair_orphan_pages, _compact_diagram_offsets, repair_diagram_lanes, repair_spacing

# Instrument-name layout lives in score_labels.py; re-exported for callers and tests.
from score_labels import instrument_label_geometry, _instrument_label_lanes, verify_instrument_label_layout, compact_instrument_label_layout


def _apply_native_header(root,metadata):
    score=root.find('Score');staff=score.find('Staff');header=_header_layout(metadata)
    for name,value in (('workTitle',metadata['title']),('subtitle',metadata['subtitle']),('composer',metadata['composer']),('artist',metadata.get('artist',''))):
        element=score.find(f"metaTag[@name='{name}']")
        if element is None:element=sub(score,'metaTag',name=name)
        element.text=value
    vbox=staff.find('VBox')
    if vbox is None:
        vbox=E.Element('VBox');staff.insert(0,vbox)
    height=vbox.find('height')
    if height is None:height=sub(vbox,'height')
    height.text=str(header['height'])
    if metadata.get('artist'):
        # Explicit text offsets must reserve a fixed frame: MuseScore's auto
        # sizing ignores them and can place long credits on the first staff.
        for name,value in (('boxAutoSize','0'),('sizeIsSpatiumDependent','1')):
            element=vbox.find(name)
            if element is None:element=sub(vbox,name)
            element.text=value
    for role in ('title','subtitle','composer'):
        existing=[text for text in vbox.findall('Text') if text.findtext('style')==role]
        if not header[role]:
            for text in existing:vbox.remove(text)
            continue
        text=existing[0] if existing else sub(vbox,'Text')
        for duplicate in existing[1:]:vbox.remove(duplicate)
        style=text.find('style')
        if style is None:style=sub(text,'style')
        style.text=role
        size=text.find('size')
        if size is None:size=sub(text,'size')
        size.text=str(header['title_size'] if role=='title' else 10)
        if role=='subtitle':
            offset=text.find('offset')
            if offset is None:offset=E.SubElement(text,'offset')
            offset.set('x','0');offset.set('y',str(header['subtitle_offset']))
        if role=='composer' and metadata.get('artist'):
            align=text.find('align')
            if align is None:align=sub(text,'align')
            align.text='right,top'
            offset=text.find('offset')
            if offset is None:offset=E.SubElement(text,'offset')
            offset.set('x','0');offset.set('y',str(header['composer_offset']))
        content=text.find('text')
        if content is None:content=sub(text,'text')
        content.clear();content.text=header[role]
    return vbox

MUSIC_FONTS={'Leland':'Leland Text','Bravura':'Bravura Text','Emmentaler':'MScore Text','Petaluma':'Petaluma Text'}
SCORE_SIZES={'normal':None,'grande':'2.0','muito-grande':'2.3'}

def score_style_values(choice):
    """Music font and staff size chosen in the app; unknown values keep MuseScore's defaults (Leland, 1.75 mm)."""
    choice=choice or {};values={}
    font=choice.get('music_font')
    if font in MUSIC_FONTS and font!='Leland':values.update(musicalSymbolFont=font,musicalTextFont=MUSIC_FONTS[font])
    size=SCORE_SIZES.get(choice.get('size'))
    if size:values['spatium']=size
    return values

def convert_score(xml,report,out,filename):
    style=E.parse(WORK/'garoto_melodia_cifras'/'melodia-cifras.mss').getroot()
    vertical=report.get('high_melody_layout',[])
    vertical_by_measure={i:row for row in vertical for i in range(row['first_measure_index'],row['last_measure_index']+1)}
    style_values={'Spatium':'1.65','minSystemDistance':'12','maxSystemDistance':'20','frameSystemDistance':'11','createMultiMeasureRests':'0'}
    labels=report.get('instrument_labels',[])
    if vertical:
        top=max(-row['image_y_spatium'] for row in vertical)
        # Image offsets do not reserve a staff skyline in MuseScore. Keep an
        # explicit inter-system and page-top allowance for their raised positions.
        distance=max(12,top+3)
        style_values.update(minSystemDistance=str(distance),maxSystemDistance=str(max(20,distance+4)),staffUpperBorder=str(max(7,top+1)))
        report['high_melody_spacing_style']={k:style_values[k] for k in ('minSystemDistance','maxSystemDistance','staffUpperBorder')}
    if labels:
        label_top=15+max((row['lift_spatium'] for row in vertical),default=0)
        style_values.update(minSystemDistance=str(max(float(style_values['minSystemDistance']),label_top+3)),
                            maxSystemDistance=str(max(float(style_values['maxSystemDistance']),label_top+7)),
                            staffUpperBorder=str(label_top+1))
    lyrics=report.get('melody_analysis',{}).get('lyrics')
    if isinstance(lyrics,dict) and lyrics.get('attached'):
        # The words under the staff need room before the next system's chord names and keyboard diagrams.
        style_values.update(minSystemDistance=str(max(float(style_values['minSystemDistance']),17)),maxSystemDistance=str(max(float(style_values['maxSystemDistance']),24)))
    style_values.update(score_style_values(report.get('score_style')))
    for tag,val in style_values.items():
        n=style.find('Style/'+tag)
        if n is None:n=sub(style.find('Style'),tag)
        n.text=val
    if any(fold.get('endings') for fold in report.get('melody_analysis',{}).get('repeat_folds',[])):
        # First/second ending brackets go above the chord names and keyboard diagrams, never through them.
        volta=style.find('Style/voltaPosAbove')
        if volta is None:volta=sub(style.find('Style'),'voltaPosAbove')
        volta.attrib.update(x='0',y='-9.5')
    st=out/'estilo.mss';E.ElementTree(style).write(st,encoding='utf-8',xml_declaration=True)
    initial=out/'melodia_importada.mscz';call_musescore(['-S',str(st),'-o',str(initial),str(xml)],out/'importacao.log')
    progress('Inserindo os diagramas de teclado junto às cifras…')
    with zipfile.ZipFile(initial) as z:contents={n:z.read(n) for n in z.namelist()}
    name=next(n for n in contents if n.endswith('.mscx'));root=E.fromstring(contents[name]);harmonies=root.findall('.//Harmony')
    if report.get('melody_analysis',{}).get('chords_only'):
        # Rhythm slashes only show the beats: they never sound (the chord symbols play the harmony).
        for note in root.iter('Note'):
            play=note.find('play')
            if play is None:play=sub(note,'play')
            play.text='0'
    jump=report.get('melody_analysis',{}).get('repeat_jump')
    if jump:
        import score_repeats
        expected=2+('D.S.' in jump['jump'])+(jump['coda_measures']>0)
        if score_repeats.native_jumps(root,E)!=expected:raise NeedsReview('Não foi possível preservar os sinais de D.S./Coda. Gere novamente sem repetições.',kind='repeat_jump')
    if labels:
        texts=root.findall('Score/Staff/Measure/voice/StaffText')
        if [''.join(text.find('text').itertext()) for text in texts]!=[label['text'] for label in labels]:
            raise NeedsReview('Não foi possível preservar os nomes dos instrumentos. Gere novamente a partitura.',kind='instrument_label_layout')
    if len(harmonies)!=len(report['chords']):raise NeedsReview(f'MuseScore importou {len(harmonies)} cifras de {len(report["chords"])}.')
    assets={};asset_manifest={};events=iter(report['chords']);images=0;previous_chord=None
    report['image_spacing']=[0.0]*len(report['chords']);report['end_spacing']={}
    # Chords, diagrams, tempo and system spacing live on the top staff; extra instrument staves only carry notes.
    for measure_index,measure in enumerate(root.findall('Score/Staff')[0].findall('Measure')):
        placement=vertical_by_measure.get(measure_index)
        lift=placement['lift_spatium'] if placement else 0
        if measure_index in report['system_starts']:previous_chord=None
        for voice in measure.findall('voice'):
            for harmony in list(voice.findall('Harmony')):
                c=next(events);inversion=chord_diagram_inversion(c,report.get('diagram_inversion',2))
                written=(report.get('chord_texts') or {}).get(c['quality'])
                if written:
                    # MuseScore turns some suffixes back into its own (7M into maj7); the chosen notation is written in the score.
                    info=harmony.find('harmonyInfo')
                    if info is None:info=harmony
                    name_tag=info.find('name')
                    if name_tag is None:
                        name_tag=E.Element('name');root_tag=info.find('root')
                        info.insert(list(info).index(root_tag) if root_tag is not None else 0,name_tag)
                    name_tag.text=written
                path,data,pitches=keyboard_asset(c['root'],c['quality'],inversion,'Dó' if report.get('note_names')=='solfejo' else 'C');assets['Pictures/'+path]=data
                asset_manifest[path]={'root':c['root'],'quality':c['quality'],'pitches':pitches,'diagram_inversion':inversion}
                im=E.Element('Image');E.SubElement(im,'offset',x=str(4.5+max(0,len(written or c['quality'])-1)*.8),y=str(-6.7-lift));sub(im,'path',path);sub(im,'autoScale',0);E.SubElement(im,'size',w='14',h=str(14*84/282));sub(im,'lockAspectRatio',1);sub(im,'sizeIsSpatium',1)
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
                off.set('x','0');off.set('y',str((-13.5 if labels else -8.5)-lift));sub(tempo,'autoplace',0)
            if labels:
                for text in voice.findall('StaffText'):
                    for tag,value in (('size','9'),('autoplace','0')):
                        element=text.find(tag)
                        if element is None:element=sub(text,tag)
                        element.text=value
                    off=text.find('offset')
                    if off is None:off=E.SubElement(text,'offset')
                    off.set('x','0');off.set('y',str(-10.5-lift))
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
    vbox=_apply_native_header(root,report.get('score_metadata') or {'title':report['title'],'subtitle':DEFAULT_SCORE_SUBTITLE,'composer':''})
    if vbox is not None:
        gap=vbox.find('bottomGap')
        if gap is None:gap=sub(vbox,'bottomGap')
        gap.text=str((18 if labels else 11)+(vertical[0]['lift_spatium'] if vertical else 0))
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
    repair_orphan_pages(final,report,out)
    repair_spacing(final,report,out)
    verify_instrument_label_layout(final,report,out)
    compact_instrument_label_layout(final,report,out)
    progress('Conferindo notas, ritmos e diagramas na partitura salva…')
    with zipfile.ZipFile(final) as z:
        native=E.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.mscx'))))
        assert len(native.findall('Score/Staff'))==report.get('staff_per_source',{}).get('staves',1)
        assert len(native.findall('.//Image'))==images
        if labels:
            assert [''.join(text.find('text').itertext()) for text in native.findall('Score/Staff/Measure/voice/StaffText')]==[label['text'] for label in labels],'Instrument labels changed during native save'
            assert not native.findall('.//InstrumentChange'),'Instrument labels must not change playback instruments'
        for im in native.findall('.//Image'):
            path='Pictures/'+Path(im.findtext('path')).name;assert path in z.namelist();assert Path(path).stem==md4(z.read(path))
    reopened=E.parse(out/'reaberto.musicxml');allnotes=[];reopened_labels=[];cursor=0
    reopened_parts=reopened.findall('part')
    assert len(reopened_parts)==report.get('staff_per_source',{}).get('staves',1),'Instrument staves changed during MuseScore import'
    # Compare the realized melody after native save/reopen, collapsing tie continuations.
    for reopened_part in reopened_parts:
        for m,b in zip(reopened_part.findall('measure'),report['bars']):
            div=m.findtext('attributes/divisions')
            if div is not None:reopened_div=int(div)
            local=0;attack=0
            if labels:
                position=0
                for element in m:
                    if element.tag=='direction':
                        words=element.find('direction-type/words')
                        if words is not None and ''.join(words.itertext()).strip():
                            tick=b['start']+position+round(int(element.findtext('offset','0'))/reopened_div*DIV)
                            reopened_labels.append((tick,''.join(words.itertext())))
                    elif element.tag in ('note','forward','backup') and element.find('grace') is None and element.find('chord') is None:
                        position+=round(int(element.findtext('duration','0'))/reopened_div*DIV)*(-1 if element.tag=='backup' else 1)
            for n in m.findall('note'):
                if n.find('grace') is not None:continue
                dur=round(int(n.findtext('duration','0'))/reopened_div*DIV)
                # Stacked notes of a chord start with the note before them (which already advanced the position).
                if n.find('chord') is None:attack=local
                if n.find('pitch') is not None and not any(t.get('type')=='stop' for t in n.findall('tie')):
                    step=n.findtext('pitch/step');alt=int(n.findtext('pitch/alter','0'));octave=int(n.findtext('pitch/octave'))
                    pitch=(octave+1)*12+{'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}[step]+alt
                    allnotes.append((b['start']+attack,pitch))
                if n.find('chord') is None:local+=dur
            assert local==b['end']-b['start'],('measure length changed',local,b)
    assert all(len(part.findall('measure'))==report['measures'] for part in reopened_parts)
    # Several staves are read part by part; compare the same attacks regardless of staff order.
    expected=[(n['start'],n['pitch']) for n in report['notes']+report.get('second_voice',[])+report.get('chord_staff_notes',[])]
    assert (sorted(allnotes)==sorted(expected) if len(reopened_parts)>1 else allnotes==expected),'Melody changed during MuseScore import'
    if labels:assert reopened_labels==[(label['tick'],label['text']) for label in labels],'Instrument label position changed during MuseScore import'
    assert len(reopened.findall('.//grace'))==report['melody_analysis']['grace_notes_preserved']
    progress('Preparando as miniaturas e a prévia da partitura…')
    pages=sorted(out.glob('pagina-*.png'),key=page_order);assert pages
    columns=min(3,len(pages));rows=math.ceil(len(pages)/columns)
    canvas=Image.new('RGB',(columns*350,rows*525),'#dedede');draw=ImageDraw.Draw(canvas)
    for i,path in enumerate(pages):
        im=Image.open(path).convert('RGB');im.thumbnail((330,480));x=10+i%columns*350;y=28+i//columns*525;canvas.paste(im,(x,y));draw.text((x,y-20),str(i+1),fill='black')
    canvas.save(out/'previa-geral.png')
    report.update(final_path=str(final),images=images,diagram_assets=asset_manifest,pages=len(pages),native_reopened=True,melody_attacks_verified=len(allnotes),final_sha256=hashlib.sha256(final.read_bytes()).hexdigest())
    return final

def prepare(config):
    title=default_score_title(config['filename'])
    score_metadata(config,title);diagram_inversion(config.get('diagram_inversion'))
    index=config['index'];stem=f'{index+1:03d} - '+re.sub(r'[<>:"/\\|?*]','',title)[:83].rstrip(' .')
    out=BATCH/f'{index+1:03d}';out.mkdir(exist_ok=True)
    progress('Lendo as notas da pista selecionada…')
    parsed=parse_midi(config['source_path'])
    progress('Preparando a tonalidade e os acordes da partitura…')
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
    def build_and_convert(active):
        xml,report=build_musicxml(parsed,active,copy.deepcopy(harmony),out,title)
        report['triage']=active
        suffix='posicoes personalizadas' if report['custom_diagram_inversions'] else ('posicao fundamental','1a inversao','2a inversao')[report['diagram_inversion']]
        return convert_score(xml,report,out,stem+' - cifras '+suffix),report
    try:final,report=build_and_convert(config)
    except NeedsReview as error:
        # One staff remains the standard. Only when instrument changes are too close to name
        # without overlap does this song get one staff per instrument, named in the margin.
        if error.kind!='instrument_label_layout' or config.get('staff_per_source') or len(config.get('melody_sources') or [])<2:raise
        progress('As trocas de instrumento estão próximas demais para uma pauta; separando cada instrumento em sua própria pauta…')
        final,report=build_and_convert(dict(config,staff_per_source=True))
        report['staff_per_source']['reason']=str(error)
    progress('Salvando o resultado da conferência musical e visual…')
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
