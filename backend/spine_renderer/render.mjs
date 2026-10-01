import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import CanvasKitInit from "canvaskit-wasm";
import { loadTextureAtlas, SkeletonDrawable, SkeletonRenderer, Skin, loadSkeletonData } from "@esotericsoftware/spine-canvaskit";

function args(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i += 2) out[argv[i].replace(/^--/, "")] = argv[i + 1];
  return out;
}

function required(value, name) {
  if (!value) throw new Error(`Missing --${name}`);
  return path.resolve(value);
}

async function main() {
  const a = args(process.argv);
  const skeletonPath = required(a.skeleton, "skeleton");
  const atlasPath = required(a.atlas, "atlas");
  const outputPath = required(a.output, "output");
  const size = Math.max(256, Math.min(2048, Number.parseInt(a.size ?? "1024", 10)));
  // Leave room for action poses that extend well beyond the setup-pose dimensions.
  const padding = Math.round(size * 0.22);
  const ck = await CanvasKitInit();
  const read = async (file) => fs.readFileSync(path.isAbsolute(file) ? file : path.resolve(path.dirname(atlasPath), file));
  const atlas = await loadTextureAtlas(ck, atlasPath, read);
  const skeletonData = await loadSkeletonData(skeletonPath, atlas, read);
  const drawable = new SkeletonDrawable(skeletonData);

  const names = skeletonData.animations.map((animation) => animation.name);
  const preferred = a.animation && names.includes(a.animation) ? a.animation
    : names.find((name) => /^(idle|idle[_ -]?loop|rest|stand|loop)$/i.test(name))
      ?? names.find((name) => /(idle|rest|stand)/i.test(name)) ?? names[0];
  const selectedSkinNames = a.skin
    ? a.skin.split(",").map((name) => name.trim()).filter(Boolean)
    : [];
  if (!selectedSkinNames.length) {
    const normal = skeletonData.skins.find((skin) => /^normal$/i.test(skin.name));
    const skin1 = skeletonData.skins.find((skin) => /^skin1$/i.test(skin.name));
    const egg1 = skeletonData.skins.find((skin) => /^egg1$/i.test(skin.name));
    if (normal) selectedSkinNames.push(normal.name);
    else if (skin1) selectedSkinNames.push(skin1.name);
    else if (egg1) selectedSkinNames.push(egg1.name);
    else {
      // Some exports split independent facial/body variants into separate numbered skin families.
      for (const family of ["eye", "pattern"]) {
        const firstVariant = skeletonData.skins.find((skin) =>
          new RegExp(`^${family}[_ -]?1$`, "i").test(skin.name));
        if (firstVariant) selectedSkinNames.push(firstVariant.name);
      }
      const variants = skeletonData.skins.filter((skin) => !/^default$/i.test(skin.name));
      if (!selectedSkinNames.length && variants.length === 1) selectedSkinNames.push(variants[0].name);
    }
  }
  const compositeSkin = new Skin("cardforge-preview");
  if (skeletonData.defaultSkin) compositeSkin.addSkin(skeletonData.defaultSkin);
  for (const name of selectedSkinNames) {
    const skin = skeletonData.findSkin(name);
    if (!skin) throw new Error(`Skin not found: ${name}`);
    if (skin !== skeletonData.defaultSkin) compositeSkin.addSkin(skin);
  }
  if (selectedSkinNames.length || skeletonData.defaultSkin) {
    drawable.skeleton.setSkin(compositeSkin);
    drawable.skeleton.setSlotsToSetupPose();
  }
  const selectedNames = a.all === "1" ? names : [preferred].filter(Boolean);
  const reports = [];
  for (let index = 0; index < selectedNames.length; index++) {
  const selected = selectedNames[index];
  const currentDrawable = index === 0 ? drawable : new SkeletonDrawable(skeletonData);
  if (selectedSkinNames.length || skeletonData.defaultSkin) {
    const compositeSkin = new Skin("cardforge-preview");
    if (skeletonData.defaultSkin) compositeSkin.addSkin(skeletonData.defaultSkin);
    for (const name of selectedSkinNames) {
      const skin = skeletonData.findSkin(name);
      if (skin && skin !== skeletonData.defaultSkin) compositeSkin.addSkin(skin);
    }
    currentDrawable.skeleton.setSkin(compositeSkin);
    currentDrawable.skeleton.setSlotsToSetupPose();
  }
  if (selected) currentDrawable.animationState.setAnimation(0, selected, true);
  const selectedAnimation = skeletonData.animations.find((animation) => animation.name === selected);
  // The start of the idle loop is a stable representative pose. Later frames can blink or hide
  // essential attachments, which makes a single thumbnail look as if parts were lost.
  const requestedTime = a.time === undefined ? 0 : Number(a.time);
  const animationTime = Number.isFinite(requestedTime) && selectedAnimation
    ? Math.max(0, Math.min(selectedAnimation.duration, requestedTime))
    : 0;

  const width = Math.max(1, skeletonData.width);
  const height = Math.max(1, skeletonData.height);
  const scale = Math.min((size - padding * 2) / width, (size - padding * 2) / height);
  currentDrawable.skeleton.scaleX = currentDrawable.skeleton.scaleY = scale;
  currentDrawable.skeleton.x = (size - width * scale) / 2 - skeletonData.x * scale;
  currentDrawable.skeleton.y = size - (size - height * scale) / 2 + skeletonData.y * scale;

  currentDrawable.update(animationTime);

  const surface = ck.MakeSurface(size, size);
  if (!surface) throw new Error("CanvasKit could not create an offscreen surface");
  const canvas = surface.getCanvas();
  canvas.clear(ck.TRANSPARENT);
  new SkeletonRenderer(ck).render(canvas, currentDrawable);
  surface.flush();
  const snapshot = surface.makeImageSnapshot();
  const encoded = snapshot.encodeToBytes();
  if (!encoded?.length) throw new Error("CanvasKit returned an empty PNG");
  const framePath = a.all === "1" ? path.join(outputPath, `${String(index).padStart(4, "0")}.png`) : outputPath;
  fs.mkdirSync(path.dirname(framePath), { recursive: true });
  fs.writeFileSync(framePath, Buffer.from(encoded));
  snapshot.delete();
  surface.delete();
  reports.push({ output: framePath, animation: selected ?? "", animationTime,
    skin: selectedSkinNames.join(","), size,
    sourceWidth: width, sourceHeight: height });
  }
  console.log(JSON.stringify(a.all === "1" ? reports : reports[0]));
}

main().catch((error) => {
  console.error(error?.stack ?? String(error));
  process.exitCode = 1;
});
