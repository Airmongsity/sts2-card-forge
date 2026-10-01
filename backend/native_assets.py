"""Native art/template discovery and import.

Imported images are copied into CardForge's data directory so game updates, Steam library moves and source extraction
cleanup cannot silently break existing cards. They follow the same local/cloud behaviour as ordinary references.
"""
from __future__ import annotations

import hashlib
import csv
import json
import os
import re
import sqlite3
import shutil
import subprocess
import tempfile
import threading
import time
from pathlib import Path

from PIL import Image, ImageOps

import config
import store

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".webp"}
SKELETON_EXTS = {".skel", ".json", ".spine"}
ROLE_VALUES = {"character", "enemy", "ally", "style", "scene", "object"}
RENDERER = Path(__file__).resolve().parent / "spine_renderer"
STS2_APP_ID = "2868840"
STS2_FOLDER = "Slay the Spire 2"

# Small fallbacks cover names used before an extracted localization table exists.  Native imports prefer the game's
# own recovered translation CSVs; this table is not used to guess arbitrary names.
BUILTIN_NAMES = {
    "zh": {"fuel": "燃料"},
}


def _inside(child: Path, parent: Path) -> bool:
    try:
        child.resolve().relative_to(parent.resolve())
        return True
    except (OSError, ValueError):
        return False


def _digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _safe_name(value: str) -> str:
    value = re.sub(r"[^\w.-]+", "_", value, flags=re.UNICODE).strip("._")
    return value[:80] or "asset"


def _name_key(value: str) -> str:
    return re.sub(r"[^\w]+", "_", value, flags=re.UNICODE).strip("_").casefold()


def _human_name(stem: str) -> str:
    words = re.sub(r"[_\-.]+", " ", stem).strip()
    return words.title() if words else stem


def _translation_names(files: list[Path], language: str) -> dict[str, str]:
    """Read localization data recovered from the user's own game files."""
    want_zh = (language or config.ui_lang()).casefold().startswith("zh")
    wanted = ("zh_cn", "zh-cn", "zh_hans", "zh-hans", "zh") if want_zh else ("en_us", "en-us", "en")
    result = {}

    def remember(raw_key, value):
        key = _name_key(str(raw_key))
        value = str(value).strip()
        if not key or not value:
            return
        result.setdefault(key, value)
        result.setdefault("@" + re.sub(r"[^\w]+", "", key, flags=re.UNICODE), value)

    # STS2 ships one flat JSON dictionary per topic under localization/zhs (or eng).  These are the authoritative
    # UI names for cards, monsters, relics, characters, etc.  Earlier builds incorrectly looked only for CSV files.
    locale = "zhs" if want_zh else "eng"
    for path in files:
        parts = [part.casefold() for part in path.parts]
        if path.suffix.casefold() != ".json" or locale not in parts or "localization" not in parts:
            continue
        try:
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(data, dict):
                continue
            for raw_key, value in data.items():
                if not isinstance(value, str):
                    continue
                key_parts = str(raw_key).split(".")
                if len(key_parts) == 2 and key_parts[-1].casefold() in {"name", "title"}:
                    remember(key_parts[0], value)
        except (OSError, ValueError):
            continue

    for path in files:
        if path.suffix.casefold() != ".csv" or path.stat().st_size > 32 * 1024 * 1024:
            continue
        try:
            with path.open("r", encoding="utf-8-sig", errors="replace", newline="") as handle:
                rows = csv.reader(handle)
                header = next(rows, [])
                columns = [_name_key(value) for value in header]
                selected = next((i for key in wanted for i, value in enumerate(columns) if value == key), None)
                if selected is None or not header:
                    continue
                for row in rows:
                    if not row or selected >= len(row) or not row[selected].strip():
                        continue
                    key, value = _name_key(row[0]), row[selected].strip()
                    remember(key, value)
                    parts = key.split("_")
                    if len(parts) >= 2 and parts[-1] in {"name", "title"}:
                        remember(parts[-2], value)
        except (OSError, csv.Error):
            continue
    return result


def localized_name_info(stem: str, language: str = "", translations=None) -> tuple[str, bool]:
    lang = (language or config.ui_lang()).casefold()
    key = _name_key(stem)
    compact = "@" + re.sub(r"[^\w]+", "", key, flags=re.UNICODE)
    translated = ((translations or {}).get(key) or (translations or {}).get(compact) or
                  BUILTIN_NAMES.get("zh" if lang.startswith("zh") else "en", {}).get(key))
    return (translated, True) if translated else (_human_name(stem), False)


def localized_name(stem: str, language: str = "", translations=None) -> str:
    return localized_name_info(stem, language, translations)[0]


def _atlas_pages(path: Path) -> set[str]:
    """Atlas page names are unindented lines at the start of a page block."""
    try:
        lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
    except OSError:
        return set()
    pages, new_block = set(), True
    for raw in lines:
        line = raw.strip()
        if not line:
            new_block = True
        elif new_block and not raw[:1].isspace() and ":" not in line:
            pages.add(Path(line).name.casefold())
            new_block = False
    return pages


def _json_spine_version(path: Path) -> str:
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        return str((data.get("skeleton") or {}).get("spine") or "")
    except (OSError, ValueError, AttributeError):
        return ""


def _binary_spine_version(path: Path) -> str:
    """Read a Spine version from the binary header (4.x starts after an eight-byte export hash)."""
    try:
        data = path.read_bytes()[:128]
        match = re.search(rb"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9_.-]+)?)(?!\d)", data)
        return match.group(1).decode("ascii") if match else ""
    except OSError:
        return ""


def _bundle_for(skeleton: Path) -> dict:
    folder = skeleton.parent
    atlases = list(folder.glob("*.atlas")) + list(folder.glob("*.atlas.txt"))
    preferred = [a for a in atlases if a.name.split(".atlas", 1)[0].casefold() == skeleton.stem.casefold()]
    atlas = (preferred or atlases or [None])[0]
    pages = []
    missing = []
    if atlas:
        for name in sorted(_atlas_pages(atlas)):
            match = next((p for p in folder.iterdir() if p.is_file() and p.name.casefold() == name), None)
            if match:
                pages.append(str(match))
            else:
                missing.append(name)
    version = (_json_spine_version(skeleton) if skeleton.suffix.casefold() == ".json" else
               _binary_spine_version(skeleton) if skeleton.suffix.casefold() == ".skel" else "")
    normalized = str(skeleton).replace("\\", "/").casefold()
    role = "character" if "/animations/characters/" in normalized else (
           "enemy" if "/animations/monsters/" in normalized else "style")
    return {"kind": "spine", "name": skeleton.stem, "path": str(skeleton), "atlas": str(atlas) if atlas else "",
            "textures": pages, "missing": missing, "version": version,
            "ready": bool(atlas and pages and not missing), "role": role}


def scan(root, language="") -> dict:
    root = Path(root).expanduser()
    if not root.is_dir():
        raise ValueError(config.tr("来源文件夹不存在", "Source folder does not exist"))
    files = [p for p in root.rglob("*") if p.is_file()]
    translations = _translation_names(files, language)
    atlas_pages = set()
    for atlas in (p for p in files if p.name.casefold().endswith((".atlas", ".atlas.txt"))):
        atlas_pages.update((atlas.parent / n).resolve() for n in _atlas_pages(atlas))
    spine = [_bundle_for(p) for p in files if p.suffix.casefold() in SKELETON_EXTS and
             (p.suffix.casefold() != ".json" or _json_spine_version(p))]
    for bundle in spine:
        bundle["name"], bundle["localized"] = localized_name_info(bundle["name"], language, translations)
    images = []
    for path in files:
        if path.suffix.casefold() not in IMAGE_EXTS or path.resolve() in atlas_pages:
            continue
        name, localized = localized_name_info(path.stem, language, translations)
        images.append({"kind": "image", "name": name, "path": str(path), "localized": localized})
    packages = [str(p) for p in files if p.suffix.casefold() == ".pck"]
    return {"root": str(root.resolve()), "images": images, "spine": spine, "packages": packages,
            "counts": {"images": len(images), "spine": len(spine),
                       "ready_spine": sum(bool(b["ready"]) for b in spine), "packages": len(packages)}}


def _registry_steam_roots() -> list[Path]:
    if os.name != "nt":
        return []
    try:
        import winreg
    except ImportError:
        return []
    roots = []
    for hive, key in ((winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam"),
                      (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam")):
        try:
            with winreg.OpenKey(hive, key) as handle:
                value = winreg.QueryValueEx(handle, "SteamPath" if hive == winreg.HKEY_CURRENT_USER else "InstallPath")[0]
                roots.append(Path(value))
        except OSError:
            pass
    return roots


def _drive_steam_roots() -> list[Path]:
    """Common Steam roots on every mounted Windows drive (C:, D:, E:, ...)."""
    if os.name != "nt":
        return []
    try:
        import ctypes
        mask = ctypes.windll.kernel32.GetLogicalDrives()
    except (AttributeError, OSError):
        return []
    roots = []
    for index in range(26):
        if not mask & (1 << index):
            continue
        drive = Path(f"{chr(ord('A') + index)}:\\")
        roots += [drive / "Program Files" / "Steam", drive / "Program Files (x86)" / "Steam",
                  drive / "Steam", drive / "SteamLibrary"]
    return roots


def discover_games(extra_steam_roots=None) -> dict:
    """Find owned STS2 installs from Steam roots and libraryfolders.vdf without launching Steam."""
    roots = [Path(r) for r in (extra_steam_roots or [])]
    roots += _drive_steam_roots()
    for env in ("ProgramFiles(x86)", "ProgramFiles"):
        if os.environ.get(env):
            roots.append(Path(os.environ[env]) / "Steam")
    roots += _registry_steam_roots()

    steam_roots, searched = [], []
    seen = set()
    for root in roots:
        key = str(root).rstrip("\\/").casefold()
        if not key or key in seen:
            continue
        seen.add(key)
        searched.append(str(root))
        if root.is_dir():
            steam_roots.append(root)

    libraries = list(steam_roots)
    for root in steam_roots:
        vdf = root / "steamapps" / "libraryfolders.vdf"
        try:
            text = vdf.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            continue
        for raw in re.findall(r'"path"\s+"([^"]+)"', text, re.IGNORECASE):
            libraries.append(Path(raw.replace(r"\\", "\\")))

    found, seen = [], set()
    for library in libraries:
        steamapps = library / "steamapps"
        manifest = steamapps / f"appmanifest_{STS2_APP_ID}.acf"
        install_dir = STS2_FOLDER
        try:
            match = re.search(r'"installdir"\s+"([^"]+)"', manifest.read_text(encoding="utf-8-sig", errors="replace"),
                              re.IGNORECASE)
            if match:
                install_dir = match.group(1)
        except OSError:
            pass
        game = steamapps / "common" / install_dir
        key = str(game).casefold()
        if key not in seen and game.is_dir() and (manifest.is_file() or any(game.glob("*.pck"))):
            seen.add(key)
            found.append(str(game.resolve()))
    return {"found": found, "searched": searched}


_EXTRACTION_LOCK = threading.Lock()
_EXTRACTION_DIR_RE = re.compile(r"^[0-9a-f]{16}(?:_[0-9]+)?$")
_EXTRACTION_PARTIAL_RE = re.compile(r"^[0-9a-f]{16}\..+\.partial$")


def _managed_extraction_entry(path: Path) -> bool:
    return bool(_EXTRACTION_DIR_RE.fullmatch(path.name) or _EXTRACTION_PARTIAL_RE.fullmatch(path.name))


def _clear_old_extractions(parent: Path, keep: Path | None = None) -> None:
    """Remove only extraction artifacts created by CardForge, never arbitrary files in or beside DATA."""
    expected = (config.DATA.resolve() / "native_extracted").resolve()
    if parent.is_symlink() or parent.resolve() != expected:
        raise RuntimeError("Refusing to clean an unexpected native extraction directory")
    parent.mkdir(parents=True, exist_ok=True)
    for child in parent.iterdir():
        if keep is not None and child == keep:
            continue
        managed = _managed_extraction_entry(child)
        if not managed and child.is_dir() and not child.is_symlink():
            managed = (child / ".cardforge-extraction.json").is_file()
        if not managed:
            continue
        if child.is_symlink() or not child.is_dir():
            child.unlink()
        else:
            shutil.rmtree(child)


def extract_pck(data) -> dict:
    # API calls run in worker threads. Serialize the cache check, purge, and recovery as one transaction.
    # A duplicate request then observes the completed marker instead of starting another GDRE process.
    with _EXTRACTION_LOCK:
        return _extract_pck_locked(data)


def _extract_pck_locked(data) -> dict:
    """Recover one user-owned Godot PCK into CardForge's versioned local cache with GDRE Tools."""
    gdre = Path(data.get("gdre") or "").resolve()
    if not gdre.is_file() or gdre.name.casefold() != "gdre_tools.exe":
        raise ValueError(config.tr("GDRE Tools 路径无效", "Invalid GDRE Tools path"))
    supplied = data.get("pck") or ""
    pck = Path(supplied).resolve() if supplied else None
    if not pck or not pck.is_file() or pck.suffix.casefold() != ".pck":
        root = Path(data.get("root") or "").resolve()
        packages = list(root.glob("*.pck")) if root.is_dir() else []
        pck = next((p for p in packages if p.name.casefold() == "sts2.pck"), packages[0] if packages else None)
    if not pck or not pck.is_file():
        raise ValueError(config.tr("游戏目录中没有找到 PCK 文件", "No PCK file was found in the game folder"))

    stat = pck.stat()
    fingerprint = hashlib.sha256(f"{pck.resolve()}|{stat.st_size}|{stat.st_mtime_ns}".encode("utf-8")).hexdigest()[:16]
    parent = config.DATA / "native_extracted"
    if parent.is_symlink() or parent.resolve() != (config.DATA.resolve() / "native_extracted").resolve():
        raise RuntimeError("Refusing to use an unexpected native extraction directory")
    parent.mkdir(parents=True, exist_ok=True)
    target = parent / fingerprint
    marker = target / ".cardforge-extraction.json"
    if not target.is_symlink() and not marker.is_symlink() and marker.is_file():
        _clear_old_extractions(parent, keep=target)
        return {"root": str(target), "pck": str(pck), "cached": True, "fingerprint": fingerprint}

    _clear_old_extractions(parent)
    staging = Path(tempfile.mkdtemp(prefix=f"{fingerprint}.", suffix=".partial", dir=parent))
    command = [str(gdre), "--headless", f"--recover={pck}", f"--output={staging}"]
    try:
        result = subprocess.run(command, capture_output=True, text=True, errors="replace", timeout=1800,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "GDRE recovery failed").strip()[-1500:]
            raise RuntimeError(config.tr(f"GDRE 解包失败：{detail}", f"GDRE extraction failed: {detail}"))
        if target.exists():
            target = parent / f"{fingerprint}_{int(time.time())}"
            marker = target / ".cardforge-extraction.json"
        staging.replace(target)
        marker.write_text(json.dumps({"pck": str(pck), "size": stat.st_size, "mtime_ns": stat.st_mtime_ns,
                                      "gdre": str(gdre), "created": time.time()}, ensure_ascii=False, indent=2),
                          encoding="utf-8")
    except Exception:
        if staging.exists() and _inside(staging, parent):
            shutil.rmtree(staging, ignore_errors=True)
        raise
    return {"root": str(target), "pck": str(pck), "cached": False, "fingerprint": fingerprint}


def renderer_info() -> dict:
    node = shutil.which("node")
    installed = (RENDERER / "node_modules" / "@esotericsoftware" / "spine-canvaskit").is_dir()
    node_version = ""
    if node:
        try:
            result = subprocess.run([node, "--version"], capture_output=True, text=True, timeout=10,
                                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            node_version = (result.stdout or "").strip().lstrip("v")
        except (OSError, subprocess.TimeoutExpired):
            pass
    try:
        node_major = int(node_version.split(".", 1)[0])
    except (ValueError, IndexError):
        node_major = 0
    return {"ok": bool(node and node_major >= 20 and installed), "node": node or "", "node_version": node_version,
            "installed": installed, "runtime_version": "4.2.106"}


def _render_spine_legacy(data):
    """Render a Spine 4.2 runtime bundle at animation time zero, validate it, then register it."""
    status = renderer_info()
    if not status["ok"]:
        raise RuntimeError(config.tr("Spine 4.2 本地渲染器尚未安装，或系统中没有 Node.js。",
                                     "The local Spine 4.2 renderer is not installed, or Node.js is unavailable."))
    skeleton = Path(data.get("skeleton") or "").resolve()
    atlas = Path(data.get("atlas") or "").resolve()
    if not skeleton.is_file() or skeleton.suffix.casefold() not in (".skel", ".json"):
        raise ValueError(config.tr("骨骼文件无效", "Invalid skeleton file"))
    if not atlas.is_file() or not atlas.name.casefold().endswith((".atlas", ".atlas.txt")):
        raise ValueError(config.tr("Atlas 文件无效", "Invalid atlas file"))
    version = _json_spine_version(skeleton) if skeleton.suffix.casefold() == ".json" else _binary_spine_version(skeleton)
    if version and not version.startswith("4.2."):
        raise ValueError(config.tr(f"资源为 Spine {version}；当前无损渲染器要求 4.2.x。",
                                   f"This asset is Spine {version}; the lossless renderer currently requires 4.2.x."))
    digest = hashlib.sha256()
    for file in [skeleton, atlas, *(Path(p) for p in data.get("textures") or [])]:
        if file.is_file():
            digest.update(_digest(file).encode("ascii"))
    animation = str(data.get("animation") or "")
    skin = str(data.get("skin") or "")
    with tempfile.TemporaryDirectory(dir=config.DATA, prefix="spine_render_") as work:
        output = Path(work) / "frame.png"
        command = [status["node"], str(RENDERER / "render.mjs"), "--skeleton", str(skeleton),
                   "--atlas", str(atlas), "--output", str(output), "--size", "1024"]
        if animation:
            command += ["--animation", animation]
        if skin:
            command += ["--skin", skin]
        result = subprocess.run(command, cwd=config.ROOT, capture_output=True, text=True, errors="replace",
                                timeout=120, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if result.returncode != 0 or not output.is_file():
            detail = (result.stderr or result.stdout or "render failed").strip().splitlines()[-1][:500]
            raise RuntimeError(config.tr(f"Spine 渲染失败：{detail}", f"Spine render failed: {detail}"))
        with Image.open(output) as im:
            alpha = im.getchannel("A") if "A" in im.getbands() else None
            if not im.getbbox() or (alpha and not alpha.getbbox()):
                raise RuntimeError(config.tr("Spine 渲染结果为空", "Spine rendered an empty frame"))
        try:
            report = json.loads((result.stdout or "").strip().splitlines()[-1])
        except (ValueError, IndexError):
            report = {}
        animation = report.get("animation") or animation
        skin = report.get("skin") or skin
        digest.update(f"{animation}\0{skin}\0{report.get('animationTime', 0)}".encode("utf-8"))
        source_hash = digest.hexdigest()
        asset = import_image(output, data.get("name") or skeleton.stem, data.get("category") or "native",
                             data.get("role") or "enemy", "spine",
                             {"animation": animation, "animation_time": report.get("animationTime", 0), "skin": skin,
                              "spine_version": version or "4.2.x", "atlas": str(atlas),
                              "localized": bool(data.get("localized")), "_source_path": str(skeleton)})
    if asset.pop("_skipped", False):
        return asset
    asset.update(source_path=str(skeleton), source_version=version or "4.2.x", source_hash=source_hash)
    return store.save_reference_asset(asset)


def render_spine(data):
    status = renderer_info()
    skeleton = Path(data.get("skeleton") or "").resolve()
    atlas = Path(data.get("atlas") or "").resolve()
    if not status["ok"] or not skeleton.is_file() or not atlas.is_file():
        raise ValueError("Spine renderer, skeleton, or atlas is unavailable")
    version = _json_spine_version(skeleton) if skeleton.suffix.casefold() == ".json" else _binary_spine_version(skeleton)
    if version and not version.startswith("4.2."):
        raise ValueError(f"This asset is Spine {version}; the renderer requires 4.2.x.")
    digest = hashlib.sha256()
    for file in [skeleton, atlas, *(Path(p) for p in data.get("textures") or [])]:
        if file.is_file():
            digest.update(_digest(file).encode("ascii"))
    with tempfile.TemporaryDirectory(dir=config.DATA, prefix="spine_render_") as work:
        output = Path(work) / "frames"
        command = [status["node"], str(RENDERER / "render.mjs"), "--skeleton", str(skeleton),
                   "--atlas", str(atlas), "--output", str(output), "--size", "2048", "--all", "1"]
        if data.get("skin"):
            command += ["--skin", str(data["skin"])]
        result = subprocess.run(command, cwd=config.ROOT, capture_output=True, text=True, errors="replace",
                                timeout=300, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        if result.returncode != 0 or not output.is_dir():
            detail = (result.stderr or result.stdout or "render failed").strip().splitlines()[-1][:500]
            raise RuntimeError(f"Spine render failed: {detail}")
        try:
            reports = json.loads((result.stdout or "").strip().splitlines()[-1])
        except (ValueError, IndexError):
            reports = []
        if not isinstance(reports, list):
            reports = [reports]
        frames = sorted(output.glob("*.png"))
        assets = []
        label_counts = {}
        for frame in frames:
            try:
                report = reports[int(frame.stem)]
            except (ValueError, IndexError):
                report = {}
            animation = str(report.get("animation") or frame.stem)
            with Image.open(frame) as im:
                rgba = im.convert("RGBA")
                bounds = rgba.getchannel("A").getbbox()
                if not bounds:
                    continue
                cropped = rgba.crop(bounds)
                cropped.thumbnail((880, 880), Image.Resampling.LANCZOS)
                normalized = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
                normalized.alpha_composite(cropped, ((1024 - cropped.width) // 2, (1024 - cropped.height) // 2))
                normalized_path = Path(work) / f"normalized_{frame.name}"
                normalized.save(normalized_path, format="PNG", optimize=True)
            frame_hash = hashlib.sha256(digest.digest() + animation.encode("utf-8")).hexdigest()
            label = _animation_label(animation)
            label_counts[label] = label_counts.get(label, 0) + 1
            if label_counts[label] > 1:
                label = f"{label} {label_counts[label]}"
            base_name = data.get("name") or skeleton.stem
            name = f"{base_name} · {label}" if len(frames) > 1 else base_name
            asset = import_image(normalized_path, name, data.get("category") or "native", data.get("role") or "enemy", "spine",
                                 {"animation": animation, "animation_time": 0, "skin": report.get("skin", ""),
                                  "spine_version": version or "4.2.x", "atlas": str(atlas),
                                  "localized": bool(data.get("localized")), "_source_path": str(skeleton),
                                  "_source_variant": animation})
            if not asset.pop("_skipped", False):
                asset.update(source_path=str(skeleton), source_version=version or "4.2.x", source_hash=frame_hash)
                asset = store.save_reference_asset(asset)
            assets.append(asset)
    return assets


def _animation_label(animation):
    key = re.sub(r"[^a-z0-9]+", "_", animation.casefold()).strip("_")
    words = set(key.split("_"))
    chinese = config.ui_lang().casefold().startswith("zh")
    labels = (({"idle", "rest", "stand"}, "待机", "Idle"),
              ({"attack", "hit"}, "攻击", "Attack"), ({"hurt", "damage"}, "受击", "Hurt"),
              ({"cast", "spell"}, "施法", "Cast"), ({"dead", "die"}, "死亡", "Death"),
              ({"relax", "relaxed"}, "放松", "Relaxed"), ({"spawn"}, "出现", "Spawn"),
              ({"burrow", "unburrow"}, "掘地", "Burrow"), ({"charge"}, "蓄力", "Charge"),
              ({"explode", "explosion"}, "爆炸", "Explosion"), ({"debuff"}, "负面效果", "Debuff"))
    for tokens, zh, en in labels:
        if words & tokens:
            return zh if chinese else en
    if "hatch" in words:
        return "孵化" if chinese else "Hatch"
    return animation


def import_image(source, name="", category="other", role="style", source_kind="image", metadata=None, language=""):
    source = Path(source).resolve()
    if not source.is_file() or source.suffix.casefold() not in IMAGE_EXTS:
        raise ValueError(config.tr("不是受支持的图片", "Not a supported image"))
    if role not in ROLE_VALUES:
        role = "style"
    name = name.strip() if isinstance(name, str) else ""
    # Manual/user imports keep their exact source name. One-click native import passes the localized scan name once.
    name = name or source.stem
    ext = ".png" if source.suffix.casefold() == ".png" else ".jpg"
    metadata = dict(metadata or {})
    identity = Path(metadata.pop("_source_path", source)).resolve()
    variant = metadata.pop("_source_variant", None)
    source_group = metadata.get("source_group")
    old_source = (store.reference_asset_by_source_variant(identity, variant) if category == "native" and variant
                  else store.reference_asset_by_source_path(identity) if category == "native" else None)
    target_dir = config.NATIVE_LIBRARY / _safe_name(source_group) if source_group else config.NATIVE_LIBRARY
    target = target_dir / f"{_safe_name(name)}{ext}"
    same_name = store.reference_asset_by_name(name)
    same_name_group = ((same_name.get("metadata") or {}).get("source_group") if same_name else None)
    if same_name and (not old_source or same_name["id"] != old_source["id"]) and (
            same_name_group == source_group if source_group else True):
        return {**same_name, "_skipped": True}
    path_owner = store.reference_asset_by_path(target)
    if path_owner and (not old_source or path_owner["id"] != old_source["id"]):
        # Another imported item may normalize to the same on-disk filename. Preserve the existing record.
        return {**path_owner, "_skipped": True}
    if target.exists() and (not old_source or Path(old_source["path"]) != target):
        existing = store.reference_asset_by_path(target)
        if existing:
            return {**existing, "_skipped": True}
        # A user-managed/orphaned file occupies this name. Never overwrite it.
        return {"name": name, "path": str(target), "_skipped": True}
    digest = _digest(source)
    refresh_managed_spine = bool(old_source and source_kind == "spine" and
                                 Path(old_source["path"]).resolve() == target.resolve())
    if not target.exists() or refresh_managed_spine:
        target_dir.mkdir(parents=True, exist_ok=True)
        save_target = target
        if refresh_managed_spine:
            fd, temp_name = tempfile.mkstemp(prefix=f".{target.stem}.", suffix=ext, dir=config.NATIVE_LIBRARY)
            os.close(fd)
            save_target = Path(temp_name)
        try:
            with Image.open(source) as im:
                im.load()
                if max(im.size) > 4096:
                    im.thumbnail((4096, 4096), Image.Resampling.LANCZOS)
                if ext == ".png":
                    im.save(save_target, format="PNG", optimize=True)
                else:
                    ImageOps.exif_transpose(im).convert("RGB").save(save_target, format="JPEG", quality=95)
            if refresh_managed_spine:
                save_target.replace(target)
        finally:
            if refresh_managed_spine and save_target.exists():
                save_target.unlink()
    with Image.open(target) as im:
        width, height = im.size
    record = {"name": name, "category": category or "other", "role": role,
        "path": str(target), "source_path": str(identity), "source_kind": source_kind, "source_hash": digest,
        "metadata": {**metadata, "source_stem": identity.stem, "width": width, "height": height}}
    # A rescan localizes the existing one-click import in place.  It must not create a second English/Chinese copy.
    if old_source:
        record["id"] = old_source["id"]
    try:
        saved = store.save_reference_asset(record)
    except sqlite3.IntegrityError:
        # A repeated import may claim this unique path after the check above. Only suppress the error
        # when the conflicting row now exists; propagate unrelated database failures.
        existing = store.reference_asset_by_path(target)
        if existing:
            return {**existing, "_skipped": True}
        raise
    old_path = Path(old_source["path"]) if old_source and old_source.get("path") else None
    if old_path and old_path != target and _inside(old_path, config.NATIVE_LIBRARY):
        try:
            old_path.unlink()
        except OSError:
            pass
    return saved


def import_many(items, category="other", role="style", language="") -> dict:
    """Import a recovered-image batch without allowing one corrupt file to abort the batch."""
    imported, skipped, failed, errors = 0, 0, 0, []
    for item in items:
        if not isinstance(item, dict) or not item.get("path"):
            failed += 1
            if len(errors) < 20:
                errors.append({"path": "", "error": config.tr("缺少图片路径", "Missing image path")})
            continue
        try:
            path = Path(item["path"]).resolve()
            parts = [part.casefold() for part in path.parts]
            group = ""
            if category == "native" and "card_portraits" in parts:
                group_index = len(parts) - 1 - parts[::-1].index("card_portraits")
                if group_index + 1 < len(parts) - 1:
                    group = parts[group_index + 1]
            asset = import_image(item["path"], item.get("name") or "", category, item.get("role") or role, "image",
                                 {"localized": bool(item.get("localized")),
                                  **({"source_group": group} if group else {})}, language)
            if asset.pop("_skipped", False):
                skipped += 1
            else:
                imported += 1
        except Exception as error:
            failed += 1
            if len(errors) < 20:
                errors.append({"path": str(item.get("path") or ""), "error": str(error)[:500]})
    return {"total": len(items), "imported": imported, "skipped": skipped, "failed": failed, "errors": errors}


def is_internal_asset(asset) -> bool:
    """Native files without an official UI name are engine/internal resources, hidden unless explicitly requested."""
    if asset.get("category") != "native":
        return False
    metadata = asset.get("metadata") or {}
    if "localized" in metadata:
        return not bool(metadata["localized"])
    stem = metadata.get("source_stem") or Path(asset.get("source_path") or "").stem
    return bool(stem and str(asset.get("name") or "").casefold() == _human_name(stem).casefold())


def delete_asset(aid):
    asset = store.delete_reference_asset(aid)
    if asset and _inside(Path(asset["path"]), config.NATIVE_LIBRARY):
        try:
            Path(asset["path"]).unlink()
        except OSError:
            pass
    return asset
