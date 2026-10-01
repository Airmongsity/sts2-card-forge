"""Eight-seed paired regression for free-form prompting versus the structured prompt compiler.

This does not call a paid prompt LLM: the B arm uses a fixed schema-valid plan so the benchmark isolates the
deterministic compiler and object constraints. Both arms use the same fuel.png reference, colour, sampler settings,
and seeds. Results are kept outside the gallery database under data/experiments.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))

import config
import graph
import promptgen
from benchmark_theme_colors import BASE_PROMPT, metrics
from comfy import Comfy
from worker import _apply_theme


SEEDS = [486112413528211, 729666335253144, 848889539631750, 382662938299715,
         297419126001431, 725252943735406, 311061674466897, 549766481981374]

CARD = {
    "cls": "colorless",
    "concept": "3张卡牌飞出，卡牌参考参考图中的卡牌（蓝色卡面）",
    "params": {"refs": ["fuel.png"]},
}

PLAN = {
    "grammar": "action",
    "composition": "三张卡牌占据画面中央，从左下近景向右上远景形成一条清晰对角线，最近一张被画面边缘轻微裁切",
    "subjects": [{
        "id": "flying_cards", "kind": "object", "label": "蓝色游戏卡牌", "count": 3,
        "appearance": "锐利多边形外轮廓、印刷边框和清楚可读的正面旋涡圆珠图案",
        "reference": "<image1>", "action": "沿同一条对角路径向右上方飞出，各自保持略有差异的旋转角度",
        "state": "三张卡牌完整且彼此分离",
    }],
    "relations": ["三张卡牌沿飞行路径依次错落并局部重叠，形成近大远小的节奏"],
    "lighting": "侧向高对比光线勾勒卡牌轮廓，并让正面图案保持清楚",
    "background": "简洁背景只保留轻微纵深和一条与飞行方向一致的运动轨迹",
    "notes": "A ACTION — 三张参考卡牌沿一条对角路径飞出",
}


def _contact_sheet(output: Path, seeds: list[int]):
    cells = []
    for seed in seeds:
        a = Image.open(output / f"{seed}_A_freeform.png").convert("RGB").resize((480, 360))
        b = Image.open(output / f"{seed}_B_structured.png").convert("RGB").resize((480, 360))
        row = Image.new("RGB", (960, 392), "#202020")
        row.paste(a, (0, 32))
        row.paste(b, (480, 32))
        draw = ImageDraw.Draw(row)
        draw.text((8, 8), f"seed {seed}  A free-form", fill="white")
        draw.text((488, 8), "B structured", fill="white")
        cells.append(row)
    sheet = Image.new("RGB", (960, 392 * len(cells)), "#202020")
    for index, row in enumerate(cells):
        sheet.paste(row, (0, index * 392))
    sheet.save(output / "contact_sheet.jpg", quality=92)


async def run(args):
    reference = Path(args.reference).resolve()
    if not reference.is_file():
        raise FileNotFoundError(reference)
    output = config.DATA / "experiments" / f"prompt_planner_ab_{time.strftime('%Y%m%d_%H%M%S')}"
    output.mkdir(parents=True, exist_ok=False)
    structured, normalized = promptgen.compile_scene_plan(PLAN, CARD, "zh")
    variants = {
        "A_freeform": _apply_theme(BASE_PROMPT, ["#269ccb"]),
        "B_structured": _apply_theme(structured, ["#269ccb"]),
    }
    if args.structured_only:
        variants = {"B_structured": variants["B_structured"]}
    settings = config.load()
    gen = settings["gen"]
    manifest = {
        "purpose": "8-seed paired regression: legacy free-form wording vs structured deterministic compiler",
        "limitation": "Fixed plan isolates compiler behavior; it does not measure live LLM plan quality.",
        "reference": str(reference), "seeds": args.seeds, "variants": variants, "normalized_plan": normalized,
        "settings": {"width": 1024, "height": 768, "steps": 25, "cfg": 3.0, "stop_at": 8,
                     "easycache": True, "lora": gen.get("lora"),
                     "lora_strength": gen.get("lora_strength", 0.9), "ref_resolution": 512},
        "images": [],
    }
    comfy = Comfy()
    try:
        await comfy.ensure()
        uploaded = await comfy.upload(reference)
        for seed in args.seeds:
            for name, prompt in variants.items():
                params = {"mode": "draft", "prompt": prompt, "negative": gen.get("negative"), "width": 1024,
                          "height": 768, "steps": 25, "cfg": 3.0, "seed": seed, "batch": 1,
                          "lora": gen.get("lora"), "lora_strength": gen.get("lora_strength", 0.9),
                          "ref_resolution": 512, "easycache": True, "stop_at": 8}
                print(f"[{name}] seed {seed}", flush=True)
                started = time.time()
                data = (await comfy.run(graph.build(params, [uploaded])))[0]
                path = output / f"{seed}_{name}.png"
                path.write_bytes(data)
                manifest["images"].append({"variant": name, "seed": seed, "path": str(path),
                                           "seconds": round(time.time() - started, 2), **metrics(data)})
                (output / "manifest.json").write_text(
                    json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        if not args.structured_only:
            _contact_sheet(output, args.seeds)
    finally:
        await comfy.stop()
        if comfy._session and not comfy._session.closed:
            await comfy._session.close()
    print(output, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", default=str(config.ROOT / "sts2-card-portraits" / "token" / "fuel.png"))
    parser.add_argument("--seeds", nargs="+", type=int, default=SEEDS)
    parser.add_argument("--structured-only", action="store_true",
                        help="Generate only the structured arm (used for targeted follow-up of failed seeds).")
    asyncio.run(run(parser.parse_args()))


if __name__ == "__main__":
    main()
