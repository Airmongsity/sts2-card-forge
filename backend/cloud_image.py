"""Cloud image generation clients used by the normal Card Forge job queue.

Supports OpenAI Images-compatible endpoints and DashScope's synchronous Qwen Image endpoint.
Provider URLs expire, so results are always downloaded before a job is completed.
"""
import base64
import io
import json
import mimetypes
from pathlib import Path
from urllib.parse import urlparse, urlunparse

import aiohttp
from PIL import Image, ImageOps

import config

MAX_IMAGE_BYTES = 80 * 1024 * 1024


def _extra(value):
    if not value:
        return {}
    if isinstance(value, dict):
        return dict(value)
    try:
        obj = json.loads(value)
    except (TypeError, ValueError) as e:
        raise ValueError(config.tr(f"图像 API 的额外参数不是有效 JSON：{e}",
                                   f"Image API extra parameters are not valid JSON: {e}"))
    if not isinstance(obj, dict):
        raise ValueError(config.tr("图像 API 的额外参数必须是 JSON 对象", "Image API extra parameters must be a JSON object"))
    return obj


def _endpoint(base, tail):
    base = (base or "").strip().rstrip("/")
    if not base:
        raise ValueError(config.tr("请在设置中填写图像 API 端点", "Enter an image API endpoint in Settings"))
    parsed = urlparse(base)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise ValueError(config.tr("图像 API 端点必须是完整的 HTTP(S) 地址", "Image API endpoint must be a complete HTTP(S) URL"))
    if parsed.hostname == "api.siliconflow.com":
        base = urlunparse(parsed._replace(netloc=parsed.netloc.replace("api.siliconflow.com", "api.siliconflow.cn", 1))).rstrip("/")
    if not tail:
        return base
    for existing in ("/images/generations", "/images/edits"):
        if base.endswith(existing):
            return base[:-len(existing)] + tail
    return base if base.endswith(tail) else base + tail


def _error_text(status, text):
    try:
        data = json.loads(text)
        err = data.get("error") or data
        if isinstance(err, dict):
            return str(err.get("message") or err.get("code") or text)
        return str(err)
    except (TypeError, ValueError):
        return text.strip() or f"HTTP {status}"


async def _request_json(session, url, *, headers=None, json_body=None, data=None):
    async with session.post(url, headers=headers, json=json_body, data=data) as response:
        raw = await response.read()
        text = raw.decode("utf-8", "replace")
        if response.status >= 400:
            detail = _error_text(response.status, text)
            raise RuntimeError(config.tr(f"图像 API 返回 HTTP {response.status}：{detail}",
                                         f"Image API returned HTTP {response.status} from {url.split('?')[0].split('#')[0]}: {detail}"))
        try:
            return json.loads(text)
        except ValueError:
            raise RuntimeError(config.tr("图像 API 没有返回有效 JSON", "Image API did not return valid JSON"))


async def _download(session, url):
    parsed = urlparse(str(url))
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise RuntimeError(config.tr("图像 API 返回了无效的图片地址", "Image API returned an invalid image URL"))
    async with session.get(url) as response:
        if response.status >= 400:
            text = (await response.read())[:2000].decode("utf-8", "replace")
            raise RuntimeError(config.tr(f"下载生成图片失败（HTTP {response.status}）：{text}",
                                         f"Generated image download failed (HTTP {response.status}): {text}"))
        data = await response.read()
        if len(data) > MAX_IMAGE_BYTES:
            raise RuntimeError(config.tr("图像 API 返回的文件过大", "Image API returned an oversized file"))
        return data


async def _images_from_openai_response(session, response):
    out = []
    for item in response.get("data") or []:
        if item.get("b64_json"):
            try:
                data = base64.b64decode(item["b64_json"], validate=True)
            except (ValueError, TypeError):
                raise RuntimeError(config.tr("图像 API 返回了无效的 Base64 图片", "Image API returned invalid base64 image data"))
            if len(data) > MAX_IMAGE_BYTES:
                raise RuntimeError(config.tr("图像 API 返回的文件过大", "Image API returned an oversized file"))
            out.append(data)
        elif item.get("url"):
            out.append(await _download(session, item["url"]))
    if not out:
        raise RuntimeError(config.tr("图像 API 完成了请求，但没有返回图片", "Image API completed without returning an image"))
    return out


async def _images_from_siliconflow_response(session, response):
    """SiliconFlow returns images[].url (rather than OpenAI data[].)."""
    out = []
    for item in response.get("images") or []:
        if item.get("url"):
            out.append(await _download(session, item["url"]))
        elif item.get("b64_json"):
            try:
                out.append(base64.b64decode(item["b64_json"], validate=True))
            except (ValueError, TypeError):
                raise RuntimeError(config.tr("鍥惧儚 API 杩斿洖浜嗘棤鏁堢殑 Base64 鍥剧墖", "Image API returned invalid base64 image data"))
    if not out:
        return await _images_from_openai_response(session, response)
    return out


def _siliconflow_payload(p, model, size, extra, sources):
    """Build SiliconFlow's native JSON request; references stay on generations."""
    payload = dict(extra)
    prompt = p["prompt"]
    if sources:
        import re
        prompt = re.sub(r"<image(\d+)>", r"attached reference image \1", prompt, flags=re.IGNORECASE)
        prompt += (" Use the attached reference image(s) to preserve the recognizable appearance of the depicted "
                   "subject(s), while following the requested action and composition.")
    is_qwen_edit = "qwen/qwen-image-edit" in model.casefold()
    if is_qwen_edit and not sources:
        raise ValueError(config.tr("Qwen Image Edit 需要参考图；纯文生图请改用支持文生图的模型。",
                                   "Qwen Image Edit requires a reference image; use a text-to-image model for text-only generation."))
    payload.update(model=model, prompt=prompt)
    if not is_qwen_edit:
        payload["image_size"] = size
    payload.setdefault("negative_prompt", p.get("negative") or "")
    payload.setdefault("seed", int(p["seed"]) % 2147483648)
    payload.setdefault("num_inference_steps", int(p.get("steps") or 25))
    target_width, target_height = (int(part) for part in size.split("x", 1))
    for index, path in enumerate(sources):
        file = Path(path)
        if not file.is_file():
            raise ValueError(config.tr(f"SiliconFlow 参考图文件不存在，未能上传：{file.name}",
                                       f"SiliconFlow reference image was not uploaded because the file is missing: {file.name}"))
        mime = mimetypes.guess_type(file.name)[0] or "application/octet-stream"
        image_bytes = file.read_bytes()
        if is_qwen_edit:
            # SiliconFlow explicitly disallows image_size for Qwen Image Edit;
            # that model derives its output canvas from its image input. Fit
            # the reference to the requested canvas without distorting it.
            with Image.open(io.BytesIO(image_bytes)) as source:
                source = ImageOps.exif_transpose(source).convert("RGB")
                fitted = ImageOps.contain(source, (target_width, target_height), method=Image.Resampling.LANCZOS)
                canvas = Image.new("RGB", (target_width, target_height), (96, 96, 96))
                canvas.paste(fitted, ((target_width - fitted.width) // 2, (target_height - fitted.height) // 2))
                encoded_image = io.BytesIO()
                canvas.save(encoded_image, format="PNG")
                image_bytes = encoded_image.getvalue()
            mime = "image/png"
        encoded = base64.b64encode(image_bytes).decode("ascii")
        payload["image" if index == 0 else f"image{index + 1}"] = f"data:{mime};base64,{encoded}"
    return payload


def normalize_qwen_edit_result(data, width, height):
    """Persist Qwen Edit output at the exact configured canvas dimensions."""
    with Image.open(io.BytesIO(data)) as image:
        image = ImageOps.exif_transpose(image).convert("RGB")
        if image.size != (int(width), int(height)):
            image = ImageOps.fit(image, (int(width), int(height)), method=Image.Resampling.LANCZOS)
        result = io.BytesIO()
        image.save(result, format="PNG")
        return result.getvalue()


def _openai_form(p, model, size, extra):
    form = aiohttp.FormData()
    fields = dict(extra)
    fields.update(model=model, prompt=p["prompt"], n="1", size=size)
    quality = (p.get("image_api_quality") or "").strip()
    if quality and quality != "auto":
        fields["quality"] = quality
    for key, value in fields.items():
        encoded = json.dumps(value) if isinstance(value, (dict, list, bool)) else str(value)
        form.add_field(str(key), encoded)
    paths = ([p["init"]] if p.get("init") else []) + list(p.get("refs") or [])
    field = "image[]" if len(paths) > 1 else "image"
    for path in paths:
        file = Path(path)
        if file.exists():
            mime = mimetypes.guess_type(file.name)[0] or "application/octet-stream"
            form.add_field(field, file.read_bytes(), filename=file.name, content_type=mime)
    return form


async def _openai(session, p, key):
    base, model = p.get("image_api_base_url"), (p.get("image_api_model") or "").strip()
    if not model:
        raise ValueError(config.tr("请在设置中填写图像模型名", "Enter an image model in Settings"))
    headers = {"Authorization": f"Bearer {key}"} if key else {}
    size = f"{p['width']}x{p['height']}"
    extra = _extra(p.get("image_api_extra"))
    sources = ([p["init"]] if p.get("init") else []) + list(p.get("refs") or [])
    if p.get("image_preset") == "siliconflow":
        if len(sources) > 3:
            raise ValueError(config.tr("SiliconFlow 的图像编辑接口最多接受 3 张参考图。",
                                       "SiliconFlow image editing accepts at most 3 reference images."))
        if len(sources) > 1 and "Qwen-Image-Edit-2509" not in model:
            raise ValueError(config.tr("SiliconFlow 当前模型不支持多张参考图；请改用 Qwen/Qwen-Image-Edit-2509 或减少参考图。",
                                       "This SiliconFlow model does not support multiple reference images; use Qwen/Qwen-Image-Edit-2509 or reduce the references."))
        response = await _request_json(session, _endpoint(base, "/images/generations"),
                                       headers=headers,
                                       json_body=_siliconflow_payload(p, model, size, extra, sources))
        return await _images_from_siliconflow_response(session, response)
    if sources:
        response = await _request_json(session, _endpoint(base, "/images/edits"), headers=headers,
                                       data=_openai_form(p, model, size, extra))
    else:
        payload = dict(extra)
        payload.update(model=model, prompt=p["prompt"], n=1, size=size)
        quality = (p.get("image_api_quality") or "").strip()
        if quality and quality != "auto":
            payload["quality"] = quality
        response = await _request_json(session, _endpoint(base, "/images/generations"),
                                       headers=headers, json_body=payload)
    return await _images_from_openai_response(session, response)


async def _dashscope(session, p, key):
    if p.get("init") or p.get("refs"):
        raise ValueError(config.tr("当前百炼预设只支持文生图；请清除参考图和起始图，或改用支持 /images/edits 的端点",
                                   "The current DashScope preset is text-to-image only; clear references/start image or use an endpoint that supports /images/edits"))
    model = (p.get("image_api_model") or "").strip()
    if not model:
        raise ValueError(config.tr("请在设置中填写图像模型名", "Enter an image model in Settings"))
    params = _extra(p.get("image_api_extra"))
    params.update(size=f"{p['width']}*{p['height']}", n=1,
                  negative_prompt=(p.get("negative") or "")[:500], seed=int(p["seed"]) % 2147483648)
    payload = {"model": model,
               "input": {"messages": [{"role": "user", "content": [{"text": p["prompt"]}]}]},
               "parameters": params}
    headers = {"Authorization": f"Bearer {key}"} if key else {}
    response = await _request_json(session, _endpoint(p.get("image_api_base_url"), ""),
                                   headers=headers, json_body=payload)
    if response.get("code"):
        raise RuntimeError(str(response.get("message") or response["code"]))
    choices = (response.get("output") or {}).get("choices") or []
    items = ((choices[0].get("message") or {}).get("content") or []) if choices else []
    urls = [item.get("image") for item in items if item.get("image")]
    if not urls:
        raise RuntimeError(config.tr("百炼请求完成，但没有返回图片", "DashScope completed without returning an image"))
    return [await _download(session, url) for url in urls]


async def generate(p, settings):
    """Return one or more encoded image byte strings for a cloud-backed job."""
    provider = p.get("image_provider") or "openai"
    key = settings.get("image_api_key") or ""
    if not key and p.get("image_preset") != "custom":
        raise ValueError(config.tr("请先在设置中填写图像 API key", "Enter the image API key in Settings first"))
    timeout = aiohttp.ClientTimeout(total=900, connect=30, sock_read=900)
    async with aiohttp.ClientSession(timeout=timeout, trust_env=True) as session:
        if provider == "openai":
            return await _openai(session, p, key)
        if provider == "dashscope":
            return await _dashscope(session, p, key)
        raise ValueError(config.tr(f"未知图像提供商：{provider}", f"Unknown image provider: {provider}"))
