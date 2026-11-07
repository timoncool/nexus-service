"""Pump only: the glass should keep the same share of the ring's light at every brightness of the user's hue."""
import json, os, sys, time
import numpy as np
from loop import post, HERE, log
from mcpc import tool
from rois import frame, SCREENS
PUMP_PIN = "lianli-wireless:BB951BE566E1:pump"
PANEL = "7qSo2XMqKB8u"
LEVELS = [1.0, 0.8, 0.6, 0.45, 0.36, 0.28, 0.2, 0.14]
BASE = (255, 0, 64)

def col(k): return "#" + "".join(f"{round(c * k):02x}" for c in BASE)
def lin(img):
    c = np.clip(img / 255.0, 0, 1)
    c = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    return c @ np.array([0.2126, 0.7152, 0.0722])
def avg_frames(n=3, gap=1.0):
    out = []
    for _ in range(n):
        out.append(frame(os.path.join(HERE, "latest.jpg"))); time.sleep(gap)
    return np.mean(out, axis=0)
def set_gamma(g):
    tool("set_panel_style", {"panel": PANEL, "config": {"slot0_glowGamma": None if g == 1 else round(g, 3)}})
def sweep(tag, masks):
    res = []
    for k in LEVELS:
        look = dict(orig); look["color"] = col(k)
        post("/devices/lighting-devices/pins", {"ids": [PUMP_PIN], "pin": look})
        time.sleep(4)
        img = avg_frames()
        if masks is None:
            masks = make_masks(img)
        l = lin(img)
        res.append((k, float(l[masks[0]].mean()), float(l[masks[1]].mean())))
        log(f"[..] {tag} {col(k)} ring {res[-1][1]:.4f} glass {res[-1][2]:.4f}")
    return res, masks
def make_masks(img):
    cx, cy, r = SCREENS[PANEL]
    yy, xx = np.mgrid[0:img.shape[0], 0:img.shape[1]]
    d = np.hypot(yy - cy, xx - cx)
    peak = img.max(axis=2); low = img.min(axis=2)
    sat = (peak - low) / np.maximum(peak, 1)
    ring = (d >= 1.15 * r) & (d <= 1.9 * r) & (peak > 30) & (peak < 235)
    glass = (d <= 0.85 * r) & (sat > 0.35) & (peak > 12) & (peak < 250)
    log(f"[..] masks: ring {int(ring.sum())} px, glass {int(glass.sum())} px")
    return ring, glass
def report(res):
    k, L, S = map(np.array, zip(*res))
    share = (S / S[0]) / (L / L[0])
    return share

s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
orig = dict(s["lighting"]["devicePins"][PUMP_PIN])
try:
    post(f"/panel/devices/{PANEL}", {"fpsCap": 7}); time.sleep(8)
    post(f"/panel/devices/{PANEL}", {"fpsCap": 8}); time.sleep(8)
    set_gamma(1); time.sleep(3)
    log("[..] pump screen reloaded, gamma off")
    before, masks = sweep("gamma 1", None)
    k, L, S = map(np.array, zip(*before))
    order = np.argsort(S)
    gammas = []
    for i in range(1, len(k)):
        want = S[0] * L[i] / L[0]
        if want <= S.min() or want >= S.max(): continue
        v = float(np.interp(want, S[order], k[order]))
        if 0 < v < 1 and k[i] < 1: gammas.append(np.log(k[i]) / np.log(v))
    if not gammas:
        log("[ERROR] glass does not cover the ring's range; no gamma fits"); sys.exit(1)
    g = float(np.clip(np.median(gammas), 1, 4))
    log(f"[..] per-level gamma {[round(x, 2) for x in gammas]}, chosen {g:.2f}")
    set_gamma(g); time.sleep(4)
    after, _ = sweep(f"gamma {g:.2f}", masks)
    sb, sa = report(before), report(after)
    log("[..] glass share of ring light vs full (1.00 = matched): " + ", ".join(f"{lv:.2f}: {b:.2f}->{a:.2f}" for lv, b, a in zip(LEVELS, sb, sa)))
    json.dump({"gamma": g, "before": before, "after": after}, open(os.path.join(HERE, "gamma_result.json"), "w"), indent=1)
    log(f"[OK] pump glowGamma {g:.2f}")
except SystemExit: raise
except Exception as ex:
    log(f"[ERROR] {ex}"); sys.exit(1)
finally:
    post("/devices/lighting-devices/pins", {"ids": [PUMP_PIN], "pin": orig})
    log(f"[..] pump pin restored to {orig['color']}")
