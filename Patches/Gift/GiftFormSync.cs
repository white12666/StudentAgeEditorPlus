using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StudentAgeEditorPlus.Patches
{
    // 纯数据逻辑：先解析并构造新列表，全部成功后调用方才提交，失败不改原记录。
    internal static class GiftFormSync
    {
        internal static List<int> ParseIds(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<int>();
            var result = new List<int>();
            foreach (string token in text.Replace('，', ',').Split(','))
            {
                if (!int.TryParse(token.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int id))
                    throw new FormatException("请填写整数编号，用逗号分隔，不要留下空项。");
                result.Add(id);
            }
            return result;
        }

        internal static List<List<int>> ParseTalks(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<List<int>>();
            // 保留空组的位置，不能将后一个 NPC 的入口向前挪。
            return text.Replace('；', ';').Split(';').Select(ParseIds).ToList();
        }

        internal static void RemapNpcs(string text, List<int> oldNpcs,
            List<List<int>> oldTalks, List<int> oldTypes,
            out List<int> npcs, out List<List<int>> talks, out List<int> types)
        {
            npcs = ParseIds(text);
            if (npcs.Any(id => id <= 0) || npcs.Distinct().Count() != npcs.Count)
                throw new FormatException("NPC ID 必须是互不重复的正整数。");
            talks = new List<List<int>>();
            types = new List<int>();
            if (npcs.Count == 0) return; // 明确清空 NPC，同时清除其全部绑定。
            oldNpcs = oldNpcs ?? new List<int>();
            if (npcs.SequenceEqual(oldNpcs))
            {
                talks = oldTalks?.Select(row => row == null ? null : new List<int>(row)).ToList();
                types = oldTypes == null ? null : new List<int>(oldTypes);
                return;
            }
            GiftBindingDraft original = GiftBindingDraft.Create(oldNpcs, oldTalks, oldTypes);
            if (original.ShapeError != null || oldNpcs.Any(id => id <= 0) ||
                oldNpcs.Distinct().Count() != oldNpcs.Count)
                throw new FormatException("原绑定存在重复 NPC 或多余列表，无法确定对应关系。请先修正，或清空 NPC 后重新绑定。");
            var byNpc = original.Rows.ToDictionary(row => row.NpcId);
            foreach (int id in npcs)
            {
                if (byNpc.TryGetValue(id, out GiftBindingRow row))
                {
                    talks.Add(new List<int>(row.TalkIds));
                    types.Add(row.ItemMode);
                }
                else
                {
                    talks.Add(new List<int>());
                    types.Add(0);
                }
            }
        }
    }
}
