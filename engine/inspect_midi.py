"""Read-only Standard MIDI File inventory. Python standard library only.

parse_midi(path) returns ordered MIDI events and paired notes with ticks.
Channel numbers are 1-based; programs are 0-based General MIDI numbers.
"""
from pathlib import Path
from collections import Counter, defaultdict, deque
import struct, hashlib, json, argparse

GM = '''Acoustic Grand Piano|Bright Acoustic Piano|Electric Grand Piano|Honky-tonk Piano|Electric Piano 1|Electric Piano 2|Harpsichord|Clavinet|Celesta|Glockenspiel|Music Box|Vibraphone|Marimba|Xylophone|Tubular Bells|Dulcimer|Drawbar Organ|Percussive Organ|Rock Organ|Church Organ|Reed Organ|Accordion|Harmonica|Tango Accordion|Acoustic Guitar nylon|Acoustic Guitar steel|Electric Guitar jazz|Electric Guitar clean|Electric Guitar muted|Overdriven Guitar|Distortion Guitar|Guitar harmonics|Acoustic Bass|Electric Bass finger|Electric Bass pick|Fretless Bass|Slap Bass 1|Slap Bass 2|Synth Bass 1|Synth Bass 2|Violin|Viola|Cello|Contrabass|Tremolo Strings|Pizzicato Strings|Orchestral Harp|Timpani|String Ensemble 1|String Ensemble 2|SynthStrings 1|SynthStrings 2|Choir Aahs|Voice Oohs|Synth Voice|Orchestra Hit|Trumpet|Trombone|Tuba|Muted Trumpet|French Horn|Brass Section|SynthBrass 1|SynthBrass 2|Soprano Sax|Alto Sax|Tenor Sax|Baritone Sax|Oboe|English Horn|Bassoon|Clarinet|Piccolo|Flute|Recorder|Pan Flute|Blown Bottle|Shakuhachi|Whistle|Ocarina|Lead 1 square|Lead 2 sawtooth|Lead 3 calliope|Lead 4 chiff|Lead 5 charang|Lead 6 voice|Lead 7 fifths|Lead 8 bass lead|Pad 1 new age|Pad 2 warm|Pad 3 polysynth|Pad 4 choir|Pad 5 bowed|Pad 6 metallic|Pad 7 halo|Pad 8 sweep|FX 1 rain|FX 2 soundtrack|FX 3 crystal|FX 4 atmosphere|FX 5 brightness|FX 6 goblins|FX 7 echoes|FX 8 sci-fi|Sitar|Banjo|Shamisen|Koto|Kalimba|Bag pipe|Fiddle|Shanai|Tinkle Bell|Agogo|Steel Drums|Woodblock|Taiko Drum|Melodic Tom|Synth Drum|Reverse Cymbal|Guitar Fret Noise|Breath Noise|Seashore|Bird Tweet|Telephone Ring|Helicopter|Applause|Gunshot'''.split('|')

def decode(data):
    try: return data.decode('utf-8').strip('\x00\r\n ')
    except UnicodeDecodeError: return data.decode('cp1252', errors='replace').strip('\x00\r\n ')

def vlq(data, pos):
    value = 0
    for _ in range(4):
        if pos >= len(data): raise ValueError('truncated VLQ')
        x = data[pos]; pos += 1; value = (value << 7) | (x & 127)
        if not x & 128: return value, pos
    raise ValueError('invalid VLQ longer than 4 bytes')

def parse_midi(path):
    blob = Path(path).read_bytes(); warnings=[]
    data = blob
    if data[:4] == b'RIFF' and data[8:12] == b'RMID':
        pos = 12
        while pos + 8 <= len(data):
            name = data[pos:pos+4]; size = struct.unpack_from('<I', data, pos+4)[0]
            if name == b'data': data=data[pos+8:pos+8+size]; break
            pos += 8+size+(size % 2)
    if data[:4] != b'MThd': raise ValueError('missing MThd header')
    size = struct.unpack_from('>I', data, 4)[0]
    if size < 6: raise ValueError('invalid header length')
    fmt, track_count, division = struct.unpack_from('>HHH', data, 8)
    if division & 0x8000: raise ValueError('SMPTE timing unsupported by this inventory parser')
    if division == 0: raise ValueError('zero PPQ')
    pos=8+size; events=[]; tracks=[]
    while pos+8 <= len(data):
        name=data[pos:pos+4]; size=struct.unpack_from('>I',data,pos+4)[0]; body=data[pos+8:pos+8+size]
        pos += 8+size
        if name != b'MTrk': warnings.append('Skipped non-MTrk chunk'); continue
        ti=len(tracks); p=0; tick=0; running=None; event_count=0; names=[]; texts=[]; lyrics=0; eot=False
        while p < len(body):
            delta,p=vlq(body,p); tick+=delta
            if p>=len(body): raise ValueError(f'track {ti}: missing status')
            status=body[p]
            if status < 128:
                if running is None: raise ValueError(f'track {ti}: running status missing')
                status=running
            else: p+=1
            event_count += 1
            if status == 0xff:
                if p>=len(body): raise ValueError('truncated meta event')
                typ=body[p]; p+=1; count,p=vlq(body,p); payload=body[p:p+count]; p+=count
                if len(payload)!=count: raise ValueError('truncated meta data')
                e={'tick':tick,'track':ti,'kind':'meta','type':typ,'data':payload,'order':event_count}
                events.append(e)
                if typ==3: names.append(decode(payload))
                elif typ in (1,2,4,6,7):
                    if len(texts)<8: texts.append({'type':typ,'text':decode(payload)[:120]})
                elif typ==5: lyrics+=1
                elif typ==47: eot=True
                continue
            if status in (0xf0,0xf7):
                count,p=vlq(body,p); p+=count
                if p>len(body): raise ValueError('truncated sysex')
                running=None
                continue
            if not 0x80<=status<=0xef: raise ValueError(f'unsupported MIDI status {status:#x}')
            running=status; kind=status>>4; ch=(status&15)+1
            count=1 if kind in (12,13) else 2
            args=body[p:p+count]; p+=count
            if len(args)!=count or any(x>=128 for x in args): raise ValueError(f'track {ti}, byte {p-count}, tick {tick}: invalid channel event data for status {status:#x}: {args.hex()}')
            events.append({'tick':tick,'track':ti,'kind':kind,'channel':ch,'a':args[0],'b':args[1] if count==2 else 0,'order':event_count})
        tracks.append({'index':ti,'names':names,'text_hints':texts,'lyric_events':lyrics,'event_count':event_count,'end_tick':tick,'has_end_of_track':eot})
    if len(tracks)!=track_count: warnings.append(f'Header declares {track_count} tracks, found {len(tracks)}')
    events.sort(key=lambda e:(e['tick'],e['track'],e['order']))
    programs=defaultdict(int); active=defaultdict(deque); notes=[]; stray=0; dangling=0
    for e in events:
        if e['kind']==12: programs[e['channel']]=e['a']
        elif e['kind']==9 and e['b']>0:
            active[(e['track'],e['channel'],e['a'])].append({'track':e['track'],'channel':e['channel'],'pitch':e['a'],'velocity':e['b'],'program':programs[e['channel']],'start':e['tick']})
        elif e['kind']==8 or (e['kind']==9 and e['b']==0):
            key=(e['track'],e['channel'],e['a'])
            if active[key]:
                n=active[key].popleft(); n['end']=e['tick']; n['duration']=n['end']-n['start']; notes.append(n)
            else: stray+=1
    last=max([t['end_tick'] for t in tracks] or [0])
    for queue in active.values():
        while queue:
            n=queue.popleft(); n['end']=last; n['duration']=last-n['start']; n['missing_note_off']=True; notes.append(n); dangling+=1
    if stray: warnings.append(f'{stray} note-offs without matching note-on')
    if dangling: warnings.append(f'{dangling} note-ons without matching note-off')
    return {'path':str(Path(path).resolve()),'size':len(blob),'sha256':hashlib.sha256(blob).hexdigest(),'format':fmt,'ppq':division,'tracks':tracks,'events':events,'notes':notes,'warnings':warnings,'end_tick':last}

def note_name(p): return ['C','C#','D','Eb','E','F','F#','G','Ab','A','Bb','B'][p%12]+str(p//12-1)

def group_summary(notes, ppq):
    events=[]
    for n in notes:
        if n['duration']>0: events.extend([(n['start'],1),(n['end'],-1)])
    events.sort(); count=peak=0
    for _,d in events: count+=d; peak=max(count,peak)
    pitches=[n['pitch'] for n in notes]
    starts=Counter(n['start'] for n in notes)
    # Offset from sixteenth-note grid, in quarter-note units.
    residuals=[abs((n['start']/(ppq/4))-round(n['start']/(ppq/4)))*(ppq/4) for n in notes]
    return {'notes':len(notes),'range':[min(pitches),max(pitches)] if notes else [],'range_names':[note_name(min(pitches)),note_name(max(pitches))] if notes else [],'peak_simultaneous':peak,'simultaneous_attack_fraction':round(sum(c for c in starts.values() if c>1)/len(notes),3) if notes else 0,'onset_sixteenth_grid_within_10ms_at_120_fraction':round(sum(r<=ppq*.02 for r in residuals)/len(notes),3) if notes else 0,'start_tick':min(n['start'] for n in notes) if notes else 0,'end_tick':max(n['end'] for n in notes) if notes else 0,'common_durations_ticks':Counter(n['duration'] for n in notes).most_common(6)}

def summarize(parsed):
    d={k:v for k,v in parsed.items() if k not in ('events','notes')}; ppq=d['ppq']; notes=parsed['notes']; ev=parsed['events']
    tempos=[]; meters=[]; keys=[]
    for e in ev:
        if e['kind']!='meta': continue
        raw=e['data']; typ=e['type']
        if typ==81 and len(raw)==3:
            us=int.from_bytes(raw,'big')
            if us: tempos.append({'tick':e['tick'],'quarter':round(e['tick']/ppq,4),'bpm':round(60000000/us,3),'microseconds':us})
        elif typ==88 and len(raw)>=2: meters.append({'tick':e['tick'],'quarter':round(e['tick']/ppq,4),'numerator':raw[0],'denominator':2**raw[1]})
        elif typ==89 and len(raw)>=2: keys.append({'tick':e['tick'],'sharps_flats':int.from_bytes(raw[:1],'big',signed=True),'minor':bool(raw[1])})
    d.update({'note_count':len(notes),'pitched_note_count':sum(n['channel']!=10 for n in notes),'percussion_note_count':sum(n['channel']==10 for n in notes),'tempo_changes':tempos,'time_signatures':meters,'key_signatures':keys})
    current=500000; previous=0; secs=0
    for tempo in tempos:
        secs+=(tempo['tick']-previous)/ppq*current/1e6; previous=tempo['tick']; current=tempo['microseconds']
    secs+=(d['end_tick']-previous)/ppq*current/1e6; d['duration_seconds']=round(secs,2); d['quarter_notes']=round(d['end_tick']/ppq,3)
    groups=defaultdict(list)
    for n in notes: groups[(n['track'],n['channel'],n['program'])].append(n)
    d['note_groups']=[dict(track=k[0],channel=k[1],program=k[2],instrument='Percussion' if k[1]==10 else GM[k[2]],**group_summary(ns,ppq)) for k,ns in sorted(groups.items())]
    return d

def inventory(source, output):
    files=sorted((p for p in Path(source).rglob('*') if p.is_file() and p.suffix.lower() in ('.mid','.midi','.kar')),key=lambda p:p.name.casefold())
    good=[]; failed=[]
    for path in files:
        try: good.append(summarize(parse_midi(path)))
        except Exception as exc: failed.append({'path':str(path),'error':str(exc)})
    hashes=defaultdict(list)
    for d in good: hashes[d['sha256']].append(d['path'])
    duplicates=[{'sha256':h,'files':paths} for h,paths in hashes.items() if len(paths)>1]
    result={'source':str(source),'total_files':len(files),'valid_files':len(good),'failed_files':failed,'duplicate_groups':duplicates,'files':good}
    Path(output).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'total':len(files),'valid':len(good),'failures':failed,'duplicate_groups':duplicates,'output':str(output)},ensure_ascii=False,indent=2))
    return result

if __name__=='__main__':
    ap=argparse.ArgumentParser(); ap.add_argument('source'); ap.add_argument('output'); args=ap.parse_args(); inventory(args.source,args.output)
