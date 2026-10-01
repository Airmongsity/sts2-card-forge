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

# The frame colour identifies a class in the UI; it is not meant to flood every illustration.  Native card art
# usually picks a dark/environment colour and, at most, one brighter effect colour from a wider family.
CLASS_COLOR_POOLS = {
    # Extracted from 550 release portraits in sts2-card-portraits (beta/ excluded), clustering each card first so
    # a few images with large flat backgrounds cannot dominate the class. Ordered dark/environment -> bright/effect.
    "ironclad":    ["#311c31", "#5f2022", "#9c2d1d", "#61309f", "#d86823", "#e9c85a"],
    "silent":      ["#371935", "#7e1a18", "#546553", "#643192", "#c54122", "#869766", "#cbcb48"],
    "defect":      ["#36223c", "#962820", "#603aaa", "#b58b38", "#5891bf", "#9addde", "#e3d251"],
    "regent":      ["#2c2138", "#632323", "#472784", "#5e6fab", "#b8481c", "#7fcbd4", "#e3c652"],
    "necrobinder": ["#3e1f2d", "#381b66", "#a12523", "#7e8c9a", "#763ebe", "#e6a749", "#a1d9c5"],
    "colorless":   ["#372436", "#812325", "#53685e", "#5c2ba4", "#cf4a1f", "#7182d1", "#d7c64e", "#93d1ca"],
}

# Optional per-card override for the prompt writer's composition router.  "auto" deliberately stores no
# instruction on the card, so existing cards keep the adaptive behaviour and only exceptions need configuration.
SCENE_MODES = {
    "auto":         ("Auto / 自动", ""),
    "action":       ("Action / 动作", "A. ACTION / IMPACT"),
    "process":      ("Process / 过程", "B. PROCESS / TRANSFORMATION"),
    "icon":         ("Portrait / 肖像", "C. ICON / PORTRAIT / POWER"),
    "object":       ("Object / 物件", "D. OBJECT / STILL LIFE / STATUS"),
    "environment":  ("Environment / 环境", "E. ENVIRONMENT / ATMOSPHERE"),
    "relationship": ("Relationship / 关系", "F. RELATIONSHIP / CHOICE"),
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
    "sts2_ancient": {"label": "STS2 ancient / full art 606×852",      "gen": (768, 1072), "out": (606, 852)},
    "mod_card":     {"label": "724×543",                              "gen": (1024, 768), "out": (724, 543)},
    "small":        {"label": "512×384",                              "gen": (1024, 768), "out": (512, 384)},
    "hires":        {"label": "1280×960 (flatter look, hotter GPU)",  "gen": (1280, 960), "out": (1280, 960)},
    "raw":          {"label": "1024×768 (no resize)",                 "gen": (1024, 768), "out": (1024, 768)},
}

# Keep the render style separate from the physical medium.  The LoRA is loaded by the
# generation graph, so the text prompt must not teach Qwen that a literal card is a
# subject in the scene.
TRIGGER = "sts2 illustration, {cls}."


def class_info(cls):
    """MOD character (or built-in class) -> trigger word, palette, appearance and reference image.
    A MOD character's trigger is its own id ("sts2 illustration, illusionist."): borrowing a built-in class's
    trigger would also borrow what the LoRA learned about that class (e.g. Necrobinder's skeletal hands)."""
    if cls in CLASSES:
        return {"id": cls, "name": CLASSES[cls][0], "trigger": cls, "palette": CLASSES[cls][3], "color": CLASSES[cls][1],
                "colors": CLASS_COLOR_POOLS.get(cls, [CLASSES[cls][1]]),
                "appearance": "", "ref": "", "use_ref": False}
    import store
    ch = store.get_character(cls)
    if not ch:
        return {"id": cls, "name": cls, "trigger": cls or "mod", "palette": NEUTRAL_PALETTE, "color": NEUTRAL_COLOR,
                "colors": [NEUTRAL_COLOR], "appearance": "", "ref": "", "use_ref": False}
    colors = valid_colors(ch.get("colors")) or default_color_pool(ch.get("color"))
    return {"id": cls, "name": ch["name"] or cls, "trigger": cls, "palette": ch["palette"] or NEUTRAL_PALETTE,
            "color": ch["color"] or NEUTRAL_COLOR, "colors": colors,
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


def valid_colors(colors):
    """Normalise a persisted colour list, keeping order and removing invalid/duplicate entries."""
    result = []
    for color in colors or []:
        color = str(color).strip().lower()
        if _rgb(color) and color not in result:
            result.append(color)
    return result


def default_color_pool(hex_color):
    """Give legacy single-colour characters a useful starter pool without changing an intentional saved pool."""
    import colorsys
    rgb = _rgb(hex_color)
    if rgb is None:
        return [NEUTRAL_COLOR]
    hue, light, sat = colorsys.rgb_to_hls(*rgb)
    sat = max(0.45, sat)
    variants = [
        (hue, light, sat),
        ((hue + 0.075) % 1, max(0.20, light * 0.68), min(0.90, sat * 0.95)),
        ((hue - 0.065) % 1, min(0.72, max(0.42, light * 1.18)), min(0.88, sat * 0.86)),
        ((hue + 0.48) % 1, min(0.64, max(0.38, light)), max(0.42, sat * 0.64)),
    ]
    return valid_colors(["#" + "".join(f"{round(c * 255):02x}" for c in colorsys.hls_to_rgb(*v)) for v in variants])


def color_words(hex_color):
    """'#b3322b' -> 'deep crimson red'."""
    import colorsys
    rgb = _rgb(hex_color)
    if rgb is None:
        return "deep teal"
    hue, light, sat = colorsys.rgb_to_hls(*rgb)
    if sat < 0.15 or light < 0.12:          # near-black reads as black whatever its tint
        return "charcoal black" if light < 0.2 else "slate gray" if light < 0.55 else "pale silver"
    name = next(n for limit, n in _HUES if hue * 360 <= limit)
    adj = ("deep" if light < 0.24 else "dark" if light < 0.34 else "vivid" if light < 0.62
           else "bright" if light < 0.78 else "pale")
    if sat < 0.35:
        adj = "muted"
    return f"{adj} {name}"


def theme_palette(colors):
    """One theme colour -> it plus a darker analogous hue (a two-tone background like the game's own cards).
    Several colours (the first dominant, the rest accents) -> their names in order."""
    import colorsys
    colors = [c for c in ([colors] if isinstance(colors, str) else colors or []) if _rgb(c)]
    if not colors:
        return NEUTRAL_PALETTE
    if len(colors) > 1:
        words = list(dict.fromkeys(color_words(c) for c in colors))
        return words[0] if len(words) == 1 else ", ".join(words[:-1]) + " and " + words[-1]
    hue, light, sat = colorsys.rgb_to_hls(*_rgb(colors[0]))
    second = colorsys.hls_to_rgb((hue + 0.09) % 1, max(0.14, light * 0.55), sat)
    second_hex = "#" + "".join(f"{round(c * 255):02x}" for c in second)
    first, other = color_words(colors[0]), color_words(second_hex)
    if other == first:
        other = "charcoal black"
    return f"{first} and {other}"


def explicit_card_themes(card):
    """Return only a card's explicit override (including the legacy single-colour field)."""
    params = (card or {}).get("params") or {}
    colors = valid_colors(params.get("theme_colors"))
    if not colors and _rgb(params.get("theme_color")):
        colors = [params["theme_color"].lower()]
    return colors


def character_color_pool(cls):
    return valid_colors(class_info(cls or "").get("colors")) or [NEUTRAL_COLOR]


def card_themes(card):
    """Colours displayed by the editor: an explicit card override, otherwise the character's whole pool."""
    return explicit_card_themes(card) or character_color_pool((card or {}).get("cls") or "")


def choose_card_themes(card, rng=None):
    """Freeze an inherited pool to one colour; an explicit user override always wins unchanged."""
    explicit = explicit_card_themes(card)
    if explicit:
        return explicit
    import random
    pool = character_color_pool((card or {}).get("cls") or "")
    picker = rng or random
    # Controlled Qwen-Image 2.1 tests showed that a second prescribed colour tends to occupy a whole region (usually
    # the background), while one anchor still lets the model introduce supporting hues naturally. Near-black clusters
    # are shared shadow language rather than useful class identity, so sample from the brighter half when possible.
    import colorsys
    ordered = sorted(pool, key=lambda c: colorsys.rgb_to_hls(*_rgb(c))[1])
    split = max(1, len(ordered) // 2)
    bright = ordered[split:]
    return [picker.choice(bright or ordered)]


def card_scene_mode(card):
    """Return a validated per-card scene grammar override; unknown/old values safely fall back to auto."""
    mode = ((card or {}).get("params") or {}).get("scene_mode") or "auto"
    return mode if mode in SCENE_MODES else "auto"


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
# The same suffix per prompt language. Chinese was tested against English on the same seeds (2026-09-25): same
# composition, style and detail, so prompts can be written in the user's own language.
STYLE_SUFFIXES = {
    "en": DEFAULT_STYLE_SUFFIX,
    "zh": "细节丰富：清晰的块面造型，硬边投影，一道细亮的轮廓光勾勒主体，层叠的高光，飞溅的碎屑与火花，少量平涂色块，没有渐变。",
    "ja": "細部まで描き込む：くっきりした面の造形、硬いエッジの影、主題をなぞる細く明るいリムライト、重なったハイライト、"
          "飛び散る破片と火花、少ない平塗りの色、グラデーションなし。",
}

DEFAULT_LORA = "deckbuilder_cardart_style_lora_v1_fp16.safetensors"

# Instructions for the LLM prompt writer. Editable in Settings; {lang} = language of the "notes" line,
# {prompt_lang} = language of the prompt itself (the user's own language by default: Qwen-Image-2.1 reads Chinese as
# well as English, so the prompt is readable at a glance and editable without translating).
DEFAULT_PROMPT_SYSTEM = """\
You create structured scene plans for Qwen-Image-2.1 with a style LoRA for small-format game illustrations.
The application, not you, compiles the plan into the final prompt. Make every field visually explicit and economical:
visual hierarchy and scene logic matter more than decorative detail. Never force an action into a concept that is
naturally static. Do not place prose outside the requested JSON schema and do not write a finished prompt.

Rules:
- Set "grammar" to exactly ONE scene grammar. The authored art concept overrides the gameplay category; it is only
  a weak hint:
  A. ACTION / IMPACT - actor or object -> motion/contact -> target -> visible result;
  B. PROCESS / TRANSFORMATION - source -> visible transition path or shared boundary -> target's intermediate state;
  C. ICON / PORTRAIT / POWER - one centered subject -> distinctive pose or condition -> one supporting motif/aura;
  D. OBJECT / STILL LIFE / STATUS - one hero object -> arrangement or physical condition -> one local effect;
  E. ENVIRONMENT / ATMOSPHERE - one focal place -> foreground/midground depth -> one anomaly or mood cue;
  F. RELATIONSHIP / CHOICE - two readable subjects or symbols -> one clear spatial relationship, with no forced motion.
  A short or abstract concept is missing scene structure, not decoration: add only the links required by its chosen
  grammar. Never pad it with extra costume texture, debris, sparks, unrelated props or an invented action.
- Fill the schema in {prompt_lang}. "composition" contains crop/viewpoint and hierarchy. Give every visible subject a
  stable id, semantic kind, short label, literal count, concrete appearance, optional exact <imageN> reference, action,
  and state. Put subject-to-subject spatial or causal links in "relations". Put only light direction/contrast in
  "lighting", and only depth/environment in "background". The application appends the render colour separately, so do not
  choose palette, background, rim-light or effect hues and do not write hex values. A colour intrinsic to the named
  subject may appear only when the art concept explicitly requires that identity.
- Use concrete spatial language (foreground/background, behind a shoulder, partially overlapping, diagonal path,
  cropped at the waist). Give dynamic modes one readable visual path; give static modes one stable visual anchor.
- Follow the author's art concept faithfully and write it in {prompt_lang}.
- Treat the illustration container and all game metadata as context, never as visible scene subjects. Every tangible
  prop in "subjects" must be grounded in the authored art concept, supplied appearance, user feedback, or an attached
  reference description. A scene grammar may add a non-tangible effect needed to make a requested process visible,
  but it must not add a new prop merely to fill the composition.
- If the configured mod character appears, describe them ONLY with the given appearance (or the specific <imageN> tag
  identified as the character reference, in {prompt_lang}). Keep every supplied <imageN> tag exactly as written and
  bind it only to its declared role (character, enemy, ally, object, scene or style). For an appearance reference,
  do not enumerate its costume. Never invent anatomy, traits or a digit count: include a nonstandard or
  exact count only when the supplied character appearance or art concept explicitly gives one.
- The image model does not understand negation: never write what must NOT appear ("no mirror", "not looking
  into a mirror", "without a crown") - that word alone makes it appear. Leave it out entirely, together with words
  that evoke it (e.g. no "mirror", "reflection" or "symmetry" when the concept forbids mirrors).
- Spend a limited visual-complexity budget. When the concept already needs several figures, transparency, overlap
  or an unusual viewpoint, simplify the pose, hands and costume detail instead of stacking more fragile demands.
- For creation, transformation, repair, growth, summoning or copying, show the PROCESS rather than only the finished
  result. If the authored concept names a tool, surface, container or device, it may serve as the visible mechanism.
  Otherwise connect the existing source and ONE target directly with a non-tangible transition path, shared boundary,
  flow, trail, distortion or gradual emergence; do not invent a tangible intermediary prop. The target must contain a
  clean boundary between two states (drawn/unfilled, solid/transparent, old/new, intact/damaged, lit/extinguishing,
  dormant/growing).
  For a vanishing or extinguishing process, retain one small remnant of the earlier state beside its emerging result
  (for example a collapsing flame flowing into smoke), so the picture captures the transition rather than only its
  completed aftermath. Keep the boundary simple and continuous; do not express it with broken body fragments.
- For clones and doubles, keep ONE detailed primary figure. The emerging copy is a clean, simplified spectral
  silhouette identified by two or three strong features. Prefer a tight overlapping or triangular composition, with
  the copy's hands hidden and both figures cropped before difficult lower-body anatomy. Do not demand pixel-identical
  faces, outfits or poses, and do not arrange completed figures side by side or top and bottom.
  Words such as "mirror image", "mirrored self" or “镜像” describe a duplicate unless the authored concept explicitly
  names a physical mirror or reflective surface. Never turn an abstract double into a card, framed panel, printed
  picture, flat rectangular sheet, portal, screen or other intermediary object.
- Give transparent subjects a simple opacity hierarchy (readable outer silhouette, faint interior, dissolving edge).
  Do not ask for fine facial, fabric or accessory detail to remain crisp through transparency.
- Hands are not a default process mechanism. Unless the authored concept explicitly names a hand gesture, keep hands
  concealed by sleeves, behind the transition, or outside the crop, and express the process through the subjects'
  shared boundary or non-tangible flow. When a visible gesture is explicitly needed, use at most one large foreground
  hand in a three-quarter or profile view; reserve a front-facing open palm for concepts whose entire subject is a hand.
- Keep the subject readable: if the subject is dark (black clothing, shadow), the background must be a deep
  saturated colour, never black or charcoal, and name a bright rim light.
- Keep the render limited to the planned subjects and their stated environment; do not append decorative overlays.
- Do not add style or quality words (no "cel shading", "illustration", "highly detailed", "masterpiece"):
  a fixed style suffix is appended automatically.

"notes" is one short line in {lang}, beginning with the chosen grammar letter and name (for example "D OBJECT —"),
then describing the composition. This makes the routing decision visible to the user.
"""

# Offline prompt (no AI). Placeholders: {trigger} {concept} {name} {effect} {palette} {character}
DEFAULT_PROMPT_TEMPLATE = "sts2 illustration, {trigger}. {concept}. Dramatic high-contrast background."
