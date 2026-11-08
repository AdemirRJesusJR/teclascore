"""Instrument-name captions: measure them in the rendered pages, stack them in lanes, and accept a
layout only after native re-rendering. Moved from batch_score.py; engine helpers are reached through
``core`` so tests and callers that replace batch_score functions keep working."""
from pathlib import Path
import xml.etree.ElementTree as E
import json, os, shutil, subprocess, zipfile
import batch_score as core


def instrument_label_geometry(path):
    """Measure complete rendered text runs, including italic glyph overhangs."""
    with core.timed('svg_analysis'):return _instrument_label_geometry(path)

def _instrument_label_geometry(path):
    root=E.parse(path).getroot();records=core.svg_geometry._records(root)
    rows=core.svg_geometry._staff_rows(records);texts=[];obstacles=[]
    for record in records:
        element=record['element'];kind=element.get('class','')
        if record['tag']=='image':
            x,y,width,height=(float(element.get(key,'0')) for key in ('x','y','width','height'))
            box=core.svg_geometry._bbox(core.svg_geometry._xy(record['matrix'],point)
                for point in ((x,y),(x+width,y),(x,y+height),(x+width,y+height)))
            kind=kind or 'Image'
        elif record['tag']=='path' and kind:
            box=core.svg_geometry._bbox(core.svg_geometry._xy(record['matrix'],point)
                for point in core.svg_geometry._path_points(element.get('d','')))
        else:continue
        if box is None:continue
        item={'bbox':box,'kind':kind,'row':core.svg_geometry._row_for(box,rows)}
        (texts if kind=='StaffText' else obstacles).append(item)
    # Native MuseScore flattens SVG keyboards into Image paths. Reserve their
    # complete rectangles, including blank gaps between keys and note markers.
    obstacles.extend({'bbox':diagram['bbox'],'kind':'Image','row':diagram['row']}
                     for diagram in core.analyze_svg(path)['diagrams'])
    texts.sort(key=lambda text:(text['row'],text['bbox'][0]))
    overlaps=[];clipped=[]
    for index,text in enumerate(texts):
        box=text['bbox'];row=rows[text['row']] if rows else None
        if row is None:
            clipped.append({'index':index,'reason':'no_staff'});continue
        spatium=(row['bbox'][3]-row['bbox'][1])/4
        for other_index,other in enumerate(texts[index+1:],index+1):
            if core.svg_geometry._intersection(box,other['bbox']):
                overlaps.append({'index':index,'other_label':other_index,'kind':'StaffText'})
        for other in obstacles:
            if core.svg_geometry._intersection(box,other['bbox']):
                overlaps.append({'index':index,'kind':other['kind']})
        # Keep complete names inside the printed staff margins, not just the page.
        left=min(r['bbox'][0] for r in rows);right=row['bbox'][2]
        shift=max(0,left-box[0])+min(0,right-box[2])
        if abs(shift)>.5:
            # Keep the caption close to its attack: at most half its width and
            # eight staff spaces. A large misplaced name requires review.
            if abs(shift)<=min((box[2]-box[0])/2,8*spatium):
                clipped.append({'index':index,'shift_spatium':shift/spatium})
            else:clipped.append({'index':index,'reason':'correction_too_large'})
        view=[float(value) for value in root.get('viewBox','0 0 9921.26 14031.5').split()]
        if box[1]<view[1] or box[3]>view[1]+view[3]:clipped.append({'index':index,'reason':'page_height'})
    return {'path':str(path),'labels':texts,'overlaps':overlaps,'clipped':clipped,'rows':rows}


def _instrument_label_lanes(pages):
    """Allocate complete text runs to separate rows, retaining each attack's x anchor.

    Only the established, unstacked render is used for pairing chronological
    labels. A native rerender is required before the result can be accepted.
    """
    positions={};offset=0;maximum=0
    for page in pages:
        rows=page.get('rows',[])
        if not rows or any('bbox' not in text for text in page['labels']):return None
        shifts={clip['index']:clip['shift_spatium'] for clip in page['clipped'] if 'shift_spatium' in clip}
        if any('reason' in clip for clip in page['clipped']):return None
        for row in rows:
            scale=(row['bbox'][3]-row['bbox'][1])/4
            if scale<=0:return None
            texts=[(index,text) for index,text in enumerate(page['labels']) if text['row']==row['row']]
            pitch=max([2.4]+[(text['bbox'][3]-text['bbox'][1])/scale+.8 for _,text in texts])
            lanes=[]
            for index,text in texts:
                box=text['bbox'];dx=shifts.get(index,0);left=box[0]+dx*scale;right=box[2]+dx*scale
                lane=next((i for i,intervals in enumerate(lanes)
                           if all(right+.8*scale<=start or left>=end+.8*scale for start,end in intervals)),len(lanes))
                if lane==len(lanes):lanes.append([])
                if lane>=8:return None
                lanes[lane].append((left,right))
                positions[offset+index]={'x':dx,'y':-lane*pitch,'lane':lane}
                maximum=max(maximum,lane*pitch)
        offset+=len(page['labels'])
    return positions,maximum

def _label_pages(out):
    return [core.instrument_label_geometry(path) for path in sorted(out.glob('qa-*.svg'),key=core.page_order)]

def _label_problems(pages,labels):
    if sum(len(page['labels']) for page in pages)!=len(labels):
        raise core.NeedsReview('Não foi possível conferir todos os nomes dos instrumentos na prévia.',kind='instrument_label_layout')
    return sum(len(page['overlaps'])+len(page['clipped']) for page in pages)

def _record_label_pass(pages,report,out,history,problems):
    history.append({'pass':len(history),'overlaps':sum(len(page['overlaps']) for page in pages),
                    'margin_overflows':sum(len(page['clipped']) for page in pages)})
    report['instrument_label_layout_qa']={'passed':not problems,'passes':history,'pages':len(pages)}
    (out/'instrumentos_verificados.json').write_text(json.dumps(pages,ensure_ascii=False),encoding='utf8')

def _label_failure(pages,labels,report):
    offset=0;affected=set()
    for page in pages:
        for problem in page['overlaps']+page['clipped']:
            affected.add(offset+problem['index'])
            if 'other_label' in problem:affected.add(offset+problem['other_label'])
        offset+=len(page['labels'])
    quarters=[labels[index]['original_quarter'] for index in sorted(affected)]
    message='As trocas de instrumento estão muito próximas para exibir todos os nomes sem sobreposição. Ajuste os trechos das pistas ou o espaçamento da partitura.'
    error=core.NeedsReview(message,quarters,kind='instrument_label_layout')
    error.review_points=[point for quarter in quarters if (point:=core.locate_review_point(
        quarter,round(report['origin_removed_quarters']*core.DIV),report['bars'],error.kind,message,True)) is not None]
    return error

def _apply_label_lanes(final,report,out,planned):
    """Empilha os nomes em faixas e reserva o espaço; um job do MuseScore grava e renderiza os SVG."""
    with zipfile.ZipFile(final) as archive:contents={name:archive.read(name) for name in archive.namelist()}
    name=next(name for name in contents if name.endswith('.mscx'));root=E.fromstring(contents[name])
    shifts,extra=planned
    for index,text in enumerate(root.findall('Score/Staff/Measure/voice/StaffText')):
        if index not in shifts:continue
        off=text.find('offset')
        if off is None:off=E.SubElement(text,'offset',x='0',y='0')
        off.set('x',str(float(off.get('x','0'))+shifts[index]['x']))
        off.set('y',str(float(off.get('y','0'))+shifts[index]['y']))
    # Tempo text occupies the row immediately above the original names.
    # Lift it with the new stack; its musical time/value is unchanged.
    for tempo in root.findall('Score/Staff/Measure/voice/Tempo'):
        off=tempo.find('offset')
        if off is None:off=E.SubElement(tempo,'offset',x='0',y='0')
        off.set('y',str(float(off.get('y','0'))-extra-3))
    for frame in root.findall('Score/Staff/VBox'):
        gap=frame.find('bottomGap')
        if gap is None:gap=E.SubElement(frame,'bottomGap')
        gap.text=str(float(gap.text or '0')+extra+3)
    # MuseScore 4 keeps style in a separate archive member; an inline Style
    # alone is silently discarded when that member is present.
    style_name=next((name for name in contents if name.endswith('score_style.mss')),None)
    style_root=E.fromstring(contents[style_name]) if style_name else root
    style=style_root.find('Style') if style_name else root.find('Score/Style')
    if style is None:style=E.SubElement(style_root if style_name else root.find('Score'),'Style')
    # Text offsets do not reserve skyline space with autoplace disabled.
    # Allow the complete label stack plus the previous system's stems/notes.
    for tag,minimum in (('minSystemDistance',28+extra),('maxSystemDistance',36+extra),
                        ('minSystemSpread',28+extra),('maxSystemSpread',36+extra),('staffUpperBorder',18+extra)):
        element=style.find(tag)
        if element is None:element=E.SubElement(style,tag)
        element.text=str(max(float(element.text or '0'),minimum))
    if style_name:contents[style_name]=E.tostring(style_root,encoding='utf-8',xml_declaration=True)
    # Fixed page breaks would compress the enlarged systems back into their
    # old pages. Keep all line boundaries and let the native renderer paginate.
    released=0
    for element in root.findall('.//LayoutBreak/subtype'):
        if element.text=='page':element.text='line';released+=1
    report['instrument_label_lanes']={'count':1+max((value['lane'] for value in shifts.values()),default=0),
                                     'extra_spatium':extra,'anchors_preserved':True,'page_breaks_released':released}
    contents[name]=E.tostring(root,encoding='utf-8',xml_declaration=True)
    prepared=out/'instrumentos_preparados.mscz'
    with zipfile.ZipFile(prepared,'w',zipfile.ZIP_DEFLATED) as archive:
        for name,data in contents.items():archive.writestr(name,data)
    core.save_and_render(prepared,final,out,out/'ajuste_instrumentos.log')

def verify_instrument_label_layout(final,report,out,history=None,first_attempt=0):
    """Reserve lanes and inter-system clearance before rejecting dense captions.

    Roda depois da paginação e do espaçamento. ``first_attempt=1`` indica que as faixas já
    foram aplicadas (layout_with_instrument_labels): qualquer problema restante é definitivo."""
    labels=report.get('instrument_labels',[])
    if not labels:return
    history=[] if history is None else history
    for attempt in range(first_attempt,2):
        pages=_label_pages(out)
        problems=_label_problems(pages,labels)
        _record_label_pass(pages,report,out,history,problems)
        if not problems:return
        planned=core._instrument_label_lanes(pages) if not attempt else None
        if planned is None:raise _label_failure(pages,labels,report)
        _apply_label_lanes(final,report,out,planned)
        # A new native save may alter pagination and line wrapping; recertify both.
        core.repair_orphan_pages(final,report,out)
        core.repair_spacing(final,report,out)

def layout_with_instrument_labels(final,report,out):
    """Ajustes do layout renderizado numa ordem só: nomes dos instrumentos, paginação, espaçamento.

    Os nomes são medidos logo após o primeiro render (só SVG). Se já cabem, nada muda e a
    ordem antiga fica idêntica: paginação, espaçamento e a conferência dos nomes. Se colidem,
    as faixas são planejadas já aqui: paginação e espaçamento rodam uma vez (não antes e de
    novo depois das faixas) e um layout sem solução falha antes de qualquer passe de
    espaçamento, para prepare() passar mais cedo a uma pauta por instrumento."""
    labels=report.get('instrument_labels',[])
    history=[];first_attempt=0
    if labels:
        pages=_label_pages(out)
        if _label_problems(pages,labels):
            _record_label_pass(pages,report,out,history,True)
            planned=core._instrument_label_lanes(pages)
            if planned is None:raise _label_failure(pages,labels,report)
            _apply_label_lanes(final,report,out,planned)
            pages=_label_pages(out)
            problems=_label_problems(pages,labels)
            _record_label_pass(pages,report,out,history,problems)
            if problems:raise _label_failure(pages,labels,report)
            first_attempt=1
    core.repair_orphan_pages(final,report,out)
    core.repair_spacing(final,report,out)
    core.verify_instrument_label_layout(final,report,out,history,first_attempt)


def compact_instrument_label_layout(final,report,out):
    """Try local vertical allowances only after obtaining a validated fallback.

    The original score and previews remain untouched until the full native
    candidate is smaller and passes content, labels, diagrams and musical-ink QA.
    Worker publication happens later, after the ordinary MusicXML round trip.
    """
    if not report.get('instrument_label_lanes'):return
    before_pages=len(list(out.glob('qa-*.svg')))
    if before_pages<2:return
    result={'pages_before':before_pages,'pages_after':before_pages,'applied':False,
            'method':'local_measure_clearance','note_and_text_sizes_preserved':True}
    report['compact_layout']=result
    candidate_dir=out/'compactacao';candidate_dir.mkdir(exist_ok=True)
    publishing=False
    try:
        with zipfile.ZipFile(final) as archive:before={name:archive.read(name) for name in archive.namelist()}
        prepared=core.compact_score_layout.prepare(before)
        if prepared is None:result['reason']='unsupported_native_layout';return
        contents,spacers=prepared
        input_path=candidate_dir/'preparado.mscz';candidate=candidate_dir/'compacta.mscz'
        with zipfile.ZipFile(input_path,'w',zipfile.ZIP_DEFLATED) as archive:
            for name,data in contents.items():archive.writestr(name,data)
        core.progress('Otimizando o espaço entre as pautas, mantendo o tamanho das notas…')
        # Um job grava a candidata e renderiza os SVG e o MusicXML; as páginas PNG da partitura
        # publicada vêm depois, quando render_final_pages reabre o arquivo salvo.
        core.save_and_render(input_path,candidate,candidate_dir,candidate_dir/'ajuste_compactacao.log',
                             [candidate_dir/'reaberto.musicxml'])
        paths=sorted(candidate_dir.glob('qa-*.svg'),key=core.page_order)
        result['candidate_pages']=len(paths)
        if not paths or len(paths)>=before_pages:result['reason']='no_page_reduction';return
        with zipfile.ZipFile(candidate) as archive:after={name:archive.read(name) for name in archive.namelist()}
        if not core.compact_score_layout.content_preserved(before,after):result['reason']='content_or_typography_changed';return
        reopened=E.parse(candidate_dir/'reaberto.musicxml');parts=reopened.findall('part')
        if (reopened.getroot().tag!='score-partwise' or not parts
            or any(len(part.findall('measure'))!=report.get('measures') for part in parts)):
            result['reason']='incomplete_native_reopen';return
        labels=[core.instrument_label_geometry(path) for path in paths]
        diagrams=[core.analyze_svg(path) for path in paths]
        with core.timed('svg_analysis'):ink=[core.compact_score_layout.page_quality(path) for path in paths]
        checks={'content_preserved':True,
                'label_count':sum(len(page['labels']) for page in labels),
                'diagram_count':sum(len(page['diagrams']) for page in diagrams),
                'label_problems':sum(len(page['overlaps'])+len(page['clipped']) for page in labels),
                'diagram_problems':sum(len(page['overlaps'])+len(page['clipped'])+len(page['warnings']) for page in diagrams),
                'ink_problems':sum(len(page['overlaps'])+len(page['clipped']) for page in ink)}
        result['checks']=checks
        (candidate_dir/'geometria_completa.json').write_text(json.dumps(ink,ensure_ascii=False),encoding='utf8')
        if (checks['label_count']!=len(report['instrument_labels']) or checks['diagram_count']!=report['chord_symbols']
            or checks['label_problems'] or checks['diagram_problems'] or checks['ink_problems']
            or not all(page['passed'] for page in ink)):
            result['reason']='candidate_geometry_not_safe';return
        # Stage every replacement before changing the validated set. A disk
        # error restores the previous set; no mixed page count reaches output.
        replacements={final:candidate.read_bytes()}
        replacements.update({out/path.name:path.read_bytes() for path in candidate_dir.glob('qa-*.svg')})
        for pages,key in ((labels,'path'),(diagrams,'svg_path'),(ink,'path')):
            for page in pages:page[key]=str(out/Path(page[key]).name)
        replacements[out/'instrumentos_verificados.json']=json.dumps(labels,ensure_ascii=False).encode('utf8')
        replacements[out/'geometria_verificada.json']=json.dumps(diagrams,ensure_ascii=False).encode('utf8')
        replacements[out/'compactacao_verificada.json']=json.dumps(ink,ensure_ascii=False).encode('utf8')
        affected=set(replacements)
        for pattern in ('pagina-*.png','qa-*.svg'):affected.update(out.glob(pattern))
        staged=candidate_dir/'publicacao';staged.mkdir(exist_ok=True)
        backup_dir=candidate_dir/'reserva';backup_dir.mkdir(exist_ok=True)
        backup={path:(backup_dir/path.name if path.exists() else None) for path in affected}
        for path,data in replacements.items():(staged/path.name).write_bytes(data)
        for path,original in backup.items():
            if original is not None:original.write_bytes(path.read_bytes())
        publishing=True
        try:
            for path in replacements:os.replace(staged/path.name,path)
            for path in affected-set(replacements):path.unlink()
        except OSError:
            for path,data in backup.items():
                if data is None:path.unlink(missing_ok=True)
                else:os.replace(data,path)
            publishing=False
            raise
        publishing=False
        result.update(applied=True,pages_after=len(paths),reason='validated_smaller_layout',local_spacers=len(spacers))
        report['instrument_label_layout_qa'].update(pages=len(paths),compact_layout_verified=True)
        report['geometric_layout_qa'].update(pages=len(paths),compact_layout_verified=True)
    except (OSError,ValueError,E.ParseError,zipfile.BadZipFile,core.NeedsReview,subprocess.TimeoutExpired) as error:
        if publishing:raise  # An un-restorable set must never reach worker publication.
        # Compactness is optional. A failed candidate must not turn the already
        # verified spacious score into a failed conversion.
        result.update(reason='candidate_failed',error_type=type(error).__name__)
