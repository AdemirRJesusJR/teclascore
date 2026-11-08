"""Largura das sílabas da letra em espaços de pauta, medida com uma fonte de métrica próxima à das letras do MuseScore.

As letras do MuseScore 4 usam a fonte Edwin (família New Century Schoolbook) em 10 pt, proporcional ao espaço de
pauta. A Edwin fica embutida no MuseScore; mede-se com uma fonte do Windows de desenho parecido e um fator
calibrado contra a largura real das letras no SVG exportado pelo MuseScore 4.7 (largura = em × fator).
"""
import os
from functools import lru_cache

# (arquivo em %WINDIR%\Fonts, fator em espaços de pauta por em). Calibrado com 18 palavras renderizadas.
FONTS = (('CENTURY.TTF', 1.977), ('georgia.ttf', 2.066), ('times.ttf', 2.290))
CHARACTER = 1.02  # sem fonte disponível: largura média de um caractere


@lru_cache(maxsize=1)
def _font():
    try:
        from PIL import ImageFont
    except ImportError:
        return None
    folder = os.path.join(os.environ.get('WINDIR', r'C:\Windows'), 'Fonts')
    for name, factor in FONTS:
        path = os.path.join(folder, name)
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, 1000), factor
            except OSError:
                continue
    return None


@lru_cache(maxsize=4096)
def text_width(text):
    """Largura do texto em espaços de pauta (o sinal de elisão conta como um espaço estreito)."""
    if not text:
        return 0.0
    font = _font()
    if font is None:
        return len(text) * CHARACTER
    face, factor = font
    return face.getlength(text.replace('\u203f', ' ')) / 1000 * factor
