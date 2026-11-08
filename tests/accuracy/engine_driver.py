"""Roda o worker do motor (analyze e depois generate) num processo isolado.

Sem --native, a etapa do MuseScore é trocada por um substituto que não renderiza: o MusicXML da
melodia (melodia.musicxml) e o validacao.json saem exatamente do mesmo caminho do aplicativo
(worker -> prepare -> build_musicxml), só sem o .mscz e as páginas. Assim a medição não depende do
MuseScore e acompanha qualquer opção nova que o worker passe a ler das requisições.

    python engine_driver.py <request-analyze.json> <request-generate.json> [--native]

Cada linha da saída é um JSON do worker (progress/result/error) ou deste driver (timing).
"""
from pathlib import Path
import json
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / 'engine'


def convert_without_musescore(xml, report, out, *args, **kwargs):
    """Substituto de batch_score.convert_score: devolve um .mscz vazio e uma prévia em branco."""
    out = Path(out)
    final = out / 'sem-musescore.mscz'
    final.write_bytes(b'')
    from PIL import Image
    Image.new('RGB', (4, 4), 'white').save(out / 'pagina-1.png')
    report.setdefault('pages', report.get('pages_planned', 1))
    report.setdefault('melody_attacks_verified', len(report.get('notes', [])))
    try:
        import score_quality
        report['quality'] = score_quality.evaluate(report)
    except Exception as error:  # a qualidade é informativa; não derruba a medição
        report['quality_error'] = str(error)
    return final


def main(argv):
    requests = [item for item in argv[1:] if not item.startswith('--')]
    native = '--native' in argv
    sys.path.insert(0, str(ENGINE))
    import worker
    for request in requests:
        mode = json.loads(Path(request).read_text(encoding='utf-8-sig'))['mode']
        sys.argv = [str(ENGINE / 'worker.py'), request]
        if mode == 'generate' and not native:
            import batch_score
            batch_score.convert_score = convert_without_musescore
        started = time.perf_counter()
        try:
            worker.main()
        except Exception as error:
            print(json.dumps({'kind': 'error', 'mode': mode, 'message': str(error), 'type': type(error).__name__,
                              'reviewPoints': getattr(error, 'review_points', []),
                              'traceback': ''.join(traceback.format_exception(type(error), error, error.__traceback__))},
                             ensure_ascii=False), flush=True)
            return 1
        finally:
            print(json.dumps({'kind': 'timing', 'mode': mode, 'seconds': round(time.perf_counter() - started, 3)}), flush=True)
    return 0


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    sys.exit(main(sys.argv))
