"""One LED colour at a time: hold it, move only its node of the screen hue table until the camera sees the same hue on both."""
import json, os, sys, time
import numpy as np
from loop import IDS, LOOP_PANELS, ALL_FANS, HERE, log, post, hue, wrap, lut_text, set_lut, screen_hue, targets, order, orig
DONE_DEG, MAX_ITERS = 2.5, 8

hist = json.load(open(os.path.join(HERE, "loop_history.json")))
node_of = {c: round(hue([int(c[i:i + 2], 16) for i in (1, 3, 5)])) % 360 for c in order}
tries = {c: [] for c in order}
for r in hist:
    outs = dict((int(float(p.split(":")[0])), float(p.split(":")[1])) for p in r["lut"].split(";"))
    for c, e in r["errors"].items():
        tries[c].append((outs[node_of[c]], e))
best = {c: min(tries[c], key=lambda t: abs(t[1])) for c in order}
lut = {node_of[c]: best[c][0] for c in order}

def unwrap_near(x, ref): return ref + wrap(x - ref)

def next_out(c):
    ref = node_of[c]
    pts = [(unwrap_near(o, ref), e) for o, e in tries[c]]
    above = [p for p in pts if p[1] > 0]
    below = [p for p in pts if p[1] < 0]
    if above and below:
        lo = max(above, key=lambda p: p[0]); hi = min(below, key=lambda p: p[0])
        if lo[0] < hi[0]:
            x = lo[0] + (hi[0] - lo[0]) * lo[1] / (lo[1] - hi[1])
            mid = (lo[0] + hi[0]) / 2
            return x if abs(x - mid) < (hi[0] - lo[0]) * 0.4 else mid
    o, e = min(pts, key=lambda p: abs(p[1]))
    return o + max(-15.0, min(15.0, 0.7 * e))

try:
    todo = [c for c in order if abs(best[c][1]) >= DONE_DEG]
    log(f"[..] start from the best node of every round; to refine: {todo}")
    for k, c in enumerate(todo, 1):
        look = dict(orig[IDS[0]]); look["color"] = c
        post("/devices/lighting-devices/pins", {"ids": IDS, "pin": look})
        for it in range(1, MAX_ITERS + 1):
            lut[node_of[c]] = next_out(c)
            set_lut(LOOP_PANELS, sorted(lut.items()))
            time.sleep(5)
            h, v = screen_hue()
            e = wrap(targets[c] - h)
            tries[c].append((lut[node_of[c]], e))
            log(f"[{k * 100 // len(todo)}%] {c} try {it}: send {lut[node_of[c]] % 360:.1f} -> screen {h:.0f} vs led {targets[c]:.0f}, err {e:+.1f}")
            if abs(e) < DONE_DEG: break
        lut[node_of[c]] = min(tries[c], key=lambda t: abs(t[1]))[0]
    final = sorted(lut.items())
    set_lut(ALL_FANS, final)
    errs = {c: min(tries[c], key=lambda t: abs(t[1]))[1] for c in order}
    open(os.path.join(HERE, "hue_lut_final.txt"), "w").write(lut_text(final))
    log("[..] per colour err: " + ", ".join(f"{c} {e:+.1f}" for c, e in errs.items()))
    log(f"[OK] mean |err| {np.mean([abs(e) for e in errs.values()]):.1f} deg, applied to all fan screens: {lut_text(final)}")
except Exception as ex:
    log(f"[ERROR] {ex}"); sys.exit(1)
finally:
    for i, look in orig.items(): post("/devices/lighting-devices/pins", {"ids": [i], "pin": look})
    log("[..] chain pins restored")
