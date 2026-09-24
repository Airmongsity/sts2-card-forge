"""Single-GPU job queue: pulls queued jobs from the DB and runs them one at a time through ComfyUI."""
import asyncio
import io
import random
import re
import time
import traceback

from PIL import Image

import config
import graph
import store
import sts2
from comfy import Interrupted


def resolve_params(card, req, settings):
    """Merge settings defaults <- card params <- request overrides into one job's params."""
    gen = dict(settings["gen"])
    gen.update({k: v for k, v in ((card or {}).get("params") or {}).items() if v is not None and v != ""})
    gen.update({k: v for k, v in req.items() if v is not None and v != ""})
    prompt = (req.get("prompt") or (card or {}).get("prompt") or "").strip()
    suffix = (gen.get("style_suffix") or "").strip()
    if suffix and suffix not in prompt:
        prompt = f"{prompt} {suffix}"
    if not prompt:
        raise ValueError(config.tr("提示词为空：先写提示词或点“生成提示词”", "Prompt is empty: write one or click \"Generate prompt\""))
    preset = sts2.SIZE_PRESETS.get(gen.get("size_preset"), sts2.SIZE_PRESETS["sts2_card"])
    width, height = preset["gen"]
    if gen.get("width") and gen.get("height"):
        width, height = int(gen["width"]), int(gen["height"])
    refs = _refs(card, req, gen)
    if not refs and "<image1>" in prompt:
        prompt = re.sub(r"(?i)\b(the) character from <image1>", r"\1 character", prompt).replace("<image1>", "the character")
    return {
        "prompt": prompt,
        "negative": (req.get("negative") or (card or {}).get("negative") or gen.get("negative") or sts2.DEFAULT_NEGATIVE),
        "width": width, "height": height,
        "steps": int(gen.get("steps") or 25),
        "cfg": float(gen.get("cfg") or 3.0),
        "lora": gen.get("lora") or "",
        "lora_strength": float(gen.get("lora_strength") if gen.get("lora_strength") is not None else 0.9),
        "refs": refs,
        "init": gen.get("init") or "",
        "denoise": float(gen.get("denoise") or 0.6),
        "size_preset": gen.get("size_preset") or "sts2_card",
    }


def _refs(card, req, gen):
    """An explicit "refs" on the request or the card wins (an empty list means "no reference for this card");
    otherwise cards use their character's reference image when it is enabled."""
    if "refs" in req or "refs" in ((card or {}).get("params") or {}):
        return [r for r in (gen.get("refs") or []) if r]
    return _character_ref(card)


def _character_ref(card):
    return sts2.card_refs(dict(card or {}, params={}))


def make_jobs(card, req, settings):
    base = resolve_params(card, req, settings)
    card_params = (card or {}).get("params") or {}
    count = max(1, min(16, int(req.get("count") or card_params.get("variants") or settings["gen"].get("variants") or 1)))
    seed = req.get("seed") if req.get("seed") not in (None, "") else card_params.get("seed")
    ids = []
    for i in range(count):
        p = dict(base, seed=(int(seed) + i) if seed not in (None, "") else random.randint(0, 2**50))
        ids.append(store.add_job(card["id"] if card else None, p))
    return ids


class Worker:
    def __init__(self, comfy, thermal):
        self.comfy = comfy
        self.thermal = thermal
        self.paused = False
        self.current = None
        self.step = (0, 0)
        self.preview = None             # latest JPEG preview of the running job
        self.cooling_until = 0.0
        self._wake = asyncio.Event()
        self._cancel_current = False
        self._skip_cooldown = False
        self.last_finished = 0.0
        self.cool_reason = ""           # "heat" | "interval"
        self.cool_peak = None
        self.cool_since = 0.0

    def wake(self):
        self._wake.set()

    def skip_cooldown(self):
        self._skip_cooldown = True
        self.wake()

    def status(self):
        return {"paused": self.paused, "current": self.current, "step": self.step[0], "steps": self.step[1],
                "queued": store.count_queued(),
                "cooldown_left": max(0, round(self.cooling_until - time.time())) if self.cooling_until else 0,
                "cooling": bool(self.cooling_until), "cool_reason": self.cool_reason,
                "cool_peak": self.cool_peak, "cool_since": self.cool_since}

    async def _sleep(self, seconds):
        self._wake.clear()
        try:
            await asyncio.wait_for(self._wake.wait(), seconds)
        except asyncio.TimeoutError:
            pass

    async def cancel(self, jid):
        job = store.get_job(jid)
        if not job:
            return
        if jid == self.current:
            self._cancel_current = True
            await self.comfy.interrupt()
        elif job["status"] == "queued":
            store.update_job(jid, status="cancelled", finished=time.time())

    async def loop(self):
        store.recover_after_restart()
        while True:
            if self.paused:
                await self._sleep(5)
                continue
            if not store.count_queued():
                await self._sleep(5)
                continue
            await self._cool_down()
            job = store.next_job()        # re-read: the queue may have changed while cooling
            if job and not self.paused:
                await self._run(job)
                self.last_finished = time.time()

    async def _cool_down(self):
        """Overheat protection with hysteresis: at or above `cool_trigger` °C, wait until the GPU is back at or
        below `cool_temp` °C (never longer than `cool_max` s). `cooldown` adds an optional fixed gap. Skippable."""
        gen = config.load()["gen"]
        trigger = float(gen.get("cool_trigger") or 0)
        resume = float(gen.get("cool_temp") or 70)
        max_wait = float(gen.get("cool_max") or 900)
        min_until = self.last_finished + float(gen.get("cooldown") or 0)
        for _ in range(10):             # right after startup the monitor may not have sampled yet
            if not trigger or self.thermal.temp is not None or self.thermal.name == "n/a":
                break
            await asyncio.sleep(1)
        temp = self.thermal.temp
        hot = bool(trigger and temp is not None and temp >= trigger)
        start = time.time()
        self._skip_cooldown = False
        self.cool_peak, self.cool_since = (temp, start) if hot else (None, 0.0)
        while not self._skip_cooldown and not self.paused:
            now, temp = time.time(), self.thermal.temp
            if hot and temp is not None:
                self.cool_peak = max(self.cool_peak or temp, temp)
                if temp <= resume:
                    hot = False
            if (not hot and now >= min_until) or now - start >= max_wait:
                break
            self.cool_reason = "heat" if hot else "interval"
            self.cooling_until = now + 2 if hot else min_until
            await self._sleep(2)
        self.cooling_until, self.cool_reason, self.cool_peak, self.cool_since = 0, "", None, 0.0

    async def _run(self, job):
        jid, p = job["id"], job["params"]
        self.current, self.step, self.preview, self._cancel_current = jid, (0, p["steps"]), None, False
        store.update_job(jid, status="running", started=time.time(), progress=0, message=config.tr("准备 ComfyUI…", "Preparing ComfyUI…"))
        t0 = time.time()
        try:
            await self.comfy.ensure()
            store.update_job(jid, message=config.tr("生成中（首张需加载模型，较慢）", "Generating (the first image loads the models, slower)"))
            refs = [await self.comfy.upload(r) for r in p.get("refs") or []]
            init = await self.comfy.upload(p["init"]) if p.get("init") else None
            note = ""
            if p.get("lora") and p["lora"] not in self.comfy.lora_list():
                note = config.tr(f"（未找到 LoRA {p['lora']}，已用基础模型生成）", f" (LoRA {p['lora']} not installed; used the base model)")
                p = dict(p, lora="")
            g = graph.build(p, refs, init, unet=config.load().get("unet_file") or graph.UNET)

            def on_progress(value, maximum):
                self.step = (value, maximum)
                store.update_job(jid, progress=value / max(1, maximum), message=config.tr("步骤", "Step") + f" {value}/{maximum}")

            def on_preview(data):
                self.preview = data

            png = await self.comfy.run(g, on_progress, on_preview)
            if self._cancel_current:
                raise Interrupted()
            img = Image.open(io.BytesIO(png))
            folder = config.IMAGES / (str(job["card_id"]) if job["card_id"] else "loose")
            folder.mkdir(parents=True, exist_ok=True)
            path = folder / f"{jid:05d}_{p['seed']}.png"
            img.save(path)
            rec = store.add_image(job["card_id"], path, img.width, img.height, p["seed"], p["prompt"], p)
            store.update_job(jid, status="done", progress=1, image_id=rec["id"], finished=time.time(),
                             message=config.tr("完成", "Done") + f" {time.time() - t0:.0f}s" + note)
        except Interrupted:
            store.update_job(jid, status="cancelled", finished=time.time(), message=config.tr("已取消", "Cancelled"))
        except Exception as e:  # keep the queue alive; the message is shown in the UI
            traceback.print_exc()
            store.update_job(jid, status="failed", finished=time.time(), message=str(e)[:1000])
        finally:
            self.current, self.preview = None, None
