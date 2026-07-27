using SkiaSharp;

namespace StudentAgeTypeset.Latex
{
    /// <summary>块级公式卡片的排版模式。</summary>
    internal enum FormulaCardMode
    {
        /// <summary>纯公式：整条式子居中放大撑满安全区（MathPainter）。</summary>
        Math = 0,

        /// <summary>
        /// 图文混排：整段话里用 $…$ 夹公式，左上对齐、自动折行（TextPainter）。
        /// 文字与公式都由 LaTeX 排版引擎出图，因此中文以外的部分也是正宗
        /// LaTeX 字形——行内烘焙受限于游戏字体（只能 &lt;i&gt; 合成斜体），
        /// 要「那个味道」就得走这条。
        /// </summary>
        Text = 1,
    }

    /// <summary>
    /// 块级公式卡片的样式常量（集中一处，便于统一调整）。
    /// </summary>
    internal static class FormulaStyle
    {
        // 参与 PNG 内容寻址 hash（sha256(latex+样式版本+渲染环境)）：任何影响出图像素的
        // 常量变更都必须递增此版本号，否则旧 PNG 会被误判为最新而不重渲。
        internal const string StyleVersion = "card-v2";

        /// <summary>
        /// 渲染环境指纹，同样参与内容寻址（LTX-D1-02）。由 FormulaRenderer 在惰性初始化
        /// 时写入（如 "cjk:simhei" / "cjk:none"）：中文字体缺失时 \text{中文} 会渲成豆腐块，
        /// 若环境不参与 hash，作者后来装上字体也永远命中旧 PNG（"已存在即幂等跳过"），
        /// 豆腐图会照旧随工坊分发。放在这里而不是 FormulaRenderer，是为了让
        /// FormulaAssetService 计算 hash 时不必触碰渲染器（测试注入假渲染器时不加载
        /// SkiaSharp 原生链路）。
        /// </summary>
        internal static string EnvironmentTag = string.Empty;

        // icon_cg_mini 是固定 1920x1080、preserveAspect=false 的拉伸框，
        // 只有这个尺寸不会被拉变形。
        internal const int CanvasWidth = 1920;
        internal const int CanvasHeight = 1080;

        // MiniCG 四周透出场景背景，透明底墨色不可控——必须不透明底。
        internal static readonly SKColor Background = new SKColor(0xFA, 0xF7, 0xF0);
        internal static readonly SKColor Ink = new SKColor(0x1F, 0x1F, 0x1F);

        // 安全区：MiniCG 下缘约 300px 压在字幕条后面，故下边距远大于上边距。
        internal const float MarginLeftRatio = 0.08f;
        internal const float MarginRightRatio = 0.08f;
        internal const float MarginTopRatio = 0.10f;
        internal const float MarginBottomRatio = 0.32f;

        // FontSize 与像素严格线性（spike 实证），上限防止极短公式被放大到失衡。
        internal const float MaxFontSize = 220f;
        internal const float MeasureFontSize = 100f;

        // 编辑器实时预览小图：透明底 + 四周少量留白。
        internal const int PreviewPadding = 12;
        internal static readonly SKColor PreviewInk = new SKColor(0x1F, 0x1F, 0x1F);

        // ---- 图文模式（TextPainter）----
        // 纯公式模式把整条式子居中放大撑满安全区；图文模式是「一段话里夹公式」，
        // 按固定字号左上对齐 + 自动折行更像讲义，放大到撑满反而失衡。
        internal const float TextModeFontSize = 64f;
        /// <summary>图文模式字号自适应下限：内容太长时逐级缩小，仍放不下就截断在安全区内。</summary>
        internal const float TextModeMinFontSize = 28f;
    }
}
