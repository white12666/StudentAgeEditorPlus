using System;
using System.Collections.Generic;
using Config;

namespace StudentAgeTypeset.Latex
{
    /// <summary>
    /// 原版编辑器内存对象的归属判定：这个配置对象是「本 mod 自有」还是
    /// 「从全局表借来的本体条目」。
    ///
    /// 为什么必须分清（D6-1 / D5-4）：原版事件编辑器会把引用到的本体对话/选项
    /// **按引用**塞进自己的容器——ModEvtEditView.cs:216-218 `talkCfgs.Add(value)` 与
    /// :228 `optionCfgs.Add(value2.id, value2)`，其中 value/value2 直接来自
    /// Cfg.TalkCfgMap / Cfg.OptionCfgMap。任何对它们的就地改写都会篡改本局全局配置
    /// （本体台词当场变样），而且原版保存是整表回写，改过的内置条目会被写进 mod JSON
    /// 分发给每个订阅者。
    ///
    /// 引用相等是最精确的判据：自有条目是从 mod JSON 现场反序列化出来的新对象
    /// （ModEvtEditView.cs:176-180 DeserializeJsonToCfgMap），永远不可能与全局表是
    /// 同一实例；借来的条目必然是同一实例。
    /// </summary>
    internal static class LatexVanillaOwnership
    {
        internal static bool IsBorrowed(TalkCfg talk)
        {
            if (talk == null) return false;
            try
            {
                Dictionary<int, TalkCfg> map = Cfg.TalkCfgMap;
                TalkCfg global;
                return map != null && map.TryGetValue(talk.id, out global)
                       && ReferenceEquals(global, talk);
            }
            catch (Exception e)
            {
                // 判不出来时保守当成借来的：宁可少改一处，也不冒污染全局表的风险。
                TypesetLog.Warn?.Invoke(
                    "[Latex.Ownership] 对话归属判定失败，保守按借来处理：" + e.Message);
                return true;
            }
        }

        internal static bool IsBorrowed(OptionCfg option)
        {
            if (option == null) return false;
            try
            {
                Dictionary<int, OptionCfg> map = Cfg.OptionCfgMap;
                OptionCfg global;
                return map != null && map.TryGetValue(option.id, out global)
                       && ReferenceEquals(global, option);
            }
            catch (Exception e)
            {
                TypesetLog.Warn?.Invoke(
                    "[Latex.Ownership] 选项归属判定失败，保守按借来处理：" + e.Message);
                return true;
            }
        }
    }
}
