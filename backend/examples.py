"""Seeds an example project (characters, cards and their finished art) on first run, from examples/cards.json."""
import json
import shutil

from PIL import Image

import config
import store

PROJECT = "示例 Examples"


def seed_once():
    if config.load().get("examples_seeded"):
        return
    src = config.ROOT / "examples"
    try:
        spec = json.loads((src / "cards.json").read_text(encoding="utf-8"))
    except FileNotFoundError:
        return
    for ch in spec["characters"]:
        if not store.get_character(ch["id"]):
            store.save_character(ch["id"], ch)
    dst = config.IMAGES / "examples"
    dst.mkdir(parents=True, exist_ok=True)
    for c in spec["cards"]:
        card = store.create_card({"project": PROJECT, **{k: v for k, v in c.items() if k != "images"}})
        for img in c["images"]:
            path = dst / img["file"]
            shutil.copy2(src / img["file"], path)
            with Image.open(path) as im:
                w, h = im.size
            store.add_image(card["id"], path, w, h, img["seed"], img["prompt"], img["params"])
    config.save({"examples_seeded": True})
