using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>快照里的一条配置记录。Blank 表示它与同编号的全新空记录完全一致。</summary>
    internal sealed class EditorRecordState
    {
        internal EditorRecordState(string kind, string fingerprint, bool blank)
        {
            Kind = kind;
            Fingerprint = fingerprint;
            Blank = blank;
        }

        internal string Kind { get; }
        internal string Fingerprint { get; }
        internal bool Blank { get; }
    }

    /// <summary>编辑页内存中全部配置记录的规范化指纹，按「类别:编号」索引。</summary>
    internal sealed class EditorSnapshot
    {
        private readonly Dictionary<string, EditorRecordState> _records =
            new Dictionary<string, EditorRecordState>(StringComparer.Ordinal);
        private readonly List<string> _kinds = new List<string>();

        internal IReadOnlyDictionary<string, EditorRecordState> Records => _records;
        internal IReadOnlyList<string> Kinds => _kinds;

        internal static string KeyOf(string kind, string id) => kind + ":" + id;

        /// <summary>
        /// blankTemplate 是同编号的全新记录（与原版「新建」产生的对象一致）；
        /// 传 null 表示这类记录没有“空白”的概念。
        /// </summary>
        internal string Add(string kind, string id, object record, object blankTemplate)
        {
            if (!_kinds.Contains(kind)) _kinds.Add(kind);
            string fingerprint = EditorChangeTracking.Fingerprint(record);
            bool blank = blankTemplate != null && string.Equals(
                fingerprint, EditorChangeTracking.Fingerprint(blankTemplate),
                StringComparison.Ordinal);
            string baseKey = KeyOf(kind, id);
            string key = baseKey;
            // 同编号重复出现时逐个编号，保证每条记录都参与比较。
            for (int n = 2; _records.ContainsKey(key); n++) key = baseKey + "#" + n;
            _records[key] = new EditorRecordState(kind, fingerprint, blank);
            return key;
        }

        internal bool TryGet(string key, out EditorRecordState state) =>
            _records.TryGetValue(key, out state);

        internal void Replace(string key, EditorRecordState state) => _records[key] = state;
    }

    internal enum EditorChangeKind { Added, Modified, Removed }

    /// <summary>两份快照的差异，按类别统计新增/修改/删除。</summary>
    internal sealed class EditorChangeSummary
    {
        private sealed class Counts
        {
            internal int Added;
            internal int Modified;
            internal int Removed;
        }

        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, Counts> _byKind =
            new Dictionary<string, Counts>(StringComparer.Ordinal);
        private readonly List<string> _changedKeys = new List<string>();

        internal bool HasChanges => _changedKeys.Count > 0;
        internal IReadOnlyList<string> ChangedKeys => _changedKeys;

        internal int Count(string kind, EditorChangeKind change)
        {
            Counts counts;
            if (!_byKind.TryGetValue(kind, out counts)) return 0;
            switch (change)
            {
                case EditorChangeKind.Added: return counts.Added;
                case EditorChangeKind.Modified: return counts.Modified;
                default: return counts.Removed;
            }
        }

        internal void DeclareKinds(IEnumerable<string> kinds)
        {
            foreach (string kind in kinds)
            {
                if (_byKind.ContainsKey(kind)) continue;
                _order.Add(kind);
                _byKind[kind] = new Counts();
            }
        }

        internal void Record(string kind, string key, EditorChangeKind change)
        {
            DeclareKinds(new[] { kind });
            Counts counts = _byKind[kind];
            if (change == EditorChangeKind.Added) counts.Added++;
            else if (change == EditorChangeKind.Modified) counts.Modified++;
            else counts.Removed++;
            _changedKeys.Add(key);
        }

        /// <summary>例：“对话修改 2 条、新增 1 条；选项删除 1 条”。</summary>
        internal string Describe()
        {
            var parts = new List<string>();
            foreach (string kind in _order)
            {
                Counts counts = _byKind[kind];
                var items = new List<string>();
                if (counts.Modified > 0) items.Add("修改 " + counts.Modified + " 条");
                if (counts.Added > 0) items.Add("新增 " + counts.Added + " 条");
                if (counts.Removed > 0) items.Add("删除 " + counts.Removed + " 条");
                if (items.Count > 0) parts.Add(kind + string.Join("、", items.ToArray()));
            }
            return string.Join("；", parts.ToArray());
        }
    }

    /// <summary>
    /// 编辑页未保存修改的判定工具：配置对象的规范化指纹、快照比较，以及脱离全局表的深拷贝。
    /// 只依赖反射与 BCL，便于离线测试。
    /// </summary>
    internal static class EditorChangeTracking
    {
        private const int MaxDepth = 16;

        private static readonly Dictionary<Type, FieldInfo[]> PublicFieldCache =
            new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, FieldInfo[]> AllFieldCache =
            new Dictionary<Type, FieldInfo[]>();
        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
            "MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// 比较时视为相同的差异：null 列表与空列表、null 字符串与空串（原版保存会把 null
        /// 列表补成空列表，输入框失焦也会把 null 正文写成空串，二者都不是作者的修改）；
        /// 浮点数按原版输入框回写时的精度（float 7 位、double 15 位有效数字）格式化，
        /// 输入框原样失焦不会因为舍入被判成修改。
        /// </summary>
        internal static string Fingerprint(object value)
        {
            var builder = new StringBuilder(256);
            Append(builder, value, value != null ? value.GetType() : null, 0);
            return builder.ToString();
        }

        /// <summary>
        /// accepted 是可选的第二份“已保存”状态：保存前后其它插件可能改写内存（例如 LaTeX
        /// 保存时把 $ 源码烘焙成成品、写盘确认后再换回源码），这两种写法都代表同一次
        /// 成功保存的内容。记录与 baseline 或 accepted 任一份一致都不算修改。
        /// </summary>
        internal static EditorChangeSummary Compare(EditorSnapshot baseline, EditorSnapshot current,
            EditorSnapshot accepted = null)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (current == null) throw new ArgumentNullException(nameof(current));
            var summary = new EditorChangeSummary();
            summary.DeclareKinds(current.Kinds);
            summary.DeclareKinds(baseline.Kinds);
            foreach (KeyValuePair<string, EditorRecordState> pair in current.Records)
            {
                EditorRecordState before;
                if (baseline.TryGet(pair.Key, out before))
                {
                    EditorRecordState alternative;
                    bool same = string.Equals(before.Fingerprint, pair.Value.Fingerprint,
                                    StringComparison.Ordinal)
                                || (accepted != null && accepted.TryGet(pair.Key, out alternative)
                                    && string.Equals(alternative.Fingerprint,
                                        pair.Value.Fingerprint, StringComparison.Ordinal));
                    if (!same)
                        summary.Record(pair.Value.Kind, pair.Key, EditorChangeKind.Modified);
                }
                else if (!pair.Value.Blank)
                {
                    summary.Record(pair.Value.Kind, pair.Key, EditorChangeKind.Added);
                }
            }
            foreach (KeyValuePair<string, EditorRecordState> pair in baseline.Records)
            {
                if (!pair.Value.Blank && !current.Records.ContainsKey(pair.Key))
                    summary.Record(pair.Value.Kind, pair.Key, EditorChangeKind.Removed);
            }
            return summary;
        }

        /// <summary>
        /// 深拷贝配置对象：先按字段浅复制（不会漏掉任何字段），再逐层复制其中的列表、
        /// 数组、字典和嵌套对象。遇到无法安全复制的集合类型直接抛异常，由调用方保留原对象。
        /// </summary>
        internal static T DeepCopy<T>(T value) where T : class
        {
            return (T)CopyValue(value, 0);
        }

        private static void Append(StringBuilder builder, object value, Type declared, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidOperationException("配置对象嵌套过深，无法比较。");
            if (value == null)
            {
                if (declared == typeof(string)) AppendString(builder, string.Empty);
                else if (declared != null && typeof(IDictionary).IsAssignableFrom(declared))
                    builder.Append("{}");
                else if (declared != null && declared != typeof(string)
                         && typeof(IEnumerable).IsAssignableFrom(declared))
                    builder.Append("[]");
                else builder.Append('~');
                return;
            }

            Type type = value.GetType();
            if (value is string text)
            {
                AppendString(builder, text);
                return;
            }
            if (value is bool flag)
            {
                builder.Append(flag ? "true" : "false");
                return;
            }
            if (value is float single)
            {
                builder.Append(single.ToString("G7", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double number)
            {
                builder.Append(number.ToString("G15", CultureInfo.InvariantCulture));
                return;
            }
            if (type.IsEnum)
            {
                builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (type.IsPrimitive || value is decimal)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is IDictionary dictionary)
            {
                Type valueType = DictionaryValueType(type);
                var entries = new List<KeyValuePair<string, string>>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = new StringBuilder();
                    Append(key, entry.Key, entry.Key != null ? entry.Key.GetType() : null, depth + 1);
                    var item = new StringBuilder();
                    Append(item, entry.Value, valueType, depth + 1);
                    entries.Add(new KeyValuePair<string, string>(key.ToString(), item.ToString()));
                }
                entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                builder.Append('{');
                foreach (KeyValuePair<string, string> entry in entries)
                    builder.Append(entry.Key).Append(':').Append(entry.Value).Append(',');
                builder.Append('}');
                return;
            }
            if (value is IEnumerable sequence)
            {
                Type elementType = SequenceElementType(type);
                builder.Append('[');
                foreach (object item in sequence)
                {
                    Append(builder, item, elementType, depth + 1);
                    builder.Append(',');
                }
                builder.Append(']');
                return;
            }

            builder.Append('{');
            foreach (FieldInfo field in PublicFields(type))
            {
                builder.Append(field.Name).Append('=');
                Append(builder, field.GetValue(value), field.FieldType, depth + 1);
                builder.Append(';');
            }
            builder.Append('}');
        }

        private static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') builder.Append('\\');
                builder.Append(c);
            }
            builder.Append('"');
        }

        private static object CopyValue(object value, int depth)
        {
            if (value == null) return null;
            Type type = value.GetType();
            if (type.IsValueType || value is string) return value;
            if (depth > MaxDepth)
                throw new InvalidOperationException("配置对象嵌套过深，无法复制。");

            if (value is Array array)
            {
                var copy = (Array)array.Clone();
                if (!type.GetElementType().IsValueType)
                {
                    for (int i = 0; i < copy.Length; i++)
                        copy.SetValue(CopyValue(array.GetValue(i), depth + 1), i);
                }
                return copy;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var source = (IList)value;
                var copy = (IList)Activator.CreateInstance(type, source.Count);
                foreach (object item in source) copy.Add(CopyValue(item, depth + 1));
                return copy;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var source = (IDictionary)value;
                var copy = (IDictionary)Activator.CreateInstance(type);
                foreach (DictionaryEntry entry in source)
                    copy[CopyValue(entry.Key, depth + 1)] = CopyValue(entry.Value, depth + 1);
                return copy;
            }
            if (value is IEnumerable)
                throw new NotSupportedException("不支持复制集合类型 " + type.FullName + "。");

            object clone = MemberwiseCloneMethod.Invoke(value, null);
            foreach (FieldInfo field in AllInstanceFields(type))
            {
                if (field.FieldType.IsValueType || field.FieldType == typeof(string)) continue;
                object fieldValue = field.GetValue(value);
                if (fieldValue == null) continue;
                field.SetValue(clone, CopyValue(fieldValue, depth + 1));
            }
            return clone;
        }

        private static Type SequenceElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            foreach (Type candidate in type.GetInterfaces())
            {
                if (candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return candidate.GetGenericArguments()[0];
            }
            return typeof(object);
        }

        private static Type DictionaryValueType(Type type)
        {
            foreach (Type candidate in type.GetInterfaces())
            {
                if (candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                    return candidate.GetGenericArguments()[1];
            }
            return typeof(object);
        }

        private static FieldInfo[] PublicFields(Type type)
        {
            lock (PublicFieldCache)
            {
                FieldInfo[] fields;
                if (PublicFieldCache.TryGetValue(type, out fields)) return fields;
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
                PublicFieldCache[type] = fields;
                return fields;
            }
        }

        private static FieldInfo[] AllInstanceFields(Type type)
        {
            lock (AllFieldCache)
            {
                FieldInfo[] fields;
                if (AllFieldCache.TryGetValue(type, out fields)) return fields;
                var list = new List<FieldInfo>();
                for (Type current = type; current != null && current != typeof(object);
                     current = current.BaseType)
                {
                    list.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                }
                fields = list.ToArray();
                AllFieldCache[type] = fields;
                return fields;
            }
        }
    }
}
