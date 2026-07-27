using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Config;
using HarmonyLib;
using StudentAgeTypeset.Latex;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 原版事件编辑器（ModEvtEditView）的行内公式实时预览与源码回填（设计 §6.2）。
    ///
    /// 预览标签挂在输入框下沿外侧的空白带里（prefab 实测：普通模式 inputex_talk
    /// 下沿在 group_talk 局部 y=80，img_talk 下沿 y=50；CG 模式 inputex_talk_cg
    /// 下沿在 mask_talk_cg 局部 y=75），因此不遮挡 btn_name / itemgroup_options
    /// 等任何既有控件。原版只占用 onEndEdit（ModEvtEditView.cs:97），我们追加
    /// onValueChanged 逐键刷新，零冲突。
    ///
    /// Unity 对象一律挂在 ConditionalWeakTable 的每视图 Holder 或预览自身的
    /// MonoBehaviour 上，静态字段只存补丁级标志（禁止静态字段持 Unity 对象）。
    /// </summary>
    internal static class LatexVanillaPreview
    {
        private sealed class Holder
        {
            internal LatexVanillaPreviewBinder Talk;
            internal LatexVanillaPreviewBinder Cg;
            /// <summary>本视图会话内发生过“源码回填进 curSelect.content”。</summary>
            internal bool SourceRestored;
        }

        private const string TalkPreviewName = "latex_preview_talk";
        private const string CgPreviewName = "latex_preview_cg";

        // 均为 prefab 实测值（prefabs_assets_mod bundle / ModEvtEditView@Mod）。
        private const float TalkPreviewWidth = 1260f;
        private const float TalkPreviewHeight = 74f;
        private const float TalkPreviewBottom = 4f;
        private const float CgPreviewWidth = 1642f;
        private const float CgPreviewHeight = 68f;
        private const float CgPreviewBottom = 4f;
        /// <summary>
        /// 预览字号上限。对话正文 prefab 实测 40，但预览带只有 60~66 的净高，
        /// 还要容纳一行 60% 字号的告警，故封到 34（字形与配色仍与玩家所见同源）。
        /// </summary>
        private const float MaxPreviewFontSize = 34f;

        private static readonly ConditionalWeakTable<ModEvtEditView, Holder> States =
            new ConditionalWeakTable<ModEvtEditView, Holder>();
        private static readonly HashSet<string> Warned = new HashSet<string>();

        /// <summary>InitUI Postfix 与 Select 的惰性重建共用入口。</summary>
        internal static void Attach(ModEvtEditView view)
        {
            if (view == null) return;
            TMP_FontAsset font = ResolveFont(view);
            if (font == null)
            {
                WarnOnce("font",
                    "取不到对话 TMP 字体，原版编辑器的行内公式预览已停用（烘焙不受影响）。");
                return;
            }

            Holder holder = GetOrCreate(view);
            holder.Talk = EnsurePreview(
                view.group_talk, TalkPreviewName, view.inputex_talk, font,
                TalkPreviewWidth, TalkPreviewHeight, TalkPreviewBottom);
            holder.Cg = EnsurePreview(
                view.mask_talk_cg, CgPreviewName, view.inputex_talk_cg, font,
                CgPreviewWidth, CgPreviewHeight, CgPreviewBottom);

            // 输入框必须显源码：richText 打开时 TMP 会把 <sup> 等标签直接渲染掉，
            // 作者就再也看不见自己写的东西。prefab 实测两个输入框 m_RichText=1，
            // 所以这里必然会翻成 false——顺带让原本也被吞掉的 <color> 标签现形。
            ForcePlainInput(view.inputex_talk, "inputex_talk");
            ForcePlainInput(view.inputex_talk_cg, "inputex_talk_cg");

            Refresh(holder);
        }

        /// <summary>Select Postfix：源码回填 + 预览显隐。</summary>
        internal static void OnSelect(ModEvtEditView view, TalkCfg cfg)
        {
            if (view == null) return;
            Holder holder;
            if (!States.TryGetValue(view, out holder)
                || holder.Talk == null || holder.Talk.Missing
                || holder.Cg == null || holder.Cg.Missing)
            {
                // 视图重开时预览节点随 prefab 一起销毁重建（照 ImagePreviewPatch
                // 的惰性重建），这里补一次即可。
                Attach(view);
                if (!States.TryGetValue(view, out holder)) return;
            }

            if (cfg != null) TryRestoreSource(view, holder, cfg);
            Refresh(holder);
        }

        /// <summary>
        /// 保存 Prefix 用：本视图是否已经把 $ 原文回填进内存。回填过而保存前
        /// 又烘焙不了，就必须整体拦下保存，否则原文会被当成正文写进 JSON。
        /// </summary>
        internal static bool HasRestoredSource(ModEvtEditView view)
        {
            Holder holder;
            return view != null && States.TryGetValue(view, out holder)
                   && holder.SourceRestored;
        }

        /// <summary>保存烘焙之后刷新一次预览（内存内容已从源码变成烘焙产物）。</summary>
        internal static void RefreshAfterSave(ModEvtEditView view)
        {
            Holder holder;
            if (view == null || !States.TryGetValue(view, out holder)) return;
            Refresh(holder);
        }

        private static void TryRestoreSource(
            ModEvtEditView view, Holder holder, TalkCfg cfg)
        {
            try
            {
                int eventId = EvtStoryGraphViewAccess.GetEventId(view);
                if (eventId <= 0 || cfg.id <= 0) return;
                // 借来的全局对象（Cfg.TalkCfgMap 里的那一个实例）不许写：回填等于把
                // 作者的 $ 源码塞进本局全局配置，本体台词当场变样（D5-4，与 D6-1 同类）。
                // 代价极小：条目一旦被保存进本 mod JSON 就成了自有对象，而在那之前
                // 边车里本来就没有它的源码，这条分支实际上够不到。
                if (LatexVanillaOwnership.IsBorrowed(cfg)) return;
                string modRoot;
                string error;
                if (!EvtStoryGraphViewAccess.TryGetModRoot(view, out modRoot, out error))
                    return;

                // 每次选中都重新读盘：剧情图窗口就在同一个视图上层打开，它保存后
                // 会改写同一份边车，缓存实例必然读到过期源码。文件很小，点击级开销。
                LatexSourceStore store = LatexSourceStore.Load(modRoot);
                string source = store.GetTalkSource(eventId, cfg.id);
                if (string.IsNullOrEmpty(source)) return;
                if (string.Equals(source, cfg.content, StringComparison.Ordinal)) return;
                // 只有“源码烘出来正好是当前内容”才敢换回；不一致说明烘焙文本被
                // 外部改过，此处以当前内容为准（过期条目留给剧情图会话去失效）。
                if (!string.Equals(
                        LatexInlineTranspiler.Bake(source).Baked, cfg.content,
                        StringComparison.Ordinal))
                    return;

                cfg.content = source;
                // 与原版 Select 同款赋值（ModEvtEditView.cs:470-472）：只会触发
                // onValueChanged（原版没挂），不会触发 onEndEdit，无副作用。
                if (view.inputex_talk != null) view.inputex_talk.text = source;
                if (view.inputex_talk_cg != null) view.inputex_talk_cg.text = source;
                holder.SourceRestored = true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[LatexVanillaPreview] 源码回填失败：" + e.Message);
            }
        }

        private static void Refresh(Holder holder)
        {
            if (holder == null) return;
            if (holder.Talk != null) holder.Talk.Refresh();
            if (holder.Cg != null) holder.Cg.Refresh();
        }

        private static Holder GetOrCreate(ModEvtEditView view)
        {
            Holder holder;
            if (States.TryGetValue(view, out holder)) return holder;
            holder = new Holder();
            States.Add(view, holder);
            return holder;
        }

        private static LatexVanillaPreviewBinder EnsurePreview(
            RectTransform parent, string name, TMP_InputField input,
            TMP_FontAsset font, float width, float height, float bottom)
        {
            if (parent == null || input == null) return null;

            Transform existing = parent.Find(name);
            GameObject root = existing != null ? existing.gameObject : null;
            if (root == null)
            {
                root = new GameObject(name,
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                root.transform.SetParent(parent, false);
                var background = root.GetComponent<Image>();
                background.color = new Color(0f, 0f, 0f, 0.55f);
                background.raycastTarget = false;

                var labelObject = new GameObject("txtex_latex_preview",
                    typeof(RectTransform));
                labelObject.transform.SetParent(root.transform, false);
                var labelRect = (RectTransform)labelObject.transform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(12f, 4f);
                labelRect.offsetMax = new Vector2(-12f, -4f);
                labelObject.AddComponent<TextMeshProUGUI>();
            }

            // 位置每次校正（照 EvtEditMiniGameSelectPatch 的双保险做法）。
            var rect = (RectTransform)root.transform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(0f, bottom);
            root.transform.SetAsLastSibling();

            var label = root.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null) return null;
            label.font = font;
            label.fontSize = ResolveFontSize(input);
            label.color = new Color(1f, 0.98f, 0.9f, 1f);
            label.richText = true;
            label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.overflowMode = TextOverflowModes.Truncate;
            label.enableWordWrapping = true;

            var binder = root.GetComponent<LatexVanillaPreviewBinder>();
            if (binder == null) binder = root.AddComponent<LatexVanillaPreviewBinder>();
            binder.Bind(input, label, root.GetComponent<Image>());
            return binder;
        }

        private static float ResolveFontSize(TMP_InputField input)
        {
            try
            {
                TMP_Text text = input != null ? input.textComponent : null;
                if (text != null && text.fontSize > 0f)
                    return Mathf.Clamp(text.fontSize, 20f, MaxPreviewFontSize);
            }
            catch { }
            return 30f;
        }

        private static void ForcePlainInput(TMP_InputField input, string label)
        {
            try
            {
                if (input == null || !input.richText) return;
                input.richText = false;
                Plugin.Log?.LogInfo(
                    "[LatexVanillaPreview] " + label
                    + ".richText 原为 true，已运行时关闭以显示 LaTeX 源码。");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[LatexVanillaPreview] 关闭 " + label + ".richText 失败：" + e.Message);
            }
        }

        private static TMP_FontAsset ResolveFont(ModEvtEditView view)
        {
            try
            {
                if (view.txtex_talk != null && view.txtex_talk.font != null)
                    return view.txtex_talk.font;
                if (view.txtex_talk_cg != null && view.txtex_talk_cg.font != null)
                    return view.txtex_talk_cg.font;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[LatexVanillaPreview] 读取对话字体失败：" + e.Message);
            }
            try { return TMP_Settings.defaultFontAsset; }
            catch { return null; }
        }

        private static void WarnOnce(string key, string message)
        {
            if (!Warned.Add(key)) return;
            Plugin.Log?.LogWarning("[LatexVanillaPreview] " + message);
        }
    }

    /// <summary>
    /// 预览标签自身的绑定器。挂在预览节点上，随节点一起销毁：监听器的注册与
    /// 反注册都由同一个实例负责，视图重开时不会累积重复监听（RemoveListener
    /// 按 target+method 比对，实例方法可精确摘除）。
    /// </summary>
    internal sealed class LatexVanillaPreviewBinder : MonoBehaviour
    {
        /// <summary>最多提示多少个缺字形字符。</summary>
        private const int MaxMissingGlyphs = 12;

        [NonSerialized] private TMP_InputField _input;
        [NonSerialized] private TextMeshProUGUI _label;
        [NonSerialized] private Image _background;

        /// <summary>标签或输入框已被销毁，需要重建（调用方先判 binder 本身是否已销毁）。</summary>
        internal bool Missing
        {
            get { return _label == null || _input == null; }
        }

        internal void Bind(TMP_InputField input, TextMeshProUGUI label, Image background)
        {
            if (_input != null && _input != input)
            {
                try { _input.onValueChanged.RemoveListener(OnInputChanged); }
                catch { }
            }
            _label = label;
            _background = background;
            if (_input != input)
            {
                _input = input;
                if (_input != null) _input.onValueChanged.AddListener(OnInputChanged);
            }
        }

        private void OnDestroy()
        {
            if (_input == null) return;
            try { _input.onValueChanged.RemoveListener(OnInputChanged); }
            catch { }
        }

        private void OnInputChanged(string value)
        {
            Refresh();
        }

        /// <summary>
        /// 逐键调用：纯字符串变换 + TMP 赋值，不防抖（设计 §6.1 同款判断）。
        /// 没有公式时整块隐藏，绝不在正常编辑时占位。
        /// </summary>
        internal void Refresh()
        {
            try
            {
                if (_label == null || _input == null) return;
                string source = _input.text;
                if (!LatexInlineTranspiler.ContainsLatex(source))
                {
                    SetVisible(false);
                    return;
                }

                LatexBakeResult result = LatexInlineTranspiler.Bake(source);
                string text = result.Baked ?? string.Empty;
                string warning = BuildWarning(result);
                if (warning != null)
                    text += "\n<size=60%><color=#FFB25C>注意：" + warning + "</color></size>";
                _label.text = text;
                SetVisible(true);
            }
            catch (Exception e)
            {
                // 预览是纯辅助，任何异常都不该影响编辑与保存。
                Plugin.Log?.LogWarning("[LatexVanillaPreview] 预览刷新失败：" + e.Message);
                SetVisible(false);
            }
        }

        private void SetVisible(bool visible)
        {
            if (_label != null && _label.enabled != visible) _label.enabled = visible;
            if (_background != null && _background.enabled != visible)
                _background.enabled = visible;
        }

        private string BuildWarning(LatexBakeResult result)
        {
            var parts = new List<string>();
            if (result != null && result.Warnings != null)
            {
                for (int i = 0; i < result.Warnings.Count && i < 2; i++)
                    parts.Add(result.Warnings[i]);
            }
            string missing = CollectMissingGlyphs(
                result != null ? result.Baked : null);
            if (!string.IsNullOrEmpty(missing))
                parts.Add("对话字体缺少字形 " + missing + "，玩家侧会显示成方块");
            if (parts.Count == 0) return null;
            return string.Join("；", parts.ToArray());
        }

        /// <summary>
        /// 对话字体是动态 atlas（运行时按需入册），所以用 tryAddCharacter 真探一次
        /// 源字体，比只查已入册表准确。探测失败不影响烘焙，也不打断编辑。
        /// </summary>
        private string CollectMissingGlyphs(string baked)
        {
            TMP_FontAsset font = _label != null ? _label.font : null;
            if (font == null || string.IsNullOrEmpty(baked)) return null;
            var missing = new StringBuilder();
            try
            {
                foreach (char c in LatexInlineTranspiler.CollectSpecialChars(baked))
                {
                    if (char.IsSurrogate(c)) continue;
                    if (font.HasCharacter(c, true, true)) continue;
                    if (missing.Length > 0) missing.Append(' ');
                    missing.Append(c);
                    if (missing.Length >= MaxMissingGlyphs) break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[LatexVanillaPreview] 字形探测失败：" + e.Message);
                return null;
            }
            return missing.Length > 0 ? missing.ToString() : null;
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class LatexVanillaPreviewInitPatch
    {
        private static void Postfix(ModEvtEditView __instance)
        {
            try { LatexVanillaPreview.Attach(__instance); }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[LatexVanillaPreviewInit] " + e);
            }
        }
    }

    [HarmonyPatch(typeof(ModEvtEditView), "Select", typeof(TalkCfg))]
    internal static class LatexVanillaPreviewSelectPatch
    {
        private static void Postfix(ModEvtEditView __instance, TalkCfg _cfg)
        {
            try { LatexVanillaPreview.OnSelect(__instance, _cfg); }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[LatexVanillaPreviewSelect] " + e);
            }
        }
    }

    /// <summary>
    /// 保底烘焙的补丁侧编排（设计 §6.2）：由 ModEvtDeleteSavePatch.Prefix 在
    /// 放行路径上调用——日志滞留被拦下时一律不烘焙，否则作者写的 $ 原文会在
    /// 保存并未真正发生的情况下被内存里替换成烘焙文本。
    ///
    /// 顺序上必须先烘焙、再走 BeforeOrdinarySave：后者的删除事务会把同一批
    /// talk 对象克隆后写盘，先烘焙才能保证两条写盘路径内容一致。
    /// </summary>
    internal static class LatexVanillaSaveBake
    {
        /// <summary>借来的对象返回烘焙用的克隆体，自有对象返回 null（就地烘焙）。</summary>
        private static TalkCfg CloneIfBorrowedTalk(TalkCfg talk)
        {
            if (talk == null) return null;
            return LatexVanillaOwnership.IsBorrowed(talk)
                ? StoryGraphEditSession.CloneTalk(talk)
                : null;
        }

        private static OptionCfg CloneIfBorrowedOption(OptionCfg option)
        {
            if (option == null) return null;
            return LatexVanillaOwnership.IsBorrowed(option)
                ? StoryGraphEditSession.CloneOption(option)
                : null;
        }

        /// <summary>返回 false 表示必须整体拦下本次保存。</summary>
        internal static bool TryBakeBeforeSave(ModEvtEditView view)
        {
            if (view == null) return true;
            List<TalkCfg> talks;
            Dictionary<int, OptionCfg> options;
            int eventId;
            List<int> entries;
            bool entriesKnown;
            string error;
            if (!EvtStoryGraphViewAccess.TrySnapshot(
                    view, out talks, out options, out eventId,
                    out entries, out entriesKnown, out error))
            {
                if (!LatexVanillaPreview.HasRestoredSource(view))
                {
                    Plugin.Log?.LogWarning(
                        "[LatexVanillaSaveBake] 读不到视图内存，跳过保底烘焙：" + error);
                    return true;
                }
                // 已经把 $ 原文回填进内存，放行等于把源码当正文写进配置。
                Warn("读不到事件数据，无法把 LaTeX 源码烘焙成成品文本，已阻止本次保存；"
                     + "请关闭并重新打开事件后再试。" );
                return false;
            }

            LatexBakeSweepResult sweep;
            try
            {
                sweep = LatexOrdinarySaveBaker.BakeInPlace(
                    talks, options, CloneIfBorrowedTalk, CloneIfBorrowedOption);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[LatexVanillaSaveBake] " + e);
                if (!LatexVanillaPreview.HasRestoredSource(view)) return true;
                Warn("LaTeX 烘焙异常，已阻止本次保存以免源码被当作正文写入："
                     + e.GetType().Name + ": " + e.Message);
                return false;
            }

            if (sweep.IsBlocked)
            {
                // 此时 BakeInPlace 一个字都没改（两段式），视图内存仍是作者原文，
                // 拦下保存不会丢任何东西；提交点尚未到达，属于合法中止。
                for (int i = 0; i < sweep.Blocked.Count; i++)
                    Plugin.Log?.LogWarning("[LatexVanillaSaveBake] 阻断：" + sweep.Blocked[i]);
                string extra = sweep.Blocked.Count > 1
                    ? "（另有 " + (sweep.Blocked.Count - 1) + " 处同类问题）" : string.Empty;
                Warn("已阻止本次保存：" + sweep.Blocked[0] + extra);
                return false;
            }

            PersistSidecar(view, eventId, sweep);
            Announce(sweep);
            LatexVanillaPreview.RefreshAfterSave(view);
            return true;
        }

        /// <summary>
        /// 边车写入 best-effort：失败只记日志，绝不把已经可以进行的保存判失败
        /// （committedOnDisk 纪律）。丢边车只失去“再编辑源码”，烘焙文本仍在。
        /// </summary>
        private static void PersistSidecar(
            ModEvtEditView view, int eventId, LatexBakeSweepResult sweep)
        {
            if (eventId <= 0)
            {
                if (sweep.TalkSources.Count > 0)
                    Plugin.Log?.LogWarning(
                        "[LatexVanillaSaveBake] 读不到事件编号，LaTeX 源码未写入边车。");
                return;
            }
            try
            {
                string modRoot;
                string error;
                if (!EvtStoryGraphViewAccess.TryGetModRoot(view, out modRoot, out error))
                {
                    if (sweep.TalkSources.Count > 0)
                        Plugin.Log?.LogWarning(
                            "[LatexVanillaSaveBake] 读不到 Mod 根目录，LaTeX 源码未写入边车："
                            + error);
                    return;
                }

                LatexSourceStore store = LatexSourceStore.Load(modRoot);
                LatexOrdinarySaveBaker.ApplyToSidecar(store, eventId, sweep);
                string saveError;
                if (!store.Save(out saveError))
                    Plugin.Log?.LogWarning("[LatexVanillaSaveBake] " + saveError);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[LatexVanillaSaveBake] 写入 LaTeX 源码边车失败：" + e.Message);
            }
        }

        private static void Announce(LatexBakeSweepResult sweep)
        {
            if (sweep.Changed)
            {
                string message = "已把 " + sweep.ChangedTalks + " 条对话、"
                                 + sweep.ChangedOptions + " 个选项里的 LaTeX 烘焙成成品文本"
                                 + "（源码保存在编辑器私有边车，可继续编辑）。";
                Plugin.Log?.LogInfo("[LatexVanillaSaveBake] " + message);
                Toast(message);
            }
            for (int i = 0; i < sweep.Warnings.Count; i++)
            {
                Plugin.Log?.LogWarning("[LatexVanillaSaveBake] " + sweep.Warnings[i]);
                if (i < 2) Toast(sweep.Warnings[i]);
            }
        }

        private static void Warn(string message)
        {
            Plugin.Log?.LogWarning("[LatexVanillaSaveBake] " + message);
            Toast(message);
        }

        private static void Toast(string message)
        {
            try { ToastHelper.Toast(message); }
            catch { }
        }
    }
}
