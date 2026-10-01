"""Cloud jobs bypass ComfyUI and run several at once; local jobs stay strictly serial."""
import asyncio
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import config
import store
import worker


def _settings(provider, concurrency=3):
    s = config.load()
    s.update(image_provider=provider, cloud_concurrency=concurrency, image_preset="siliconflow",
             image_api_base_url="https://api.example.test/v1", image_api_model="m")
    return s


class FakeComfy:
    def __init__(self):
        self.calls = 0

    async def ensure(self):
        self.calls += 1
        raise AssertionError("ComfyUI must not be used for cloud jobs")

    async def interrupt(self):
        pass


class FakeThermal:
    temp, name = None, "n/a"


class ResolveTests(unittest.TestCase):
    def test_cloud_params_carry_provider_and_one_image_per_job(self):
        p = worker.resolve_params({"prompt": "x", "params": {}}, {}, _settings("openai"))
        self.assertEqual(p["image_provider"], "openai")
        self.assertEqual(p["batch"], 1)
        self.assertEqual(p["image_api_model"], "m")

    def test_cloud_drafts_become_normal_jobs(self):
        p = worker.resolve_params({"prompt": "x", "params": {}}, {"mode": "draft"}, _settings("openai"))
        self.assertEqual(p["mode"], "normal")

    def test_local_params_stay_local(self):
        p = worker.resolve_params({"prompt": "x", "params": {}}, {}, _settings("comfy"))
        self.assertEqual(p["image_provider"], "comfy")
        self.assertFalse(worker.is_cloud({"params": p}))


class SchedulerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        # an in-memory database, so the user's real queue and gallery are never touched
        self.temp = tempfile.TemporaryDirectory()
        self.old_images, self.old_db = config.IMAGES, store._db
        config.IMAGES = Path(self.temp.name)
        db = sqlite3.connect(":memory:", check_same_thread=False)
        db.row_factory = sqlite3.Row
        db.executescript(store.SCHEMA)
        store._db = db

    def tearDown(self):
        store._db.close()
        store._db, config.IMAGES = self.old_db, self.old_images
        self.temp.cleanup()

    async def _run(self, settings, jobs, fake_generate):
        ids = [store.add_job(None, dict(worker.resolve_params(None, {"prompt": f"p{i}"}, settings), seed=i))
               for i in range(jobs)]
        comfy = FakeComfy()
        w = worker.Worker(comfy, FakeThermal())
        with mock.patch.object(worker.config, "load", return_value=settings), \
             mock.patch.object(worker.cloud_image, "generate", fake_generate):
            w._schedule()
            peak = len(w.cloud_running)
            while w.cloud_running:
                await asyncio.gather(*w.cloud_running.values(), return_exceptions=True)
                w._schedule()
        return ids, comfy, peak

    async def test_cloud_jobs_run_concurrently_without_comfy(self):
        from io import BytesIO
        from PIL import Image
        buf = BytesIO()
        Image.new("RGB", (8, 8)).save(buf, format="PNG")
        active, peak = 0, 0

        async def fake_generate(p, s):
            nonlocal active, peak
            active += 1
            peak = max(peak, active)
            await asyncio.sleep(0.05)
            active -= 1
            return [buf.getvalue()]

        ids, comfy, started = await self._run(_settings("openai", 2), 5, fake_generate)
        self.assertEqual(comfy.calls, 0)
        self.assertEqual(started, 2)
        self.assertEqual(peak, 2)
        self.assertTrue(all(store.get_job(i)["status"] == "done" for i in ids))

    async def test_cloud_failure_marks_job_failed(self):
        async def fake_generate(p, s):
            raise RuntimeError("boom")

        ids, _, _ = await self._run(_settings("openai"), 1, fake_generate)
        self.assertEqual(store.get_job(ids[0])["status"], "failed")
        self.assertIn("boom", store.get_job(ids[0])["message"])


if __name__ == "__main__":
    unittest.main()
