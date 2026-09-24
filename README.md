# STS2 Card Forge

**杀戮尖塔 2 卡图生成器** — a local, out-of-the-box card-art generator for *Slay the Spire 2* mod makers.
Write a card, let an AI turn it into an image prompt, generate art in the game's style on your own GPU,
and export PNGs at the game's native portrait sizes straight into your mod.

Built on Qwen-Image-2.1 (GGUF) + a style LoRA trained on STS2 card art, running in ComfyUI.
WinUI 3 desktop app + a small Python backend. UI in 简体中文 / English.

> **Non-commercial only.** Qwen-Image-2.1 and the style LoRA are under the Qwen RESEARCH LICENSE, which permits
> use for non-commercial purposes (research or evaluation). Read the license and decide for yourself whether your
> mod qualifies before publishing generated art; never use it in anything sold. See [NOTICE](NOTICE).

## Getting started
1. Unzip and double-click **`STS2 Card Forge.bat`**.
2. The first launch opens **Setup**. Click *Install everything missing*: it fetches ComfyUI portable, the GGUF node,
   the model quant that fits your VRAM, the text encoder, the VAE and the style LoRA. Downloads resume and are SHA-256 checked.
   Already have ComfyUI? Use *Use existing ComfyUI…*. Prefer a download manager? *Copy download links*.
3. In **Settings → Prompt AI**, choose a provider, paste an API key and click *Test*.

## Workflow
1. **Characters**: create your mod character first: name, id (the prompt's trigger word, `sts2 card art, <id> card.`), frame
   colour, palette, appearance and a reference image. Every card belongs to a
   mod character; its appearance is written into every card prompt and its reference image is attached to every card
   by default for consistency (a card can use another image or none).
2. **Cards**: type your mod's name as the project, create cards (or **AI ideas** to brainstorm a batch from a theme).
3. Fill in name, export id (e.g. `sleeve_blade`), character, type and card text. *Art concept* takes any language.
   The **theme colour** (default: the character colour) sets the card's light and background; pick one or roll a random one.
4. **Generate prompt with AI** writes the picture content in the style of the best-performing prompts (editable).
   The fixed detail suffix and the default negative prompt are added when generating (both pre-filled, editable).
5. **Generate art**. The Queue page shows progress and a live preview.
6. Click a variant to use it as the card art. Right-click to **Refine**, reuse the seed, or use it as a reference / img2img start.
7. **Export art** into your mod at native sizes: **1000×760** (normal cards) or **606×852** (ancient / full-art), optionally one subfolder per character.

## Low-end machines and overheating
- **Performance profile** (Settings): Auto / Standard / Low / Minimum.
- **Model quant** (Setup): Q4 for ≤6 GB VRAM, Q5 for 8 GB (default), Q6/Q8 for 12 GB+.
- **Draft size**: 768×576, ~1.5x faster. Then right-click → *Refine* to re-render the pick at full size, keeping its composition.
- **Overheat protection**: above 90 °C the queue pauses between images and resumes once the GPU is below 70 °C (thresholds, max wait and an optional fixed gap are in Settings). *Skip cooldown* in the Queue page.
- Measured on an 8 GB laptop GPU: ~6.7 s/step at 1024×768, ~3.5 min per image. The live preview uses `latent2rgb` (free); the `auto` preview overflowed 8 GB VRAM and ran >15x slower.

## Prompting tips
- CFG **3.0** plus a negative prompt matter most (at CFG 1.0 the model mostly ignores the prompt). 25 steps are enough. LoRA strength 0.9.
- Use **concrete** detail words ("chipped metal edges", "rivets", "flying stone chips"); vague ones ("highly detailed", "masterpiece") do nothing.
- The rules the AI follows when writing prompts, and the offline template, are editable in **Settings → Default prompt templates** (with restore-default).
- Objects, weapons, hands, effects and hooded/backlit figures work best. Generate several and pick.
- Name every part of complex objects (a bicycle: two round wheels, handlebars, pedals, chain) or parts go missing.
- Recurring mod character: describe it concretely on the Characters page (hair, outfit, props); turn on its reference image when you need stronger consistency.

---

## For developers

```
app/CardForge/      WinUI 3 app (C#, .NET 10, Windows App SDK, unpackaged + self-contained)
  Services/         API client, backend host (job object), setup/downloader, localization
  Views/            Cards, Characters, Queue, Gallery, Setup, Settings pages
backend/            Python API (aiohttp) on ComfyUI's embedded Python, 127.0.0.1:8190
  server.py         HTTP routes          worker.py   single-GPU queue + overheat protection
  comfy.py          ComfyUI process + websocket client, performance profiles
  graph.py          Qwen-Image-2.1 GGUF workflow   promptgen.py  LLM providers (Claude SDK / OpenAI-compatible / offline)
  store.py          SQLite (cards, characters, images, jobs)   exporter.py   native-size export
  sts2.py           classes, sizes, style rules and caption examples learned while training the LoRA
examples/           sample characters/cards/art seeded into the Examples project on first run (cards.json)
assets/lora/        optional, not in the repo: drop the style LoRA here to bundle it into a release
                    (otherwise Setup downloads it from HuggingFace: Airmongsity/Qwen-Image-2.1-Sts2-Cards-Drawer)
data/               runtime: settings.json, cardforge.db, images/, logs/ (created on first run)
```

- Quick local test (no downloads, reuses this folder's ComfyUI, models and `data\`): double-click `run_dev.bat`.
  A release folder next to or below an existing `ComfyUI_windows_portable` also finds and reuses it.
  The app finds the project root by walking up to `backend/server.py` and starts the backend with ComfyUI's Python.
- Backend alone: `ComfyUI_windows_portable\python_embeded\python.exe backend\server.py`.
- Release: `powershell -ExecutionPolicy Bypass -File build_release.ps1` → `dist\STS2CardForge.zip`.

## License

Code: yours to choose. Model weights and generated images: **Qwen RESEARCH LICENSE, non-commercial**; see [NOTICE](NOTICE).
The LoRA's training images (card art from the game) are not distributed. *Slay the Spire* is a trademark of Mega Crit;
this project is unofficial and not affiliated with Mega Crit.
