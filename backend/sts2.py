"""Slay the Spire 2 domain knowledge: classes, card types, sizes, and the art-style rules that
were learned the hard way while training and testing the style LoRA."""

# Built-in classes the LoRA learned (used by the example prompts). Cards belong to MOD characters, whose trigger is
# their own id; a card's colours come from its theme colour (default: the character colour).
CLASSES = {
    # id: (display name, frame colour, darker trim, palette hint used by the offline prompt builder)
    "ironclad":    ("Ironclad / 铁甲战士",   "#b3322b", "#5c1510", "crimson red and burnt orange"),
    "silent":      ("Silent / 静默猎手",     "#3f8a3a", "#1c4219", "poison green and deep teal"),
    "defect":      ("Defect / 故障机器人",   "#2f6fb5", "#15345a", "electric blue and cyan"),
    "regent":      ("Regent / 储君",         "#d08a1e", "#6b420a", "royal gold and starry orange"),
    "necrobinder": ("Necrobinder / 亡灵契约师", "#8e4aa8", "#40184f", "ghostly violet and bone white"),
    "colorless":   ("Colorless / 无色",      "#8a8a8a", "#3d3d3d", "deep teal and warm amber"),
}

CARD_TYPES = {
    # id: (display, composition hint for the offline prompt builder)
    "attack": ("Attack / 攻击", "a dynamic close-up of a strike or weapon impact, diagonal motion, flying debris"),
    "skill":  ("Skill / 技能",  "a hand, tool or gesture performing a trick or defensive move, one clear object in focus"),
    "power":  ("Power / 能力",  "an iconic emblem or a figure surrounded by a radiating aura, symmetrical and centered"),
    "status": ("Status / 状态", "a simple symbolic object, slightly broken or dirty"),
    "curse":  ("Curse / 诅咒",  "an ominous symbolic object wrapped in dark smoke"),
}

RARITIES = {
    "basic":    ("Basic / 基础",    "#9a9a9a"),
    "common":   ("Common / 普通",   "#b8b8b8"),
    "uncommon": ("Uncommon / 罕见", "#5aa0e6"),
    "rare":     ("Rare / 稀有",     "#e8c252"),
    "ancient":  ("Ancient / 远古",  "#e07bd8"),
    "special":  ("Special / 特殊",  "#6fd3c2"),
}

# Native STS2 portrait sizes (from the game files): 1000x760 for normal cards, 606x852 for ancient / full-art
# cards. Generation happens at a model-friendly size; export center-crops to the aspect and resizes (Lanczos).
SIZE_PRESETS = {
    "sts2_card":    {"label": "STS2 card portrait 1000×760",          "gen": (1024, 768), "out": (1000, 760)},
    "draft":        {"label": "Draft 768×576 (~1.5x faster, then Refine the best)", "gen": (768, 576), "out": (1000, 760)},
    "sts2_ancient": {"label": "STS2 ancient / full art 606×852",      "gen": (768, 1072), "out": (606, 852)},
    "mod_card":     {"label": "724×543",                              "gen": (1024, 768), "out": (724, 543)},
    "small":        {"label": "512×384",                              "gen": (1024, 768), "out": (512, 384)},
    "hires":        {"label": "1280×960 (flatter look, hotter GPU)",  "gen": (1280, 960), "out": (1280, 960)},
    "raw":          {"label": "1024×768 (no resize)",                 "gen": (1024, 768), "out": (1024, 768)},
}

TRIGGER = "sts2 card art, {cls} card."


def class_info(cls):
    """MOD character (or built-in class) -> trigger word, palette, appearance and reference image.
    A MOD character's trigger is its own id ("sts2 card art, illusionist card."): borrowing a built-in class's
    trigger would also borrow what the LoRA learned about that class (e.g. Necrobinder's skeletal hands)."""
    if cls in CLASSES:
        return {"id": cls, "name": CLASSES[cls][0], "trigger": cls, "palette": CLASSES[cls][3], "color": CLASSES[cls][1],
                "appearance": "", "ref": "", "use_ref": False}
    import store
    ch = store.get_character(cls)
    if not ch:
        return {"id": cls, "name": cls, "trigger": cls or "mod", "palette": NEUTRAL_PALETTE, "color": NEUTRAL_COLOR,
                "appearance": "", "ref": "", "use_ref": False}
    return {"id": cls, "name": ch["name"] or cls, "trigger": cls, "palette": ch["palette"] or NEUTRAL_PALETTE,
            "color": ch["color"] or NEUTRAL_COLOR,
            "appearance": ch["appearance"], "ref": ch["ref_image"], "use_ref": bool(ch["use_ref"])}


NEUTRAL_PALETTE = "deep teal and warm amber"
NEUTRAL_COLOR = "#2a8a8a"

# hue (degrees, upper bound) -> name; used to turn a card's theme colour into words the image model understands
_HUES = [(10, "crimson red"), (22, "scarlet"), (38, "orange"), (50, "amber"), (62, "golden yellow"), (75, "yellow"),
         (100, "lime green"), (145, "emerald green"), (172, "teal"), (192, "cyan"), (215, "azure blue"),
         (240, "royal blue"), (258, "indigo"), (282, "violet"), (305, "purple"), (330, "magenta"),
         (348, "rose pink"), (360, "crimson red")]


def _rgb(hex_color):
    h = (hex_color or "").strip().lstrip("#")
    try:
        return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)) if len(h) == 6 else None
    except ValueError:
        return None


def color_words(hex_color):
    """'#b3322b' -> 'deep crimson red'."""
    import colorsys
    rgb = _rgb(hex_color)
    if rgb is None:
        return "deep teal"
    hue, light, sat = colorsys.rgb_to_hls(*rgb)
    if sat < 0.15 or light < 0.06:
        return "charcoal black" if light < 0.2 else "slate gray" if light < 0.55 else "pale silver"
    name = next(n for limit, n in _HUES if hue * 360 <= limit)
    adj = ("deep" if light < 0.24 else "dark" if light < 0.34 else "vivid" if light < 0.62
           else "bright" if light < 0.78 else "pale")
    if sat < 0.35:
        adj = "muted"
    return f"{adj} {name}"


def theme_palette(hex_color):
    """The theme colour plus a darker analogous hue: a two-tone background like the game's own cards."""
    import colorsys
    rgb = _rgb(hex_color)
    if rgb is None:
        return NEUTRAL_PALETTE
    hue, light, sat = colorsys.rgb_to_hls(*rgb)
    second = colorsys.hls_to_rgb((hue + 0.09) % 1, max(0.14, light * 0.55), sat)
    second_hex = "#" + "".join(f"{round(c * 255):02x}" for c in second)
    first, other = color_words(hex_color), color_words(second_hex)
    if other == first:
        other = "charcoal black"
    return f"{first} and {other}"


def card_theme(card):
    """A card's colour theme: its own "theme_color" param, else its character's colour."""
    params = (card or {}).get("params") or {}
    if _rgb(params.get("theme_color")):
        return params["theme_color"]
    return class_info((card or {}).get("cls") or "")["color"]


def card_refs(card):
    """Reference images a card is generated with: its own "refs" param when set (an empty list = none),
    otherwise its character's reference image when the character has that enabled."""
    params = (card or {}).get("params") or {}
    if "refs" in params:
        return [r for r in (params["refs"] or []) if r]
    info = class_info((card or {}).get("cls") or "")
    return [info["ref"]] if info["use_ref"] and info["ref"] else []

# From lora/best_prompts.csv: cfg 3.0 + this negative + this detail suffix gave the best results.
# (At the template default cfg 1.0 the model largely ignores the prompt.)
DEFAULT_NEGATIVE = ("flat, plain, simple, blurry, smooth gradients, empty background, low detail, "
                    "deformed, extra limbs")
DEFAULT_STYLE_SUFFIX = ("Highly detailed: crisp faceted shapes, hard-edged cast shadows, a thin bright rim-light "
                        "outline tracing the subject, layered highlights, small flying chips and sparks, "
                        "few flat colors, no gradients.")

DEFAULT_LORA = "deckbuilder_cardart_style_lora_v1_fp16.safetensors"

# Instructions for the LLM prompt writer. Editable in Settings; {lang} = language of the "notes" line.
DEFAULT_PROMPT_SYSTEM = """\
You write image prompts for Qwen-Image-2.1 with a style LoRA that paints Slay the Spire 2 card art.
The prompt is rendered as-is, so it must be complete and richly detailed.

Format and level of detail (these examples only show the FORMAT; never reuse their subjects, objects or colours):
- sts2 card art, warrior card. A spiked mace crashes down with force, embedding itself into a rocky surface and \
sending shards of stone flying outward. The impact is highlighted by a burst of fiery orange and yellow energy \
radiating from the point of contact. The mace's metallic gray head glows with heat, with chipped and nicked metal \
edges, rivets and worn scratches, and a thin bright red outline tracing the weapon, framed tightly on the moment of \
impact. Deep purple and maroon background.
- sts2 card art, rogue card. A hooded green-cloaked hand thrusts a curved dagger forward, the blade glinting with a \
bright white sparkle and leaving a poison-green trail. Leather wraps around the wrist, droplets of venom fly from the \
edge, and a thin yellow rim light traces the knuckles and the blade. Yellow and olive background.

Rules:
- Start with exactly "sts2 card art, <trigger> card." using the trigger given with the card.
- Then 3-5 plain English sentences, 60-120 words, in this order:
  1. ONE focal subject doing ONE clear action that expresses what the card does, framed close;
  2. concrete visible details: materials, parts, textures and wear ("chipped metal edges", "rivets",
     "frayed cloth") - name every visible part of complex objects, or the model leaves parts out;
  3. light and effects: glows, sparks, trails, flying debris, a thin bright rim-light outline and its colour;
  4. exactly ONE final background sentence: one or two saturated colours that suit the card. No scenery.
- Follow the author's art concept faithfully and translate it into English.
- If the card's mod character appears, describe them ONLY with the given appearance (or "the character from
  <image1>" when a reference image is attached). Never invent anatomy or traits: no skeletal, robotic, animal
  or monstrous features unless the appearance says so.
- Do not add extra characters, text, letters, card frames or UI.
- Do not add style or quality words (no "cel shading", "illustration", "highly detailed", "masterpiece"):
  a fixed style suffix is appended automatically.

"notes" is one short line in {lang} describing the visual idea.
"""

# Offline prompt (no AI). Placeholders: {trigger} {concept} {name} {effect} {palette} {character}
DEFAULT_PROMPT_TEMPLATE = "sts2 card art, {trigger} card. {concept}. {palette} background."
