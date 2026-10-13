"""JSON protocol for the C# desktop app. One isolated process per operation."""
from pathlib import Path
import json,sys,os,hashlib,shutil,traceback,copy,re
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
def send(kind,**data):print(json.dumps(dict(kind=kind,**data),ensure_ascii=False),flush=True)
def main():
    req=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    workspace=Path(req['workspace']).resolve();workspace.mkdir(parents=True,exist_ok=True)
    os.environ['PARTITURAS_JOB']=str(workspace/'work')
    import batch_score as engine
    from inspect_midi import parse_midi
    from batch_harmony import analyze_harmony,CHORD_INTERVALS
    source=Path(req['inputMidi']).resolve()
    if not source.is_file():raise ValueError('O MIDI original não foi encontrado.')
    mode=req['mode']
    if mode=='analyze':
        send('progress',message='Lendo pistas e notas do MIDI…')
        parsed=parse_midi(source)
        profiles=json.loads((Path(__file__).parent/'profiles.json').read_text(encoding='utf8'))
        profile=profiles.get(parsed['sha256']);reviewed=bool(req.get('useReviewedProfile') and profile and profile['config']['status']=='ready')
        if reviewed:
            config=copy.deepcopy(profile['config']);override=profile['harmony']
        else:
            selected=req.get('melodySources',[])
            if not selected:raise ValueError('Escolha ao menos uma pista para a melodia.')
            if any(s['channel']==10 for s in selected):raise ValueError('A pista de percussão não pode ser usada como melodia.')
            config={'index':0,'status':'ready','melody_sources':selected,'solo_sources':[],'reserve_high_melody':True}
            override=None
        config.update(filename=source.name,source_path=str(source))
        send('progress',message='Analisando acordes e tonalidade…')
        harmony=analyze_harmony(parsed,config['melody_sources'])
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
        points,rhythm_ready,rhythm_message=engine.review_points(parsed,config,harmony)
        document={'source':str(source),'sha256':parsed['sha256'],'config':config,'harmony':harmony,'reviewedProfile':reviewed,'ppq':parsed['ppq'],'reviewPoints':points,'rhythmReady':rhythm_ready,'rhythmMessage':rhythm_message}
        analysis=workspace/'analysis.json';analysis.write_text(json.dumps(document,ensure_ascii=False,indent=2),encoding='utf8')
        fifths=harmony.get('key',{}).get('fifths',0)
        chords=[]
        for c in collapsed:
            active=harmony.get('key',{'fifths':0,'minor':False})
            for change in harmony.get('key_changes',[]):
                if change['tick']<=c['tick']:active=change
            step,alter=engine.spellings(active['fifths'],active.get('minor',False))[c['root']]
            name=step+('#'*alter if alter>0 else 'b'*(-alter))
            chords.append({'quarter':c['tick']/parsed['ppq'],'root':c.get('root_spelling') or name,'quality':c['quality'],'confidence':round(c.get('confidence',0),2)})
        send('result',data={'analysisPath':str(analysis),'reviewedProfile':reviewed,'keyFifths':fifths,'keyMinor':harmony.get('key',{}).get('minor',False),'chords':chords,'warnings':[] if reviewed else harmony.get('warnings',[]),'reason':config.get('reason',''),'registerAdaptation':config.get('register_adaptation',''),'reviewPoints':points,'rhythmReady':rhythm_ready,'rhythmMessage':rhythm_message})
        return
    if mode!='generate':raise ValueError('Operação desconhecida.')
    data=json.loads(Path(req['analysisPath']).read_text(encoding='utf8'))
    if hashlib.sha256(source.read_bytes()).hexdigest()!=data['sha256']:raise ValueError('O MIDI mudou após a análise. Analise novamente.')
    if str(source)!=data['source']:raise ValueError('A análise pertence a outro MIDI.')
    config=copy.deepcopy(data['config']);harmony=data['harmony'];edited=req.get('chords')
    for field in ('scoreTitle','scoreSubtitle','scoreComposer'):
        if field in req:config[field]=req[field]
    config['diagram_inversion']=engine.diagram_inversion(req.get('diagramInversion'))
    config['transpose_semitones']=engine.transpose_semitones(req.get('transposeSemitones'))
    engine.score_metadata(config,engine.default_score_title(config['filename']))
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
            event['pitch_classes']=sorted((pc+x)%12 for x in CHORD_INTERVALS[quality])
            if changed:event.update(user_edited=True,evidence={'method':'user_edit_in_desktop'})
            new.append(event)
        if len({c['tick'] for c in new})!=len(new):raise ValueError('Há dois acordes na mesma posição. Mantenha uma cifra por posição.')
        harmony['events']=sorted(new,key=lambda c:c['tick'])
    fifths=int(req.get('keyFifths',harmony.get('key',{}).get('fifths',0)))
    if not -7<=fifths<=7:raise ValueError('Armadura precisa ficar entre 7 bemóis e 7 sustenidos.')
    minor=bool(req.get('keyMinor',False))
    old_key=harmony.get('key',{})
    if (fifths,minor)!=(old_key.get('fifths',0),old_key.get('minor',False)):
        harmony['key']=dict(old_key,fifths=fifths,minor=minor)
        for change in harmony.get('key_changes',[]):
            if change['tick']==0:change.update(fifths=fifths,minor=minor)
    exe=Path(req['musescorePath'])
    if not exe.is_file():raise ValueError('Localize o executável do MuseScore nas configurações.')
    engine.EXE=str(exe)
    engine.analyze_harmony=lambda parsed,sources:copy.deepcopy(harmony)
    inversion_label=('posição fundamental','1ª inversão','2ª inversão')[config['diagram_inversion']]
    send('progress',message=f'Preparando melodia, cifras e diagramas em {inversion_label}…')
    result=engine.prepare(config)
    final=Path(result['path']);targetdir=Path(req['outputDirectory']).resolve();targetdir.mkdir(parents=True,exist_ok=True)
    clean=re.sub(r'[<>:"/\\|?*]','',source.stem).strip(' .')[:110] or 'Partitura'
    dest=targetdir/(clean+' - teclado.mscz');counter=2
    while dest.exists() or (targetdir/(dest.stem+' - revisao')).exists():
        dest=targetdir/(clean+f' - teclado ({counter}).mscz');counter+=1
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
    send('result',data={'scorePath':str(dest),'previewPath':str(first),'reviewDirectory':str(report_dir),'pages':result['pages'],'reviewedProfile':data['reviewedProfile'],'message':notice,'transposeSemitones':config['transpose_semitones']})
if __name__=='__main__':
    try:main()
    except Exception as e:
        send('error',message=str(e),reviewPoints=getattr(e,'review_points',[]));traceback.print_exc(file=sys.stderr);sys.exit(1)
