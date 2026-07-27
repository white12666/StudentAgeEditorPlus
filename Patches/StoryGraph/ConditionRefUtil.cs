using System;
using System.Collections.Generic;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// ConditionerEvent(family 3) 条件行的参数内嵌 ID 工具。
    /// 行布局 [family, subType, id, id2, cnt]（ConditionerEvent.cs:21-44）：
    /// |subType|∈{3,30} 时 id/id2 是对话编号，|subType|==2 时是选项编号，
    /// |subType|==1 是事件编号、{4,5,6} 与剧情图节点无关，其余 family 不含节点引用。
    /// </summary>
    internal static class ConditionRefUtil
    {
        /// <summary>把条件行里引用的对话编号并入 target（只收 >0 的值）。</summary>
        internal static void CollectTalkIds(List<List<double>> rows, ISet<int> target)
        {
            CollectIds(rows, target, true);
        }

        /// <summary>把条件行里引用的选项编号并入 target（只收 >0 的值）。</summary>
        internal static void CollectOptionIds(List<List<double>> rows, ISet<int> target)
        {
            CollectIds(rows, target, false);
        }

        /// <summary>
        /// 返回 rows 的深拷贝，family 3 行中命中 map 的对话/选项参数替换为新编号。
        /// 不在 map 里的保持原值不动：跨组条件引用是合法创作，不清零不报错。
        /// remappedRows 是发生过替换的行数。null 输入/null 行原样返回。
        /// </summary>
        internal static List<List<double>> RemapInGroup(
            List<List<double>> rows,
            IDictionary<int, int> talkMap,
            IDictionary<int, int> optionMap,
            out int remappedRows)
        {
            remappedRows = 0;
            if (rows == null) return null;
            var result = new List<List<double>>(rows.Count);
            foreach (List<double> row in rows)
            {
                if (row == null)
                {
                    result.Add(null);
                    continue;
                }
                var copy = new List<double>(row);
                bool isTalkRow;
                if (TryClassifyRow(copy, out isTalkRow))
                {
                    IDictionary<int, int> map = isTalkRow ? talkMap : optionMap;
                    bool changed = false;
                    changed |= TryRemapCell(copy, 2, map);
                    changed |= TryRemapCell(copy, 3, map);
                    if (changed) remappedRows++;
                }
                result.Add(copy);
            }
            return result;
        }

        internal static bool ReferencesTalk(List<List<double>> rows, int id)
        {
            return ReferencesId(rows, id, true);
        }

        internal static bool ReferencesOption(List<List<double>> rows, int id)
        {
            return ReferencesId(rows, id, false);
        }

        private static void CollectIds(
            List<List<double>> rows, ISet<int> target, bool talk)
        {
            if (rows == null || target == null) return;
            foreach (List<double> row in rows)
            {
                bool isTalkRow;
                if (!TryClassifyRow(row, out isTalkRow) || isTalkRow != talk)
                    continue;
                int id = ToId(row[2]);
                if (id > 0) target.Add(id);
                if (row.Count >= 4)
                {
                    int id2 = ToId(row[3]);
                    if (id2 > 0) target.Add(id2);
                }
            }
        }

        private static bool ReferencesId(List<List<double>> rows, int id, bool talk)
        {
            if (rows == null || id <= 0) return false;
            foreach (List<double> row in rows)
            {
                bool isTalkRow;
                if (!TryClassifyRow(row, out isTalkRow) || isTalkRow != talk)
                    continue;
                if (ToId(row[2]) == id) return true;
                if (row.Count >= 4 && ToId(row[3]) == id) return true;
            }
            return false;
        }

        /// <summary>只识别携带对话/选项引用的 family 3 行；其余行返回 false。</summary>
        private static bool TryClassifyRow(List<double> row, out bool isTalkRow)
        {
            isTalkRow = false;
            if (row == null || row.Count < 3 || ToId(row[0]) != 3) return false;
            int subType = Math.Abs(ToId(row[1]));
            if (subType == 3 || subType == 30)
            {
                isTalkRow = true;
                return true;
            }
            return subType == 2;
        }

        private static bool TryRemapCell(
            List<double> row, int index, IDictionary<int, int> map)
        {
            if (map == null || row.Count <= index) return false;
            int id = ToId(row[index]);
            int mapped;
            if (id <= 0 || !map.TryGetValue(id, out mapped) || mapped == id)
                return false;
            row[index] = mapped;
            return true;
        }

        // 条件参数来自作者手填 JSON；NaN/越界 double 直接视为无引用，避免未定义转换。
        private static int ToId(double value)
        {
            if (double.IsNaN(value) || value < int.MinValue || value > int.MaxValue)
                return 0;
            return (int)value;
        }
    }
}
