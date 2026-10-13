"""Worker failures keep readable status messages and recoverable diagnostics."""
from contextlib import redirect_stderr,redirect_stdout
from pathlib import Path
from unittest.mock import patch
import io,json,subprocess,sys,tempfile,unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'engine'))
import worker


class WorkerErrorTests(unittest.TestCase):
    def report(self,error,request_path):
        output=io.StringIO();stderr=io.StringIO()
        with redirect_stdout(output),redirect_stderr(stderr):
            worker.report_error(error,request_path)
        return json.loads(output.getvalue()),stderr.getvalue()

    def test_legacy_layout_details_are_saved_without_overloading_status(self):
        history=[{'pass':i,'overlaps':2,'margin_overflows':1} for i in range(10)]
        original='Sobreposição residual após ajuste de diagrama: '+json.dumps(history)
        with tempfile.TemporaryDirectory() as directory:
            request=Path(directory)/'request.json'
            try:raise RuntimeError(original)
            except RuntimeError as error:message,stderr=self.report(error,request)
            self.assertLess(len(message['message']),180)
            self.assertNotIn('overlaps',message['message'])
            self.assertIn('diagramas de acordes',message['message'])
            diagnostic=Path(message['diagnosticPath'])
            self.assertEqual(diagnostic.parent,Path(directory).resolve())
            saved=json.loads(diagnostic.read_text(encoding='utf8'))
            self.assertEqual(saved['message'],original)
            self.assertIn('test_legacy_layout_details',saved['traceback'])
            self.assertIn(original,stderr)

    def test_structured_layout_error_preserves_history(self):
        error=RuntimeError('Falha de diagramação')
        error.kind='diagram_layout';error.layout_history=[{'pass':4,'overlaps':1}]
        with tempfile.TemporaryDirectory() as directory:
            message,_=self.report(error,Path(directory)/'request.json')
            saved=json.loads(Path(message['diagnosticPath']).read_text(encoding='utf8'))
            self.assertEqual(saved['kind'],'diagram_layout')
            self.assertEqual(saved['layoutHistory'],error.layout_history)
            self.assertIn('diagramas de acordes',message['message'])

    def test_actionable_validation_and_review_points_are_not_rewritten(self):
        error=ValueError('Escolha ao menos uma pista para a melodia.')
        error.review_points=[{'measure':3,'kind':'rhythm_collision'}]
        with tempfile.TemporaryDirectory() as directory:
            message,_=self.report(error,Path(directory)/'request.json')
            self.assertEqual(message['message'],str(error))
            self.assertEqual(message['reviewPoints'],error.review_points)

    def test_diagnostic_failure_does_not_hide_original_error_or_claim_saved(self):
        error=RuntimeError('Sobreposição residual após ajuste de diagrama: []')
        with patch.object(Path,'open',side_effect=PermissionError('negado')):
            message,stderr=self.report(error,ROOT/'request.json')
        self.assertIsNone(message['diagnosticPath'])
        self.assertNotIn('salvos',message['message'])
        self.assertIn('negado',stderr)
        self.assertIn(str(error),stderr)

    def test_retries_preserve_prior_diagnostic(self):
        with tempfile.TemporaryDirectory() as directory:
            request=Path(directory)/'request.json'
            first,_=self.report(ValueError('Primeira falha'),request)
            previous=Path(first['diagnosticPath']).read_bytes()
            second,_=self.report(ValueError('Segunda falha'),request)
            self.assertNotEqual(first['diagnosticPath'],second['diagnosticPath'])
            self.assertEqual(Path(first['diagnosticPath']).read_bytes(),previous)

    def test_cli_reports_invalid_request_and_exits_with_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            request=Path(directory)/'request.json';request.write_text('{invalid',encoding='utf8')
            run=subprocess.run([sys.executable,'-B',str(ROOT/'engine/worker.py'),str(request)],
                capture_output=True,text=True,encoding='utf8',timeout=30)
            self.assertEqual(run.returncode,1)
            message=json.loads(run.stdout)
            self.assertEqual(message['kind'],'error')
            self.assertTrue(Path(message['diagnosticPath']).is_file())
            self.assertIn('JSONDecodeError',run.stderr)


if __name__=='__main__':unittest.main()
