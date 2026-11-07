import json, os, time, urllib.request
import numpy as np
from PIL import Image
HERE = os.path.dirname(os.path.abspath(__file__))
IDS = ["lianli-wireless:22E584E566E1:outer", "lianli-wireless:22E584E566E1:inner"]
COLORS = ["#ff0000", "#00ff00", "#0000ff", "#00ffff", "#ff00ff", "#ffff00", "#ffffff",
          "#ff8000", "#ff0080", "#8000ff", "#00ff80", "#0080ff", "#80ff00", "#808080"]
SCREEN = (815, 318, 832, 348)

def token():
    r = urllib.request.Request("http://127.0.0.1:9400/pair", headers={"Host": "localhost:9400"})
    return json.load(urllib.request.urlopen(r, timeout=10))["token"]

def pin(ids, look):
    r = urllib.request.Request("http://127.0.0.1:9400/devices/lighting-devices/pins", data=json.dumps({"ids": ids, "pin": look}).encode(),
        headers={"Host": "localhost:9400", "Authorization": "Bearer " + token(), "Content-Type": "application/json"})
    urllib.request.urlopen(r, timeout=10).read()

s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
orig = {i: s["lighting"]["devicePins"][i] for i in IDS}
out = []
try:
    for k, c in enumerate(COLORS, 1):
        look = dict(orig[IDS[0]]); look["color"] = c
        pin(IDS, look)
        time.sleep(4)
        img = np.asarray(Image.open(os.path.join(HERE, "latest.jpg")).convert("RGB"), dtype=np.float32)
        val = np.median(img[SCREEN[1]:SCREEN[3], SCREEN[0]:SCREEN[2]].reshape(-1, 3), axis=0)
        out.append({"sent": c, "screen": val.round(1).tolist()})
        print(f"[{k * 100 // len(COLORS)}%] {c} screen {np.round(val).astype(int).tolist()}", flush=True)
    json.dump(out, open(os.path.join(HERE, "pass_screen.json"), "w"), indent=1)
    print("[OK]", flush=True)
finally:
    for i, look in orig.items():
        pin([i], look)
