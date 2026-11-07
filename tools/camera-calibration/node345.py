import json, os, time
import numpy as np
from loop import HERE, wrap
from mcpc import tool
from rois import frame, led_colour, screen_colour, hue
P = "7qSo2XMqKB8u"
cfg = json.loads(tool("get_panels", {}))
lut = next(p for p in cfg["panels"] if p["id"] == P)["widgets"][0]["config"]["slot0_glowHueLut"]
nodes = dict((int(float(a)), float(b)) for a, b in (x.split(":") for x in lut.split(";")))
def txt(): return ";".join(f"{k}:{v % 360:.1f}" for k, v in sorted(nodes.items()))
def look():
    imgs = [frame(os.path.join(HERE, "latest.jpg"))]; time.sleep(0.8); imgs.append(frame(os.path.join(HERE, "latest.jpg")))
    img = np.mean(imgs, axis=0)
    return hue(led_colour(img, "pump")[0]), hue(screen_colour(img, P)[0])
tries = []
for it in range(8):
    led, scr = look(); e = wrap(led - scr)
    tries.append((nodes[345], e))
    print(f"node 345 -> {nodes[345] % 360:.1f}: ring {led:.0f} glass {scr:.0f} err {e:+.1f}", flush=True)
    if abs(e) < 2.5: break
    above = [t for t in tries if t[1] > 0]; below = [t for t in tries if t[1] < 0]
    o = nodes[345]
    if above and below:
        a = max(above, key=lambda t: t[0] - 345 if t[0] >= 180 else t[0] + 15); b = min(below, key=lambda t: t[0] - 345 if t[0] >= 180 else t[0] + 15)
        nxt = a[0] + (b[0] - a[0]) * a[1] / (a[1] - b[1])
    else:
        nxt = o + max(-15, min(15, 0.8 * e))
    nodes[345] = nxt % 360
    tool("set_panel_style", {"panel": P, "config": {"slot0_glowHueLut": txt()}}); time.sleep(2.5)
best = min(tries, key=lambda t: abs(t[1]))
nodes[345] = best[0] % 360
tool("set_panel_style", {"panel": P, "config": {"slot0_glowHueLut": txt()}})
print(f"[OK] node 345 = {nodes[345]:.1f}, err {best[1]:+.1f}; table {txt()}", flush=True)
