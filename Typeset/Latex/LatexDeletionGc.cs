using System;
using System.Collections.Generic;
using Config;

namespace StudentAgeTypeset.Latex
{
    /// <summary>
    /// 设计 §4 的 GC 第二层：**原版编辑器**删除路径的公式资产回收（D6-3 / LTX-D1-05）。
    ///
    /// 剧情图保存有自己的收尾（StoryGraphWindow.Formula.cs 的 PersistLatexSidecarAndGc），
    /// 但作者在原版事件表单里删掉对话时完全不经过那条路：边车登记会把已经没人引用的
    /// CGCfg 条目与 PNG 永久保活，且编号被复用后旧登记会给新对话挂上「幽灵公式」，
    /// 甚至因为校验不通过而挡住保存。
    ///
    /// 时序纪律：只在删除事务**提交成功之后**调用，全程 best-effort——边车写失败或
    /// GC 失败一律只警告，绝不把一次已经成功的删除判成失败（对齐 committedOnDisk 纪律）。
    /// </summary>
    internal static class LatexDeletionGc
    {
        /// <summary>
        /// 删除已提交后的收尾：清掉被删对话的边车登记，再按「磁盘 TalkCfg.json ∪
        /// 剩余边车登记 ∪ 视图内存现存对话」的引用集回收孤儿公式资产。
        /// </summary>
        internal static void AfterOrdinaryDelete(
            string modRoot, int eventId,
            ICollection<int> removedTalkIds, IEnumerable<TalkCfg> remainingTalks)
        {
            if (string.IsNullOrWhiteSpace(modRoot)) return;
            try
            {
                LatexSourceStore store = LatexSourceStore.Load(modRoot);
                if (store == null) return;

                if (eventId > 0 && removedTalkIds != null)
                    foreach (int talkId in removedTalkIds)
                    {
                        if (talkId <= 0) continue;
                        store.PruneTalk(eventId, talkId);
                    }

                if (store.Dirty)
                {
                    string saveError;
                    if (!store.Save(out saveError))
                        TypesetLog.Warn?.Invoke(
                            "[Latex.DeletionGc] LaTeX 源码边车写入失败（删除本身已生效）："
                            + saveError);
                }

                HashSet<int> referenced;
                string readError;
                if (!FormulaAssetService.TryCollectModTalkFormulaReferences(
                        modRoot, out referenced, out readError))
                {
                    // 反查不到引用就一张都不能删——宁可留孤儿，也不能删掉在用的图。
                    TypesetLog.Warn?.Invoke(
                        "[Latex.DeletionGc] 公式资产清理已跳过：" + readError);
                    return;
                }
                foreach (int id in store.CollectBlockCgIds()) referenced.Add(id);
                if (remainingTalks != null)
                    foreach (int id in FormulaAssetService
                                 .CollectReferencedFormulaCgIds(remainingTalks))
                        referenced.Add(id);

                FormulaGcResult gc = FormulaAssetService.GcOrphans(modRoot, referenced);
                if (!gc.Success)
                {
                    TypesetLog.Warn?.Invoke(
                        "[Latex.DeletionGc] 公式资产清理未完成：" + gc.Error);
                    return;
                }
                foreach (string warning in gc.Warnings)
                    TypesetLog.Warn?.Invoke("[Latex.DeletionGc] " + warning);
                if (gc.RemovedEntries > 0 || gc.DeletedPngs > 0)
                    TypesetLog.Info?.Invoke("[Latex.DeletionGc] 已清理 "
                        + gc.RemovedEntries + " 条无引用公式 CG 条目和 "
                        + gc.DeletedPngs + " 张孤儿公式图。");
            }
            catch (Exception e)
            {
                // 删除已经落盘，这里的任何异常都不许上抛。
                TypesetLog.Error?.Invoke("[Latex.DeletionGc] " + e);
            }
        }
    }
}
