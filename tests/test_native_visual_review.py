"""Rendered visual findings use real SVG bounds and conservative native pairing."""
from pathlib import Path
import hashlib
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'engine'))
import native_visual_review as review


class NativeVisualReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def score(self, pairs=0, second_voice=False, page_break=False):
        pair = '<Harmony><root>14</root></Harmony><Image><path>keyboard.svg</path></Image>'
        voice = '<voice>' + pair*pairs + '<Chord><durationType>whole</durationType><Note><pitch>60</pitch><tpc>14</tpc></Note></Chord></voice>'
        second = '<voice><Rest><durationType>whole</durationType></Rest></voice>' if second_voice else ''
        page = '<LayoutBreak><subtype>page</subtype></LayoutBreak>' if page_break else ''
        xml = f'<museScore version="4.70"><Score><Staff id="1"><Measure>{voice}{second}{page}</Measure></Staff></Score></museScore>'
        path = self.directory / 'source.mscz'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('score.mscx', xml)
        return path

    def svg(self, contents='', name='page-1.svg'):
        staff = ''.join(f'<polyline class="StaffLines" points="100,{y} 900,{y}"/>' for y in range(200, 241, 10))
        path = self.directory / name
        path.write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1400">{staff}{contents}</svg>', encoding='utf-8')
        return path

    @staticmethod
    def box(kind, x, y, width, height, fill='black'):
        return f'<path class="{kind}" fill="{fill}" d="M{x} {y} L{x+width} {y} L{x+width} {y+height} L{x} {y+height} Z"/>'

    def keyboard(self):
        return ''.join(self.box('Image', 201+20*i, 101, 20, 60, 'white') for i in range(14))

    def test_paired_collision_is_localized_but_does_not_modify_source(self):
        source = self.score(1)
        original = hashlib.sha256(source.read_bytes()).digest()
        page = self.svg(self.box('Harmony', 250, 120, 20, 15)+self.keyboard())
        result = review.review(source, [page])
        finding = next(x for x in result['issues'] if x['kind'] == 'chord_diagram_collision')
        self.assertEqual((finding['page'], finding['staff'], finding['measure']), (1, 1, 1))
        self.assertTrue(finding['canAutoFix'])
        self.assertEqual(result['hints'][0]['kind'], 'imageOffset')
        self.assertEqual(hashlib.sha256(source.read_bytes()).digest(), original)

    def test_multiple_voices_do_not_receive_guessed_image_associations(self):
        result = review.review(self.score(1, second_voice=True), [self.svg(self.box('Harmony',250,120,20,15)+self.keyboard())])
        self.assertFalse(result['coverage']['mappedPairs'])
        self.assertTrue(result['issues'])
        self.assertTrue(all(x['measure'] is None and not x['canAutoFix'] for x in result['issues']))
        self.assertFalse(result['hints'])

    def test_count_mismatch_blocks_automatic_pairing(self):
        result = review.review(self.score(2), [self.svg(self.box('Harmony',250,120,20,15)+self.keyboard())])
        self.assertFalse(result['coverage']['mappedPairs'])
        self.assertFalse(result['hints'])

    def test_unknown_image_is_explicitly_outside_template_coverage(self):
        result = review.review(self.score(1), [self.svg('<image x="200" y="100" width="100" height="80"/>')])
        self.assertEqual(result['coverage']['recognizedDiagrams'], 0)
        self.assertFalse(result['coverage']['mappedPairs'])
        self.assertTrue(any('não são diagramas' in text for text in result['limitations']))

    def test_transformed_text_cut_is_detected_and_offers_autoplace(self):
        page = self.svg('<g transform="translate(-170,0)">'+self.box('StaffText',150,100,40,15)+'</g>')
        result = review.review(self.score(), [page])
        self.assertTrue(any(x['kind'] == 'page_clip' for x in result['issues']))
        self.assertTrue(any(x['kind'] == 'autoplaceTexts' for x in result['hints']))

    def test_header_cut_is_visible_without_claiming_a_safe_header_rewrite(self):
        result = review.review(self.score(), [self.svg(self.box('Text',-20,30,200,30))])
        self.assertTrue(any(x['kind'] == 'page_clip' and not x['canAutoFix'] for x in result['issues']))
        self.assertFalse(result['hints'])

    def test_clean_text_and_chord_do_not_get_a_collision(self):
        page = self.svg(self.box('StaffText',150,50,130,15)+self.box('Harmony',180,120,15,15)+self.keyboard())
        result = review.review(self.score(1), [page])
        self.assertFalse(result['issues'])
        self.assertFalse(result['hints'])

    def test_sparse_interior_page_is_advice_not_last_page_error(self):
        pages = [self.svg(name=f'page-{i}.svg') for i in range(1,4)]
        result = review.review(self.score(page_break=True), pages)
        self.assertEqual([(x['page'],x['kind'],x['severity']) for x in result['issues']], [(2,'sparse_page','suggestion')])
        self.assertEqual(result['hints'][0]['kind'], 'pageBreaksToLines')


if __name__ == '__main__':
    unittest.main()
