import json, os, sys, time
import numpy as np
from loop import post, HERE
from rois import frame, SCREENS
PUMP = "lianli-wireless:BB951BE566E1:pump"
cx, cy, r = SCREENS["7qSo2XMqKB8u"]
def prefs():
    s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
    return s["devices"]["lightingDevicePrefs"].get(PUMP) if "devices" in s else None
def lin(img):
    c = np.clip(img / 255.0, 0, 1)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
def measure():
    img = frame(os.path.join(HERE, "latest.jpg"))
    yy, xx = np.mgrid[0:img.shape[0], 0:img.shape[1]]
    d = np.hypot(yy - cy, xx - cx)
    l = lin(img)
    ring = l[(d >= 1.15 * r) & (d <= 1.45 * r)].mean(axis=0)
    peak = img.max(axis=2); sat = (peak - img.min(axis=2)) / np.maximum(peak, 1)
    glass = l[(d <= 0.85 * r) & (sat > 0.35) & (peak > 12)].mean(axis=0)
    return ring, glass
if __name__ == "__main__":
    print("pref before", prefs())
    for b in [int(x) for x in sys.argv[1:]]:
        post("/devices/lighting-devices/brightness", {"id": PUMP, "brightness": b})
        time.sleep(2)
        ring, glass = measure()
        print(f"brightness {b}: ring lum {ring @ [0.2126, 0.7152, 0.0722]:.4f} rgb {np.round(ring, 3).tolist()} | glass lum {glass @ [0.2126, 0.7152, 0.0722]:.4f} rgb {np.round(glass, 3).tolist()}", flush=True)
