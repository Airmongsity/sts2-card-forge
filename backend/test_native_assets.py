import json
import sys
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))

import config
import native_assets
import store
import worker


class NativeAssetsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.old_library = config.NATIVE_LIBRARY
        self.old_data = config.DATA
        config.DATA = self.root / "data"
        config.DATA.mkdir()
        config.NATIVE_LIBRARY = self.root / "library"
        config.NATIVE_LIBRARY.mkdir()
        self.assets = []

    def tearDown(self):
        for aid in self.assets:
            native_assets.delete_asset(aid)
        config.NATIVE_LIBRARY = self.old_library
        config.DATA = self.old_data
        self.temp.cleanup()

    def test_scan_finds_complete_bundle_and_excludes_atlas_page(self):
        (self.root / "hero.json").write_text(json.dumps({"skeleton": {"spine": "4.2.10"}}), encoding="utf-8")
        (self.root / "hero.atlas").write_text("hero.png\nsize: 8, 8\nregion\n  bounds: 0, 0, 8, 8\n", encoding="utf-8")
        Image.new("RGBA", (8, 8), "red").save(self.root / "hero.png")
        Image.new("RGB", (12, 10), "blue").save(self.root / "portrait.jpg")

        found = native_assets.scan(self.root)

        self.assertEqual(found["counts"], {"images": 1, "spine": 1, "ready_spine": 1, "packages": 0})
        self.assertEqual(found["spine"][0]["version"], "4.2.10")
        self.assertEqual(Path(found["images"][0]["path"]).name, "portrait.jpg")

    def test_scan_uses_ui_language_name_from_recovered_translation_csv(self):
        Image.new("RGBA", (8, 8), "blue").save(self.root / "fuel.png")
        (self.root / "translations.csv").write_text("key,en,zh-CN\nTOKEN_FUEL_NAME,Fuel,燃料\n", encoding="utf-8")

        found = native_assets.scan(self.root, "zh-CN")

        self.assertEqual(found["images"][0]["name"], "燃料")

    def test_scan_uses_sts2_zhs_json_names(self):
        image_dir = self.root / "images" / "monsters" / "aeonglass"
        image_dir.mkdir(parents=True)
        Image.new("RGBA", (8, 8), "blue").save(image_dir / "aeonglass.png")
        locale = self.root / "localization" / "zhs"
        locale.mkdir(parents=True)
        (locale / "monsters.json").write_text(json.dumps({"AEONGLASS.name": "永世沙漏"}), encoding="utf-8")

        found = native_assets.scan(self.root, "zh-CN")

        self.assertEqual(found["images"][0]["name"], "永世沙漏")

    def test_binary_spine_42_version_after_fixed_hash(self):
        skeleton = self.root / "monster.skel"
        skeleton.write_bytes(bytes.fromhex("AEA4F3030F7F7F02") + b"\x074.2.43\x00")

        self.assertEqual(native_assets._binary_spine_version(skeleton), "4.2.43")

    def test_import_can_be_used_by_cloud_jobs(self):
        source = self.root / "enemy.png"
        Image.new("RGBA", (16, 20), (10, 20, 30, 255)).save(source)
        asset = native_assets.import_image(source, category="native", role="enemy")
        self.assets.append(asset["id"])

        self.assertTrue(Path(asset["path"]).exists())
        self.assertEqual(asset["local_only"], 0)
        settings = config.load()
        settings["image_provider"] = "openai"
        card = {"prompt": "test prompt", "cls": "colorless", "params": {"refs": [asset["path"]]}}
        params = worker.resolve_params(card, {}, settings)
        self.assertEqual(params["refs"], [asset["path"]])
        self.assertEqual(params["image_provider"], "openai")

    def test_multiple_references_survive_job_resolution(self):
        refs = []
        for name, color in (("first.png", "red"), ("second.png", "blue"), ("third.png", "green")):
            source = self.root / name
            Image.new("RGBA", (16, 20), color).save(source)
            refs.append(str(source))
        card = {"prompt": "three referenced subjects", "cls": "colorless", "params": {"refs": refs}}

        params = worker.resolve_params(card, {}, config.load())

        self.assertEqual(params["refs"], refs)

    def test_missing_library_file_is_pruned(self):
        source = self.root / "temporary_asset.png"
        Image.new("RGBA", (16, 20), "blue").save(source)
        asset = native_assets.import_image(source, "temporary_asset_" + self.root.name, category="custom")
        Path(asset["path"]).unlink()

        removed = store.prune_missing_reference_assets()

        self.assertIn(asset["id"], [item["id"] for item in removed])
        self.assertIsNone(store.get_reference_asset(asset["id"]))

    def test_manual_import_keeps_user_filename(self):
        source = self.root / "My_Custom_Fuel.png"
        Image.new("RGBA", (16, 20), "blue").save(source)

        asset = native_assets.import_image(source, category="custom", role="object", language="zh-CN")
        self.assets.append(asset["id"])

        self.assertEqual(asset["name"], "My_Custom_Fuel")
        self.assertEqual(Path(asset["path"]).name, "My_Custom_Fuel.png")

    def test_one_click_native_import_uses_localized_name_once(self):
        source = self.root / "fuel.png"
        Image.new("RGBA", (16, 20), "blue").save(source)
        name = "燃料测试_" + self.root.name.replace("-", "_")

        asset = native_assets.import_image(source, name, category="native", role="object")
        self.assets.append(asset["id"])

        self.assertEqual(asset["name"], name)
        self.assertEqual(Path(asset["path"]).name, native_assets._safe_name(name) + ".png")

    def test_batch_import_skips_bad_image_and_continues(self):
        valid = self.root / "valid.png"
        invalid = self.root / "broken.png"
        Image.new("RGBA", (16, 20), "blue").save(valid)
        invalid.write_bytes(b"not an image")

        result = native_assets.import_many([
            {"path": str(valid), "name": "有效模板"},
            {"path": str(invalid), "name": "损坏模板"},
        ], category="native", role="style", language="zh-CN")

        self.assertEqual(result["total"], 2)
        self.assertEqual(result["imported"], 1)
        self.assertEqual(result["failed"], 1)
        self.assertEqual(Path(result["errors"][0]["path"]), invalid)
        imported = store.list_reference_assets()
        self.assets.extend(asset["id"] for asset in imported if asset["name"] == "有效模板")
        self.assertTrue(any(asset["name"] == "有效模板" for asset in imported))

    def test_native_rescan_localizes_existing_record_in_place(self):
        source = self.root / "fuel.png"
        Image.new("RGBA", (16, 20), "blue").save(source)
        unique = self.root.name.replace("-", "_")
        first = native_assets.import_image(source, f"Before_{unique}", category="native")
        second = native_assets.import_image(source, f"After_{unique}", category="native")
        self.assets.append(second["id"])

        self.assertEqual(second["id"], first["id"])
        self.assertEqual(second["name"], f"After_{unique}")
        self.assertFalse(Path(first["path"]).exists())
        self.assertTrue(Path(second["path"]).exists())

    def test_prompt_full_name_match_attaches_template_and_binding(self):
        source = self.root / "fuel.png"
        Image.new("RGBA", (16, 20), "blue").save(source)
        name = "燃料测试" + self.root.name.replace("-", "_")
        asset = native_assets.import_image(source, name, category="native", role="object", language="zh-CN")
        self.assets.append(asset["id"])
        settings = config.load()
        card = {"prompt": f"角色举起{name}。", "cls": "colorless", "params": {"refs": []}}

        params = worker.resolve_params(card, {}, settings)

        self.assertEqual(params["refs"], [asset["path"]])
        self.assertIn("<image1>", params["prompt"])

    def test_prompt_does_not_match_partial_latin_filename(self):
        source = self.root / "fuel.png"
        Image.new("RGBA", (16, 20), "blue").save(source)
        asset = native_assets.import_image(source, "Fuel", category="native", role="object", language="en-US")
        self.assets.append(asset["id"])
        prompt, refs = worker._attach_named_library_refs("A colorful fuelled engine", [], "en")

        self.assertEqual(refs, [])
        self.assertNotIn("<image", prompt)

    def test_style_asset_name_does_not_auto_attach(self):
        source = self.root / "create.png"
        Image.new("RGBA", (16, 20), "blue").save(source)
        name = "创造测试" + self.root.name.replace("-", "_")
        asset = native_assets.import_image(source, name, category="native", role="style")
        self.assets.append(asset["id"])

        prompt, refs = worker._attach_named_library_refs(f"角色正在{name}一个复制体", [], "zh")

        self.assertEqual(refs, [])
        self.assertNotIn("<image", prompt)

    def test_same_name_is_skipped_without_overwrite(self):
        first_source = self.root / "first.png"
        second_source = self.root / "second.png"
        Image.new("RGBA", (8, 8), "red").save(first_source)
        Image.new("RGBA", (8, 8), "blue").save(second_source)
        name = "同名测试" + self.root.name.replace("-", "_")
        first = native_assets.import_image(first_source, name, category="native")
        second = native_assets.import_image(second_source, name, category="native")
        self.assets.append(first["id"])

        self.assertEqual(second["id"], first["id"])
        self.assertTrue(second["_skipped"])
        with Image.open(first["path"]) as image:
            self.assertEqual(image.getpixel((0, 0))[:3], (255, 0, 0))

    def test_native_card_portrait_names_are_scoped_by_card_group(self):
        cards = []
        for group, color in (("ironclad", "red"), ("defect", "blue")):
            source = self.root / "extracted" / "images" / "packed" / "card_portraits" / group / "same_card.png"
            source.parent.mkdir(parents=True)
            Image.new("RGBA", (8, 8), color).save(source)
            cards.append({"path": str(source), "name": "同名卡牌", "localized": True})

        result = native_assets.import_many(cards, category="native", role="style", language="zh-CN")

        self.assertEqual((result["imported"], result["skipped"], result["failed"]), (2, 0, 0))
        records = store.list_reference_assets("native")
        grouped = [asset for asset in records if (asset.get("metadata") or {}).get("source_group")]
        self.assertEqual({asset["metadata"]["source_group"] for asset in grouped}, {"ironclad", "defect"})
        self.assertEqual(len({asset["path"] for asset in grouped}), 2)
        self.assets.extend(asset["id"] for asset in grouped)

    def test_unlocalized_native_asset_is_internal(self):
        source = self.root / "engine_glow_mask.png"
        Image.new("RGBA", (8, 8), "red").save(source)
        asset = native_assets.import_image(source, "Engine Glow Mask", category="native", metadata={"localized": False})
        self.assets.append(asset["id"])

        self.assertTrue(native_assets.is_internal_asset(asset))

    def test_discovers_sts2_in_secondary_steam_library(self):
        steam = self.root / "steam"
        library = self.root / "games"
        (steam / "steamapps").mkdir(parents=True)
        escaped = str(library).replace("\\", "\\\\")
        (steam / "steamapps" / "libraryfolders.vdf").write_text(
            f'"libraryfolders"\n{{\n  "1" {{ "path" "{escaped}" }}\n}}', encoding="utf-8")
        steamapps = library / "steamapps"
        game = steamapps / "common" / "Slay the Spire 2"
        game.mkdir(parents=True)
        (game / "sts2.pck").write_bytes(b"pck")
        (steamapps / "appmanifest_2868840.acf").write_text(
            '"AppState" { "installdir" "Slay the Spire 2" }', encoding="utf-8")

        result = native_assets.discover_games([steam])

        self.assertIn(str(game.resolve()), result["found"])

    def test_extract_pck_uses_versioned_cache(self):
        gdre = self.root / "gdre_tools.exe"
        gdre.write_bytes(b"")
        pck = self.root / "sts2.pck"
        pck.write_bytes(b"owned game package")
        completed = SimpleNamespace(returncode=0, stdout="done", stderr="")

        with patch("native_assets.subprocess.run", return_value=completed) as run:
            first = native_assets.extract_pck({"gdre": str(gdre), "pck": str(pck)})
            parent = config.DATA / "native_extracted"
            stale_partial = parent / ("a" * 16 + ".dead.partial")
            stale_partial.mkdir()
            (stale_partial / "partial.bin").write_bytes(b"old")
            second = native_assets.extract_pck({"gdre": str(gdre), "pck": str(pck)})

        self.assertFalse(first["cached"])
        self.assertTrue(second["cached"])
        self.assertFalse(stale_partial.exists())
        self.assertEqual(first["root"], second["root"])
        self.assertIn("--headless", run.call_args.args[0])
        self.assertTrue((Path(first["root"]) / ".cardforge-extraction.json").is_file())

    def test_extract_pck_removes_old_managed_results_but_keeps_unrecognized_data(self):
        gdre = self.root / "gdre_tools.exe"
        gdre.write_bytes(b"")
        pck = self.root / "first.pck"
        pck.write_bytes(b"first package")
        completed = SimpleNamespace(returncode=0, stdout="done", stderr="")
        with patch("native_assets.subprocess.run", return_value=completed):
            first = native_assets.extract_pck({"gdre": str(gdre), "pck": str(pck)})

            cache = config.DATA / "native_extracted"
            stale_partial = cache / ("a" * 16 + ".dead.partial")
            stale_partial.mkdir()
            (stale_partial / "partial.bin").write_bytes(b"old")
            stale_cache = cache / ("b" * 16)
            stale_cache.mkdir()
            (stale_cache / ".cardforge-extraction.json").write_text("{}", encoding="utf-8")
            user_data = cache / "keep-this"
            user_data.mkdir()
            (user_data / "notes.txt").write_text("keep", encoding="utf-8")

            pck.write_bytes(b"second package, different fingerprint")
            second = native_assets.extract_pck({"gdre": str(gdre), "pck": str(pck)})

        self.assertFalse(second["cached"])
        self.assertFalse(Path(first["root"]).exists())
        self.assertFalse(stale_partial.exists())
        self.assertFalse(stale_cache.exists())
        self.assertTrue((user_data / "notes.txt").is_file())

    def test_concurrent_duplicate_extraction_runs_gdre_once(self):
        gdre = self.root / "gdre_tools.exe"
        gdre.write_bytes(b"")
        pck = self.root / "sts2.pck"
        pck.write_bytes(b"one package")
        completed = SimpleNamespace(returncode=0, stdout="done", stderr="")
        start = threading.Barrier(2)

        def delayed_run(*args, **kwargs):
            time.sleep(0.05)
            return completed

        def extract():
            start.wait()
            return native_assets.extract_pck({"gdre": str(gdre), "pck": str(pck)})

        with patch("native_assets.subprocess.run", side_effect=delayed_run) as run:
            with ThreadPoolExecutor(max_workers=2) as pool:
                results = list(pool.map(lambda _: extract(), range(2)))

        self.assertEqual(run.call_count, 1)
        self.assertEqual(sum(result["cached"] for result in results), 1)
        self.assertEqual(results[0]["root"], results[1]["root"])


if __name__ == "__main__":
    unittest.main()
