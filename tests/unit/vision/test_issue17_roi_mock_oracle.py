"""Issue #17 ROI/MOCK deterministic oracle. NOT a production transform contract."""
import hashlib
import unittest

def rotated_pixel(point, width, height, degrees):
    x, y = point
    if not (width > 0 and height > 0 and 0 <= x < width and 0 <= y < height):
        raise ValueError('pixel outside source')
    if degrees == 0:
        return (x, y)
    if degrees == 90:
        return (height - 1 - y, x)
    if degrees == 180:
        return (width - 1 - x, height - 1 - y)
    if degrees == 270:
        return (y, width - 1 - x)
    raise ValueError('unsupported rotation')

def simulated_inference(raw_bytes, seed, injected_error=False):
    if injected_error:
        return {'origin': 'SIMULATED', 'processing': 'ERROR', 'quality': 'UNDECIDED'}
    if not raw_bytes:
        return {'origin': 'SIMULATED', 'processing': 'MISSING', 'quality': 'UNDECIDED'}
    pixel_sha = hashlib.sha256(seed.to_bytes(8,'big') + raw_bytes).hexdigest()
    return {'origin': 'SIMULATED', 'processing': 'EXECUTED',
            'quality': 'NG' if int(pixel_sha[:2],16) % 2 else 'OK',
            'fixture_sha': pixel_sha}

def display_roi_view(defects, x, y, w, h):
    if w <= 0 or h <= 0:
        raise ValueError('ROI must be positive')
    shown = tuple(p for p in defects if x <= p[0] < x+w and y <= p[1] < y+h)
    return {'quality': 'NG' if defects else 'UNDECIDED',
            'defect_count': len(defects), 'visible': shown}

class RoiMockOracle(unittest.TestCase):
    def test_rotation_8200x15000_metadata_only(self):
        self.assertEqual(rotated_pixel((8199,14999),8200,15000,90),(0,8199))

    def test_display_crop_never_hides_ng(self):
        result = display_roi_view(((5,5),(99,99)),0,0,10,10)
        self.assertEqual(result['visible'],((5,5),))
        self.assertEqual(result['defect_count'],2)
        self.assertEqual(result['quality'],'NG')

    def test_mock_deterministic_but_not_accuracy(self):
        self.assertEqual(simulated_inference(b'abc',4),simulated_inference(b'abc',4))
        self.assertEqual(simulated_inference(b'abc',4)['origin'],'SIMULATED')
        self.assertEqual(simulated_inference(b'abc',4,True)['quality'],'UNDECIDED')
        self.assertEqual(simulated_inference(b'',4)['processing'],'MISSING')

    def test_invalid_roi_and_transform_fail_closed(self):
        with self.assertRaises(ValueError):
            display_roi_view((),0,0,0,10)
        with self.assertRaises(ValueError):
            rotated_pixel((-1,0),10,10,0)
        with self.assertRaises(ValueError):
            rotated_pixel((0,0),10,10,45)

if __name__ == '__main__':
    unittest.main()
