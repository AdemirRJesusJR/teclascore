"""Restores Portuguese accents in karaoke lyrics written without them ("voce", "coracao").

Many karaoke files strip accents and the cedilla. Only unambiguous words are restored: a form that is
also a correct word without the accent (e/é, esta/está, pais/país, sabia/sabiá...) is left untouched.
Every replacement has the same number of letters, so it maps letter by letter onto the syllables.
"""
import unicodedata

_WORDS = '''
você vocês não então também além ninguém alguém porém até após atrás aliás já só pé fé céu réu mão mãos chão
irmão irmã irmãos coração corações paixão paixões emoção emoções canção canções oração orações solidão
razão perdão sertão verão nação nações ilusão ilusões multidão escuridão imensidão estação estações
tão são vão estão serão terão darão farão dirão então cristão pão leão sabão sabões feijão algodão
mãe mães pães cães alemão capitão violão violões sanfona mamãe papai vovó vovô
será terá dará fará dirá virá estará ficará voltará chegará verá lembrará esquecerá morrerá viverá
amará chorará cantará sonhará
está estás
três mês português inglês francês freguês burguês
café cafuné bebê nenê buquê clichê
você pescoço moço moça moços moças coraçãozinho laço abraço abraços braço braços pedaço pedaços
espaço cansaço fracasso começo começa começou começar caçar caço criança crianças lembrança lembranças
esperança esperanças dança danças balança mudança mudanças confiança segurança saudação maçã
música músicas lágrima lágrimas lâmpada fácil difícil único única últimas último última próximo próxima
sábado domingo número números história histórias memória memórias glória vitória sozinho
coração amável impossível possível incrível terrível horrível sensível invisível
água águas mágico mágica rápido rápida pássaro pássaros árvore árvores lábios lábio
olá adeus ninguém
avião aviões razões perdões ilusória canções
cruéis anéis papéis pastéis fiéis hotéis
herói heróis dói dói constrói destrói
saí caí traí distraí atraí sorrí
aí'''.split()

_AMBIGUOUS = {'e', 'esta', 'estas', 'pais', 'sabia', 'secretaria', 'amem', 'nos', 'la', 'ai', 'avo', 'magoa',
              'pe', 'ha', 'da', 'de', 'do', 'se', 'so', 'sai', 'cai', 'trai', 'dói', 'sorri', 'esta', 'nossa',
              'publico', 'pratica', 'musica', 'duvida', 'valido', 'transito', 'critica', 'fabrica', 'secretaria',
              'cantara', 'chorara', 'sonhara', 'amara', 'morrera', 'vivera', 'esquecera', 'fara', 'vira', 'sera',
              'tera', 'dara', 'ficara', 'voltara', 'chegara', 'vera', 'lembrara', 'estara', 'dira'}


def _plain(word):
    return ''.join(c for c in unicodedata.normalize('NFD', word) if unicodedata.category(c) != 'Mn')


def _table():
    table = {}
    for word in _WORDS:
        word = unicodedata.normalize('NFC', word)
        plain = _plain(word)
        if plain == word or plain in _AMBIGUOUS or len(plain) != len(word):
            continue
        table[plain] = word
    # "só" and "pé" are words of their own; "so"/"pe" never are in Portuguese lyrics.
    table.update(so='só', pe='pé')
    return table


ACCENTED = _table()


def needs_restoring(texts):
    """True when the lyric has letters but not a single accented one (an accent-stripped file)."""
    joined = ''.join(texts)
    return any(c.isalpha() for c in joined) and not any(ord(c) > 127 and c.isalpha() for c in joined)


def restore_word(word):
    """Accented spelling of one word, keeping its capital letters; None when unknown or ambiguous."""
    accented = ACCENTED.get(word.lower())
    if accented is None:
        return None
    return ''.join(a.upper() if w.isupper() else a for w, a in zip(word, accented))


def restore(syllables):
    """Restores accents in place on syllable dicts ({'text','word_start'}); returns how many words changed."""
    words = []
    for item in syllables:
        if item['word_start'] or not words:
            words.append([item])
        else:
            words[-1].append(item)
    changed = 0
    for group in words:
        text = ''.join(item['text'] for item in group)
        letters = ''.join(c for c in text if c.isalpha())
        if letters != text.strip(".,;:!?\"'()…-"):
            continue
        core = text.strip(".,;:!?\"'()…-")
        fixed = restore_word(core)
        if fixed is None or fixed == core:
            continue
        offset = text.index(core)
        result = text[:offset] + fixed + text[offset + len(core):]
        position = 0
        for item in group:
            size = len(item['text'])
            item['text'] = result[position:position + size]
            position += size
        changed += 1
    return changed
