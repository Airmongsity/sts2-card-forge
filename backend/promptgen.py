"""Turns card data into image prompts (and brainstorms new cards) with an LLM.

Providers: "anthropic" (Claude, official SDK), "openai" (any OpenAI-compatible server such as Ollama or
LM Studio, for fully local use) and "template" (offline, rule-based fallback)."""
import json
import re

import aiohttp

import sts2
from config import tr

LANG_NAMES = {"zh": "Simplified Chinese", "en": "English", "ja": "Japanese"}

ART_SCHEMA = {
    "type": "object",
    "properties": {
        "prompt": {"type": "string", "description": "The full English image prompt."},
        "notes": {"type": "string", "description": "One short line explaining the visual idea, in the user's language."},
    },
    "required": ["prompt", "notes"],
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
    text = (settings.get("prompt_system") or sts2.DEFAULT_PROMPT_SYSTEM).replace("{lang}", LANG_NAMES.get(lang, "English"))
    return text + '\n\nReply with JSON: "prompt" (the prompt) and "notes" (the one-line explanation).'


def _ideas_system(lang):
    types = ", ".join(sts2.CARD_TYPES)
    rarities = ", ".join(sts2.RARITIES)
    return f"""You design cards for a Slay the Spire 2 mod. Cards follow Slay the Spire conventions: energy cost \
(0-3, X, or "-" for unplayable), a type ({types}), a rarity ({rarities}) and short, precise effect text using the \
game's keywords (Block, Vulnerable, Weak, Exhaust, Retain, Innate, Ethereal, Draw, Discard, Strength, Dexterity, ...).
Make each card mechanically distinct, balanced for its cost and rarity, and fitting the theme.
"concept" is a one-sentence idea for the card's illustration: a single clear focal subject.
Write name, description and concept in {LANG_NAMES.get(lang, 'English')}. Reply with JSON: {{"cards": [...]}}."""


def _card_brief(card):
    info = sts2.class_info(card.get("cls") or "colorless")
    fields = {
        "name": card.get("name"), "trigger": info["trigger"], "type": card.get("type"),
        "rarity": card.get("rarity"), "cost": card.get("cost"),
        "effect_text": card.get("description"), "art_concept": card.get("concept"),
    }
    brief = json.dumps({k: v for k, v in fields.items() if v}, ensure_ascii=False, indent=1)
    if info["appearance"]:
        brief += f"\n\nThe card belongs to the mod character \"{info['name']}\". Appearance:\n{info['appearance']}"
    if info["palette"] and info["palette"] != sts2.NEUTRAL_PALETTE:
        brief += f"\nThe character's own colours (for their outfit only): {info['palette']}"
    themes = sts2.card_themes(card)
    brief += f"\nColour theme of this card: {sts2.theme_palette(themes)} ({', '.join(themes)})."
    if len(themes) > 1:
        brief += (" The first colour dominates the picture (mostly the background and shadows); the others are accent "
                  "colours for the glowing objects, the light and the effects.")
    brief += (" Build the light, the effects and the background sentence around this theme, even when it differs from "
              "the character's own colours.")
    if sts2.card_refs(card):
        brief += ("\nA reference image of the character is attached as <image1>. When the character appears, write "
                  "\"the character from <image1>\" (same face, hair and outfit) instead of re-describing them.")
    else:
        brief += ("\nNo reference image is attached: never write <image1>; describe the character only with the "
                  "appearance above (if any).")
    return brief


# ---- providers -------------------------------------------------------------------------------

async def _anthropic(settings, system, user, schema):
    import anthropic  # installed by setup into ComfyUI's python

    key = settings.get("anthropic_api_key") or None
    client = anthropic.AsyncAnthropic(api_key=key) if key else anthropic.AsyncAnthropic()
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
    if sts2.card_refs(card):
        concept = f"the character from <image1>, {concept}"
    template = (settings or {}).get("prompt_template") or sts2.DEFAULT_PROMPT_TEMPLATE
    values = _Keep(trigger=info["trigger"], concept=concept[0].upper() + concept[1:], name=card.get("name") or "",
                   effect=card.get("description") or "", palette=sts2.theme_palette(sts2.card_themes(card)).capitalize(),
                   character=info["appearance"] or info["name"])
    return " ".join(template.format_map(values).split())


async def _call(settings, system, user, schema):
    provider = settings.get("llm_provider")
    if provider == "anthropic":
        return await _anthropic(settings, system, user, schema)
    if provider == "openai":
        return await _openai(settings, system, user, schema)
    raise RuntimeError(tr("当前为离线模板模式，AI 功能不可用（设置 → 提示词 AI）", "Offline template mode: AI features are off (Settings → Prompt AI)"))


async def art_prompt(card, settings):
    if settings.get("llm_provider") == "template":
        return {"prompt": template_prompt(card, settings), "notes": tr("离线模板生成；建议在“画面构思”里用英文写具体内容", "Built by the offline template; write the art concept in concrete English")}
    lang = settings.get("card_language") or "zh"
    res = await _call(settings, _art_system(lang, settings), _card_brief(card), ART_SCHEMA)
    # enforce the exact trigger phrase the LoRA was trained with
    body = re.sub(r"^\s*sts2 card art,[^.]*\.\s*", "", res.get("prompt", "").strip(), flags=re.I)
    prompt = f"{sts2.TRIGGER.format(cls=sts2.class_info(card.get('cls') or 'colorless')['trigger'])} {body}"
    return {"prompt": prompt, "notes": res.get("notes", "")}


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
