"""Turns card data into image prompts (and brainstorms new cards) with an LLM.

Providers: "anthropic" (Claude, official SDK), "openai" (any OpenAI-compatible server such as Ollama or
LM Studio, for fully local use) and "template" (offline, rule-based fallback)."""
import json
import re

import aiohttp

import sts2
from config import tr

LANG_NAMES = {"zh": "Simplified Chinese", "en": "English", "ja": "Japanese"}

_THEME_FOOTER = re.compile(
    r"\s*(?:Required theme colou?rs?:\s*[^.。]*(?:[.。])?|主题色必须使用：[^。]*(?:。)?|"
    r"テーマカラー：[^。]*(?:。)?)\s*$", re.IGNORECASE)

PLAN_SCHEMA = {
    "type": "object",
    "properties": {
        "grammar": {"type": "string", "enum": ["action", "process", "icon", "object", "environment", "relationship"]},
        "composition": {"type": "string", "description": "Crop, viewpoint, focal hierarchy, and spatial layout."},
        "subjects": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "id": {"type": "string"},
                    "kind": {"type": "string", "enum": ["character", "creature", "object", "effect", "environment", "symbol"]},
                    "label": {"type": "string", "description": "A short visible noun phrase in the requested prompt language."},
                    "count": {"type": "integer", "minimum": 1, "maximum": 8},
                    "appearance": {"type": "string"},
                    "reference": {"type": "string", "description": "An exact <imageN> tag, or an empty string."},
                    "action": {"type": "string"},
                    "state": {"type": "string"},
                },
                "required": ["id", "kind", "label", "count", "appearance", "reference", "action", "state"],
                "additionalProperties": False,
            },
        },
        "relations": {"type": "array", "items": {"type": "string"}},
        "lighting": {"type": "string"},
        "background": {"type": "string"},
        "notes": {"type": "string", "description": "One short line explaining the visual idea, in the user's language."},
    },
    "required": ["grammar", "composition", "subjects", "relations", "lighting", "background", "notes"],
    "additionalProperties": False,
}

IDEAS_SCHEMA = {
    "type": "object",
    "properties": {
        "cards": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "name": {"type": "string"},
                    "type": {"type": "string", "enum": list(sts2.CARD_TYPES)},
                    "rarity": {"type": "string", "enum": list(sts2.RARITIES)},
                    "cost": {"type": "string"},
                    "description": {"type": "string"},
                    "concept": {"type": "string"},
                },
                "required": ["name", "type", "rarity", "cost", "description", "concept"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["cards"],
    "additionalProperties": False,
}


def _art_system(lang, settings):
    import config
    text = (settings.get("prompt_system") or sts2.DEFAULT_PROMPT_SYSTEM).replace("{lang}", LANG_NAMES.get(lang, "English"))
    text = text.replace("{prompt_lang}", LANG_NAMES.get(config.prompt_lang(settings), "English"))
    if settings.get("image_provider", "comfy") != "comfy":
        model = settings.get("image_api_model") or "the configured cloud image model"
        text += (f"\n\nTARGET RENDERER OVERRIDE: The image will be rendered by {model} through a cloud API, without "
                 "the local STS2 style LoRA. Do not assume trigger words alone carry a learned appearance or style; "
                 "make the visible character traits, rendering style, composition, and lighting explicit in "
                 "ordinary language. The application appends the selected palette separately, so do not choose palette, "
                 "background, rim-light or effect hues in the creative body. Keep any supplied reference-image tag "
                 "exactly as instructed.")
    return text + ("\n\nReturn only the structured scene-plan JSON. Do not write a finished image prompt. "
                   "Use the requested prompt language for all descriptive fields; keep grammar, kind, id and "
                   "<imageN> tags in their specified machine-readable forms.")


def _ideas_system(lang):
    types = ", ".join(sts2.CARD_TYPES)
    rarities = ", ".join(sts2.RARITIES)
    return f"""You design cards for a Slay the Spire 2 mod. Cards follow Slay the Spire conventions: energy cost \
(0-3, X, or "-" for unplayable), a type ({types}), a rarity ({rarities}) and short, precise effect text using the \
game's keywords (Block, Vulnerable, Weak, Exhaust, Retain, Innate, Ethereal, Draw, Discard, Strength, Dexterity, ...).
Make each card mechanically distinct, balanced for its cost and rarity, and fitting the theme.
"concept" is a one-sentence idea for the card's illustration: a single clear focal subject.
Write name, description and concept in {LANG_NAMES.get(lang, 'English')}. Reply with JSON: {{"cards": [...]}}."""


def _card_brief(card, rejected_history=(), feedback=""):
    info = sts2.class_info(card.get("cls") or "colorless")
    concept = card.get("concept")
    fields = {
        # A deliberate art concept is authoritative. Hiding a conflicting or metaphorical title keeps the prompt
        # writer from literalising it (for example, a card named "Mirror" whose concept explicitly forbids one).
        "name": None if concept else card.get("name"), "trigger": info["trigger"],
        "type": None if concept else card.get("type"), "rarity": None if concept else card.get("rarity"),
        "cost": None if concept else card.get("cost"),
        "effect_text": None if concept else card.get("description"), "art_concept": concept,
    }
    brief = json.dumps({k: v for k, v in fields.items() if v}, ensure_ascii=False, indent=1)
    mode = sts2.card_scene_mode(card)
    if mode == "auto":
        brief += "\nScene grammar: AUTO. Choose the single grammar that best fits the art concept."
    else:
        brief += (f"\nRequired scene grammar: {sts2.SCENE_MODES[mode][1]}. This is an author-selected override: "
                  "use this grammar and do not choose another one.")
    if info["appearance"]:
        brief += f"\n\nUse the mod character \"{info['name']}\" when the concept depicts them. Appearance:\n{info['appearance']}"
    if info["palette"] and info["palette"] != sts2.NEUTRAL_PALETTE:
        brief += f"\nThe character's own colours (for their outfit only): {info['palette']}"
    brief += ("\nPALETTE IS APPLIED AFTER PROMPT WRITING. Do not name palette, background, rim-light or effect "
              "colours and do not write hex values in the creative prompt. Describe their visual roles without hues. "
              "Palette words in the current prompt and rejected history are obsolete and must not be copied. A colour "
              "intrinsic to the subject may be retained only when the art concept explicitly requires it.")
    refs = sts2.card_refs(card)
    params = card.get("params") or {}
    ref_assets = params.get("ref_assets") or []
    inherited_character_ref = "refs" not in params and bool(info.get("use_ref") and info.get("ref"))
    if refs:
        if not isinstance(ref_assets, list):
            ref_assets = []
        roles = {"character": "main character appearance", "enemy": "enemy appearance", "ally": "ally appearance",
                 "style": "visual style reference", "scene": "scene/environment reference", "object": "object reference",
                 "reference": "general visual reference for the subject or object named by the art concept"}
        brief += "\nREFERENCE IMAGES (preserve every tag exactly; do not swap subjects):"
        for i, _ in enumerate(refs, 1):
            item = ref_assets[i - 1] if i <= len(ref_assets) and isinstance(ref_assets[i - 1], dict) else {}
            # Legacy/manual file references have no role metadata. Calling the first one a character reference caused
            # object art (for example a card/token image) to be described as a person. Let the authored concept bind it.
            role = str(item.get("role") or ("character" if inherited_character_ref and i == 1 else "reference"))
            name = str(item.get("name") or "").strip()
            brief += f"\n- <image{i}> = {roles.get(role, role)}" + (f" named {name}" if name else "") + "."
            hint = str(item.get("prompt_hint") or "").strip()
            if hint:
                brief += (f"\n  Library visual guidance for <image{i}>: {hint[:1200]}"
                          " Treat this only as guidance for that referenced subject; it must not introduce any "
                          "additional subject or override the authored art concept.")
        brief += ("\nWhen a referenced subject appears, explicitly bind it to its own tag, for example "
                  "'the enemy from <image2>'. Use character references for identity, style references only for "
                  "rendering language, and scene references only for environment/composition. A general reference "
                  "must be bound to the subject/object that the art concept explicitly says it depicts. Never merge the "
                  "identities of different reference images.")
    else:
        brief += ("\nNo reference image is attached: never write <image1>; describe the character only with the "
                  "appearance above (if any).")
    current = (card.get("prompt") or "").strip()
    if current:
        if feedback:
            brief += f"\n\nCURRENT PROMPT TO REVISE:\n{current}"
        else:
            # A plain regenerate means replacement, not revision. Re-injecting the old prompt here preserved exactly
            # the discarded props and composition the user was trying to escape.
            brief += "\n\nA current prompt exists, but the user requested a fresh replacement; its prose is withheld."
    if feedback:
        brief += ("\n\nUSER'S REQUESTED CHANGES:\n" + feedback.strip() +
                  "\nTreat this as the author's latest intent: it overrides conflicting choices in the current or "
                  "rejected prompts and may refine the art concept, but it does not override supplied character facts "
                  "or technical output requirements. Return one coherent, complete scene plan; do not merely append "
                  "the comment to the old plan.")
    history = list(rejected_history)[-8:]
    if history:
        # The full rejected text remains stored on the card, but feeding it back verbatim caused discarded props and
        # legacy phrases such as "card art" to reappear in later plans. Give the planner only the rejection record and
        # the user's actual criticism.
        brief += ("\n\nREJECTED EARLIER ATTEMPTS: the user abandoned these attempts because their results were poor. "
                  "Their prompt prose is intentionally withheld so discarded visual content cannot leak into the new "
                  "plan. Use only the explicit user criticism below:")
        for i, item in enumerate(reversed(history), 1):
            if isinstance(item, dict):
                prompt, note = str(item.get("prompt") or ""), str(item.get("feedback") or "")
            else:
                prompt, note = str(item), ""
            if not prompt.strip():
                continue
            brief += f"\n[{i}] Rejected attempt recorded."
            if note.strip():
                brief += f"\nUser criticism/request at that iteration: {note.strip()[:600]}"
    return brief


_GRAMMARS = {"action", "process", "icon", "object", "environment", "relationship"}


def _clean_clause(value):
    """Keep compiler output to one sentence per slot even when a planner returns prose punctuation."""
    value = re.sub(r"\s+", " ", str(value or "")).strip()
    return value.strip(" 。.!！?？;；,，")


def normalize_scene_plan(raw, card):
    """Validate planner output and deterministically restore authored counts/reference bindings."""
    raw = raw if isinstance(raw, dict) else {}
    plan = {
        "grammar": raw.get("grammar") if raw.get("grammar") in _GRAMMARS else "object",
        "composition": _clean_clause(raw.get("composition")),
        "subjects": [],
        "relations": [_clean_clause(x) for x in (raw.get("relations") or []) if _clean_clause(x)],
        "lighting": _clean_clause(raw.get("lighting")),
        "background": _clean_clause(raw.get("background")),
        "notes": _clean_clause(raw.get("notes")),
    }
    refs = sts2.card_refs(card)
    valid_tags = {f"<image{i}>" for i in range(1, len(refs) + 1)}
    for index, item in enumerate(raw.get("subjects") or []):
        if not isinstance(item, dict):
            continue
        kind = item.get("kind") if item.get("kind") in PLAN_SCHEMA["properties"]["subjects"]["items"]["properties"]["kind"]["enum"] else "object"
        try:
            count = max(1, min(8, int(item.get("count") or 1)))
        except (TypeError, ValueError):
            count = 1
        subject = {
            "id": re.sub(r"[^a-zA-Z0-9_-]", "", str(item.get("id") or f"subject{index + 1}")) or f"subject{index + 1}",
            "kind": kind,
            "label": _clean_clause(item.get("label")) or kind,
            "count": count,
            "appearance": _clean_clause(item.get("appearance")),
            "reference": item.get("reference") if item.get("reference") in valid_tags else "",
            "action": _clean_clause(item.get("action")),
            "state": _clean_clause(item.get("state")),
        }
        plan["subjects"].append(subject)

    # A legacy/manual reference has no role metadata. Bind it to the most likely authored object rather than silently
    # treating it as the character. Explicit planner bindings always win.
    if valid_tags and not any(s["reference"] for s in plan["subjects"]):
        params = card.get("params") or {}
        assets = params.get("ref_assets") or []
        info = sts2.class_info(card.get("cls") or "colorless")
        inherited_character_ref = "refs" not in params and bool(info.get("use_ref") and info.get("ref"))
        first_role = assets[0].get("role") if assets and isinstance(assets[0], dict) else "reference"
        if inherited_character_ref:
            candidate = next((s for s in plan["subjects"] if s["kind"] == "character"), None)
            if candidate:
                candidate["reference"] = "<image1>"
        elif first_role in (None, "", "reference", "object"):
            candidate = next((s for s in plan["subjects"] if s["kind"] in ("object", "symbol")), None)
            if candidate:
                candidate["reference"] = "<image1>"
    return plan


def compile_scene_plan(raw, card, prompt_lang="en", template="structured"):
    """Compile an LLM scene plan into a prompt. Template: 'structured' (list-style for local LoRA) or 'prose' (flowing for cloud)."""
    if template == "prose":
        return compile_scene_plan_prose(raw, card, prompt_lang)
    return compile_scene_plan_structured(raw, card, prompt_lang)


def compile_scene_plan_structured(raw, card, prompt_lang="en"):
    """Compile an LLM scene plan into a stable four-sentence prompt plus the trained LoRA trigger (structured list style)."""
    plan = normalize_scene_plan(raw, card)
    zh = prompt_lang in ("zh", "ja")
    joiner, stop = ("；", "。") if zh else ("; ", ".")

    inventory = []
    for subject in plan["subjects"]:
        ref = f"（依据 {subject['reference']}）" if zh and subject["reference"] else (f" from {subject['reference']}" if subject["reference"] else "")
        inventory.append((f"{subject['count']}个{subject['label']}{ref}" if zh else
                          f"{subject['count']} {subject['label']}{ref}"))
    first_parts = [plan["composition"]]
    if inventory:
        first_parts.append(("画面主体：" if zh else "Focal subjects: ") + ("、" if zh else ", ").join(inventory))

    motion = []
    for subject in plan["subjects"]:
        if subject["action"]:
            motion.append(f"{subject['label']}：{subject['action']}" if zh else f"{subject['label']}: {subject['action']}")
    motion.extend(plan["relations"])

    details = []
    for subject in plan["subjects"]:
        visible = [x for x in (subject["appearance"], subject["state"]) if x]
        if visible:
            details.append((f"{subject['label']}：" if zh else f"{subject['label']}: ") + joiner.join(visible))

    fourth = [x for x in (plan["lighting"], plan["background"]) if x]
    fallbacks = {
        "first": "主体占据清晰视觉中心" if zh else "The focal subject occupies a clear visual center",
        "second": "各主体之间保持清楚可读的空间关系" if zh else "The subjects keep a clear, readable spatial relationship",
        "third": "仅保留识别主体所需的具体形状和材质" if zh else "Only concrete shapes and materials needed to recognize the subject are shown",
        "fourth": "定向高对比光线勾勒主体，背景保留简洁的纵深" if zh else "Directional high-contrast light outlines the subject against a simple background with depth",
    }
    sentences = [joiner.join(x for x in first_parts if x) or fallbacks["first"],
                 joiner.join(motion) or fallbacks["second"],
                 joiner.join(details) or fallbacks["third"],
                 joiner.join(fourth) or fallbacks["fourth"]]
    trigger = sts2.TRIGGER.format(cls=sts2.class_info(card.get("cls") or "colorless")["trigger"])
    return trigger + " " + " ".join(_clean_clause(sentence) + stop for sentence in sentences), plan


def compile_scene_plan_prose(raw, card, prompt_lang="en"):
    """Compile an LLM scene plan into a flowing descriptive prompt (prose style for cloud renderers without LoRA)."""
    plan = normalize_scene_plan(raw, card)
    zh = prompt_lang in ("zh", "ja")

    # Build a single flowing sentence that integrates subjects, actions, and details naturally
    parts = []

    # Start with composition if provided
    if plan["composition"]:
        parts.append(_clean_clause(plan["composition"]))

    # Weave subjects with their actions and appearance into a cohesive description
    for i, subject in enumerate(plan["subjects"]):
        subject_parts = []

        # Count and label
        if subject["count"] == 1:
            article = "a " if not zh and subject["label"][0].lower() not in "aeiou" else "an " if not zh else ""
            subject_parts.append(f"{article}{subject['label']}")
        else:
            subject_parts.append(f"{subject['count']} {subject['label']}" if not zh else f"{subject['count']}个{subject['label']}")

        # Action (integrated into the flow)
        if subject["action"]:
            subject_parts.append(_clean_clause(subject["action"]))

        # Appearance and state details
        visible = [_clean_clause(x) for x in (subject["appearance"], subject["state"]) if x]
        if visible:
            subject_parts.extend(visible)

        # Reference binding
        if subject["reference"]:
            subject_parts.append(f"from {subject['reference']}" if not zh else f"（依据 {subject['reference']}）")

        # Join this subject's description
        if not zh:
            parts.append(", ".join(subject_parts))
        else:
            parts.append("，".join(subject_parts))

    # Add relations as continuation
    for relation in plan["relations"]:
        parts.append(_clean_clause(relation))

    # Lighting and background
    if plan["lighting"]:
        parts.append(_clean_clause(plan["lighting"]))
    if plan["background"]:
        parts.append(_clean_clause(plan["background"]))

    # Join with appropriate separators for a flowing prose style
    if zh:
        prompt_body = "，".join(parts) + "。"
    else:
        # Use commas for most parts, periods for major breaks
        prompt_body = ". ".join([", ".join(parts[:3]) if len(parts) > 2 else ", ".join(parts[:2]),
                                  ", ".join(parts[3:]) if len(parts) > 3 else parts[2] if len(parts) > 2 else ""]).strip(", ") + "."

    trigger = sts2.TRIGGER.format(cls=sts2.class_info(card.get("cls") or "colorless")["trigger"])
    return trigger + " " + prompt_body, plan


def _append_theme(prompt, card, lang):
    """Make the card's exact selected colours survive the creative LLM step deterministically."""
    prompt = _THEME_FOOTER.sub("", prompt).rstrip()
    colors = sts2.explicit_card_themes(card)
    if not colors:  # inherited pools are sampled and appended when the generation job is created
        return prompt.rstrip()
    entries = [f"{sts2.color_words(color)} ({color.lower()})" for color in colors]
    joined = ("、" if lang in ("zh", "ja") else ", ").join(entries)
    label = {"zh": "主题色必须使用：", "ja": "テーマカラー："}.get(lang, "Required theme colours: ")
    return f"{prompt.rstrip()} {label}{joined}。" if lang in ("zh", "ja") else f"{prompt.rstrip()} {label}{joined}."


# ---- providers -------------------------------------------------------------------------------

async def _anthropic(settings, system, user, schema):
    import anthropic  # bundled with releases; setup installs it into ComfyUI's Python for a source checkout

    key = settings.get("anthropic_api_key") or None
    base_url = settings.get("anthropic_base_url") or None
    client = anthropic.AsyncAnthropic(api_key=key, base_url=base_url, timeout=120.0) if key \
        else anthropic.AsyncAnthropic(base_url=base_url, timeout=120.0)
    no_key = tr("Claude API 密钥无效或未设置（设置 → 提示词 AI）", "Claude API key missing or invalid (Settings → Prompt AI)")
    try:
        resp = await client.beta.messages.create(
            model=settings.get("anthropic_model") or "claude-opus-5",
            max_tokens=16000,
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            system=system,
            messages=[{"role": "user", "content": user}],
            output_config={"effort": settings.get("anthropic_effort") or "medium",
                           "format": {"type": "json_schema", "schema": schema}},
        )
    except anthropic.AuthenticationError:
        raise RuntimeError(no_key)
    except TypeError as e:  # raised by the SDK when no credential source is configured at all
        if "authentication" in str(e):
            raise RuntimeError(no_key)
        raise
    except anthropic.RateLimitError:
        raise RuntimeError(tr("Claude API 限流，请稍后重试", "Claude API rate limited, try again later"))
    except anthropic.APIStatusError as e:
        raise RuntimeError(f"Claude API {e.status_code}: {e.message}")
    except anthropic.APIConnectionError:
        raise RuntimeError(tr("无法连接 Claude API，请检查网络/代理", "Cannot reach the Claude API, check network/proxy"))
    finally:
        await client.close()
    if resp.stop_reason == "refusal":
        raise RuntimeError(tr("Claude 拒绝了这个请求，请修改卡牌描述后重试", "Claude declined this request; edit the card text and retry"))
    text = next((b.text for b in resp.content if b.type == "text"), "")
    return json.loads(text)


def _extract_json(text):
    text = re.sub(r"<think>.*?</think>", "", text, flags=re.S).strip()
    m = re.search(r"\{.*\}", text, flags=re.S)
    if not m:
        raise RuntimeError(tr("模型没有返回 JSON：", "The model did not return JSON: ") + text[:300])
    return json.loads(m.group(0))


async def _openai(settings, system, user, schema):
    base = (settings.get("openai_base_url") or "").rstrip("/")
    headers = {"Content-Type": "application/json"}
    if settings.get("openai_api_key"):
        headers["Authorization"] = f"Bearer {settings['openai_api_key']}"
    body = {
        "model": settings.get("openai_model"),
        "messages": [{"role": "system", "content": system + "\nReply with a single JSON object and nothing else. "
                      f"JSON schema: {json.dumps(schema)}"},
                     {"role": "user", "content": user}],
        "response_format": {"type": "json_object"},
    }
    async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=600)) as s:
        for attempt in range(2):
            async with s.post(base + "/chat/completions", json=body, headers=headers) as r:
                if r.status == 400 and attempt == 0:      # some servers reject response_format
                    body.pop("response_format")
                    continue
                if r.status != 200:
                    raise RuntimeError(f"LLM server {r.status}: {(await r.text())[:300]}")
                data = await r.json(content_type=None)
                return _extract_json(data["choices"][0]["message"]["content"])
    raise RuntimeError(tr("LLM 服务器拒绝请求", "The LLM server rejected the request"))


class _Keep(dict):
    def __missing__(self, key):
        return "{" + key + "}"


def template_prompt(card, settings=None):
    """Offline prompt (no LLM) from the editable template. Works best when the concept is written in English."""
    info = sts2.class_info(card.get("cls") or "colorless")
    concept = (card.get("concept") or card.get("name") or "a glowing magical artifact").strip().rstrip(".")
    refs = sts2.card_refs(card)
    ref_assets = (card.get("params") or {}).get("ref_assets") or []
    if refs:
        labels = []
        for i in range(len(refs)):
            item = ref_assets[i] if i < len(ref_assets) and isinstance(ref_assets[i], dict) else {}
            role = str(item.get("role") or "reference")
            name = str(item.get("name") or role).strip()
            labels.append(f"<image{i + 1}> is the {role} reference for {name}")
        concept = f"{concept}. Reference bindings: " + "; ".join(labels)
    template = (settings or {}).get("prompt_template") or sts2.DEFAULT_PROMPT_TEMPLATE
    themes = sts2.explicit_card_themes(card)
    palette = sts2.theme_palette(themes).capitalize() if themes else "A high-contrast limited palette"
    values = _Keep(trigger=info["trigger"], concept=concept[0].upper() + concept[1:], name=card.get("name") or "",
                   effect=card.get("description") or "", palette=palette,
                   character=info["appearance"] or info["name"])
    return " ".join(template.format_map(values).split())


async def _call(settings, system, user, schema):
    provider = settings.get("llm_provider")
    if provider == "anthropic":
        return await _anthropic(settings, system, user, schema)
    if provider == "openai":
        return await _openai(settings, system, user, schema)
    raise RuntimeError(tr("当前为离线模板模式，AI 功能不可用（设置 → 提示词 AI）", "Offline template mode: AI features are off (Settings → Prompt AI)"))


async def art_prompt(card, settings, rejected_history=(), feedback=""):
    import config
    prompt_lang = config.prompt_lang(settings)
    if settings.get("llm_provider") == "template":
        return {"prompt": _append_theme(template_prompt(card, settings), card, prompt_lang),
                "notes": tr("离线模板生成；建议在「画面构思」里用英文写具体内容", "Built by the offline template; write the art concept in concrete English")}
    lang = settings.get("card_language") or "zh"
    system = _art_system(lang, settings)
    brief = _card_brief(card, rejected_history, feedback)
    res = await _call(settings, system, brief, PLAN_SCHEMA)
    # Select template based on image provider: prose for cloud, structured for local LoRA
    template = "prose" if settings.get("image_provider", "comfy") != "comfy" else "structured"
    prompt, plan = compile_scene_plan(res, card, prompt_lang, template=template)
    prompt = _append_theme(prompt, card, prompt_lang)
    return {"prompt": prompt, "notes": plan.get("notes", ""), "plan": plan}


async def card_ideas(theme, cls, count, settings, existing=()):
    lang = settings.get("card_language") or "zh"
    info = sts2.class_info(cls)
    user = f"Character / class: {info['name']}\nTheme / mechanics: {theme}\nNumber of cards: {count}"
    if info["appearance"]:
        user += f"\nThe character looks like: {info['appearance']}"
    if existing:
        user += "\nAlready existing cards (do not repeat them): " + ", ".join(existing)
    res = await _call(settings, _ideas_system(lang), user, IDEAS_SCHEMA)
    cards = res.get("cards") or []
    for c in cards:
        c["cls"] = cls
        if c.get("type") not in sts2.CARD_TYPES:
            c["type"] = "skill"
        if c.get("rarity") not in sts2.RARITIES:
            c["rarity"] = "common"
    return cards[:count]
