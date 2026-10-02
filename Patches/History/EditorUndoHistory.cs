using System;
using System.Collections.Generic;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>内存历史；连续输入合并，导航/保存打断合并，恢复成功后才移动游标。</summary>
    internal sealed class EditorUndoHistory<T> where T : class
    {
        private sealed class Entry
        {
            internal T Before;
            internal T After;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Func<T, T, bool> _same;
        private readonly int _capacity;
        private int _position;
        private string _mergeKey;
        private double _lastEdit;

        internal EditorUndoHistory(Func<T, T, bool> same, int capacity = 100)
        {
            _same = same;
            _capacity = Math.Max(1, capacity);
        }

        internal T Current { get; private set; }
        internal bool CanUndo => _position > 0;
        internal bool CanRedo => _position < _entries.Count;
        internal int Count => _entries.Count;

        internal void Reset(T state)
        {
            _entries.Clear();
            _position = 0;
            Current = state;
            BreakMerge();
        }

        // 选中项、光标、原版 onEndEdit 的规范化不是新的编辑。
        internal void Rebase(T state)
        {
            Current = state;
            if (_position > 0) _entries[_position - 1].After = state;
            if (_position < _entries.Count) _entries[_position].Before = state;
        }

        internal void BreakMerge() => _mergeKey = null;

        internal bool Record(T state, string mergeKey, double now)
        {
            if (Current == null) { Reset(state); return false; }
            if (_same(Current, state)) { Rebase(state); return false; }
            bool merge = mergeKey != null && mergeKey == _mergeKey
                         && _position == _entries.Count && _position > 0
                         && now >= _lastEdit && now - _lastEdit <= 1.0;
            if (_position < _entries.Count)
                _entries.RemoveRange(_position, _entries.Count - _position);
            if (merge) _entries[_position - 1].After = state;
            else
            {
                _entries.Add(new Entry { Before = Current, After = state });
                _position++;
            }
            Current = state;
            _mergeKey = mergeKey;
            _lastEdit = now;
            if (_entries.Count > _capacity)
            {
                _entries.RemoveAt(0);
                _position--;
            }
            return true;
        }

        internal bool Move(bool redo, Func<T, bool> restore)
        {
            BreakMerge();
            if (redo ? !CanRedo : !CanUndo) return false;
            T target = redo ? _entries[_position].After : _entries[_position - 1].Before;
            if (!restore(target)) return false;
            _position += redo ? 1 : -1;
            Current = target;
            return true;
        }

        internal void Visit(Action<T> action)
        {
            if (Current != null) action(Current);
            foreach (Entry entry in _entries)
            {
                action(entry.Before);
                action(entry.After);
            }
        }
    }
}
