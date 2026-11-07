import colorsys, ctypes, json, os, sys, time, urllib.request
import numpy as np
from PIL import Image, ImageGrab
ctypes.windll.shcore.SetProcessDpiAwareness(2)
HERE = os.path.dirname(os.path.abspath(__file__))
MODE = sys.argv[1]  # "leds" or "screen"
IDS = ["lianli-wireless:22E584E566E1:outer", "lianli-wireless:22E584E566E1:inner"]
COLORS = ["#ff0000", "#00ff00", "#0000ff", "#00ffff", "#ff00ff", "#ffff00", "#ffffff",
          "#ff8000", "#ff0080", "#8000ff", "#00ff80", "#0080ff", "#80ff00", "#808080"]
PREVIEW = (1290, 318, 2755, 1138)          # Link Controller preview on the primary monitor
SCREEN = (622, 243, 635, 266)              # inside the preview
LEDS = (755, 214, 786, 290)
WAIT_MAX = 25

def token():
    r = urllib.request.Request("http://127.0.0.1:9400/pair", headers={"Host": "localhost:9400"})
    return json.load(urllib.request.urlopen(r, timeout=10))["token"]

def pin(ids, look):
    r = urllib.request.Request("http://127.0.0.1:9400/devices/lighting-devices/pins", data=json.dumps({"ids": ids, "pin": look}).encode(),
        headers={"Host": "localhost:9400", "Authorization": "Bearer " + token(), "Content-Type": "application/json"})
    urllib.request.urlopen(r, timeout=10).read()

def box(img, b):
    return img[b[1]:b[3], b[0]:b[2]].reshape(-1, 3)

def hue(rgb):
    h, s, v = colorsys.rgb_to_hsv(*(np.clip(rgb, 0, 255) / 255))
    return h * 360, s

def gap(a, b):
    d = abs(a - b) % 360
    return min(d, 360 - d)

def led_of(img):
    px = box(img, LEDS); peak = px.max(axis=1)
    ok = px[(peak > 30) & (peak < 245)]
    if len(ok) < 20:
        return None
    return np.median(ok[ok.max(axis=1) >= np.percentile(ok.max(axis=1), 80)], axis=0)

s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
orig = {i: s["lighting"]["devicePins"][i] for i in IDS}
out, crops = [], []
try:
    for k, c in enumerate(COLORS, 1):
        look = dict(orig[IDS[0]]); look["color"] = c
        pin(IDS, look)
        want_h, want_s = hue(np.array([int(c[i:i + 2], 16) for i in (1, 3, 5)], float))
        start, img, led = time.time(), None, None
        if MODE == "screen":
            time.sleep(4)
        while MODE != "screen" and time.time() - start < WAIT_MAX:
            time.sleep(2)
            img = np.asarray(ImageGrab.grab(PREVIEW).convert("RGB"), dtype=np.float32)
            led = led_of(img)
            # The LEDs must show the test colour first: a stalled dongle leaves them on the old one.
            if led is not None and (want_s < 0.2 or gap(hue(led)[0], want_h) < 45):
                break
        else:
            if MODE != "screen":
                print(f"[..] {c}: LEDs did not change within {WAIT_MAX}s, skipped", flush=True)
                continue
        val = None
        for _ in range(5):
            time.sleep(1.5)
            img = np.asarray(ImageGrab.grab(PREVIEW).convert("RGB"), dtype=np.float32)
            val = np.median(box(img, SCREEN), axis=0) if MODE == "screen" else led_of(img)
            if val is not None:
                break
        if val is None:
            print(f"[..] {c}: no readable pixels, skipped", flush=True)
            continue
        out.append({"sent": c, MODE: np.asarray(val).round(1).tolist()})
        crops.append(Image.fromarray(img.astype(np.uint8)).crop((560, 160, 820, 320)))
        print(f"[{k * 100 // len(COLORS)}%] {c} {MODE} {np.round(val).astype(int).tolist()}", flush=True)
    json.dump(out, open(os.path.join(HERE, f"pass_{MODE}.json"), "w"), indent=1)
    sheet = Image.new("RGB", (260 * 7, 160 * 2))
    for n, im in enumerate(crops):
        sheet.paste(im, ((n % 7) * 260, (n // 7) * 160))
    sheet.save(os.path.join(HERE, f"sheet_{MODE}.jpg"))
    print(f"[OK] {len(out)}/{len(COLORS)} colours", flush=True)
finally:
    for i, look in orig.items():
        pin([i], look)
