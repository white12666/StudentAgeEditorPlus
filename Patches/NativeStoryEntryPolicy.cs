using System;
using System.Collections.Generic;

namespace StudentAgeEditorPlus.Patches
{
    internal static class NativeStoryEntryPolicy
    {
        internal static int FirstTalkId(int eventId) =>
            eventId > 0 && eventId <= 1999999 ? eventId * 1000 + 1 : -1;

        internal static bool UsesAutomaticEntry(bool newlyCreated, bool explicitlyEdited,
            IList<int> entries) =>
            entries == null || entries.Count == 0 || (newlyCreated && !explicitlyEdited);

        internal static int AllocateTalkId(int eventId, int selectedId, IEnumerable<int> occupied)
        {
            int first = FirstTalkId(eventId);
            if (first < 0) return -1;
            int last = first + 998;
            var used = new HashSet<int>(occupied);
            int start = selectedId >= first && selectedId < last ? selectedId + 1 : first;
            for (int i = 0; i < 999; i++)
            {
                int candidate = first + (start - first + i) % 999;
                if (!used.Contains(candidate)) return candidate;
            }
            return -1;
        }
    }
}
