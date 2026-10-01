"""A/B benchmark for prompt compilation styles: structured (list-style) vs prose (flowing descriptive).

Compares the two compile_scene_plan templates using the same fixed plan and seeds, generating images via cloud API
(since this validates cloud rendering quality). Results saved to data/experiments/ for manual review.

This calls a paid cloud API — run manually when ready, not automatically in CI.
"""
from __future__ import annotations

import argparse
import asyncio
import io
import json
import sys
import time
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))

import cloud_image
import config
import promptgen
import sts2


SEEDS = [486112413528211, 729666335253144, 848889539631750, 382662938299715]

# Fixed scene plan to compare compilation only (not LLM variance)
FIXED_PLAN = {
    "composition": "A hooded figure thrusts a curved dagger forward, frame-filling close-up crop at the hand and blade",
    "subjects": [
        {
            "id": "dagger",
            "kind": "weapon",
            "label": "curved dagger",
            "count": 1,
            "action": "thrusts forward toward viewer",
            "appearance": "blade glinting with bright white sparkle",
            "state": "poison-green trail flowing from the blade tip",
            "reference": ""
        },
        {
            "id": "hand",
            "kind": "body_part",
            "label": "hand",
            "count": 1,
            "action": "gripping the dagger handle",
            "appearance": "green cloak sleeve partially covering the wrist",
            "state": "tense grip",
            "reference": ""
        }
    ],
    "relations": [],
    "lighting": "thin bright rim-light outline tracing the blade and hand edges",
    "background": "yellow and olive flat color background, no gradients",
    "grammar": "ICON",
    "notes": ""
}

CARD = {
    "cls": "silent",
    "name": "Poisoned Strike",
    "concept": "A swift dagger attack with poison",
    "params": {}
}


async def run(args):
    settings = config.load()
    if settings.get("image_provider", "comfy") == "comfy":
        print("Error: This benchmark requires a cloud image provider (not local ComfyUI).")
        print("Configure a cloud provider in Setup > Image generation service, then try again.")
        return

    output = config.DATA / "experiments" / f"prompt_style_{int(time.time())}"
    output.mkdir(parents=True, exist_ok=True)

    prompt_lang = config.prompt_lang(settings)

    # Compile both templates
    structured_prompt, _ = promptgen.compile_scene_plan_structured(FIXED_PLAN, CARD, prompt_lang)
    prose_prompt, _ = promptgen.compile_scene_plan_prose(FIXED_PLAN, CARD, prompt_lang)

    # Apply theme colors
    structured_prompt = promptgen._append_theme(structured_prompt, CARD, prompt_lang)
    prose_prompt = promptgen._append_theme(prose_prompt, CARD, prompt_lang)

    # Apply style suffix
    style_suffix = settings["gen"].get("style_suffix", "")
    if style_suffix:
        structured_prompt = f"{structured_prompt} {style_suffix}"
        prose_prompt = f"{prose_prompt} {style_suffix}"

    print(f"Structured prompt:\n{structured_prompt}\n")
    print(f"Prose prompt:\n{prose_prompt}\n")

    manifest = {
        "benchmark": "prompt_style_ab",
        "timestamp": time.time(),
        "fixed_plan": FIXED_PLAN,
        "card": CARD,
        "structured_prompt": structured_prompt,
        "prose_prompt": prose_prompt,
        "seeds": args.seeds,
        "image_provider": settings.get("image_provider"),
        "image_model": settings.get("image_api_model"),
        "images": []
    }

    for seed in args.seeds:
        for variant, prompt in [("structured", structured_prompt), ("prose", prose_prompt)]:
            print(f"Generating {variant} seed {seed}...")
            started = time.time()
            try:
                params = {
                    "prompt": prompt,
                    "negative": settings["gen"].get("negative", ""),
                    "seed": seed,
                    "width": 1280,
                    "height": 960,
                    "steps": settings["gen"].get("steps", 25),
                    "cfg": settings["gen"].get("cfg", 3.0),
                    "image_preset": settings.get("image_preset"),
                    "image_api_base_url": settings.get("image_api_base_url"),
                    "image_api_model": settings.get("image_api_model"),
                    "image_api_key": settings.get("image_api_key"),
                    "image_api_quality": settings.get("image_api_quality", "auto"),
                }
                results = await cloud_image.generate(params, settings)
                data = results[0]

                img = Image.open(io.BytesIO(data))
                img.load()
                path = output / f"{seed}_{variant}.png"
                path.write_bytes(data)

                manifest["images"].append({
                    "variant": variant,
                    "seed": seed,
                    "path": str(path),
                    "seconds": round(time.time() - started, 2),
                    "width": img.width,
                    "height": img.height
                })
                (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

            except Exception as e:
                print(f"Failed: {e}")
                manifest["images"].append({
                    "variant": variant,
                    "seed": seed,
                    "error": str(e),
                    "seconds": round(time.time() - started, 2)
                })
                (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"\nResults saved to: {output}")
    print(f"Review the images to compare structured vs prose prompt compilation quality.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--seeds", nargs="+", type=int, default=SEEDS)
    asyncio.run(run(parser.parse_args()))


if __name__ == "__main__":
    main()
