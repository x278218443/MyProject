"""Export local PSD layers, text and layout for the standalone Local PSD UI tool."""
import argparse
import json
import math
import sys
from pathlib import Path


def check_dependencies():
    import psd_tools
    import aggdraw
    import skimage
    if psd_tools.__version__ != "1.17.1":
        raise RuntimeError('Please install psd-tools[composite]==1.17.1')


def vertical_text_gradient(effect, warnings=None, layer_name=""):
    """Keep gradient RGB/alpha separate from the text's base color for TMP."""
    from psd_tools.terminology import Key, Enum, Type
    desc, gradient = effect.descriptor, effect.gradient
    angle = effect.angle % 360
    if effect.type != Enum.Linear or min(abs(angle - 90), abs(angle - 270)) > 0.01:
        raise ValueError("only vertical linear gradients are supported")
    if effect.blend_mode != Enum.Normal:
        raise ValueError("gradient requires Normal blending")
    opacity = float(desc.get(Key.Opacity, 100)) / 100
    if not 0 <= opacity <= 1:
        raise ValueError("gradient opacity is outside 0-100%")
    offset = effect.offset or {}
    if (not effect.aligned or abs(float(desc.get(Key.Scale, 100)) - 100) > 0.01
            or any(abs(float(offset.get(key, 0))) > 0.01 for key in (Key.Horizontal, Key.Vertical))):
        raise ValueError("gradient requires layer alignment, 100% scale and zero offset")
    if gradient is None or getattr(gradient.get(Type.GradientForm), "enum", None) != Enum.CustomStops:
        raise ValueError("noise gradients are not supported")
    stops = sorted(gradient.get(Key.Colors, []), key=lambda item: float(item.get(Key.Location, 0)))
    if len(stops) != 2:
        raise ValueError("gradient requires exactly two color stops")
    positions = [float(stop.get(Key.Location, -1)) for stop in stops]
    if not 0 <= positions[0] < positions[1] <= 4096:
        raise ValueError("gradient color stop positions must be distinct and within 0-100%")
    midpoints = [float(s.get(Key.Midpoint, 50)) for s in stops]
    if any(not 0 <= midpoint <= 100 for midpoint in midpoints):
        raise ValueError("gradient midpoint is outside 0-100%")
    alphas = [float(stop.get(Key.Opacity, 100)) / 100 for stop in gradient.get(Key.Transparency, [])]
    if alphas and (any(not 0 <= a <= 1 for a in alphas) or max(alphas) - min(alphas) > 0.0001):
        # This importer currently supports a uniform gradient opacity.
        raise ValueError("varying gradient transparency requires raster text")
    opacity *= alphas[0] if alphas else 1
    colors = []
    for stop in stops:
        color = stop.get(Key.Color, {})
        if not all(key in color for key in (Key.Red, Key.Green, Key.Blue)):
            raise ValueError("only RGB gradient colors are supported")
        colors.append(dict(r=float(color[Key.Red]) / 255, g=float(color[Key.Green]) / 255,
                           b=float(color[Key.Blue]) / 255, a=opacity))
    top, bottom = (colors[1], colors[0]) if angle < 180 else (colors[0], colors[1])
    if effect.reversed:
        top, bottom = bottom, top
    if warnings is not None and positions != [0, 4096]:
        values = "/".join(f"{position / 4096 * 100:.3f}%" for position in positions)
        warnings.append(f"{layer_name}: 渐变色标位置为 {values}，已保留两端颜色及透明度，"
                        "按 TMP 全高度线性渐变近似；色标外的纯色区域可能与 PSD 不同。")
    if warnings is not None and any(abs(midpoint - 50) > 0.01 for midpoint in midpoints):
        values = "/".join(f"{midpoint:g}%" for midpoint in midpoints)
        warnings.append(f"{layer_name}: 渐变色标中点为 {values}，已保留两端颜色及透明度，"
                        "使用 TMP 线性顶点渐变近似；过渡曲线与 PSD 可能不同。")
    return dict(top=top, bottom=bottom)


def text_info(layer, warnings):
    engine = layer.engine_dict
    runs = engine["StyleRun"]["RunArray"]
    style = runs[0]["StyleSheet"]["StyleSheetData"]
    if len(runs) > 1:
        warnings.append(f"{layer.name}: mixed text styles use the first style")
    fonts = layer.resource_dict["FontSet"]
    font = str(fonts[int(style.get("Font", 0))]["Name"])
    values = style.get("FillColor", {}).get("Values", [1, 1, 1, 1])
    a, r, g, b = (float(v) for v in values)
    transform = layer.transform
    scale = math.hypot(float(transform[2]), float(transform[3]))
    paragraphs = engine["ParagraphRun"]["RunArray"]
    justification = int(paragraphs[0]["ParagraphSheet"]["Properties"].get("Justification", 0))
    return dict(text=layer.text, size=float(style.get("FontSize", 14)) * scale,
                fontPostScriptName=font, fontName=font,
                color=dict(r=r, g=g, b=b, a=a * layer.opacity / 255.0),
                justification={0: "left", 1: "right", 2: "center"}.get(justification, "left"),
                leading=0 if style.get("AutoLeading", True) else float(style.get("Leading", 0)) * scale,
                tracking=float(style.get("Tracking", 0)))


def export(source, output, raster_text=False):
    from psd_tools import PSDImage
    from psd_tools.constants import BlendMode
    psd = PSDImage.open(source)
    output.mkdir(parents=True, exist_ok=True)
    textures = output / "layers"
    textures.mkdir(exist_ok=True)
    warnings = []
    source_indices = {id(layer): index for index, layer in enumerate(psd.descendants(), 1)}
    sidecar_path = source.with_suffix(".roundtrip.json")
    roundtrip = {}
    if sidecar_path.is_file():
        sidecar = json.loads(sidecar_path.read_text(encoding="utf-8-sig"))
        if sidecar.get("version") != 1:
            raise ValueError("Unsupported round-trip metadata version")
        ids = [layer.layer_id for layer in psd.descendants()]
        for item in sidecar.get("nodes", []):
            identity = item.get("psdLayerId")
            if ids.count(identity) == 1:
                roundtrip[identity] = item
            else:
                warnings.append(f"PSD layer ID {identity}: missing or duplicated; metadata not applied")
    flat = []
    counter = 0

    def convert(parent, depth=0):
        nonlocal counter
        # psd-tools iterates bottom-to-top, matching Unity sibling drawing order.
        result = []
        for layer in parent:
            metadata = roundtrip.get(layer.layer_id)
            if not layer.is_visible() and metadata is None:
                continue
            if depth >= 10:
                raise ValueError(f"{layer.name}: exceeds the supported 10-level hierarchy limit")
            counter += 1
            identity = str(counter)
            left, top, right, bottom = layer.bbox
            if layer.blend_mode not in (BlendMode.NORMAL, BlendMode.PASS_THROUGH):
                warnings.append(f"{layer.name}: {layer.blend_mode.value!r} blend mode needs visual review in Unity")
            node = dict(id=identity, name=layer.name, type="layerSection" if layer.is_group() else "bitmapLayer",
                        left=left, top=top, width=right-left, height=bottom-top,
                        opacity=1.0, isVisible=True)
            node["psdLayerId"] = layer.layer_id
            node["sourceLayerIndex"] = source_indices[id(layer)]
            if layer.is_group():
                node["layers"] = convert(layer, depth + 1)
                if not node["layers"] and metadata is None:
                    continue
                if layer.opacity != 255 or layer.has_mask() or any(e.enabled for e in layer.effects):
                    warnings.append(f"{layer.name}: group opacity/mask/effects need visual review; hierarchy preserved")
            elif layer.kind == "type" and not raster_text:
                node["type"] = "textLayer"
                node["opacity"] = layer.opacity / 255.0
                node["textInfo"] = text_info(layer, warnings)
                for effect in layer.effects:
                    if not effect.enabled:
                        continue
                    if effect.name == "GradientOverlay":
                        try:
                            node["textInfo"]["gradient"] = vertical_text_gradient(effect, warnings, layer.name)
                        except (ValueError, TypeError, KeyError, AttributeError) as error:
                            warnings.append(f"{layer.name}: gradient not applied ({error}); use raster text for appearance")
                        continue
                    if effect.name == "Stroke" and effect.color is not None:
                        # Export the stroke color for the local outline material lookup.
                        color = effect.color
                        if all(key in color for key in (b"Rd  ", b"Grn ", b"Bl  ")):
                            node.setdefault("layerEffects", {})["frameFX"] = dict(
                                enabled=True, size=float(effect.size),
                                color=dict(red=float(color[b"Rd  "]), green=float(color[b"Grn "]), blue=float(color[b"Bl  "])))
                            continue
                    warnings.append(f"{layer.name}: {effect.name} not applied to editable text; use raster text for appearance")
            else:
                if right <= left or bottom <= top:
                    warnings.append(f"{layer.name}: empty/adjustment layer skipped")
                    continue
                if layer.clipping:
                    warnings.append(f"{layer.name}: clipping layer needs visual review")
                # Include space around the layer for shadows, strokes and glows.
                padding = 0
                for effect in layer.effects:
                    if effect.enabled:
                        padding = max(padding, math.ceil(float(getattr(effect, "size", 0)) * 2
                                                        + abs(float(getattr(effect, "distance", 0))) + 4))
                viewport = (left-padding, top-padding, right+padding, bottom+padding)
                image = layer.composite(viewport=viewport, layer_filter=(lambda _: True) if metadata is not None else None)
                if image is None:
                    raise ValueError(f"Cannot render layer: {layer.name}")
                image = image.convert("RGBA")
                bounds = image.getchannel("A").getbbox()
                if bounds is None:
                    if metadata is None:
                        warnings.append(f"{layer.name}: fully transparent layer skipped")
                        continue
                    bounds = (0, 0, image.width, image.height)
                image = image.crop(bounds)
                node.update(left=viewport[0]+bounds[0], top=viewport[1]+bounds[1],
                            width=image.width, height=image.height)
                path = textures / f"{identity}.png"
                image.save(path)
                node["images"] = dict(png_xxxhd=path.resolve().as_uri())
                if layer.kind == "type":
                    if depth + 1 >= 10:
                        raise ValueError(f"{layer.name}: raster text child exceeds hierarchy limit")
                    # Keep the original name for prefab rules, but put the image on a
                    # neutral child: prefix component rules can create Text components
                    # even for bitmap layers and would otherwise discard this sprite.
                    raster = dict(node, id=identity + "-raster", name="__PSD_TextRaster")
                    flat.append(raster)
                    node.pop("images")
                    node.update(type="layerSection", layers=[raster])
            if metadata is not None:
                node["roundTripId"] = metadata["stableId"]
                node["name"] = metadata.get("unityName") or layer.name
                for key in ("replacementKey", "bindingName", "prefabKey"):
                    node[key] = metadata.get(key) or ""
                node["isVisible"] = layer.visible
                pixel_node = dict(node)
                if metadata.get("control"):
                    node["control"] = metadata["control"]
                rect = metadata.get("rect", {})
                exported = metadata.get("exportBounds", list(layer.bbox))
                current = layer.bbox
                sx = (current[2]-current[0]) / (exported[2]-exported[0]) if exported[2] > exported[0] else 1
                sy = (current[3]-current[1]) / (exported[3]-exported[1]) if exported[3] > exported[1] else 1
                node.update(left=current[0] + (rect.get("left", current[0])-exported[0])*sx,
                            top=current[1] + (rect.get("top", current[1])-exported[1])*sy,
                            width=rect.get("width", node["width"])*sx,
                            height=rect.get("height", node["height"])*sy)
                if metadata.get("textRaster") and "images" in node:
                    child = dict(pixel_node, id=identity+"-raster", name="__PSD_TextRaster", roundTripId=metadata["stableId"]+":raster",
                                 replacementKey="", bindingName="", prefabKey="")
                    node.pop("images")
                    node.update(type="textLayer", textInfo=dict(text="", size=14, color=dict(r=1,g=1,b=1,a=1)), layers=[child])
                # A graphic on a Unity group is represented by one helper pixel
                # layer in PS. Fold that helper back onto its parent on import.
                if layer.is_group():
                    children = node.get("layers", [])
                    helpers = [c for c in children if roundtrip.get(c.get("psdLayerId"), {}).get("role") == "graphic"]
                    if len(helpers) == 1:
                        helper = helpers[0]
                        for key in ("images", "textInfo"):
                            if key in helper: node[key] = helper[key]
                        if helper.get("type") == "textLayer": node["type"] = "textLayer"
                        node["layers"] = [c for c in children if c not in helpers]
            result.append(node)
            flat.append(node)
            print(f"LAYER {identity}: {layer.name}", flush=True)
        return result

    layers = convert(psd)
    if not layers:
        raise ValueError("PSD contains no exportable visible layers")
    # Explicit local document schema; no external converter/parser is required.
    data = dict(artboard=dict(id="local-psd", name=source.stem, width=psd.width,
                             height=psd.height, layers=layers),
                source=str(source.resolve()), warnings=warnings)
    (output / "layout.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    preview = psd.topil()
    if preview is None:
        preview = psd.composite()
    if preview is not None:
        preview.convert("RGBA").save(output / "preview.png")
    report = dict(source=str(source), width=psd.width, height=psd.height,
                  layers=len(flat), textures=len(list(textures.glob("*.png"))), warnings=warnings)
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False), flush=True)
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--raster-text", action="store_true")
    args = parser.parse_args()
    check_dependencies()
    if args.check:
        print("PSD environment ready")
    else:
        if not args.source or not args.output:
            parser.error("--source and --output are required")
        export(args.source.resolve(), args.output.resolve(), args.raster_text)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    main()
