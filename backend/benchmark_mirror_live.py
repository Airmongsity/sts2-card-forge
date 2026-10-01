"""Live prompt-LLM + Qwen regression for the Mirror concept, without mutating the card database."""
from __future__ import annotations

import asyncio
import argparse
import json
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))

import config
import graph
import promptgen
import store
import worker
from benchmark_theme_colors import metrics
from comfy import Comfy


SEEDS = [486112413528211, 729666335253144, 848889539631750, 382662938299715,
         297419126001431, 725252943735406, 311061674466897, 549766481981374]


def contact_sheet(output: Path):
    images = []
    for seed in SEEDS:
        im = Image.open(output / f"{seed}.png").convert("RGB").resize((480, 360))
        cell = Image.new("RGB", (480, 388), "#202020")
        cell.paste(im, (0, 28))
        ImageDraw.Draw(cell).text((8, 7), f"seed {seed}", fill="white")
        images.append(cell)
    sheet = Image.new("RGB", (960, 388 * 4), "#202020")
    for index, image in enumerate(images):
        sheet.paste(image, ((index % 2) * 480, (index // 2) * 388))
    sheet.save(output / "contact_sheet.jpg", quality=92)


async def run(resume="", scene_mode=""):
    card = dict(store.get_card(22))
    # Isolate the new planner itself. The saved prompt is the contaminated output under investigation and remains in
    # the database untouched for comparison.
    card["prompt"] = ""
    if scene_mode:
        card["params"] = dict(card.get("params") or {}, scene_mode=scene_mode)
    settings = config.load()
    if resume:
        output = Path(resume).resolve()
        manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
        result = manifest["prompt_result"]
        card["params"] = dict(card.get("params") or {},
                              theme_colors=manifest.get("settings", {}).get("theme_colors") or [])
    else:
        result = await promptgen.art_prompt(card, settings)
    base = worker.resolve_params(card, {"mode": "draft", "prompt": result["prompt"], "batch": 1}, settings)
    base.update(width=1024, height=768, steps=25, cfg=3.0, easycache=True, stop_at=8, batch=1)
    if not resume:
        output = config.DATA / "experiments" / f"mirror_live_{time.strftime('%Y%m%d_%H%M%S')}"
        output.mkdir(parents=True, exist_ok=False)
        manifest = {"card_id": card["id"], "concept": card["concept"], "prompt_result": result,
                    "settings": {k: base.get(k) for k in ("mode", "width", "height", "steps", "cfg", "lora",
                                                                   "lora_strength", "ref_resolution", "theme_colors")},
                    "seeds": SEEDS, "images": []}
    completed = {int(item["seed"]) for item in manifest.get("images") or []}
    pending = [seed for seed in SEEDS if seed not in completed or not (output / f"{seed}.png").is_file()]
    # A fresh local renderer every four images avoids the accumulating low-VRAM patch state observed in the first
    # live run. This is test-harness isolation, not a change to prompts or image parameters.
    for start in range(0, len(pending), 4):
        comfy = Comfy()
        try:
            await comfy.ensure()
            references = [await comfy.upload(Path(path)) for path in base.get("refs") or []]
            for seed in pending[start:start + 4]:
                params = dict(base, seed=seed)
                print(f"[mirror_live] seed {seed}", flush=True)
                started = time.time()
                data = (await comfy.run(graph.build(params, references)))[0]
                path = output / f"{seed}.png"
                path.write_bytes(data)
                manifest["images"].append({"seed": seed, "path": str(path),
                                           "seconds": round(time.time() - started, 2), **metrics(data)})
                (output / "manifest.json").write_text(
                    json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        finally:
            await comfy.stop()
            if comfy._session and not comfy._session.closed:
                await comfy._session.close()
    if all((output / f"{seed}.png").is_file() for seed in SEEDS):
        contact_sheet(output)
    print(output, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--resume", default="")
    parser.add_argument("--scene-mode", default="")
    args = parser.parse_args()
    asyncio.run(run(args.resume, args.scene_mode))
