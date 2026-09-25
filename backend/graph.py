"""ComfyUI API graph for Qwen-Image-2.1 (GGUF), mirroring the official templates (same as qig.py)."""
import os

UNET = "qwen_image_2.1_Q5_K_M.gguf"
CLIP = "qwen3vl_8b_w4a8.safetensors"
VAE = "qwen_image_2.1_vae_bf16.safetensors"


def build(p, uploaded_refs=(), uploaded_init=None, unet=UNET, uploaded_resume=None):
    """p: resolved job params (prompt, negative, width, height, steps, cfg, seed, lora, lora_strength, denoise,
    batch, ref_resolution, pick, easycache, mode, stop_at). uploaded_resume: a draft's saved latent to finish."""
    g = {
        "unet": {"class_type": "UnetLoaderGGUF", "inputs": {"unet_name": unet}},
        "clip": {"class_type": "CLIPLoader", "inputs": {"clip_name": CLIP, "type": "qwen_image", "device": "default"}},
        "vae": {"class_type": "VAELoader", "inputs": {"vae_name": VAE}},
        "enc": {"class_type": "TextEncodeQwenImage21", "inputs": {
            "clip": ["clip", 0], "prompt": p["prompt"], "negative_prompt": p["negative"],
            "resolution": int(p.get("ref_resolution") or 1024), "vae": ["vae", 0]}},
        "latent": {"class_type": "EmptyLatentImage", "inputs": {
            "width": p["width"], "height": p["height"], "batch_size": int(p.get("batch") or 1)}},
        "sampler": {"class_type": "KSampler", "inputs": {
            "model": ["unet", 0], "positive": ["enc", 0], "negative": ["enc", 1], "latent_image": ["latent", 0],
            "seed": p["seed"], "steps": p["steps"], "cfg": p["cfg"], "sampler_name": "euler", "scheduler": "simple",
            "denoise": p.get("denoise", 1.0) if uploaded_init else 1.0}},
        "decode": {"class_type": "VAEDecode", "inputs": {"samples": ["sampler", 0], "vae": ["vae", 0]}},
        "save": {"class_type": "SaveImage", "inputs": {"images": ["decode", 0], "filename_prefix": "cardforge/img"}},
    }
    pick, batch = p.get("pick"), int(p.get("batch") or 1)
    if (batch > 1 or pick is not None) and not uploaded_init:
        # Explicit noise indices make ComfyUI draw each item's noise on its own, so item i of a batch can be
        # re-drawn alone later with the same seed (pick = i).
        if pick is not None:
            g["latent"]["inputs"]["batch_size"] = int(pick) + 1
        g["items"] = {"class_type": "LatentFromBatch", "inputs": {
            "samples": ["latent", 0], "batch_index": int(pick or 0), "length": 1 if pick is not None else batch}}
        g["sampler"]["inputs"]["latent_image"] = ["items", 0]
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
    if p.get("easycache"):
        # skips steps whose output barely changes (measured on drafts: 6 of 12 skipped, 2x faster, same composition)
        g["easycache"] = {"class_type": "EasyCache", "inputs": {"model": [model, 0], "reuse_threshold": 0.2,
                                                                "start_percent": 0.15, "end_percent": 0.95, "verbose": False}}
        model = "easycache"
    g["sampler"]["inputs"]["model"] = [model, 0]
    slots = [[f"ref{i}", 0] for i in range(1, len(uploaded_refs) + 1)]
    for i, name in enumerate(uploaded_refs, 1):
        g[f"ref{i}"] = {"class_type": "LoadImage", "inputs": {"image": name}}
    if slots:
        # reference images go through the prefix-cache node from the official edit template
        g["cache"] = {"class_type": "QwenImage21Cache", "inputs": {"model": [model, 0], "device": "auto", "dtype": "default"}}
        g["sampler"]["inputs"]["model"] = ["cache", 0]
        for i, slot in enumerate(slots, 1):
            g["enc"]["inputs"][f"images.image_{i}"] = slot
    if p.get("mode") == "draft" and p.get("stop_at"):
        _split_sampling(g, p, stop_at=int(p["stop_at"]))
    elif uploaded_resume:
        _split_sampling(g, p, resume_from=uploaded_resume)
    return g


def _split_sampling(g, p, stop_at=None, resume_from=None):
    """One run of the full step schedule cut in two. A draft runs steps 0..stop_at: its image is the model's
    prediction of the finished picture at that point (blurry, but the final's composition, colours and light) and
    its half-done latent is saved. The final loads that latent and runs the remaining steps without new noise, so it
    is that very picture finished (measured: same as an uncut run, 0.65/255 mean pixel difference)."""
    model = g["sampler"]["inputs"]["model"]
    latent = g["sampler"]["inputs"]["latent_image"]
    del g["sampler"]
    g["sched"] = {"class_type": "BasicScheduler", "inputs": {"model": model, "scheduler": "simple",
                                                             "steps": int(p["steps"]), "denoise": 1.0}}
    g["split"] = {"class_type": "SplitSigmas", "inputs": {"sigmas": ["sched", 0], "step": int(p["stop_at"])}}
    g["guider"] = {"class_type": "CFGGuider", "inputs": {"model": model, "positive": ["enc", 0], "negative": ["enc", 1],
                                                         "cfg": float(p["cfg"])}}
    g["euler"] = {"class_type": "KSamplerSelect", "inputs": {"sampler_name": "euler"}}
    if resume_from:
        g["resume"] = {"class_type": "LoadLatent", "inputs": {"latent": resume_from}}
        latent = ["resume", 0]
        if p.get("pick") is not None:
            g["resume_item"] = {"class_type": "LatentFromBatch", "inputs": {
                "samples": latent, "batch_index": int(p["pick"]), "length": 1}}
            latent = ["resume_item", 0]
        g["noise"] = {"class_type": "DisableNoise", "inputs": {}}
    else:
        g["noise"] = {"class_type": "RandomNoise", "inputs": {"noise_seed": int(p["seed"])}}
    g["sampler"] = {"class_type": "SamplerCustomAdvanced", "inputs": {
        "noise": ["noise", 0], "guider": ["guider", 0], "sampler": ["euler", 0],
        "sigmas": ["split", 1 if resume_from else 0], "latent_image": latent}}
    if resume_from:
        g["decode"]["inputs"]["samples"] = ["sampler", 0]
    else:
        g["keep"] = {"class_type": "SaveLatent", "inputs": {"samples": ["sampler", 0], "filename_prefix": "cardforge/draft"}}
        g["decode"]["inputs"]["samples"] = ["sampler", 1]      # the denoised prediction
