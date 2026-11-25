"""Build a collaborative PSD from Unity nodes plus the original PSD design layers."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import copy


def build(payload_path, output):
    from PIL import Image
    from psd_tools import PSDImage
    from psd_tools.api.layers import Group, PixelLayer
    from psd_tools.constants import Tag

    payload = json.loads(payload_path.read_text(encoding="utf-8-sig"))
    psd = PSDImage.new("RGBA", (payload["width"], payload["height"]))
    source_path = payload.get("sourcePsd")
    source = PSDImage.open(source_path) if source_path and Path(source_path).is_file() else None
    source_layers = list(source.descendants()) if source else []
    warnings = list(payload.get("warnings", []))
    mapping = []
    counter = 0

    def original_layer(data):
        if not data:
            return None
        identity = data.get("psdLayerId", -1)
        matches = [l for l in source_layers if identity >= 0 and l.layer_id == identity]
        if len(matches) == 1:
            return matches[0]
        index = data.get("sourceLayerIndex", 0)
        if 0 < index <= len(source_layers):
            return source_layers[index - 1]
        matches = [l for l in source_layers if l.name == data.get("name")]
        return matches[0] if len(matches) == 1 else None

    def register(layer, node, role="node"):
        nonlocal counter
        counter += 1
        # Constructors in 1.17 do not normalize the legacy Pascal name. The
        # setter keeps the full Unicode name in luni and a safe legacy fallback.
        layer.name = node["name"]
        layer.tagged_blocks.set_data(Tag.LAYER_ID, counter)
        layer.visible = node.get("visible", True)
        entry = {"psdLayerId": counter, "stableId": node["stableId"], "unityName": node["name"],
                 "replacementKey": node.get("replacementKey") or "", "bindingName": node.get("bindingName") or "",
                 "prefabKey": node.get("prefabKey") or "", "role": role,
                 "rect": {k: node.get(k, 0) for k in ("left", "top", "width", "height")}}
        if role == "node" and node.get("control"):
            entry["control"] = {key: node["control"].get(key) for key in ("type", "name")}
        entry["textRaster"] = (node.get("textInfo") or {}).get("size", 0) > 0 and layer.kind != "type"
        mapping.append((entry, layer))
        return entry

    def pixels(parent, name, image, left, top):
        image = image.convert("RGBA")
        bounds = image.getchannel("A").getbbox()
        if bounds:
            image = image.crop(bounds)
            left += bounds[0]
            top += bounds[1]
        return PixelLayer.frompil(image, parent, name=name, left=round(left), top=round(top))

    def restore_design(layer, parent, frame, source_frame, root_node, is_root=True):
        sx = frame["width"] / max(1, source_frame["width"])
        sy = frame["height"] / max(1, source_frame["height"])
        node = dict(root_node)
        node["stableId"] = root_node["stableId"] if is_root else root_node["stableId"] + ":design:" + str(layer.layer_id)
        node["name"] = root_node["name"] if is_root else layer.name
        node["visible"] = root_node.get("visible", True) if is_root else layer.visible
        if not is_root:
            node["replacementKey"] = ""
            node["prefabKey"] = ""
            node.pop("control", None)
        if layer.is_group():
            group = Group.new(parent, name=node["name"])
            register(group, node)
            for child in layer:
                restore_design(child, group, frame, source_frame, node, False)
            return group
        padding = 0
        for effect in layer.effects:
            if effect.enabled:
                padding = max(padding, math.ceil(float(getattr(effect, "size", 0)) * 2 + abs(float(getattr(effect, "distance", 0))) + 4))
        left, top, right, bottom = layer.bbox
        image = layer.composite(viewport=(left-padding, top-padding, right+padding, bottom+padding), layer_filter=lambda _: True)
        if image is None:
            raise ValueError("Cannot restore original design layer: " + layer.name)
        image = image.convert("RGBA")
        image = image.resize((max(1, round(image.width * sx)), max(1, round(image.height * sy))), Image.Resampling.LANCZOS)
        result = pixels(parent, node["name"], image, frame["left"] + (left-padding-source_frame["left"]) * sx,
                        frame["top"] + (top-padding-source_frame["top"]) * sy)
        register(result, node)
        return result

    def build_node(node, parent):
        original = node.get("original") or {}
        design = original_layer(original)
        atomic = bool(node.get("replacementKey")) or (node.get("prefabKey") or "").startswith("G_P_")
        if atomic and design is not None:
            return restore_design(design, parent, node, original, node)
        if atomic:
            warnings.append(node["name"] + ": original design could not be found; current appearance exported")
        # Retain native editable text if Unity did not change its contents/size.
        # Adjust both cached pixel bounds and Photoshop's type-tool transform.
        info = node.get("textInfo") or {}
        old_info = original.get("textInfo") or {}
        if design is not None and design.kind == "type" and not node.get("children") and info.get("size", 0) > 0 and node.get("nativeTextCompatible", True):
            same_text = info.get("text", "").replace("\r", "\n") == design.text.replace("\r", "\n")
            same_size = abs(info.get("size", 0) - old_info.get("size", -100)) < 0.01
            if same_text and same_size:
                # The pinned 1.17 reader has no copy_to API. Clone the typed layer
                # records/channels while retaining Photoshop's native type data.
                copied = type(design)(parent, copy.deepcopy(design._record), copy.deepcopy(design._channels))
                parent.append(copied)
                dx = round(node["left"] + node["width"] / 2 - (design.left + design.width / 2))
                dy = round(node["top"] + node["height"] / 2 - (design.top + design.height / 2))
                copied.name = node["name"]
                copied.offset = (design.left + dx, design.top + dy)
                transform = list(copied._data.transform)
                transform[4] += dx
                transform[5] += dy
                copied._data.transform = tuple(transform)
                register(copied, node)
                return copied
        has_image = node.get("image") and Path(node["image"]).is_file()
        if node.get("children") or not has_image:
            group = Group.new(parent, name=node["name"])
            register(group, node)
            if has_image:
                child = dict(node, name="__PSD_Graphic", children=[], stableId=node["stableId"] + ":graphic")
                layer = pixels(group, child["name"], Image.open(node["image"]), 0, 0)
                register(layer, child, "graphic")
            for child in node.get("children", []):
                build_node(child, group)
            return group
        image = Image.open(node["image"]).convert("RGBA")
        result = pixels(parent, node["name"], image, 0, 0)
        register(result, node)
        if info.get("size", 0) > 0:
            warnings.append(node["name"] + ": text exported as pixels because native text could not be retained")
        return result

    for node in payload["nodes"]:
        build_node(node, psd)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + ".writing")
    psd.save(temporary)
    # Validate serialized layer IDs before publishing the file and its sidecar.
    check = PSDImage.open(temporary)
    if len(list(check.descendants())) != len(mapping):
        raise ValueError("PSD layer count changed during serialization")
    entries = []
    for entry, layer in mapping:
        entry["exportBounds"] = list(layer.bbox)
        entries.append(entry)
    sidecar = {"version": 1, "prefabPath": payload.get("prefabPath"), "width": payload["width"], "height": payload["height"],
               "nodes": entries, "warnings": warnings}
    output.with_suffix(".roundtrip.json").write_text(json.dumps(sidecar, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(output)
    output.with_suffix(".report.txt").write_text("\n".join(warnings) if warnings else "Export completed without warnings.", encoding="utf-8")
    print(json.dumps({"output": str(output), "layers": len(mapping), "warnings": warnings}, ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    from psd_export import check_dependencies
    check_dependencies()
    build(args.input.resolve(), args.output.resolve())


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    main()
