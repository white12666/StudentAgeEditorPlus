using System;
using System.Collections.Generic;
using System.Linq;

namespace StudentAgeEditorPlus.Patches
{
    internal sealed class GiftBindingRow
    {
        internal int NpcId;
        internal int ItemMode;
        internal List<int> TalkIds;
        private int? _femaleBeforeSharing;

        internal bool Separate => TalkIds.Count >= 2;

        internal GiftBindingRow(int npcId, IEnumerable<int> talks, int itemMode)
        {
            NpcId = npcId;
            ItemMode = itemMode;
            TalkIds = talks == null ? new List<int>() : new List<int>(talks);
            _femaleBeforeSharing = TalkIds.Count > 1 ? TalkIds[1] : (int?)null;
        }

        internal int GetTalk(int slot) => slot < TalkIds.Count ? TalkIds[slot] : 0;

        internal void SetTalk(int slot, int id)
        {
            while (TalkIds.Count <= slot) TalkIds.Add(0);
            TalkIds[slot] = id;
            if (slot == 1) _femaleBeforeSharing = id;
        }

        internal void SetSeparate(bool separate)
        {
            if (TalkIds.Count > 2)
                throw new InvalidOperationException("旧数据超过两个入口，请先在高级 ID 字段修正。");
            if (separate == Separate) return;
            int first = GetTalk(0);
            if (separate)
            {
                TalkIds = new List<int> { first, _femaleBeforeSharing ?? first };
            }
            else
            {
                _femaleBeforeSharing = GetTalk(1);
                TalkIds = new List<int> { first };
            }
        }
    }

    // 不依赖 Unity；窗口编辑深拷贝，只有通过校验后才回填三个原生平行列表。
    internal sealed class GiftBindingDraft
    {
        internal readonly List<GiftBindingRow> Rows = new List<GiftBindingRow>();
        internal string ShapeError { get; private set; }

        internal static GiftBindingDraft Create(
            List<int> npcs, List<List<int>> talks, List<int> types)
        {
            var draft = new GiftBindingDraft();
            int count = npcs?.Count ?? 0;
            if ((talks?.Count ?? 0) > count || (types?.Count ?? 0) > count)
                draft.ShapeError = "对话或类型标记的组数超过 NPC 数量。为避免丢失旧数据，请取消并在高级 ID 字段修正。";
            for (int i = 0; i < count; i++)
                draft.Rows.Add(new GiftBindingRow(npcs[i],
                    talks != null && i < talks.Count ? talks[i] : null,
                    types != null && i < types.Count ? types[i] : 0));
            return draft;
        }

        internal string Validate(Func<int, bool> hasTalk)
        {
            if (ShapeError != null) return ShapeError;
            // 三个列表均为空表示停用该送礼规则；有残余组的情况仍由 ShapeError 拒绝。
            if (Rows.Count == 0) return null;
            var seen = new HashSet<int>();
            foreach (GiftBindingRow row in Rows)
            {
                string prefix = "NPC " + row.NpcId + "：";
                if (row.NpcId <= 0) return prefix + "NPC ID 必须大于 0。";
                if (!seen.Add(row.NpcId)) return prefix + "重复添加了同一位 NPC。";
                if (row.ItemMode != 0 && row.ItemMode != 1)
                    return prefix + "类型标记只支持 0（赠出后消失）或 1（保留）。";
                if (row.TalkIds.Count > 2)
                    return prefix + "最多支持男主、女主两个入口，不会依次播放多段剧情。请在高级 ID 字段修正。";
                if (row.TalkIds.Count == 0 || row.TalkIds.All(id => id == 0))
                    return prefix + "尚未绑定剧情，请选择至少一个有效入口。";
                for (int i = 0; i < row.TalkIds.Count; i++)
                {
                    int id = row.TalkIds[i];
                    // 双性别槽中的 0 是原生“不触发”，不是缺失的对话。
                    if (id == 0 && row.Separate) continue;
                    if (id <= 0 || !hasTalk(id))
                        return prefix + (row.Separate ? (i == 0 ? "男主" : "女主") : "共用") +
                            "首句 " + id + " 不存在。请重新选择剧情（不要填写事件 ID）。";
                }
            }
            return null;
        }

        internal void Export(out List<int> npcs, out List<List<int>> talks, out List<int> types)
        {
            if (ShapeError != null) throw new InvalidOperationException(ShapeError);
            npcs = Rows.Select(row => row.NpcId).ToList();
            talks = Rows.Select(row => new List<int>(row.TalkIds)).ToList();
            types = Rows.Select(row => row.ItemMode).ToList();
        }

        internal bool AddNpc(int id)
        {
            if (Rows.Any(row => row.NpcId == id)) return false;
            Rows.Add(new GiftBindingRow(id, null, 0));
            return true;
        }

        internal void Move(int index, int offset)
        {
            int target = index + offset;
            if (index < 0 || index >= Rows.Count || target < 0 || target >= Rows.Count) return;
            GiftBindingRow row = Rows[index];
            Rows.RemoveAt(index);
            Rows.Insert(target, row);
        }
    }

    internal sealed class GiftStoryChoice
    {
        internal int Id;
        internal string Title;
        internal string Content;
        internal bool IsLocal;
        internal bool IsEntry;

        internal bool Matches(string query)
        {
            query = query?.Trim();
            return string.IsNullOrEmpty(query) || Contains(Id.ToString(), query) ||
                Contains(Title, query) || Contains(Content, query);
        }

        private static bool Contains(string value, string query) =>
            value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
