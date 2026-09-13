using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace SimpleShot
{
    /// <summary>
    /// 内置 OCR 引擎（PP-OCR 系列 ONNX 模型，取自 RapidOCR / PaddleOCR 官方导出）：
    ///   文本检测(det) -> 方向分类(cls, 可选) -> 文本识别(rec) -> CTC 解码。
    /// 模型放在 plugins\ocr\ 下（默认文件名对应 RapidOCR 的 PP-OCRv4 导出），
    /// 归一化参数、输入尺寸等均可通过 plugins\ocr\config.ini 调整以适配不同导出版本。
    /// 缺少运行时或模型时全部自动降级，不影响主程序。
    /// 预处理/后处理严格对齐 RapidOCR 参考实现：
    ///   det 与 rec 均使用 (x/255 - 0.5) / 0.5 归一化；DB 后处理含膨胀、unclip 外扩与分数阈值；
    ///   rec 词表直接来自模型元数据，索引 0 为 blank。
    /// </summary>
    internal sealed class OnnxOcr : IDisposable
    {
        private static OnnxOcr _inst;
        private static bool _failed;

        private Onnx.Session _det, _cls, _rec;
        private string[] _dict;

        // ---- 可调参数（默认值适配 RapidOCR 的 PP-OCRv4 导出）----
        private string _detFile = "ch_PP-OCRv4_det_infer.onnx";
        private string _clsFile = "ch_ppocr_mobile_v2.0_cls_infer.onnx";
        private string _recFile = "ch_PP-OCRv4_rec_infer.onnx";
        private string _dictFile = "ppocr_keys_v1.txt";
        private int _limitSide = 736;                 // det 短边下限（limit_type=min）
        private bool _useCls = true;
        private int _recHeight = 48, _recWidth = 320, _clsWidth = 192;

        /// <summary>运行时与必需模型是否齐备。</summary>
        public static bool Available
        {
            get
            {
                if (_failed) return false;
                if (!Onnx.Available) return false;
                bool det = File.Exists(Onnx.ModelPath("ocr", "det.onnx")) ||
                           File.Exists(Onnx.ModelPath("ocr", "ch_PP-OCRv4_det_infer.onnx"));
                bool rec = File.Exists(Onnx.ModelPath("ocr", "rec.onnx")) ||
                           File.Exists(Onnx.ModelPath("ocr", "ch_PP-OCRv4_rec_infer.onnx"));
                return det && rec;
            }
        }

        /// <summary>获取共享实例（模型较大，进程内只加载一次）。</summary>
        public static OnnxOcr Get()
        {
            if (_inst != null) return _inst;
            if (_failed) return null;
            try { _inst = new OnnxOcr(); }
            catch { _failed = true; _inst = null; }
            return _inst;
        }

        private OnnxOcr()
        {
            LoadConfig();
            _det = new Onnx.Session(DetPath(), 2);
            _rec = new Onnx.Session(RecPath(), 2);
            try { if (_useCls) _cls = new Onnx.Session(ClsPath(), 1); }
            catch { _cls = null; _useCls = false; }

            string dictPath = Onnx.ModelPath("ocr", _dictFile);
            if (File.Exists(dictPath)) _dict = File.ReadAllLines(dictPath, Encoding.UTF8);
            else _dict = null;
        }

        private string DetPath()
        {
            string p = Onnx.ModelPath("ocr", _detFile);
            return File.Exists(p) ? p : Onnx.ModelPath("ocr", "det.onnx");
        }
        private string ClsPath()
        {
            string p = Onnx.ModelPath("ocr", _clsFile);
            return File.Exists(p) ? p : Onnx.ModelPath("ocr", "cls.onnx");
        }
        private string RecPath()
        {
            string p = Onnx.ModelPath("ocr", _recFile);
            return File.Exists(p) ? p : Onnx.ModelPath("ocr", "rec.onnx");
        }

        public void Dispose()
        {
            if (_det != null) { _det.Dispose(); _det = null; }
            if (_cls != null) { _cls.Dispose(); _cls = null; }
            if (_rec != null) { _rec.Dispose(); _rec = null; }
        }

        /// <summary>识别位图中的文字，按行输出。</summary>
        public string Recognize(Bitmap src)
        {
            if (src == null || src.Width < 4 || src.Height < 4) return null;

            // ---- 1. 检测：缩放到 32 的倍数，归一化 (x/255-0.5)/0.5 ----
            int rw, rh;
            Bitmap detImg = DetPreprocess(src, out rw, out rh);
            float[] detIn = ToNchw(detImg, 0.5f);
            detImg.Dispose();

            OnnxTensor map = _det.Run(detIn, new long[] { 1, 3, rh, rw });
            var boxes = DetPostprocess(map, rw, rh, src.Width, src.Height);
            if (boxes.Count == 0) return null;

            // ---- 2/3. 方向分类 + 识别（从上到下、从左到右）----
            var lines = new List<string>();
            foreach (var r in boxes)
            {
                using (var crop = Crop(src, r))
                {
                    bool rot = _useCls && _cls != null && ShouldRotate180(crop);
                    if (rot) crop.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    string line = RecognizeLine(crop);
                    if (!string.IsNullOrEmpty(line)) lines.Add(line);
                }
            }
            return lines.Count == 0 ? null : string.Join("\r\n", lines.ToArray());
        }

        // ---------------- 检测预处理 ----------------

        private Bitmap DetPreprocess(Bitmap src, out int rw, out int rh)
        {
            int w = src.Width, h = src.Height;
            // limit_type=min：短边不足 _limitSide 时放大到 _limitSide
            float ratio = 1f;
            int minSide = Math.Min(w, h);
            if (minSide < _limitSide) ratio = (float)_limitSide / minSide;
            rw = Round32((int)Math.Round(w * ratio));
            rh = Round32((int)Math.Round(h * ratio));
            // 防止极端长宽比导致尺寸爆炸：长边封顶 2000
            int maxSide = Math.Max(rw, rh);
            if (maxSide > 2000)
            {
                float s = 2000f / maxSide;
                rw = Round32((int)(rw * s));
                rh = Round32((int)(rh * s));
            }
            var bmp = new Bitmap(rw, rh, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, rw, rh);
            }
            return bmp;
        }

        private static int Round32(int v)
        {
            int r = (int)Math.Round(v / 32f) * 32;
            return r < 32 ? 32 : r;
        }

        /// <summary>
        /// DB 概率图后处理：阈值化 -> 2x2 膨胀 -> 连通域 -> 外接框(unclip 外扩) -> 分数过滤 -> 映射回原图。
        /// 对齐 RapidOCR：thresh=0.3、use_dilation、unclip_ratio=1.6、box_thresh=0.5、min_size=3。
        /// 文本行多为横排，此处用轴对齐外接框（足够且稳健），旋转文本由识别阶段容错。
        /// </summary>
        private static List<Rectangle> DetPostprocess(OnnxTensor t, int w, int h, int srcW, int srcH)
        {
            var res = new List<Rectangle>();
            if (t.Shape.Length < 4) return res;
            int H = (int)t.Shape[2], W = (int)t.Shape[3];
            if (H != h || W != w) { h = H; w = W; }

            var mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++) mask[i] = t.Data[i] > 0.3f;
            Dilate2x2(mask, w, h);                       // use_dilation（kernel [[1,1],[1,1]]）

            float scaleX = (float)srcW / w;
            float scaleY = (float)srcH / h;

            var visited = new bool[mask.Length];
            var stack = new Stack<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start]) continue;
                visited[start] = true;
                stack.Clear();
                stack.Push(start);
                int x0 = w, y0 = h, x1 = 0, y1 = 0, count = 0;
                float sum = 0f;
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    int y = k / w, x = k - y * w;
                    if (x < x0) x0 = x; if (y < y0) y0 = y;
                    if (x > x1) x1 = x; if (y > y1) y1 = y;
                    sum += t.Data[k]; count++;
                    // 8 连通
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int nk = ny * w + nx;
                            if (!visited[nk] && mask[nk]) { visited[nk] = true; stack.Push(nk); }
                        }
                }
                int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                if (bw < 3 || bh < 3) continue;
                float score = count > 0 ? sum / count : 0f;
                if (score < 0.5f) continue;             // box_thresh
                // unclip_ratio=1.6：按面积/周长外扩（与 RapidOCR 默认一致，避免切字）
                float d = (bw * bh * 1.6f) / (2f * (bw + bh));
                float fx0 = x0 - d, fy0 = y0 - d, fx1 = x1 + d, fy1 = y1 + d;
                int ox0 = (int)Math.Round(Clamp(fx0, 0, w - 1) * scaleX);
                int oy0 = (int)Math.Round(Clamp(fy0, 0, h - 1) * scaleY);
                int ox1 = (int)Math.Round(Clamp(fx1, 0, w - 1) * scaleX);
                int oy1 = (int)Math.Round(Clamp(fy1, 0, h - 1) * scaleY);
                if (ox1 - ox0 < 2 || oy1 - oy0 < 2) continue;
                res.Add(Rectangle.FromLTRB(ox0, oy0, ox1, oy1));
            }
            // 从上到下、从左到右排序
            res.Sort(delegate(Rectangle a, Rectangle b)
            {
                int dy = Math.Abs(a.Y - b.Y);
                if (dy > Math.Min(a.Height, b.Height) / 2) return a.Y.CompareTo(b.Y);
                return a.X.CompareTo(b.X);
            });
            return res;
        }

        private static void Dilate2x2(bool[] mask, int w, int h)
        {
            var outp = new bool[mask.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool set = mask[y * w + x];
                    if (!set && x + 1 < w && mask[y * w + x + 1]) set = true;
                    if (!set && y + 1 < h && mask[(y + 1) * w + x]) set = true;
                    if (!set && x + 1 < w && y + 1 < h && mask[(y + 1) * w + x + 1]) set = true;
                    outp[y * w + x] = set;
                }
            Array.Copy(outp, mask, mask.Length);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ---------------- 分类 / 识别 ----------------

        private bool ShouldRotate180(Bitmap crop)
        {
            try
            {
                int h = 48, w = _clsWidth;
                int cw = Math.Min(w, (int)Math.Ceiling(48f * crop.Width / crop.Height));
                using (var b = Fit(crop, h, w, 0f, cw))       // 黑边填充，对齐 RapidOCR
                {
                    float[] input = ToNchw(b, 0.5f);
                    var t = _cls.Run(input, new long[] { 1, 3, h, w });
                    int idx = Onnx.Session.ArgMax(t, 0, Math.Min(2, t.Data.Length));
                    float score = t.Data.Length > 1 ? t.Data[idx] : 1f;
                    return idx == 1 && score > 0.9f;          // label '180'
                }
            }
            catch { return false; }
        }

        private string RecognizeLine(Bitmap crop)
        {
            int tw = (int)Math.Ceiling(_recHeight * (float)crop.Width / crop.Height);
            if (tw < 1) tw = 1;
            if (tw > _recWidth) tw = _recWidth;
            using (var b = Fit(crop, _recHeight, _recWidth, 0f, tw))   // 黑边填充
            {
                float[] input = ToNchw(b, 0.5f);
                var t = _rec.Run(input, new long[] { 1, 3, _recHeight, _recWidth });
                return DecodeCtc(t);
            }
        }

        /// <summary>把任意裁剪图缩放到固定高（按比例限宽）后右侧补黑边。</summary>
        private static Bitmap Fit(Bitmap crop, int h, int w, float padGray, int contentW)
        {
            if (contentW <= 0) contentW = w;
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(crop, 0, 0, contentW, h);
            }
            return bmp;
        }

        /// <summary>CTC 贪心解码：跳过 blank(索引0)、折叠连续重复。</summary>
        private string DecodeCtc(OnnxTensor t)
        {
            if (t.Shape.Length < 3) return "";
            int seq = (int)t.Shape[1];
            int cls = (int)t.Shape[2];
            if (seq == 0 || cls == 0) return "";
            var sb = new StringBuilder();
            int prev = -1;
            for (int i = 0; i < seq; i++)
            {
                int idx = Onnx.Session.ArgMax(t, i * cls, cls);
                if (idx == 0) { prev = idx; continue; }    // blank
                if (idx == prev) continue;
                prev = idx;
                string ch = CharOf(idx);
                if (ch != null) sb.Append(ch);
            }
            return sb.ToString();
        }

        private string CharOf(int idx)
        {
            if (_dict == null || idx < 0 || idx >= _dict.Length) return null;
            return _dict[idx];
        }

        // ---------------- 通用工具 ----------------

        /// <summary>Bitmap -> NCHW float，并做 (x/255 - 0.5)/0.5 归一化（mean=std=0.5）。</summary>
        private static float[] ToNchw(Bitmap bmp, float k)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                                    PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var out1 = new float[3 * h * w];
                unsafe
                {
                    byte* p = (byte*)data.Scan0;
                    int c = h * w;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = p + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte* px = row + x * 3;
                            int idx = y * w + x;
                            // 位图为 BGR，归一化对称故通道顺序无关
                            out1[idx] = (px[2] / 255f - k) / k;
                            out1[c + idx] = (px[1] / 255f - k) / k;
                            out1[2 * c + idx] = (px[0] / 255f - k) / k;
                        }
                    }
                }
                return out1;
            }
            finally { bmp.UnlockBits(data); }
        }

        private static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width <= 0 || r.Height <= 0) return new Bitmap(1, 1);
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            return bmp;
        }

        // ---------------- 配置 ----------------

        private void LoadConfig()
        {
            string path = Onnx.ModelPath("ocr", "config.ini");
            if (!File.Exists(path)) return;
            try
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                _detFile = Str(map, "DetModel", _detFile);
                _clsFile = Str(map, "ClsModel", _clsFile);
                _recFile = Str(map, "RecModel", _recFile);
                _dictFile = Str(map, "Dict", _dictFile);
                _limitSide = Int(map, "LimitSide", _limitSide);
                _useCls = Int(map, "UseCls", 1) != 0;
                _recHeight = Int(map, "RecHeight", _recHeight);
                _recWidth = Int(map, "RecWidth", _recWidth);
                _clsWidth = Int(map, "ClsWidth", _clsWidth);
            }
            catch { }
        }

        private static string Str(Dictionary<string, string> m, string k, string d)
        {
            string v; return m.TryGetValue(k, out v) && v.Length > 0 ? v : d;
        }
        private static int Int(Dictionary<string, string> m, string k, int d)
        {
            string v; int r;
            return m.TryGetValue(k, out v) &&
                   int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : d;
        }
    }
}
