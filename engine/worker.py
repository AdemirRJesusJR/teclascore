"""JSON protocol for the C# desktop app. One isolated process per operation."""
from pathlib import Path
import json,sys,os,hashlib,shutil,traceback,copy,re,uuid
import ntpath,unicodedata

def score_output_stem(artist,title,fallback_path=''):
    """Windows-safe export name from displayed properties; leave the metadata unchanged."""
    name=title.strip() if title and title.strip() else ntpath.splitext(ntpath.basename(str(fallback_path)))[0]
    if not name.strip():name='Partitura'
    if artist and artist.strip():name=artist.strip()+' - '+name
    name=re.sub(r'\s+',' ',name)
    name=''.join('-' if unicodedata.category(c)=='Cc' or c in '<>:"/\\|?*' else c for c in name)
    name=re.sub(r'\s+',' ',name).strip(' .') or 'Partitura'
    name=name[:100].rstrip(' .')
    if re.match(r'^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)',name,re.I):name='Partitura - '+name
    return name

def score_output_path(directory,artist,title,fallback_path=''):
    stem=score_output_stem(artist,title,fallback_path)
    directory=Path(directory);dest=directory/(stem+'.mscz');counter=2
    while dest.exists() or (directory/(dest.stem+' - revisao')).exists():
        dest=directory/(stem+f' ({counter}).mscz');counter+=1
    return dest
def send(kind,**data):print(json.dumps(dict(kind=kind,**data),ensure_ascii=False),flush=True)

def manual_melody_config(selected):
    """Request order is musical priority; keep the first occurrence of each key."""
    if not isinstance(selected,list) or not selected:raise ValueError('Escolha ao menos uma pista para a melodia.')
    sources=[];seen=set()
    for item in selected:
        if not isinstance(item,dict) or any(type(item.get(key)) is not int for key in ('track','channel','program')):
            raise ValueError('Cada fonte de melodia deve informar pista, canal e instrumento.')
        if item['track']<0 or not 1<=item['channel']<=16 or not 0<=item['program']<=127:
            raise ValueError('Pista, canal ou instrumento de melodia inválido.')
        if item['channel']==10:raise ValueError('A pista de percussão não pode ser usada como melodia.')
        key=tuple(item[field] for field in ('track','channel','program'))
        if key in seen:continue
        seen.add(key);sources.append({field:item[field] for field in ('track','channel','program')})
    config={'index':0,'status':'ready','melody_sources':sources,'solo_sources':[],'reserve_high_melody':True}
    if len(sources)>1:config['merge_mode']='priority'
    return config

def queue_receipt_path(req,workspace):
    if 'queueReceipt' not in req:return None
    value=req['queueReceipt']
    if not isinstance(value,str) or not value.strip():raise ValueError('Caminho do recibo da fila inválido.')
    path=Path(value)
    if not path.is_absolute():raise ValueError('O recibo da fila precisa de um caminho absoluto dentro do trabalho.')
    workspace=Path(workspace).resolve();path=path.resolve()
    if not path.is_relative_to(workspace) or path==workspace or path.suffix.lower()!='.json':
        raise ValueError('O recibo da fila deve permanecer dentro da pasta de trabalho.')
    job=req.get('queueJobId');recipe=req.get('queueRecipeSha256')
    if not isinstance(job,str) or not job.strip() or len(job)>128 or not isinstance(recipe,str) or not re.fullmatch(r'[0-9a-fA-F]{64}',recipe):
        raise ValueError('O recibo precisa da identificação da tarefa e do hash da receita congelada.')
    if path.exists():
        previous=json.loads(path.read_text(encoding='utf-8-sig'))
        if not isinstance(previous,dict) or previous.get('schemaVersion')!=1 or previous.get('jobId')!=job or str(previous.get('recipeSha256','')).lower()!=recipe.lower():
            raise ValueError('Já existe outro documento no caminho reservado para o recibo da fila.')
    return path

def write_queue_receipt(req,workspace,result,source_sha256):
    """Publish a recoverable result only after every output has been hashed."""
    path=queue_receipt_path(req,workspace)
    if path is None:return
    score=Path(result['scorePath']).resolve();review=Path(result['reviewDirectory']).resolve()
    files=[score]+sorted((file.resolve() for file in review.rglob('*') if file.is_file()),key=str)
    artifacts=[]
    for file in files:
        with file.open('rb') as stream:
            digest=hashlib.file_digest(stream,'sha256').hexdigest()
        artifacts.append({'path':str(file),'sha256':digest,
                          'kind':'score' if file==score else 'preview' if file.suffix.lower()=='.png' else 'review'})
    document={'schemaVersion':1,'jobId':req['queueJobId'],'recipeSha256':req['queueRecipeSha256'],
              'sourceSha256':source_sha256,'result':result,'artifacts':artifacts}
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name('.'+path.name+'.'+uuid.uuid4().hex+'.tmp')
    try:
        with temporary.open('x',encoding='utf8',newline='\n') as stream:
            json.dump(document,stream,ensure_ascii=False,indent=2)
            stream.flush();os.fsync(stream.fileno())
        os.replace(temporary,path)
    finally:
        if temporary.exists():temporary.unlink()
def report_error(error,request_path=None):
    """Keep technical failure details in the job, not in the library status."""
    details=''.join(traceback.format_exception(type(error),error,error.__traceback__))
    diagnostic_path=None
    if request_path:
        try:
            request_path=Path(request_path).resolve()
            diagnostic=request_path.with_name(request_path.stem+'-error-'+uuid.uuid4().hex+'.json')
            document={'requestFile':request_path.name,'exceptionType':type(error).__name__,
                'message':str(error),'traceback':details,'kind':getattr(error,'kind',None),
                'reviewPoints':getattr(error,'review_points',[]),
                'layoutHistory':getattr(error,'layout_history',[])}
            with diagnostic.open('x',encoding='utf8') as stream:
                json.dump(document,stream,ensure_ascii=False,indent=2)
            diagnostic_path=str(diagnostic)
        except (OSError,TypeError,ValueError) as diagnostic_error:
            print('Não foi possível salvar o diagnóstico: '+str(diagnostic_error),file=sys.stderr)
    message=str(error)
    if getattr(error,'kind',None)=='diagram_layout' or message.startswith('Sobreposição residual após ajuste de diagrama:'):
        message='Não foi possível ajustar o espaço entre alguns diagramas de acordes.'
        if diagnostic_path:message+=' Os detalhes foram salvos no diagnóstico da conversão.'
    send('error',message=message,reviewPoints=getattr(error,'review_points',[]),diagnosticPath=diagnostic_path)
    print(details,file=sys.stderr,end='')

def main():
    req=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    send('progress',message='Lendo o mapa de compassos…' if req['mode']=='measureMap' else 'Preparando a análise musical…' if req['mode']=='analyze' else 'Preparando a geração da partitura…')
    workspace=Path(req['workspace']).resolve();workspace.mkdir(parents=True,exist_ok=True)
    if req['mode']=='generate':queue_receipt_path(req,workspace)
    os.environ['PARTITURAS_JOB']=str(workspace/'work')
    import batch_score as engine
    from inspect_midi import parse_midi
    from batch_harmony import analyze_harmony,CHORD_INTERVALS,chord_label
    source=Path(req['inputMidi']).resolve()
    if not source.is_file():raise ValueError('O MIDI original não foi encontrado.')
    mode=req['mode']
    from melody_sections import measure_map,normalize_sections,all_sources
    if mode=='measureMap':
        parsed=parse_midi(source)
        send('result',data=measure_map(parsed,engine))
        return
    if mode=='analyze':
        send('progress',message='Lendo pistas e notas do MIDI…')
        parsed=parse_midi(source)
        expected=req.get('expectedSourceSha256')
        if expected is not None and (not isinstance(expected,str) or expected.lower()!=parsed['sha256'].lower()):
            raise ValueError('O MIDI mudou desde a importação. Reimporte a versão atual antes de aplicar pistas por trecho.')
        profiles=json.loads((Path(__file__).parent/'profiles.json').read_text(encoding='utf8'))
        profile=profiles.get(parsed['sha256']);reviewed=bool(req.get('useReviewedProfile') and profile and profile['config']['status']=='ready')
        if reviewed:
            config=copy.deepcopy(profile['config']);override=profile['harmony']
        else:
            config=manual_melody_config(req.get('melodySources',[]))
            sections=normalize_sections(req.get('melodySections'),parsed,engine)
            if sections:config['melody_sections']=sections
            override=None
        config.update(filename=source.name,source_path=str(source))
        send('progress',message='Analisando acordes e tonalidade…')
        harmony=analyze_harmony(parsed,all_sources(config),context_advice=not reviewed)
        if override:
            if override.get('deliverable') is False:raise ValueError(override.get('reason','Perfil ainda não aprovado.'))
            for field in ('key','key_changes'):
                if override.get(field):harmony[field]=copy.deepcopy(override[field])
            if override.get('events'):harmony['events']=copy.deepcopy(override['events'])
            else:
                for change in override.get('event_overrides',[]):
                    found=[c for c in harmony['events'] if c['tick']==change['tick']]
                    if change.get('add') and not found:
                        event={'tick':change['tick'],'root':change['root'],'quality':change['quality'],'confidence':.85 if change.get('context_inferred') else 1.,'evidence':{'method':'reviewed_profile','reason':change.get('reason','')}}
                        harmony['events'].append(event);found=[event]
                    for event in found:
                        if change.get('omit'):harmony['events'].remove(event);continue
                        for field in ('root','quality','root_spelling'): 
                            if field in change:event[field]=change[field]
                        if 'new_tick' in change:event['tick']=change['new_tick']
                        event['pitch_classes']=sorted((event['root']+x)%12 for x in CHORD_INTERVALS[event['quality']])
                        event['manual_review']=change.get('reason','')
            harmony['manual_review']=override;harmony['review_required']=False
        collapsed=[]
        for c in sorted(harmony['events'],key=lambda c:c['tick']):
            if collapsed and (c['root'],c['quality'])==(collapsed[-1]['root'],collapsed[-1]['quality']):continue
            collapsed.append(c)
        harmony['events']=collapsed
        send('progress',message='Conferindo a melodia, o ritmo e os trechos que precisam de revisão…')
        points,rhythm_ready,rhythm_message,melody_report=engine.review_points(parsed,config,harmony,include_melody_report=True)
        document={'source':str(source),'sha256':parsed['sha256'],'config':config,'harmony':harmony,'reviewedProfile':reviewed,'ppq':parsed['ppq'],'reviewPoints':points,'rhythmReady':rhythm_ready,'rhythmMessage':rhythm_message}
        if 'melody_merge' in melody_report:document['melodyMerge']=melody_report['melody_merge']
        analysis=workspace/'analysis.json'
        fifths=harmony.get('key',{}).get('fifths',0)
        chords=[]
        for c in collapsed:
            active=harmony.get('key',{'fifths':0,'minor':False})
            for change in harmony.get('key_changes',[]):
                if change['tick']<=c['tick']:active=change
            step,alter=engine.spellings(active['fifths'],active.get('minor',False))[c['root']]
            name=step+('#'*alter if alter>0 else 'b'*(-alter))
            chord={'quarter':c['tick']/parsed['ppq'],'root':c.get('root_spelling') or name,'quality':c['quality'],'confidence':round(c.get('confidence',0),2)}
            if not reviewed:
                evidence=c.get('evidence',{})
                chord['recommendation']=evidence.get('recommendation','')
                chord['alternatives']=', '.join(chord_label(option['root'],option['quality'],active['fifths'])
                    for option in evidence.get('alternatives',[]))
            chords.append(chord)
        document.update(chords=chords,keyFifths=fifths,keyMinor=harmony.get('key',{}).get('minor',False))
        analysis.write_text(json.dumps(document,ensure_ascii=False,indent=2),encoding='utf8')
        send('progress',message='Análise concluída. Preparando as cifras e os pontos de revisão…')
        response={'analysisPath':str(analysis),'reviewedProfile':reviewed,'keyFifths':fifths,'keyMinor':harmony.get('key',{}).get('minor',False),'chords':chords,'warnings':[] if reviewed else harmony.get('warnings',[]),'reason':config.get('reason',''),'registerAdaptation':config.get('register_adaptation',''),'reviewPoints':points,'rhythmReady':rhythm_ready,'rhythmMessage':rhythm_message}
        if 'melodyMerge' in document:response['melodyMerge']=document['melodyMerge']
        send('result',data=response)
        return
    if mode!='generate':raise ValueError('Operação desconhecida.')
    send('progress',message='Conferindo o MIDI e as opções escolhidas para a partitura…')
    data=json.loads(Path(req['analysisPath']).read_text(encoding='utf8'))
    if hashlib.sha256(source.read_bytes()).hexdigest()!=data['sha256']:raise ValueError('O MIDI mudou após a análise. Analise novamente.')
    if str(source)!=data['source']:raise ValueError('A análise pertence a outro MIDI.')
    config=copy.deepcopy(data['config']);harmony=data['harmony'];edited=req.get('chords')
    for field in ('scoreTitle','scoreSubtitle','scoreArtist','scoreComposer'):
        if field in req:config[field]=req[field]
    config['diagram_inversion']=engine.diagram_inversion(req.get('diagramInversion'))
    config['transpose_semitones']=engine.transpose_semitones(req.get('transposeSemitones'))
    config['fold_repeats']=bool(req.get('foldRepeats'))
    if isinstance(req.get('scoreStyle'),dict):config['score_style']={'music_font':str(req['scoreStyle'].get('musicFont','')),'size':str(req['scoreStyle'].get('size',''))}
    metadata=engine.score_metadata(config,engine.default_score_title(config['filename']))
    if edited is not None:
        if not edited:raise ValueError('A partitura precisa de pelo menos uma cifra.')
        new=[];naturals={'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}
        import math
        for c in edited:
            root=c['root'].strip().replace('♯','#').replace('♭','b')
            if not re.fullmatch('[A-G](#{1,2}|b{1,2})?',root):raise ValueError('Raiz de acorde inválida: '+root)
            quality=c.get('quality','').strip()
            if quality not in CHORD_INTERVALS:raise ValueError('Tipo de acorde não suportado: '+quality)
            q=float(c['quarter'])
            if not math.isfinite(q) or q<0:raise ValueError('A posição do acorde deve ser um tempo positivo.')
            pc=(naturals[root[0]]+root.count('#')-root.count('b'))%12;tick=round(q*data['ppq'])
            old=next((x for x in harmony['events'] if x['tick']==tick),{})
            changed=(old.get('root'),old.get('quality'))!=(pc,quality)
            event=dict(old,tick=tick,root=pc,quality=quality,root_spelling=root,confidence=old.get('confidence',0) if not changed else 0)
            selected_inversion=c.get('diagramInversion')
            event['diagram_inversion']=None if selected_inversion is None else engine.diagram_inversion(selected_inversion)
            event['pitch_classes']=sorted((pc+x)%12 for x in CHORD_INTERVALS[quality])
            if changed:event.update(user_edited=True,evidence={'method':'user_edit_in_desktop'})
            new.append(event)
        if len({c['tick'] for c in new})!=len(new):raise ValueError('Há dois acordes na mesma posição. Mantenha uma cifra por posição.')
        harmony['events']=sorted(new,key=lambda c:c['tick'])
    fifths=int(req.get('keyFifths',harmony.get('key',{}).get('fifths',0)))
    if not -7<=fifths<=7:raise ValueError('Armadura precisa ficar entre 7 bemóis e 7 sustenidos.')
    old_key=harmony.get('key',{})
    minor=bool(req.get('keyMinor',old_key.get('minor',False)))
    if (fifths,minor)!=(old_key.get('fifths',0),old_key.get('minor',False)):
        harmony['key']=dict(old_key,fifths=fifths,minor=minor)
        for change in harmony.get('key_changes',[]):
            if change['tick']==0:change.update(fifths=fifths,minor=minor)
    exe=Path(req['musescorePath'])
    if not exe.is_file():raise ValueError('Localize o executável do MuseScore nas configurações.')
    engine.EXE=str(exe)
    engine.analyze_harmony=lambda parsed,sources:copy.deepcopy(harmony)
    inversion_label=('posição fundamental','1ª inversão','2ª inversão')[config['diagram_inversion']]
    if any(engine.chord_diagram_inversion(chord,config['diagram_inversion'])!=config['diagram_inversion'] for chord in harmony['events']):
        inversion_label='posições personalizadas'
    send('progress',message=f'Preparando melodia, cifras e diagramas em {inversion_label}…')
    result=engine.prepare(config)
    send('progress',message='Salvando a partitura e as páginas na pasta de saída…')
    final=Path(result['path']);targetdir=Path(req['outputDirectory']).resolve();targetdir.mkdir(parents=True,exist_ok=True)
    dest=score_output_path(targetdir,metadata['artist'],metadata['title'],source)
    # Exclusive creation prevents overwriting even when another app creates the same name.
    report_dir=targetdir/(dest.stem+' - revisao');report_dir.mkdir(exist_ok=False)
    with dest.open('xb') as out:out.write(final.read_bytes())
    for pattern in ('pagina-*.png','previa-geral.png','validacao.json','harmonia_fonte.json','reaberto.musicxml'):
        for p in final.parent.glob(pattern):shutil.copy2(p,report_dir/p.name)
    first=sorted(report_dir.glob('pagina-*.png'),key=lambda p:int(p.stem.rsplit('-',1)[-1]))[0]
    report=json.loads((report_dir/'validacao.json').read_text(encoding='utf8'))
    notice='Partitura gerada e reaberta no MuseScore. Confira a prévia antes de considerar a revisão musical concluída.'
    collisions=report['melody_analysis']['quantized_attack_collisions']
    if collisions and not data['reviewedProfile']:notice+=f' {collisions} ataques próximos foram agrupados; revise os ornamentos.'
    if report.get('staff_per_source'):notice+=f" As trocas de instrumento ficaram próximas demais para uma pauta; cada instrumento ganhou sua pauta ({', '.join(report['staff_per_source']['names'])})."
    response={'scorePath':str(dest),'previewPath':str(first),'reviewDirectory':str(report_dir),'pages':result['pages'],'reviewedProfile':data['reviewedProfile'],'message':notice,'transposeSemitones':config['transpose_semitones']}
    if 'melody_merge' in report['melody_analysis']:response['melodyMerge']=report['melody_analysis']['melody_merge']
    write_queue_receipt(req,workspace,response,data['sha256'])
    send('result',data=response)
if __name__=='__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    try:main()
    except Exception as e:
        report_error(e,sys.argv[1] if len(sys.argv)>1 else None);sys.exit(1)
