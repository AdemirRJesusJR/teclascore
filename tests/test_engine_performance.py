"""Desempenho do motor: um job do MuseScore por passe, ordem das verificações, parada antecipada e tempos."""
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
import copy, io, json, os, shutil, sys, tempfile, unittest, zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScorePerformanceTests'))
import batch_score as engine
import score_labels
import worker


def geometry(count, width=1.0):
    """Uma página com ``count`` sobreposições do diagrama consigo mesmo, cada uma com ``width``."""
    return dict(diagrams=[dict(index=0, row=0, bbox=[4.5, 0, 18.5, 4.2], harmony_index=0)],
                harmonies=[dict(index=0, row=0, bbox=[0, 0, 1.5, 3], diagram_index=0)],
                rows=[dict(row=0, diagrams=[0], harmonies=[0], pairing_by_order_safe=True)],
                warnings=[], clipped=[],
                overlaps=[dict(type='harmony-image', harmony=0, diagram=0, own_pair=True, width=width)] * count)


class JobTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.out = Path(self.temporary.name)

    def test_save_and_render_is_one_job_with_score_and_qa_svg_only(self):
        for name in ('qa-1.svg', 'qa-2.svg', 'pagina-1.png'): (self.out / name).write_text('velho')
        calls = []
        def fake(args, log, timeout=90):
            calls.append((args, log, timeout))
            self.assertEqual(sorted(p.name for p in self.out.glob('qa-*.svg')) + list(self.out.glob('pagina-*.png')), [])
        with patch.object(engine, 'call_musescore', side_effect=fake):
            engine.save_and_render(self.out / 'preparado.mscz', self.out / 'final.mscz', self.out, self.out / 'ajuste_espacamento.log')
        self.assertEqual(len(calls), 1)
        args, log, timeout = calls[0]
        self.assertEqual(args[:3], ['-r', '120', '-j'])
        job = json.loads(Path(args[3]).read_text(encoding='utf8'))
        self.assertEqual(job, [{'in': str(self.out / 'preparado.mscz'), 'out': [str(self.out / 'final.mscz'), str(self.out / 'qa.svg')]}])
        self.assertEqual(log.stem, 'ajuste_espacamento')  # mantém a mensagem de progresso e o tipo nos tempos
        self.assertGreaterEqual(timeout, 150)

    def test_final_render_reopens_saved_score_for_png_and_musicxml(self):
        (self.out / 'qa-1.svg').write_text('svg'); (self.out / 'pagina-7.png').write_text('velho')
        def fake(args, log, timeout=90):
            job = json.loads(Path(args[3]).read_text(encoding='utf8'))
            self.assertEqual(job, [{'in': str(self.out / 'final.mscz'), 'out': [str(self.out / 'pagina.png'), str(self.out / 'reaberto.musicxml')]}])
            self.assertFalse((self.out / 'pagina-7.png').exists())
            (self.out / 'pagina-1.png').write_text('png')
        with patch.object(engine, 'call_musescore', side_effect=fake):
            engine.render_final_pages(self.out / 'final.mscz', self.out)

    def test_final_render_with_other_page_count_is_a_musescore_error(self):
        (self.out / 'qa-1.svg').write_text('svg'); (self.out / 'qa-2.svg').write_text('svg')
        def fake(args, log, timeout=90): (self.out / 'pagina-1.png').write_text('png')
        with patch.object(engine, 'call_musescore', side_effect=fake), self.assertRaises(engine.NeedsReview) as raised:
            engine.render_final_pages(self.out / 'final.mscz', self.out)
        self.assertEqual(raised.exception.kind, 'musescore')


class TimingTests(unittest.TestCase):
    def setUp(self):
        engine.reset_timings(); self.addCleanup(engine.TIMINGS.clear)

    def test_each_musescore_call_records_kind_and_seconds(self):
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
            log = Path(directory) / 'gravacao.log'
            with patch.object(engine.subprocess, 'run', return_value=SimpleNamespace(stdout='', stderr='', returncode=0)):
                engine.call_musescore(['-v'], log)
            with patch.object(engine.subprocess, 'run', return_value=SimpleNamespace(stdout='', stderr='', returncode=3)), \
                    self.assertRaises(engine.NeedsReview) as raised:
                engine.call_musescore(['-v'], Path(directory) / 'render.log')
        self.assertEqual(raised.exception.kind, 'musescore')
        snapshot = engine.timings_snapshot()
        self.assertEqual([call['kind'] for call in snapshot['musescore_calls']], ['gravacao', 'render'])
        self.assertTrue(all(call['seconds'] >= 0 for call in snapshot['musescore_calls']))
        self.assertIn('musescore', snapshot['stages']); self.assertIn('total', snapshot['stages'])

    def test_nested_stage_counts_once_and_snapshot_is_json(self):
        with engine.timed('svg_analysis'):
            with engine.timed('svg_analysis'): pass
        engine.count_timing('spacing_passes'); engine.count_timing('spacing_passes')
        snapshot = json.loads(json.dumps(engine.timings_snapshot()))
        self.assertEqual(snapshot['spacing_passes'], 2)
        self.assertEqual(set(snapshot), {'version', 'stages', 'musescore_calls', 'spacing_passes', 'conversions'})
        self.assertLessEqual(snapshot['stages']['svg_analysis'], snapshot['stages']['total'])


class SpacingStallTests(unittest.TestCase):
    def run_repair(self, observed):
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
            out = Path(directory); final = out / 'score.mscz'
            with zipfile.ZipFile(final, 'w') as archive:
                archive.writestr('score.mscx', '''<museScore><Score><Division>480</Division><Staff><Measure><voice>
                    <Harmony><root>14</root></Harmony><Image><offset x="4.5" y="-6.7"/><path>k.svg</path></Image>
                    <Rest><durationType>quarter</durationType></Rest></voice></Measure></Staff></Score></museScore>''')
            (out / 'qa-1.svg').touch()
            report = dict(chord_symbols=1, chords=[dict(tick=0)], bars=[dict(start=0, end=engine.DIV)], image_spacing=[0.0], end_spacing={})
            def fake(args, log, timeout=90):
                job = json.loads(Path(args[3]).read_text(encoding='utf8'))[0]
                shutil.copyfile(job['in'], job['out'][0]); (out / 'qa-1.svg').touch()
            pages = iter(observed)
            with patch.object(engine, 'analyze_svg', side_effect=lambda _: copy.deepcopy(next(pages))), \
                    patch.object(engine, 'call_musescore', side_effect=fake), \
                    patch.object(engine, 'repair_diagram_lanes', return_value=False) as lanes:
                try:
                    engine.repair_spacing(final, report, out); error = None
                except engine.NeedsReview as raised: error = raised
            return report, error, lanes

    def test_plateau_in_count_with_shrinking_overlaps_still_converges(self):
        # Histórico real (11, 2, 2, 2, 0): a contagem para, mas as sobreposições encolhem.
        observed = [geometry(11, 3), geometry(2, 2), geometry(2, 1.2), geometry(2, .5), geometry(0)]
        report, error, lanes = self.run_repair(observed)
        self.assertIsNone(error)
        self.assertEqual([p['overlaps'] for p in report['geometric_layout_qa']['passes']], [11, 2, 2, 2, 0])
        lanes.assert_not_called()

    def test_two_passes_without_progress_go_to_lanes_then_fail_with_same_kind(self):
        # Oscilação real (… 81, 84, 78, 90, 78, 84, 84) reduzida: nada melhora em 2 passes seguidos.
        observed = [geometry(5, 2), geometry(3, 2), geometry(4, 2), geometry(3, 2)] + [geometry(3, 2)] * 6
        report, error, lanes = self.run_repair(observed)
        self.assertIsNotNone(error)
        self.assertEqual(error.kind, 'diagram_layout')
        self.assertIn('Não foi possível acomodar todos os diagramas', str(error))
        self.assertEqual(len(error.layout_history), 4)
        lanes.assert_called_once()
        self.assertFalse(report['geometric_layout_qa']['passed'])

    def test_steady_improvement_keeps_all_ten_passes(self):
        observed = [geometry(10 - i, 2) for i in range(10)]
        report, error, lanes = self.run_repair(observed)
        self.assertEqual(len(error.layout_history), 10)


class LabelOrderTests(unittest.TestCase):
    """Nomes dos instrumentos logo após o primeiro render; paginação e espaçamento uma vez."""
    def run_layout(self, before, after=None, planned=((({0: {'x': 0, 'y': 0, 'lane': 0}}), 2.0))):
        calls = []
        pages = iter([before] + ([after] if after is not None else []))
        report = {'instrument_labels': [{'original_quarter': 1.0}], 'origin_removed_quarters': 0, 'bars': [dict(start=0, end=engine.DIV)]}
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory); (out / 'qa-1.svg').touch()
            with patch.object(score_labels, '_label_pages', side_effect=lambda _: [copy.deepcopy(next(pages))]), \
                    patch.object(engine, '_instrument_label_lanes', side_effect=lambda _: calls.append('plan') or planned), \
                    patch.object(score_labels, '_apply_label_lanes', side_effect=lambda *a: calls.append('lanes')), \
                    patch.object(engine, 'repair_orphan_pages', side_effect=lambda *a: calls.append('orphan')), \
                    patch.object(engine, 'repair_spacing', side_effect=lambda *a: calls.append('spacing')), \
                    patch.object(engine, 'verify_instrument_label_layout', side_effect=lambda f, r, o, h, first: calls.append(('verify', first))):
                engine.layout_with_instrument_labels(out / 'score.mscz', report, out)
        return calls, report

    clean = dict(labels=[{}], overlaps=[], clipped=[])
    crowded = dict(labels=[{}], overlaps=[{'index': 0, 'kind': 'Image'}], clipped=[])

    def test_names_that_fit_keep_the_previous_order_exactly(self):
        calls, report = self.run_layout(self.clean)
        self.assertEqual(calls, ['orphan', 'spacing', ('verify', 0)])
        self.assertNotIn('instrument_label_layout_qa', report)

    def test_colliding_names_get_lanes_before_one_pagination_and_spacing(self):
        calls, report = self.run_layout(self.crowded, self.clean)
        self.assertEqual(calls, ['plan', 'lanes', 'orphan', 'spacing', ('verify', 1)])
        self.assertEqual([p['overlaps'] for p in report['instrument_label_layout_qa']['passes']], [1, 0])

    def test_hopeless_names_fail_before_any_spacing_pass(self):
        with self.assertRaises(engine.NeedsReview) as raised: self.run_layout(self.crowded, planned=None)
        self.assertEqual(raised.exception.kind, 'instrument_label_layout')

    def test_names_still_colliding_after_lanes_fail_without_spacing(self):
        with self.assertRaises(engine.NeedsReview) as raised: self.run_layout(self.crowded, self.crowded)
        self.assertEqual(raised.exception.kind, 'instrument_label_layout')


class WorkerErrorKindTests(unittest.TestCase):
    def test_error_message_carries_error_kind_review_points_and_timings(self):
        engine.reset_timings(); self.addCleanup(engine.TIMINGS.clear)
        error = engine.NeedsReview('Repetição', kind='repeat_jump'); error.review_points = [{'measure': 3}]
        output = io.StringIO()
        with redirect_stdout(output), redirect_stderr(io.StringIO()): worker.report_error(error)
        message = json.loads(output.getvalue())
        self.assertEqual(message['kind'], 'error')
        self.assertEqual(message['errorKind'], 'repeat_jump')
        self.assertEqual(message['reviewPoints'], [{'measure': 3}])
        self.assertIn('stages', message['timings'])

    def test_unexpected_failure_has_null_error_kind(self):
        output = io.StringIO()
        with redirect_stdout(output), redirect_stderr(io.StringIO()): worker.report_error(ValueError('x'))
        self.assertIsNone(json.loads(output.getvalue())['errorKind'])


if __name__ == '__main__': unittest.main()
