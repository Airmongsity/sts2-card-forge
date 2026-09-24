"""Paths and persisted settings (data/settings.json, shared with the WinUI app)."""
import json
import threading
from copy import deepcopy
from pathlib import Path

import sts2

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
IMAGES = DATA / "images"
LOGS = DATA / "logs"
SETTINGS_FILE = DATA / "settings.json"
DB_FILE = DATA / "cardforge.db"

DEFAULTS = {
    "comfy_root": str(ROOT / "ComfyUI_windows_portable"),
    "comfy_port": 8188,
    "comfy_autostart": True,
    "comfy_extra_args": "",
    "comfy_profile": "auto",               # auto | standard | low | minimal (see comfy.PROFILE_FLAGS)
    "comfy_preview": "latent2rgb",         # none | latent2rgb | auto. latent2rgb measured free on an 8 GB card;
                                           # auto overflowed VRAM there and ran >15x slower
    "backend_port": 8190,
    "hf_endpoint": "https://huggingface.co",
    "unet_file": "qwen_image_2.1_Q5_K_M.gguf",   # picked by VRAM during setup
    "ui_language": "",                            # "" = follow Windows
    "llm_provider": "anthropic",           # anthropic | openai (any OpenAI-compatible API) | template
    "llm_preset": "claude",
    "anthropic_api_key": "",               # blank: use ANTHROPIC_API_KEY / `ant auth login` profile
    "anthropic_model": "claude-opus-5",
    "anthropic_effort": "medium",
    "openai_base_url": "http://127.0.0.1:11434/v1",
    "openai_api_key": "",
    "openai_model": "qwen3:8b",
    "card_language": "zh",
    "prompt_system": sts2.DEFAULT_PROMPT_SYSTEM,
    "prompt_template": sts2.DEFAULT_PROMPT_TEMPLATE,
    "gen": {
        "cfg": 3.0,
        "steps": 25,
        "negative": sts2.DEFAULT_NEGATIVE,
        "style_suffix": sts2.DEFAULT_STYLE_SUFFIX,   # appended to every prompt at generation time
        "lora": sts2.DEFAULT_LORA,
        "lora_strength": 0.9,
        "size_preset": "sts2_card",
        "variants": 2,
        # overheat protection (hysteresis): above cool_trigger °C the queue stops between images until the GPU is
        # back below cool_temp °C (at most cool_max s). cooldown adds an optional fixed gap between images.
        "cool_trigger": 90,
        "cool_temp": 70,
        "cool_max": 900,
        "cooldown": 0,
    },
    "export_dir": str(DATA / "exports"),
    "export_by_class": False,              # write <folder>/<character id>/<card id>.png
    "examples_seeded": False,
}

_lock = threading.Lock()


def _merge(base, over):
    out = deepcopy(base)
    for k, v in (over or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _merge(out[k], v)
        elif k in out or not k.startswith("_"):
            out[k] = v
    return out


def _prune(values, defaults):
    out = {}
    for k, v in values.items():
        if isinstance(v, dict) and isinstance(defaults.get(k), dict):
            v = _prune(v, defaults[k])
            if v:
                out[k] = v
        elif k not in defaults or v != defaults[k]:
            out[k] = v
    return out


def load():
    with _lock:
        try:
            saved = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
        except (FileNotFoundError, ValueError):
            saved = {}
        return _merge(DEFAULTS, saved)


def save(patch):
    """Merge a (partial) settings dict into the file and return the full result."""
    with _lock:
        try:
            saved = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
        except (FileNotFoundError, ValueError):
            saved = {}
        # persist only values that differ from DEFAULTS, so later default changes reach existing installs
        stored = _prune(_merge(saved, patch), DEFAULTS)
        DATA.mkdir(parents=True, exist_ok=True)
        tmp = SETTINGS_FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps(stored, indent=2, ensure_ascii=False), encoding="utf-8")
        tmp.replace(SETTINGS_FILE)
        return _merge(DEFAULTS, stored)


def comfy_dir(s=None):
    """The ComfyUI folder that contains main.py and models/ (inside the portable package)."""
    root = Path((s or load())["comfy_root"])
    return root / "ComfyUI" if (root / "ComfyUI" / "main.py").exists() else root


def comfy_python(s=None):
    root = Path((s or load())["comfy_root"])
    for p in (root / "python_embeded" / "python.exe", root.parent / "python_embeded" / "python.exe"):
        if p.exists():
            return p
    return None


def ui_lang():
    lang = load().get("ui_language") or ""
    if not lang:
        try:
            import ctypes
            # Windows UI language; the primary language id 0x04 is Chinese
            lang = "zh-CN" if (ctypes.windll.kernel32.GetUserDefaultUILanguage() & 0x3FF) == 0x04 else "en-US"
        except (AttributeError, OSError):
            lang = "en-US"
    return lang


def tr(zh, en):
    """Pick the message for the current UI language."""
    return zh if ui_lang().startswith("zh") else en


for d in (DATA, IMAGES, LOGS):
    d.mkdir(parents=True, exist_ok=True)
