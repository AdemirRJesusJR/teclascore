from pathlib import Path
import importlib.util
import json
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('debug_kit',Path(__file__).resolve().parents[1]/'tools/build_debug.py')
kit=importlib.util.module_from_spec(spec);spec.loader.exec_module(kit)

class DebugKitTests(unittest.TestCase):
    def test_complete_kit_requires_verified_payload_and_prerequisite(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);app=root/'Aplicativo';(app/'runtime').mkdir(parents=True)
            (app/'runtime/python.exe').write_bytes(b'isolated test fixture')
            (app/'installation-manifest.json').write_text(json.dumps({'product':'TeclaScore','version':'1.2.3','files':{'runtime/python.exe':kit.digest(app/'runtime/python.exe')}}))
            for name in ['LEIA-ME.txt','VALIDACAO.txt','ROTEIRO-DE-TESTES.txt']: (app/name).write_text('test')
            project=root/'src/Teclas.App/Teclas.App.csproj';project.parent.mkdir(parents=True);project.write_text('<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>')
            installer=root/'qa/offline-dependencies/official.msi';installer.parent.mkdir(parents=True);installer.write_bytes(b'test MSI')
            docs=root/'tools/debug-kit';docs.mkdir(parents=True);(docs/'LEIA-ME-INNO.txt').write_text('guide')
            (root/'tools/offline-dependencies.json').write_text(json.dumps({'musescore':{'file':'official.msi','sha256':kit.digest(installer)}}))
            result=kit.build(root)
            self.assertIn('app/runtime/python.exe',result['files'])
            self.assertEqual((root/'debug/prerequisitos/official.msi').read_bytes(),b'test MSI')
            (app/'runtime/python.exe').write_bytes(b'changed')
            with self.assertRaises(ValueError): kit.build(root)
    def test_updates_only_owned_files_and_removes_obsolete_entries(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); source=root/'source';source.write_text('v1');dest=root/'debug'
            kit.sync_owned(dest,{'app/a.dll':source,'app/old.dll':source},{'version':'1'})
            custom=dest/'meu-instalador.iss';custom.write_text('preserve me')
            source.write_text('v2')
            result=kit.sync_owned(dest,{'app/a.dll':source},{'version':'2'})
            self.assertEqual((dest/'app/a.dll').read_text(),'v2')
            self.assertFalse((dest/'app/old.dll').exists())
            self.assertEqual(custom.read_text(),'preserve me')
            self.assertEqual(result['version'],'2')
            self.assertEqual(result['files']['app/a.dll'],kit.digest(source))
    def test_rejects_escape_and_unmanaged_overwrite(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); source=root/'source';source.write_text('new');dest=root/'debug';dest.mkdir()
            (dest/'mine').write_text('personal')
            for name in ['../escape','/absolute','C:/absolute','app/../../escape']:
                with self.assertRaises(ValueError): kit.sync_owned(dest,{name:source},{})
            with self.assertRaises(ValueError): kit.sync_owned(dest,{'mine':source},{})
            self.assertEqual((dest/'mine').read_text(),'personal')
    def test_manifest_cannot_delete_outside_destination(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); outside=root/'preserve';outside.write_text('safe');dest=root/'debug';dest.mkdir()
            (dest/'kit-manifest.json').write_text(json.dumps({'files':{'../preserve':'fake'}}))
            with self.assertRaises(ValueError): kit.sync_owned(dest,{}, {})
            self.assertEqual(outside.read_text(),'safe')

if __name__=='__main__': unittest.main()
