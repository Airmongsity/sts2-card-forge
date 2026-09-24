"""Writes chosen card art to a mod folder at the game's native portrait sizes."""
import re
import unicodedata
from pathlib import Path

from PIL import Image

import config
import store
import sts2


def slugify(text, fallback):
    text = unicodedata.normalize("NFKD", text or "").encode("ascii", "ignore").decode()
    text = re.sub(r"[^a-zA-Z0-9]+", "_", text).strip("_").lower()
    return text or fallback


def card_slug(card):
    return slugify(card.get("slug") or card.get("name"), f"card_{card['id']}")


def fit(img, size):
    """Center-crop to the target aspect ratio, then resize with Lanczos."""
    tw, th = size
    w, h = img.size
    if w * th > h * tw:                       # too wide
        nw = round(h * tw / th)
        img = img.crop(((w - nw) // 2, 0, (w - nw) // 2 + nw, h))
    elif w * th < h * tw:                     # too tall
        nh = round(w * th / tw)
        img = img.crop((0, (h - nh) // 2, w, (h - nh) // 2 + nh))
    return img.resize(size, Image.LANCZOS) if img.size != size else img


def out_size(preset=None, width=None, height=None):
    if width and height:
        return int(width), int(height)
    return sts2.SIZE_PRESETS.get(preset or "sts2_card", sts2.SIZE_PRESETS["sts2_card"])["out"]


def export_cards(card_ids, folder=None, by_class=False, preset=None, width=None, height=None):
    """Export each card's selected image. preset=None uses each card's own size preset."""
    settings = config.load()
    folder = Path(folder or settings["export_dir"])
    written, skipped = [], []
    for cid in card_ids:
        card = store.get_card(cid)
        if not card:
            continue
        img_rec = store.get_image(card["selected_image"]) if card["selected_image"] else None
        if not img_rec or not Path(img_rec["path"]).exists():
            skipped.append(card["name"] or f"#{cid}")
            continue
        card_preset = preset or (card.get("params") or {}).get("size_preset") or settings["gen"]["size_preset"]
        size = out_size(card_preset, width, height)
        dst_dir = folder / card["cls"] if by_class else folder
        dst_dir.mkdir(parents=True, exist_ok=True)
        dst = dst_dir / f"{card_slug(card)}.png"
        with Image.open(img_rec["path"]) as im:
            fit(im.convert("RGB"), size).save(dst)
        written.append(str(dst))
    return {"written": written, "skipped": skipped, "folder": str(folder)}


def export_image(image_id, dst, preset=None, width=None, height=None):
    rec = store.get_image(image_id)
    size = out_size(preset or rec["params"].get("size_preset"), width, height)
    Path(dst).parent.mkdir(parents=True, exist_ok=True)
    with Image.open(rec["path"]) as im:
        fit(im.convert("RGB"), size).save(dst)
    return str(dst)
