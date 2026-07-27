using System;
using System.IO;
using System.Runtime.InteropServices;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using Typography.OpenFont;

namespace StudentAgeTypeset.Latex
{
    /// <summary>
    /// 块级 LaTeX 公式渲染服务（CSharpMath.SkiaSharp）。
    /// 无 Unity / BepInEx 依赖——net472 测试工程直接源码引用同一份文件；
    /// 全部 API 同步执行、异常内部捕获转 error 字符串，可在 Unity 主线程直接调用。
    /// </summary>
    internal static class FormulaRenderer
    {
        // libSkiaSharp.dll 位于 BepInEx 插件目录，不在 Windows 加载器默认搜索路径上，
        // 必须先按绝对路径 LoadLibrary，否则首个 SkiaSharp P/Invoke 即 DllNotFound。
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        private static readonly object InitLock = new object();
        private static bool _initialized;
        private static bool _available;
        private static string _unavailableReason;
        // Typography 类型内嵌于 CSharpMath.Rendering.dll，纯托管对象（非 Unity 对象），
        // 静态持有仅为避免每次渲染重复解析系统字体文件。
        private static Typeface[] _cjkTypefaces;
        private static string _cjkWarning;

        /// <summary>原生链路是否可用；false 时块级公式功能应整体禁用（行内路线不受影响）。</summary>
        internal static bool IsAvailable
        {
            get { EnsureInitialized(); return _available; }
        }

        /// <summary>不可用原因（IsAvailable 为 true 时为 null）。</summary>
        internal static string UnavailableReason
        {
            get { EnsureInitialized(); return _unavailableReason; }
        }

        /// <summary>中文字体注入警告（成功注入时为 null）；仅影响 \text 中文，不阻断渲染。</summary>
        internal static string CjkWarning
        {
            get { EnsureInitialized(); return _cjkWarning; }
        }

        /// <summary>
        /// 渲染完整 MiniCG 卡片：1920x1080 不透明底，公式在上部安全区内缩放居中。
        /// 失败返回 false 并给出 error，绝不抛异常。
        /// </summary>
        internal static bool TryRenderCard(string latex, out byte[] png, out string error)
        {
            return TryRenderCard(latex, FormulaCardMode.Math, out png, out error);
        }

        internal static bool TryRenderCard(
            string latex, FormulaCardMode mode, out byte[] png, out string error)
        {
            png = null;
            error = null;
            if (string.IsNullOrWhiteSpace(latex))
            {
                error = "公式为空。";
                return false;
            }
            EnsureInitialized();
            if (!_available)
            {
                error = _unavailableReason;
                return false;
            }
            if (mode == FormulaCardMode.Text)
                return TryRenderTextCard(latex, out png, out error);
            try
            {
                MathPainter painter = CreatePainter(latex, FormulaStyle.MeasureFontSize, FormulaStyle.Ink);
                if (painter.ErrorMessage != null)
                {
                    error = painter.ErrorMessage;
                    return false;
                }
                System.Drawing.RectangleF measure = painter.Measure();
                if (!(measure.Width > 0f) || !(measure.Height > 0f))
                {
                    error = "公式测量结果为空。";
                    return false;
                }

                // FontSize 与像素严格线性（spike 实证），按安全区一次缩放即可撑满；
                // 上限防止极短公式被放大到失衡。
                float safeWidth = FormulaStyle.CanvasWidth
                    * (1f - FormulaStyle.MarginLeftRatio - FormulaStyle.MarginRightRatio);
                float safeHeight = FormulaStyle.CanvasHeight
                    * (1f - FormulaStyle.MarginTopRatio - FormulaStyle.MarginBottomRatio);
                float scale = Math.Min(safeWidth / measure.Width, safeHeight / measure.Height);
                float fontSize = Math.Min(FormulaStyle.MeasureFontSize * scale, FormulaStyle.MaxFontSize);
                if (Math.Abs(fontSize - FormulaStyle.MeasureFontSize) > 0.01f)
                {
                    painter.FontSize = fontSize;
                    measure = painter.Measure();
                }

                var info = new SKImageInfo(FormulaStyle.CanvasWidth, FormulaStyle.CanvasHeight,
                    SKColorType.Rgba8888, SKAlphaType.Premul);
                using (SKSurface surface = SKSurface.Create(info))
                {
                    if (surface == null)
                    {
                        error = "SKSurface.Create 失败。";
                        return false;
                    }
                    SKCanvas canvas = surface.Canvas;
                    canvas.Clear(FormulaStyle.Background);
                    // Measure(): X=0、Y=-Ascent、Height=Ascent+Descent；
                    // Draw(canvas, x, y) 的 (x, y) 是基线起点（画布左上角原点、y 向下）。
                    // 不用 DrawAsStream——它按 int 截断表面尺寸会裁边，且无法加底色与边距。
                    float ascent = -measure.Y;
                    float safeLeft = FormulaStyle.CanvasWidth * FormulaStyle.MarginLeftRatio;
                    float safeTop = FormulaStyle.CanvasHeight * FormulaStyle.MarginTopRatio;
                    float x = safeLeft + (safeWidth - measure.Width) / 2f;
                    float y = safeTop + (safeHeight - measure.Height) / 2f + ascent;
                    painter.Draw(canvas, x, y);
                    png = EncodePng(surface);
                }
                if (png == null || png.Length == 0)
                {
                    png = null;
                    error = "PNG 编码失败。";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                png = null;
                error = "渲染异常: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 图文模式卡片：整段话里用 $…$ 夹公式，左上对齐 + 按安全区宽度自动折行。
        /// 与纯公式模式的关键差别是**不放大撑满**——一段话被拉到满屏会失衡，
        /// 改为固定字号起步、放不下就逐级缩小到 TextModeMinFontSize 为止。
        /// </summary>
        private static bool TryRenderTextCard(string latex, out byte[] png, out string error)
        {
            png = null;
            error = null;
            try
            {
                float safeWidth = FormulaStyle.CanvasWidth
                    * (1f - FormulaStyle.MarginLeftRatio - FormulaStyle.MarginRightRatio);
                float safeHeight = FormulaStyle.CanvasHeight
                    * (1f - FormulaStyle.MarginTopRatio - FormulaStyle.MarginBottomRatio);

                var painter = new TextPainter
                {
                    LaTeX = latex,
                    FontSize = FormulaStyle.TextModeFontSize,
                    TextColor = FormulaStyle.Ink,
                };
                if (painter.ErrorMessage != null)
                {
                    error = painter.ErrorMessage;
                    return false;
                }
                Typeface[] faces = _cjkTypefaces;
                if (faces != null) painter.LocalTypefaces = faces;

                // 逐级缩小到能放进安全区；TextPainter 的 Measure 需要折行宽度。
                System.Drawing.RectangleF measure = painter.Measure(safeWidth);
                while (measure.Height > safeHeight
                       && painter.FontSize > FormulaStyle.TextModeMinFontSize)
                {
                    painter.FontSize = Math.Max(
                        FormulaStyle.TextModeMinFontSize, painter.FontSize - 4f);
                    measure = painter.Measure(safeWidth);
                }
                if (!(measure.Width > 0f) || !(measure.Height > 0f))
                {
                    error = "图文内容测量结果为空。";
                    return false;
                }

                var info = new SKImageInfo(FormulaStyle.CanvasWidth, FormulaStyle.CanvasHeight,
                    SKColorType.Rgba8888, SKAlphaType.Premul);
                using (SKSurface surface = SKSurface.Create(info))
                {
                    if (surface == null)
                    {
                        error = "SKSurface.Create 失败。";
                        return false;
                    }
                    SKCanvas canvas = surface.Canvas;
                    canvas.Clear(FormulaStyle.Background);
                    float safeLeft = FormulaStyle.CanvasWidth * FormulaStyle.MarginLeftRatio;
                    float safeTop = FormulaStyle.CanvasHeight * FormulaStyle.MarginTopRatio;
                    // 内容不足一屏时整体垂直居中，长文则从安全区顶端开始。
                    float y = measure.Height < safeHeight
                        ? safeTop + (safeHeight - measure.Height) / 2f
                        : safeTop;
                    // 第三参是 padding（类型在 CSharpMath 内部命名空间，用 default 交给
                    // 目标类型推断即可）；(safeLeft, y) 是内容左上角，不是基线。
                    painter.Draw(canvas,
                        CSharpMath.Rendering.FrontEnd.TextAlignment.TopLeft,
                        default, safeLeft, y);
                    png = EncodePng(surface);
                }
                if (png == null || png.Length == 0)
                {
                    png = null;
                    error = "PNG 编码失败。";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                png = null;
                error = "图文渲染异常: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 图文模式的预览小图：透明底、按与成品同比例的折行宽度排版，
        /// 使属性栏里看到的折行位置与保存出的 MiniCG 一致。
        /// </summary>
        private static bool TryRenderTextPreview(
            string latex, float fontSize, out byte[] png, out string error)
        {
            png = null;
            error = null;
            try
            {
                float size = fontSize;
                if (float.IsNaN(size) || size < 1f) size = 1f;
                if (size > FormulaStyle.MaxFontSize) size = FormulaStyle.MaxFontSize;
                // 折行宽度按「预览字号 : 成品字号」等比缩放成品安全区宽度，
                // 折行位置才与成品一致。
                float cardSafeWidth = FormulaStyle.CanvasWidth
                    * (1f - FormulaStyle.MarginLeftRatio - FormulaStyle.MarginRightRatio);
                float wrapWidth = cardSafeWidth * (size / FormulaStyle.TextModeFontSize);
                if (!(wrapWidth > 1f)) wrapWidth = cardSafeWidth;

                var painter = new TextPainter
                {
                    LaTeX = latex,
                    FontSize = size,
                    TextColor = FormulaStyle.PreviewInk,
                };
                if (painter.ErrorMessage != null)
                {
                    error = painter.ErrorMessage;
                    return false;
                }
                Typeface[] faces = _cjkTypefaces;
                if (faces != null) painter.LocalTypefaces = faces;

                System.Drawing.RectangleF measure = painter.Measure(wrapWidth);
                if (!(measure.Width > 0f) || !(measure.Height > 0f))
                {
                    error = "图文内容测量结果为空。";
                    return false;
                }
                int width = (int)Math.Ceiling(measure.Width) + FormulaStyle.PreviewPadding * 2;
                int height = (int)Math.Ceiling(measure.Height) + FormulaStyle.PreviewPadding * 2;
                var info = new SKImageInfo(
                    width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (SKSurface surface = SKSurface.Create(info))
                {
                    if (surface == null)
                    {
                        error = "SKSurface.Create 失败。";
                        return false;
                    }
                    surface.Canvas.Clear(SKColors.Transparent);
                    painter.Draw(surface.Canvas,
                        CSharpMath.Rendering.FrontEnd.TextAlignment.TopLeft,
                        default,
                        FormulaStyle.PreviewPadding, FormulaStyle.PreviewPadding);
                    png = EncodePng(surface);
                }
                if (png == null || png.Length == 0)
                {
                    png = null;
                    error = "PNG 编码失败。";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                png = null;
                error = "图文预览渲染异常: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 渲染编辑器实时预览小图：透明底、尺寸=测量结果+少量留白。
        /// 失败返回 false 并给出 error，绝不抛异常。
        /// </summary>
        internal static bool TryRenderPreview(string latex, float fontSize, out byte[] png, out string error)
        {
            return TryRenderPreview(
                latex, fontSize, FormulaCardMode.Math, out png, out error);
        }

        internal static bool TryRenderPreview(
            string latex, float fontSize, FormulaCardMode mode,
            out byte[] png, out string error)
        {
            png = null;
            error = null;
            if (string.IsNullOrWhiteSpace(latex))
            {
                error = "公式为空。";
                return false;
            }
            EnsureInitialized();
            if (!_available)
            {
                error = _unavailableReason;
                return false;
            }
            // 图文模式的预览必须也走 TextPainter：否则作者看到的排版与保存出的
            // 成品是两套引擎的结果（尤其折行位置完全不同）。
            if (mode == FormulaCardMode.Text)
                return TryRenderTextPreview(latex, fontSize, out png, out error);
            try
            {
                float size = fontSize;
                if (float.IsNaN(size) || size < 1f) size = 1f;
                if (size > FormulaStyle.MaxFontSize) size = FormulaStyle.MaxFontSize;
                MathPainter painter = CreatePainter(latex, size, FormulaStyle.PreviewInk);
                if (painter.ErrorMessage != null)
                {
                    error = painter.ErrorMessage;
                    return false;
                }
                System.Drawing.RectangleF measure = painter.Measure();
                if (!(measure.Width > 0f) || !(measure.Height > 0f))
                {
                    error = "公式测量结果为空。";
                    return false;
                }
                int width = (int)Math.Ceiling(measure.Width) + FormulaStyle.PreviewPadding * 2;
                int height = (int)Math.Ceiling(measure.Height) + FormulaStyle.PreviewPadding * 2;
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (SKSurface surface = SKSurface.Create(info))
                {
                    if (surface == null)
                    {
                        error = "SKSurface.Create 失败。";
                        return false;
                    }
                    SKCanvas canvas = surface.Canvas;
                    canvas.Clear(SKColors.Transparent);
                    painter.Draw(canvas, FormulaStyle.PreviewPadding,
                        FormulaStyle.PreviewPadding + (-measure.Y));
                    png = EncodePng(surface);
                }
                if (png == null || png.Length == 0)
                {
                    png = null;
                    error = "PNG 编码失败。";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                png = null;
                error = "渲染异常: " + exception.Message;
                return false;
            }
        }

        private static MathPainter CreatePainter(string latex, float fontSize, SKColor ink)
        {
            var painter = new MathPainter
            {
                LaTeX = latex,
                FontSize = fontSize,
                TextColor = ink,
            };
            Typeface[] faces = _cjkTypefaces;
            // LocalTypefaces 的 setter 会整体重建内部 Fonts 集合，只能整组赋值。
            if (faces != null) painter.LocalTypefaces = faces;
            return painter;
        }

        private static byte[] EncodePng(SKSurface surface)
        {
            using (SKImage image = surface.Snapshot())
            using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
            {
                return data == null ? null : data.ToArray();
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (InitLock)
            {
                if (_initialized) return;
                try
                {
                    InitializeCore();
                }
                catch (Exception exception)
                {
                    _available = false;
                    _unavailableReason = "块级公式初始化失败: " + exception.Message;
                }
                finally
                {
                    _initialized = true;
                }
            }
        }

        private static void InitializeCore()
        {
            string baseDir = null;
            try
            {
                string location = typeof(FormulaRenderer).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                    baseDir = Path.GetDirectoryName(location);
            }
            catch
            {
                // Location 不可用时走默认搜索路径探针。
            }
            string nativePath = baseDir == null ? null : Path.Combine(baseDir, "libSkiaSharp.dll");
            if (nativePath != null && File.Exists(nativePath))
            {
                if (LoadLibraryW(nativePath) == IntPtr.Zero)
                {
                    _available = false;
                    _unavailableReason = string.Format(
                        "libSkiaSharp.dll 加载失败 (Win32Error={0}): {1}",
                        Marshal.GetLastWin32Error(), nativePath);
                    return;
                }
            }
            // 文件不在本目录时仍可能经默认搜索路径解析（如测试宿主）——
            // 以一次真实 Skia 原生调用为准判定可用性，失败不抛。
            try
            {
                var info = new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (SKSurface probe = SKSurface.Create(info))
                {
                    if (probe == null)
                    {
                        _available = false;
                        _unavailableReason = "SkiaSharp 探针失败：SKSurface.Create 返回 null。";
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                _available = false;
                _unavailableReason = string.Format(
                    "SkiaSharp 原生调用失败 ({0}): {1}",
                    exception.GetType().Name, exception.Message);
                return;
            }
            _available = true;
            _unavailableReason = null;
            LoadCjkTypefaces();
        }

        private static void LoadCjkTypefaces()
        {
            // \text 中文依赖注入系统字体；失败仅警告（中文显示豆腐），纯数学式不受影响。
            // 结果写进 FormulaStyle.EnvironmentTag 参与 PNG 内容寻址（LTX-D1-02）：
            // 作者后来装上中文字体时，hash 变化才会让旧豆腐图被重渲替换。
            try
            {
                string fontsDir = GetWindowsFontsDir();
                string simhei = Path.Combine(fontsDir, "simhei.ttf");
                if (File.Exists(simhei))
                {
                    using (FileStream stream = File.OpenRead(simhei))
                    {
                        Typeface face = new OpenFontReader().Read(stream);
                        if (face != null)
                        {
                            _cjkTypefaces = new[] { face };
                            FormulaStyle.EnvironmentTag = "cjk:simhei";
                            return;
                        }
                    }
                }
                string msyh = Path.Combine(fontsDir, "msyh.ttc");
                if (File.Exists(msyh))
                {
                    Typeface face = ReadTtcFirstFace(msyh);
                    if (face != null)
                    {
                        _cjkTypefaces = new[] { face };
                        FormulaStyle.EnvironmentTag = "cjk:msyh";
                        return;
                    }
                }
                _cjkWarning = "未找到可用的 simhei.ttf / msyh.ttc，块级公式 \\text 中文不可用。";
                FormulaStyle.EnvironmentTag = "cjk:none";
            }
            catch (Exception exception)
            {
                _cjkWarning = "中文字体注入失败: " + exception.Message;
                FormulaStyle.EnvironmentTag = "cjk:error";
            }
        }

        private static Typeface ReadTtcFirstFace(string path)
        {
            // OpenFontReader 不认整个 .ttc（直接 Read 返回 null）——须自解析 ttcf 头
            // （大端：tag@0 / numFonts@8 / 首字体偏移@12）后按偏移读首个成员字体。
            using (FileStream stream = File.OpenRead(path))
            {
                var header = new byte[16];
                int total = 0;
                while (total < header.Length)
                {
                    int read = stream.Read(header, total, header.Length - total);
                    if (read <= 0) return null;
                    total += read;
                }
                if (ReadU32BigEndian(header, 0) != 0x74746366u) return null;
                uint firstOffset = ReadU32BigEndian(header, 12);
                if (firstOffset > int.MaxValue) return null;
                stream.Position = 0;
                return new OpenFontReader().Read(stream, (int)firstOffset);
            }
        }

        private static uint ReadU32BigEndian(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16
                | buffer[offset + 2] << 8 | buffer[offset + 3]);
        }

        private static string GetWindowsFontsDir()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows)) windows = @"C:\Windows";
            return Path.Combine(windows, "Fonts");
        }
    }
}
