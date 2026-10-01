"""Paired test: corrected thin-card/reference wording against theme benchmark's thick-card baseline."""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import config
import graph
import sts2
from benchmark_theme_colors import metrics
from comfy import Comfy
from worker import _apply_theme


FLAT_PROMPT = (
    "sts2 card art. 画面主体是三张薄而平整的游戏卡牌，卡牌外形和正面图案采用 <image1> 中蓝色卡牌/令牌的"
    "锐利多边形轮廓与浅色印刷边框。三张牌从左下向右上飞出，沿同一条对角路径依次错落并部分重叠，"
    "形成清晰的近大远小层次。每张牌保持单层平面薄片的比例，侧边仅是一条极细线，正面图案清楚可读；"
    "定格在飞行中的不同旋转角度。以清晰轮廓光和简洁高对比背景突出三张卡牌。"
)


async def run(args):
    reference = Path(args.reference).resolve()
    output = config.DATA / "experiments" / f"card_shape_{time.strftime('%Y%m%d_%H%M%S')}"
    output.mkdir(parents=True, exist_ok=False)
    prompt = _apply_theme(FLAT_PROMPT, ["#269ccb"])
    settings = config.load()
    gen = settings["gen"]
    comfy = Comfy()
    manifest = {"reference": str(reference), "seeds": args.seeds, "prompt": prompt,
                "baseline": "matching A_one_clean images in the latest theme_abc experiment", "images": []}
    try:
        await comfy.ensure()
        uploaded = await comfy.upload(reference)
        for seed in args.seeds:
            params = {"mode": "draft", "prompt": prompt, "negative": gen.get("negative"), "width": 1024,
                      "height": 768, "steps": 25, "cfg": 3.0, "seed": seed, "batch": 1,
                      "lora": gen.get("lora"), "lora_strength": gen.get("lora_strength", 0.9),
                      "ref_resolution": 512, "easycache": True, "stop_at": 8}
            print(f"[flat_reference] seed {seed}", flush=True)
            started = time.time()
            data = (await comfy.run(graph.build(params, [uploaded])))[0]
            path = output / f"{seed}_flat_reference.png"
            path.write_bytes(data)
            manifest["images"].append({"seed": seed, "path": str(path),
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
