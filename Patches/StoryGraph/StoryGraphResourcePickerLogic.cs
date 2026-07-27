using System;
using System.Collections.Generic;
using System.Linq;

namespace StudentAgeEditorPlus.Patches
{
    internal enum StoryGraphResourceKind
    {
        Background,
        Audio,
        Cg,
        MiniGame,
        Event,
    }

    /// <summary>
    /// 剧情图资源查阅器的纯数据项。与 Unity 控件解耦，搜索、分页和窄屏
    /// 布局可以在不启动游戏的情况下做回归测试。
    /// </summary>
    internal sealed class StoryGraphResourceEntry
    {
        internal StoryGraphResourceEntry(int id, string name)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? "未命名资源" : name.Trim();
        }

        internal int Id { get; }
        internal string Name { get; }
    }

    internal sealed class StoryGraphResourcePage
    {
        internal StoryGraphResourcePage(
            IList<StoryGraphResourceEntry> items,
            int page, int pageCount, int totalCount)
        {
            Items = items ?? new List<StoryGraphResourceEntry>();
            Page = page;
            PageCount = pageCount;
            TotalCount = totalCount;
        }

        internal IList<StoryGraphResourceEntry> Items { get; }
        internal int Page { get; }
        internal int PageCount { get; }
        internal int TotalCount { get; }
    }

    internal struct StoryGraphResourcePickerPlacement
    {
        internal float Width;
        internal float Height;
        internal float RightInset;
        internal float TopInset;
        internal bool BesideInspector;
    }

    internal static class StoryGraphResourcePickerLogic
    {
        internal const int DefaultPageSize = 8;
        internal const float PreferredWidth = 520f;
        internal const float PreferredHeight = 600f;
        internal const float MinimumBesideWidth = 340f;
        internal const float Margin = 12f;

        internal static StoryGraphResourcePage Query(
            IEnumerable<StoryGraphResourceEntry> source,
            string searchText,
            int requestedPage,
            int pageSize = DefaultPageSize,
            bool descending = false)
        {
            if (pageSize <= 0) pageSize = DefaultPageSize;
            string[] tokens = (searchText ?? string.Empty)
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<StoryGraphResourceEntry> distinct = (source
                    ?? Enumerable.Empty<StoryGraphResourceEntry>())
                .Where(item => item != null && Matches(item, tokens))
                // 配置合并时当前 Mod 会覆盖同 ID；即使调用方意外传入重复项，
                // 查阅器也只能显示一个稳定选择，避免分页中出现两个同 ID。
                .GroupBy(item => item.Id)
                .Select(group => group.Last());
            // 非正数是“停止/沿用/无”的操作项，不属于资源 ID 排序，始终
            // 固定在最前；其余项目才按作者选择的 ID 正序或倒序排列。
            List<StoryGraphResourceEntry> filtered = descending
                ? distinct
                    .OrderBy(item => item.Id <= 0 ? 0 : 1)
                    .ThenBy(item => item.Id <= 0 ? item.Id : 0)
                    .ThenByDescending(item => item.Id > 0 ? item.Id : 0)
                    .ToList()
                : distinct
                    .OrderBy(item => item.Id <= 0 ? 0 : 1)
                    .ThenBy(item => item.Id)
                    .ToList();

            int pageCount = Math.Max(1,
                (filtered.Count + pageSize - 1) / pageSize);
            int page = Math.Max(1, Math.Min(requestedPage, pageCount));
            List<StoryGraphResourceEntry> items = filtered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();
            return new StoryGraphResourcePage(
                items, page, pageCount, filtered.Count);
        }

        /// <summary>
        /// 原版 ModSelectBgView 只把编号不小于 200000 的 BgCfg 放进背景
        /// 选择器。较小编号属于其它图片/内部资源，不能因为共用 BgCfgMap
        /// 就混进剧情背景列表。
        /// </summary>
        internal static bool IsBackgroundBrowsable(int id)
        {
            return id >= 200000;
        }

        /// <summary>
        /// 与原版 ModSelectAudioView 保持一致：只列出有地址的背景音乐。
        /// 音效、语音等其它 AudioCfg 仍可用于名称解释，但不进入查阅器。
        /// </summary>
        internal static bool IsAudioBrowsable(int type, string url)
        {
            return type == 1 && !string.IsNullOrWhiteSpace(url);
        }

        internal static List<StoryGraphResourceEntry> BuildMiniGameEntries(
            bool forTalk)
        {
            IEnumerable<int> ids = forTalk
                ? MiniGameUtil.TalkSupportedIds
                : MiniGameUtil.OptionSupportedIds;
            return ids.OrderBy(id => id).Select(id =>
            {
                string name = MiniGameUtil.GameName(id);
                if (MiniGameUtil.NeedParamIds.Contains(id))
                    name += "（选择后需继续设置）";
                return new StoryGraphResourceEntry(id, name);
            }).ToList();
        }

        private static bool Matches(
            StoryGraphResourceEntry item, IEnumerable<string> tokens)
        {
            string haystack = item.Id + " " + item.Name;
            foreach (string token in tokens)
            {
                if (haystack.IndexOf(token,
                        StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 宽屏时把资源册放在右栏左侧；空间不足时改为画布内覆盖式弹层。
        /// 两种布局都保证 left/right/top/bottom 不越出逻辑画布。
        /// </summary>
        internal static StoryGraphResourcePickerPlacement CalculatePlacement(
            float canvasWidth,
            float canvasHeight,
            float inspectorWidth,
            float toolbarHeight,
            float statusbarHeight)
        {
            canvasWidth = Math.Max(0f, canvasWidth);
            canvasHeight = Math.Max(0f, canvasHeight);
            float besideRight = Math.Max(0f, inspectorWidth) + Margin;
            float besideAvailable = canvasWidth - besideRight - Margin;
            bool beside = besideAvailable >= MinimumBesideWidth;
            float rightInset = beside ? besideRight : Margin;
            float availableWidth = Math.Max(0f,
                canvasWidth - rightInset - Margin);
            float topInset = Math.Max(Margin, toolbarHeight + Margin);
            float availableHeight = Math.Max(0f,
                canvasHeight - topInset
                - Math.Max(Margin, statusbarHeight + Margin));

            return new StoryGraphResourcePickerPlacement
            {
                Width = Math.Min(PreferredWidth, availableWidth),
                Height = Math.Min(PreferredHeight, availableHeight),
                RightInset = rightInset,
                TopInset = topInset,
                BesideInspector = beside,
            };
        }

        /// <summary>
        /// 图内 Toast 显示时长：4 秒起步，按文字量增加，上限 8 秒。
        /// 中文提示阅读速度按每字约 0.05 秒补偿。
        /// </summary>
        internal static float ToastDurationSeconds(int messageLength)
        {
            const float baseSeconds = 4f;
            const float perChar = 0.05f;
            const float maxSeconds = 8f;
            if (messageLength <= 0) return baseSeconds;
            float seconds = baseSeconds + perChar * messageLength;
            return seconds > maxSeconds ? maxSeconds : seconds;
        }
    }
}
