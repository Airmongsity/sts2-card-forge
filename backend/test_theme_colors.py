import random
import sys
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

import promptgen
import sts2


class ThemeColourTests(unittest.TestCase):
    def test_inherited_pool_always_samples_one_colour(self):
        card = {"cls": "silent", "params": {}}
        pool = set(sts2.character_color_pool("silent"))
        samples = [sts2.choose_card_themes(card, random.Random(seed)) for seed in range(100)]
        self.assertTrue(all(len(sample) == 1 for sample in samples))
        self.assertTrue(all(sample[0] in pool for sample in samples))

    def test_explicit_multi_colour_override_is_preserved(self):
        colors = ["#269ccb", "#ccbc4d"]
        card = {"cls": "silent", "params": {"theme_colors": colors}}
        self.assertEqual(sts2.choose_card_themes(card, random.Random(1)), colors)

    def test_llm_brief_keeps_render_palette_out_of_creative_body(self):
        card = {"cls": "colorless", "concept": "three cards flying diagonally",
                "params": {"theme_colors": ["#269ccb", "#ccbc4d"]}}
        brief = promptgen._card_brief(card)
        self.assertNotIn("#269ccb", brief)
        self.assertNotIn("#ccbc4d", brief)
        self.assertNotIn("Colour theme of this card", brief)
        self.assertIn("PALETTE IS APPLIED AFTER PROMPT WRITING", brief)

    def test_theme_is_appended_after_creative_prompt(self):
        card = {"params": {"theme_colors": ["#269ccb"]}}
        result = promptgen._append_theme(
            "A neutral scene. Required theme colours: vivid red (#ff0000).", card, "en")
        self.assertTrue(result.startswith("A neutral scene."))
        self.assertIn("#269ccb", result)
        self.assertNotIn("#ff0000", result)
        self.assertEqual(result.count("Required theme colours:"), 1)

    def test_untyped_legacy_reference_is_not_called_a_character(self):
        card = {"cls": "colorless", "concept": "cards based on the card in the reference",
                "params": {"refs": ["fuel.png"]}}
        brief = promptgen._card_brief(card)
        self.assertIn("<image1> = general visual reference", brief)
        self.assertNotIn("<image1> = main character appearance", brief)


class StructuredPromptTests(unittest.TestCase):
    def test_compiler_preserves_the_planned_subject_without_object_specific_rules(self):
        card = {"cls": "colorless", "concept": "创建一个关于自身的镜像", "params": {}}
        raw = {
            "grammar": "process", "composition": "人物与正在形成的复制体紧密重叠",
            "subjects": [{"id": "double", "kind": "character", "label": "半透明复制体", "count": 1,
                          "appearance": "保留人物的帽檐与肩部轮廓", "reference": "",
                          "action": "从施法轨迹中形成", "state": "下半部仍透明"}],
            "relations": ["施法轨迹连接人物与复制体"], "lighting": "侧向轮廓光",
            "background": "简洁纵深", "notes": "B PROCESS",
        }
        prompt, plan = promptgen.compile_scene_plan(raw, card, "zh")
        self.assertEqual(plan["subjects"][0]["count"], 1)
        self.assertIn("半透明复制体", prompt)
        self.assertIn("施法轨迹连接人物与复制体", prompt)

    def test_invalid_reference_tag_is_removed(self):
        card = {"cls": "colorless", "concept": "一盏熄灭中的油灯", "params": {"refs": []}}
        raw = {"grammar": "process", "composition": "近景", "subjects": [
            {"id": "lamp", "kind": "object", "label": "油灯", "count": 1, "appearance": "黄铜灯身",
             "reference": "<image7>", "action": "火焰收缩成烟", "state": "半明半暗"}],
               "relations": [], "lighting": "侧光", "background": "暗色纵深", "notes": "B PROCESS"}
        _, plan = promptgen.compile_scene_plan(raw, card, "zh")
        self.assertEqual(plan["subjects"][0]["reference"], "")

class StructuredPromptAsyncTests(unittest.IsolatedAsyncioTestCase):
    async def test_art_prompt_requests_a_plan_then_compiles_it(self):
        card = {"cls": "colorless", "concept": "three cards flying", "params": {}}
        raw = {"grammar": "action", "composition": "three-quarter view", "subjects": [
            {"id": "cards", "kind": "object", "label": "cards", "count": 3, "appearance": "printed faces",
             "reference": "", "action": "fly along one diagonal", "state": "separated"}],
               "relations": ["near-to-far overlap"], "lighting": "directional high-contrast light",
               "background": "simple background depth", "notes": "A ACTION"}
        settings = {"llm_provider": "openai", "card_language": "en", "prompt_language": "en"}
        with patch.object(promptgen, "_call", AsyncMock(return_value=raw)) as call:
            result = await promptgen.art_prompt(card, settings)
        self.assertIs(call.call_args.args[3], promptgen.PLAN_SCHEMA)
        self.assertTrue(result["prompt"].startswith("sts2 illustration, colorless."))


class ProsePromptTests(unittest.TestCase):
    def test_prose_compiler_integrates_subjects_and_actions_into_flowing_text(self):
        card = {"cls": "silent", "concept": "a dagger thrust", "params": {}}
        raw = {
            "grammar": "ICON", "composition": "close-up crop at the hand and blade",
            "subjects": [
                {"id": "dagger", "kind": "weapon", "label": "curved dagger", "count": 1,
                 "appearance": "blade glinting", "reference": "",
                 "action": "thrusts forward", "state": "poison-green trail flowing from the tip"},
                {"id": "hand", "kind": "body_part", "label": "hand", "count": 1,
                 "appearance": "green cloak sleeve", "reference": "",
                 "action": "gripping the handle", "state": "tense"}
            ],
            "relations": [], "lighting": "thin bright rim-light",
            "background": "flat color background", "notes": ""
        }
        prompt, plan = promptgen.compile_scene_plan_prose(raw, card, "en")
        # Prose style should integrate action and appearance naturally, not as "label: action" lists
        self.assertNotIn("curved dagger:", prompt.lower())
        self.assertNotIn("hand:", prompt.lower())
        self.assertIn("curved dagger", prompt)
        self.assertIn("thrusts forward", prompt)
        self.assertIn("gripping", prompt)
        self.assertIn("poison-green trail", prompt)
        self.assertIn("rim-light", prompt)
        self.assertTrue(prompt.startswith("sts2 illustration, silent."))

    def test_prose_compiler_preserves_reference_bindings(self):
        card = {"cls": "colorless", "concept": "character based on reference", "params": {"refs": ["fuel.png"]}}
        raw = {
            "grammar": "ICON", "composition": "portrait",
            "subjects": [
                {"id": "char", "kind": "character", "label": "character", "count": 1,
                 "appearance": "hooded cloak", "reference": "<image1>",
                 "action": "standing", "state": "calm"}
            ],
            "relations": [], "lighting": "side lighting", "background": "simple depth", "notes": ""
        }
        prompt, plan = promptgen.compile_scene_plan_prose(raw, card, "en")
        self.assertIn("from <image1>", prompt)
        self.assertIn("character", prompt)

    def test_prose_compiler_handles_multiple_subjects(self):
        card = {"cls": "colorless", "concept": "two entities interacting", "params": {}}
        raw = {
            "grammar": "RELATIONSHIP", "composition": "side by side",
            "subjects": [
                {"id": "left", "kind": "character", "label": "warrior", "count": 1,
                 "appearance": "armor", "reference": "", "action": "reaching forward", "state": "alert"},
                {"id": "right", "kind": "character", "label": "mage", "count": 1,
                 "appearance": "robe", "reference": "", "action": "channeling energy", "state": "focused"}
            ],
            "relations": ["energy flows between them"],
            "lighting": "backlight", "background": "dark void", "notes": ""
        }
        prompt, plan = promptgen.compile_scene_plan_prose(raw, card, "en")
        self.assertIn("warrior", prompt)
        self.assertIn("mage", prompt)
        self.assertIn("energy flows between them", prompt)
        self.assertIn("armor", prompt)
        self.assertIn("robe", prompt)


class ProsePromptAsyncTests(unittest.IsolatedAsyncioTestCase):
    async def test_cloud_provider_selects_prose_template(self):
        card = {"cls": "silent", "concept": "a swift strike", "params": {}}
        raw = {"grammar": "ACTION", "composition": "dynamic angle", "subjects": [
            {"id": "blade", "kind": "weapon", "label": "blade", "count": 1, "appearance": "sharp edge",
             "reference": "", "action": "slashing", "state": "motion blur"}],
               "relations": [], "lighting": "dramatic light", "background": "simple depth", "notes": ""}
        settings = {"llm_provider": "openai", "card_language": "en", "prompt_language": "en",
                    "image_provider": "siliconflow"}  # cloud provider
        with patch.object(promptgen, "_call", AsyncMock(return_value=raw)):
            result = await promptgen.art_prompt(card, settings)
        # Prose template should not use "label:" list format
        self.assertNotIn("blade:", result["prompt"].lower())
        self.assertIn("blade", result["prompt"])

    async def test_local_comfy_selects_structured_template(self):
        card = {"cls": "silent", "concept": "a swift strike", "params": {}}
        raw = {"grammar": "ACTION", "composition": "dynamic angle", "subjects": [
            {"id": "blade", "kind": "weapon", "label": "blade", "count": 1, "appearance": "sharp edge",
             "reference": "", "action": "slashing", "state": "motion blur"}],
               "relations": [], "lighting": "dramatic light", "background": "simple depth", "notes": ""}
        settings = {"llm_provider": "openai", "card_language": "en", "prompt_language": "en",
                    "image_provider": "comfy"}  # local provider
        with patch.object(promptgen, "_call", AsyncMock(return_value=raw)):
            result = await promptgen.art_prompt(card, settings)
        # Structured template uses "label:" format
        self.assertIn("blade:", result["prompt"].lower())


class StructuredPromptAsyncTests_Legacy(unittest.IsolatedAsyncioTestCase):
    async def test_art_prompt_requests_a_plan_then_compiles_it(self):
        card = {"cls": "colorless", "concept": "three cards flying", "params": {}}
        raw = {"grammar": "action", "composition": "three-quarter view", "subjects": [
            {"id": "cards", "kind": "object", "label": "cards", "count": 3, "appearance": "printed faces",
             "reference": "", "action": "fly along one diagonal", "state": "separated"}],
               "relations": ["near-to-far overlap"], "lighting": "directional high-contrast light",
               "background": "simple background depth", "notes": "A ACTION"}
        settings = {"llm_provider": "openai", "card_language": "en", "prompt_language": "en"}
        with patch.object(promptgen, "_call", AsyncMock(return_value=raw)) as call:
            result = await promptgen.art_prompt(card, settings)
        self.assertIs(call.call_args.args[3], promptgen.PLAN_SCHEMA)
        self.assertTrue(result["prompt"].startswith("sts2 illustration, colorless."))
        self.assertNotIn("card art", result["prompt"].lower())
        self.assertEqual(result["plan"]["grammar"], "action")

class PromptGroundingTests(unittest.TestCase):
    def test_selected_library_asset_guidance_is_scoped_to_its_reference(self):
        card = {"cls": "colorless", "concept": "角色举起油灯", "params": {
            "refs": ["lamp.png"], "ref_assets": [{"role": "object", "name": "oil lamp",
            "prompt_hint": "黄铜油灯，保留玻璃罩、提环与灯芯"}]}}
        brief = promptgen._card_brief(card)
        self.assertIn("Library visual guidance for <image1>", brief)
        self.assertIn("黄铜油灯", brief)
        self.assertIn("must not introduce any additional subject", brief)

    def test_fresh_regeneration_does_not_reinject_current_or_rejected_prompt_prose(self):
        card = {"cls": "colorless", "concept": "创建一个自身的复制体", "prompt": "sts2 card art, a flat card panel",
                "params": {}}
        history = [{"prompt": "a printed card frame with a mirror", "feedback": "构图错误"}]

        brief = promptgen._card_brief(card, history)

        self.assertNotIn("flat card panel", brief)
        self.assertNotIn("printed card frame", brief)
        self.assertIn("Rejected attempt recorded", brief)
        self.assertIn("构图错误", brief)


if __name__ == "__main__":
    unittest.main()
