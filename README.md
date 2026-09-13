# 快截 SnapCut — WeChat-style Screenshot + Screen Recorder

A tiny Windows screenshot tool that replicates WeChat's (微信) capture experience,
plus a built-in screen recorder. Written in pure C# / WinForms with **no third-party
dependencies** — it compiles with the C# compiler that already ships inside Windows.

**Final size: ~42 KB single .exe. No runtime install, no NuGet, no bundled codecs.**

---

## Features

### Screenshot (WeChat-parity)
- **Freeze-frame overlay** across all monitors (virtual screen aware, per-monitor DPI aware).
- **Automatic window highlight** — hover to detect the window under the cursor (uses DWM
  frame bounds, ignores cloaked/hidden windows); single click captures it.
- **Drag selection** with a WeChat-green (`#07C160`) border and **8 resize handles**.
- **Move / resize** the selection; **live size badge** (`W × H`).
- **Magnifier + color picker** while selecting: 8× pixel zoom, crosshair, live `POS` and
  `RGB #hex` readout with a color swatch.
- **Annotation toolbar** (all icons drawn in code, no image assets):
  rectangle · ellipse · arrow · pen · mosaic · text · undo · save · record · cancel · ok.
- **Style panel**: 3 stroke widths + 8-color palette.
- **Mosaic** pixelation, tapered **arrows**, freehand **pen**, and **text** just like WeChat.
- **Copy to clipboard** (OK / Enter / double-click) and **Save as** PNG/JPG/BMP (Ctrl+S).

### Screen recording (the extra feature)
- Pick any region (or a window) with the same overlay, then hit the **record** icon.
- Thin **red click-through border** around the region + a compact dark control bar
  (blinking dot · `HH:MM:SS` timer · stop · cancel).
- Captures at 15 fps into a self-contained **MJPEG AVI** — plays in Windows Media Player,
  Movies & TV, and VLC with no codecs to install. Mouse cursor is included in the video.
- On stop, Explorer opens with the new file selected (saved to `Videos\`).

---

## Keyboard shortcuts

| Action                         | Shortcut          |
|--------------------------------|-------------------|
| Start screenshot               | `Alt + A`         |
| Start screen recording         | `Ctrl + Alt + R`  |
| Confirm / copy to clipboard    | `Enter` / `Ctrl+C` / double-click |
| Save to file                   | `Ctrl + S`        |
| Undo last annotation           | `Ctrl + Z`        |
| Cancel current tool / exit     | `Esc`             |
| Reset selection (in overlay)   | Right-click       |

> `Alt + A` matches WeChat's default. If WeChat is running it may own that hotkey first;
> the tray menu and `Ctrl+Alt+R` always work, or close WeChat's shortcut in its settings.

---

## Build

Requirements: **Windows 8 or newer** — that's it. The .NET Framework 4.x C# compiler
(`csc.exe`) is preinstalled at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`.

```powershell
# from the project root
powershell -ExecutionPolicy Bypass -File .\build.ps1
# -> build\SnapCut.exe   (~42 KB)
```

Run it:

```powershell
.\build\SnapCut.exe
```

It starts to the system tray (green crop icon). Double-click the tray icon for a
screenshot, or right-click for the menu / exit.

### Verify the AVI muxer (optional)

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-test.ps1
# -> ALL PASS - valid AVI, 30 frames, ... bytes
```

---

## Project layout

```
simpleShortCut/
├─ build.ps1                # one-command build (uses in-box csc.exe)
├─ README.md
├─ src/
│  ├─ Program.cs            # tray host, global hotkeys, single-instance guard
│  ├─ NativeMethods.cs      # P/Invoke (hotkeys, DPI, DWM, cursor, window styles)
│  ├─ WindowDetector.cs     # z-ordered window enumeration for auto-highlight
│  ├─ OverlayForm.cs        # the capture overlay: selection, magnifier, painting
│  ├─ Toolbar.cs            # WeChat-style toolbar + style panel (GDI+ drawn icons)
│  ├─ Annotation.cs         # shape model + rendering (rect/ellipse/arrow/pen/mosaic/text)
│  ├─ AviWriter.cs          # dependency-free MJPEG AVI muxer
│  └─ RecorderBar.cs        # red border frame + control bar + capture thread
└─ tests/
   ├─ AviTest.cs            # structural verification of AviWriter output
   └─ run-test.ps1
```

---

## Why this stack (size / resource notes)

- **No frameworks/libraries.** WinForms + GDI+ are OS components, so the payload is only
  our compiled code (~42 KB) — far smaller than Electron (~150 MB) or a .NET-self-contained
  build (~60 MB+). Zero install footprint.
- **Screen recording without codecs.** Instead of pulling in FFmpeg or Media Foundation,
  each frame is JPEG-encoded via the built-in GDI+ encoder and wrapped in a hand-written
  MJPEG AVI container. The muxer is ~140 lines and produces files any Windows player opens.
- **Low idle cost.** The app sits in the tray as a hidden message-only window; capture and
  recording resources are created on demand and disposed immediately after use. The recorder
  runs its capture loop on a single background thread with a frame-paced sleep.

### Tuning the recorder

In `src/RecorderBar.cs`:
- `FPS` (default `15`) — higher = smoother but larger files / more CPU.
- `JPEG_QUALITY` (default `75`) — 1–100; higher = better quality, larger files.

---

## Cross-platform note

The capture/annotation/recording **logic** (selection math, the `Annotation` renderer, and
the `AviWriter` muxer) is portable. The **shell** (global hotkeys, window detection, screen
grab, cursor capture, click-through borders) uses Win32/WinForms and is Windows-only. A
Linux/macOS port would keep `Annotation.cs` and `AviWriter.cs` and reimplement the overlay
and capture layer on a cross-platform UI toolkit (e.g. Avalonia) — the biggest such pieces
are already isolated for that reason.

---

## Binary assets (optional, not in the repo)

The compiled `.exe` is fully functional on its own — screenshot, long-screenshot,
annotation, screen recording and the update checker all work with **zero** external files.
A few optional features pull in large binaries that are intentionally **git-ignored**
(see `.gitignore`) so the repo stays small:

| Feature            | Asset                                 | How to get it                                                  |
|--------------------|---------------------------------------|----------------------------------------------------------------|
| Video conversion   | `plugins/ffmpeg/ffmpeg.exe` (~100 MB) | Drop any static `ffmpeg.exe` build there; it gets embedded into the `.exe` at build time. Without it the "video convert" entry is hidden. |
| Offline OCR        | built-in WinRT (Win10/11 only)        | Nothing to download — `build.ps1` auto-detects the in-box WinRT metadata. |
| Smart matting / SAM / PP-OCR models | `plugins/**/*.onnx`, `onnxruntime*.dll` | See `plugins/MODEL_GUIDE.md` for download links. Missing models degrade gracefully (the feature is hidden or shows a friendly tip). |

So a typical clone + `build.ps1` already produces a working `SnapCut.exe`; the assets above
only unlock the extra, model/codec-heavy features.
