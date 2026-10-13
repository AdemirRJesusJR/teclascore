"""Worker receipts survive process loss without accepting outputs for another recipe."""
from pathlib import Path
from unittest.mock import patch
import hashlib,json,sys,tempfile,unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'engine'))
import worker

class QueueReceiptTests(unittest.TestCase):
    def fixture(self,folder):
        workspace=Path(folder).resolve();output=workspace/'output';output.mkdir()
        score=output/'score.mscz';score.write_bytes(b'score')
        review=output/'review';review.mkdir()
        for name,content in [('pagina-1.png',b'page1'),('pagina-2.png',b'page2'),('previa-geral.png',b'contact'),
                             ('validacao.json',b'{}'),('harmonia_fonte.json',b'{"events":[]}'),('reaberto.musicxml',b'<score/>')]:
            (review/name).write_bytes(content)
        req=dict(queueReceipt=str(workspace/'queue-receipt.json'),queueJobId='job-123',queueRecipeSha256='a'*64)
        result=dict(scorePath=str(score),previewPath=str(review/'pagina-1.png'),reviewDirectory=str(review),
                    pages=2,reviewedProfile=False,message='Salva',transposeSemitones=0,melodyMerge={'mode':'priority'})
        return workspace,req,result

    def test_complete_result_and_every_generated_file_are_verified(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,req,result=self.fixture(folder)
            worker.write_queue_receipt(req,workspace,result,'b'*64)
            receipt=json.loads(Path(req['queueReceipt']).read_text(encoding='utf8'))
            self.assertEqual(receipt['schemaVersion'],1)
            self.assertEqual(receipt['jobId'],req['queueJobId']);self.assertEqual(receipt['recipeSha256'],'a'*64)
            self.assertEqual(receipt['sourceSha256'],'b'*64);self.assertEqual(receipt['result'],result)
            self.assertEqual(len(receipt['artifacts']),7)
            self.assertEqual({r['kind'] for r in receipt['artifacts']},{'score','preview','review'})
            for artifact in receipt['artifacts']:
                self.assertEqual(artifact['sha256'],hashlib.sha256(Path(artifact['path']).read_bytes()).hexdigest())
            self.assertFalse(list(workspace.glob('*.tmp')))

    def test_legacy_generation_does_not_write_receipt(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,_,result=self.fixture(folder)
            worker.write_queue_receipt({},workspace,result,'b'*64)
            self.assertEqual(list(workspace.glob('*.json')),[])

    def test_invalid_paths_and_recipe_identity_fail_before_generation(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,req,_=self.fixture(folder)
            bad=[dict(req,queueReceipt=str(workspace.parent/'escape.json')),dict(req,queueReceipt='relative.json'),
                 dict(req,queueReceipt=str(workspace/'sub'/'..'/'..'/'escape.json')),dict(req,queueReceipt=str(workspace)),
                 dict(req,queueReceipt=str(workspace/'receipt.txt')),dict(req,queueJobId=''),dict(req,queueRecipeSha256='wrong')]
            for request in bad:
                with self.assertRaises(ValueError):worker.queue_receipt_path(request,workspace)

    def test_existing_receipt_for_different_recipe_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,req,result=self.fixture(folder)
            worker.write_queue_receipt(req,workspace,result,'b'*64);path=Path(req['queueReceipt']);before=path.read_bytes()
            with self.assertRaises(ValueError):worker.write_queue_receipt(dict(req,queueRecipeSha256='c'*64),workspace,result,'b'*64)
            with self.assertRaises(ValueError):worker.write_queue_receipt(dict(req,queueJobId='another'),workspace,result,'b'*64)
            self.assertEqual(path.read_bytes(),before)

    def test_replace_failure_keeps_previous_receipt_and_cleans_temporary(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,req,result=self.fixture(folder)
            worker.write_queue_receipt(req,workspace,result,'b'*64);path=Path(req['queueReceipt']);before=path.read_bytes()
            with patch.object(worker.os,'replace',side_effect=OSError('interrupted')):
                with self.assertRaises(OSError):worker.write_queue_receipt(req,workspace,dict(result,message='new'),'b'*64)
            self.assertEqual(path.read_bytes(),before);self.assertFalse(list(workspace.glob('*.tmp')))

    def test_hash_failure_cannot_publish_partial_receipt(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,req,result=self.fixture(folder)
            with patch.object(worker.hashlib,'file_digest',side_effect=OSError('locked')):
                with self.assertRaises(OSError):worker.write_queue_receipt(req,workspace,result,'b'*64)
            self.assertFalse(Path(req['queueReceipt']).exists());self.assertFalse(list(workspace.glob('*.tmp')))

    def test_unrelated_existing_document_is_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            workspace,req,result=self.fixture(folder);path=Path(req['queueReceipt']);path.write_text('{"user":"data"}',encoding='utf8')
            before=path.read_bytes()
            with self.assertRaises(ValueError):worker.write_queue_receipt(req,workspace,result,'b'*64)
            self.assertEqual(path.read_bytes(),before)

if __name__=='__main__':unittest.main(verbosity=2)
