using System.Collections.Generic;

namespace StudentAgeEditorPlus.Patches
{
    // 不依赖 Unity：窗口租约、播放请求与 UI 状态可独立回归。
    internal sealed class EditorAudioPolicy
    {
        private readonly HashSet<object> editors = new HashSet<object>();
        private object preview;
        private long generation;

        internal bool Quiet { get; set; }
        internal long Generation => generation;
        internal bool Active => editors.Count > 0 || preview != null;
        internal bool InPreview => preview != null;
        internal bool PreviewMusic { get; private set; }
        internal bool MuteMusic => Active && Quiet && !PreviewMusic;
        internal string Label => PreviewMusic ? "试听中" : "BGM";
        internal string Hint => PreviewMusic
            ? "正在试听剧情音乐，结束后恢复编辑 BGM 设置"
            : Quiet ? "恢复编辑背景音乐（不改变游戏音量）"
                    : "关闭编辑背景音乐，不影响剧情预览";

        internal void Enter(object editor)
        {
            if (editor == null) return;
            if (!Active) Invalidate();
            editors.Add(editor);
        }

        internal void Leave(object editor)
        {
            bool wasActive = Active;
            editors.Remove(editor);
            if (wasActive && !Active) Invalidate();
        }

        internal void BeginPreview(object owner)
        {
            preview = owner;
            Invalidate();
        }

        internal bool EndPreview(object owner)
        {
            if (!ReferenceEquals(owner, preview) || preview == null) return false;
            preview = null;
            Invalidate();
            return true;
        }

        internal bool Request(bool fromPreview, out long token)
        {
            token = 0;
            if (!Active || (fromPreview ? !InPreview : InPreview)) return false;
            token = ++generation;
            return true;
        }

        internal bool IsCurrent(long token) => Active && token == generation;

        internal bool Complete(long token, bool fromPreview, bool loaded)
        {
            if (!IsCurrent(token)) return false;
            PreviewMusic = fromPreview && loaded;
            return true;
        }

        internal void Invalidate()
        {
            generation++;
            PreviewMusic = false;
        }
    }
}
