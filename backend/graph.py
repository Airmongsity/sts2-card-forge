"""ComfyUI API graph for Qwen-Image-2.1 (GGUF), mirroring the official templates (same as qig.py)."""
import os

UNET = "qwen_image_2.1_Q5_K_M.gguf"
CLIP = "qwen3vl_8b_w4a8.safetensors"
VAE = "qwen_image_2.1_vae_bf16.safetensors"


def build(p, uploaded_refs=(), uploaded_init=None, unet=UNET):
    """p: resolved job params (prompt, negative, width, height, steps, cfg, seed, lora, lora_strength, denoise)."""
    g = {
        "unet": {"class_type": "UnetLoaderGGUF", "inputs": {"unet_name": unet}},
        "clip": {"class_type": "CLIPLoader", "inputs": {"clip_name": CLIP, "type": "qwen_image", "device": "default"}},
        "vae": {"class_type": "VAELoader", "inputs": {"vae_name": VAE}},
        "enc": {"class_type": "TextEncodeQwenImage21", "inputs": {
            "clip": ["clip", 0], "prompt": p["prompt"], "negative_prompt": p["negative"],
            "resolution": 1024, "vae": ["vae", 0]}},
        "latent": {"class_type": "EmptyLatentImage", "inputs": {"width": p["width"], "height": p["height"], "batch_size": 1}},
        "sampler": {"class_type": "KSampler", "inputs": {
            "model": ["unet", 0], "positive": ["enc", 0], "negative": ["enc", 1], "latent_image": ["latent", 0],
            "seed": p["seed"], "steps": p["steps"], "cfg": p["cfg"], "sampler_name": "euler", "scheduler": "simple",
            "denoise": p.get("denoise", 1.0) if uploaded_init else 1.0}},
        "decode": {"class_type": "VAEDecode", "inputs": {"samples": ["sampler", 0], "vae": ["vae", 0]}},
        "save": {"class_type": "SaveImage", "inputs": {"images": ["decode", 0], "filename_prefix": "cardforge/img"}},
    }
    if uploaded_init:
        # img2img: start from the encoded init image (cropped to width x height) instead of an empty latent
        g["init"] = {"class_type": "LoadImage", "inputs": {"image": uploaded_init}}
        g["init_scaled"] = {"class_type": "ImageScale", "inputs": {
            "image": ["init", 0], "upscale_method": "lanczos", "width": p["width"], "height": p["height"], "crop": "center"}}
        g["latent"] = {"class_type": "VAEEncode", "inputs": {"pixels": ["init_scaled", 0], "vae": ["vae", 0]}}
    model = "unet"
    if p.get("lora") and float(p.get("lora_strength") or 0) > 0:
        # ComfyUI lists LoRA subfolders with the OS separator
        g["lora"] = {"class_type": "LoraLoaderModelOnly", "inputs": {
            "model": ["unet", 0], "lora_name": p["lora"].replace("/", os.sep), "strength_model": float(p["lora_strength"])}}
        model = "lora"
    g["sampler"]["inputs"]["model"] = [model, 0]
    if uploaded_refs:
        # reference images go through the prefix-cache node from the official edit template
        g["cache"] = {"class_type": "QwenImage21Cache", "inputs": {"model": [model, 0], "device": "auto", "dtype": "default"}}
        g["sampler"]["inputs"]["model"] = ["cache", 0]
        for i, name in enumerate(uploaded_refs, 1):
            g[f"ref{i}"] = {"class_type": "LoadImage", "inputs": {"image": name}}
            g["enc"]["inputs"][f"images.image_{i}"] = [f"ref{i}", 0]
    return g
