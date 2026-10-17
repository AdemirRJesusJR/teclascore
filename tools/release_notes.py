"""Write GitHub release notes from CHANGELOG.md: every version after --since up to the current one."""
from pathlib import Path
import argparse
import re
import xml.etree.ElementTree as ET

BASE = Path(__file__).resolve().parents[1]


def version_key(text):
    return tuple(int(part) for part in text.split('.'))


def notes(since=None):
    current = ET.parse(BASE / 'src/Teclas.App/Teclas.App.csproj').findtext('.//Version')
    text = (BASE / 'CHANGELOG.md').read_text(encoding='utf-8')
    sections = re.split(r'(?m)^## ', text)[1:]
    chosen = []
    for section in sections:
        version = section.split(' ', 1)[0].strip()
        if not re.fullmatch(r'\d+\.\d+\.\d+', version) or version_key(version) > version_key(current):
            continue
        if since and version_key(version) <= version_key(since):
            continue
        chosen.append('## ' + section.strip())
    if not chosen or not chosen[0].startswith('## ' + current):
        raise SystemExit('O CHANGELOG não tem a seção da versão ' + current)
    intro = (f'TeclaScore {current} para Windows x64.\n\n'
             'Baixe o instalador (TeclaScore-' + current + '-Setup-x64.exe) ou o ZIP portátil. '
             'Os hashes SHA-256 estão em SHA256SUMS.\n')
    if len(chosen) > 1:
        intro += f'\nEsta release reúne as mudanças desde a versão {since}.\n'
    return current, intro + '\n' + '\n\n'.join(chosen) + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--since', help='Última versão publicada como release (exclusiva).')
    parser.add_argument('--output', required=True)
    options = parser.parse_args()
    version, body = notes(options.since)
    Path(options.output).write_text(body, encoding='utf-8')
    print(version, len(body))
