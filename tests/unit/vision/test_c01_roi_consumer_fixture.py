"""Agent03 C-09 ROI/model consumer oracle; only SIMULATED fixtures, not frozen DTO.

No OpenCV, calibrated frame transforms, model accuracy, or production quality proof.
"""
import hashlib
import unittest


def rect(x,y,w,h,frame_width,frame_height):
    values=(x,y,w,h,frame_width,frame_height)
    if any(type(v) is not int for v in values):
        raise ValueError('only integer pixel fixtures supported')
    if x<0 or y<0 or w<=0 or h<=0 or frame_width<=0 or frame_height<=0:
        raise ValueError('bad ROI/shape')
    if x+w>frame_width or y+h>frame_height:
        raise ValueError('ROI outside coordinate space')
    return (x,y,x+w,y+h)


def within(point, bounds):
    x,y=point
    x0,y0,x1,y1=bounds
    return x0<=x<x1 and y0<=y<y1


def detect_fixture(defects,sensor_shape,detection_roi):
    region=rect(*detection_roi,*sensor_shape)
    inside=tuple(p for p in defects if within(p,region))
    return {'origin':'SIMULATED','quality':'NG' if inside else 'UNDECIDED',
            'detected':inside, 'count':len(inside), 'space':'SENSOR_PIXEL'}


def display_fixture(detection,sensor_shape,display_roi):
    region=rect(*display_roi,*sensor_shape)
    shown=tuple(p for p in detection['detected'] if within(p,region))
    # Read-only projection. The quality is deliberately not recomputed from shown.
    return {'quality':detection['quality'], 'total':detection['count'],
            'shown':shown,'origin':detection['origin']}


def rotate(point,width,height,angle):
    x,y=point
    if not (0<=x<width and 0<=y<height):
        raise ValueError('outside original')
    if angle==0:return x,y
    if angle==90:return height-1-y,x
    if angle==180:return width-1-x,height-1-y
    if angle==270:return y,width-1-x
    raise ValueError('invalid rotation')


def mock(seed,data,forced_error=False):
    if forced_error or data is None:
        return {'origin':'SIMULATED','processing':'ERROR','quality':'UNDECIDED'}
    if len(data)==0:
        return {'origin':'SIMULATED','processing':'MISSING','quality':'UNDECIDED'}
    digest=hashlib.sha256(seed.to_bytes(4,'big')+data).hexdigest()
    return {'origin':'SIMULATED','processing':'SUCCEEDED','quality':'UNDECIDED','fixture_sha':digest}


class C01VisionConsumerFixture(unittest.TestCase):
    def test_ng_outside_display_still_ng(self):
        defects=((95,95),(5,5))
        detection=detect_fixture(defects,(100,100),(0,0,100,100))
        result=display_fixture(detection,(100,100),(0,0,10,10))
        self.assertEqual(result['shown'],((5,5),))
        self.assertEqual(result['total'],2)
        self.assertEqual(result['quality'],'NG')

    def test_detection_roi_changes_detection_not_screenshot(self):
        defects=((95,95),(5,5))
        detection=detect_fixture(defects,(100,100),(0,0,10,10))
        self.assertEqual(detection['detected'],((5,5),))
        shown=display_fixture(detection,(100,100),(50,50,50,50))
        self.assertEqual(shown['shown'],())
        self.assertEqual(shown['quality'],'NG')
        self.assertEqual(shown['total'],1)

    def test_defect_outside_detection_is_not_automatic_ok(self):
        detection=detect_fixture(((95,95),),(100,100),(0,0,10,10))
        self.assertEqual(detection['quality'],'UNDECIDED')
        self.assertEqual(detection['count'],0)

    def test_half_open_coordinate_and_rect_validation(self):
        bounds=rect(5,5,10,10,25,25)
        self.assertTrue(within((5,5),bounds))
        self.assertTrue(within((14,14),bounds))
        self.assertFalse(within((15,15),bounds))
        for bad in ((0,0,0,5,20,20),(-1,0,2,2,20,20),(19,19,3,3,20,20),
                    (0,0,True,2,20,20),(0,0,5,5,0,20)):
            with self.subTest(bad=bad),self.assertRaises(ValueError):
                rect(*bad)

    def test_rotation_round_trip_fixtures_no_full_frame_allocation(self):
        width,height=8200,15000
        for point in ((0,0),(width-1,height-1),(1234,5678)):
            new=rotate(point,width,height,90)
            restored=rotate(new,height,width,270)
            self.assertEqual(restored,point)
        self.assertEqual(rotate((0,0),width,height,180),(8199,14999))
        with self.assertRaises(ValueError):
            rotate((2,3),width,height,45)

    def test_error_and_missing_never_become_ok(self):
        self.assertEqual(mock(7,None)['quality'],'UNDECIDED')
        self.assertEqual(mock(7,b'')['processing'],'MISSING')
        self.assertEqual(mock(7,b'abc',True)['processing'],'ERROR')
        result=mock(7,b'abc')
        self.assertEqual(result,mock(7,b'abc'))
        self.assertEqual(result['origin'],'SIMULATED')
        self.assertNotEqual(result['quality'],'OK')

    def test_processing_sql_states_require_explicit_adapter(self):
        draft={'PENDING','RUNNING','SUCCEEDED','ERROR','CANCELLED','TIMEOUT'}
        reference_sql={'EXECUTED','ERROR','MISSING','SKIPPED_VERIFIED','CANCELED'}
        self.assertNotEqual(draft,reference_sql)
        self.assertNotIn('TIMEOUT',reference_sql)
        self.assertNotIn('SUCCEEDED',reference_sql)
        self.assertNotIn('SKIPPED_BY_VERIFIED_CONDITION',reference_sql)

if __name__=='__main__':
    unittest.main()
