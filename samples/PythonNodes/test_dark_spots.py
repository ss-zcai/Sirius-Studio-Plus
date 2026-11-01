"""Tests for the sample library, run without the application: python -m pytest."""

import numpy as np

from sirius_nodes import NodeRunner
from dark_spots import DarkSpots, Sharpness


def test_finds_one_spot_and_measures_it():
    image = np.full((50, 80), 200, np.uint8)
    image[10:20, 30:45] = 20

    out = NodeRunner(DarkSpots).run({"level": 60, "min_area": 5}, image=image)

    assert out["count"] == 1
    spot = out["spots"][0]
    assert spot.metrics["area"] == 150
    assert abs(spot.cx - 37) < 1 and abs(spot.cy - 14.5) < 1


def test_small_spots_are_ignored_and_an_empty_mask_warns():
    image = np.full((20, 20), 200, np.uint8)
    image[5, 5] = 0

    runner = NodeRunner(DarkSpots)
    out = runner.run({"min_area": 5}, image=image)

    assert out["count"] == 0
    assert runner.warnings == []

    out = runner.run({"level": 0}, image=image)
    assert runner.warnings == ["Nothing is darker than 0."]


def test_a_flat_image_is_not_sharp():
    out = NodeRunner(Sharpness).run({}, image=np.full((30, 30, 3), 128, np.uint8))

    assert out["sharpness"] == 0
    assert out["sharp"] is False
