using System;
using Config;

namespace StudentAgeEditorPlus.Patches
{
    internal delegate bool StoryGraphLatexPreviewRenderer(
        string source, out string rendered, out string warning);

    internal delegate bool StoryGraphLatexSessionPreviewRenderer(
        object session, int nodeId, bool isOption, string source,
        out string rendered, out string warning);

    internal delegate bool StoryGraphLatexAuthorPreviewBinder(
        object session, TalkCfg previewTalk);

    /// <summary>
    /// 剧情图会话的可选 LaTeX 探针（单向运行时组合接缝）。
    ///
    /// StudentAgeLatex 插件在场时经反射把兼容委托挂上来，获得：
    ///   1) 进入编辑模式时换回作者的 $ 源码（磁盘上是烘焙成品，草稿内存用原文）；
    ///   2) 保存前把行内烘焙 + 块级公式物化落在会话克隆草稿的 JSON 快照上，
    ///      以 out 参数交还本侧的原子保存事务（committedOnDisk 纪律不变）；
    ///   3) 本侧明确告知保存成功/失败，插件只在成功后提交边车与 GC，并在两种
    ///      结果下都把会话正文恢复为作者源码。
    ///   4) 基础页正文编辑时，把源码与会话/节点身份交给插件做实时显示转译；
    ///      成功预览可形成会话候选，仍只在 CompleteSave(true) 后发布。本侧只负责
    ///      防抖、布局和 TMP 呈现，仍不引用 Typeset。
    ///
    /// 本侧对 LaTeX 零引用：委托缺席时进入/保存路径与探针引入前逐字节一致。
    /// 委托是**单一注册点**——重复赋值等价于替换，天然幂等；插件卸载（BepInEx
    /// 不支持热卸载）不会发生，故不提供反注册。
    ///
    /// 为什么不用接口/事件：本程序集与 StudentAgeLatex 双向都不许有编译期引用，
    /// 唯一能跨边界的只有 mscorlib 与 Assembly-CSharp 的类型。委托签名因此只用
    /// TalkCfg/OptionCfg（Assembly-CSharp）与 BCL 值类型。
    /// </summary>
    internal static class StoryGraphLatexProbe
    {
        /// <summary>
        /// 进入编辑模式、会话构造**之前**调用：把磁盘烘焙文本换回作者的 $ 源码。
        /// 入参是已进入会话构造流程的两份**深拷贝**草稿（本侧已隔离原编辑器内存
        /// 与全局配置），探针可就地改写 content。modRoot/eventId 用于定位边车条目。
        /// </summary>
        internal static Action<string, int, System.Collections.Generic.List<TalkCfg>,
            System.Collections.Generic.Dictionary<int, OptionCfg>> SwapbackSource;

        /// <summary>
        /// 会话保存**写盘之前**调用：在草稿上完成行内烘焙与块级公式 screenEffect
        /// 编排。返回 null/空串表示放行；非空串为阻断原因。公式 PNG/CGCfg 可按
        /// 内容寻址提前准备，但私有/公开源码边车与 GC 必须等 CompleteSave(true)。
        /// </summary>
        internal static Func<StoryGraphEditSession, string> PrepareForSave;

        /// <summary>
        /// PrepareForSave 成功之后必调一次。committed=true 表示 TalkCfg/OptionCfg
        /// 原子事务已提交；false 表示后续视图同步或写盘失败。实现不得反向抛异常。
        /// </summary>
        internal static Action<StoryGraphEditSession, bool> CompleteSave;

        /// <summary>
        /// 剧情图基础页实时预览。返回 true 表示源码含 LaTeX、应显示预览区；
        /// rendered 可为空（例如公式尚未闭合），warning 为面向作者的校验提示。
        /// </summary>
        internal static StoryGraphLatexPreviewRenderer RenderPreview;

        /// <summary>
        /// 带会话与节点身份的新版实时预览。StudentAgeLatex 可据此把成功预览登记为
        /// 当前会话的编译候选；候选仍须等 CompleteSave(true) 才能发布。
        /// 保留上面的旧委托用于旧插件兼容和无状态显示探测。
        /// </summary>
        internal static StoryGraphLatexSessionPreviewRenderer RenderPreviewForSession;

        /// <summary>
        /// “预览本句”构造播放器 TalkCfg 副本时调用。实现只可把当前会话中已成功
        /// 实时渲染且源码仍匹配的候选绑定到该副本，不得提前发布玩家侧快照。
        /// </summary>
        internal static StoryGraphLatexAuthorPreviewBinder BindAuthorPreview;
    }
}
