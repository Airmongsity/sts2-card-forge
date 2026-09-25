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
2. The first launch opens **Setup**. Click *Install everything missing*: it fetches the ComfyUI portable package for your GPU,
   the GGUF node, the model quant that fits your VRAM, the text encoder, the VAE and the style LoRA, then runs a GPU self-test.
   Downloads resume, are SHA-256 checked and switch to the next source when one fails or stalls.
   Already have ComfyUI? Use *Use existing ComfyUI…*. Prefer a download manager? *Copy download links*.
3. In **Settings → Prompt AI**, choose a provider, paste an API key and click *Test*.

The download is small (the app only). Before fetching anything big, Setup checks the GPU, driver, RAM and free disk
space and warns first if this PC is unlikely to generate images, so nobody downloads ~14 GB for nothing.

**Updates**: on startup the app checks GitHub releases for a newer version (turn it off in **Settings → Updates**).
*Update now* downloads the release, replaces only the program files (cards, images, settings and models stay) and restarts.

## Supported GPUs
Setup detects the card and picks the matching ComfyUI package (override it under *Package*):

| GPU | Package | Notes |
|---|---|---|
| NVIDIA RTX 20 / GTX 16 series and newer | `nvidia` (CUDA 13) | driver 580 or newer |
| NVIDIA GTX 900 / 10 series | `nvidia_cu126` (CUDA 12.6) | works, slower (no bf16) |
| AMD RX 6000 / 7000 / 9000, Ryzen AI (RDNA 2+) | `amd` (ROCm) | Windows 11 + current Adrenalin driver; RX 5000 and older are not supported |
| Intel Arc | `intel` (XPU) | current Arc driver; UHD / Iris integrated graphics are too weak |
| anything else | CPU | works, but takes hours per image |

The GPU self-test runs a small PyTorch computation on the card and explains failures (driver too old, card too old for
the package, wrong vendor). Overheat protection reads the temperature through `nvidia-smi`, so it is only active on NVIDIA.

## Download sources
*Download source* in Setup: **Auto** (by system region), **Global** or **Mainland China**. Every file has several sources
and falls back automatically, in the chosen order:
- Models: HuggingFace ⇄ ModelScope (identical files; the order follows the region) → hf-mirror.com. hf-mirror.com mirrors only metadata: large files still come from
  HuggingFace's CDN, so on its own it does not help when HuggingFace is unreachable.
- Python packages: PyPI ⇄ Tsinghua / Aliyun mirrors.
- ComfyUI and the GGUF node come from GitHub; set *GitHub proxy* (a download-proxy prefix) if GitHub is blocked, or
  download the `.7z` yourself (*Copy download links*), extract it and use *Use existing ComfyUI…*.

*Network proxy* in Setup: **Auto** uses the Windows system proxy, or finds a proxy app's local port when "system
proxy" is off (Clash / mihomo 7890, Clash Verge 7897, v2rayN 10809, …; a port only counts if it really proxies). Downloads,
pip and the AI prompt writer go through it; local addresses stay direct. **Off** or a manual address are also possible.

## Workflow
1. **Characters**: create your mod character first: name, id (the prompt's trigger word, `sts2 card art, <id> card.`), frame
   colour, palette, appearance and a reference image. Every card belongs to a
   mod character; its appearance is written into every card prompt and its reference image is attached to every card
   by default for consistency (a card can use another image or none).
2. **Cards**: type your mod's name as the project, create cards (or **AI ideas** to brainstorm a batch from a theme).
3. Fill in name, export id (e.g. `sleeve_blade`), character, type and card text. *Art concept* takes any language.
   The **theme colour** (default: the character colour) sets the card's light and background; pick one or roll a random one.
4. **Generate prompt with AI** writes the picture content in the style of the best-performing prompts (editable), in
   the UI language by default (the image model reads Chinese as well as English; Settings → prompt language).
   The fixed detail suffix and the default negative prompt are added when generating (both pre-filled, editable).
5. **Draw drafts**: several quick drafts at once. Each draft is the first 8 of its final image's 25 steps: blurry, but
   already the final composition, colours and light. Right-click the one you like → **Paint final** continues it from
   there to the finished image (no redraw). *Fast drafts* (EasyCache) makes drafts about 2x faster.
   **Generate art** draws full images directly. The Queue page shows progress and a live preview.
6. Click a variant to use it as the card art. Right-click to reuse the seed (it re-draws exactly that image), refine with
   img2img, or use it as a reference / img2img start.
7. **Export art** into your mod at native sizes: **1000×760** (normal cards) or **606×852** (ancient / full-art), optionally one subfolder per character.

## Low-end machines and overheating
- **Performance profile** (Settings): Auto / Standard / Low / Minimum.
- **Model quant** (Setup): Q4 for ≤6 GB VRAM, Q5 for 8 GB (default), Q6/Q8 for 12 GB+.
- **Drafts** instead of full images while exploring: ~40 s per draft, ~2 min to finish the chosen one (8 GB laptop GPU).
- **Overheat protection**: above 90 °C the queue pauses between images and resumes once the GPU is below 70 °C (thresholds, max wait and an optional fixed gap are in Settings). *Skip cooldown* in the Queue page.
- Measured on an 8 GB laptop GPU: ~3 min per full image with a reference image; two images per run are ~19% faster each (on by default for the Standard profile). The live preview uses `latent2rgb` (free); the `auto` preview overflowed 8 GB VRAM and ran >15x slower.

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
VERSION             the one version number (app, backend, release tag v<VERSION>)
data/               runtime: settings.json, cardforge.db, images/, logs/ (created on first run)
```

- Quick local test (no downloads, reuses this folder's ComfyUI, models and `data\`): double-click `run_dev.bat`.
  A release folder next to or below an existing `ComfyUI_windows_portable` also finds and reuses it.
  The app finds the project root by walking up to `backend/server.py` and starts the backend with ComfyUI's Python.
- Backend alone: `ComfyUI_windows_portable\python_embeded\python.exe backend\server.py`.
- Release: bump `VERSION`, run `powershell -ExecutionPolicy Bypass -File build_release.ps1` → `dist\STS2CardForge.zip`,
  then publish a GitHub release tagged `v<VERSION>` with that zip attached. The zip holds only the app, backend and
  examples (no models, no LoRA): Setup checks the machine first and downloads the rest only when it makes sense.
  The style LoRA comes from HuggingFace `Airmongsity/Qwen-Image-2.1-Sts2-Cards-Drawer` (Setup also tries ModelScope under
  the same repo name, so mirroring it there adds a China source).

## License

Code: yours to choose. Model weights and generated images: **Qwen RESEARCH LICENSE, non-commercial**; see [NOTICE](NOTICE).
The LoRA's training images (card art from the game) are not distributed. *Slay the Spire* is a trademark of Mega Crit;
this project is unofficial and not affiliated with Mega Crit.
