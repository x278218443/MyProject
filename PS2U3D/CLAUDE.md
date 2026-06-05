# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**PS2U3D** — a Unity 2021.3.14f1 (LTS) project. C# 9.0, targeting .NET 4.7.1.

## Build & Run

This is a Unity project; there is no CLI build step separate from the Unity Editor. All compilation happens automatically when Unity opens the project or when scripts are saved.

**Batch-mode build (headless):**
```
"C:\Program Files\Unity\Hub\Editor\2021.3.14f1\Editor\Unity.exe" \
  -batchmode -quit \
  -projectPath "D:/MyProject/PS2U3D/PS2U3D" \
  -buildTarget StandaloneWindows64 \
  -executeMethod BuildScript.Build \
  -logFile build.log
```

**Run tests (Unity Test Framework):**
```
"C:\Program Files\Unity\Hub\Editor\2021.3.14f1\Editor\Unity.exe" \
  -batchmode -quit \
  -projectPath "D:/MyProject/PS2U3D/PS2U3D" \
  -runTests -testPlatform EditMode \
  -testResults testResults.xml \
  -logFile test.log
```

Tests can also be run interactively via **Window → General → Test Runner** inside the Editor.

## Project Structure

```
Assets/          — all game content (scenes, scripts, prefabs, art)
  Scenes/        — Unity scene files (.unity)
Packages/        — Unity Package Manager manifest (manifest.json)
ProjectSettings/ — editor and player settings (committed to version control)
```

`Library/` and `Temp/` are generated caches — never edit or commit them.

## Key Packages

| Package | Purpose |
|---|---|
| `com.unity.test-framework` 1.1.31 | NUnit-based edit/play mode tests |
| `com.unity.textmeshpro` 3.0.6 | Rich-text UI |
| `com.unity.timeline` 1.6.4 | Cinematic/animation sequences |
| `com.unity.visualscripting` 1.7.8 | Graph-based logic |
| `com.unity.ugui` 1.0.0 | Legacy UI system |

## Unity-Specific Conventions

- All scripts go under `Assets/` and must be in a `namespace` matching their folder path.
- `MonoBehaviour` subclasses must live in a file named exactly after the class.
- Use `[SerializeField]` instead of public fields to expose values in the Inspector while keeping fields private.
- Prefer `Awake` for component wiring and `Start` for logic that depends on other components being initialized.
- Asset changes are reflected in `.meta` files — always commit `.meta` files alongside their asset.
