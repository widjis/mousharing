import unittest
from screens import Screen, SIDES, OPPOSITE


class GeometryTests(unittest.TestCase):
    def test_edge_direction_and_entry_inset_for_all_layouts(self):
        screen=Screen(0,0,1920,1080)
        movements={'left':(-20,0),'right':(20,0),'top':(0,-20),'bottom':(0,20)}
        for side in SIDES:
            x,y=screen.point(side,0.5,inset=0)
            dx,dy=movements[side]
            self.assertTrue(screen.crossed(side,x,y,dx,dy))
            self.assertFalse(screen.crossed(side,x,y,-dx,-dy))
            self.assertFalse(screen.crossed(side,*screen.point(side,0.5),dx,dy))
            self.assertAlmostEqual(screen.ratio(side,x,y),0.5)

    def test_different_resolutions_preserve_fraction_in_both_directions(self):
        mac=Screen(-1440,-100,1440,900)
        windows=Screen(0,0,2560,1440)
        for side in SIDES:
            for ratio in (0,0.25,0.75,1):
                wx,wy=windows.point(OPPOSITE[side],ratio)
                back=windows.ratio(OPPOSITE[side],wx,wy)
                mx,my=mac.point(side,back)
                self.assertAlmostEqual(mac.ratio(side,mx,my),ratio)

    def test_invalid_geometry_and_positions_are_rejected(self):
        for width in (0,1,float('inf'),float('nan')):
            with self.assertRaises(ValueError):
                Screen(0,0,width,900)
        with self.assertRaises(ValueError):
            Screen(0,0,1440,900).point('left',float('nan'))


if __name__ == '__main__':
    unittest.main()
