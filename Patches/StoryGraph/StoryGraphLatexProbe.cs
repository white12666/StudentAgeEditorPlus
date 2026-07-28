using System;
using Config;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 剧情图会话的可选 LaTeX 探针（单向运行时组合接缝）。
    ///
    /// StudentAgeLatex 插件在场时经反射把两个委托挂上来，获得：
    ///   1) 进入编辑模式时换回作者的 $ 源码（磁盘上是烘焙成品，草稿内存用原文）；
    ///   2) 保存前把行内烘焙 + 块级公式物化落在会话克隆草稿的 JSON 快照上，
    ///      以 out 参数交还本侧的原子保存事务（committedOnDisk 纪律不变）。
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
        /// 编排。返回 null/空串表示放行；非空串为阻断原因（保存整体拦下，草稿与
        /// 磁盘均未改）。写盘/事务由本侧负责，探针绝不直接落盘。
        /// </summary>
        internal static Func<StoryGraphEditSession, string> PrepareForSave;
    }
}
