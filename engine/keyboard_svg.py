"""Highlighted keyboard diagram: the chord's keys filled and named on a 10-white-key window.

Same 282x84 frame as the classic two-octave drawing, so the score layout, spacing and visual QA
keep their geometry. The window moves with the chord (preferably starting on C or F), which makes
every key 40% wider than in the classic drawing. Pressed keys are filled; the lowest one (the
inversion's bass) is darker. The Teclas.Core C# generator (NativeHarmonyEditing.HighlightSvg)
writes byte-for-byte the same element tree: keep both in sync.

'pontos' (the default) keeps the keys white: a black dot on each white key of the chord, a white dot on
each black key, the bass as a hollow ring, and the note names in a row under the keyboard (bass in bold).
"""
import xml.etree.ElementTree as E

STYLES=('pontos','destacado','cinza','classico')
WHITE_KEYS=10
KEY_WIDTH=28
NATURAL={0,2,4,5,7,9,11}
LETTERS_SHARP=('C','C#','D','D#','E','F','F#','G','G#','A','A#','B')
LETTERS_FLAT=('C','Db','D','Eb','E','F','Gb','G','Ab','A','Bb','B')
SOLFEGE_SHARP=('Dó','Dó#','Ré','Ré#','Mi','Fá','Fá#','Sol','Sol#','Lá','Lá#','Si')
SOLFEGE_FLAT=('Dó','Réb','Ré','Mib','Mi','Fá','Solb','Sol','Láb','Lá','Sib','Si')
# (pressed white, pressed black, bass white, bass black, name on white, name on black)
PALETTES={
    'destacado':('#5B8DEF','#2F6FEB','#1D3F99','#1D3F99','#FFFFFF','#FFFFFF'),
    'cinza':('#A6A6A6','#8C8C8C','#4D4D4D','#4D4D4D','#000000','#FFFFFF'),
}


LETTERS='CDEFGAB'
LETTER_PC={'C':0,'D':2,'E':4,'F':5,'G':7,'A':9,'B':11}
SOLFEGE_LETTER={'C':'Dó','D':'Ré','E':'Mi','F':'Fá','G':'Sol','A':'Lá','B':'Si'}


def root_spelling(rootpc,spelling=None,flats=False):
    """(letter, alteration) of the chord root: its written spelling, else sharps or flats."""
    text=str(spelling or '').strip()
    if text[:1] in LETTER_PC:
        alter=text[1:].count('#')-text[1:].count('b')
        if (LETTER_PC[text[0]]+alter)%12==rootpc%12:return text[0],alter
    names=LETTERS_FLAT if flats else LETTERS_SHARP
    name=names[rootpc%12]
    return name[0],(1 if '#' in name else -1 if 'b' in name[1:] else 0)


def tone_names(rootpc,intervals,spelling=None,flats=False,names='letras'):
    """Name of each chord tone by its degree over the root (Cm → C, Eb, G; Cdim7 → Bbb as the seventh)."""
    letter,alter=root_spelling(rootpc,spelling,flats)
    seventh_dim=9 in intervals and 3 in intervals and 6 in intervals and 10 not in intervals and 11 not in intervals
    augmented=8 in intervals and 4 in intervals and 7 not in intervals
    result={}
    for interval in intervals:
        steps={0:0,1:1,2:1,3:2,4:2,5:3,6:4,7:4,8:4 if augmented else 5,9:6 if seventh_dim else 5,10:6,11:6}[interval%12]
        if interval==3 and 4 in intervals:steps=1          # #9 next to a major third
        if interval==6 and 7 in intervals:steps=3          # #11 next to a perfect fifth
        target=LETTERS[(LETTERS.index(letter)+steps)%7]
        accidental=((rootpc+interval)-LETTER_PC[target])%12
        if accidental>6:accidental-=12
        if abs(accidental)>2:return None
        if abs(accidental)==2:
            # A key is played, not analysed: Bbb is shown as the key's plain name (A).
            simple=(LETTERS_FLAT if accidental<0 else LETTERS_SHARP)[(rootpc+interval)%12]
            target,accidental=simple[0],(1 if '#' in simple else -1 if 'b' in simple[1:] else 0)
        base=SOLFEGE_LETTER[target] if names=='solfejo' else target
        result[(rootpc+interval)%12]=base+('#'*accidental if accidental>0 else 'b'*-accidental)
    return result


def diagram_style(value):
    if value in (None,''):return 'pontos'
    if value not in STYLES:raise ValueError('Estilo de diagrama desconhecido: '+str(value))
    return value


def window_start(pitches):
    """Lowest white key of the 10-key window: holds every pitch, prefers C or F, then centres the chord."""
    low,high=min(pitches),max(pitches)
    best=None
    for start in range(low-17,low+1):
        if start%12 not in NATURAL:continue
        whites=[p for p in range(start,start+24) if p%12 in NATURAL][:WHITE_KEYS]
        last=whites[-1]
        if high>last:continue
        centre=(start+last)/2-(low+high)/2
        key=(start%12 not in (0,5),abs(centre),-start)
        if best is None or key<best[0]:best=(key,start)
    return best[1]


def dot_name_rows(xs):
    """(font size, y of each name) for names under the keys: one row, or two alternating rows when neighbours crowd."""
    if all(b-a>=24 for a,b in zip(xs,xs[1:])):return '16',['79']*len(xs)
    return '12',['72' if index%2==0 else '83' for index in range(len(xs))]


def svg_dots(pitches,names='letras',flats=False,spelled=None):
    """Element tree text of the 'pontos' diagram (UTF-8, with XML declaration)."""
    pitches=sorted(pitches)
    start=window_start(pitches)
    whites=[p for p in range(start,start+24) if p%12 in NATURAL][:WHITE_KEYS]
    blacks=[p for p in range(start,whites[-1]) if p%12 not in NATURAL]
    table=(SOLFEGE_FLAT if flats else SOLFEGE_SHARP) if names=='solfejo' else (LETTERS_FLAT if flats else LETTERS_SHARP)
    root=E.Element('svg',xmlns='http://www.w3.org/2000/svg',width='282',height='84',viewBox='0 0 282 84')
    E.SubElement(root,'title').text='Acorde'
    E.SubElement(root,'desc').text='Teclas marcadas com pontos'
    bass=pitches[0]
    for index,pitch in enumerate(whites):
        E.SubElement(root,'rect',x=str(1+index*KEY_WIDTH),y='1',width=str(KEY_WIDTH),height='60',fill='white',stroke='#111111',**{'stroke-width':'1'})
    for pitch in blacks:
        E.SubElement(root,'rect',x=str(1+(whites.index(pitch-1)+1)*KEY_WIDTH-8),y='1',width='16',height='37',fill='#111111')
    xs=[]
    for pitch in pitches:
        natural=pitch%12 in NATURAL
        x=1+whites.index(pitch)*KEY_WIDTH+KEY_WIDTH//2 if natural else 1+(whites.index(pitch-1)+1)*KEY_WIDTH
        xs.append(x)
        ink='#111111' if natural else '#FFFFFF'
        if pitch==bass:
            attributes={'r':'6' if natural else '5','fill':'none','stroke':ink,'stroke-width':'2.5'}
        else:
            attributes={'r':'7' if natural else '6','fill':ink}
        E.SubElement(root,'circle',cx=str(x),cy='50' if natural else '28',**attributes,**{'data-midi':str(pitch)})
    size,rows=dot_name_rows(xs)
    for pitch,x,y in zip(pitches,xs,rows):
        name=(spelled or {}).get(pitch%12) or table[pitch%12]
        attributes={'font-family':'Arial','font-size':size,'text-anchor':'middle'}
        if pitch==bass:attributes['font-weight']='bold'
        E.SubElement(root,'text',x=str(x),y=y,fill='#000000' if pitch==bass else '#555555',**attributes).text=name
    return E.tostring(root,encoding='utf-8',xml_declaration=True)


def svg_highlight(pitches,style='destacado',names='letras',flats=False,spelled=None):
    """Element tree text of the highlighted diagram (UTF-8, with XML declaration).

    spelled maps pitch class → written name (from tone_names); without it keys use sharps or flats."""
    style=diagram_style(style)
    if style=='pontos':return svg_dots(pitches,names,flats,spelled)
    if style=='classico':raise ValueError('Use o desenho clássico para o estilo classico.')
    pitches=sorted(pitches)
    start=window_start(pitches)
    whites=[p for p in range(start,start+24) if p%12 in NATURAL][:WHITE_KEYS]
    blacks=[p for p in range(start,whites[-1]) if p%12 not in NATURAL]
    white_fill,black_fill,bass_white,bass_black,white_ink,black_ink=PALETTES[style]
    table=(SOLFEGE_FLAT if flats else SOLFEGE_SHARP) if names=='solfejo' else (LETTERS_FLAT if flats else LETTERS_SHARP)
    root=E.Element('svg',xmlns='http://www.w3.org/2000/svg',width='282',height='84',viewBox='0 0 282 84')
    E.SubElement(root,'title').text='Acorde'
    E.SubElement(root,'desc').text='Teclas destacadas'
    bass=pitches[0]
    for index,pitch in enumerate(whites):
        pressed=pitch in pitches
        fill=(bass_white if pitch==bass else white_fill) if pressed else 'white'
        attributes={'x':str(1+index*KEY_WIDTH),'y':'1','width':str(KEY_WIDTH),'height':'60','fill':fill,'stroke':'#111111','stroke-width':'1'}
        if pressed:attributes['data-midi']=str(pitch)
        E.SubElement(root,'rect',**attributes)
    for pitch in blacks:
        pressed=pitch in pitches
        x=1+(whites.index(pitch-1)+1)*KEY_WIDTH-8
        fill=(bass_black if pitch==bass else black_fill) if pressed else '#111111'
        attributes={'x':str(x),'y':'1','width':'16','height':'37','fill':fill}
        if pressed:attributes.update({'stroke':'#111111','stroke-width':'1','data-midi':str(pitch)})
        E.SubElement(root,'rect',**attributes)
    for pitch in pitches:
        natural=pitch%12 in NATURAL
        if natural:x=1+whites.index(pitch)*KEY_WIDTH+KEY_WIDTH//2;y='56';size='15';ink=white_ink
        else:x=1+(whites.index(pitch-1)+1)*KEY_WIDTH;y='33';size='11';ink=black_ink
        name=(spelled or {}).get(pitch%12) or table[pitch%12]
        E.SubElement(root,'text',x=str(x),y=y,fill=ink,**{'font-family':'Arial','font-size':size,'font-weight':'bold','text-anchor':'middle'}).text=name
    label=table[0]
    for index,pitch in enumerate(whites):
        if pitch%12==0:
            E.SubElement(root,'text',x=str(1+index*KEY_WIDTH+KEY_WIDTH//2),y='79',fill='#555555',
                         **{'font-family':'Arial','font-size':'15','text-anchor':'middle'}).text=label+str(pitch//12-1)
    return E.tostring(root,encoding='utf-8',xml_declaration=True)
