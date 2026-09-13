# SimpleShot AI 插件模型指南

本目录用于放置可选的 AI 能力所需文件。**主程序不依赖它们**：目录为空时，
截图、录屏、长截图等全部功能照常工作，AI 功能自动隐藏或降级。

## 目录结构

```
SimpleShot.exe
└─ plugins\
   ├─ onnxruntime.dll          # ONNX Runtime 原生库（必需，64 位）
   ├─ MODEL_GUIDE.md           # 本文件
   ├─ ocr\                     # 内置 OCR 引擎（自集成，用户无需安装其它软件）
   │  ├─ det.onnx              # 文本检测
   │  ├─ cls.onnx              # 方向分类（可选，缺失则跳过）
   │  ├─ rec.onnx              # 文本识别
   │  ├─ dict.txt              # 识别字典，每行一个字符
   │  └─ config.ini            # 可选：不同导出版本的参数适配
   └─ sam\                     # 智能抠图（SAM / MobileSAM / SAM-HQ）
      ├─ encoder.onnx          # 图像编码器
      ├─ decoder.onnx          # 提示解码器
      └─ config.ini            # 可选：输入名 / 尺寸 / 归一化适配
```

## 1. ONNX Runtime（必需）

从 NuGet 包 `Microsoft.ML.OnnxRuntime`（建议 1.16+）中获取原生库：

- 下载 `microsoft.ml.onnxruntime.<版本>.nupkg`，解压
- 取 `runtimes\win-x64\native\onnxruntime.dll` 放到 `plugins\`
- **必须使用 x64 版本**（SimpleShot 在 64 位系统上以 64 位进程运行）

## 2. OCR 模型（推荐，全部免费）

推荐来源（PP-OCR 系列，Apache-2.0 许可）：

- **RapidOCR**：<https://github.com/RapidAI/RapidOCR>
  其 `rapidocr_onnxruntime` 发布包内含可直接使用的 ONNX 模型，例如：
  - 检测：`ch_PP-OCRv4_det_infer.onnx`（或 v5 版本）
  - 分类：`ch_ppocr_mobile_v2.0_cls_infer.onnx`
  - 识别：`ch_PP-OCRv4_rec_infer.onnx`
  - 字典：`ppocr_keys_v1.txt`
- **PaddleOCR**：<https://github.com/PaddlePaddle/PaddleOCR>
  官方导出脚本亦可产出同类 ONNX 模型（det / rec / cls）。

把文件重命名放到 `plugins\ocr\` 下即可：`det.onnx`、`cls.onnx`、`rec.onnx`、`dict.txt`。

`plugins\ocr\config.ini`（可选，缺省用下列值）：

```ini
DetModel=det.onnx
ClsModel=cls.onnx
RecModel=rec.onnx
Dict=dict.txt
LimitSide=960
DetMean=0.5,0.5,0.5
DetStd=0.5,0.5,0.5
RecMean=0.5,0.5,0.5
RecStd=0.5,0.5,0.5
UseCls=1
RecHeight=48
RecWidth=320
ClsWidth=192
BlankIndex=0
```

> 若使用 PaddleOCR 官方（非 RapidOCR）导出，归一化通常为
> `DetMean=0.485,0.456,0.406` / `DetStd=0.229,0.224,0.225`，
> 识别端常用 `RecMean=0.5,0.5,0.5` / `RecStd=0.5,0.5,0.5`。

## 3. 抠图模型（SAM / MobileSAM / SAM-HQ）

**已实测可用的一键下载（推荐）**——Acly/MobileSAM（Krita 插件同源，经大量用户验证），
国内直接用 hf-mirror 镜像：

```powershell
cd plugins\sam
curl -L -o encoder.onnx "https://hf-mirror.com/Acly/MobileSAM/resolve/main/mobile_sam_image_encoder.onnx"
curl -L -o decoder.onnx "https://hf-mirror.com/Acly/MobileSAM/resolve/main/sam_mask_decoder_single.onnx"
```

该导出的适配配置已写在 `plugins\sam\config.ini`（编码器 NHWC 无批维、
orig_im_size 为 float32）——随文件一起保留即可。

其它可选来源：Hugging Face 搜索 **MobileSAM ONNX**（如 `onnx-community/mobile-sam`、
`PulpCut/mobilesam-onnx`）；也可自行用官方导出脚本转换
（`segment-anything` / `sam-hq` 的 ONNX 导出）。不同导出的输入布局/类型不同，
用 `config.ini` 的下列开关适配（逐项实测报错信息对应修改）。

放好后，截图工具条上会出现“剪刀”图标：框选主体 → 点剪刀 → 自动以选区中心
作为提示点生成掩膜 → 透明背景预览（棋盘格）→ 点“完成”复制或“保存”另存为 PNG。

`plugins\sam\config.ini`（可选，缺省用下列值）：

```ini
EncoderModel=encoder.onnx
DecoderModel=decoder.onnx
ImageSize=1024
Mean=0.485,0.456,0.406
Std=0.229,0.224,0.225
MaskThreshold=0
Feather=2
SigmoidApplied=0
OrigImSizeType=7
MaskOutputIndex=0
EncoderBatched=1
EncoderLayout=NCHW
```

> `OrigImSizeType`：7 = int64（SAM 官方导出默认），若报
> "Actual: (tensor(int64)) expected: (tensor(float))" 改为 1（float32）。
> `SigmoidApplied`：部分导出已包含 sigmoid，此时置 1。
> `EncoderBatched`：0 = 编码器输入无批维 `[3,S,S]`（报 "Invalid rank ... Expected: 3" 时）。
> `EncoderLayout`：`NHWC` = 输入为 `[S,S,3]` 像素交错（报 dim2 Expected: 3 时）。

**验证**：编译并运行 `tests\MattingTest.cs`（与 `plugins\` 目录同级放置），
会用合成图 + Windows 壁纸做中心点抠图并检查保留率 / 剔除率。

## 常见问题

- **工具条没有剪刀图标**：`plugins\onnxruntime.dll` 或 `plugins\sam\` 模型缺失。
- **识别结果为空**：确认 `dict.txt` 与 `rec.onnx` 类别数匹配；尝试改用对应模型的
  归一化参数。
- **首次识别较慢**：模型加载 + 编码耗时，后续调用会复用会话（进程内只加载一次）。
- **CPU 占用**：OCR 用 2 线程、抠图用 4 线程，可在源码 `OnnxOcr` / `OnnxMatting`
  构造处调整。
