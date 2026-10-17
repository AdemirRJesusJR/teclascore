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
        self.assertEqual(report,{'syllables':18,'attached':18})
        self.assertNotIn('@TTitulo',[text for text,_ in lyrics])

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
            self.assertEqual(report['melody_analysis']['lyrics'],{'syllables':18,'attached':18})


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
