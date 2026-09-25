"""Runs a local ComfyUI as a child process and talks to its HTTP/websocket API."""
import asyncio
import json
import re
import shlex
import struct
import subprocess
import time
import uuid
from pathlib import Path

import aiohttp

import config


class Interrupted(Exception):
    pass


# ComfyUI flags per performance profile. This ComfyUI uses dynamic VRAM by default (so --lowvram is a no-op);
# what helps small machines is reserving VRAM for the desktop, not pinning RAM, and not caching node outputs.
PROFILE_FLAGS = {
    "standard": [],
    "low": ["--reserve-vram", "1.0", "--disable-pinned-memory"],
    "minimal": ["--novram", "--disable-pinned-memory", "--cache-none"],
}


def gpu_info():
    """(name, total VRAM MiB, temperature °C) of the first NVIDIA GPU, or None."""
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total,temperature.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW).stdout
        name, mem, temp = [x.strip() for x in out.splitlines()[0].split(",")]
        return name, int(mem), int(temp)
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return None


def total_ram_mib():
    import ctypes

    class MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong)] + \
                   [(n, ctypes.c_ulonglong) for n in ("total", "avail", "tpf", "apf", "tv", "av", "aev")]
    m = MEMORYSTATUSEX()
    m.dwLength = ctypes.sizeof(m)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.total // (1024 * 1024)


def resolve_profile(name, s=None):
    if name in PROFILE_FLAGS:
        return name
    info = gpu_info()
    # AMD / Intel have no nvidia-smi: the app stores the VRAM it read from the display adapter
    vram = info[1] if info else int((s or config.load()).get("gpu_vram_mib") or 0)
    if vram and vram < 5500:
        return "minimal"
    if vram < 7000 or total_ram_mib() < 14000:
        return "low"
    return "standard"


class Comfy:
    def __init__(self):
        self.proc = None
        self.state = "stopped"          # stopped | starting | ready | external | error
        self.error = ""
        self.profile = ""
        self.log_path = config.LOGS / "comfyui.log"
        self._start_lock = asyncio.Lock()
        self._session = None

    @property
    def url(self):
        return f"http://127.0.0.1:{config.load()['comfy_port']}"

    async def session(self):
        if self._session is None or self._session.closed:
            self._session = aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=None, sock_connect=5))
        return self._session

    async def ping(self):
        try:
            s = await self.session()
            async with s.get(self.url + "/system_stats", timeout=aiohttp.ClientTimeout(total=3)) as r:
                return r.status == 200
        except (aiohttp.ClientError, asyncio.TimeoutError, OSError):
            return False

    def status(self):
        return {"state": self.state, "error": self.error, "url": self.url, "profile": self.profile,
                "pid": self.proc.pid if self.proc and self.proc.returncode is None else None}

    async def start(self):
        async with self._start_lock:
            if self.proc and self.proc.returncode is None and await self.ping():
                self.state = "ready"
                return
            if await self.ping():
                self.state = "external"     # already started outside the app; reuse it
                return
            s = config.load()
            py, cdir = config.comfy_python(s), config.comfy_dir(s)
            if not py or not (cdir / "main.py").exists():
                self.state, self.error = "error", config.tr("找不到 ComfyUI，请先完成环境安装: ", "ComfyUI not found, finish Setup first: ") + s["comfy_root"]
                return
            args = [str(py), "-s", str(cdir / "main.py"), "--windows-standalone-build", "--disable-auto-launch",
                    "--port", str(s["comfy_port"])]
            if s.get("gpu_package") == "cpu":
                self.profile = "cpu"
                args += ["--cpu"]
            else:
                self.profile = resolve_profile(s.get("comfy_profile", "auto"), s)
                args += PROFILE_FLAGS[self.profile]
            if s.get("comfy_preview", "latent2rgb") != "none":
                args += ["--preview-method", s["comfy_preview"]]
            args += shlex.split(s.get("comfy_extra_args") or "", posix=False)
            self.state, self.error = "starting", ""
            log = open(self.log_path, "ab")
            log.write(f"\n==== {time.ctime()} {' '.join(args)}\n".encode())
            self.proc = await asyncio.create_subprocess_exec(
                *args, cwd=str(cdir.parent), stdout=log, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW)
            log.close()
            for _ in range(300):
                if self.proc.returncode is not None:
                    self.state, self.error = "error", config.tr("ComfyUI 已退出，请查看日志", "ComfyUI exited, see the log") + f" (code {self.proc.returncode})"
                    return
                if await self.ping():
                    self.state = "ready"
                    return
                await asyncio.sleep(1)
            self.state, self.error = "error", config.tr("ComfyUI 启动超时", "ComfyUI start timed out")

    async def stop(self):
        if self.proc and self.proc.returncode is None:
            self.proc.terminate()
            try:
                await asyncio.wait_for(self.proc.wait(), 15)
            except asyncio.TimeoutError:
                self.proc.kill()
        self.proc = None
        self.state = "external" if await self.ping() else "stopped"

    async def ensure(self):
        if self.state in ("ready", "external") and await self.ping():
            return
        await self.start()
        if self.state not in ("ready", "external"):
            raise RuntimeError(self.error or config.tr("ComfyUI 未就绪", "ComfyUI is not ready"))

    def log_tail(self, n=200):
        try:
            with open(self.log_path, "rb") as f:
                f.seek(0, 2)
                f.seek(max(0, f.tell() - 64 * 1024))
                lines = f.read().decode("utf-8", "replace").splitlines()
            return re.sub(r"\x1b\[[0-9;]*m", "", "\n".join(lines[-n:]))   # strip ANSI colours
        except FileNotFoundError:
            return ""

    # ---- API ---------------------------------------------------------------------------------

    async def upload(self, path):
        path = Path(path)
        s = await self.session()
        form = aiohttp.FormData()
        form.add_field("overwrite", "true")
        form.add_field("image", path.read_bytes(), filename=path.name, content_type="application/octet-stream")
        async with s.post(self.url + "/upload/image", data=form) as r:
            r.raise_for_status()
            res = await r.json()
        return (res["subfolder"] + "/" if res.get("subfolder") else "") + res["name"]

    async def interrupt(self):
        try:
            s = await self.session()
            async with s.post(self.url + "/interrupt", json={}) as r:
                await r.read()
        except aiohttp.ClientError:
            pass

    async def run(self, graph, on_progress=None, on_preview=None, latents=None):
        """Queue a graph, stream progress/previews over the websocket, return the output PNGs (one per batch item).
        Files of SaveLatent nodes are appended to `latents` when a list is given."""
        s = await self.session()
        client_id = uuid.uuid4().hex
        ws_url = self.url.replace("http", "ws") + f"/ws?clientId={client_id}"
        async with s.ws_connect(ws_url, heartbeat=30, max_msg_size=64 * 1024 * 1024) as ws:
            async with s.post(self.url + "/prompt", json={"prompt": graph, "client_id": client_id}) as r:
                res = await r.json(content_type=None)
            if r.status != 200 or res.get("node_errors"):
                raise RuntimeError(json.dumps(res.get("node_errors") or res.get("error") or res, ensure_ascii=False)[:1500])
            pid = res["prompt_id"]
            async for msg in ws:
                if msg.type == aiohttp.WSMsgType.BINARY and on_preview:
                    data = msg.data
                    kind = struct.unpack(">I", data[:4])[0]
                    if kind == 1:                                  # PREVIEW_IMAGE
                        on_preview(data[8:])
                    elif kind == 4:                                # PREVIEW_IMAGE_WITH_METADATA
                        meta_len = struct.unpack(">I", data[4:8])[0]
                        on_preview(data[8 + meta_len:])
                    continue
                if msg.type != aiohttp.WSMsgType.TEXT:
                    if msg.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR):
                        break
                    continue
                ev = json.loads(msg.data)
                d = ev.get("data") or {}
                if d.get("prompt_id") not in (None, pid):
                    continue
                t = ev.get("type")
                if t == "progress" and on_progress:
                    on_progress(d.get("value", 0), d.get("max", 1))
                elif t == "execution_error":
                    raise RuntimeError(f"{d.get('exception_type', '')}: {d.get('exception_message', '')}".strip())
                elif t == "execution_interrupted":
                    raise Interrupted()
                elif t == "execution_success" or (t == "executing" and d.get("node") is None and d.get("prompt_id") == pid):
                    break
        return await self._fetch_output(pid, latents)

    async def _fetch_output(self, pid, latents=None):
        s = await self.session()
        for _ in range(3600):
            async with s.get(f"{self.url}/history/{pid}") as r:
                hist = await r.json()
            if pid in hist:
                entry = hist[pid]
                if entry.get("status", {}).get("status_str") == "error":
                    raise RuntimeError(json.dumps(entry["status"].get("messages"), ensure_ascii=False)[:1500])
                pngs = []
                for out in entry.get("outputs", {}).values():
                    for img in out.get("images", []):
                        if img.get("type") != "output":        # previews of intermediate nodes
                            continue
                        params = {"filename": img["filename"], "subfolder": img["subfolder"], "type": img["type"]}
                        async with s.get(self.url + "/view", params=params) as r2:
                            r2.raise_for_status()
                            pngs.append(await r2.read())
                if pngs:
                    for out in entry.get("outputs", {}).values():
                        for f in out.get("latents", []) if latents is not None else []:
                            params = {"filename": f["filename"], "subfolder": f["subfolder"], "type": f.get("type", "output")}
                            async with s.get(self.url + "/view", params=params) as r2:
                                r2.raise_for_status()
                                latents.append(await r2.read())
                    return pngs
                if entry.get("status", {}).get("completed"):
                    raise RuntimeError(config.tr("ComfyUI 完成但没有输出图像", "ComfyUI finished without an image"))
            await asyncio.sleep(1)
        raise RuntimeError(config.tr("等待 ComfyUI 输出超时", "Timed out waiting for ComfyUI output"))

    def lora_list(self):
        root = config.comfy_dir() / "models" / "loras"
        if not root.exists():
            return []
        return sorted(p.relative_to(root).as_posix() for p in root.rglob("*.safetensors"))
