"""
A sample Python node library for Sirius Studio Plus.

Needs numpy and opencv-python in the Python that runs it. Copy this folder
into the application's plugins folder (or add dark_spots.py in
Tools > Custom Nodes) and the two nodes appear in the toolbox.

To try it outside the application:

    python -m pytest test_dark_spots.py

with the application's python folder on PYTHONPATH, for sirius_nodes.
"""

import cv2
import numpy as np

from sirius_nodes import node, Input, Output, Setting, Box


@node("Dark Spots", key="sample.dark-spots", group="Blob",
      description="Finds spots darker than a level and reports each as a shape with its area.")
class DarkSpots:
    image = Input.image("Image")

    spots = Output.shapes("Spots", description="One turned rectangle per spot, labelled with its area.")
    count = Output.integer("Count")
    mask = Output.image("Mask")

    level = Setting.integer("Level", 60, min=0, max=255,
                            description="Pixels darker than this are part of a spot.")
    min_area = Setting.integer("Min Area", 20, min=0, max=100000, connectable=True,
                               description="Smaller spots are ignored, in pixels.")

    def run(self, image):
        gray = image if image.ndim == 2 else cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        if gray.dtype != np.uint8:
            gray = cv2.normalize(gray, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)

        mask = np.where(gray < self.level, 255, 0).astype(np.uint8)
        found, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)

        spots = []
        for index in range(1, found):
            area = int(stats[index, cv2.CC_STAT_AREA])
            if area < self.min_area:
                continue

            points = np.column_stack(np.nonzero(labels == index)[::-1]).astype(np.float32)
            (cx, cy), (width, height), angle = cv2.minAreaRect(points)
            spots.append(Box(cx, cy, width, height, angle, label="%d px" % area, area=area))

        if found <= 1:
            self.warn("Nothing is darker than %d." % self.level)

        return {"spots": spots, "count": len(spots), "mask": mask}


@node("Sharpness", key="sample.sharpness", group="Analysis",
      description="Variance of the Laplacian - higher is sharper. A quick focus check.")
class Sharpness:
    image = Input.image("Image")

    sharpness = Output.number("Sharpness")
    sharp = Output.bool("Sharp")

    minimum = Setting.number("Minimum", 100.0, min=0, max=10000, logarithmic=True,
                             description="Below this the image counts as out of focus.")

    def run(self, image):
        gray = image if image.ndim == 2 else cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        value = float(cv2.Laplacian(gray, cv2.CV_64F).var())
        return {"sharpness": value, "sharp": value >= self.minimum}
