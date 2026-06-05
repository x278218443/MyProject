"""
PSD → Unity UI 自动切图工具
用法: python psd_exporter.py <input.psd> <unity_assets_path> [--screen WxH]

示例: python psd_exporter.py ui_main.psd ../PS2U3D/Assets/UI --screen 1920x1080

输出:
  <unity_assets_path>/Sprites/<psd_name>/*.png   各图层PNG
  <unity_assets_path>/Layouts/<psd_name>.json    布局数据（平铺数组 + parent_index）
"""

import sys
import json
import argparse
from pathlib import Path

try:
    from psd_tools import PSDImage
except ImportError:
    print("ERROR: 缺少依赖，请先运行: pip install psd-tools pillow")
    sys.exit(1)


def sanitize(name: str) -> str:
    for ch in r'\/:*?"<>|':
        name = name.replace(ch, "_")
    return name.strip().replace(" ", "_")


def extract_text(layer) -> str:
    # psd_tools >= 2.0: TypeLayer 直接暴露 .text 属性
    try:
        if hasattr(layer, "text") and layer.text:
            return str(layer.text).replace("\r", "\n").strip()
    except Exception:
        pass
    # 旧版 / 备用：从 engine_dict 深取
    try:
        if hasattr(layer, "engine_dict") and layer.engine_dict:
            editor = layer.engine_dict.get("EngineDict", {}).get("Editor", {})
            text = editor.get("Text", {}).get("Txt ", "")
            if text:
                return text.replace("\r", "\n").strip()
    except Exception:
        pass
    return ""


def flatten(psd, sprite_dir: Path) -> list:
    """
    将 PSD 图层树展平为带 parent_index 的平铺列表。
    psd_tools 迭代顺序：index 0 = 图层面板最底层（背景），最后 = 最顶层（前景）。
    直接按此顺序处理，背景先入列表 → Unity sibling index 最小 → 渲染在最下方，与 PSD 一致。
    """
    result = []

    def process(layer, parent_idx: int):
        if not layer.visible:
            return

        idx = len(result)
        kind = "group" if layer.is_group() else ("text" if layer.kind == "type" else "image")

        node = {
            "name":         layer.name,
            "type":         kind,
            "x":            layer.left,
            "y":            layer.top,
            "width":        layer.width,
            "height":       layer.height,
            "file":         "",
            "text":         "",
            "parent_index": parent_idx,
        }
        result.append(node)

        if layer.is_group():
            for child in list(layer):
                process(child, idx)
        else:
            if kind == "text":
                node["text"] = extract_text(layer)

            # 导出 PNG，文件名附加 idx 避免重名
            file_name = f"{sanitize(layer.name)}_{idx}.png"
            try:
                img = layer.composite()
                if img and img.width > 0 and img.height > 0:
                    img.save(sprite_dir / file_name)
                    node["file"] = file_name
                    print(f"  [OK] [{idx:03d}] {file_name}")
                else:
                    print(f"  [--] [{idx:03d}] {layer.name} (空图层，跳过)")
            except Exception as e:
                print(f"  [NG] [{idx:03d}] {layer.name}: {e}")

    for layer in list(psd):
        process(layer, -1)

    return result


def export(psd_path: str, assets_dir: str, design_w: int = 0, design_h: int = 0):
    psd_path  = Path(psd_path)
    assets_dir = Path(assets_dir)
    psd_name  = psd_path.stem

    sprite_dir = assets_dir / "Sprites" / psd_name
    layout_dir = assets_dir / "Layouts"
    sprite_dir.mkdir(parents=True, exist_ok=True)
    layout_dir.mkdir(parents=True, exist_ok=True)

    print(f"正在解析 {psd_path.name} ...")
    psd = PSDImage.open(str(psd_path))

    ref_w = design_w or psd.width
    ref_h = design_h or psd.height
    print(f"画布: {psd.width}x{psd.height}  参考分辨率: {ref_w}x{ref_h}")
    print("导出图层:")

    layers = flatten(psd, sprite_dir)

    layout = {
        "psd_name":     psd_name,
        "design_width":  ref_w,
        "design_height": ref_h,
        "layers":        layers,
    }

    out_json = layout_dir / f"{psd_name}.json"
    with open(out_json, "w", encoding="utf-8") as f:
        json.dump(layout, f, indent=2, ensure_ascii=False)

    exported = sum(1 for l in layers if l["file"])
    print(f"\n完成！共 {len(layers)} 个节点，导出 {exported} 张 PNG")
    print(f"PNG  → {sprite_dir}")
    print(f"JSON → {out_json}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("psd",    help="PSD 文件路径")
    parser.add_argument("assets", help="Unity Assets 目录路径")
    parser.add_argument("--screen", default="", help="设计分辨率，如 1920x1080")
    args = parser.parse_args()

    w, h = 0, 0
    if args.screen:
        try:
            w, h = map(int, args.screen.lower().split("x"))
        except ValueError:
            print("--screen 格式错误，应为 WxH，如 1920x1080")
            sys.exit(1)

    export(args.psd, args.assets, w, h)


if __name__ == "__main__":
    main()
