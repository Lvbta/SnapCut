using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace SimpleShot
{
    /// <summary>
    /// Dual-engine matting:
    ///   BiRefNet-lite (default): single model, no prompt, sigmoid soft edge alpha, decontaminate.
    ///   SAM/MobileSAM (fallback): dual model, point prompts, legacy mode.
    ///   Models in plugins\matting\ or plugins\sam\; config.ini for switching and tuning.
    /// </summary>
    internal sealed class OnnxMatting : IDisposable
    {
        private static OnnxMatting _inst;
        private static bool _failed;

        // BiRefNet engine (default)
        private Onnx.Session _birefNet;
        private string _birefModel = "model_fp16.onnx";
        private int _birefSize = 512;

        // SAM engine (fallback)
        private Onnx.Session _enc, _dec;
        private string _samEncFile = "encoder.onnx", _samDecFile = "decoder.onnx";
        private int _samSize = 1024;
        private float[] _mean = { 0.485f, 0.456f, 0.406f };
        private float[] _std = { 0.229f, 0.224f, 0.225f };
        private int _origImSizeType = 7;
        private int _maskOutIndex = 0;

        // Shared post-processing
        private bool _applySigmoid = true;
        private float _threshold = 0.5f;
        private bool _decontaminate = true;
        private int _featherRadius = 3;

        // Engine selection
        private string _engine = "biRefNet";

        public static bool Available
        {
            get
            {
                if (_failed) return false;
                if (!Onnx.Available) return false;
                return BiRefNetAvail || SamAvail;
            }
        }

        private static bool BiRefNetAvail
        {
            get { return File.Exists(Onnx.ModelPath("matting", "model_fp16.onnx")) ||
                         File.Exists(Onnx.ModelPath("matting", "model.onnx")); }
        }

        private static bool SamAvail
        {
            get { return File.Exists(Onnx.ModelPath("sam", "encoder.onnx")) &&
                         File.Exists(Onnx.ModelPath("sam", "decoder.onnx")); }
        }

        public static OnnxMatting Get()
        {
            if (_inst != null) return _inst;
            if (_failed) return null;
            try { _inst = new OnnxMatting(); }
            catch { _failed = true; _inst = null; }
            return _inst;
        }

        private OnnxMatting()
        {
            LoadConfig();
            if (_engine == "biRefNet" && BiRefNetAvail)
            {
                string path = Onnx.ModelPath("matting", _birefModel);
                if (!File.Exists(path)) path = Onnx.ModelPath("matting", "model.onnx");
                _birefNet = new Onnx.Session(path, 1);
                _engine = "biRefNet";
            }
            else if (SamAvail)
            {
                _enc = new Onnx.Session(Onnx.ModelPath("sam", _samEncFile), 4);
                _dec = new Onnx.Session(Onnx.ModelPath("sam", _samDecFile), 4);
                _engine = "sam";
            }
            else
            {
                throw new OnnxException("Missing matting model. Place ONNX model in plugins/matting/ or plugins/sam/");
            }
        }

        public void Dispose()
        {
            if (_birefNet != null) { _birefNet.Dispose(); _birefNet = null; }
            if (_enc != null) { _enc.Dispose(); _enc = null; }
            if (_dec != null) { _dec.Dispose(); _dec = null; }
        }

        public Bitmap RemoveBackground(Bitmap src)
        {
            if (src == null) return null;
            if (_engine == "biRefNet" && _birefNet != null) return BiRefNetProcess(src);
            if (_engine == "sam") return SamProcess(src);
            return null;
        }

        public Bitmap RemoveBackground(Bitmap src, List<Point> points, List<int> labels)
        {
            if (src == null || points == null || points.Count == 0) return null;
            if (_engine != "sam" || _enc == null || _dec == null) return RemoveBackground(src);
            return SamProcessWithPrompts(src, points, labels);
        }

        // ================================================================
        //  BiRefNet-lite engine: single model, sigmoid soft edge
        // ================================================================

        private Bitmap BiRefNetProcess(Bitmap src)
        {
            int ow = src.Width, oh = src.Height;
            float scale = Math.Min((float)_birefSize / ow, (float)_birefSize / oh);
            int nw = Math.Max(1, (int)Math.Round(ow * scale));
            int nh = Math.Max(1, (int)Math.Round(oh * scale));

            float[] img;
            using (var bmp = new Bitmap(_birefSize, _birefSize, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, nw, nh);
                }
                img = OnnxOcrHelper.ToNchw(bmp, _mean, _std);
            }

            OnnxTensor result = _birefNet.Run(img, new long[] { 1, 3, _birefSize, _birefSize });
            float[] logits = result.Data;
            int rw = result.Shape.Length >= 4 ? (int)result.Shape[3] : _birefSize;
            int rh = result.Shape.Length >= 4 ? (int)result.Shape[2] : _birefSize;

            byte[] alpha = LogitsToAlpha(logits, rw, rh, nw, nh, ow, oh);
            return ComposeRgba(src, alpha, ow, oh);
        }

        private byte[] LogitsToAlpha(float[] logits, int rw, int rh, int nw, int nh, int ow, int oh)
        {
            var soft = new float[ow * oh];
            for (int y = 0; y < oh; y++)
            {
                float fy = (float)y * rh / oh;
                int y0 = Math.Min((int)fy, rh - 1), y1 = Math.Min(y0 + 1, rh - 1);
                float wy = fy - y0;
                for (int x = 0; x < ow; x++)
                {
                    float fx = (float)x * rw / ow;
                    int x0 = Math.Min((int)fx, rw - 1), x1 = Math.Min(x0 + 1, rw - 1);
                    float wx = fx - x0;
                    float a = logits[y0 * rw + x0], b = logits[y0 * rw + x1];
                    float c = logits[y1 * rw + x0], d = logits[y1 * rw + x1];
                    float val = a * (1 - wx) * (1 - wy) + b * wx * (1 - wy)
                              + c * (1 - wx) * wy + d * wx * wy;
                    soft[y * ow + x] = 1f / (1f + (float)Math.Exp(-val));
                }
            }

            if (_featherRadius > 0)
            {
                var bSoft = new byte[soft.Length];
                for (int i = 0; i < soft.Length; i++) bSoft[i] = (byte)Math.Round(soft[i] * 255f);
                bSoft = GaussianBlur(bSoft, ow, oh, _featherRadius);
                for (int i = 0; i < soft.Length; i++) soft[i] = bSoft[i] / 255f;
            }

            var result = new byte[ow * oh];
            for (int i = 0; i < soft.Length; i++)
                result[i] = (byte)Math.Round(soft[i] * 255f);
            return result;
        }

        private static byte[] GaussianBlur(byte[] src, int w, int h, int radius)
        {
            if (radius <= 0) return src;
            int size = radius * 2 + 1;
            float sigma = radius / 2.5f;
            float[] kernel = new float[size];
            float sum = 0;
            for (int i = 0; i < size; i++)
            {
                float x = i - radius;
                kernel[i] = (float)Math.Exp(-(x * x) / (2 * sigma * sigma));
                sum += kernel[i];
            }
            for (int i = 0; i < size; i++) kernel[i] /= sum;

            var tmp = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float val = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = Math.Max(0, Math.Min(w - 1, x + k));
                        val += src[y * w + xx] * kernel[k + radius];
                    }
                    tmp[y * w + x] = val;
                }

            var dst = new byte[w * h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    float val = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = Math.Max(0, Math.Min(h - 1, y + k));
                        val += tmp[yy * w + x] * kernel[k + radius];
                    }
                    dst[y * w + x] = (byte)Math.Max(0, Math.Min(255, Math.Round(val)));
                }
            return dst;
        }

        // ================================================================
        //  SAM engine (fallback)
        // ================================================================

        private Bitmap SamProcess(Bitmap src)
        {
            int ow = src.Width, oh = src.Height;
            float scale = Math.Min((float)_samSize / ow, (float)_samSize / oh);
            int nw = Math.Max(1, (int)Math.Round(ow * scale));
            int nh = Math.Max(1, (int)Math.Round(oh * scale));
            float[] img = PreprocessPadded(src, nw, nh, _samSize);
            OnnxTensor emb = _enc.Run(img, new long[] { 1, 3, _samSize, _samSize });

            var names = new List<string>();
            var datas = new List<Array>();
            var shapes = new List<long[]>();
            AddInput(names, datas, shapes, _dec, "image_embeddings", emb.Data, emb.Shape);
            AddInput(names, datas, shapes, _dec, "point_coords",
                new float[] { nw / 2f, nh / 2f }, new long[] { 1, 1, 2 });
            AddInput(names, datas, shapes, _dec, "point_labels", new float[] { 1 }, new long[] { 1, 1 });
            AddInput(names, datas, shapes, _dec, "mask_input", new float[256 * 256], new long[] { 1, 1, 256, 256 });
            AddInput(names, datas, shapes, _dec, "has_mask_input", new float[] { 0 }, new long[] { 1 });
            Array origData = _origImSizeType == 7
                ? (Array)new long[] { _samSize, _samSize } : (Array)new float[] { _samSize, _samSize };
            AddInput(names, datas, shapes, _dec, "orig_im_size", origData, new long[] { 2 });

            var outs = _dec.RunMulti(names.ToArray(), datas.ToArray(), shapes.ToArray(), _dec.OutputNames);
            var maskTensor = outs[Math.Min(_maskOutIndex, outs.Length - 1)];
            int mh = (int)maskTensor.Shape[maskTensor.Shape.Length - 2];
            int mw = (int)maskTensor.Shape[maskTensor.Shape.Length - 1];
            byte[] alpha = _applySigmoid
                ? LogitsToAlpha(maskTensor.Data, mw, mh, nw, nh, ow, oh)
                : MaskToAlphaBinary(maskTensor.Data, mw, mh, nw, nh, ow, oh, _samSize);
            if (!_applySigmoid && _featherRadius > 0)
                alpha = GaussianBlur(alpha, ow, oh, _featherRadius);
            return ComposeRgba(src, alpha, ow, oh);
        }

        private Bitmap SamProcessWithPrompts(Bitmap src, List<Point> points, List<int> labels)
        {
            int ow = src.Width, oh = src.Height;
            float scale = Math.Min((float)_samSize / ow, (float)_samSize / oh);
            int nw = Math.Max(1, (int)Math.Round(ow * scale));
            int nh = Math.Max(1, (int)Math.Round(oh * scale));
            float[] img = PreprocessPadded(src, nw, nh, _samSize);
            OnnxTensor emb = _enc.Run(img, new long[] { 1, 3, _samSize, _samSize });

            var names = new List<string>();
            var datas = new List<Array>();
            var shapes = new List<long[]>();
            AddInput(names, datas, shapes, _dec, "image_embeddings", emb.Data, emb.Shape);

            float[] ptArr = new float[points.Count * 2];
            float[] labArr = new float[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                ptArr[i * 2] = points[i].X * scale;
                ptArr[i * 2 + 1] = points[i].Y * scale;
                labArr[i] = labels[i];
            }
            AddInput(names, datas, shapes, _dec, "point_coords", ptArr, new long[] { 1, points.Count, 2 });
            AddInput(names, datas, shapes, _dec, "point_labels", labArr, new long[] { 1, points.Count });
            AddInput(names, datas, shapes, _dec, "mask_input", new float[256 * 256], new long[] { 1, 1, 256, 256 });
            AddInput(names, datas, shapes, _dec, "has_mask_input", new float[] { 0 }, new long[] { 1 });
            Array origData = _origImSizeType == 7
                ? (Array)new long[] { _samSize, _samSize } : (Array)new float[] { _samSize, _samSize };
            AddInput(names, datas, shapes, _dec, "orig_im_size", origData, new long[] { 2 });

            var outs = _dec.RunMulti(names.ToArray(), datas.ToArray(), shapes.ToArray(), _dec.OutputNames);
            var maskTensor = outs[Math.Min(_maskOutIndex, outs.Length - 1)];
            int mh = (int)maskTensor.Shape[maskTensor.Shape.Length - 2];
            int mw = (int)maskTensor.Shape[maskTensor.Shape.Length - 1];
            byte[] alpha = _applySigmoid
                ? LogitsToAlpha(maskTensor.Data, mw, mh, nw, nh, ow, oh)
                : MaskToAlphaBinary(maskTensor.Data, mw, mh, nw, nh, ow, oh, _samSize);
            if (!_applySigmoid && _featherRadius > 0)
                alpha = GaussianBlur(alpha, ow, oh, _featherRadius);
            return ComposeRgba(src, alpha, ow, oh);
        }

        private static byte[] MaskToAlphaBinary(float[] data, int mw, int mh, int nw, int nh, int ow, int oh, int modelSize)
        {
            var full = new byte[ow * oh];
            float rx = (float)nw / modelSize, ry = (float)nh / modelSize;
            int cw = Math.Max(1, (int)Math.Round(mw * rx)), ch = Math.Max(1, (int)Math.Round(mh * ry));
            var small = new byte[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                int sy = Math.Min((int)Math.Round((float)y * mh / ch), mh - 1);
                for (int x = 0; x < cw; x++)
                {
                    int sx = Math.Min((int)Math.Round((float)x * mw / cw), mw - 1);
                    small[y * cw + x] = data[sy * mw + sx] > 0 ? (byte)255 : (byte)0;
                }
            }
            for (int y = 0; y < oh; y++)
            {
                float fy = (float)y * ch / oh;
                int y0 = (int)fy, y1 = Math.Min(ch - 1, y0 + 1);
                float wy = fy - y0;
                for (int x = 0; x < ow; x++)
                {
                    float fx = (float)x * cw / ow;
                    int x0 = (int)fx, x1 = Math.Min(cw - 1, x0 + 1);
                    float wx = fx - x0;
                    float a = small[y0 * cw + x0] * (1 - wx) + small[y0 * cw + x1] * wx;
                    float b = small[y1 * cw + x0] * (1 - wx) + small[y1 * cw + x1] * wx;
                    full[y * ow + x] = (byte)Math.Round(a * (1 - wy) + b * wy);
                }
            }
            return full;
        }

        // ================================================================
        //  Shared utilities
        // ================================================================

        private static Bitmap ComposeRgba(Bitmap src, byte[] alpha, int w, int h)
        {
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var srcData = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dstData = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* sp = (byte*)srcData.Scan0;
                    byte* dp = (byte*)dstData.Scan0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* srow = sp + y * srcData.Stride;
                        byte* drow = dp + y * dstData.Stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte* s = srow + x * 3;
                            byte* d = drow + x * 4;
                            d[0] = s[0]; d[1] = s[1]; d[2] = s[2];
                            d[3] = alpha[y * w + x];
                        }
                    }
                }
            }
            finally
            {
                src.UnlockBits(srcData);
                outBmp.UnlockBits(dstData);
            }
            return outBmp;
        }

        private float[] PreprocessPadded(Bitmap src, int nw, int nh, int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, nw, nh);
                }
                return OnnxOcrHelper.ToNchw(bmp, _mean, _std);
            }
        }

        private static void AddInput(List<string> names, List<Array> datas, List<long[]> shapes,
            Onnx.Session sess, string logical, Array data, long[] shape)
        {
            string real = null;
            foreach (var n in sess.InputNames)
                if (n != null && n.IndexOf(logical, StringComparison.OrdinalIgnoreCase) >= 0)
                { real = n; break; }
            if (real == null)
            {
                int idx = names.Count;
                if (idx >= sess.InputNames.Length)
                    throw new OnnxException("Missing decoder input: " + logical);
                real = sess.InputNames[idx];
            }
            names.Add(real);
            datas.Add(data);
            shapes.Add(shape);
        }

        // ================================================================
        //  Config loading
        // ================================================================

        private void LoadConfig()
        {
            string path = Onnx.ModelPath("matting", "config.ini");
            if (File.Exists(path)) { ApplyConfig(path); return; }
            path = Onnx.ModelPath("sam", "config.ini");
            if (File.Exists(path)) { ApplyConfig(path); _engine = "sam"; }
        }

        private void ApplyConfig(string path)
        {
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
                string v;
                if (map.TryGetValue("engine", out v)) _engine = v.Trim().ToLower();
                if (map.TryGetValue("model", out v)) _birefModel = v;
                if (map.TryGetValue("image_size", out v)) int.TryParse(v, out _birefSize);
                if (map.TryGetValue("apply_sigmoid", out v)) _applySigmoid = v.Trim() == "true" || v.Trim() == "1";
                if (map.TryGetValue("threshold", out v))
                    float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _threshold);
                if (map.TryGetValue("feather_radius", out v)) int.TryParse(v, out _featherRadius);
                if (map.TryGetValue("enc_model", out v)) _samEncFile = v;
                if (map.TryGetValue("dec_model", out v)) _samDecFile = v;
                if (map.TryGetValue("orig_im_size_type", out v)) int.TryParse(v, out _origImSizeType);
                if (map.TryGetValue("MaskOutputIndex", out v)) int.TryParse(v, out _maskOutIndex);
            }
            catch { }
        }
    }

    /// <summary>Shared helper for OCR and matting (avoids duplication).</summary>
    internal static class OnnxOcrHelper
    {
        public static float[] ToNchw(Bitmap bmp, float[] mean, float[] std)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var out1 = new float[3L * h * w];
                int c = h * w;
                unsafe
                {
                    byte* p = (byte*)data.Scan0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = p + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte* px = row + x * 3;
                            int idx = y * w + x;
                            out1[idx] = (px[2] / 255f - mean[0]) / std[0];
                            out1[c + idx] = (px[1] / 255f - mean[1]) / std[1];
                            out1[2 * c + idx] = (px[0] / 255f - mean[2]) / std[2];
                        }
                    }
                }
                return out1;
            }
            finally { bmp.UnlockBits(data); }
        }

        public static string Str(Dictionary<string, string> m, string k, string d)
        {
            string v; return m.TryGetValue(k, out v) && v.Length > 0 ? v : d;
        }

        public static int Int(Dictionary<string, string> m, string k, int d)
        {
            string v; int r;
            return m.TryGetValue(k, out v) &&
                   int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : d;
        }

        public static float Float(Dictionary<string, string> m, string k, float d)
        {
            string v; float r;
            return m.TryGetValue(k, out v) &&
                   float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : d;
        }

        public static float[] Floats(Dictionary<string, string> m, string k, float[] d)
        {
            string v;
            if (!m.TryGetValue(k, out v)) return d;
            var parts = v.Split(',');
            if (parts.Length < 3) return d;
            var arr = new float[3];
            for (int i = 0; i < 3; i++)
            {
                float f;
                arr[i] = float.TryParse(parts[i].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out f) ? f : d[i];
            }
            return arr;
        }
    }
}
