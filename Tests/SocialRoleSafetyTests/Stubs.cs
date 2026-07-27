using System.Collections.Generic;

namespace StudentAgeEditorPlus
{
    // AtomicFilePairTransaction 在提交点之后的 best-effort 清理里经 Plugin.Log 记警告。
    // 测试宿主不引用 BepInEx，只需一个能编译的空替身；这些测试不断言日志内容。
    internal static class Plugin
    {
        internal static readonly StubLogSource Log = new StubLogSource();
    }

    internal sealed class StubLogSource
    {
        internal void LogWarning(object data) { }
    }
}

namespace Config
{
    internal sealed class PersonCfg
    {
        public List<int> birthday = new();
        public List<int> init = new();
        public string note;
    }
}
