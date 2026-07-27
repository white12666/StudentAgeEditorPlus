using System;

namespace StudentAgeTypeset
{
    /// <summary>
    /// 排版类库的日志接缝。库自身不依赖 BepInEx 日志类型（net8.0 测试工程
    /// 直接源码编译本文件即可，无需任何桩）；宿主插件（EditorPlus）在 Awake
    /// 里注入委托后，库内日志才真正落到 BepInEx 控制台/文件。
    ///
    /// 调用形态 <c>TypesetLog.Warn?.Invoke(...)</c> 与拆分前
    /// <c>Plugin.Log?.LogWarning(...)</c> 空安全语义逐点等价：
    /// 未注入 = 静默无操作，绝不抛异常。
    /// </summary>
    internal static class TypesetLog
    {
        // 显式 = null：测试工程直接源码编译本文件且不注入委托，
        // 显式初始化让 CS0649（字段从未赋值）不在测试构建里刷屏。
        internal static Action<string> Info = null;
        internal static Action<string> Warn = null;
        internal static Action<string> Error = null;
    }
}
