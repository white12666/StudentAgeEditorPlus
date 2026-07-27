using System;
using System.Collections.Generic;
using Config;

namespace StudentAgeTypeset.Latex
{
    /// <summary>
    /// 原版编辑器保底烘焙的一次扫描结果（设计 §6.2）。
    /// TalkSources 是烘焙前的作者原文，写回边车 Source；PlainTalks 是本轮确认
    /// 已无行内公式的对话，调用方据此清掉对不上的过期边车条目。
    /// </summary>
    internal sealed class LatexBakeSweepResult
    {
        /// <summary>内容真的被改写过的对话数（未闭合 $ 等原样保留的不计）。</summary>
        internal int ChangedTalks;
        internal int ChangedOptions;
        /// <summary>因借自全局表而改用克隆体的条目数（D6-1，全局配置未被污染）。</summary>
        internal int ClonedBorrowed;
        /// <summary>含行内公式的对话：id → 烘焙前原文。id≤0 的草稿条目不收（原版保存会丢弃）。</summary>
        internal readonly Dictionary<int, string> TalkSources =
            new Dictionary<int, string>();
        /// <summary>不含行内公式的对话：id → 最终内容（用于判定边车旧源码是否过期）。</summary>
        internal readonly Dictionary<int, string> PlainTalks =
            new Dictionary<int, string>();
        /// <summary>面向作者的中文告警，已按文本去重并限量。</summary>
        internal readonly List<string> Warnings = new List<string>();

        /// <summary>
        /// 阻断项：$ 配对错位到无法安全烘焙的条目（带来源前缀）。非空时本轮
        /// 一个字都没改，调用方必须整体拦下本次保存。
        /// </summary>
        internal readonly List<string> Blocked = new List<string>();

        internal bool IsBlocked
        {
            get { return Blocked.Count > 0; }
        }

        internal bool Changed
        {
            get { return ChangedTalks > 0 || ChangedOptions > 0; }
        }
    }

    /// <summary>
    /// 原版 ModEvtEditView 保存前的“保底烘焙”纯逻辑（设计 §6.2）。
    ///
    /// 分工硬约束：本类只依赖 LatexInlineTranspiler 与游戏配置类型，零 Unity /
    /// BepInEx / 磁盘依赖——视图内存的就地替换时机、边车写入与 Toast 全部留在
    /// 补丁层（LatexVanillaSaveBake），测试工程才能直接编译这一份源码。
    ///
    /// 选项（OptionCfg）与对话同为 TMP 显示（Cell_CommonOptionItemUI.txtex_content
    /// 是 TextMeshProUGUI，NewTalkView.cs:198 用的就是它），所以选项也走完整
    /// Bake，不需要 BakeUnicodeOnly 降级；但边车只有 Talks 槽位（设计 §1），
    /// 选项烘焙是单向的，作者之后看到的是烘焙文本。
    /// </summary>
    internal static class LatexOrdinarySaveBaker
    {
        /// <summary>单次保存最多向作者展示多少条不同告警，避免刷屏。</summary>
        internal const int MaxWarnings = 12;

        private struct PendingBake<T> where T : class
        {
            internal T Cfg;
            internal string Source;
            internal LatexBakeResult Baked;
            /// <summary>
            /// 非 null 说明这是借来的全局对象：不能就地改，写回时把容器槽位换成这个克隆体
            /// （D6-1）。由调用方的 cloneBorrowed* 接缝产出。
            /// </summary>
            internal T Clone;
            /// <summary>换克隆体时在容器里的位置（对话是列表下标，选项是字典键）。</summary>
            internal int Slot;
        }

        /// <summary>
        /// 就地烘焙视图内存里的 content。传入的就是编辑器自己的容器，
        /// 因此改完之后原版 OnClickSave 写盘的必然是烘焙产物。
        /// 单条解析失败不会中断整轮（Bake 本身不抛，见 A1 约定）。
        ///
        /// 两段式：先整轮试烘焙，只有全部条目都安全才真的写回内存。任何一条带
        /// BlockingReason 就一个字都不改并把理由放进 Blocked——就地烘焙一旦部分
        /// 生效而保存又被拦下，作者的 $ 原文就在没有保存的情况下被换成了烘焙文本。
        ///
        /// **借来的对象必须换成克隆体再改（D6-1）**：原版编辑器把引用到的本体
        /// 对话/选项直接从全局表按引用塞进自己的容器
        /// （ModEvtEditView.cs:216-218 talkCfgs.Add(value)、:228 optionCfgs.Add(value2.id, value2)，
        /// value 来自 Cfg.TalkCfgMap / Cfg.OptionCfgMap）。就地改写它们等于篡改本局
        /// 全局配置——本体台词当场变样，且原版保存会把改过的内置条目整表写进
        /// mod JSON 分发给订阅者。改成「克隆后替换容器里的槽位」：全局表不受影响，
        /// mod JSON 仍拿到烘焙产物（作者本来就是在覆盖那一条）。
        /// 归属判定与克隆合成一个接缝：cloneBorrowedTalk/Option 只在对象确实借自全局表时
        /// 返回克隆体，否则返回 null（本类因此不必依赖会话类或 Unity，测试可自带假克隆）。
        /// 两个委托都为空时视为全部自有——纯逻辑测试的默认。
        /// </summary>
        internal static LatexBakeSweepResult BakeInPlace(
            IList<TalkCfg> talks, IDictionary<int, OptionCfg> options,
            Func<TalkCfg, TalkCfg> cloneBorrowedTalk = null,
            Func<OptionCfg, OptionCfg> cloneBorrowedOption = null)
        {
            var result = new LatexBakeSweepResult();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pendingTalks = new List<PendingBake<TalkCfg>>();
            var pendingOptions = new List<PendingBake<OptionCfg>>();

            if (talks != null)
            {
                for (int index = 0; index < talks.Count; index++)
                {
                    TalkCfg talk = talks[index];
                    if (talk == null) continue;
                    string source = talk.content;
                    if (!LatexInlineTranspiler.ContainsLatex(source))
                    {
                        if (talk.id > 0) result.PlainTalks[talk.id] = source;
                        continue;
                    }
                    LatexBakeResult baked = LatexInlineTranspiler.Bake(source);
                    Collect(result, seen, "对话 " + talk.id, baked.Warnings);
                    if (baked.HasBlockingIssue)
                    {
                        result.Blocked.Add("对话 " + talk.id + "：" + baked.BlockingReason);
                        continue;
                    }
                    pendingTalks.Add(new PendingBake<TalkCfg>
                    {
                        Cfg = talk, Source = source, Baked = baked,
                        Clone = cloneBorrowedTalk != null ? cloneBorrowedTalk(talk) : null,
                        Slot = index,
                    });
                }
            }

            if (options != null)
            {
                foreach (KeyValuePair<int, OptionCfg> pair in options)
                {
                    OptionCfg option = pair.Value;
                    if (option == null) continue;
                    string source = option.content;
                    if (!LatexInlineTranspiler.ContainsLatex(source)) continue;
                    LatexBakeResult baked = LatexInlineTranspiler.Bake(source);
                    Collect(result, seen, "选项 " + option.id, baked.Warnings);
                    if (baked.HasBlockingIssue)
                    {
                        result.Blocked.Add("选项 " + option.id + "：" + baked.BlockingReason);
                        continue;
                    }
                    pendingOptions.Add(new PendingBake<OptionCfg>
                    {
                        Cfg = option, Source = source, Baked = baked,
                        Clone = cloneBorrowedOption != null
                            ? cloneBorrowedOption(option)
                            : null,
                        Slot = pair.Key,
                    });
                }
            }

            if (result.IsBlocked) return result;

            foreach (PendingBake<TalkCfg> pending in pendingTalks)
            {
                TalkCfg target = pending.Cfg;
                if (pending.Clone != null)
                {
                    target = pending.Clone;
                    talks[pending.Slot] = target;
                    result.ClonedBorrowed++;
                }
                if (pending.Baked.Baked != null) target.content = pending.Baked.Baked;
                if (pending.Baked.Changed) result.ChangedTalks++;
                if (target.id > 0) result.TalkSources[target.id] = pending.Source;
            }
            foreach (PendingBake<OptionCfg> pending in pendingOptions)
            {
                OptionCfg target = pending.Cfg;
                if (pending.Clone != null)
                {
                    target = pending.Clone;
                    options[pending.Slot] = target;
                    result.ClonedBorrowed++;
                }
                if (pending.Baked.Baked != null) target.content = pending.Baked.Baked;
                if (pending.Baked.Changed) result.ChangedOptions++;
            }

            return result;
        }

        /// <summary>
        /// 把一轮扫描结果落进边车内存态：含公式的对话登记原文，不含公式的对话
        /// 清掉已经对不上的旧源码（留着只会让下次选中回填出错误的文本）。
        /// 写盘与否由调用方决定（store.Save 是 best-effort，失败不影响保存）。
        /// </summary>
        internal static void ApplyToSidecar(
            LatexSourceStore store, int eventId, LatexBakeSweepResult sweep)
        {
            if (store == null || sweep == null || eventId <= 0) return;
            foreach (KeyValuePair<int, string> pair in sweep.TalkSources)
                store.SetTalkSource(eventId, pair.Key, pair.Value);
            foreach (KeyValuePair<int, string> pair in sweep.PlainTalks)
            {
                string existing = store.GetTalkSource(eventId, pair.Key);
                if (existing == null) continue;
                if (IsSidecarSourceStale(existing, pair.Value))
                    store.SetTalkSource(eventId, pair.Key, null);
            }
        }

        /// <summary>
        /// 边车里的旧源码是否已经对不上当前内容：烘焙是幂等的，所以“源码烘出来
        /// 就是当前内容”才算仍然有效；不一致说明烘焙文本被外部改过，旧源码作废。
        /// </summary>
        internal static bool IsSidecarSourceStale(string source, string content)
        {
            if (string.IsNullOrEmpty(source)) return false;
            string baked = LatexInlineTranspiler.Bake(source).Baked;
            return !string.Equals(baked, content, StringComparison.Ordinal);
        }

        private static void Collect(
            LatexBakeSweepResult result, HashSet<string> seen,
            string owner, IReadOnlyList<string> warnings)
        {
            if (warnings == null) return;
            for (int i = 0; i < warnings.Count; i++)
            {
                if (result.Warnings.Count >= MaxWarnings) return;
                string text = owner + "：" + warnings[i];
                if (!seen.Add(text)) continue;
                result.Warnings.Add(text);
            }
        }
    }
}
