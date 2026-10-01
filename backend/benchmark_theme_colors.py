"""Controlled local A/B/C benchmark for CardForge theme-colour prompting.

Writes images and a machine-readable manifest under data/experiments without touching cards, jobs or the gallery DB.
All variants share seeds, reference, composition text and generation settings.
"""
from __future__ import annotations

import argparse
import asyncio
import colorsys
import io
import json
import math
import sys
import time
from collections import Counter
from pathlib import Path

from PIL import Image, ImageFilter, ImageStat

sys.path.insert(0, str(Path(__file__).resolve().parent))

import config
import graph
from comfy import Comfy
from worker import _apply_theme


BASE_PROMPT = (
    "sts2 card art. 画面主体是三张厚实的卡牌，从左下角斜向右上方飞出；最前一张被画面边缘裁切，"
    "三张牌沿同一条对角路径依次错落并部分重叠，形成清晰的近大远小层次。每张牌的正面都有压印边框、"
    "磨损边缘和微微翘起的卷角，硬质纸芯在翻转的角上露出纤维。背景保持简洁，以强烈明暗对比突出卡牌。"
)
POLLUTION = (
    "一道明亮的金黄色边缘光勾出每张牌的轮廓，牌面叠着玫红色高光，背景是浓郁的暗绯红色。"
)


def metrics(data: bytes) -> dict:
    image = Image.open(io.BytesIO(data)).convert("RGB").resize((256, 192), Image.Resampling.LANCZOS)
    pixels = list(image.get_flattened_data())
    hues, saturated = [0] * 12, 0
    quantized = Counter()
    for r, g, b in pixels:
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        if s >= 0.25 and v >= 0.12:
            hues[min(11, int(h * 12))] += 1
            saturated += 1
        quantized[(r // 32, g // 32, b // 32)] += 1
    total = len(pixels)
    entropy = -sum((n / total) * math.log2(n / total) for n in quantized.values())
    edge = ImageStat.Stat(image.filter(ImageFilter.FIND_EDGES).convert("L")).mean[0]
    active_hues = sum(n >= max(1, saturated * 0.03) for n in hues)
    top_hues = sorted(enumerate(hues), key=lambda item: item[1], reverse=True)[:4]
    return {"rgb_entropy": round(entropy, 4), "edge_mean": round(edge, 3), "active_hue_bins": active_hues,
            "hue_histogram": hues, "top_hue_bins": [index for index, _ in top_hues]}


async def run(args):
    reference = Path(args.reference).resolve()
    if not reference.is_file():
        raise FileNotFoundError(reference)
    stamp = time.strftime("%Y%m%d_%H%M%S")
    output = config.DATA / "experiments" / f"theme_abc_{stamp}"
    output.mkdir(parents=True, exist_ok=False)
    variants = {
        "A_one_clean": _apply_theme(BASE_PROMPT, ["#269ccb"]),
        "B_two_clean": _apply_theme(BASE_PROMPT, ["#269ccb", "#ccbc4d"]),
        "C_one_polluted": _apply_theme(BASE_PROMPT + POLLUTION, ["#269ccb"]),
    }
    settings = config.load()
    gen = settings["gen"]
    comfy = Comfy()
    manifest = {"reference": str(reference), "seeds": args.seeds, "variants": variants, "settings": {
        "width": 1024, "height": 768, "steps": 25, "cfg": 3.0, "stop_at": 8, "easycache": True,
        "lora": gen.get("lora"), "lora_strength": gen.get("lora_strength"), "ref_resolution": 512}, "images": []}
    try:
        await comfy.ensure()
        uploaded = await comfy.upload(reference)
        for seed in args.seeds:
            for name, prompt in variants.items():
                p = {"mode": "draft", "prompt": prompt, "negative": gen.get("negative"), "width": 1024,
                     "height": 768, "steps": 25, "cfg": 3.0, "seed": seed, "batch": 1,
                     "lora": gen.get("lora"), "lora_strength": gen.get("lora_strength", 0.9),
                     "ref_resolution": 512, "easycache": True, "stop_at": 8}
                print(f"[{name}] seed {seed}", flush=True)
                started = time.time()
                images = await comfy.run(graph.build(p, [uploaded]))
                data = images[0]
                path = output / f"{seed}_{name}.png"
                path.write_bytes(data)
                manifest["images"].append({"variant": name, "seed": seed, "path": str(path),
                                           "seconds": round(time.time() - started, 2), **metrics(data)})
                (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    finally:
        await comfy.stop()
        if comfy._session and not comfy._session.closed:
            await comfy._session.close()
    print(output, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", default=str(config.ROOT / "sts2-card-portraits" / "token" / "fuel.png"))
    parser.add_argument("--seeds", nargs="+", type=int,
                        default=[486112413528211, 729666335253144, 848889539631750])
    asyncio.run(run(parser.parse_args()))


if __name__ == "__main__":
    main()
