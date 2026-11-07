"""Measure how the case camera sees each fan screen against its own LEDs, and fit a colour correction.

The lighting goes through test colours (the Ambient Glow screens follow it on their own),
one long camera stream keeps the gimbal where it was put, and each colour's screen and LED
appearance are measured in the same frame so camera exposure cancels out.
"""
import json
import os
import subprocess
import sys
import time
import urllib.request

import numpy as np
from PIL import Image
from scipy import ndimage

HERE = os.path.dirname(os.path.abspath(__file__))
FFMPEG = r"C:\Program Files\Nexus\tools\ffmpeg\ffmpeg.exe"
DUVC = r"D:\Projects\TEMP\_rtest\duvc\duvc-cli-x64.exe"
LATEST = os.path.join(HERE, "latest.jpg")
ANGLES = "Pan=-80,Tilt=-38,Zoom=210"
SETTLE_S = 7.0
CHAIN_IDS = ["lianli-wireless:22E584E566E1:outer", "lianli-wireless:22E584E566E1:inner"]
TEST_COLORS = [
    "#ff0000", "#00ff00", "#0000ff", "#00ffff", "#ff00ff", "#ffff00", "#ffffff",
    "#ff8000", "#ff0080", "#8000ff", "#00ff80", "#0080ff", "#80ff00", "#808080",
]


def log(msg):
    print(msg, flush=True)


def mcp(name, args):
    tok = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))["aiIntegration"]["token"]
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": args}}).encode()
    req = urllib.request.Request("http://127.0.0.1:9420/mcp", data=body, headers={
        "Authorization": "Bearer " + tok, "Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    raw = urllib.request.urlopen(req, timeout=20).read().decode()
    raw = raw[raw.index("{"):raw.rindex("}") + 1]
    result = json.loads(raw)["result"]
    if result.get("isError"):
        raise RuntimeError(f"{name}: {result['content'][0]['text']}")
    return result["content"][0]["text"]


def service_token():
    req = urllib.request.Request("http://127.0.0.1:9400/pair", headers={"Host": "localhost:9400"})
    return json.load(urllib.request.urlopen(req, timeout=10))["token"]


def pin(ids, pin_look):
    body = json.dumps({"ids": ids, "pin": pin_look}).encode()
    req = urllib.request.Request("http://127.0.0.1:9400/devices/lighting-devices/pins", data=body, headers={
        "Host": "localhost:9400", "Authorization": "Bearer " + service_token(), "Content-Type": "application/json"})
    urllib.request.urlopen(req, timeout=10).read()


def saved_pins():
    s = json.load(open(r"C:\ProgramData\Nexus\settings.json", encoding="utf-8-sig"))
    return {i: s["lighting"]["devicePins"].get(i) for i in CHAIN_IDS}


ORIGINAL = {}


def frame():
    deadline = time.time() + 5
    while time.time() < deadline:
        try:
            with Image.open(LATEST) as im:
                return np.asarray(im.convert("RGB"), dtype=np.float32)
        except OSError:
            time.sleep(0.2)
    raise RuntimeError("no camera frame")


def view_score(img):
    ref = np.asarray(Image.open(os.path.join(HERE, "reference.jpg")).convert("L").resize((192, 108)), dtype=np.float64)
    cur = np.asarray(Image.fromarray(img.astype(np.uint8)).convert("L").resize((192, 108)), dtype=np.float64)
    ref = (ref - ref.mean()) / (ref.std() + 1e-6)
    cur = (cur - cur.mean()) / (cur.std() + 1e-6)
    return float((ref * cur).mean())


def aim_at_case():
    """The angles that held in the hand-run captures, re-sent before every measurement so the camera cannot wander off."""
    subprocess.run([DUVC, "set", "0", "cam", ANGLES], capture_output=True)
    time.sleep(2.5)


def show(color):
    aim_at_case()
    look = dict(next(v for v in ORIGINAL.values() if v))
    look["color"] = color
    pin(CHAIN_IDS, look)
    time.sleep(SETTLE_S)
    return frame()


def srgb_to_linear(c):
    c = np.clip(np.asarray(c, dtype=np.float64) / 255.0, 0, 1)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def hex_rgb(h):
    return np.array([int(h[i:i + 2], 16) for i in (1, 3, 5)], dtype=np.float64)


def find_regions(dark, bright):
    # The camera re-exposes between frames, so each is scaled to its own median before differencing.
    b = bright.mean(axis=2) / max(np.median(bright), 1.0)
    d = dark.mean(axis=2) / max(np.median(dark), 1.0)
    diff = (b - d) * 40
    lit = diff > 40
    labels, n = ndimage.label(lit)
    screens = []
    for i in range(1, n + 1):
        ys, xs = np.nonzero(labels == i)
        if ys.size < 2000:
            continue
        h, w = np.ptp(ys) + 1, np.ptp(xs) + 1
        fill = ys.size / (h * w)
        aspect = min(h, w) / max(h, w)
        r = max(h, w) / 2
        if aspect > 0.45 and fill > 0.5 and 50 < r < 200:
            cy = float(ys.max() - r) if h < w else float(ys.mean())
            screens.append({"cy": cy, "cx": float(xs.mean()), "r": float(r), "label": i})
    screens.sort(key=lambda s: s["cy"])
    return lit, labels, screens


def regions_for(screen, lit, shape):
    yy, xx = np.mgrid[0:shape[0], 0:shape[1]]
    d = np.hypot(yy - screen["cy"], xx - screen["cx"])
    r = screen["r"]
    glass = (d > 0.86 * r) & (d < 0.97 * r)
    leds = lit & (d > 1.6 * r) & (d < 6.0 * r)
    return glass, leds


def measure(img, mask):
    px = img[mask]
    if px.size == 0:
        return None
    peak = px.max(axis=1)
    keep = (peak < 245) & (peak > 25)
    px = px[keep] if keep.sum() > 50 else px
    lum = px.mean(axis=1)
    lo, hi = np.percentile(lum, 35), np.percentile(lum, 85)
    sel = px[(lum >= lo) & (lum <= hi)]
    return sel.mean(axis=0) if sel.size else px.mean(axis=0)


def fit(sent, seen):
    x = srgb_to_linear(np.array(sent))
    y = srgb_to_linear(np.array(seen))
    m, *_ = np.linalg.lstsq(x, y, rcond=None)
    return m.T


def hue_error(a, b):
    a = a / max(a.max(), 1e-6)
    b = b / max(b.max(), 1e-6)
    return float(np.abs(a - b).mean() * 255)


def main():
    ORIGINAL.update(saved_pins())
    log(f"[..] chain pins before: {[(k.split(':')[-1], (v or {}).get('color')) for k, v in ORIGINAL.items()]}")
    sheet = []
    cam = subprocess.Popen([FFMPEG, "-hide_banner", "-loglevel", "error", "-f", "dshow", "-vcodec", "h264",
                            "-video_size", "3840x2160", "-i", "video=Insta360 Link", "-r", "3", "-update", "1",
                            "-q:v", "3", "-y", LATEST], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(2.5)
        dark = show("#000000")
        bright = show("#ffffff")
        Image.fromarray(bright.astype(np.uint8)).save(os.path.join(HERE, "regions_bright.jpg"))
        Image.fromarray(dark.astype(np.uint8)).save(os.path.join(HERE, "regions_dark.jpg"))
        Image.fromarray(np.clip((bright - dark).mean(axis=2) * 3, 0, 255).astype(np.uint8)).save(os.path.join(HERE, "regions_diff.jpg"))
        lit, labels, screens = find_regions(dark, bright)
        log(f"[..] screens found: {[(round(s['cx']), round(s['cy']), round(s['r'])) for s in screens]}")
        if not screens:
            raise RuntimeError("no fan screen in view")
        masks = [regions_for(s, lit, lit.shape) for s in screens]
        for i, (glass, leds) in enumerate(masks):
            log(f"[..] screen {i}: glass px {int(glass.sum())}, led px {int(leds.sum())}")
        samples = [[] for _ in screens]
        for k, color in enumerate(TEST_COLORS, 1):
            img = show(color)
            sheet.append((color, Image.fromarray(img.astype(np.uint8)).resize((480, 270))))
            for i, (glass, leds) in enumerate(masks):
                samples[i].append({"sent": color, "screen": measure(img, glass).tolist(), "leds": measure(img, leds).tolist()})
            log(f"[{k * 100 // len(TEST_COLORS)}%] {k}/{len(TEST_COLORS)} {color}")
        report = []
        for i, s in enumerate(screens):
            sent = [hex_rgb(p["sent"]) for p in samples[i]]
            a = fit(sent, [p["screen"] for p in samples[i]])
            b = fit(sent, [p["leds"] for p in samples[i]])
            corr = np.linalg.solve(a, b)
            corr /= max((corr @ np.ones(3)).max(), 1e-6)
            before = np.mean([hue_error(np.array(p["screen"]), np.array(p["leds"])) for p in samples[i]])
            report.append({"screen": i, "center": [s["cx"], s["cy"]], "radius": s["r"], "matrix": corr.round(4).tolist(),
                           "hueErrorBefore": round(float(before), 1),
                           "samples": samples[i]})
            log(f"[..] screen {i}: matrix {corr.round(3).tolist()}, hue error before {before:.1f}")
        json.dump(report, open(os.path.join(HERE, "calibration.json"), "w"), indent=1)
        cols = 4
        out = Image.new("RGB", (480 * cols, 270 * ((len(sheet) + cols - 1) // cols)))
        for n, (_, im) in enumerate(sheet):
            out.paste(im, ((n % cols) * 480, (n // cols) * 270))
        out.save(os.path.join(HERE, "sheet.jpg"))
        log("[OK] calibration written")
    finally:
        for i, look in ORIGINAL.items():
            if look:
                try:
                    pin([i], look)
                except Exception as e:
                    log(f"[ERROR] restore pin {i}: {e}")
        log("[..] chain pins restored")
        cam.terminate()
        try:
            cam.wait(5)
        except subprocess.TimeoutExpired:
            cam.kill()


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        log(f"[ERROR] {e}")
        sys.exit(1)
