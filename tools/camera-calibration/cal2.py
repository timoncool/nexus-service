"""Per screen, per colour: hold one colour on the LEDs, read their hue (dimmed so the camera does not clip),
then move only that colour's node of each screen's hue table until the camera sees the same hue on the glass."""
import json, os, sys, time
import numpy as np
from loop import post, wrap, HERE, log
from mcpc import tool
from rois import frame, led_colour, screen_colour, hue
CHAIN = ["lianli-wireless:22E584E566E1:outer", "lianli-wireless:22E584E566E1:inner"]
PUMP_PIN = "lianli-wireless:BB951BE566E1:pump"
PANELS = {"7qSo2XMqKB8u": "pump", "oqCehmgjROQe": "chain", "h_WlElG90KeR": "chain"}
NAMES = {"7qSo2XMqKB8u": "LIQUID", "oqCehmgjROQe": "GPU LOAD", "h_WlElG90KeR": "CPU LOAD"}
COLORS = ["#ff0000", "#ff4000", "#ff8000", "#ffff00", "#80ff00", "#00ff00", "#00ff80", "#00ffff",
          "#0080ff", "#0000ff", "#8000ff", "#ff00ff", "#ff00c0", "#ff0080", "#ff0040"]
DONE_DEG, MAX_ITERS = 2.5, 8
FAN_LUT = "0:0.9;30:29.0;60:36.9;90:80.9;120:137.6;150:154.2;180:169.3;210:207.5;240:215.4;270:268.8;300:326.0;330:335.8"

def node(c): return round(hue([int(c[i:i + 2], 16) for i in (1, 3, 5)])) % 360
def dim(c): return "#" + "".join(f"{int(c[i:i + 2], 16) // 4:02x}" for i in (1, 3, 5))
def interp(pairs, h):
    pts = sorted(pairs)
    for i in range(len(pts)):
        x0, y0 = pts[i]; x1, y1 = pts[(i + 1) % len(pts)]
        if i + 1 == len(pts): x1 += 360; y1 += 360
        if y1 < y0 - 180: y1 += 360
        hh = h if h >= x0 else h + 360
        if x0 <= hh <= x1: return (y0 + (y1 - y0) * (hh - x0) / (x1 - x0)) % 360
    return h
def text(lut): return ";".join(f"{k}:{v % 360:.1f}" for k, v in sorted(lut.items()))
def avg_frames(n=2, gap=1.2):
    imgs = []
    for _ in range(n):
        imgs.append(frame(os.path.join(HERE, "latest.jpg"))); time.sleep(gap)
    return np.mean(imgs, axis=0)
def show(color):
    for ids in (CHAIN, [PUMP_PIN]):
        look = dict(orig[PUMP_PIN]); look["color"] = color
        post("/devices/lighting-devices/pins", {"ids": ids, "pin": look})

def next_out(tries, ref):
    pts = [(ref + wrap(o - ref), e) for o, e in tries]
    above = [p for p in pts if p[1] > 0]; below = [p for p in pts if p[1] < 0]
    if above and below:
        lo = max(above, key=lambda p: p[0]); hi = min(below, key=lambda p: p[0])
        if lo[0] < hi[0] - 0.3:
            x = lo[0] + (hi[0] - lo[0]) * lo[1] / (lo[1] - hi[1])
            mid = (lo[0] + hi[0]) / 2
            return x if abs(x - mid) < (hi[0] - lo[0]) * 0.4 else mid
    o, e = pts[-1]
    return o + max(-15.0, min(15.0, 0.8 * e))

if __name__ == "__main__":
    s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
    orig = {i: s["lighting"]["devicePins"][i] for i in CHAIN + [PUMP_PIN]}
    orig.update({i: dict(orig[PUMP_PIN]) for i in CHAIN})
    fan = dict((int(float(a)), float(b)) for a, b in (p.split(":") for p in FAN_LUT.split(";")))
    luts = {p: ({node(c): float(node(c)) for c in COLORS} if kind == "pump" else {node(c): interp(fan.items(), node(c)) for c in COLORS})
            for p, kind in PANELS.items()}
    report = {p: {} for p in PANELS}
    try:
        post("/panel/devices/7qSo2XMqKB8u", {"fpsCap": 7}); time.sleep(8)
        post("/panel/devices/7qSo2XMqKB8u", {"fpsCap": 8}); time.sleep(8)
        for p in PANELS: tool("set_panel_style", {"panel": p, "config": {"slot0_glowHueLut": text(luts[p])}})
        log("[..] pump screen reloaded; starting tables set")
        for k, c in enumerate(COLORS, 1):
            show(dim(c)); time.sleep(4.5)
            img = avg_frames()
            target = {w: led_colour(img, w)[0] for w in ("pump", "chain")}
            if any(v is None for v in target.values()):
                log(f"[ERROR] {c}: LEDs not found in the frame"); sys.exit(1)
            th = {w: hue(v) for w, v in target.items()}
            show(c); time.sleep(5)
            tries = {p: [] for p in PANELS}; open_ = set(PANELS)
            for it in range(1, MAX_ITERS + 1):
                img = avg_frames()
                line = []
                for p in list(open_):
                    v, _ = screen_colour(img, p)
                    if v is None: line.append(f"{NAMES[p]} no glass px"); continue
                    e = wrap(th[PANELS[p]] - hue(v))
                    tries[p].append((luts[p][node(c)], e))
                    line.append(f"{NAMES[p]} send {luts[p][node(c)] % 360:.0f} screen {hue(v):.0f} led {th[PANELS[p]]:.0f} err {e:+.1f}")
                    if abs(e) < DONE_DEG: open_.discard(p)
                    else: luts[p][node(c)] = next_out(tries[p], node(c)) % 360
                log(f"[{(k - 1) * 100 // len(COLORS)}%] {c} it {it}: " + " | ".join(line))
                if not open_: break
                for p in open_: tool("set_panel_style", {"panel": p, "config": {"slot0_glowHueLut": text(luts[p])}})
                time.sleep(4)
            for p in PANELS:
                if tries[p]:
                    best = min(tries[p], key=lambda t: abs(t[1]))
                    luts[p][node(c)] = best[0] % 360; report[p][c] = round(best[1], 1)
                tool("set_panel_style", {"panel": p, "config": {"slot0_glowHueLut": text(luts[p])}})
        tool("set_panel_style", {"panel": "PTALbXZtFRWZ", "config": {"slot0_glowHueLut": text(luts["h_WlElG90KeR"])}})
        json.dump({"luts": {p: text(l) for p, l in luts.items()}, "errors": report}, open(os.path.join(HERE, "cal2_result.json"), "w"), indent=1)
        for p in PANELS:
            errs = list(report[p].values())
            log(f"[..] {NAMES[p]}: mean |err| {np.mean(np.abs(errs)):.1f}, worst {np.max(np.abs(errs)):.1f} | " + ", ".join(f"{c} {e:+.1f}" for c, e in report[p].items()))
        log("[OK] tables applied (GPU TEMP takes the CPU LOAD table)")
    except SystemExit: raise
    except Exception as ex:
        log(f"[ERROR] {ex}"); sys.exit(1)
    finally:
        for i, look in orig.items(): post("/devices/lighting-devices/pins", {"ids": [i], "pin": look})
        log("[..] pins restored")
