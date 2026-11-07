import colorsys
import numpy as np
from PIL import Image
SCREENS = {"7qSo2XMqKB8u": (988, 442, 72), "oqCehmgjROQe": (1585, 256, 48), "h_WlElG90KeR": (1555, 531, 46)}
LED_BOXES = {"pump": ("ring", 988, 442, 72), "chain": ("box", 1625, 330, 1720, 500)}

def frame(path):
    with Image.open(path) as im:
        return np.asarray(im.convert("RGB"), dtype=np.float32)

def hue(v):
    r, g, b = (np.asarray(v, dtype=float) / 255.0).tolist()
    return colorsys.rgb_to_hsv(r, g, b)[0] * 360

def _disk(img, cx, cy, r0, r1):
    yy, xx = np.mgrid[0:img.shape[0], 0:img.shape[1]]
    d = np.hypot(yy - cy, xx - cx)
    return img[(d >= r0) & (d <= r1)]

def _coloured(px, lo=20, hi=246):
    peak = px.max(axis=1); low = px.min(axis=1)
    sat = (peak - low) / np.maximum(peak, 1)
    keep = (peak > lo) & (peak < hi) & (sat > 0.35)
    return px[keep]

def screen_colour(img, panel):
    cx, cy, r = SCREENS[panel]
    px = _coloured(_disk(img, cx, cy, 0, 0.85 * r), lo=12)
    return (np.median(px, axis=0) if len(px) > 30 else None), len(px)

def led_colour(img, which):
    spec = LED_BOXES[which]
    if spec[0] == "ring":
        _, cx, cy, r = spec
        px = _disk(img, cx, cy, 1.15 * r, 1.9 * r)
    else:
        _, x0, y0, x1, y1 = spec
        px = img[y0:y1, x0:x1].reshape(-1, 3)
    px = _coloured(px, lo=40)
    if len(px) < 30: return None, len(px)
    top = px[px.max(axis=1) >= np.percentile(px.max(axis=1), 70)]
    return np.median(top, axis=0), len(px)
