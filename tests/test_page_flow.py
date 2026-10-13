"""Automatic pagination repairs only interior orphan pages in generated scores."""
from pathlib import Path
from unittest.mock import patch
import os, shutil, sys, tempfile, unittest, zipfile
import xml.etree.ElementTree as E

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'engine'))
os.environ.setdefault('PARTITURAS_JOB', str(Path(tempfile.gettempdir()) / 'TeclaScorePageFlowTests'))
import batch_score as engine


class PageFlowTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.out = Path(self.temporary.name)
        self.final = self.out / 'score.mscz'
        self.xml = '''<museScore version="4.70"><Score><Staff id="1">
            <Measure><voice><Harmony><root>14</root><name>m</name></Harmony>
                <Image><path>keyboard.svg</path><size w="14" h="4.2"/></Image>
                <Chord><durationType>quarter</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord>
                </voice><LayoutBreak><subtype>page</subtype></LayoutBreak></Measure>
            <Measure><voice><Rest><durationType>half</durationType></Rest></voice>
                <LayoutBreak><subtype>line</subtype></LayoutBreak></Measure>
            <Measure><voice><Chord><durationType>half</durationType><Note><pitch>64</pitch><tpc>18</tpc></Note></Chord></voice>
                <LayoutBreak><subtype>page</subtype></LayoutBreak></Measure>
            <Measure><voice><Rest><durationType>whole</durationType></Rest></voice>
                <LayoutBreak><subtype>section</subtype><pause>3</pause></LayoutBreak></Measure>
            </Staff></Score></museScore>'''
        self.write_score(self.xml)
        for page in range(1, 4):
            (self.out / f'qa-{page}.svg').write_text('old SVG')
            (self.out / f'pagina-{page}.png').write_bytes(b'old PNG')
        self.report = {}

    def write_score(self, xml):
        with zipfile.ZipFile(self.final, 'w') as archive:
            archive.writestr('score.mscx', xml)
            archive.writestr('Pictures/keyboard.svg', 'original keyboard image')
            archive.writestr('META-INF/container.xml', 'original container manifest')

    def repair(self, row_counts, renderer=None):
        def geometry(path):
            index = int(path.stem.rsplit('-', 1)[1]) - 1
            return {'rows': list(range(row_counts[index]))}
        with patch.object(engine, 'analyze_svg', side_effect=geometry), \
                patch.object(engine, 'call_musescore', side_effect=renderer) as mock:
            engine.repair_orphan_pages(self.final, self.report, self.out)
        return mock

    def test_sparse_first_or_last_page_does_not_trigger_repagination(self):
        before = self.final.read_bytes()
        for row_counts in ([1, 7, 1], [8, 7, 1], [1, 7, 8], [7, 7, 7]):
            with self.subTest(row_counts=row_counts):
                renderer = self.repair(row_counts)
                renderer.assert_not_called()
                self.assertEqual(self.final.read_bytes(), before)
                self.assertNotIn('page_flow_adjustment', self.report)
                self.assertEqual(len(list(self.out.glob('qa-*.svg'))), 3)

    def test_orphan_without_hard_page_breaks_is_left_unchanged(self):
        self.write_score(self.xml.replace('<subtype>page</subtype>', '<subtype>line</subtype>'))
        before = self.final.read_bytes()
        renderer = self.repair([8, 1, 7])
        renderer.assert_not_called()
        self.assertEqual(self.final.read_bytes(), before)
        self.assertNotIn('page_flow_adjustment', self.report)

    def test_orphan_releases_only_page_breaks_and_preserves_music_and_assets(self):
        with zipfile.ZipFile(self.final) as archive:
            originals = {name: archive.read(name) for name in archive.namelist()}

        def render(args, log, timeout=90):
            if args[0] == '-o':
                shutil.copyfile(args[2], args[1])
            else:
                # Stale pages must not survive when the renderer produces fewer pages.
                self.assertEqual(list(self.out.glob('qa-*.svg')), [])
                self.assertEqual(list(self.out.glob('pagina-*.png')), [])
                for page in range(1, 3):
                    (self.out / f'qa-{page}.svg').write_text('fresh SVG')
                    (self.out / f'pagina-{page}.png').write_bytes(b'fresh PNG')

        renderer = self.repair([8, 1, 7], render)
        self.assertEqual(renderer.call_count, 2)
        self.assertEqual(self.report['page_flow_adjustment'],
            {'pages_before': 3, 'page_breaks_released': 2})
        with zipfile.ZipFile(self.final) as archive:
            changed = {name: archive.read(name) for name in archive.namelist()}
        self.assertEqual(set(changed), set(originals))
        for name in ('Pictures/keyboard.svg', 'META-INF/container.xml'):
            self.assertEqual(changed[name], originals[name])
        expected = E.fromstring(originals['score.mscx'])
        for subtype in expected.findall('.//LayoutBreak/subtype'):
            if subtype.text == 'page':
                subtype.text = 'line'
        actual = E.fromstring(changed['score.mscx'])
        self.assertEqual(E.tostring(actual), E.tostring(expected))
        self.assertEqual([e.text for e in actual.findall('.//LayoutBreak/subtype')],
            ['line', 'line', 'line', 'section'])
        self.assertEqual(len(list(self.out.glob('qa-*.svg'))), 2)
        self.assertFalse((self.out / 'pagina-3.png').exists())


if __name__ == '__main__':
    unittest.main()
