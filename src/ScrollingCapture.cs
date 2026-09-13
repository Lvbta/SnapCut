using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SimpleShot
{
    /// <summary>
    /// 长截图底层工具：抓帧与行签名。行签名（4 水平分段加权均值）用于粘性区掩码、
    /// 静止判定与位移估计；拼接匹配见 StitchEngine —— 画布锚定的逐行匹配。
    /// </summary>
    internal static class ScrollingCapture
    {
        /// <summary>拼接结果高度上限（px）。</summary>
        internal const int MaxHeight = 30000;

        /// <summary>每行采样的水平分段数。</summary>
        private const int Segments = 4;

        internal static Bitmap Grab(Rectangle region)
        {
            var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);
            return bmp;
        }

        /// <summary>抓帧到复用缓冲。dst 尺寸须与 region 一致。</summary>
        internal static void GrabInto(Bitmap dst, Rectangle region)
        {
            using (var g = Graphics.FromImage(dst))
                g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size);
        }

        /// <summary>每行 Segments 个分段各自的颜色加权均值（0..1020 刻度）。</summary>
        internal static double[][] RowSignatures(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var sig = new double[h][];
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                int stepX = Math.Max(1, w / 512);
                int segW = w / Segments;
                unsafe
                {
                    byte* baseP = (byte*)data.Scan0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* rowP = baseP + (long)y * stride;
                        var row = new double[Segments];
                        for (int s = 0; s < Segments; s++)
                        {
                            long sum = 0;
                            int cnt = 0;
                            int xEnd = (s == Segments - 1) ? w : (s + 1) * segW;
                            for (int x = s * segW; x < xEnd; x += stepX)
                            {
                                byte* px = rowP + x * 3;
                                sum += px[0] + (px[1] << 1) + px[2]; // weight green
                                cnt++;
                            }
                            row[s] = cnt > 0 ? (double)sum / cnt : 0;
                        }
                        sig[y] = row;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return sig;
        }

        /// <summary>两行签名的平均绝对差（跨分段）。</summary>
        internal static double RowDiff(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 999;
            double d = 0;
            for (int s = 0; s < a.Length; s++) d += Math.Abs(a[s] - b[s]);
            return d / a.Length;
        }

        /// <summary>两帧是否基本一致（画面静止）：抽样行平均差异低于阈值。</summary>
        internal static bool IsStatic(double[][] a, double[][] b)
        {
            int h = Math.Min(a.Length, b.Length);
            if (h == 0) return true;
            int step = Math.Max(1, h / 200);
            double diff = 0;
            int n = 0;
            for (int i = 0; i < h; i += step) { diff += RowDiff(a[i], b[i]); n++; }
            return n > 0 && diff / n < 6;
        }

        /// <summary>行匹配列区内的字节和——候选行 O(1) 快速预检。</summary>
        internal static unsafe uint SumRow(byte* row, int bytes)
        {
            uint s = 0;
            for (int i = 0; i < bytes; i++) s += row[i];
            return s;
        }

        /// <summary>行在匹配列区内是否近似纯色。</summary>
        internal static unsafe bool IsUniformRow(byte* row, int bytes)
        {
            byte v0 = row[0];
            for (int i = 4; i < bytes; i += 4)
                if (row[i] != v0) return false;
            return true;
        }
    }

    /// <summary>
    /// 拼接决策引擎（无 UI，可离线测试）。核心仍是 ShareX 式的"画布锚定逐行匹配"。
    ///
    /// 相对早期版本的关键修正：
    /// · 【去掉不可逆裁尾】早期用首帧"从未变化行带"一次性裁剪画布（_seeded）。一旦把
    ///   "没滚动 / 被遮挡 / 是任务栏"的底部误判成粘性页脚，画布就被永久砍掉一段，再也
    ///   无法锚定；导出时又按掩码再裁一次，导致成图比选区还短（线上 diag 的元凶）。
    ///   现在画布只在拼接中自然增长，粘性带仅由底部忽略带吸收，导出时裁且只裁一次。
    /// · 【冷启动兜底】从未成功锚定过时用行签名互相关估计位移并强拼，避免"首拍失配 =
    ///   永久死局"（bestGuess 也依赖 lastMatchIndex，diag 中 stitchCount=0 正是此态）。
    /// · 【容差降级】高 DPI 缩放 / 平滑滚动产生亚像素重采样差异，字节精确匹配先天不成立；
    ///   精确失败后降级为有界容差匹配，保证这类页面仍能推进。
    /// · 【不中断】失配只做标记，是否收图交给会话层决定。
    /// </summary>
    internal sealed class StitchEngine : IDisposable
    {
        /// <summary>一次 Process 的结果。</summary>
        public struct Decision
        {
            public bool Stitch;
            public int Shift;      // 向下拼接的行数
            public int UpShift;    // 回滚截断的行数
            public static Decision None() { return new Decision { Stitch = false }; }
        }

        private const double ChangedThreshold = 0.5;
        /// <summary>连续无匹配帧数上限（仅用于"疑似停滞"提示，不再触发收图）。</summary>
        private const int AmbiguousLimit = 25;
        /// <summary>容差匹配：整行平均绝对差上限（0..255 刻度）。</summary>
        private const int NearAvgBudget = 3;
        /// <summary>容差匹配：单字节差异超过该值即计入"显著差异"。</summary>
        private const int NearBigDiff = 24;
        /// <summary>容差锚定要求的最小连续命中行数。固定为 0（禁用容差锚定）：
        /// 近均匀内容上单行容差有百分之一量级的偶然命中率，即使要求 8 行连续命中，
        /// 在色块 / 留白页面（自相似内容）上仍会误锚定。亚像素重采样场景由
        /// VerifyShift 的逐字节容差校验负责 —— 那里有"分数最优 + 字节验证"双重约束，
        /// 远比"扫描所有候选位移取最长容差命中"可靠。</summary>
        private const int TolMinRun = 0;

        private readonly int _w, _frameH;
        private readonly int _x0, _x0B, _bytes;
        private readonly int _baseIgnore;
        private readonly StitchCanvas _canvas;
        private readonly uint[] _sumC;
        private double[][] _refSig;
        private double[][] _prevSig;    // 上一拍的帧签名（判断"页面是否还在动"）
        private readonly bool[] _changed;
        private int _lastMatchIndex = -1;
        private int _lastIgnore;
        /// <summary>画布"内容末行"（不含贴底覆盖层）对应 _refSig 的行号。-1 表示未知，
        /// 此时禁用位移估计。位移关系 _refSig[i+d] ≈ curSig[i] 只在内容区内成立。</summary>
        private int _refContentBottom = -1;
        private int _ambiguous;
        private bool _tolerant;
        private bool _stalled;
        private int _stuckBeats;     // 页面已停住却连续对不上的拍数（卡死判定）
        private int _resyncCount;    // 卡死重同步次数（诊断用）
        private int _dumpSeq;

        /// <summary>每拍轨迹（环形），诊断转储时输出 —— 用于离线判断"到底卡在哪一步"。</summary>
        private readonly string[] _trace = new string[64];
        private int _traceLen;

        /// <summary>记录一拍的结果摘要（会话层每拍调用一次）。</summary>
        public void Trace(string s)
        {
            _trace[_traceLen % _trace.Length] = s;
            _traceLen++;
        }

        /// <summary>当前已拼接高度（px）。</summary>
        public int Height { get { return _canvas.Height; } }
        /// <summary>成功拼接次数。</summary>
        public int StitchCount { get; private set; }
        /// <summary>长时间持续失配（瞬态，重新精确锚定后自动消除）。</summary>
        public bool Skipped { get { return _stalled; } }
        /// <summary>曾用过容差匹配 / 位移估计（像素可能略有偏差，但内容连续）。</summary>
        public bool Approximate { get { return _tolerant; } }
        /// <summary>当前帧画面在动但与画布失配（未重锁）。</summary>
        public bool ActiveMismatch { get; private set; }
        /// <summary>会话是否曾经成功锚定过。</summary>
        public bool HasAnchor { get { return _lastMatchIndex >= 0; } }
        /// <summary>卡死重同步次数（>0 表示长图中间有接缝，多半是滚太快造成的）。</summary>
        public int ResyncCount { get { return _resyncCount; } }

        public StitchEngine(Bitmap firstFrame)
        {
            _w = firstFrame.Width;
            _frameH = firstFrame.Height;
            _x0 = Math.Min(Math.Max(50, _w / 20), _w / 3);
            if (_w - _x0 * 2 < 32) _x0 = 0;
            _x0B = _x0 * 3;
            _bytes = (_w - _x0 * 2) * 3;
            _baseIgnore = Math.Max(50, _frameH / 10);

            _canvas = new StitchCanvas(firstFrame);
            _sumC = new uint[ScrollingCapture.MaxHeight];
            _refSig = ScrollingCapture.RowSignatures(firstFrame);
            _changed = new bool[_frameH];
            _refContentBottom = -1;   // 首帧的贴底覆盖层要等掩码算出来才知道
            UpdateCanvasSums(0, _canvas.Height);
        }

        /// <summary>处理一帧。frame 只被读取，调用方随后可安全复用 / 释放。</summary>
        public Decision Process(Bitmap frame)
        {
            return Process(frame, ScrollingCapture.RowSignatures(frame));
        }

        /// <summary>处理一帧（签名由调用方预计算，稳定判定与拼接共用一次计算）。</summary>
        public Decision Process(Bitmap frame, double[][] curSig)
        {
            if (frame.Width != _w || frame.Height != _frameH) return Decision.None();
            ActiveMismatch = false;

            // 页面是否还在动（相对上一拍）。手动模式下用户连续滚动会产生成片失配帧，
            // 那是正常操作；只有"页面已经停住却仍然对不上"才是真的丢了内容。
            double[][] prevSig = _prevSig;
            _prevSig = curSig;

            // 掩码只用于"限制匹配范围"与"导出时裁掉贴底覆盖层"，绝不再裁剪画布。
            for (int y = 0; y < _frameH; y++)
            {
                if (!_changed[y] && ScrollingCapture.RowDiff(_refSig[y], curSig[y]) > ChangedThreshold)
                    _changed[y] = true;
            }
            int top, bottom;
            GetDynamicRange(out top, out bottom);

            // 首帧：画布 = 整个首帧，其"内容末行"由掩码给出的贴底覆盖层位置决定
            if (_refContentBottom < 0)
                _refContentBottom = (bottom < _frameH) ? bottom - 1 : _frameH - 1;

            int matchCount, matchIndex, ignore, rollback;
            bool tailEq;
            // ① 字节精确匹配；② 失败后降级为有界容差匹配（高 DPI / 平滑滚动）
            // 容差匹配要求更长的连续命中（minRun=8）：单行容差在近均匀行（大片底色）
            // 上有百分之一量级的偶然命中率，run≥2 会频繁误匹配，run≥8 基本不可能。
            Scan(frame, true, top, bottom, 2, out matchCount, out matchIndex, out ignore, out tailEq, out rollback);
            if (matchCount < 2 && TolMinRun > 0)
                Scan(frame, false, top, bottom, TolMinRun, out matchCount, out matchIndex, out ignore, out tailEq, out rollback);

            if (matchCount >= 2 && matchIndex >= top)
            {
                int s = (_frameH - 1) - matchIndex;
                if (s > ignore)
                {
                    if (tailEq)
                    {
                        // 待拼行与画布中对应行完全一致 → 内容已在画布中（回滚 / 防重复虚增）
                        _canvas.Truncate(s);
                        // 注意：这里不能把 _refSig 换成 curSig —— 画布末行对应的仍是
                        // 上一帧的内容，换成当前帧会让位移估计的前提失效（线上故障：
                        // 一次回滚之后估计器永远拼错）
                        _ambiguous = 0;
                        _stalled = false;
                        _stuckBeats = 0;
                        _refContentBottom = matchIndex;
                        var du = Decision.None(); du.UpShift = s; return du;
                    }
                    _canvas.Truncate(ignore);
                    _canvas.AppendSlice(frame, matchIndex + 1, s);
                    UpdateCanvasSums(_canvas.Height - s, _canvas.Height);
                    _lastMatchIndex = matchIndex;
                    _lastIgnore = ignore;
                    _refContentBottom = bottom - 1;
                    _stalled = false;
                    _stuckBeats = 0;
                    _ambiguous = 0;
                    _refSig = curSig;
                    StitchCount++;
                    var d = Decision.None(); d.Stitch = true; d.Shift = s - ignore; return d;
                }
            }

            if (rollback >= 3)
            {
                _canvas.Truncate(rollback);
                // 同上：保留旧的 _refSig，画布末行仍对应它的第 frameH-1-rollback 行
                _ambiguous = 0;
                _stalled = false;
                _stuckBeats = 0;
                _refContentBottom = _frameH - 1 - rollback;
                var du = Decision.None(); du.UpShift = rollback; return du;
            }

            if (ScrollingCapture.IsStatic(_refSig, curSig))
            {
                _ambiguous = 0;
                return Decision.None();
            }

            // 位移估计兜底：锚定失败但画布末行与参考帧末行对齐时，
            // 用行签名互相关求出位移 d 并拼入当前帧底部 d 行。
            // 这比 bestGuess（沿用上次锚点、每次失配只能用一次）更可靠：
            // 粘性页头把可锚定区压缩到 d ≤ anchor-top 时，大步长会持续失配，
            // bestGuess 只能救一次就停摆，而位移估计每次都能自校正。
            if (_refContentBottom >= top)
            {
                var db = TryEstimateShift(frame, curSig, top, bottom);
                if (db.Stitch) return db;
            }

            // bestGuess（沿用上次锚点强拼）已移除：它会产生"可能缝一截"的垃圾拼接，
            // 是"滚动过快已跳过"误报的唯一来源。正在运动的帧本来就不该拼 —— 交给
            // 下一拍静止后的精确匹配即可。
            ActiveMismatch = true;

            // 只有"页面已停住却仍对不上"才算真丢内容。用户连续滚动时画面每拍都在变，
            // 此时失配是常态，绝不能计为停滞（否则必然误报"滚动过快"）。
            bool pageMoving = prevSig != null &&
                (prevSig.Length != curSig.Length || !ScrollingCapture.IsStatic(prevSig, curSig));
            if (pageMoving) { _stuckBeats = 0; return Decision.None(); }

            _ambiguous++;
            _stuckBeats++;
            if (_ambiguous >= AmbiguousLimit)
            {
                _stalled = true;
                _ambiguous = 0;
            }

            // 卡死重同步：页面停住却彻底对不上，最常见的原因是用户一次滚过了一屏多，
            // 画布末行早已滚出视口 —— 此时画布与画面零重叠，任何匹配算法都无解
            // （ShareX 到这里是直接失败退出）。把当前这一屏整段接上，画布末尾就回到
            // 当前视口，后续滚动又能正常匹配。代价是中间空一段，但总比彻底卡死好。
            if (_stuckBeats >= 30)
            {
                _stuckBeats = 0;
                var dr = Resync(frame, curSig, top, bottom);
                if (dr.Stitch) return dr;
            }
            return Decision.None();
        }

        /// <summary>把当前帧的内容区整段接为画布新的一段（卡死恢复用）。</summary>
        private Decision Resync(Bitmap frame, double[][] curSig, int top, int bottom)
        {
            int len = bottom - top;
            if (len < _frameH / 2) return Decision.None();

            _canvas.AppendSlice(frame, top, len);
            UpdateCanvasSums(_canvas.Height - len, _canvas.Height);
            _refContentBottom = bottom - 1;
            _lastMatchIndex = bottom - 1;
            _lastIgnore = 0;
            _tolerant = true;
            _stuckBeats = 0;
            _ambiguous = 0;
            _resyncCount++;
            _refSig = curSig;
            StitchCount++;
            var d = Decision.None(); d.Stitch = true; d.Shift = len; return d;
        }

        /// <summary>动态行范围 [top, bottom)（排除粘性页头 / 页脚）。样本不足时回退全帧。</summary>
        private void GetDynamicRange(out int top, out int bottom)
        {
            int first = -1, last = -1;
            for (int y = 0; y < _frameH; y++)
            {
                if (_changed[y]) { if (first < 0) first = y; last = y; }
            }
            if (first < 0 || last - first < _frameH / 4) { top = 0; bottom = _frameH; return; }
            top = first;
            bottom = last + 1;
        }

        /// <summary>
        /// 位移估计：在 [8, frameH-baseIgnore] 内找使 _refSig[i+d] ≈ curSig[i] 的位移 d，
        /// 命中则把当前帧底部 d 行拼入画布。要求平均签名残差 &lt; 2 —— 随机内容下残差远
        /// 大于此，不会误判。仅在 _refAligned（画布末行 = 参考帧末行）时调用。
        /// </summary>
        private Decision TryEstimateShift(Bitmap frame, double[][] curSig, int top, int bottom)
        {
            if (_refContentBottom < top || _refContentBottom >= _frameH) return Decision.None();
            if (_refSig.Length != _frameH || curSig.Length != _frameH) return Decision.None();

            // 只在"会随滚动移动"的内容区 [top, bottom) 内求相关，排除粘性页头 / 页脚 ——
            // 固定不动的覆盖层会把相关性彻底污染
            int maxD = Math.Min(_frameH - Math.Max(8, _baseIgnore), bottom - top - 8);
            maxD = Math.Min(maxD, _refContentBottom - top - 8);
            if (maxD < 8) return Decision.None();

            // 贴底覆盖层行数：只有画布确实以"本帧的贴底覆盖层"结尾时才裁它
            // （回滚之后画布末尾未必是覆盖层，此时不能按 tail 裁）
            int tail = (_refContentBottom == bottom - 1) ? _frameH - bottom : 0;
            if (tail < 0 || tail >= _frameH / 2) tail = 0;

            // 阶段一：粗筛（4 段行签名，O(1) 每行）→ 保留分数最好的若干个候选
            const int CandN = 4;
            var candD = new int[CandN];
            var candS = new double[CandN];
            int candCount = 0;
            for (int d = 8; d <= maxD; d++)
            {
                int n = bottom - top - d;
                if (n < 8) break;
                int step = Math.Max(1, n / 64);
                double sum = 0;
                int cnt = 0;
                for (int i = top; i + d < bottom; i += step)
                {
                    sum += ScrollingCapture.RowDiff(_refSig[i + d], curSig[i]);
                    cnt++;
                }
                if (cnt < 4) continue;
                double score = sum / cnt;
                if (score > 2.0) continue;
                int p = candCount < CandN ? candCount++ : CandN - 1;
                while (p > 0 && candS[p - 1] > score)
                {
                    candS[p] = candS[p - 1]; candD[p] = candD[p - 1]; p--;
                }
                if (p < CandN) { candS[p] = score; candD[p] = d; }
            }

            // 阶段二：逐字节验证。4 段均值签名太粗，在大片留白 / 块状海报这类自相似
            // 内容上会把错误位移判成最优；只有真正按字节对上的候选才被采信。
            int bestD = -1;
            for (int c = 0; c < candCount; c++)
            {
                if (VerifyShift(frame, candD[c], top, bottom, tail))
                {
                    bestD = candD[c];
                    break;
                }
            }
            if (bestD < 8) return Decision.None();

            // 画布内容末行对应参考帧第 _refContentBottom 行，在新帧中位于
            // _refContentBottom - d。其下方直到 bottom-1 都是新增内容。
            int start = _refContentBottom - bestD + 1;
            if (start < top) start = top;
            int contentLen = bottom - start;
            if (contentLen <= 0) return Decision.None();   // 没有真正的新内容

            _canvas.Truncate(tail);
            _canvas.AppendSlice(frame, start, contentLen + tail);
            UpdateCanvasSums(_canvas.Height - contentLen - tail, _canvas.Height);
            _lastMatchIndex = start - 1;
            _lastIgnore = tail;
            _refContentBottom = bottom - 1;
            _tolerant = true;
            _ambiguous = 0;
            _stalled = false;
            _stuckBeats = 0;
            _refSig = curSig;
            StitchCount++;
            var d2 = Decision.None(); d2.Stitch = true; d2.Shift = contentLen; return d2;
        }

        /// <summary>位移候选的逐字节验证：画布内容末行应与当前帧第 (_refContentBottom-d) 行对齐。
        /// 命中率 ≥75% 才采信 —— 比 4 段签名均值强得多，能挡掉自相似内容上的误判。</summary>
        private unsafe bool VerifyShift(Bitmap frame, int d, int top, int bottom, int tail)
        {
            int canvasH = _canvas.Height;
            int avail = Math.Min(bottom - top - d, canvasH - tail);
            avail = Math.Min(avail, _refContentBottom - d - top + 1);
            if (avail < 4) return false;

            long rb = _canvas.RowBytes;
            byte* baseC = _canvas.RowPtr(0);
            var bd = frame.LockBits(new Rectangle(0, 0, _w, _frameH),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* baseF = (byte*)bd.Scan0;
                int strideF = bd.Stride;
                int want = Math.Min(24, avail);
                int step = Math.Max(1, avail / want);
                int hit = 0, total = 0;
                for (int k = 0; k < avail; k += step)
                {
                    int cr = canvasH - 1 - tail - k;
                    int fr = _refContentBottom - d - k;
                    if (cr < 0 || fr < top) break;
                    total++;
                    if (RowsNear(baseC + (long)cr * rb + _x0B,
                                 baseF + (long)fr * strideF + _x0B, _bytes)) hit++;
                }
                return total >= 4 && hit * 4 >= total * 3;
            }
            finally { frame.UnlockBits(bd); }
        }

        /// <summary>
        /// 匹配主流程：① 底部忽略带 ② 锚定匹配 ③ 回滚验证 ④ 深回滚。
        /// exact=true 逐字节精确；false 为有界容差（应对亚像素重采样）。
        /// </summary>
        private unsafe void Scan(Bitmap frame, bool exact, int top, int bottom, int minRun,
            out int matchCount, out int matchIndex, out int ignore, out bool tailEq,
            out int rollback)
        {
            matchCount = 0; matchIndex = -1; ignore = 0; tailEq = false; rollback = 0;
            int span = bottom - top;
            if (span < 8) return;
            int limit = Math.Max(8, span / 2);

            int canvasH = _canvas.Height;
            long rb = _canvas.RowBytes;
            byte* baseC = _canvas.RowPtr(0);

            // ① 底部忽略带
            ignore = Math.Min(_baseIgnore, canvasH / 2);
            int ignoreMax = Math.Min(Math.Min(_frameH / 3, canvasH - 2), span - 4);
            if (ignoreMax > ignore)
            {
                BitmapData bdf = frame.LockBits(
                    new Rectangle(0, _frameH - 1 - ignoreMax, _w, ignoreMax + 1),
                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte* pf = (byte*)bdf.Scan0 + (long)ignoreMax * bdf.Stride;
                    for (int i = 0; i <= ignoreMax - ignore; i++)
                    {
                        // 底部忽略带始终逐字节精确（与 ShareX 的 memcmp 一致）：
                        // 用容差会让"几乎纯色"的页脚与任意行误判为同一行，忽略带被撑大
                        byte* pc = baseC + (long)(canvasH - 1 - i) * rb + _x0B;
                        byte* pfp = pf - (long)i * bdf.Stride + _x0B;
                        if (!RowsEqual(pc, pfp, _bytes)) { ignore += i; break; }
                    }
                }
                finally { frame.UnlockBits(bdf); }
                if (ignore < _lastIgnore) ignore = Math.Min(_lastIgnore, ignoreMax);
            }
            if (ignore >= span - 4) return;

            BitmapData bdF = frame.LockBits(new Rectangle(0, 0, _w, _frameH),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* baseF = (byte*)bdF.Scan0;
                int strideF = bdF.Stride;

                var sumF = new uint[_frameH];
                for (int r = 0; r < _frameH; r++)
                    sumF[r] = ScrollingCapture.SumRow(baseF + (long)r * strideF + _x0B, _bytes);

                // ② 锚定匹配（G = canvasH-1-ignore；失败则以 G-1 重试）
                for (int trimAt = 0; trimAt <= 1 && matchCount < 2; trimAt++)
                {
                    int anchor = canvasH - 1 - ignore - trimAt;
                    if (anchor < 0) break;
                    for (int fy = bottom - 1; fy >= top; fy--)
                    {
                        int run = 0;
                        for (int k = 0; k < limit; k++)
                        {
                            int fr = fy - k, cg = anchor - k;
                            if (fr < top || cg < 0) break;
                            if (exact && sumF[fr] != _sumC[cg]) break;
                            byte* pf2 = baseF + (long)fr * strideF + _x0B;
                            byte* pc2 = baseC + (long)cg * rb + _x0B;
                            if (exact ? !RowsEqual(pf2, pc2, _bytes) : !RowsNear(pf2, pc2, _bytes)) break;
                            run++;
                        }
                        if (run >= minRun && run > matchCount) { matchCount = run; matchIndex = fy; }
                    }
                }

                if (matchCount >= 2 && matchIndex >= top)
                {
                    // ③ 回滚验证
                    int s = (_frameH - 1) - matchIndex;
                    if (s > ignore && canvasH - ignore - s >= 0)
                    {
                        tailEq = true;
                        for (int k = 0; k < s; k++)
                        {
                            int fr = matchIndex + 1 + k, cg = canvasH - ignore - s + k;
                            if (sumF[fr] != _sumC[cg] ||
                                !RowsEqual(baseF + (long)fr * strideF + _x0B,
                                           baseC + (long)cg * rb + _x0B, _bytes))
                            { tailEq = false; break; }
                        }
                        return;
                    }
                }

                // ④ 深回滚
                int maxUp = Math.Min(canvasH - 1, _frameH * 3 + 64);
                if (maxUp < 3) return;
                for (int u = 3; u <= maxUp; u++)
                {
                    int cg = canvasH - 1 - u;
                    if (cg - span + 1 < 0) break;
                    if (sumF[bottom - 1] != _sumC[cg]) continue;
                    if (!RowsEqual(baseF + (long)(bottom - 1) * strideF + _x0B,
                                   baseC + (long)cg * rb + _x0B, _bytes)) continue;
                    int run = 1;
                    for (int k = 1; k < span; k++)
                    {
                        int c2 = cg - k, f2 = bottom - 1 - k;
                        if (c2 < 0 || f2 < top) break;
                        if (sumF[f2] != _sumC[c2] ||
                            !RowsEqual(baseF + (long)f2 * strideF + _x0B,
                                       baseC + (long)c2 * rb + _x0B, _bytes)) break;
                        run++;
                    }
                    if (run >= span - 2) { rollback = u; return; }
                }
            }
            finally { frame.UnlockBits(bdF); }
        }

        private static unsafe bool RowsEqual(byte* a, byte* b, int bytes)
        {
            for (int i = 0; i < bytes; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>有界容差行比较：整行平均绝对差 ≤ NearAvgBudget，且显著差异字节 ≤ 1/64。</summary>
        private static unsafe bool RowsNear(byte* a, byte* b, int bytes)
        {
            int budget = bytes * NearAvgBudget;
            int bigMax = bytes >> 6;
            int big = 0;
            for (int i = 0; i < bytes; i++)
            {
                int d = a[i] - b[i];
                if (d < 0) d = -d;
                if (d == 0) continue;
                budget -= d;
                if (budget < 0) return false;
                if (d > NearBigDiff && ++big > bigMax) return false;
            }
            return true;
        }

        private unsafe void UpdateCanvasSums(int from, int to)
        {
            if (to <= from || to > _sumC.Length) return;
            long rb = _canvas.RowBytes;
            byte* bp = _canvas.RowPtr(0);
            for (int r = from; r < to; r++)
                _sumC[r] = ScrollingCapture.SumRow(bp + (long)r * rb + _x0B, _bytes);
        }

        /// <summary>诊断转储到 %TEMP%\SnapCutDiag。只导出画布末尾若干行，
        /// 避免为诊断再分配一张全尺寸位图。</summary>
        public void DumpDiag(Bitmap lastFrame)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "SnapCutDiag");
                Directory.CreateDirectory(dir);
                int seq = System.Threading.Interlocked.Increment(ref _dumpSeq);
                string pfx = Path.Combine(dir, "diag" + seq);

                int take = Math.Min(_canvas.Height, 3000);
                using (var tail = _canvas.ExportRange(Math.Max(0, _canvas.Height - take), take))
                {
                    double scale = Math.Min(1.0, 4000.0 / Math.Max(tail.Width, tail.Height));
                    if (scale < 1.0)
                    {
                        using (var small = new Bitmap(
                            Math.Max(1, (int)(tail.Width * scale)),
                            Math.Max(1, (int)(tail.Height * scale))))
                        {
                            using (var g = Graphics.FromImage(small))
                                g.DrawImage(tail, 0, 0, small.Width, small.Height);
                            small.Save(pfx + "_canvas.jpg", ImageFormat.Jpeg);
                        }
                    }
                    else tail.Save(pfx + "_canvas.jpg", ImageFormat.Jpeg);
                }
                if (lastFrame != null) lastFrame.Save(pfx + "_frame.png", ImageFormat.Png);
                int top, bottom;
                GetDynamicRange(out top, out bottom);
                File.WriteAllLines(pfx + ".txt", new[]
                {
                    "time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "canvasH=" + _canvas.Height,
                    "frameW=" + _w, "frameH=" + _frameH, "x0=" + _x0,
                    "baseIgnore=" + _baseIgnore,
                    "stitchCount=" + StitchCount,
                    "lastMatchIndex=" + _lastMatchIndex, "lastIgnore=" + _lastIgnore,
                    "guessed=removed", "stalled=" + _stalled,
                    "tolerant=" + _tolerant, "hasAnchor=" + HasAnchor,
                    "resyncCount=" + _resyncCount, "stuckBeats=" + _stuckBeats,
                    "ambiguous=" + _ambiguous, "maskTop=" + top, "maskBottom=" + bottom,
                    "skipped=" + Skipped
                });
                // 每拍轨迹：+=拼接(+行数) ^=回滚 x=失配 .=静止无变化
                var lines = new List<string>();
                int startIdx = _traceLen > _trace.Length ? _traceLen - _trace.Length : 0;
                for (int i = startIdx; i < _traceLen; i++)
                    lines.Add((i + 1).ToString() + ": " + _trace[i % _trace.Length]);
                File.AppendAllLines(pfx + ".txt", lines);
            }
            catch { /* 诊断失败不影响主流程 */ }
        }

        /// <summary>导出实际高度的拼接结果。尾部粘性带（帧底重供的页脚等贴底行）按
        /// 掩码裁除 —— 只裁一次，不再与画布裁切重复。</summary>
        public Bitmap Export()
        {
            int trim = 0;
            int top, bottom;
            GetDynamicRange(out top, out bottom);
            bool maskValid = bottom < _frameH && !_changed[Math.Min(_frameH - 1, bottom)];
            if (maskValid) trim = _frameH - bottom;
            int h = Math.Max(0, _canvas.Height - trim);
            return _canvas.Export(h);
        }

        public void Dispose()
        {
            _canvas.Dispose();
        }
    }

    /// <summary>
    /// 增长式拼接画布。使用**非托管连续缓冲**而不是 GDI+ 位图：
    /// · 早期用一张 GDI+ 大位图 + Ensure() 倍增（新旧两张并存）+ 导出再复制一张，
    ///   1920 宽拼到 30000 高时峰值可达 500MB+，且 GDI+ 对超大位图一律抛
    ///   OutOfMemory —— 被上层 catch 吞掉后就变成"静默截断的短图"。
    /// · 现在容量用 ReAllocHGlobal 原地增长（无双份峰值），导出时分带写出，
    ///   全程不需要任何全尺寸 GDI+ 位图。
    /// </summary>
    internal sealed class StitchCanvas : IDisposable
    {
        private IntPtr _buf;
        private int _cap;
        private int _height;
        private readonly int _w;
        private readonly int _rowBytes;

        // 画布自持内存拷贝，不依赖 NativeMethods（引擎保持可在无 WinForms 引用的测试里编译）
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void MoveMemory(IntPtr dest, IntPtr src, IntPtr count);

        public int Height { get { return _height; } }
        internal int RowBytes { get { return _rowBytes; } }
        /// <summary>已用字节数（诊断用）。</summary>
        internal long UsedBytes { get { return (long)_height * _rowBytes; } }

        internal unsafe byte* RowPtr(int row)
        {
            return (byte*)_buf + (long)row * _rowBytes;
        }

        public StitchCanvas(Bitmap first)
        {
            _w = first.Width;
            _rowBytes = _w * 3;
            _cap = Math.Min(Math.Max(first.Height * 6, 6000), ScrollingCapture.MaxHeight);
            _buf = Marshal.AllocHGlobal(new IntPtr((long)_cap * _rowBytes));
            CopyIn(first, 0, 0, first.Height);
            _height = first.Height;
        }

        /// <summary>把 frame 的 [srcY, srcY+len) 追加到画布尾部（frame 由调用方释放）。</summary>
        public void AppendSlice(Bitmap frame, int srcY, int len)
        {
            if (len <= 0) return;
            if (_height + len > ScrollingCapture.MaxHeight)
                len = ScrollingCapture.MaxHeight - _height;
            if (len <= 0) return;
            // 拷贝量按源位图实际可用行裁剪（越界时不再越界读），但逻辑高度仍按 len 增长，
            // 与早期 GDI+ DrawImage 的行为保持一致（超限即截断到 MaxHeight）。
            if (srcY < 0) srcY = 0;
            int copy = len;
            if (srcY + copy > frame.Height) copy = frame.Height - srcY;
            if (copy > 0) CopyIn(frame, srcY, _height, copy);
            _height += len;
        }

        private unsafe void CopyIn(Bitmap src, int srcY, int dstRow, int rows)
        {
            if (rows <= 0) return;
            Ensure(dstRow + rows);
            var bd = src.LockBits(new Rectangle(0, srcY, _w, rows),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* s = (byte*)bd.Scan0;
                int ss = bd.Stride;
                for (int r = 0; r < rows; r++)
                {
                    MoveMemory(
                        (IntPtr)RowPtr(dstRow + r),
                        (IntPtr)(s + (long)r * ss),
                        new IntPtr(_rowBytes));
                }
            }
            finally { src.UnlockBits(bd); }
        }

        private void Ensure(int need)
        {
            if (need <= _cap) return;
            int cap = (int)Math.Min(Math.Max((long)_cap * 2, need), ScrollingCapture.MaxHeight);
            if (cap <= _cap) return;
            try
            {
                _buf = Marshal.ReAllocHGlobal(_buf, new IntPtr((long)cap * _rowBytes));
                _cap = cap;
            }
            catch (OutOfMemoryException)
            {
                // 退一步：只增长到刚好够用；再不行就抛出，让上层给出明确提示而不是静默截断
                if (need > _cap)
                {
                    _buf = Marshal.ReAllocHGlobal(_buf, new IntPtr((long)need * _rowBytes));
                    _cap = need;
                }
                else throw;
            }
        }

        /// <summary>回滚：把画布逻辑高度回退 remove 行（后续拼接会覆写这些行）。</summary>
        public int Truncate(int remove)
        {
            remove = Math.Min(remove, _height);
            _height -= remove;
            return remove;
        }

        /// <summary>导出 [0, rows) 的实际高度位图（新位图，调用方负责释放）。</summary>
        public Bitmap Export(int rows)
        {
            return ExportRange(0, Math.Min(rows, _height));
        }

        /// <summary>导出画布 [startRow, startRow+rows) 区间。分带写出，内存占用恒定，
        /// 避免为超大长图创建全尺寸中间位图。</summary>
        public Bitmap ExportRange(int startRow, int rows)
        {
            startRow = Math.Max(0, Math.Min(startRow, _height));
            rows = Math.Max(0, Math.Min(rows, _height - startRow));
            var outB = new Bitmap(_w, Math.Max(1, rows), PixelFormat.Format24bppRgb);
            if (rows == 0) return outB;

            int band = Math.Min(Math.Max(1, 2000000 / Math.Max(1, _w)), rows);
            var tmp = new Bitmap(_w, band, PixelFormat.Format24bppRgb);
            try
            {
                using (var g = Graphics.FromImage(outB))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
                    int y = 0;
                    while (y < rows)
                    {
                        int n = Math.Min(band, rows - y);
                        Fill(tmp, startRow + y, n);
                        g.DrawImage(tmp, new Rectangle(0, y, _w, n),
                            new Rectangle(0, 0, _w, n), GraphicsUnit.Pixel);
                        y += n;
                    }
                }
            }
            finally { tmp.Dispose(); }
            return outB;
        }

        /// <summary>把画布 [startRow, startRow+rows) 拷进 tmp 的顶部 rows 行。</summary>
        private unsafe void Fill(Bitmap tmp, int startRow, int rows)
        {
            var bd = tmp.LockBits(new Rectangle(0, 0, _w, rows),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* d = (byte*)bd.Scan0;
                int ds = bd.Stride;
                for (int r = 0; r < rows; r++)
                {
                    MoveMemory(
                        (IntPtr)(d + (long)r * ds),
                        (IntPtr)RowPtr(startRow + r),
                        new IntPtr(_rowBytes));
                }
            }
            finally { tmp.UnlockBits(bd); }
        }

        public void Dispose()
        {
            if (_buf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buf);
                _buf = IntPtr.Zero;
            }
        }
    }
}
