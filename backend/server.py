"""STS2 Card Forge backend: a local HTTP API over ComfyUI, used by the WinUI app.

    python backend/server.py [--port 8190]

Runs on ComfyUI's embedded Python (aiohttp and Pillow come with ComfyUI; `anthropic` is installed by setup).
Listens on 127.0.0.1 only.
"""
import argparse
import asyncio
import csv
import io
import json
import os
import sys
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from aiohttp import web  # noqa: E402
from PIL import Image  # noqa: E402

import config  # noqa: E402
import examples  # noqa: E402
import exporter  # noqa: E402
import promptgen  # noqa: E402
import store  # noqa: E402
import sts2  # noqa: E402
from comfy import Comfy  # noqa: E402
from thermal import Thermal  # noqa: E402
from worker import Worker, make_jobs  # noqa: E402

try:
    VERSION = (config.ROOT / "VERSION").read_text(encoding="utf-8").strip()
except OSError:
    VERSION = "0.0.0"
THUMBS = config.DATA / "thumbs"
THUMBS.mkdir(exist_ok=True)

routes = web.RouteTableDef()
comfy = Comfy()
thermal = Thermal()
worker = Worker(comfy, thermal)


class ApiError(Exception):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


@web.middleware
async def errors(request, handler):
    try:
        return await handler(request)
    except web.HTTPException:
        raise
    except ApiError as e:
        return web.json_response({"error": str(e)}, status=e.status)
    except (ValueError, KeyError, RuntimeError) as e:
        return web.json_response({"error": str(e)}, status=400)
    except Exception as e:  # unexpected: log and report
        traceback.print_exc()
        return web.json_response({"error": f"{type(e).__name__}: {e}"}, status=500)


def ok(data=None):
    return web.json_response(data if data is not None else {"ok": True},
                             dumps=lambda o: json.dumps(o, ensure_ascii=False))


async def body(request):
    if not request.can_read_body:
        return {}
    try:
        return await request.json()
    except ValueError:
        raise ApiError(config.tr("请求不是有效的 UTF-8 JSON", "Request body is not valid UTF-8 JSON"))


def card_or_404(cid):
    card = store.get_card(int(cid))
    if not card:
        raise ApiError(config.tr("卡牌不存在", "Card not found"), 404)
    return card


def image_or_404(iid):
    img = store.get_image(int(iid))
    if not img:
        raise ApiError(config.tr("图片不存在", "Image not found"), 404)
    return img


# ---- status / settings / meta ----------------------------------------------------------------

@routes.get("/api/health")
async def health(_):
    return ok({"ok": True, "version": VERSION})


@routes.get("/api/status")
async def status(_):
    gen = config.load()["gen"]
    return ok({"version": VERSION, "comfy": comfy.status(), "worker": worker.status(), "gpu": thermal.gpu(),
               "thermal": {"trigger": gen.get("cool_trigger") or 0, "resume": gen.get("cool_temp")}})


@routes.get("/api/thermal")
async def thermal_state(_):
    gen = config.load()["gen"]
    return ok({"gpu": thermal.gpu(), "history": [t for _, t in thermal.history],
               "interval": 3, "trigger": gen.get("cool_trigger"), "resume": gen.get("cool_temp"),
               "worker": worker.status()})


@routes.get("/api/settings")
async def get_settings(_):
    return ok(config.load())


@routes.put("/api/settings")
async def put_settings(request):
    s = config.save(await body(request))
    worker.wake()
    return ok(s)


@routes.get("/api/meta")
async def meta(_):
    return ok({
        "classes": [{"id": c["id"], "name": c["name"] or c["id"], "color": c["color"],
                     "ref": c["ref_image"], "use_ref": bool(c["use_ref"])} for c in store.list_characters()],
        "default_style_suffix": sts2.DEFAULT_STYLE_SUFFIX,
        "default_prompt_system": sts2.DEFAULT_PROMPT_SYSTEM,
        "default_prompt_template": sts2.DEFAULT_PROMPT_TEMPLATE,
        "types": [{"id": k, "name": v[0]} for k, v in sts2.CARD_TYPES.items()],
        "rarities": [{"id": k, "name": v[0], "color": v[1]} for k, v in sts2.RARITIES.items()],
        "sizes": [{"id": k, "name": v["label"], "gen": v["gen"], "out": v["out"]} for k, v in sts2.SIZE_PRESETS.items()],
        "loras": comfy.lora_list(),
        "default_negative": sts2.DEFAULT_NEGATIVE,
        "projects": store.projects(),
    })


@routes.post("/api/shutdown")
async def shutdown(_):
    async def later():
        await asyncio.sleep(0.2)
        await comfy.stop()
        os._exit(0)
    asyncio.get_running_loop().create_task(later())
    return ok()


# ---- MOD characters ------------------------------------------------------------------------

@routes.get("/api/characters")
async def list_characters(_):
    return ok(store.list_characters())


@routes.put("/api/characters/{id}")
async def save_character(request):
    cid = exporter.slugify(request.match_info["id"], "")
    if not cid or cid in sts2.CLASSES:
        raise ApiError(config.tr("角色 id 需为英文字母/数字，且不能与原版职业重名", "Character id must be letters/digits and differ from the built-in classes"))
    return ok(store.save_character(cid, await body(request)))


@routes.delete("/api/characters/{id}")
async def delete_character(request):
    store.delete_character(request.match_info["id"])
    return ok()


# ---- ComfyUI ---------------------------------------------------------------------------------

@routes.post("/api/comfy/start")
async def comfy_start(_):
    asyncio.get_running_loop().create_task(comfy.start())
    return ok(comfy.status())


@routes.post("/api/comfy/stop")
async def comfy_stop(_):
    await comfy.stop()
    return ok(comfy.status())


@routes.get("/api/comfy/log")
async def comfy_log(request):
    return web.Response(text=comfy.log_tail(int(request.query.get("tail", 200))))


# ---- cards -----------------------------------------------------------------------------------

@routes.get("/api/cards")
async def list_cards(request):
    return ok(store.list_cards(request.query.get("project") or None))


@routes.post("/api/cards")
async def create_card(request):
    data = await body(request)
    return ok(store.create_card(data))


@routes.post("/api/cards/bulk")
async def create_cards(request):
    data = await body(request)
    project = data.get("project") or "default"
    return ok([store.create_card({"project": project, **c}) for c in data.get("cards", [])])


@routes.get(r"/api/cards/{id:\d+}")
async def get_card(request):
    return ok(card_or_404(request.match_info["id"]))


@routes.put(r"/api/cards/{id:\d+}")
async def update_card(request):
    card = card_or_404(request.match_info["id"])
    return ok(store.update_card(card["id"], await body(request)))


@routes.delete(r"/api/cards/{id:\d+}")
async def delete_card(request):
    card = card_or_404(request.match_info["id"])
    store.delete_card(card["id"])
    return ok()


@routes.post(r"/api/cards/{id:\d+}/prompt")
async def card_prompt(request):
    """Generate an image prompt for a card with the configured LLM and save it on the card."""
    card = card_or_404(request.match_info["id"])
    card.update({k: v for k, v in (await body(request)).items() if k in store.CARD_FIELDS})
    res = await promptgen.art_prompt(card, config.load())
    store.update_card(card["id"], {"prompt": res["prompt"]})
    return ok(res)


@routes.post("/api/prompt")
async def prompt_for(request):
    """Prompt for an unsaved card dict; the settings page uses it to test the configured LLM."""
    data = await body(request)
    settings = config.load()
    settings.update(data.pop("_settings", None) or {})
    return ok(await promptgen.art_prompt(data, settings))


@routes.post("/api/ideas")
async def ideas(request):
    data = await body(request)
    existing = [c["name"] for c in store.list_cards(data.get("project") or None)]
    cards = await promptgen.card_ideas(data.get("theme", ""), data.get("cls", "colorless"),
                                       int(data.get("count") or 5), config.load(), existing)
    return ok(cards)


CSV_FIELDS = ["project", "name", "slug", "cls", "type", "rarity", "cost", "description", "concept", "prompt", "negative"]


@routes.post("/api/cards/import")
async def import_csv(request):
    """CSV with a header row; understands the column names above, plus "class" as an alias of "cls"."""
    data = await body(request)
    rows = list(csv.DictReader(io.StringIO(data.get("csv", "").lstrip("﻿"))))
    created = []
    for r in rows:
        r = {k.strip().lower(): (v or "").strip() for k, v in r.items() if k}
        if "class" in r and "cls" not in r:
            r["cls"] = r.pop("class")
        if not r.get("name") and not r.get("slug"):
            continue
        card = {k: r[k] for k in CSV_FIELDS if r.get(k)}
        card.setdefault("project", data.get("project") or "default")
        created.append(store.create_card(card))
    return ok(created)


@routes.get("/api/cards/export.csv")
async def export_csv(request):
    out = io.StringIO()
    w = csv.DictWriter(out, CSV_FIELDS, extrasaction="ignore")
    w.writeheader()
    for c in store.list_cards(request.query.get("project") or None):
        w.writerow(c)
    return web.Response(text="﻿" + out.getvalue(), content_type="text/csv", charset="utf-8")


# ---- generation queue ------------------------------------------------------------------------

@routes.post("/api/jobs")
async def create_jobs(request):
    """{card_id?, prompt?, negative?, count?, seed?, size_preset?, lora?, lora_strength?, cfg?, steps?, refs?, init?, denoise?}"""
    data = await body(request)
    card = card_or_404(data["card_id"]) if data.get("card_id") else None
    ids = make_jobs(card, data, config.load())
    worker.wake()
    return ok({"jobs": ids})


@routes.post("/api/jobs/project")
async def create_project_jobs(request):
    """Queue every card of a project that has a prompt (optionally only cards without any image yet)."""
    data = await body(request)
    settings, ids, skipped = config.load(), [], []
    for card in store.list_cards(data.get("project") or None):
        if data.get("only_missing") and card["image_count"]:
            continue
        if not card["prompt"]:
            skipped.append(card["name"])
            continue
        ids += make_jobs(card, {"count": data.get("count")}, settings)
    worker.wake()
    return ok({"jobs": ids, "skipped": skipped})


@routes.get("/api/jobs")
async def list_jobs(request):
    return ok({"jobs": store.list_jobs(request.query.get("active") == "1"), "worker": worker.status()})


@routes.delete(r"/api/jobs/{id:\d+}")
async def cancel_job(request):
    await worker.cancel(int(request.match_info["id"]))
    return ok()


@routes.post("/api/jobs/clear")
async def clear_jobs(_):
    store.clear_finished()
    return ok()


@routes.post("/api/jobs/cancel-all")
async def cancel_all(_):
    for j in store.list_jobs(active_only=True):
        await worker.cancel(j["id"])
    return ok()


@routes.post("/api/worker")
async def control_worker(request):
    data = await body(request)
    if "paused" in data:
        worker.paused = bool(data["paused"])
    if data.get("skip_cooldown"):
        worker.skip_cooldown()
    worker.wake()
    return ok(worker.status())


@routes.get("/api/preview")
async def preview(_):
    if not worker.preview:
        raise web.HTTPNoContent()
    return web.Response(body=worker.preview, content_type="image/jpeg", headers={"Cache-Control": "no-store"})


# ---- images ----------------------------------------------------------------------------------

@routes.get("/api/images")
async def list_images(request):
    q = request.query
    return ok(store.list_images(int(q["card_id"]) if q.get("card_id") else None, q.get("project") or None,
                                q.get("favorites") == "1", int(q.get("limit", 500)), int(q.get("offset", 0))))


@routes.get(r"/api/images/{id:\d+}/file")
async def image_file(request):
    img = image_or_404(request.match_info["id"])
    return web.FileResponse(img["path"])


@routes.get(r"/api/images/{id:\d+}/thumb")
async def image_thumb(request):
    img = image_or_404(request.match_info["id"])
    w = max(64, min(1024, int(request.query.get("w", 360))))
    path = THUMBS / f"{img['id']}_{w}.jpg"
    if not path.exists():
        with Image.open(img["path"]) as im:
            im = im.convert("RGB")
            im.thumbnail((w, w * 2), Image.LANCZOS)
            im.save(path, quality=88)
    return web.FileResponse(path, headers={"Cache-Control": "max-age=86400"})


@routes.post(r"/api/images/{id:\d+}/select")
async def select_image(request):
    img = image_or_404(request.match_info["id"])
    data = await body(request)
    cid = int(data.get("card_id") or img["card_id"] or 0)
    card = card_or_404(cid)
    store.update_card(card["id"], {"selected_image": img["id"]})
    return ok()


@routes.post(r"/api/images/{id:\d+}/favorite")
async def favorite_image(request):
    img = image_or_404(request.match_info["id"])
    store.set_favorite(img["id"], (await body(request)).get("favorite", True))
    return ok()


@routes.delete(r"/api/images/{id:\d+}")
async def delete_image(request):
    img = store.delete_image(int(request.match_info["id"]))
    if img:
        for p in [Path(img["path"]), *THUMBS.glob(f"{img['id']}_*.jpg")]:
            try:
                p.unlink()
            except OSError:
                pass
    return ok()


@routes.post(r"/api/images/{id:\d+}/refine")
async def refine(request):
    """Re-render an image (typically a draft) at full size via img2img, keeping its composition."""
    img = image_or_404(request.match_info["id"])
    data = await body(request)
    card = store.get_card(img["card_id"]) if img["card_id"] else None
    p = img["params"]
    preset = data.get("size_preset") or ((card or {}).get("params") or {}).get("size_preset")
    if not preset or preset == "draft":
        preset = config.load()["gen"]["size_preset"]
        if preset == "draft":
            preset = "sts2_card"
    req = {"prompt": p.get("prompt") or img["prompt"], "negative": p.get("negative"), "seed": img["seed"],
           "count": 1, "size_preset": preset, "init": img["path"], "denoise": float(data.get("denoise") or 0.55),
           "lora": p.get("lora"), "lora_strength": p.get("lora_strength"), "refs": p.get("refs") or []}
    ids = make_jobs(card, req, config.load())
    worker.wake()
    return ok({"jobs": ids})


@routes.post(r"/api/images/{id:\d+}/export")
async def export_one(request):
    data = await body(request)
    img = image_or_404(request.match_info["id"])
    return ok({"path": exporter.export_image(img["id"], data["path"], data.get("size_preset"),
                                             data.get("width"), data.get("height"))})


@routes.post("/api/export")
async def export(request):
    """{card_ids? | project?, folder?, by_class?, size_preset?, width?, height?}"""
    data = await body(request)
    ids = data.get("card_ids") or [c["id"] for c in store.list_cards(data.get("project") or None)]
    return ok(exporter.export_cards(ids, data.get("folder"), bool(data.get("by_class")), data.get("size_preset"),
                                    data.get("width"), data.get("height")))


# ---- app -------------------------------------------------------------------------------------

async def on_startup(app):
    examples.seed_once()
    loop = asyncio.get_running_loop()
    app["worker"] = loop.create_task(worker.loop())
    app["thermal"] = loop.create_task(thermal.loop())
    if config.load().get("comfy_autostart"):
        loop.create_task(comfy.start())


async def on_cleanup(app):
    app["worker"].cancel()
    await comfy.stop()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=config.load()["backend_port"])
    a = ap.parse_args()
    app = web.Application(middlewares=[errors], client_max_size=64 * 1024 * 1024)
    app.add_routes(routes)
    app.on_startup.append(on_startup)
    app.on_cleanup.append(on_cleanup)
    print(f"STS2 Card Forge backend {VERSION} on http://127.0.0.1:{a.port}", flush=True)
    web.run_app(app, host="127.0.0.1", port=a.port, print=None)


if __name__ == "__main__":
    main()
