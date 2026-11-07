import colorsys, os, time
import numpy as np
from loop import post, HERE
from rois import frame, SCREENS
from bright import PUMP, lin
cx, cy, r = SCREENS["7qSo2XMqKB8u"]
W = np.array([0.2126, 0.7152, 0.0722])
def setb(b):
    post("/devices/lighting-devices/brightness", {"id": PUMP, "brightness": int(b)}); time.sleep(2)
    return frame(os.path.join(HERE, "latest.jpg"))
def hue_of(lrgb):
    s = np.where(lrgb <= 0.0031308, lrgb * 12.92, 1.055 * np.power(np.maximum(lrgb, 0), 1 / 2.4) - 0.055)
    return colorsys.rgb_to_hsv(*np.clip(s, 0, 1))[0] * 360
img = setb(100)
yy, xx = np.mgrid[0:img.shape[0], 0:img.shape[1]]
d = np.hypot(yy - cy, xx - cx)
ann = (d >= 1.15 * r) & (d <= 1.45 * r)
L = lin(img) @ W
band = ann & (L >= np.percentile(L[ann], 80))
peak = img.max(axis=2); sat = (peak - img.min(axis=2)) / np.maximum(peak, 1)
glass = (d <= 0.85 * r) & (sat > 0.35) & (peak > 12)
def metrics(img):
    l = lin(img)
    return float(np.median(l[band] @ W)), float(np.median(l[glass] @ W)), l[band].mean(axis=0), l[glass].mean(axis=0)
lo, hi = 1, 100
seen = {}
while hi - lo > 2:
    mid = (lo + hi) // 2
    rb, gl, rc, gc = metrics(setb(mid)); seen[mid] = (rb, gl, rc, gc)
    print(f"brightness {mid}: ring {rb:.4f} glass {gl:.4f} | hue ring {hue_of(rc):.0f} glass {hue_of(gc):.0f}", flush=True)
    if rb > gl: hi = mid
    else: lo = mid
best = min(seen, key=lambda b: abs(np.log(seen[b][0] / seen[b][1])))
setb(best)
print(f"[OK] pump ring brightness {best}: ring {seen[best][0]:.4f} vs glass {seen[best][1]:.4f}", flush=True)
