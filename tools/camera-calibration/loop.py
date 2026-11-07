"""Closed-loop hue calibration: the camera compares each fan screen's hue with its LEDs' and nudges the screen's hue table."""
import colorsys, json, os, sys, time, urllib.request
import numpy as np
from PIL import Image
from mcpc import tool
HERE = os.path.dirname(os.path.abspath(__file__))
IDS = ["lianli-wireless:22E584E566E1:outer", "lianli-wireless:22E584E566E1:inner"]
LOOP_PANELS = ["oqCehmgjROQe", "h_WlElG90KeR"]
ALL_FANS = LOOP_PANELS + ["PTALbXZtFRWZ"]
SCREEN = (815, 318, 832, 348)
KP, MAX_STEP, ROUNDS, DONE_DEG = 0.7, 25.0, 4, 3.0

def log(m): print(m, flush=True)
def token():
    r = urllib.request.Request("http://127.0.0.1:9400/pair", headers={"Host": "localhost:9400"})
    return json.load(urllib.request.urlopen(r, timeout=10))["token"]
def post(path, body):
    r = urllib.request.Request("http://127.0.0.1:9400" + path, data=json.dumps(body).encode(),
        headers={"Host": "localhost:9400", "Authorization": "Bearer " + token(), "Content-Type": "application/json"})
    urllib.request.urlopen(r, timeout=10).read()
def hue(rgb):
    r, g, b = (np.asarray(rgb, dtype=float) / 255.0).tolist()
    return colorsys.rgb_to_hsv(r, g, b)[0] * 360
def wrap(d): return (d + 180) % 360 - 180
def lut_text(lut): return ";".join(f"{i}:{o % 360:.1f}" for i, o in lut)
def set_lut(panels, lut):
    for p in panels: tool("set_panel_style", {"panel": p, "config": {"slot0_glowHueLut": lut_text(lut)}})
def screen_hue():
    img = np.asarray(Image.open(os.path.join(HERE, "latest.jpg")).convert("RGB"), dtype=np.float32)
    v = np.median(img[SCREEN[1]:SCREEN[3], SCREEN[0]:SCREEN[2]].reshape(-1, 3), axis=0)
    return hue(v), v

leds = {p["sent"]: p["leds"] for p in json.load(open(os.path.join(HERE, "pass_leds.json"))) if p["sent"] != "#808080"}
order = sorted(leds, key=lambda c: round(hue([int(c[i:i + 2], 16) for i in (1, 3, 5)])))
targets = {c: hue(leds[c]) for c in order}
lut = [(int(float(p.split(":")[0])), float(p.split(":")[1])) for p in open(os.path.join(HERE, "hue_lut.txt")).read().strip().split(";")]
s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
orig = {i: s["lighting"]["devicePins"][i] for i in IDS}
if __name__ == "__main__":
    history = []
    try:
        for p in ALL_FANS: post(f"/panel/devices/{p}", {"fpsCap": 11})
        time.sleep(8)
        for p in ALL_FANS: post(f"/panel/devices/{p}", {"fpsCap": 12})
        time.sleep(8)
        log("[..] screens reloaded on the new web build")
        total = ROUNDS * len(order); step = 0
        for rnd in range(1, ROUNDS + 1):
            set_lut(LOOP_PANELS, lut)
            time.sleep(3)
            errs = {}
            for c in order:
                look = dict(orig[IDS[0]]); look["color"] = c
                post("/devices/lighting-devices/pins", {"ids": IDS, "pin": look})
                time.sleep(4)
                h, v = screen_hue()
                errs[c] = wrap(targets[c] - h)
                step += 1
                log(f"[{step * 100 // total}%] {step}/{total} round {rnd} {c} led {targets[c]:.0f} screen {h:.0f} err {errs[c]:+.1f} rgb {np.round(v).astype(int).tolist()}")
            history.append({"round": rnd, "lut": lut_text(lut), "errors": errs})
            worst = max(abs(e) for e in errs.values()); mean = np.mean([abs(e) for e in errs.values()])
            log(f"[..] round {rnd}: mean |err| {mean:.1f} deg, worst {worst:.1f} deg")
            if worst < DONE_DEG: break
            byhue = {round(hue([int(c[i:i + 2], 16) for i in (1, 3, 5)])) % 360: e for c, e in errs.items()}
            lut = [(i, o + max(-MAX_STEP, min(MAX_STEP, KP * byhue.get(i, 0.0)))) for i, o in lut]
        best = min(history, key=lambda r: np.mean([abs(e) for e in r["errors"].values()]))
        lut = [(int(float(p.split(":")[0])), float(p.split(":")[1])) for p in best["lut"].split(";")]
        set_lut(ALL_FANS, lut)
        open(os.path.join(HERE, "hue_lut_loop.txt"), "w").write(best["lut"])
        json.dump(history, open(os.path.join(HERE, "loop_history.json"), "w"), indent=1)
        log(f"[OK] best round {best['round']} applied to all fan screens: {best['lut']}")
    except Exception as e:
        log(f"[ERROR] {e}"); sys.exit(1)
    finally:
        for i, look in orig.items(): post("/devices/lighting-devices/pins", {"ids": [i], "pin": look})
        log("[..] chain pins restored")
