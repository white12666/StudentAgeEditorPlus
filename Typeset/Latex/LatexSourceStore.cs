using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;

namespace StudentAgeTypeset.Latex
{
    /// <summary>
    /// 单条 talk 的块级公式附件（LATEX-DESIGN §1 的 Block）。CgId 供保存后 GC 做
    /// “边车登记”并集；AutoCloseTalkIds 记录保存时自动补写 [4017] 的下一句 id，
    /// 移除公式时只回收仍保持 [4017] 原样的自动条目。
    /// </summary>
    internal sealed class LatexBlockData
    {
        public string Latex;
        public int CgId;
        /// <summary>
        /// 排版模式：0=纯公式（居中放大），1=图文混排（左上对齐+自动折行）。
        /// 用 int 而不是枚举：边车 JSON 要能被旧版本插件容错读取，缺字段即 0。
        /// </summary>
        public int Mode;
        // 设计 §5：4019 显示后不会随下一句自动消失，默认开启“下一句自动关闭”。
        public bool AutoClose = true;
        public List<int> AutoCloseTalkIds = new List<int>();

        internal LatexBlockData Clone()
        {
            return new LatexBlockData
            {
                Latex = Latex,
                CgId = CgId,
                Mode = Mode,
                AutoClose = AutoClose,
                AutoCloseTalkIds = AutoCloseTalkIds != null
                    ? new List<int>(AutoCloseTalkIds)
                    : new List<int>(),
            };
        }

        internal static bool ContentEquals(LatexBlockData left, LatexBlockData right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (!string.Equals(left.Latex, right.Latex, StringComparison.Ordinal)
                || left.CgId != right.CgId
                || left.AutoClose != right.AutoClose) return false;
            List<int> a = left.AutoCloseTalkIds ?? new List<int>();
            List<int> b = right.AutoCloseTalkIds ?? new List<int>();
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// LaTeX 源码边车。mod JSON 只存烘焙产物（纯原版形状），$ 原文与块级公式附件存
    /// BepInEx/config/StudentAgeEditorPlus/LatexSources/——私有、不随创意工坊分发；
    /// 丢失只失去“再编辑”，烘焙文本仍可当普通文本改（LATEX-DESIGN §1）。
    /// 全套照 StoryGraphWorkspace 模式：Version 字段、容错读（损坏则空表+警告）、
    /// temp+File.Replace+.bak 原子写、仅 dirty 才写。写入时机由调用方对齐
    /// PruneSavedWorkspacePositions：TrySave 提交成功之后 best-effort，失败仅警告，
    /// 绝不把已提交的配置保存判为失败（committedOnDisk 纪律）。
    /// </summary>
    internal sealed class LatexSourceStore
    {
        private sealed class TalkData
        {
            public string Source;
            public LatexBlockData Block;
        }

        private sealed class EventData
        {
            public Dictionary<string, TalkData> Talks =
                new Dictionary<string, TalkData>(StringComparer.Ordinal);
            /// <summary>
            /// 选项正文的 $ 源码（D6-2）。选项没有块级公式（画面指令挂在对话上），
            /// 因此只需要一张 id → 源码 表。旧版边车缺该字段，反序列化得 null，
            /// Normalize 会补成空表——向后兼容，Version 保持 1。
            /// </summary>
            public Dictionary<string, string> Options =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private sealed class StoreFile
        {
            public int Version = 1;
            public string ModRoot;
            public Dictionary<string, EventData> Events =
                new Dictionary<string, EventData>(StringComparer.Ordinal);
        }

        private readonly string _path;
        private readonly StoreFile _file;
        private bool _dirty;

        private LatexSourceStore(string path, StoreFile file)
        {
            _path = path;
            _file = file;
        }

        internal bool Dirty
        {
            get { return _dirty; }
        }

        internal static LatexSourceStore Load(string modRoot)
        {
            string normalizedRoot;
            try { normalizedRoot = Path.GetFullPath(modRoot ?? string.Empty); }
            catch { normalizedRoot = modRoot ?? string.Empty; }
            string hash = StableHash(normalizedRoot.ToLowerInvariant());
            // 目录名 "StudentAgeEditorPlus" 是冻结契约：既有作者机器上的边车都在这里，
            // 库迁入 StudentAgeTypeset 后也绝不能改，否则所有已存源码一夜"丢失"。
            string directory = Path.Combine(
                Paths.ConfigPath, "StudentAgeEditorPlus", "LatexSources");
            string path = Path.Combine(directory, hash + ".json");
            StoreFile file = null;
            try
            {
                if (File.Exists(path))
                    file = JsonConvert.DeserializeObject<StoreFile>(
                        File.ReadAllText(path));
            }
            catch (Exception e)
            {
                TypesetLog.Warn?.Invoke(
                    "[Latex.SourceStore] 边车文件读取失败，将按无源码继续"
                    + "（烘焙文本不受影响）：" + e.Message);
            }
            if (file == null) file = new StoreFile();
            file.Version = 1;
            file.ModRoot = normalizedRoot;
            bool normalized = Normalize(file);
            var store = new LatexSourceStore(path, file);
            store._dirty = normalized;
            return store;
        }

        internal string GetTalkSource(int eventId, int talkId)
        {
            TalkData talk = FindTalk(eventId, talkId);
            return talk != null ? talk.Source : null;
        }

        internal void SetTalkSource(int eventId, int talkId, string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                TalkData existing = FindTalk(eventId, talkId);
                if (existing == null || existing.Source == null) return;
                existing.Source = null;
                PruneIfEmpty(eventId, talkId);
                _dirty = true;
                return;
            }
            TalkData talk = GetOrCreateTalk(eventId, talkId);
            if (string.Equals(talk.Source, source, StringComparison.Ordinal)) return;
            talk.Source = source;
            _dirty = true;
        }

        internal string GetOptionSource(int eventId, int optionId)
        {
            EventData eventData = FindEvent(eventId);
            if (eventData == null || eventData.Options == null) return null;
            string source;
            return eventData.Options.TryGetValue(
                optionId.ToString(), out source) ? source : null;
        }

        internal void SetOptionSource(int eventId, int optionId, string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                PruneOption(eventId, optionId);
                return;
            }
            EventData eventData = GetOrCreateEvent(eventId);
            string key = optionId.ToString();
            string existing;
            if (eventData.Options.TryGetValue(key, out existing)
                && string.Equals(existing, source, StringComparison.Ordinal))
                return;
            eventData.Options[key] = source;
            _dirty = true;
        }

        /// <summary>某事件当前登记过源码的全部 optionId：保存成功后据此剪除已从
        /// 草稿删除的选项条目（与 CollectTalkIds 同口径）。</summary>
        internal List<int> CollectOptionIds(int eventId)
        {
            var result = new List<int>();
            EventData eventData = FindEvent(eventId);
            if (eventData == null || eventData.Options == null) return result;
            foreach (string key in eventData.Options.Keys)
            {
                int id;
                if (int.TryParse(key, out id)) result.Add(id);
            }
            return result;
        }

        internal bool PruneOption(int eventId, int optionId)
        {
            EventData eventData = FindEvent(eventId);
            if (eventData == null || eventData.Options == null) return false;
            if (!eventData.Options.Remove(optionId.ToString())) return false;
            PruneEventIfEmpty(eventId, eventData);
            _dirty = true;
            return true;
        }

        internal LatexBlockData GetBlock(int eventId, int talkId)
        {
            TalkData talk = FindTalk(eventId, talkId);
            // 返回克隆：调用方改动必须经 SetBlock 回写，否则 dirty 追踪失真。
            return talk != null && talk.Block != null ? talk.Block.Clone() : null;
        }

        internal void SetBlock(int eventId, int talkId, LatexBlockData block)
        {
            if (block == null || string.IsNullOrEmpty(block.Latex))
            {
                RemoveBlock(eventId, talkId);
                return;
            }
            TalkData talk = GetOrCreateTalk(eventId, talkId);
            if (LatexBlockData.ContentEquals(talk.Block, block)) return;
            talk.Block = block.Clone();
            _dirty = true;
        }

        internal bool RemoveBlock(int eventId, int talkId)
        {
            TalkData talk = FindTalk(eventId, talkId);
            if (talk == null || talk.Block == null) return false;
            talk.Block = null;
            PruneIfEmpty(eventId, talkId);
            _dirty = true;
            return true;
        }

        internal bool PruneTalk(int eventId, int talkId)
        {
            EventData eventData = FindEvent(eventId);
            if (eventData == null) return false;
            if (!eventData.Talks.Remove(talkId.ToString())) return false;
            PruneEventIfEmpty(eventId, eventData);
            _dirty = true;
            return true;
        }

        internal bool PruneEvent(int eventId)
        {
            if (!_file.Events.Remove(eventId.ToString())) return false;
            _dirty = true;
            return true;
        }

        /// <summary>某事件当前登记过 Source/Block 的全部 talkId：保存成功后据此剪除
        /// 已从草稿删除的对话条目（否则其 CgId 会永远把孤儿 PNG 保活）。</summary>
        internal List<int> CollectTalkIds(int eventId)
        {
            var result = new List<int>();
            EventData eventData = FindEvent(eventId);
            if (eventData == null || eventData.Talks == null) return result;
            foreach (string key in eventData.Talks.Keys)
            {
                int id;
                if (int.TryParse(key, out id)) result.Add(id);
            }
            return result;
        }

        /// <summary>全部已登记块级公式的 CgId：保存后 GC 的“边车登记”并集来源（设计 §4）。</summary>
        internal HashSet<int> CollectBlockCgIds()
        {
            var result = new HashSet<int>();
            foreach (KeyValuePair<string, EventData> eventPair in _file.Events)
            {
                if (eventPair.Value == null || eventPair.Value.Talks == null) continue;
                foreach (KeyValuePair<string, TalkData> talkPair in eventPair.Value.Talks)
                {
                    LatexBlockData block = talkPair.Value != null ? talkPair.Value.Block : null;
                    if (block != null && block.CgId > 0) result.Add(block.CgId);
                }
            }
            return result;
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
                error = "保存 LaTeX 源码边车失败："
                        + e.GetType().Name + ": " + e.Message;
                TypesetLog.Error?.Invoke("[Latex.SourceStore.Save] " + e);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        private TalkData FindTalk(int eventId, int talkId)
        {
            EventData eventData = FindEvent(eventId);
            if (eventData == null) return null;
            TalkData talk;
            return eventData.Talks.TryGetValue(talkId.ToString(), out talk) ? talk : null;
        }

        private EventData FindEvent(int eventId)
        {
            EventData eventData;
            return _file.Events.TryGetValue(eventId.ToString(), out eventData)
                ? eventData
                : null;
        }

        private EventData GetOrCreateEvent(int eventId)
        {
            string eventKey = eventId.ToString();
            EventData eventData;
            if (!_file.Events.TryGetValue(eventKey, out eventData) || eventData == null)
            {
                eventData = new EventData();
                _file.Events[eventKey] = eventData;
            }
            if (eventData.Talks == null)
                eventData.Talks = new Dictionary<string, TalkData>(StringComparer.Ordinal);
            if (eventData.Options == null)
                eventData.Options = new Dictionary<string, string>(StringComparer.Ordinal);
            return eventData;
        }

        private void PruneEventIfEmpty(int eventId, EventData eventData)
        {
            if (eventData == null) return;
            if ((eventData.Talks != null && eventData.Talks.Count > 0)
                || (eventData.Options != null && eventData.Options.Count > 0))
                return;
            _file.Events.Remove(eventId.ToString());
        }

        private TalkData GetOrCreateTalk(int eventId, int talkId)
        {
            EventData eventData = GetOrCreateEvent(eventId);
            string talkKey = talkId.ToString();
            TalkData talk;
            if (!eventData.Talks.TryGetValue(talkKey, out talk) || talk == null)
            {
                talk = new TalkData();
                eventData.Talks[talkKey] = talk;
            }
            return talk;
        }

        private void PruneIfEmpty(int eventId, int talkId)
        {
            EventData eventData = FindEvent(eventId);
            if (eventData == null) return;
            string talkKey = talkId.ToString();
            TalkData talk;
            if (eventData.Talks.TryGetValue(talkKey, out talk)
                && (talk == null || (talk.Source == null && talk.Block == null)))
                eventData.Talks.Remove(talkKey);
            PruneEventIfEmpty(eventId, eventData);
        }

        /// <summary>
        /// 读入后的结构净化：手改/损坏文件里的空壳条目一律剔除，Block 无 Latex 视为无
        /// Block（CgId 只在有公式时有意义）；净化发生过则置 dirty，下次保存写回干净版。
        /// </summary>
        private static bool Normalize(StoreFile file)
        {
            bool changed = false;
            if (file.Events == null)
            {
                file.Events = new Dictionary<string, EventData>(StringComparer.Ordinal);
                return false;
            }
            var eventKeys = new List<string>(file.Events.Keys);
            foreach (string eventKey in eventKeys)
            {
                EventData eventData = file.Events[eventKey];
                if (eventData == null)
                {
                    file.Events.Remove(eventKey);
                    changed = true;
                    continue;
                }
                if (eventData.Talks == null)
                    eventData.Talks =
                        new Dictionary<string, TalkData>(StringComparer.Ordinal);
                // 旧版边车没有 Options 字段：反序列化为 null，补空表而不是整条丢弃。
                if (eventData.Options == null)
                    eventData.Options =
                        new Dictionary<string, string>(StringComparer.Ordinal);
                var optionKeys = new List<string>(eventData.Options.Keys);
                foreach (string optionKey in optionKeys)
                {
                    string optionSource = eventData.Options[optionKey];
                    if (!string.IsNullOrEmpty(optionSource)) continue;
                    eventData.Options.Remove(optionKey);
                    changed = true;
                }
                var talkKeys = new List<string>(eventData.Talks.Keys);
                foreach (string talkKey in talkKeys)
                {
                    TalkData talk = eventData.Talks[talkKey];
                    if (talk != null && talk.Source != null && talk.Source.Length == 0)
                    {
                        talk.Source = null;
                        changed = true;
                    }
                    if (talk != null && talk.Block != null
                        && string.IsNullOrEmpty(talk.Block.Latex))
                    {
                        talk.Block = null;
                        changed = true;
                    }
                    if (talk != null && talk.Block != null
                        && talk.Block.AutoCloseTalkIds == null)
                    {
                        talk.Block.AutoCloseTalkIds = new List<int>();
                        changed = true;
                    }
                    if (talk == null || (talk.Source == null && talk.Block == null))
                    {
                        eventData.Talks.Remove(talkKey);
                        changed = true;
                    }
                }
                if (eventData.Talks.Count == 0 && eventData.Options.Count == 0)
                {
                    file.Events.Remove(eventKey);
                    changed = true;
                }
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
    }
}
