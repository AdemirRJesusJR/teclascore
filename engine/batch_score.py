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
from batch_harmony import analyze_harmony, CHORD_INTERVALS, DOUBTFUL_CHORD, chord_label
import batch_harmony
from batch_svg_qa import analyze as analyze_svg
import batch_svg_qa as svg_geometry
import compact_score_layout
import diagram_lanes
import keyboard_svg
import lyrics_accents
import time
from contextlib import contextmanager

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
        if chord.get('bass') is not None:chord['bass']=(chord['bass']+semitones)%12
    # New analyses spell each root by its function in the transposed key; reviewed profiles keep the key table.
    functional=bool(harmony.get('context_advice'))
    ordered=sorted(result['events'],key=lambda c:c['tick'])
    for index,chord in enumerate(ordered):
        active=batch_harmony.active_key(result['key'],result['key_changes'],chord['tick'])
        fifths,minor=active['fifths'],active.get('minor',False)
        if functional:
            following=ordered[index+1] if index+1<len(ordered) else None
            chord['root_spelling']=batch_harmony.functional_root_spelling(chord['root'],chord['quality'],fifths,minor,
                None if following is None else (following['root'],following['quality']))
        else:
            letter,alter=spellings(fifths,minor)[chord['root']]
            chord['root_spelling']=letter+('#'*alter if alter>0 else 'b'*(-alter))
        if chord.get('bass') is not None:
            # A slash bass moves with its chord: a chord tone keeps its degree's letter (D/F# → E/G#).
            chord['bass_spelling']=batch_harmony.bass_spelling(chord['root_spelling'],chord['quality'],chord['bass'],fifths,minor)
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

def song_start_review_point(parsed,kind,message):
    """A review point about the whole song, placed on its first bar."""
    try:
        _,meters,_=meta(parsed);origin,_,bars=make_bars(parsed,meters)
    except NeedsReview:
        return None
    if not bars:return None
    return locate_review_point((bars[0]['start']+origin)/DIV,origin,bars,kind,message)

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
        if 0<chord.get('confidence',0)<DOUBTFUL_CHORD:
            add(chord['tick']/parsed['ppq'],'chord_low_confidence',
                f"Acorde com confiança de {chord.get('confidence',0):.0%}. Confira a cifra ouvindo o acompanhamento.")
    # Changes left out for weak support are a doubt, not silence: one point per bar.
    fifths=harmony.get('key',{}).get('fifths',0);flagged=set()
    for item in harmony.get('statistics',{}).get('uncertain_discarded',[]):
        point=locate_review_point(item['tick']/parsed['ppq'],origin,bars,'chord_discarded','')
        if point is None or point['measure'] in flagged:continue
        flagged.add(point['measure'])
        point['message']=(f"Possível troca para {chord_label(item['root'],item['quality'],fifths)} "
                          f"({item['confidence']:.0%}) ficou fora da cifra por falta de apoio. "
                          'Ouça o compasso e acrescente a cifra se ela fizer parte da música.')
        points.append(point)
    try:
        import melody_rhythm
        _,doubt=melody_rhythm.meter_guess(parsed,summarize(parsed)['time_signatures'])
    except Exception:doubt=None
    if doubt and bars:
        if doubt['declared']:text=(f"O MIDI marca {doubt['declared']}, mas os acentos (graves, notas longas, trocas de acorde e bumbo) "
                                   f"sugerem {doubt['guess']}. Ouça o início e confira o compasso.")
        elif doubt['adopted']:text=f"O MIDI não informa o compasso; pelos acentos foi usado {doubt['guess']}. Ouça o início e confira."
        else:text=f"O MIDI não informa o compasso e foi usado 4/4, mas os acentos sugerem {doubt['guess']}. Ouça o início e confira."
        add((bars[0]['start']+origin)/DIV,'meter_guess',text)
    if harmony.get('context_advice'):
        for diagnostic in harmony.get('diagnostics',[]):
            add(diagnostic['tick']/parsed['ppq'],diagnostic['kind'],diagnostic['message'])
    report={}
    try:
        melody_notes,_,report=melody_events(parsed,config,origin,end)
        if config.get('include_lyrics',True):
            # Karaoke syllables left out or crowded on one note are worth a look (never blocking).
            points.extend(attach_lyrics(parsed,melody_notes,origin,copy.deepcopy(bars),config).get('review_points',[]))
        for quarter in report.get('collision_original_quarters',[]):
            add(quarter,'rhythm_collision','Ataques próximos agrupados na grade; confira as notas e os ornamentos.')
        for change in report.get('program_changes_followed',[]):
            add(change['first_quarter'],'program_change',
                f"A pista da melodia troca de instrumento (programa {change['from_program']+1} → {change['to_program']+1}); "
                'as notas depois da troca continuam na melodia. Ouça o trecho para confirmar.')
        if report.get('masked_inner_attacks',0)>=4 and report.get('masked_inner_quarters'):
            # Um único ponto, no primeiro trecho: a pista da melodia também toca acompanhamento.
            add(report['masked_inner_quarters'][0],'inner_voice',
                f"{report['masked_inner_attacks']} notas mais graves tocadas sob notas longas da própria pista da melodia "
                'foram tratadas como acompanhamento e ficaram fora da melodia. Ouça o trecho para confirmar.')
        if report.get('swing'):
            add((bars[0]['start']+origin)/DIV,'swing',
                f"Colcheias tocadas com swing (razão {report['swing']['ratio']:.2f} do tempo) foram escritas retas, com a indicação Swing.")
        for item in report.get('boundary_grid_refinements',[]):
            add(item['beat_original'],'rhythm_refined',
                'Ritmo refinado automaticamente para separar ataques próximos. Confira o trecho com velocidade reduzida.')
        for item in report.get('section_boundary_adjustments',[]):
            add(item['original_quarter'],'rhythm_refined',
                'Ataque na troca de trecho ajustado automaticamente: '+('omitido, porque o trecho seguinte já toca a nota nesse tempo.'
                if item['action']=='dropped' else 'mantido no tempo em que soa.')+' Ouça o compasso para confirmar.')
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
    """Title from the file name; a name written without any accent gets them back where the word is unambiguous
    ("Coracao esta em pedacos" -> "Coração está em pedaços")."""
    title=re.sub(r'[_]+',' ',Path(filename).stem)
    return lyrics_accents.restore_title(re.sub(r'\s+',' ',title).strip()) or 'Partitura'

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

KEY_MAJOR=('Cb','Gb','Db','Ab','Eb','Bb','F','C','G','D','A','E','B','F#','C#')
KEY_MINOR=('Ab','Eb','Bb','F','C','G','D','A','E','B','F#','C#','G#','D#','A#')
SOLFEGE={'C':'Dó','D':'Ré','E':'Mi','F':'Fá','G':'Sol','A':'Lá','B':'Si'}

def key_name(key):
    """'Dó menor (Cm)' for a key signature with its mode; None when the signature is out of range."""
    fifths=key.get('fifths',0) if isinstance(key,dict) else 0
    if not -7<=fifths<=7:return None
    minor=bool(key.get('minor'))
    letter=(KEY_MINOR if minor else KEY_MAJOR)[fifths+7]
    spoken=SOLFEGE[letter[0]]+{'#':' sustenido','b':' bemol'}.get(letter[1:],'')
    return f"{spoken} {'menor' if minor else 'maior'} ({letter}{'m' if minor else ''})"

def header_info(key,meter,bpm,original_key=None,key_changes=0):
    """The line under the title: key (and the original one when transposed), time signature and tempo."""
    parts=[]
    name=key_name(key)
    if name:
        text='Tom: '+name
        if original_key is not None and key_name(original_key) and key_name(original_key)!=name:
            text+=', original '+key_name(original_key).split(' (')[0]
        if key_changes:text+=' (muda de tom)'
        parts.append(text)
    if meter:parts.append(f'Compasso {meter[0]}/{meter[1]}')
    if bpm:parts.append(f'♩ = {round(bpm)}')
    return ' · '.join(parts)

def _header_layout(metadata):
    title=_header_lines(metadata['title'],68)
    subtitle=_header_lines(metadata['subtitle'],100)
    artist=metadata.get('artist','')
    composer=('\n'.join(_header_lines(label+value,70) for label,value in
        (('Intérprete: ',artist),('Autor: ',metadata['composer'])) if value)
        if artist else _header_lines(metadata['composer'],70))
    info=_header_lines(metadata.get('info',''),100)
    title_size=20 if len(metadata['title'])<60 else 16
    lines=lambda text:len(text.splitlines()) if text else 0
    # Frame text uses absolute millimetre offsets, unlike staff-attached text.
    # A multiline title must push the subtitle below all its lines; MuseScore's
    # fixed subtitle offset does not do this automatically on MusicXML import.
    line_mm=lambda text,size:lines(text)*size*25.4/72*1.2
    subtitle_offset=max(10,line_mm(title,title_size)+1)
    content_bottom=subtitle_offset+line_mm(subtitle,10) if subtitle else line_mm(title,title_size)
    composer_offset=content_bottom+2
    # The key, time and tempo line sits on the left, level with the performer on the right; below it when
    # both would not fit on one line.
    shared=bool(info and composer) and len(info)+max(len(line) for line in composer.splitlines())<=92
    info_offset=composer_offset if shared or not composer else composer_offset+line_mm(composer,10)
    extra=max(line_mm(composer,10),info_offset-composer_offset+line_mm(info,10) if info else 0)
    height=max(10,(content_bottom+(extra+3 if composer or info else 1))/1.65)
    return dict(title=title,subtitle=subtitle,composer=composer,info=info,title_size=title_size,
                subtitle_offset=round(subtitle_offset,3),composer_offset=round(composer_offset,3),
                info_offset=round(info_offset,3),height=round(height,3))

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
    meters=summary['time_signatures']
    if not meters:
        # Sem compasso no MIDI: 3/4 ou 6/8 só quando os acentos mostram isso com folga; senão 4/4.
        import melody_rhythm
        try:meters,_=melody_rhythm.meter_guess(parsed,[])
        except Exception:meters=None
        meters=meters or [{'tick':0,'numerator':4,'denominator':4}]
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
            # The same nearest position as the main snap: an anticipation attributed to the beat sits on it.
            position=round((beat+max(0,round(phase*grids[beat]))/grids[beat])*DIV)
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
                # Uma antecipação atribuída a este tempo (fase negativa) também pode ocupar o fim do tempo anterior.
                candidates=[beat*DIV+slot*step for slot in range(-div if min(bybeat[beat])<0 else 0,div+1)
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
        # Only a position away from the attack's own nearest slot on the refined grid is an adjustment; an
        # anticipation written just before its beat is the refinement itself.
        if position!=nearest and position!=round(round(quarter*refined[beat])/refined[beat]*DIV)]
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


def _readable_stop(start,stop,next_start,end,origin,grids):
    """Notated end of a melody note: same attack, fewer tiny rests.

    MIDI melodies are often played detached, which writes a 32nd note followed by a chain of short
    rests. Only the written length changes, never an attack: a rest shorter than half a beat before
    the next attack is filled (legato), and a note shorter than an eighth with room after it is
    lengthened to the next eighth boundary. Triplet beats keep their exact lengths.
    Returns (stop, 1 if changed else 0); positions are relative to the bar origin.
    """
    half=DIV//2;limit=min(next_start,end-origin)
    def binary(position):return grids.get(int((position+origin)//DIV),4)%3!=0
    if not (binary(start) and binary(max(start,stop-1))):return stop,0
    if stop<limit and limit-stop<half and binary(limit-1):
        return limit,1
    if stop-start<half:
        rounded=-(-(stop+origin)//half)*half-origin
        if start<rounded<=limit and rounded!=stop:return rounded,1
    return stop,0

def _followed_program_sources(parsed,config,sources):
    """Troca de instrumento na pista da melodia: o mesmo (pista, canal) com outro programa continua a fonte.

    Receitas antigas guardam o programa; as notas tocadas depois de uma troca de patch na mesma pista e no mesmo
    canal (que não pertencem a outra fonte escolhida) seguem a fonte. Retorna (fontes acrescentadas, trocas)."""
    if config.get('follow_program_changes') is False or config.get('melody_sections'):return [],[]
    from melody_sections import all_sources
    claimed={tuple(s.get(k) for k in ('track','channel','program')) for s in all_sources(config)+config.get('solo_sources',[])}
    extra=[];changes=[]
    for source in sources:
        if 'program' not in source or 'track' not in source or 'channel' not in source:continue
        found={}
        for note in parsed['notes']:
            if note['track']!=source['track'] or note['channel']!=source['channel'] or note['program']==source['program']:continue
            if (note['track'],note['channel'],note['program']) in claimed:continue
            found[note['program']]=min(found.get(note['program'],note['start']),note['start'])
        for program,first in sorted(found.items(),key=lambda item:item[1]):
            added=dict(source,program=program,_followed_from=source['program'])
            extra.append(added);claimed.add((source['track'],source['channel'],program))
            changes.append({'track':source['track'],'channel':source['channel'],'from_program':source['program'],
                            'to_program':program,'first_quarter':first/parsed['ppq']})
    return extra,changes

def melody_events(parsed,config,origin,end):
    import melody_rhythm as rhythm
    ppq=parsed['ppq'];sources=list(config['melody_sources'])
    followed,program_changes=_followed_program_sources(parsed,config,sources)
    if followed:
        # Cada fonte acompanhada fica logo depois da fonte original, com a mesma prioridade relativa.
        extended=[]
        for source in sources:
            extended.append(source)
            extended.extend(f for f in followed if all(f[k]==source.get(k) for k in ('track','channel')) and f['_followed_from']==source.get('program'))
        sources=extended
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
    # Voz interna da própria pista: ataques graves sob uma nota aguda que continua soando não são melodia.
    cluster=config.get('onset_cluster_quarters',.04)
    masked=[]
    if config.get('mask_inner_voice',True):
        # Os solos instrumentais acrescentados nas lacunas (acordeom, teclado) têm textura polifônica própria e
        # foram aprovados assim: a regra vale para as fontes da melodia.
        solos=[n for n in selected if n.get('_role')=='solo']
        selected,masked=rhythm.mask_inner_by_source([n for n in selected if n.get('_role')!='solo'],ppq,cluster=cluster)
        if solos:selected=sorted(selected+solos,key=lambda n:(n['start'],n['pitch']))
    merge_report=None
    if sections or config.get('merge_mode')=='priority' and len(sources)>1:
        top,merge_report=priority_melody_notes(selected,sources,ppq,cluster)
    else:
        # Reviewed profiles and single-source selections retain their exact path.
        groups=[]
        for n in selected:
            if not groups or n['start']-groups[-1][0]['start']>ppq*cluster:groups.append([n])
            else:groups[-1].append(n)
        top=[max(g,key=lambda n:n['pitch']) for g in groups]
    try:_,meters,tempos=meta(parsed)
    except Exception:meters,tempos=[{'tick':0,'numerator':4,'denominator':4}],[{'tick':0,'bpm':120}]
    tempo=rhythm.TempoMap(tempos,ppq);meter_map=rhythm.MeterMap(meters,ppq)
    # Swing: colcheias longa-curta são escritas retas (a indicação "Swing" vai na partitura).
    swing=None
    if config.get('swing','auto')!='off':
        swing=rhythm.swing_estimate([n['start']/ppq for n in top])
        if swing:
            ratio=swing['ratio'];straight=[]
            for n in top:
                start=round(rhythm.straighten(n['start']/ppq,ratio)*ppq)
                stop=max(start+1,round(rhythm.straighten(n['end']/ppq,ratio)*ppq))
                straight.append(dict(n,start=start,end=stop,_swing_start=n['start']))
            top=straight
    # Execução sistematicamente adiantada ou atrasada: a grade é escolhida depois de compensar o deslocamento local.
    timing=rhythm.TimingOffset([n['start']/ppq for n in top],tempo)
    def shifted(q):return q+timing.at(q)
    bybeat=defaultdict(list);attack_quarter={}
    for n in top:
        beat,phase=rhythm.attribute(shifted(n['start']/ppq),tempo);bybeat[beat].append(phase)
        attack_quarter[id(n)]=beat+phase
    grids,grid_choices,pair_starts,triplet_beats,collided=rhythm.choose_grids(bybeat,tempo,meter_map)
    initial_grids=dict(grids);attack_positions={};attack_adjustments=[]
    # Ataques de tempos vizinhos que se juntam mesmo na grade mais fina (uma antecipação atribuída ao tempo
    # seguinte) vão direto para a separação limitada; colisões internas toleradas seguem o caminho antigo.
    # Ataques quase simultâneos (até o agrupamento de onset, como um ornamento de 10 ms) seguem o caminho antigo, em
    # que o mais forte representa os dois; só ataques realmente distintos pedem a separação.
    def distinct(phases):
        ordered=sorted(phases);slots=[max(0,round(p*16)) for p in ordered]
        return any(slots[i]==slots[i+1] and ordered[i+1]-ordered[i]>cluster for i in range(len(ordered)-1))
    crossing=any(any(p<0 for p in bybeat[b]) and distinct(bybeat[b]) for b in collided)
    try:
        if crossing:raise NeedsReview('Ataques vizinhos na fronteira do tempo.')
        boundary_refinements=_refine_boundary_grids(bybeat,grids,grid_choices,origin,end)
    except NeedsReview:
        grids=initial_grids
        attack_positions,boundary_refinements,attack_adjustments=_separate_quantized_attacks(bybeat,grids,grid_choices,origin,end)
    def snap(t):
        beat,phase=rhythm.attribute(shifted(t/ppq),tempo);div=rhythm.grid_div(grids.get(beat,4))
        # Near a beat boundary use the shared endpoint; an anticipation attributed to the beat is written on it.
        return round((beat+max(0,round(phase*div))/div)*DIV)
    if not attack_positions:
        nearest=[snap(n['start']) for n in top if origin<=snap(n['start'])<end]
        if len(nearest)-len(set(nearest))>(0 if merge_report is not None else max(3,len(top)*.008)):
            attack_positions,_,attack_adjustments=_separate_quantized_attacks(bybeat,grids,grid_choices,origin,end)
            boundary_refinements=[{'beat_original':beat,'from':initial_grids[beat],'to':div}
                for beat,div in sorted(grids.items()) if div!=initial_grids.get(beat)]
    pairs=rhythm.encode_pairs(grids,pair_starts)
    # A partitura mede os tempos pelo início: o tempo que recebe uma antecipação precisa de grade que a represente.
    final_positions=sorted({attack_positions.get(attack_quarter[id(n)],snap(n['start'])) for n in top})
    grids,floor_refined=rhythm.floor_grids(final_positions,grids,DIV)
    known={item['beat_original'] for item in boundary_refinements}
    boundary_refinements=boundary_refinements+[{'beat_original':beat,'from':old,'to':new}
        for beat,old,new in floor_refined if beat not in known]
    onsetmap={};collision=0;collision_quarters=[];section_adjustments=[]
    positions=[(n,attack_positions.get(attack_quarter[id(n)],snap(n['start']))) for n in top]
    # An attack played a hair before a section change snaps onto the next section.
    # The incoming section owns that position: a duplicate attack there is dropped,
    # otherwise the note stays where it is heard, with its real length.
    owned={p for n,p in positions if sections and n['_section_start']<=p<n['_section_end']}
    for n,position in positions:
        s=position-origin
        if sections and not n['_section_start']<=position<n['_section_end']:
            dropped=position in owned
            section_adjustments.append({'original_quarter':n['start']/ppq,'quantized_quarter':position/DIV,
                                        'action':'dropped' if dropped else 'kept'})
            if dropped:continue
            n=dict(n,_crossing=True)
        if not 0<=s<end-origin:continue
        if s in onsetmap:
            collision+=1
            collision_quarters.extend((n['start']/ppq,onsetmap[s]['start']/ppq))
        if s not in onsetmap or n['velocity']>onsetmap[s]['velocity']:onsetmap[s]=n
    if collision>max(3,len(top)*.008):raise NeedsReview(f'{collision} ataques colidem após quantização; requer revisão rítmica individual.',sorted(set(collision_quarters)))
    events=[];errors=[];onsets=sorted(onsetmap);durations_cleaned=0
    # Primeiro passo: a duração de cada nota sem o legato e se o legato a preencheria.
    plans=[]
    for i,s in enumerate(onsets):
        n=onsetmap[s];next_s=onsets[i+1] if i+1<len(onsets) else end-origin
        # A note moved across a section change was clipped at the old section's end; use its MIDI length.
        note_end=n.get('_source_end',n['end']) if n.get('_crossing') else n['end']
        if n.get('_crossing') and swing:note_end=round(rhythm.straighten(note_end/ppq,swing['ratio'])*ppq)
        stop=snap(note_end)-origin
        fill=False
        if i+1<len(onsets):
            gap=onsetmap[next_s]['start']-note_end
            fill=rhythm.legato_fill(gap,note_end-n['start'],ppq,tempo.quarter_ms(n['start']/ppq))
        beat=rhythm.attribute((s+origin)/DIV,tempo)[0];minimum=DIV//rhythm.grid_div(grids.get(beat,4))
        plans.append((s,n,next_s,stop,fill,minimum))
    # Uma pausa curta que se repete no mesmo ponto das frases repetidas é articulação: fica escrita.
    def bar_of(position):
        quarter=(position+origin)/DIV;start,_,_=meter_map.at(quarter)
        return round(quarter-start,4),round(start,4)
    candidates=[]
    for index,(s,n,next_s,stop,fill,minimum) in enumerate(plans):
        visible=min(next_s,end-origin,max(s+minimum,stop))
        # Só a respiração depois de uma nota de um tempo ou mais (semínima pontuada e pausa de semicolcheia, por
        # exemplo). Colcheias e semicolcheias destacadas continuam escritas com o legato de sempre, sem pausas miúdas.
        if fill and next_s-visible>=DIV//4 and visible-s>=DIV and index+1<len(plans):
            candidates.append((s,n['pitch'],plans[index+1][1]['pitch'],visible-s,next_s-visible))
    # Desligado por padrão: nas partituras reais, a respiração repetida escrita trazia ligaduras e pausas miúdas
    # (semínima ligada a semicolcheia e pausa) onde o legato aprovado escreve uma figura só.
    preserved_keys=rhythm.repeated_rest_keys(candidates,bar_of) if config.get('preserve_repeated_rests') else set()
    preserved=set()
    for s,pitch,following,duration,rest in candidates:
        offset,_=bar_of(s)
        if (offset,pitch,following,duration,rest) in preserved_keys:preserved.add(s)
    for s,n,next_s,stop,fill,minimum in plans:
        if fill and s not in preserved:stop=next_s
        stop=min(next_s,end-origin,max(s+minimum,stop))
        if config.get('clean_durations') and s not in preserved:
            stop,cleaned=_readable_stop(s,stop,next_s,end,origin,grids)
            durations_cleaned+=cleaned
        if sections and not n.get('_crossing'):stop=min(stop,n['_section_end']-origin)
        if stop>s:
            event={'start':s,'end':stop,'pitch':n['pitch']}
            gs=[g['pitch'] for g in grace_notes if abs(n.get('_swing_start',n['start'])/ppq-g['target_quarter'])<.001]
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
    report={'raw_notes':len(selected)+len(masked),'principal_source_notes':main_count,'grace_notes_preserved':len(grace_notes),'solo_notes_added':solo_count,'simultaneous_notes_reduced':len(selected)-len(top),'melody_attacks':len(events),'quantized_attack_collisions':collision,'triplet_beats_original':triplet_beats,'onset_adjustment_max_quarters':max(errors),'onset_adjustment_mean_quarters':sum(errors)/len(errors)}
    if masked:
        report['masked_inner_attacks']=len(masked)
        report['masked_inner_quarters']=sorted({round(n['start']/ppq,4) for n in masked})[:200]
    if swing:report['swing']=swing
    if timing.offsets:report['timing_offset_windows']=len(timing.offsets)
    if pairs:report['quarter_triplet_groups']=[beat-int(origin//DIV) for beat in pairs]
    if preserved:report['preserved_rests']=len(preserved)
    if program_changes:report['program_changes_followed']=program_changes
    if config.get('clean_durations'):report['durations_cleaned']=durations_cleaned
    if boundary_refinements:report['boundary_grid_refinements']=boundary_refinements
    if attack_adjustments:report['attack_position_adjustments']=attack_adjustments
    if section_adjustments:report['section_boundary_adjustments']=section_adjustments
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
    from inspect_midi import decode_raw
    return decode_raw(data).strip('\x00\r\n')

_CREDIT=re.compile(r'@|https?:|www\.|\.com\b|\.br\b',re.I)

def karaoke_syllables(parsed):
    """Syllables of the song text: lyric events (type 5), else karaoke text events (type 1, @ headers skipped).

    Each syllable keeps where the karaoke starts a word, a line ('/' before it, or a line break after the previous
    one) and a paragraph ('\\' before it, or a blank line). The texts are decoded together, so a UTF-8 character
    split between two events is kept. Fewer than 16 syllables usually means credits or comments, not lyrics, so
    nothing is returned.
    """
    from inspect_midi import decode_texts
    metas=[e for e in parsed.get('events',[]) if e.get('kind')=='meta']
    source=[e for e in metas if e['type']==5]
    karaoke=len(source)<16
    if karaoke:source=[e for e in metas if e['type']==1 and not _lyric_text(e['data']).lstrip().startswith('@')]
    source=sorted(source,key=lambda e:(e['tick'],e['order']))
    texts=decode_texts([e['data'] for e in source])
    # Lyric events usually mark the syllables of a word with a hyphen ("Ma-", "ri-", "a "); many karaoke
    # conversions write the word end as a trailing space instead ("Se", "gun", "da "), as text events do.
    spaces=karaoke or sum(t.strip('\x00\r\n ').endswith('-') for t in texts)<max(2,len(texts)//20)
    syllables=[];previous=None;line=paragraph=True
    for event,raw in zip(source,texts):
        raw=raw.strip('\x00')
        lead=raw[:len(raw)-len(raw.lstrip(' \t\r\n/\\'))]
        if '/' in lead or '\r' in lead or '\n' in lead:line=True
        if '\\' in lead:line=paragraph=True
        text=raw.strip('\r\n')
        # A new word starts after a space, slash or line mark, whether it is written at the start of
        # this syllable or at the end of the previous one ("o " + "seu").
        if spaces:starts=not syllables or text[:1] in (' ','/','\\') or (previous or '').endswith((' ','\t'))
        else:starts=previous is None or not previous.rstrip().endswith('-')
        clean=text.lstrip('/\\').strip()
        if clean.endswith('-'):clean=clean[:-1].rstrip()
        previous=text
        trail=raw[len(raw.rstrip(' \t\r\n')):]
        breaks=trail.count('\n') or trail.count('\r')
        # Credits mixed into the lyrics (e-mail, web address) are not sung.
        if clean and not _CREDIT.search(clean):
            syllables.append({'tick':event['tick'],'text':clean,'word_start':starts or line,
                              'line_start':line,'paragraph_start':paragraph})
            line=paragraph=False
        if breaks:line=True
        if breaks>=2:paragraph=True
    if len(syllables)<16:return []
    fix_split_digraphs(syllables)
    if lyrics_accents.needs_restoring([s['text'] for s in syllables]):
        restored=lyrics_accents.restore(syllables)
        if restored:syllables[0]['accents_restored']=restored
    return syllables

def fix_split_digraphs(syllables):
    """nh, lh and ch are never split in Portuguese: karaoke files often write "vin"+"ha" or "fil"+"ho".
    Inside one word the consonant moves to the next syllable ("vi"+"nha"); the timing stays the same."""
    for previous,current in zip(syllables,syllables[1:]):
        if current['word_start']:continue
        a,b=previous['text'],current['text']
        if (len(a)>=2 and a[-1].lower() in 'nlc' and a[-2].isalpha() and len(b)>=2 and b[0].lower()=='h'
                and b[1].lower() in 'aeiouáéíóúâêôãõ'):
            previous['text'],current['text']=a[:-1],a[-1]+b
    return syllables

def lyrics_lines(syllables,ppq,origin=0,bars=None,kinds=None,groups=None):
    """Lines of the lyric for the lyric page: tick (MIDI), quarter, measure, text and paragraph number (from 1)."""
    import lyrics_align
    events=[dict(s,pos=round(s['tick']/ppq*DIV)-origin) for s in syllables]
    if groups is None:groups,_=lyrics_align.group_lines(events,DIV)
    lines=[];paragraph=0
    for number,indices in enumerate(groups):
        first=syllables[indices[0]]
        if first.get('paragraph_start') or not lines:paragraph+=1
        item={'tick':first['tick'],'quarter':round(first['tick']/ppq,6),'text':lyrics_align.line_text(syllables,indices),'paragraph':paragraph}
        if bars:
            position=events[indices[0]]['pos']
            index=next((i for i,b in enumerate(bars) if b['start']<=position<b['end']),None)
            item['measure']=None if index is None else index+1
            if index is not None:item['measure_position']=round((position-bars[index]['start'])/(bars[index]['end']-bars[index]['start']),3)
        if kinds:item['placement']={'sung':'notes','spoken':'spoken','text':'text'}[kinds[number]]
        lines.append(item)
    return lines

def _solo_positions(parsed,config,origin):
    """Score positions of solo-source attacks that the main melody sources do not play."""
    if not config or not config.get('solo_sources'):return []
    ppq=parsed['ppq'];main=config.get('melody_sources',[])
    return sorted({round(n['start']/ppq*DIV)-origin for n in parsed['notes']
                   if source_matches(n,config['solo_sources']) and not source_matches(n,main)})

def attach_lyrics(parsed,notes,origin,bars=None,config=None):
    """Puts the karaoke syllables on the melody notes (lyrics_align.py) and reports what did not fit.

    Words written whole by the karaoke are split into syllables over the notes until the next word; two words on one
    note are joined by the elision sign; speech over an instrumental part becomes text "(falado: …)" and a sung line
    the chosen melody does not play becomes text under its first measure. When the melody does not carry the song
    (fewer than 40% of the syllables fit), every line is written under its first measure ('lines' mode).
    """
    import lyrics_align
    syllables=karaoke_syllables(parsed)
    attacks=sorted(notes,key=lambda n:n['start'])
    if not syllables:return {'syllables':0,'attached':0,'sung_ticks':[]}
    config=config or {}
    mode=config.get('lyrics_mode','auto')
    if mode not in ('auto','notes','lines'):mode='auto'
    ppq=parsed['ppq']
    events=[{'pos':round(s['tick']/ppq*DIV)-origin,'text':s['text'],'word_start':s['word_start'],
             'line_start':s.get('line_start',False),'paragraph_start':s.get('paragraph_start',False)}
            for s in syllables]
    plan=lyrics_align.plan(events,attacks,DIV,bars=bars,mode=mode,solo_positions=_solo_positions(parsed,config,origin))
    ordered=plan['attacks'];detail=plan['report'];placed=detail['events']
    crowded=[];attached=0;placed_events=set()
    for k,pieces in sorted(plan['placements'].items()):
        note=ordered[k];lyric,words=lyrics_align.note_lyric(pieces)
        note['lyric']=lyric;attached+=1
        placed_events.update(piece['event'] for piece in pieces)
        # Three or more sung words on one note: the melody line is probably not the sung voice.
        if words>=3:crowded.append(note['start'])
    for k in plan['extend_stop']:ordered[k]['lyric_extend_stop']=True
    def where(position,text=None):
        quarter=(position+origin)/DIV
        item={'quarter':round(quarter,6),'tick':round(quarter*ppq)}
        if bars:item['measure']=next((i+1 for i,b in enumerate(bars) if b['start']<=position<b['end']),None)
        if text is not None:item['text']=text
        return item
    spoken=[];texts=[]
    for item in plan['texts']:
        text=f"(falado: {item['text']})" if item['kind']=='spoken' else item['text']
        entry=dict(where(item['pos'],text),kind=item['kind'])
        if item['kind']=='spoken':spoken.append(entry)
        texts.append(dict(entry,pos=item['pos']))
    if bars:_anchor_lyric_texts(texts,bars,attacks)
    spoken_events={i for number,kind in enumerate(detail['kinds']) if kind=='spoken' for i in detail['lines'][number]}
    result={'syllables':len(syllables),'attached':attached,'share':round(len(placed_events)/len(syllables),3),
            'mode':detail['mode'],'requested_mode':mode,
            'dropped':[where(placed[i]['pos'],placed[i]['text']) for i in detail['dropped']],
            'crowded':[where(start,ordered_text) for start,ordered_text in ((s,next((n['lyric']['text'] for n in ordered if n['start']==s),'')) for s in crowded)],
            'spoken':spoken,'text_lines':[{k:v for k,v in t.items() if k!='pos'} for t in texts if t['kind']=='text'],
            'lines':lyrics_lines(syllables,ppq,origin,bars,detail['kinds'],detail['lines']),
            'offset_quarters':round(detail['offset']/DIV,4),'match_estimate':detail['match_estimate'],
            'line_marks':detail['line_marks'],
            # When each word is heard (score ticks), also for words left off the melody; speech is not sung.
            'sung_ticks':[e['pos'] for i,e in enumerate(placed) if i not in spoken_events]}
    if crowded:result['crowded_notes']=crowded
    if syllables[0].get('accents_restored'):result['accents_restored']=syllables[0]['accents_restored']
    if bars:result['review_points']=lyrics_review_points(result,origin,bars)
    return result

def _anchor_lyric_texts(texts,bars,attacks):
    """Each spoken or unsung line goes to the measure where it starts, at the last attack before it (or the bar start)."""
    for item in texts:
        index=next((i for i,b in enumerate(bars) if b['start']<=item['pos']<b['end']),None)
        if index is None:index=0 if item['pos']<bars[0]['start'] else len(bars)-1
        bar=bars[index]
        if index+1<len(bars) and item['pos']-bar['start']>=.75*(bar['end']-bar['start']):
            # A line that starts on the last beat (pickup) is written under the next measure.
            index+=1;bar=bars[index];item=dict(item,pos=bar['start'])
        candidates=[n['start'] for n in attacks if bar['start']<=n['start']<=item['pos']+DIV//8]
        offset=(max(candidates) if candidates else bar['start'])-bar['start']
        existing=next((t for t in bar.setdefault('lyric_texts',[]) if t['offset']==offset),None)
        if existing:existing['text']+=' / '+item['text']
        else:bar['lyric_texts'].append({'offset':offset,'text':item['text'],'kind':item['kind']})

def lyrics_review_points(lyrics,origin,bars):
    """Non-blocking review points: one per measure with syllables left out or crowded, one when the lyric went by lines."""
    points=[]
    if lyrics.get('mode')=='lines':
        point=locate_review_point((bars[0]['start']+origin)/DIV,origin,bars,'lyrics_dropped',
            f"Só {lyrics.get('match_estimate',0):.0%} das sílabas casam com a melodia escolhida: a letra foi escrita por versos, "
            'sob o primeiro compasso de cada um. Escolha a pista do canto para ter a letra sob as notas.')
        if point is not None:points.append(point)
        return points
    for kind,items,message in (
            ('lyrics_dropped',lyrics.get('dropped',[]),'{n} sílaba(s) da letra sem nota próxima ficaram fora: {words}. Confira a melodia e a letra neste compasso.'),
            ('lyrics_crowded',lyrics.get('crowded',[]),'Três ou mais palavras na mesma nota ({words}). Confira se a melodia é a voz cantada.')):
        by_measure={}
        for item in items:by_measure.setdefault(item.get('measure'),[]).append(item)
        groups=[(measure,group) for measure,group in by_measure.items() if measure is not None]
        for number,(measure,group) in enumerate(groups[:LYRIC_POINTS]):
            words=', '.join(f"“{item['text']}”" for item in group[:6] if item.get('text'))
            text=message.format(n=len(group),words=words)
            if number==LYRIC_POINTS-1 and len(groups)>LYRIC_POINTS:text+=f' O mesmo acontece em mais {len(groups)-LYRIC_POINTS} compasso(s).'
            point=locate_review_point(group[0]['quarter'],origin,bars,kind,text)
            if point is not None:points.append(point)
    return points

LYRIC_POINTS=12  # review points of each kind; the last one counts the remaining measures

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

def diagram_flats(chord,report=None):
    """Key names follow the chord's spelling (Bb → flats); a natural root follows the key signature."""
    spelling=str(chord.get('root_spelling') or '')
    if 'b' in spelling[1:]:return True
    if '#' in spelling:return False
    key=(report or {}).get('key')
    fifths=key.get('fifths',0) if isinstance(key,dict) else 0
    return fifths<0

def keyboard_asset(rootpc,quality,inversion=2,c_label='C',style='classico',flats=False,spelling=None):
    """(file name, SVG bytes, MIDI pitches) of one diagram. 'classico' is the original two-octave drawing
    (byte-identical to delivered scores); 'pontos' dots and names the chord's keys, 'destacado' and 'cinza' fill them."""
    inversion=diagram_inversion(inversion)
    style=keyboard_svg.diagram_style(style)
    intervals=CHORD_INTERVALS[quality]
    # First inversion: the major third when both thirds sound (7#9), else the third or the suspended note.
    bass_interval=0 if inversion==0 else next(x for x in ((4,3,2,5) if inversion==1 else (7,6,8)) if x in intervals)
    bass=53+((rootpc+bass_interval-53)%12)
    pitches=sorted(bass+(rootpc+x-bass)%12 for x in intervals)
    if max(pitches)>71:pitches=[p-12 for p in pitches]
    assert 48<=min(pitches)<max(pitches)<=71
    if style!='classico':
        names='solfejo' if c_label!='C' else 'letras'
        spelled=keyboard_svg.tone_names(rootpc,intervals,spelling,flats,names)
        svg=E.fromstring(keyboard_svg.svg_highlight(pitches,style,names,flats,spelled))
        svg.find(NS+'title').text=('Acorde em posição fundamental','Acorde em primeira inversão','Acorde em segunda inversão')[inversion]
        svg.find(NS+'desc').text=('Fundamental no grave','Terça ou nota suspensa no grave','Quinta no grave')[inversion]+'; notas MIDI: '+','.join(map(str,pitches))
        data=E.tostring(svg,encoding='utf-8',xml_declaration=True)
        return md4(data)+'.svg',data,pitches
    label=str(rootpc)+quality;svg=E.fromstring(svg_keyboard(label,pitches))
    svg.find(NS+'title').text=('Acorde em posição fundamental','Acorde em primeira inversão','Acorde em segunda inversão')[inversion]
    label=('Fundamental no grave','Terça ou nota suspensa no grave','Quinta no grave')[inversion]
    svg.find(NS+'desc').text=label+'; notas MIDI: '+','.join(map(str,pitches))
    if c_label!='C':
        for text in svg.iter(NS+'text'):text.text=c_label
    data=E.tostring(svg,encoding='utf-8',xml_declaration=True)
    return md4(data)+'.svg',data,pitches

# Tempos por etapa de uma geração, gravados em validacao.json ('timings') e lidos pelo app
# (EngineClient) para o registro local de desempenho. Só nomes fixos e números entram aqui.
TIMINGS={}
_TIMING_DEPTH={}

def reset_timings():
    TIMINGS.clear();_TIMING_DEPTH.clear()
    TIMINGS.update(started=time.perf_counter(),stages={},musescore_calls=[],spacing_passes=0,conversions=0)

@contextmanager
def timed(stage):
    """Soma o tempo de uma etapa; chamadas aninhadas da mesma etapa contam uma vez."""
    if not TIMINGS:reset_timings()
    depth=_TIMING_DEPTH.get(stage,0);_TIMING_DEPTH[stage]=depth+1;start=time.perf_counter()
    try:yield
    finally:
        _TIMING_DEPTH[stage]=depth
        if not depth:TIMINGS['stages'][stage]=TIMINGS['stages'].get(stage,0)+time.perf_counter()-start

def count_timing(name,amount=1):
    if not TIMINGS:reset_timings()
    TIMINGS[name]=TIMINGS.get(name,0)+amount

def timings_snapshot():
    """Segundos por etapa (arredondados), cada chamada ao MuseScore com o tipo e os contadores."""
    if not TIMINGS:return {}
    stages={name:round(value,3) for name,value in TIMINGS['stages'].items()}
    stages['total']=round(time.perf_counter()-TIMINGS['started'],3)
    return {'version':1,'stages':stages,'musescore_calls':[dict(call) for call in TIMINGS['musescore_calls']],
            'spacing_passes':TIMINGS.get('spacing_passes',0),'conversions':TIMINGS.get('conversions',0)}

_native_analyze_svg=analyze_svg
def analyze_svg(path):
    with timed('svg_analysis'):return _native_analyze_svg(path)

def call_musescore(args,log,timeout=90):
    messages={
        'importacao':'Criando a partitura no formato MuseScore…',
        'gravacao':'Salvando a partitura com os diagramas de teclado…',
        'render':'Gerando as páginas da prévia para conferência…',
        'ajuste_paginacao':'Reorganizando as quebras de página…',
        'ajuste_espacamento':'Aplicando ajustes de espaço entre cifras e diagramas…',
    }
    progress(messages.get(log.stem,'Processando a partitura no MuseScore…'))
    start=time.perf_counter()
    try:
        with timed('musescore'):p=subprocess.run([EXE]+args,capture_output=True,text=True,errors='replace',timeout=timeout)
    finally:
        if not TIMINGS:reset_timings()
        TIMINGS['musescore_calls'].append({'kind':log.stem,'seconds':round(time.perf_counter()-start,3)})
    log.write_text(p.stdout+'\n'+p.stderr,encoding='utf8')
    if p.returncode:raise NeedsReview(f'MuseScore retornou {p.returncode}; veja {log.name}.',kind='musescore')

def save_and_render(prepared,final,folder,log,extra=()):
    """Grava o .mscz e o SVG de conferência num único processo do MuseScore (job -j).

    Substitui o par antigo '-o final.mscz' + '-j render.json' (precedente: build_keyboard_diagrams).
    Conferido em MIDIs reais: o .mscx gravado é idêntico (exceto os <eid> aleatórios), as páginas
    PNG saem iguais pixel a pixel e a geometria analisada dos qa-*.svg é a mesma (no máximo 0,01
    unidade SVG de diferença num rótulo de diagrama). PNG e reaberto.musicxml ficam para o fim
    (render_final_pages), quando o .mscz salvo é reaberto."""
    for pattern in ('pagina-*.png','qa-*.svg'):
        for old in folder.glob(pattern):old.unlink()
    job=folder/(log.stem+'-job.json')
    job.write_text(json.dumps([{'in':str(prepared),'out':[str(final),str(folder/'qa.svg'),*map(str,extra)]}]),encoding='utf8')
    call_musescore(['-r','120','-j',str(job)],log,180)

def render_final_pages(final,out):
    """Reabre o .mscz salvo e gera as páginas PNG e o reaberto.musicxml usados na conferência final."""
    for old in out.glob('pagina-*.png'):old.unlink()
    (out/'render.json').write_text(json.dumps([{'in':str(final),'out':[str(out/'pagina.png'),str(out/'reaberto.musicxml')]}]),encoding='utf8')
    call_musescore(['-r','120','-j',str(out/'render.json')],out/'render.log',150)
    if len(list(out.glob('pagina-*.png')))!=len(list(out.glob('qa-*.svg'))):
        raise NeedsReview('As páginas da partitura reaberta não conferem com as páginas verificadas. Gere novamente a partitura.',kind='musescore')

def page_order(path):return int(path.stem.rsplit('-',1)[-1])

# Rendered-layout repairs live in score_layout.py; re-exported for callers and tests.
from score_layout import repair_orphan_pages, _compact_diagram_offsets, repair_diagram_lanes, repair_spacing

# Instrument-name layout lives in score_labels.py; re-exported for callers and tests.
from score_labels import instrument_label_geometry, _instrument_label_lanes, verify_instrument_label_layout, compact_instrument_label_layout, layout_with_instrument_labels


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
    # Key, time signature and tempo: a left-aligned line in the header (the lyricist text style sits on the left).
    for text in [t for t in vbox.findall('Text') if t.findtext('style')=='lyricist']:vbox.remove(text)
    if header.get('info'):
        if not metadata.get('artist'):
            for name,value in (('boxAutoSize','0'),('sizeIsSpatiumDependent','1')):
                element=vbox.find(name)
                if element is None:element=sub(vbox,name)
                element.text=value
        text=sub(vbox,'Text');sub(text,'style','lyricist');sub(text,'size',10);sub(text,'align','left,top')
        E.SubElement(text,'offset',x='0',y=str(header['info_offset']))
        content=sub(text,'text');before,_,after=header['info'].partition('♩')
        content.text=before
        if _:
            # The quarter-note glyph of the music font, as in MuseScore's own tempo texts.
            sym=sub(content,'sym','metNoteQuarterUp');sym.tail=after
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

# Espaço entre as pautas (Configurações): "compacto" põe mais sistemas por página, "amplo" deixa
# mais respiro para anotar. As folgas mínimas para diagramas, nomes e letras continuam garantidas.
SCORE_SPACINGS=('compacto','normal','amplo')
AMPLE_EXTRA_SPATIUM=6

def score_spacing(choice):
    value=(choice or {}).get('spacing')
    return value if value in SCORE_SPACINGS else 'normal'

def spacing_limits(spacing,low,high):
    """System distance range for the chosen spacing; the minimum keeps every engine allowance."""
    if spacing=='amplo':return low+AMPLE_EXTRA_SPATIUM,high+AMPLE_EXTRA_SPATIUM+2
    # Vertical justification would stretch compact systems again to fill the page.
    if spacing=='compacto':return low,low+2
    return low,high

def apply_score_spacing(style_values,spacing):
    if spacing=='normal':return style_values
    low,high=spacing_limits(spacing,float(style_values['minSystemDistance']),float(style_values['maxSystemDistance']))
    spread=max(32.0,high) if spacing=='amplo' else high
    style_values.update(minSystemDistance=str(low),maxSystemDistance=str(high),minSystemSpread=str(low),maxSystemSpread=str(spread))
    return style_values

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
    lyrics_attached=isinstance(lyrics,dict) and bool(lyrics.get('attached'))
    if lyrics_attached:
        # The words under the staff need room before the next system's chord names and keyboard diagrams.
        style_values.update(minSystemDistance=str(max(float(style_values['minSystemDistance']),17)),maxSystemDistance=str(max(float(style_values['maxSystemDistance']),24)))
        # A visible gap between neighbouring words: MuseScore's 0.25 sp lets "deixar" and "você" touch.
        style_values['lyricsMinDistance']='1.2'
        report['lyrics_min_distance']=1.2
    style_values.update(score_style_values(report.get('score_style')))
    if report.get('staff_per_source'):
        # Instrument staves that only rest in a system are hidden (the first system shows them all).
        # Vertical spread uses the system spread, not the system distance: keep the same room for the
        # lyrics and the raised keyboard diagrams once the systems become shorter.
        style_values.update(hideEmptyStaves='1',dontHideStavesInFirstSystem='1',
                            minSystemSpread=style_values['minSystemDistance'],
                            maxSystemSpread=str(max(32,float(style_values['maxSystemDistance']))))
        report['hidden_empty_staves']=True
    spacing=score_spacing(report.get('score_style'));report['score_spacing']=spacing
    apply_score_spacing(style_values,spacing)
    footer=report.get('page_footer',False)
    if footer:
        # "página X de Y" on every page, so loose printed pages stay in order (the title is already at the top).
        text='página $P de $n'
        style_values.update(showFooter='1',footerFirstPage='1',footerOddEven='0',oddFooterC=text,evenFooterC=text,
                            oddFooterL='',oddFooterR='',evenFooterL='',evenFooterR='',
                            # The footer already numbers the pages; the corner number would sit on the first diagrams.
                            showPageNumber='0',showHeader='0')
        report['page_footer']=text
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
    if report.get('hidden_empty_staves'):
        # The top staff carries the chord names and keyboard diagrams: it is never hidden.
        top=root.find('Score/Part/Staff')
        if top is not None:
            hide=top.find('hideWhenEmpty')
            if hide is None:
                hide=E.Element('hideWhenEmpty');staff_type=top.find('StaffType')
                top.insert(list(top).index(staff_type)+1 if staff_type is not None else 0,hide)
            hide.text='2'
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
                path,data,pitches=keyboard_asset(c['root'],c['quality'],inversion,'Dó' if report.get('note_names')=='solfejo' else 'C',
                                                 report.get('diagram_style','classico'),diagram_flats(c,report),c.get('root_spelling'));assets['Pictures/'+path]=data
                asset_manifest[path]={'root':c['root'],'quality':c['quality'],'pitches':pitches,'diagram_inversion':inversion,'diagram_style':report.get('diagram_style','classico')}
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
            header_tempo=bool((report.get('score_metadata') or {}).get('info')) and measure_index==0
            for number,tempo in enumerate(voice.findall('Tempo')):
                off=tempo.find('offset')
                if off is None:off=E.SubElement(tempo,'offset')
                off.set('x','0');off.set('y',str((-13.5 if labels else -8.5)-lift));sub(tempo,'autoplace',0)
                if header_tempo and number==0:
                    # The opening tempo is printed in the header line; the mark stays for playback, hidden.
                    sub(tempo,'visible',0);report['opening_tempo_hidden']=True
            for mark in voice.findall('RehearsalMark'):
                # Section name just above the keyboard diagrams (and above instrument names or a visible
                # opening tempo). Its height is reserved over the staff, so it never reaches the system above.
                visible_tempo=measure_index==0 and voice.findall('Tempo') and not header_tempo
                y=(-12.8 if labels else -7.8)-lift-(2.6 if visible_tempo else 0)
                off=mark.find('offset')
                if off is None:off=E.SubElement(mark,'offset')
                off.set('x','0');off.set('y',str(round(y,3)))
                for tag,value in (('size','11'),('autoplace','0')):
                    element=mark.find(tag)
                    if element is None:element=sub(mark,tag)
                    element.text=value
                top=round(-y+3.2+(6 if lyrics_attached else 4),3)
                spacer=measure.find('vspacerUp')
                if spacer is None or float(spacer.text or 0)<top:
                    if spacer is None:spacer=sub(measure,'vspacerUp')
                    spacer.text=str(top)
                report.setdefault('section_marks_placed',[]).append({'measure':measure_index+1,'text':''.join(mark.find('text').itertext()) if mark.find('text') is not None else '','y':round(y,3),'reserved':top})
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
        # Room under the header for the key/tempo line and for a section name on the first bar.
        first_mark=any(item['bar']==0 for item in report.get('melody_analysis',{}).get('sections',[]))
        info=bool((report.get('score_metadata') or {}).get('info'))
        gap.text=str((18 if labels else 11)+(vertical[0]['lift_spatium'] if vertical else 0)+(2 if info else 0)+(4.5 if first_mark else 0))
    contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
    container=E.fromstring(contents['META-INF/container.xml'])
    for path in assets:sub(container.find('rootfiles'),'rootfile',**{'full-path':path})
    contents['META-INF/container.xml']=E.tostring(container,encoding='utf-8',xml_declaration=True);contents.update(assets)
    pre=out/'com_diagramas_preparado.mscz'
    with zipfile.ZipFile(pre,'w',zipfile.ZIP_DEFLATED) as z:
        for name,data in contents.items():z.writestr(name,data)
    final=out/(filename+'.mscz')
    assert out.resolve().is_relative_to(BATCH.resolve())
    (out/'reaberto.musicxml').unlink(missing_ok=True)
    # Um processo do MuseScore grava a partitura e renderiza os SVG de conferência; o arquivo
    # salvo é reaberto uma vez só, no fim, para as páginas PNG e o MusicXML de ida e volta.
    save_and_render(pre,final,out,out/'gravacao.log')
    # Primeiro os nomes dos instrumentos (só SVG), depois paginação e espaçamento uma vez.
    layout_with_instrument_labels(final,report,out)
    # Syllables checked on the rendered pages (score_layout.py); kept apart from repair_spacing.
    from score_layout import repair_lyric_collisions;repair_lyric_collisions(final,report,out)
    compact_instrument_label_layout(final,report,out)
    render_final_pages(final,out)
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
                    # Lyric lines written as text sit below the staff; the instrument names above it.
                    if element.tag=='direction' and element.get('placement')!='below':
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
    pages=sorted(out.glob('pagina-*.png'),key=page_order);assert pages
    # previa-geral.png vai na pasta de revisão entregue ao usuário; nada no app a lê. A config
    # pode desligá-la ('overview_preview': false); o padrão mantém a entrega atual.
    (out/'previa-geral.png').unlink(missing_ok=True)
    if report.pop('_overview_preview',True):
        progress('Preparando as miniaturas e a prévia da partitura…')
        with timed('preview'):
            columns=min(3,len(pages));rows=math.ceil(len(pages)/columns)
            canvas=Image.new('RGB',(columns*350,rows*525),'#dedede');draw=ImageDraw.Draw(canvas)
            for i,path in enumerate(pages):
                im=Image.open(path).convert('RGB');im.thumbnail((330,480));x=10+i%columns*350;y=28+i//columns*525;canvas.paste(im,(x,y));draw.text((x,y-20),str(i+1),fill='black')
            canvas.save(out/'previa-geral.png')
    report.update(final_path=str(final),images=images,diagram_assets=asset_manifest,pages=len(pages),native_reopened=True,melody_attacks_verified=len(allnotes),final_sha256=hashlib.sha256(final.read_bytes()).hexdigest())
    import score_quality
    report['quality']=score_quality.evaluate(report)
    return final

def prepare(config,parsed=None):
    """``parsed`` é o MIDI que o worker já leu e conferiu pelo hash; sem ele, o arquivo é lido aqui."""
    if parsed is None:reset_timings()
    title=default_score_title(config['filename'])
    score_metadata(config,title);diagram_inversion(config.get('diagram_inversion'))
    index=config['index'];stem=f'{index+1:03d} - '+re.sub(r'[<>:"/\\|?*]','',title)[:83].rstrip(' .')
    out=BATCH/f'{index+1:03d}';out.mkdir(exist_ok=True)
    if parsed is None:
        progress('Lendo as notas da pista selecionada…')
        with timed('midi_read'):parsed=parse_midi(config['source_path'])
    progress('Preparando a tonalidade e os acordes da partitura…')
    with timed('analysis'):harmony=analyze_harmony(parsed,config['melody_sources'])
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
        count_timing('conversions')
        with timed('musicxml'):xml,report=build_musicxml(parsed,active,copy.deepcopy(harmony),out,title)
        report['triage']=active
        report['_overview_preview']=active.get('overview_preview') is not False
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
    report['timings']=timings_snapshot()
    (out/'validacao.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
    preview=out/'previa-geral.png'
    return {'index':index,'filename':config['filename'],'status':'generated_pending_visual_review','path':str(final),'preview':str(preview) if preview.exists() else None,'pages':report['pages'],'measures':report['measures'],'notes':report['melody_attacks_verified'],'harmony_warnings':harmony.get('warnings',[])}

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
