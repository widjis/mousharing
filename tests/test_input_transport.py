import queue
import unittest
from input_transport import take_input
from screens import ReturnGate, Screen


class TransportTests(unittest.TestCase):
    def test_batches_moves_without_reordering_click_or_key(self):
        events=queue.Queue()
        sequence=[{'type':'move','dx':3,'dy':2,'edge_switch':True},
                  {'type':'move','dx':5,'dy':-1,'edge_switch':True},
                  {'type':'button','name':'left','down':True},
                  {'type':'move','dx':2,'dy':0,'edge_switch':True},
                  {'type':'key','name':'a','down':True}]
        for event in sequence:
            events.put_nowait(event)
        motion,pending=take_input(events)
        self.assertEqual((motion['dx'],motion['dy']),(8,1))
        click,pending=take_input(events,pending)
        self.assertEqual(click,sequence[2])
        motion,pending=take_input(events,pending)
        self.assertEqual(motion,sequence[3])
        key,pending=take_input(events,pending)
        self.assertEqual(key,sequence[4])
        self.assertIsNone(pending)
        self.assertEqual(sequence[0]['dx'],3)

    def test_small_edge_jitter_does_not_switch_back(self):
        gate=ReturnGate()
        screen=Screen(0,0,1920,1200)
        for _ in range(4):
            self.assertFalse(gate.should_leave(screen,'left',0,600,-5,0))
        self.assertTrue(gate.should_leave(screen,'left',0,600,-5,0))
        gate.reset()
        self.assertFalse(gate.should_leave(screen,'left',0,600,-100,0,blocked=True))
        self.assertEqual(gate.pressure,0)


if __name__ == '__main__':
    unittest.main()
