# PS2U3D — PSD 转 Unity UI 工具使用说明

## 环境准备（只需做一次）

**1. 安装 Python 依赖**

```bash
pip install psd-tools pillow
```

**2. 确认 Python 路径**

在 Unity 菜单栏打开 `Tools → UI → 2. 构建 Canvas`，窗口顶部填写 Python 可执行程序路径。

| 情况 | 填写值 |
|---|---|
| Python 已加入系统 PATH | `python`（默认，无需修改） |
| 未加入 PATH | 完整路径，如 `C:/Python311/python.exe` |

设置保存在 EditorPrefs 中，只需设置一次。

---

## 每次导入新 UI 的流程

### 第一步：放入 PSD 文件

将美术提供的 `.psd` 文件放入工程根目录下的文件夹：

```
ps文件放到这里/
└── your_ui.psd
```

### 第二步：导出 Sprites

点击菜单 `Tools → UI → 1. 导出 PSD → Sprites`

- 自动读取当前 Game 窗口分辨率作为设计参考分辨率
- 自动扫描 `ps文件放到这里/` 中的所有 `.psd` 文件并逐一处理
- 输出到 `Assets/UI/`：

```
Assets/UI/
├── Sprites/<psd文件名>/   各图层 PNG（自动配置为 UI Sprite）
└── Layouts/<psd文件名>.json   布局数据
```

### 第三步：构建 Canvas

点击菜单 `Tools → UI → 2. 构建 Canvas`，在弹出窗口中：

1. **Browse** 选择 `Assets/UI/Layouts/<psd文件名>.json`
2. **CanvasScaler Match** 根据游戏方向调整：
   - 横屏填 `0`，竖屏填 `1`，不确定填 `0.5`
3. 点击 **Build Canvas**

场景中自动生成包含完整层级的 Canvas，图层对应关系：

| PSD 图层类型 | Unity 组件 |
|---|---|
| 普通图层 | `Image` |
| 文字图层 | `TextMeshProUGUI` |
| 图层组 | `RectTransform`（纯容器） |

---

## 构建后的手动调整

自动构建完成后，以下内容需要手动补充：

- **文字**：字号、颜色、字体 Asset 需在 `TextMeshProUGUI` 组件上逐一设置
- **可拉伸图片**（按钮框等）：在 Sprite Editor 中设置九宫格 Border，再将 Image 的 `Image Type` 改为 `Sliced`

---

## 工程文件结构

```
PS2U3D/
├── ps文件放到这里/          ← PSD 源文件投放目录
├── Assets/
│   ├── UI/
│   │   ├── Sprites/         ← 自动生成的切图
│   │   └── Layouts/         ← 自动生成的布局 JSON
│   └── Editor/AutoUI/
│       ├── psd_exporter.py          Python 切图脚本
│       ├── UITexturePostprocessor.cs  自动配置导入的 Sprite
│       └── PSDLayoutImporter.cs       菜单工具主脚本
└── README.md
```

---

## 常见问题

| 现象 | 原因 | 解决 |
|---|---|---|
| 点击导出后弹出"没有 PSD 文件" | PSD 没放对位置 | 检查文件是否在 `ps文件放到这里/` 目录下 |
| 启动 Python 失败 | Python 不在 PATH 或路径错误 | 在构建 Canvas 窗口中修正 Python 路径 |
| Image 没有 Sprite | 导出时图层为空或不可见 | 检查 PSD 中对应图层是否可见，重新导出 |
| 元素位置偏移 | Game 窗口分辨率与 PSD 设计稿不一致 | 调整 Game 窗口分辨率后重新导出 |
| 部分图层没有导出 | 图层被隐藏 | 在 Photoshop 中打开图层可见性后重新导出 |
