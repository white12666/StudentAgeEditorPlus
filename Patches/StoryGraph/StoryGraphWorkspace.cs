using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 仅保存作者工作区信息（节点坐标、分组、注释），绝不写进游戏剧情 JSON。
    /// 文件位于 BepInEx/config/StudentAgeEditorPlus/StoryGraphLayouts，玩家端无需携带。
    /// </summary>
    internal sealed class StoryGraphWorkspace
    {
        // 防止损坏/手改的工作区文件把 NaN、Infinity 或极端大坐标带进
        // RectTransform / Bounds，进而让整张图消失或变得无法导航。
        private const float MaxCoordinate = 1000000f;

        internal sealed class PointData
        {
            public float X;
            public float Y;
        }

        internal sealed class GroupData
        {
            public string Id;
            public string Title;
            public string Color;
            public string Note;
            public bool IsNote;
            public float X;
            public float Y;
            public float Width;
            public float Height;
            public List<string> NodeKeys = new List<string>();
        }

        private sealed class EventData
        {
            public Dictionary<string, PointData> NodePositions =
                new Dictionary<string, PointData>(StringComparer.Ordinal);
            public List<GroupData> Groups = new List<GroupData>();
        }

        private sealed class WorkspaceFile
        {
            public int Version = 1;
            public string ModRoot;
            public Dictionary<string, EventData> Events =
                new Dictionary<string, EventData>(StringComparer.Ordinal);
        }

        internal sealed class Snapshot
        {
            internal Dictionary<string, Vector2> Positions;
            internal List<GroupData> Groups;
        }

        private readonly string _path;
        private readonly WorkspaceFile _file;
        private readonly EventData _event;
        private bool _dirty;

        private StoryGraphWorkspace(
            string path, WorkspaceFile file, EventData eventData)
        {
            _path = path;
            _file = file;
            _event = eventData;
        }

        internal IReadOnlyList<GroupData> Groups
        {
            get { return _event.Groups; }
        }

        internal bool Dirty
        {
            get { return _dirty; }
        }

        internal static StoryGraphWorkspace Load(string modRoot, int eventId)
        {
            string normalizedRoot;
            try { normalizedRoot = Path.GetFullPath(modRoot ?? string.Empty); }
            catch { normalizedRoot = modRoot ?? string.Empty; }
            string hash = StableHash(normalizedRoot.ToLowerInvariant());
            string directory = Path.Combine(
                Paths.ConfigPath, "StudentAgeEditorPlus", "StoryGraphLayouts");
            string path = Path.Combine(directory, hash + ".json");
            WorkspaceFile file = null;
            try
            {
                if (File.Exists(path))
                    file = JsonConvert.DeserializeObject<WorkspaceFile>(
                        File.ReadAllText(path));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[StoryGraph.Workspace] 布局文件读取失败，将使用自动布局：" + e.Message);
            }
            if (file == null) file = new WorkspaceFile();
            if (file.Events == null)
                file.Events = new Dictionary<string, EventData>(StringComparer.Ordinal);
            file.Version = 1;
            file.ModRoot = normalizedRoot;
            string key = eventId.ToString();
            EventData eventData;
            if (!file.Events.TryGetValue(key, out eventData) || eventData == null)
            {
                eventData = new EventData();
                file.Events[key] = eventData;
            }
            if (eventData.NodePositions == null)
                eventData.NodePositions = new Dictionary<string, PointData>(StringComparer.Ordinal);
            if (eventData.Groups == null) eventData.Groups = new List<GroupData>();
            bool normalized = NormalizePositions(eventData.NodePositions);
            NormalizeGroups(eventData.Groups);
            var workspace = new StoryGraphWorkspace(path, file, eventData);
            workspace._dirty = normalized;
            return workspace;
        }

        internal bool TryGetPosition(string stableKey, out Vector2 position)
        {
            position = Vector2.zero;
            if (string.IsNullOrEmpty(stableKey)) return false;
            PointData value;
            if (!_event.NodePositions.TryGetValue(stableKey, out value) || value == null)
                return false;
            return TrySanitizePosition(value.X, value.Y, out position);
        }

        internal Dictionary<string, Vector2> GetPositions()
        {
            var result = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, PointData> pair in _event.NodePositions)
            {
                Vector2 position;
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null
                    || !TrySanitizePosition(
                        pair.Value.X, pair.Value.Y, out position)) continue;
                result[pair.Key] = position;
            }
            return result;
        }

        internal void SetPosition(string stableKey, Vector2 position)
        {
            if (string.IsNullOrEmpty(stableKey)
                || !TrySanitizePosition(
                    position.x, position.y, out position)) return;
            PointData old;
            if (_event.NodePositions.TryGetValue(stableKey, out old) && old != null
                && Mathf.Abs(old.X - position.x) < 0.01f
                && Mathf.Abs(old.Y - position.y) < 0.01f) return;
            _event.NodePositions[stableKey] = new PointData
            {
                X = position.x,
                Y = position.y,
            };
            _dirty = true;
        }

        internal bool RemovePosition(string stableKey)
        {
            if (string.IsNullOrEmpty(stableKey)) return false;
            bool removed = _event.NodePositions.Remove(stableKey);
            if (removed) _dirty = true;
            return removed;
        }

        internal void ClearPositions()
        {
            if (_event.NodePositions.Count == 0) return;
            _event.NodePositions.Clear();
            _dirty = true;
        }

        internal int RemovePositionsExcept(ISet<string> validKeys)
        {
            var keys = new List<string>(_event.NodePositions.Keys);
            int removed = 0;
            foreach (string key in keys)
            {
                if (validKeys != null && validKeys.Contains(key)) continue;
                if (_event.NodePositions.Remove(key)) removed++;
            }
            if (removed > 0) _dirty = true;
            return removed;
        }

        internal int PruneGroupNodeKeys(
            ISet<string> validKeys, out int removedGroups)
        {
            int removedKeys = 0;
            removedGroups = 0;
            for (int i = _event.Groups.Count - 1; i >= 0; i--)
            {
                GroupData group = _event.Groups[i];
                if (group == null || group.IsNote) continue;
                if (group.NodeKeys == null) group.NodeKeys = new List<string>();
                removedKeys += group.NodeKeys.RemoveAll(key =>
                    string.IsNullOrEmpty(key)
                    || validKeys == null || !validKeys.Contains(key));
                if (group.NodeKeys.Count == 0)
                {
                    _event.Groups.RemoveAt(i);
                    removedGroups++;
                }
            }
            if (removedKeys > 0 || removedGroups > 0) _dirty = true;
            return removedKeys;
        }

        internal GroupData CreateGroup(
            string title, IEnumerable<string> nodeKeys, string color = "D7C49A",
            string note = null, bool isNote = false)
        {
            var group = new GroupData
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(title)
                    ? (isNote ? "新注释" : "新分组")
                    : title.Trim(),
                Color = string.IsNullOrWhiteSpace(color) ? "D7C49A" : color.Trim(),
                Note = note ?? string.Empty,
                IsNote = isNote,
                X = StoryGraphMetrics.Margin,
                Y = StoryGraphMetrics.Margin,
                Width = isNote ? 320f : 0f,
                Height = isNote ? 180f : 0f,
                NodeKeys = new List<string>(),
            };
            if (nodeKeys != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string key in nodeKeys)
                    if (!string.IsNullOrEmpty(key) && seen.Add(key)) group.NodeKeys.Add(key);
            }
            _event.Groups.Add(group);
            _dirty = true;
            return group;
        }

        internal bool RemoveGroup(string id)
        {
            int removed = _event.Groups.RemoveAll(group =>
                group != null && string.Equals(group.Id, id, StringComparison.Ordinal));
            if (removed > 0) _dirty = true;
            return removed > 0;
        }

        internal void RenameGroup(string id, string title)
        {
            GroupData group = FindGroup(id);
            if (group == null) return;
            string value = string.IsNullOrWhiteSpace(title) ? "分组" : title.Trim();
            if (group.Title == value) return;
            group.Title = value;
            _dirty = true;
        }

        internal bool UpdateGroupText(string id, string title, string note)
        {
            GroupData group = FindGroup(id);
            if (group == null) return false;
            title = string.IsNullOrWhiteSpace(title)
                ? (group.IsNote ? "注释" : "分组")
                : title.Trim();
            note = note ?? string.Empty;
            if (string.Equals(group.Title, title, StringComparison.Ordinal)
                && string.Equals(group.Note ?? string.Empty, note, StringComparison.Ordinal))
                return false;
            group.Title = title;
            group.Note = note;
            _dirty = true;
            return true;
        }

        internal bool SetGroupBounds(string id, Rect bounds)
        {
            GroupData group = FindGroup(id);
            if (group == null || float.IsNaN(bounds.x) || float.IsInfinity(bounds.x)
                || float.IsNaN(bounds.y) || float.IsInfinity(bounds.y)
                || float.IsNaN(bounds.width) || float.IsInfinity(bounds.width)
                || float.IsNaN(bounds.height) || float.IsInfinity(bounds.height))
                return false;
            float x = Mathf.Clamp(bounds.x, StoryGraphMetrics.Margin, MaxCoordinate);
            float y = Mathf.Clamp(bounds.y, StoryGraphMetrics.Margin, MaxCoordinate);
            float width = Mathf.Clamp(bounds.width, 160f, 2000f);
            float height = Mathf.Clamp(bounds.height, 100f, 2000f);
            if (Mathf.Abs(group.X - x) < 0.01f
                && Mathf.Abs(group.Y - y) < 0.01f
                && Mathf.Abs(group.Width - width) < 0.01f
                && Mathf.Abs(group.Height - height) < 0.01f) return false;
            group.X = x;
            group.Y = y;
            group.Width = width;
            group.Height = height;
            _dirty = true;
            return true;
        }

        internal GroupData FindGroup(string id)
        {
            return _event.Groups.Find(group =>
                group != null && string.Equals(group.Id, id, StringComparison.Ordinal));
        }

        internal Snapshot Capture()
        {
            return new Snapshot
            {
                Positions = GetPositions(),
                Groups = CloneGroups(_event.Groups),
            };
        }

        internal void Restore(Snapshot snapshot)
        {
            Restore(snapshot, true);
        }

        // 事务失败回滚时需要恢复调用前的 dirty 状态；正常撤销/重做则传 true，
        // 让 Save 确实把目标快照写回磁盘。
        internal void Restore(Snapshot snapshot, bool dirty)
        {
            if (snapshot == null) return;
            _event.NodePositions.Clear();
            if (snapshot.Positions != null)
            {
                foreach (KeyValuePair<string, Vector2> pair in snapshot.Positions)
                {
                    Vector2 position;
                    if (string.IsNullOrEmpty(pair.Key)
                        || !TrySanitizePosition(
                            pair.Value.x, pair.Value.y, out position)) continue;
                    _event.NodePositions[pair.Key] = new PointData
                    {
                        X = position.x,
                        Y = position.y,
                    };
                }
            }
            _event.Groups = CloneGroups(snapshot.Groups);
            _dirty = dirty;
        }

        internal bool Save(out string error)
        {
            error = null;
            if (!_dirty) return true;
            string temp = _path + ".tmp";
            string backup = _path + ".bak";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(temp,
                    JsonConvert.SerializeObject(_file, Formatting.Indented),
                    new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(temp, _path, backup, true);
                }
                else
                {
                    File.Move(temp, _path);
                }
                _dirty = false;
                return true;
            }
            catch (Exception e)
            {
                error = "保存剧情图工作区布局失败："
                        + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogError("[StoryGraph.Workspace.Save] " + e);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        internal static string StableNodeKey(StoryGraphDisplayNode node)
        {
            if (node == null) return null;
            // 折叠 Segment 在布局上替代首句 Talk。两种投影必须共享同一坐标键，
            // 否则作者移动过首句后，收起时段块会跳回自动位置并拉动可见连线。
            if (node.IsSegment && node.SegmentNodes != null
                && node.SegmentNodes.Count > 0
                && node.SegmentNodes[0] != null
                && node.SegmentNodes[0].Talk != null)
                return "talk:" + node.SegmentNodes[0].Talk.id;
            EvtStoryGraphNode source = node.SourceNode;
            if (source == null)
                return string.IsNullOrEmpty(node.Key) ? null : node.Key;

            // Talk 的重复 ID 无法通过保存校验；持久化键只使用真实记录 ID，
            // 避免 ordinal 在删除重复项后漂移并继承另一副本的旧坐标。
            if (source.Talk != null)
                return "talk:" + source.Talk.id;

            // 同一 OptionCfg 可以被多个父 Talk 合法复用。模板节点和 use 克隆
            // 必须统一按「选项字典键 + 父 Talk」标识，不能让“第一个引用者”
            // 的遍历顺序决定谁继承 option:{id} 的坐标。
            if (source.Option != null)
            {
                string key = "option:" + source.Id;
                if (source.LocateTalk != null)
                    key += ":talk:" + source.LocateTalk.id;
                else
                    key += ":orphan";
                return key;
            }

            return string.IsNullOrEmpty(source.Key) ? null : source.Key;
        }

        private static bool TrySanitizePosition(
            float x, float y, out Vector2 position)
        {
            position = Vector2.zero;
            if (float.IsNaN(x) || float.IsInfinity(x)
                || float.IsNaN(y) || float.IsInfinity(y)) return false;
            position = new Vector2(
                Mathf.Clamp(x, StoryGraphMetrics.Margin, MaxCoordinate),
                Mathf.Clamp(y, StoryGraphMetrics.Margin, MaxCoordinate));
            return true;
        }

        private static bool NormalizePositions(
            Dictionary<string, PointData> positions)
        {
            if (positions == null) return false;
            bool changed = false;
            var keys = new List<string>(positions.Keys);
            foreach (string key in keys)
            {
                PointData value;
                Vector2 position;
                if (string.IsNullOrEmpty(key)
                    || !positions.TryGetValue(key, out value)
                    || value == null
                    || !TrySanitizePosition(value.X, value.Y, out position))
                {
                    positions.Remove(key);
                    changed = true;
                    continue;
                }
                if (Mathf.Abs(value.X - position.x) < 0.01f
                    && Mathf.Abs(value.Y - position.y) < 0.01f) continue;
                value.X = position.x;
                value.Y = position.y;
                changed = true;
            }
            return changed;
        }

        private static string StableHash(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var result = new StringBuilder(24);
                for (int i = 0; i < 12; i++) result.Append(hash[i].ToString("x2"));
                return result.ToString();
            }
        }

        private static void NormalizeGroups(List<GroupData> groups)
        {
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                GroupData group = groups[i];
                if (group == null)
                {
                    groups.RemoveAt(i);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(group.Id))
                    group.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(group.Title))
                    group.Title = group.IsNote ? "注释" : "分组";
                if (string.IsNullOrWhiteSpace(group.Color)) group.Color = "D7C49A";
                if (group.Note == null) group.Note = string.Empty;
                if (group.NodeKeys == null) group.NodeKeys = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                group.NodeKeys.RemoveAll(key =>
                    string.IsNullOrEmpty(key) || !seen.Add(key));
                if (group.IsNote)
                {
                    Vector2 position;
                    if (!TrySanitizePosition(group.X, group.Y, out position))
                        position = new Vector2(
                            StoryGraphMetrics.Margin, StoryGraphMetrics.Margin);
                    group.X = position.x;
                    group.Y = position.y;
                    group.Width = !float.IsNaN(group.Width)
                        && !float.IsInfinity(group.Width)
                        ? Mathf.Clamp(group.Width <= 0f ? 320f : group.Width, 160f, 2000f)
                        : 320f;
                    group.Height = !float.IsNaN(group.Height)
                        && !float.IsInfinity(group.Height)
                        ? Mathf.Clamp(group.Height <= 0f ? 180f : group.Height, 100f, 2000f)
                        : 180f;
                }
            }
        }

        private static List<GroupData> CloneGroups(IEnumerable<GroupData> source)
        {
            var result = new List<GroupData>();
            if (source == null) return result;
            foreach (GroupData group in source)
            {
                if (group == null) continue;
                result.Add(new GroupData
                {
                    Id = group.Id,
                    Title = group.Title,
                    Color = group.Color,
                    Note = group.Note,
                    IsNote = group.IsNote,
                    X = group.X,
                    Y = group.Y,
                    Width = group.Width,
                    Height = group.Height,
                    NodeKeys = group.NodeKeys != null
                        ? new List<string>(group.NodeKeys)
                        : new List<string>(),
                });
            }
            return result;
        }
    }
}
