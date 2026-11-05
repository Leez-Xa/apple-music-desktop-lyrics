import copy
import unittest
from amlyrics.button_layout import choose_button_rect


def observed_layout():
    return {"transport_rect": [-1333, 86, 1893, 96], "controls": [
        {"automation_id": "TransportControl_PlayPauseStop", "rect": [-1093, 103, 60, 60]},
        {"automation_id": "RepeatButton", "rect": [-974, 103, 60, 60]},
        {"automation_id": "LCD", "rect": [-787, 94, 720, 79]},
        {"automation_id": "LyricsToggleButton", "rect": [115, 109, 66, 48]}]}

class ButtonLayoutTests(unittest.TestCase):
    def test_real_gap_on_negative_coordinate_monitor(self):
        rect=choose_button_rect(observed_layout(),144)
        self.assertEqual(rect,(-910,114,120,39))
    def test_new_interactive_control_in_gap_refuses_placement(self):
        layout=observed_layout()
        layout['controls'].append({'automation_id':'NewAppleControl','rect':[-900,103,30,60]})
        self.assertIsNone(choose_button_rect(layout,144))
    def test_narrow_window_does_not_cover_existing_controls(self):
        layout=observed_layout()
        layout['controls'][2]['rect'][0]=-850
        self.assertIsNone(choose_button_rect(layout,144))
    def test_invalid_unknown_or_duplicate_geometry_is_refused(self):
        for value in (None,{}, {'transport_rect':[0,0,float('inf'),90],'controls':[]}):
            self.assertIsNone(choose_button_rect(value,144))
        value=observed_layout()
        value['controls'].append(copy.deepcopy(value['controls'][0]))
        self.assertIsNone(choose_button_rect(value,144))
    def test_dpi_changes_require_enough_gap(self):
        self.assertIsNone(choose_button_rect(observed_layout(),192))
        self.assertIsNone(choose_button_rect(observed_layout(),True))

if __name__=='__main__': unittest.main()
