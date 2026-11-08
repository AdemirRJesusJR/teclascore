"""Rendered-layout repairs: page flow, margins and diagram spacing, each checked against MuseScore's own
rendering before it is kept. Moved from batch_score.py; engine helpers are reached through ``core`` so tests
and callers that replace batch_score functions keep working."""
from pathlib import Path
from collections import defaultdict
import xml.etree.ElementTree as E
import json, shutil, tempfile, zipfile
import batch_score as core


def repair_orphan_pages(final,report,out):
    """Release planned page breaks when wider music creates an orphan interior page."""
    core.progress('Conferindo a distribuição da música entre as páginas…')
    pages=[core.analyze_svg(p) for p in sorted(out.glob('qa-*.svg'),key=core.page_order)]
    if not any(len(page['rows'])<=1 for page in pages[1:-1]):return
    with zipfile.ZipFile(final) as archive:contents={name:archive.read(name) for name in archive.namelist()}
    name=next(name for name in contents if name.endswith('.mscx'));root=E.fromstring(contents[name])
    breaks=[element for element in root.findall('.//LayoutBreak/subtype') if element.text=='page']
    if not breaks:return
    # Keep every requested system boundary; MuseScore can then paginate by its
    # realized staff height and any extra systems required by dense measures.
    for element in breaks:element.text='line'
    contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
    pre=out/'paginacao_preparada.mscz'
    with zipfile.ZipFile(pre,'w',zipfile.ZIP_DEFLATED) as archive:
        for name,data in contents.items():archive.writestr(name,data)
    # Um único job do MuseScore grava a partitura e renderiza só os SVG de conferência (PNG no fim).
    core.save_and_render(pre,final,out,out/'ajuste_paginacao.log')
    report['page_flow_adjustment']={'pages_before':len(pages),'page_breaks_released':len(breaks)}

def _compact_diagram_offsets(page,offset,compacted):
    """Reclaim surplus label padding once in crowded rows, keeping full-size keys."""
    affected=set()
    for overlap in page['overlaps']:
        indices=(overlap['diagram'],) if overlap['type']=='harmony-image' else (overlap['a'],overlap['b'])
        affected.update(page['diagrams'][i]['row'] for i in indices)
    affected.update(page['diagrams'][clip['index']]['row'] for clip in page['clipped'] if clip['type']=='image')
    moves={}
    for row in page['rows']:
        indices=row['diagrams'];key=tuple(offset+i for i in indices)
        if row['row'] not in affected or len(indices)<4 or key in compacted:continue
        if not row['pairing_by_order_safe']:continue
        compacted.add(key)
        for i in indices:
            diagram=page['diagrams'][i];harmony=page['harmonies'][diagram['harmony_index']]
            scale=(diagram['bbox'][2]-diagram['bbox'][0])/14
            delta=(harmony['bbox'][2]-diagram['bbox'][0])/scale+.8
            if abs(delta)>.05:moves[offset+i]=delta
    return moves

def repair_diagram_lanes(final,report,out,pages,history):
    """Try a separate candidate; publish it only after native geometric validation."""
    with zipfile.ZipFile(final) as archive:contents={name:archive.read(name) for name in archive.namelist()}
    prepared=core.diagram_lanes.prepare(contents,pages)
    if prepared is None:return False
    native,details=prepared
    core.progress('Distribuindo os diagramas mais próximos em alturas alternadas…')
    with tempfile.TemporaryDirectory(prefix='diagram-lanes-',dir=out) as temporary:
        folder=Path(temporary);candidate=folder/'score.mscz'
        with zipfile.ZipFile(folder/'prepared.mscz','w',zipfile.ZIP_DEFLATED) as archive:
            for name,data in native.items():archive.writestr(name,data)
        core.save_and_render(folder/'prepared.mscz',candidate,folder,folder/'save.log')
        paths=sorted(folder.glob('qa-*.svg'),key=core.page_order)
        verified=[core.analyze_svg(path) for path in paths]
        if not verified or sum(len(p['diagrams']) for p in verified)!=report['chord_symbols']:return False
        if any(p['warnings'] or p['overlaps'] or p['clipped'] for p in verified):return False
        # Include notes, tempo, labels and adjacent staves in the new stack's clearance.
        for path in paths:
            with core.timed('svg_analysis'):ink=core.compact_score_layout.page_quality(path)
            if any('Image' in problem['kinds'] or problem['cross_system'] for problem in ink['overlaps']):return False
            if any(item['kind']=='Image' for item in ink['clipped']):return False
        with zipfile.ZipFile(candidate) as archive:
            after={name:archive.read(name) for name in archive.namelist()}
        if not core.diagram_lanes.music_preserved(contents,after):return False
        shutil.copyfile(candidate,final)
        # As páginas PNG e o MusicXML finais saem da reabertura da partitura publicada.
        for pattern in ('pagina-*.png','qa-*.svg'):
            for old in out.glob(pattern):old.unlink()
        for page in folder.glob('qa-*.svg'):shutil.copyfile(page,out/page.name)
        for page in verified:page['svg_path']=str((out/Path(page['svg_path']).name).resolve())
        report['diagram_lane_layout']=dict(details,validated=True)
        report['geometric_layout_qa']={'passed':True,'passes':history,'pages':len(verified),'fallback':'full_size_lanes'}
        (out/'geometria_verificada.json').write_text(json.dumps(verified,ensure_ascii=False),encoding='utf8')
        return True


def _layout_excess(pages):
    """Largura das sobreposições mais estouros de margem, em espaços de pauta (diagramas têm 14)."""
    total=0.
    for page in pages:
        widths=sorted(d['bbox'][2]-d['bbox'][0] for d in page['diagrams'])
        scale=widths[len(widths)//2]/14 if widths and widths[len(widths)//2]>0 else 1
        total+=sum(item.get('width',0) for item in page['overlaps'])/scale
        total+=sum(item.get('overflow',0) for item in page['clipped'])/scale
    return total

# Passes seguidos sem progresso antes de desistir do espaçamento incremental. Um passe progride
# quando o número de problemas ou o tamanho somado deles (em espaços de pauta) cai abaixo do
# melhor já visto: partituras reais convergem depois de um platô na contagem (11, 2, 2, 2, 0)
# enquanto as sobreposições restantes encolhem; um layout que oscila (137 … 78, 90, 78, 84, 84)
# vai antes para as faixas alternadas de diagramas ou para o mesmo erro diagram_layout.
SPACING_STALL_LIMIT=2

def repair_spacing(final,report,out):
    """Use the rendered geometry to reserve space, preserving musical content."""
    history=[];compacted=set();best=None;stalled=0
    for attempt in range(10):
        core.progress(f'Conferindo margens e diagramas — verificação {attempt+1} de até 10…')
        core.count_timing('spacing_passes')
        pages=[core.analyze_svg(p) for p in sorted(out.glob('qa-*.svg'),key=core.page_order)]
        offset=0;image_add=defaultdict(float);end_add=defaultdict(float);move_image=defaultdict(float);problems=0
        for page in pages:
            if page['warnings']:raise core.NeedsReview('Geometria de cifras não pôde ser pareada: '+str(page['warnings']),kind='diagram_layout')
            diagrams=page['diagrams'];harmonies=page['harmonies']
            compact_moves=core._compact_diagram_offsets(page,offset,compacted)
            move_image.update(compact_moves)
            compacting_rows={diagrams[i-offset]['row'] for i in compact_moves}
            problems+=len(page['overlaps'])+len(page['clipped'])
            for overlap in page['overlaps']:
                index=overlap['diagram'] if overlap['type']=='harmony-image' else overlap['a']
                if diagrams[index]['row'] in compacting_rows:continue
                if overlap['type']=='harmony-image':
                    h=harmonies[overlap['harmony']];d=diagrams[overlap['diagram']];scale=(d['bbox'][2]-d['bbox'][0])/14
                    if overlap.get('own_pair'):
                        move_image[offset+overlap['diagram']]=max(move_image[offset+overlap['diagram']],(h['bbox'][2]-d['bbox'][0])/scale+1)
                    else:
                        target=h.get('diagram_index')
                        if target is None:raise core.NeedsReview('Cifra sem diagrama correspondente na verificação geométrica.',kind='diagram_layout')
                        image_add[offset+target]=max(image_add[offset+target],(d['bbox'][2]-h['bbox'][0])/scale+1.2)
                else:
                    first,second=sorted((overlap['a'],overlap['b']));d=diagrams[first];scale=(d['bbox'][2]-d['bbox'][0])/14
                    image_add[offset+second]=max(image_add[offset+second],overlap['width']/scale+1.2)
            for clip in page['clipped']:
                if clip['type']!='image' or clip['edge']!='staff_right':continue
                idx=offset+clip['index'];d=diagrams[clip['index']];scale=(d['bbox'][2]-d['bbox'][0])/14
                if d['row'] in compacting_rows:continue
                tick=report['chords'][idx]['tick'];measure_index=next(i for i,b in enumerate(report['bars']) if b['start']<=tick<b['end'])
                end_add[measure_index]=max(end_add[measure_index],clip['overflow']/scale+1.2)
            offset+=len(diagrams)
        assert offset==report['chord_symbols'],('Rendered diagram count changed',offset,report['chord_symbols'])
        history.append({'pass':attempt,'overlaps':sum(len(p['overlaps']) for p in pages),'margin_overflows':sum(len(p['clipped']) for p in pages)})
        if not problems:
            report['geometric_layout_qa']={'passed':True,'passes':history,'pages':len(pages)}
            (out/'geometria_verificada.json').write_text(json.dumps(pages,ensure_ascii=False),encoding='utf8')
            return
        excess=_layout_excess(pages)
        if best is None or problems<best[0] or excess<best[1]-.05:stalled=0
        else:stalled+=1
        best=(problems,excess) if best is None else (min(best[0],problems),min(best[1],excess))
        if attempt==9 or stalled>=SPACING_STALL_LIMIT:
            if core.repair_diagram_lanes(final,report,out,pages,history):return
            report['geometric_layout_qa']={'passed':False,'passes':history,'pages':len(pages)}
            (out/'geometria_verificada.json').write_text(json.dumps(pages,ensure_ascii=False),encoding='utf8')
            error=core.NeedsReview('Não foi possível acomodar todos os diagramas dentro das margens da partitura.',kind='diagram_layout')
            error.layout_history=history
            raise error
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
                        seg=E.Element('Segment');core.sub(seg,'leadingSpace',report['image_spacing'][count]);voice.insert(list(voice).index(im)+1,seg)
                    count+=1
            if mi in report['end_spacing']:
                voice=m.find('voice')
                for bl in list(voice.findall('BarLine')):voice.remove(bl)
                div=int(root.findtext('Score/Division','480'));core.sub(voice,'tick',round(report['bars'][mi]['end']/core.DIV*div))
                bl=core.sub(voice,'BarLine');core.sub(bl,'subtype','end' if mi+1==len(report['bars']) else 'normal')
                seg=core.sub(voice,'Segment');core.sub(seg,'leadingSpace',report['end_spacing'][mi])
        contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
        pre=out/'espacamento_preparado.mscz'
        with zipfile.ZipFile(pre,'w',zipfile.ZIP_DEFLATED) as z:
            for name,data in contents.items():z.writestr(name,data)
        core.save_and_render(pre,final,out,out/'ajuste_espacamento.log')


def _rendered_systems(musicxml):
    """Measure indices of each system as MuseScore laid them out (its MusicXML export marks every new system)."""
    root=E.parse(musicxml).getroot();part=root.find('part');systems=[]
    for index,measure in enumerate(part.findall('measure') if part is not None else []):
        mark=measure.find('print')
        if not systems or (mark is not None and (mark.get('new-system')=='yes' or mark.get('new-page')=='yes')):systems.append([])
        systems[-1].append(index)
    return systems


def _lyric_pages(folder):
    return [core.svg_geometry.analyze_lyrics(path) for path in sorted(folder.glob('qa-*.svg'),key=core.page_order)]


def _diagram_problems(folder):
    pages=[core.analyze_svg(path) for path in sorted(folder.glob('qa-*.svg'),key=core.page_order)]
    return sum(len(page['overlaps'])+len(page['clipped']) for page in pages)


def repair_lyric_collisions(final,report,out,passes=2):
    """Checks the rendered syllables for overlaps or too little room between neighbours and repairs them in a separate
    candidate: a crowded measure is widened inside its system (measure stretch); on the second pass a system that is
    still crowded is split in two. A candidate is kept only when it has fewer collisions and no new diagram problem.
    The result goes to report['lyric_collisions']."""
    lyrics=report.get('melody_analysis',{}).get('lyrics')
    if not isinstance(lyrics,dict) or not (lyrics.get('attached') or lyrics.get('text_lines') or lyrics.get('spoken')):return
    pages=_lyric_pages(out);before=sum(len(page['collisions']) for page in pages)
    summary={'before':before,'count':before,'passes':[]}
    report['lyric_collisions']=summary
    if not before:return
    core.progress('Conferindo o espaço entre as sílabas da letra…')
    diagram_problems=_diagram_problems(out);stretched={}
    if not (out/'reaberto.musicxml').exists():
        # The final pages are rendered only at the end; the system layout is read from a MusicXML export of the saved score.
        job=out/'letra-sistemas.json'
        job.write_text(json.dumps([{'in':str(final),'out':[str(out/'reaberto.musicxml')]}]),encoding='utf8')
        core.call_musescore(['-j',str(job)],out/'letra-sistemas.log',150)
    for attempt in range(passes):
        systems=_rendered_systems(out/'reaberto.musicxml')
        rows=[(page,row) for page in pages for row in range(page['rows'])]
        if len(rows)!=len(systems):
            summary['passes'].append({'pass':attempt,'skipped':'staves_per_system'});break
        stretch=defaultdict(float);split=set()
        for number,(page,row) in enumerate(rows):
            found=[c for c in page['collisions'] if c['row']==row]
            if not found:continue
            measures=systems[number];bars=page['barlines'].get(row,[])
            for collision in found:
                if len(bars)!=len(measures):split.add(number);continue
                index=next((k for k,x in enumerate(bars) if collision['x']<=x),len(bars)-1)
                stretch[measures[index]]+=max(.1,LYRIC_GAP-collision['gap'])
                if attempt:split.add(number)
        with zipfile.ZipFile(final) as archive:contents={name:archive.read(name) for name in archive.namelist()}
        name=next(n for n in contents if n.endswith('.mscx'));root=E.fromstring(contents[name])
        measures=root.find('Score/Staff').findall('Measure')
        trial=dict(stretched)
        for index,amount in stretch.items():
            trial[index]=round(min(2.,stretched.get(index,1.)*(1.15+min(.6,amount/6))),3)
            measure=measures[index];element=measure.find('stretch')
            if element is None:
                element=E.Element('stretch');eid=measure.find('eid')
                measure.insert(list(measure).index(eid)+1 if eid is not None else 0,element)
            element.text=str(trial[index])
        breaks=0
        for number in split:
            group=systems[number]
            if len(group)<2:continue
            measure=measures[group[len(group)//2-1]]
            if measure.find('LayoutBreak') is not None:continue
            layout=E.Element('LayoutBreak');core.sub(layout,'subtype','line');eid=measure.find('eid')
            measure.insert(list(measure).index(eid)+1 if eid is not None else 0,layout);breaks+=1
        if not stretch and not breaks:break
        contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
        with tempfile.TemporaryDirectory(prefix='letra-',dir=out) as temporary:
            folder=Path(temporary);candidate=folder/'score.mscz'
            with zipfile.ZipFile(folder/'prepared.mscz','w',zipfile.ZIP_DEFLATED) as archive:
                for item,data in contents.items():archive.writestr(item,data)
            # One MuseScore job saves the candidate, its check SVG and the MusicXML with its systems.
            core.save_and_render(folder/'prepared.mscz',candidate,folder,folder/'ajuste_letra.log',extra=[folder/'reaberto.musicxml'])
            verified=_lyric_pages(folder);after=sum(len(page['collisions']) for page in verified)
            accepted=after<summary['count'] and _diagram_problems(folder)<=diagram_problems
            summary['passes'].append({'pass':attempt,'stretched_measures':len(stretch),'system_breaks':breaks,'collisions':after,'accepted':accepted})
            if not accepted:break
            stretched=trial;summary['count']=after
            shutil.copyfile(candidate,final)
            for pattern in ('pagina-*.png','qa-*.svg'):
                for old in out.glob(pattern):old.unlink()
                for page in folder.glob(pattern):shutil.copyfile(page,out/page.name)
            shutil.copyfile(folder/'reaberto.musicxml',out/'reaberto.musicxml')
        pages=_lyric_pages(out)
        if not after:break
    summary['items']=[dict(page=number+1,row=c['row'],type=c['type'],gap=c['gap']) for number,page in enumerate(pages) for c in page['collisions']][:30]
    if stretched:summary['measure_stretch']={str(index+1):value for index,value in sorted(stretched.items())}


LYRIC_GAP=.6  # staff spaces aimed at between neighbouring syllables when a measure is widened
