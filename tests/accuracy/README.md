# Medição de precisão do motor

Mede o quanto a partitura gerada acerta a música, comparando-a com um gabarito, em vez de comparar
hashes. Uma correção no motor faz as métricas subirem. Só uma piora faz o teste falhar.

## Arquivos

- `make_corpus.py`: gera 22 MIDIs sintéticos e determinísticos, cada um com seu gabarito `.json` ao
  lado. O gabarito traz a melodia escrita (altura, grafia, início e duração), as cifras (fundamental,
  qualidade e baixo), a tonalidade e as mudanças, o compasso, a anacruse e as sílabas. Os casos cobrem:
  - melodia reta, swing e tercinas (completas, parciais e de semínima);
  - voz interna, troca de programa, 3/4 sem meta de compasso, 6/8 e anacruse;
  - modulação de meio tom (com e sem meta 89) e dominantes secundárias;
  - acordes sem quinta, cifras com baixo, pedal e acordes 13, 7(b9) e 6/9;
  - karaokê por sílaba e por palavra, fala na introdução, letra deslocada e duas sílabas numa nota.
- `metrics.py`: lê o `melodia.musicxml` gerado, ignorando campos que não conhece, e calcula as métricas.
- `engine_driver.py`: roda o `worker.py` de verdade (analyze e generate) num processo separado. Por
  padrão, a etapa do MuseScore é substituída e não renderiza nada.
- `run_accuracy.py`: roda o corpus, grava `accuracy-report.json` e compara com a linha de base.
- `baseline.json`: métricas do motor atual. O campo `engine_commit` indica o último commit que mudou `engine/`.
- `quantization_metrics_baseline.json`: métricas dos 18 perfis revisados, usadas por `tests/quantization_regression.py`.

## Como rodar

Use o Python embutido do aplicativo (veja `tools/ci/run_python_tests.py`):

    python -B tests/accuracy/run_accuracy.py --baseline tests/accuracy/baseline.json --output accuracy-report.json

O corpus inteiro leva poucos segundos e não precisa do MuseScore. O código de saída é 1 quando
alguma métrica piora mais que a tolerância (`--tolerance`, padrão 0,02) em algum caso ou na média.
As métricas que pioraram aparecem como `PIOROU` e as que melhoraram como `MELHOROU`.

Outras opções:

- `--case swing --case anacruse`: mede só esses casos.
- `--native "C:\Program Files\MuseScore 4\bin\MuseScore4.exe"`: gera com o MuseScore de verdade (mais lento).
- `--work <pasta>`: muda a pasta de trabalho (padrão: `%TEMP%\TeclaScoreAccuracy`).

O `tools/ci/run_python_tests.py` roda o corpus depois dos testes de unidade e grava
`accuracy-report.json` ao lado do relatório. Use `--skip-accuracy` para pular essa etapa.

## Como ler o relatório

`accuracy-report.json` traz:

- `cases.<caso>.metrics`: as métricas do caso;
- `cases.<caso>.details`: notas pareadas, deslocamento usado e tempo;
- `averages`: a média de cada métrica entre os casos em que ela se aplica;
- `comparison`: pioras e melhoras em relação à linha de base.

Todas as métricas vão de 0 a 1. Só `lyrics_dropped` é melhor quanto menor. `null` significa que a
métrica não se aplica ao caso, por exemplo letra num caso sem letra. Se o motor falhar num caso, as
métricas dele ficam com o pior valor.

| Métrica | O que mede |
|---|---|
| `onset_f1` | F1 dos ataques da melodia, com erro menor que 1/16 |
| `onset_exact_f1` | F1 dos ataques com o ritmo escrito exato (tercinas, swing) |
| `pitch`, `pitch_class`, `octave` | altura exata, classe de altura e oitava das notas pareadas |
| `duration` | duração escrita certa, com tolerância de 1/32 |
| `spelling` | grafia certa (letra e alteração), por exemplo G# sobre E7 em Dó |
| `key`, `key_changes` | tonalidade inicial e F1 das mudanças (mesma armadura, até 1 compasso de distância) |
| `meter`, `pickup` | fórmula de compasso inicial e anacruse |
| `chord_root`, `chord_quality`, `chord_bass`, `chord_symbol`, `chord_root_spelling` | cifras ponderadas pelo tempo; o baixo só conta onde importa |
| `lyrics_syllables`, `lyrics_words`, `lyrics_dropped`, `lyrics_spoken_off` | sílabas e palavras na nota certa, sílabas perdidas e fala fora das notas |

## Atualizar a linha de base

Depois de uma melhora confirmada, ou de mudar o corpus, grave a nova linha de base com o corpus inteiro:

    python -B tests/accuracy/run_accuracy.py --update-baseline

Para os perfis revisados, rode a partir dos MIDIs originais (não roda no CI):

    python -B tests/quantization_regression.py --midi-directory "<pasta dos MIDIs>" --update-metrics

## Corpus real (opcional, não roda no CI)

    python -B tests/accuracy/run_accuracy.py --real ["P:\2. Documentos\4. Teclado\2. MIDI"] --real-limit 12

As pistas da melodia vêm da biblioteca do aplicativo (`%LOCALAPPDATA%\TeclasPartituras\settings.json`)
ou de requisições antigas em `jobs`. As saídas vão só para a pasta de trabalho.

Uma partitura aprovada na biblioteca serve de referência. Ela é procurada também na pasta de saída e
exportada para MusicXML com o MuseScore. Com `--any-reference`, a última partitura gerada também vale
como referência, mesmo sem aprovação.

Com referência, o relatório traz as mesmas métricas do corpus sintético. Sem referência, traz
indicadores sem gabarito:

- fração de sílabas encaixadas;
- cobertura da letra pela melodia;
- pontos de revisão;
- colisões de letra (duas ou mais palavras numa nota);
- nota de qualidade;
- tempo de geração.
