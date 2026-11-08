"""Karaoke lyrics become MusicXML lyrics on the melody attacks."""
from pathlib import Path
import io, os, struct, tempfile, unittest, zipfile
from contextlib import redirect_stdout
from unittest.mock import patch
from test_priority_melody import A, PPQ, engine, worker


def karaoke_midi(path, words, lyric_type=1, header=True):
    """Melody on track 1 (one quarter note per syllable) and text events on track 2."""
    def vlq(value):
        data=[value&127];value>>=7
        while value:data.insert(0,(value&127)|128);value>>=7
        return bytes(data)
    def track(events):
        body=b'';previous=0
        for tick,message in sorted(events,key=lambda e:e[0]):body+=vlq(tick-previous)+message;previous=tick
        body+=b'\x00\xff\x2f\x00';return b'MTrk'+struct.pack('>I',len(body))+body
    melody=[];text=[]
    if header:text.append((0,b'\xff\x01'+vlq(8)+b'@TTitulo'))
    for index in range(max(len(words),24)):
        tick=index*PPQ
        melody+=[(tick,bytes([0x90,60+index%5,90])),(tick+PPQ-1,bytes([0x80,60+index%5,0]))]
        if index<len(words):
            data=words[index].encode('cp1252');text.append((tick,bytes([0xff,lyric_type])+vlq(len(data))+data))
    conductor=b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd'+struct.pack('>IHHH',6,1,3,PPQ)+b'MTrk'+struct.pack('>I',len(conductor))+conductor+track(melody)+track(text))


KARAOKE=[chr(92)+'Ca','sa','ri','nho',' da',' ro','ça',' ','/Sol',' de',' ve','rão',' lu','ar',' do',' ser','tão',' a','mor']


class LyricsTests(unittest.TestCase):
    def events(self,words,lyric_type=1):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'letra.kar';karaoke_midi(path,words,lyric_type);parsed=engine.parse_midi(path)
        notes,_,_=engine.melody_events(parsed,worker.manual_melody_config([dict(A,track=1)]),0,max(len(words),24)*PPQ)
        return parsed,notes

    def test_karaoke_words_and_syllables(self):
        parsed,notes=self.events(KARAOKE)
        report=engine.attach_lyrics(parsed,notes,0)
        lyrics=[(n['lyric']['text'],n['lyric']['syllabic']) for n in notes if 'lyric' in n]
        self.assertEqual(lyrics[:7],[('Ca','begin'),('sa','middle'),('ri','middle'),('nho','end'),('da','single'),('ro','begin'),('ça','end')])
        self.assertEqual(lyrics[7],('Sol','single'))
        self.assertEqual(len(report.pop('sung_ticks')),18)
        self.assertEqual((report['syllables'],report['attached'],report['share'],report['mode']),(18,18,1.0,'notes'))
        self.assertEqual(report['dropped'],[])
        self.assertNotIn('@TTitulo',[text for text,_ in lyrics])

    def test_split_digraphs_move_to_the_next_syllable(self):
        syllables=[{'text':t,'word_start':w} for t,w in (('no',True),('vin',False),('ha',False),('fil',True),('ho',False),('bic',True),('ho',False),('com',True),('ha',True))]
        engine.fix_split_digraphs(syllables)
        self.assertEqual([s['text'] for s in syllables],['no','vi','nha','fi','lho','bi','cho','com','ha'])

    def test_lyric_events_use_hyphens(self):
        words=['Ma-','ri-','a ','vem ','cá ']*4
        parsed,notes=self.events(words,lyric_type=5)
        engine.attach_lyrics(parsed,notes,0)
        self.assertEqual([(n['lyric']['text'],n['lyric']['syllabic']) for n in notes[:5]],[('Ma','begin'),('ri','middle'),('a','end'),('vem','single'),('cá','single')])

    def test_few_texts_are_credits_not_lyrics(self):
        parsed,notes=self.events(['Feito por','alguém','2020'])
        self.assertEqual(engine.attach_lyrics(parsed,notes,0)['attached'],0)
        self.assertFalse(any('lyric' in n for n in notes))

    def test_musicxml_carries_the_lyrics(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'letra.kar';karaoke_midi(path,KARAOKE);parsed=engine.parse_midi(path)
            harmony=dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])
            xml,report=engine.build_musicxml(parsed,worker.manual_melody_config([dict(A,track=1)]),harmony,Path(folder),'Letra')
            root=engine.E.parse(xml).getroot()
            texts=[e.text for e in root.iter('text') if e.text]
            self.assertEqual(texts[:4],['Ca','sa','ri','nho'])
            lyrics=report['melody_analysis']['lyrics']
            self.assertEqual((lyrics['syllables'],lyrics['attached'],lyrics['mode']),(18,18,'notes'))
            self.assertNotIn('sung_ticks',lyrics)
            self.assertEqual([line['text'] for line in lyrics['lines']],['Casarinho da roça','Sol de verão luar do sertão amor'])

    def test_lyrics_can_be_left_out(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'letra.kar';karaoke_midi(path,KARAOKE);parsed=engine.parse_midi(path)
            harmony=dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])
            config=worker.manual_melody_config([dict(A,track=1)]);config['include_lyrics']=False
            xml,report=engine.build_musicxml(parsed,config,harmony,Path(folder),'Sem letra')
            self.assertEqual(list(engine.E.parse(xml).getroot().iter('lyric')),[])
            self.assertEqual(report['melody_analysis']['lyrics'],0)


def midi_file(path, notes, texts, lyric_type=5):
    """notes: (quarter, pitch, length in quarters) on track 1; texts: (quarter, bytes) as meta events on track 2."""
    def vlq(value):
        data=[value&127];value>>=7
        while value:data.insert(0,(value&127)|128);value>>=7
        return bytes(data)
    def track(events):
        body=b'';previous=0
        for tick,message in sorted(events,key=lambda e:e[0]):body+=vlq(tick-previous)+message;previous=tick
        body+=b'\x00\xff\x2f\x00';return b'MTrk'+struct.pack('>I',len(body))+body
    melody=[];text=[]
    for quarter,pitch,length in notes:
        tick=round(quarter*PPQ);melody+=[(tick,bytes([0x90,pitch,90])),(tick+round(length*PPQ)-1,bytes([0x80,pitch,0]))]
    for quarter,data in texts:text.append((round(quarter*PPQ),bytes([0xff,lyric_type])+vlq(len(data))+data))
    conductor=b'\x00\xff\x58\x04\x04\x02\x18\x08\x00\xff\x2f\x00'
    path.write_bytes(b'MThd'+struct.pack('>IHHH',6,1,3,PPQ)+b'MTrk'+struct.pack('>I',len(conductor))+conductor+track(melody)+track(text))


def song(notes, texts, config=None, lyric_type=5):
    """Parsed MIDI, melody notes, bars and the lyric report."""
    with tempfile.TemporaryDirectory() as folder:
        path=Path(folder)/'letra.mid';midi_file(path,notes,texts,lyric_type);parsed=engine.parse_midi(path)
    end=max(q+l for q,_,l in notes)*PPQ
    melody=worker.manual_melody_config([dict(A,track=1)])
    melody_notes,_,_=engine.melody_events(parsed,melody,0,end)
    _,meters,_=engine.meta(parsed);origin,_,bars=engine.make_bars(parsed,meters)
    report=engine.attach_lyrics(parsed,melody_notes,origin,bars,dict(melody,**(config or {})))
    return parsed,melody_notes,bars,report


def sung(notes):
    return [n['lyric']['text'] for n in sorted(notes,key=lambda n:n['start']) if 'lyric' in n]


class SyllableTests(unittest.TestCase):
    def test_portuguese_syllables(self):
        from lyrics_syllables import syllabify
        cases={'coração':'co-ra-ção','carro':'car-ro','filho':'fi-lho','guerra':'guer-ra','quando':'quan-do',
               'palavra':'pa-la-vra','abraço':'a-bra-ço','paixão':'pai-xão','saída':'sa-í-da','rainha':'ra-i-nha',
               'juiz':'ju-iz','poeta':'po-e-ta','glória':'gló-ria','água':'á-gua','nascer':'nas-cer','muito':'mui-to',
               'bairro':'bair-ro','pássaro':'pás-sa-ro','saudade':'sau-da-de','mãe':'mãe','amor,':'a-mor,',
               'beija-flor':'bei-ja-flor','baby':'ba-by','caiu':'ca-iu','praia':'prai-a','luar':'lu-ar'}
        self.assertEqual({word:'-'.join(syllabify(word)) for word in cases},cases)

    def test_accents_are_restored_with_care(self):
        import lyrics_accents
        self.assertEqual(lyrics_accents.restore_title('Coracao esta em pedacos'),'Coração está em pedaços')
        self.assertEqual(lyrics_accents.restore_title('Mamae que e mamae'),'Mamãe que é mamãe')
        self.assertEqual(engine.default_score_title('Coracao_esta_em_pedacos.mid'),'Coração está em pedaços')
        self.assertEqual(engine.default_score_title('Já é tarde.mid'),'Já é tarde')
        def words(text):
            syllables=[{'text':word,'word_start':True} for word in text.split()]
            lyrics_accents.restore(syllables);return ' '.join(s['text'] for s in syllables)
        self.assertEqual(words('o bebe chorou'),'o bebê chorou')
        # Words that are right without accent stay as they are.
        self.assertEqual(words('ele bebe agua'),'ele bebe água')
        self.assertEqual(words('os pais e a nenem atrai'),'os pais e a nenem atrai')


class AlignmentTests(unittest.TestCase):
    def test_whole_karaoke_word_is_split_over_its_notes(self):
        # "coração" written once over three notes; "amor" over one note and a long melisma.
        notes=[(0,60,1),(1,62,1),(2,64,1),(4,65,.5),(4.5,64,.5),(5,62,1),(6,60,1)]+[(8+i,60+i%3,1) for i in range(20)]
        texts=[(0,b'cora\xe7\xe3o '),(4,b'amor ')]+[(8+i,b'la ') for i in range(20)]
        _,notes,_,report=song(notes,texts)
        lyric=[n.get('lyric') for n in sorted(notes,key=lambda n:n['start'])]
        self.assertEqual([(l['text'],l['syllabic']) for l in lyric[:3]],[('co','begin'),('ra','middle'),('ção','end')])
        self.assertEqual((lyric[3]['text'],lyric[3]['syllabic']),('a','begin'))
        self.assertEqual((lyric[4]['text'],lyric[4].get('extend')),('mor',True))
        self.assertEqual(report['mode'],'notes')

    def test_fewer_notes_join_the_last_syllables(self):
        import lyrics_align
        attacks=[dict(start=0,end=900,pitch=60),dict(start=960,end=1900,pitch=62),dict(start=3840,end=4800,pitch=64)]
        events=[dict(pos=0,text='coração',word_start=True),dict(pos=3840,text='sol',word_start=True)]
        plan=lyrics_align.plan(events,attacks,960)
        self.assertEqual([[p['text'] for p in plan['placements'][k]] for k in (0,1,2)],[['co'],['ração'],['sol']])

    def test_global_offset_and_elision(self):
        import lyrics_align
        attacks=[dict(start=i*960,end=i*960+900,pitch=60) for i in range(16)]
        events=[dict(pos=i*960-120,text=f's{i}',word_start=True) for i in range(16)]
        plan=lyrics_align.plan(events,attacks,960)
        self.assertEqual(plan['report']['offset'],120)
        self.assertEqual(len(plan['placements']),16)
        lyric,words=lyrics_align.note_lyric([dict(text='que',word_start=True,word_end=True),dict(text='eu',word_start=True,word_end=True)])
        self.assertEqual((lyric['text'],lyric['syllabic'],words),('que‿eu','single',2))

    def test_spoken_intro_over_a_riff_becomes_text(self):
        riff=[(0,67,1),(1,69,1),(2,71,1),(3,72,1)]
        melody=[(q+16,p,l) for q,p,l in riff]+[(32+i,60+i%4,1) for i in range(24)]
        intro=[(0,b'Is'),(.5,b'so '),(1,b'\xe9 '),(2,b'a '),(3,b'ban'),(3.5,b'da ')]
        texts=[(q,b'\\'+t if not i else t) for i,(q,t) in enumerate(intro)]+[(32+i,(b'/' if i==0 else b'')+b'la ') for i in range(24)]
        _,notes,bars,report=song(riff+melody,texts)
        self.assertEqual([item['text'] for item in report['spoken']],['(falado: Isso é a banda)'])
        self.assertEqual(bars[0]['lyric_texts'][0]['text'],'(falado: Isso é a banda)')
        self.assertTrue(all(n['start']>=16*engine.DIV for n in notes if 'lyric' in n))
        self.assertEqual(report['lines'][0]['placement'],'spoken')

    def test_lines_mode_writes_each_line_under_its_measure(self):
        notes=[(i,60+i%5,1) for i in range(24)]
        texts=[(i,(b'/' if i%8==0 else b'')+b'la ') for i in range(24)]
        _,melody,bars,report=song(notes,texts,{'lyrics_mode':'lines'})
        self.assertEqual((report['mode'],report['attached']),('lines',0))
        self.assertFalse(any('lyric' in n for n in melody))
        self.assertEqual([b['lyric_texts'][0]['text'] for b in bars if b.get('lyric_texts')],['la la la la la la la la']*3)
        self.assertEqual([p['kind'] for p in report['review_points']],['lyrics_dropped'])
        # Automatic mode chooses lines when the melody does not carry the song (here it only plays afterwards).
        offbeat=[(24+i,60+i%3,1) for i in range(24)]
        self.assertEqual(song(offbeat,texts)[3]['mode'],'lines')

    def test_dropped_syllables_are_review_points_per_measure(self):
        notes=[(i,60+i%5,1) for i in range(8)]+[(16+i,62,1) for i in range(12)]
        texts=[(i,b'la ') for i in range(8)]+[(9,b'sem '),(10,b'no'),(10.5,b'ta ')]+[(16+i,b'la ') for i in range(12)]
        report=song(notes,texts,{'lyrics_mode':'notes'})[3]
        self.assertEqual([d['text'] for d in report['dropped']],['sem','no','ta'])
        points=[p for p in report['review_points'] if p['kind']=='lyrics_dropped']
        self.assertEqual([p['measure'] for p in points],[3])
        self.assertIn('“sem”',points[0]['message'])

    def test_musicxml_extender_and_text_line(self):
        import score_musicxml
        lyrics={'lines':[{'measure':1,'measure_position':0,'placement':'notes'},{'measure':2,'measure_position':.75,'placement':'notes'},
                         {'measure':5,'placement':'text'}]}
        self.assertEqual(score_musicxml.verse_bars(lyrics,[{}]*6),{0,2})
        self.assertEqual(score_musicxml.verse_break_penalty(2,{0,2}),0)
        self.assertEqual(score_musicxml.verse_break_penalty(1,{0,2}),.08)
        self.assertEqual(score_musicxml.verse_break_penalty(1,set()),0)
        self.assertGreater(score_musicxml.lyric_width('coração'),score_musicxml.lyric_width('co'))
        long_line=dict(lyric_texts=[dict(offset=0,text='Falei que era empresário e tinha uma Hilux',kind='text')])
        self.assertGreater(score_musicxml.lyric_text_width(long_line),30)
        self.assertEqual(score_musicxml.lyric_text_width({}),0)

    def test_musicxml_writes_speech_as_text_and_extenders(self):
        riff=[(0,67,1),(1,69,1),(2,71,1),(3,72,1)]
        melody=riff+[(q+16,p,l) for q,p,l in riff]+[(32,60,1),(33,62,.5),(33.5,64,.5),(34,65,2)]+[(36+i,60+i%4,1) for i in range(20)]
        texts=[(0,b'\\Is'),(.5,b'so '),(1,b'\xe9 '),(2,b'a '),(3,b'ban'),(3.5,b'da '),(32,b'/um '),(33,b'sol ')]+[(36+i,b'la ') for i in range(20)]
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'letra.mid';midi_file(path,melody,texts);parsed=engine.parse_midi(path)
            harmony=dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])
            xml,report=engine.build_musicxml(parsed,worker.manual_melody_config([dict(A,track=1)]),harmony,Path(folder),'Fala')
            root=engine.E.parse(xml).getroot()
        words=[w.text for w in root.iter('words')]
        self.assertIn('(falado: Isso é a banda)',words)
        self.assertEqual([e.get('type') for e in root.iter('extend')],['start','stop'])
        self.assertNotIn('sung_ticks',report['melody_analysis']['lyrics'])


class KaraokeTextTests(unittest.TestCase):
    def test_split_utf8_and_single_byte_texts(self):
        from inspect_midi import decode_texts
        self.assertEqual(decode_texts([b'cora\xc3','\xa7\xc3\xa3o'.encode('latin-1')]),['cora','ção'])
        self.assertEqual(decode_texts([b'\xef\xbb\xbfs\xc3\xb3']),['só'])
        self.assertEqual(decode_texts([b'cora\xe7\xe3o',b'p\xe9']),['coração','pé'])
        self.assertEqual(decode_texts([b'\x81x']),['\x81x'])

    def test_lines_and_paragraphs_from_karaoke_marks(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'letra.kar';karaoke_midi(path,KARAOKE);parsed=engine.parse_midi(path)
        syllables=engine.karaoke_syllables(parsed)
        self.assertEqual([i for i,s in enumerate(syllables) if s['line_start']],[0,7])
        self.assertEqual([i for i,s in enumerate(syllables) if s['paragraph_start']],[0])

    def test_lyric_events_with_line_breaks_and_trailing_spaces(self):
        words=['Se','gun','da ','fei','ra\r','to','do ','di','a\r\r']*3
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'letra.mid';karaoke_midi(path,words,lyric_type=5,header=False);parsed=engine.parse_midi(path)
        syllables=engine.karaoke_syllables(parsed)
        self.assertEqual([s['text'] for s in syllables if s['word_start']][:4],['Se','fei','to','di'])
        self.assertEqual([i for i,s in enumerate(syllables) if s['line_start']][:4],[0,5,9,14])
        self.assertEqual([i for i,s in enumerate(syllables) if s['paragraph_start']][:2],[0,9])


class LyricCollisionTests(unittest.TestCase):
    def test_rendered_lyric_collisions_are_found(self):
        import batch_svg_qa
        staff=''.join(f'<polyline class="StaffLines" points="100,{200+i*25} 2000,{200+i*25}"/>' for i in range(5))
        def word(x,width,y=420):return f'<path class="Lyrics" d="M{x},{y} L{x+width},{y} L{x+width},{y+30} L{x},{y+30} Z"/>'
        svg=('<svg xmlns="http://www.w3.org/2000/svg">'+staff+word(200,100)+word(303,80)+word(500,80)+
             '<path class="LyricsLineSegment" d="M570,432 L640,432 L640,436 L570,436 Z"/>'+word(630,60)+
             '<path class="Expression" d="M900,410 L1100,410 L1100,440 L900,440 Z"/>'+word(1000,60)+'</svg>')
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'page.svg';path.write_text(svg,encoding='utf-8')
            result=batch_svg_qa.analyze_lyrics(path)
        self.assertEqual(result['staff_space'],25)
        self.assertEqual(sorted(c['type'] for c in result['collisions']),['line-syllable','syllable-syllable','text-syllable'])


@unittest.skipUnless(os.environ.get('TECLAS_TEST_NATIVE_LABELS')=='1','Set TECLAS_TEST_NATIVE_LABELS=1 to render in MuseScore')
class NativeLyricsTests(unittest.TestCase):
    def test_lyrics_survive_musescore_save_and_reopen(self):
        with tempfile.TemporaryDirectory() as folder:
            base=Path(folder);out=base/'letra';out.mkdir()
            path=out/'letra.kar';karaoke_midi(path,KARAOKE);parsed=engine.parse_midi(path)
            harmony=dict(key={'fifths':0,'minor':False},events=[dict(tick=0,root=0,quality='',confidence=1.,pitch_classes=[0,4,7])])
            with patch.object(engine,'BATCH',base),redirect_stdout(io.StringIO()):
                xml,report=engine.build_musicxml(parsed,worker.manual_melody_config([dict(A,track=1)]),harmony,out,'Letra')
                final=engine.convert_score(xml,report,out,'Letra')
            reopened=engine.E.parse(out/'reaberto.musicxml').getroot()
            self.assertEqual([e.findtext('text') for e in reopened.iter('lyric')][:4],['Ca','sa','ri','nho'])
            with zipfile.ZipFile(final) as archive:
                native=engine.E.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.mscx'))))
            self.assertEqual(len(native.findall('.//Lyrics')),18)
            self.assertTrue(report['native_reopened'])


if __name__=='__main__':unittest.main()
