"""Explicit, exclusive melody sources tied to the generator's stable measure map."""
from copy import deepcopy

_FIELDS=('track','channel','program')

def source_key(source):
    return tuple(source[field] for field in _FIELDS)

def measure_map(parsed,engine):
    _,meters,_=engine.meta(parsed)
    origin,end,bars=engine.make_bars(parsed,meters)
    return dict(sourceSha256=parsed['sha256'],originQuarter=origin/engine.DIV,
                endQuarter=end/engine.DIV,ppq=parsed['ppq'],
                measures=[dict(number=index+1,startQuarter=(origin+bar['start'])/engine.DIV,
                               endQuarter=(origin+bar['end'])/engine.DIV,numerator=bar['meter'][0],
                               denominator=bar['meter'][1],partial=bar['partial'])
                          for index,bar in enumerate(bars)])

def normalize_sections(value,parsed,engine):
    if value is None:return []
    if not isinstance(value,list) or len(value)>700:
        raise ValueError('Informe uma lista de até 700 trechos de melodia.')
    if not value:return []
    mapping=measure_map(parsed,engine);available={source_key(note) for note in parsed['notes'] if note['channel']!=10}
    sections=[]
    for item in value:
        if not isinstance(item,dict) or type(item.get('firstMeasure')) is not int or type(item.get('lastMeasure')) is not int:
            raise ValueError('Cada trecho precisa informar os compassos inicial e final inteiros.')
        first,last=item['firstMeasure'],item['lastMeasure'];source=item.get('source')
        if not 1<=first<=last<=len(mapping['measures']):
            raise ValueError(f'Trecho {first}–{last} fora dos compassos disponíveis (1–{len(mapping["measures"])}).')
        if (not isinstance(source,dict) or any(type(source.get(field)) is not int for field in _FIELDS)
            or source['track']<0 or not 1<=source['channel']<=16 or not 0<=source['program']<=127):
            raise ValueError('Escolha uma fonte MIDI válida para cada trecho.')
        if source['channel']==10 or source_key(source) not in available:
            raise ValueError('A fonte do trecho não está disponível neste MIDI ou é uma pista de percussão.')
        sections.append(dict(firstMeasure=first,lastMeasure=last,source={field:source[field] for field in _FIELDS}))
    sections.sort(key=lambda section:(section['firstMeasure'],section['lastMeasure']))
    if any(current['firstMeasure']<=previous['lastMeasure'] for previous,current in zip(sections,sections[1:])):
        raise ValueError('Os trechos de melodia não podem se sobrepor.')
    return sections

def all_sources(config):
    if not config.get('melody_sections'):return config['melody_sources']
    result=[];seen=set()
    for source in list(config['melody_sources'])+[section['source'] for section in config.get('melody_sections',[])]:
        key=source_key(source)
        if key not in seen:result.append(deepcopy(source));seen.add(key)
    return result

def selected_notes(parsed,config,engine,origin,end):
    """Keep evidenced attacks only. A selected silent source never borrows a fallback."""
    sections=normalize_sections(config.get('melody_sections'),parsed,engine)
    mapping=measure_map(parsed,engine)
    if abs(mapping['originQuarter']-origin/engine.DIV)>1e-8 or abs(mapping['endQuarter']-end/engine.DIV)>1e-8:
        raise ValueError('O mapa de compassos mudou. Reabra a seleção por trechos e analise novamente.')
    base=list(config['melody_sources']);sources=all_sources(config);ppq=parsed['ppq']
    windows=[];cursor=mapping['originQuarter']
    for section in sections:
        begin=mapping['measures'][section['firstMeasure']-1]['startQuarter']
        stop=mapping['measures'][section['lastMeasure']-1]['endQuarter']
        if cursor<begin:windows.append((cursor,begin,base))
        windows.append((begin,stop,[section['source']]));cursor=stop
    if cursor<mapping['endQuarter']:windows.append((cursor,mapping['endQuarter'],base))
    selected=[]
    for note in parsed['notes']:
        if config.get('velocity_filter') and note['velocity'] not in config['velocity_filter']:continue
        quarter=note['start']/ppq;key=source_key(note)
        index=next((i for i,(start,stop,choices) in enumerate(windows) if start<=quarter<stop),None)
        if index is None:continue
        begin,stop,choices=windows[index]
        if not any(engine._source_accepts(note,choice,ppq) for choice in choices):continue
        # Continue a sustained selected note only while that same source is allowed.
        # Never synthesize a new attack when an earlier masked note enters a new region.
        for next_start,next_stop,next_choices in windows[index+1:]:
            if not any(source_key(choice)==key for choice in next_choices):break
            stop=next_stop
        clipped=min(note['end'],round(stop*ppq))
        if clipped<=note['start']:continue
        selected.append(dict(note,end=clipped,duration=clipped-note['start'],
                             _section_start=round(begin*engine.DIV),_section_end=round(stop*engine.DIV)))
    selected.sort(key=lambda note:(note['start'],note['pitch']))
    return selected,sources,sections
