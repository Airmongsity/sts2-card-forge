"""Samples the GPU temperature in the background and keeps a short history for the thermal UI."""
import asyncio
import time
from collections import deque

from comfy import gpu_info

INTERVAL = 3            # seconds between samples
HISTORY = 200           # ~10 minutes


class Thermal:
    def __init__(self):
        self.name = ""
        self.vram = 0
        self.temp = None
        self.history = deque(maxlen=HISTORY)

    async def loop(self):
        while True:
            info = await asyncio.to_thread(gpu_info)
            if info:
                self.name, self.vram, self.temp = info
                self.history.append((round(time.time(), 1), self.temp))
            else:
                self.name = "n/a"           # no NVIDIA GPU: don't make the worker wait for a reading
            await asyncio.sleep(INTERVAL)

    def gpu(self):
        return {"name": self.name, "vram": self.vram, "temp": self.temp} if self.temp is not None else None
