import unittest
import base64
import tempfile
from pathlib import Path
from PIL import Image
import io

import cloud_image


class SiliconFlowRequestTests(unittest.TestCase):
    def test_legacy_host_migrates_and_full_endpoint_is_preserved(self):
        self.assertEqual(
            cloud_image._endpoint("https://api.siliconflow.com/v1", "/images/generations"),
            "https://api.siliconflow.cn/v1/images/generations",
        )
        self.assertEqual(
            cloud_image._endpoint("https://api.siliconflow.cn/v1/images/edits", "/images/generations"),
            "https://api.siliconflow.cn/v1/images/generations",
        )

    def test_payload_uses_siliconflow_dimensions_and_reference_fields(self):
        payload = cloud_image._siliconflow_payload(
            {"prompt": "a creature", "negative": "text", "seed": 42, "steps": 25},
            "Kwai-Kolors/Kolors", "1024x1536", {}, [],
        )
        self.assertEqual(payload["image_size"], "1024x1536")
        self.assertNotIn("size", payload)
        self.assertNotIn("n", payload)
        self.assertEqual(payload["negative_prompt"], "text")
        self.assertEqual(payload["seed"], 42)

    def test_reference_file_is_embedded_as_image_data(self):
        source = io.BytesIO()
        Image.new("RGB", (640, 640), (220, 20, 40)).save(source, format="PNG")
        original = source.getvalue()
        with tempfile.TemporaryDirectory() as folder:
            reference = Path(folder) / "oc_cr.png"
            reference.write_bytes(original)
            payload = cloud_image._siliconflow_payload(
                {"prompt": "same character from <image1>", "seed": 1}, "Kwai-Kolors/Kolors",
                "768x1072", {}, [str(reference)],
            )
        self.assertTrue(payload["image"].startswith("data:image/png;base64,"))
        data = payload["image"].split(",", 1)[1]
        with Image.open(io.BytesIO(base64.b64decode(data))) as uploaded:
            self.assertEqual(uploaded.size, (640, 640))
        self.assertIn("attached reference image 1", payload["prompt"])
        self.assertIn("preserve the recognizable appearance", payload["prompt"])

    def test_qwen_edit_reference_is_prepared_at_target_canvas_size(self):
        source = io.BytesIO()
        Image.new("RGB", (640, 640), (220, 20, 40)).save(source, format="PNG")
        with tempfile.TemporaryDirectory() as folder:
            reference = Path(folder) / "oc_cr.png"
            reference.write_bytes(source.getvalue())
            payload = cloud_image._siliconflow_payload(
                {"prompt": "same character", "seed": 1}, "Qwen/Qwen-Image-Edit-2509",
                "768x1072", {}, [str(reference)],
            )
        self.assertNotIn("image_size", payload)
        image_bytes = base64.b64decode(payload["image"].split(",", 1)[1])
        with Image.open(io.BytesIO(image_bytes)) as uploaded:
            self.assertEqual(uploaded.size, (768, 1072))

    def test_qwen_edit_result_is_normalized_to_requested_dimensions(self):
        source = io.BytesIO()
        Image.new("RGB", (1024, 1024), (20, 80, 220)).save(source, format="PNG")
        normalized = cloud_image.normalize_qwen_edit_result(source.getvalue(), 1024, 768)
        with Image.open(io.BytesIO(normalized)) as result:
            self.assertEqual(result.size, (1024, 768))

    def test_qwen_edit_without_reference_fails_before_paid_request(self):
        with self.assertRaisesRegex(ValueError, "Qwen Image Edit"):
            cloud_image._siliconflow_payload(
                {"prompt": "a scene", "seed": 1}, "Qwen/Qwen-Image-Edit-2509",
                "1024x768", {}, [],
            )

    def test_missing_reference_fails_instead_of_silently_becoming_text_to_image(self):
        with self.assertRaisesRegex(ValueError, "missing-oc\\.png"):
            cloud_image._siliconflow_payload(
                {"prompt": "same character", "seed": 1}, "Kwai-Kolors/Kolors",
                "1024x1024", {}, ["missing-oc.png"],
            )


if __name__ == "__main__":
    unittest.main()
