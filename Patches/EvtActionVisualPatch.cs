using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Config;
using GenUI.Common;
using GenUI.Mod;
using HarmonyLib;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using View.Evt;
using View.Mod;

namespace StudentAgeEditorPlus.Patches
{
    /// <summary>
    /// 改进：事件对话编辑器的"动作指令"缺少可视化。
    ///
    /// 作者反馈：动作指令只能填一串数字（如 1,3004,300），既看不出每个数字
    /// 是什么意思，也看不出人物移动 300 到底是多远，只能反复开剧情预览确认。
    ///
    /// 代码层面成因（反编译核实）：
    ///   - input_action 是纯文本框，内容为 TalkCfg.roles 的数字串序列化，
    ///     动作码含义（TalkAnimeDefine，30+ 种）编辑器完全不翻译；
    ///   - 编辑器小舞台（RefreshRoles/OnRenderRole）只解析进场（type 1）、
    ///     退场（type 2）和换装（3006），横移 3004 / 纵移 3008 等其余动作
    ///     在舞台上没有任何呈现，人物钉死在固定槽位；
    ///   - 原版预览只能从事件第一句开始播（PreviewTalkView.OnOpen 写死取
    ///     talkId[0]），调中间某句的动画要从头点过去。
    ///
    /// 本补丁做四件事（纯显示层，不改数据格式与存档）：
    ///   1. 动作翻译悬浮提示：鼠标移到动作指令输入框上时弹出提示框，
    ///      把数字串翻译成人话。迭代记录：初版是输入框下方常驻灰字，被紧邻
    ///      的"屏幕效果"框遮住（用户实测反馈）→ 改为悬浮；第二版向上弹出，
    ///      内容多时溢出屏幕上边缘（用户实测反馈）→ 改为向下弹出 + 屏幕
    ///      边缘保护（提示框置顶渲染，盖住下方控件无妨，鼠标正悬在输入框上）。
    ///   2. 屏幕效果悬浮提示：input_screeneffect 同样加悬浮翻译（用户要求）。
    ///   3. 舞台状态示意：沿编辑器选定的前驱链回放到当前对话结束，舞台上的
    ///      人物显示历史累计 + 当前句的横移/纵移及 3000 表情；退场重进、换景
    ///      或换方位时按真实播放器语义处理，分支合流沿 FindRoles 首个前驱解析。
    ///   4. "预览本句"按钮：先按唯一前驱路径建立当前句执行前的完整播放器快照
    ///      （人物/槽位/移动/外观/背景/滤镜/手机/CG/BGM），再播放当前原始动作；
    ///      历史动作不进入当前 Tween，避免并发错位。
    ///
    /// 关键换算：编辑器舞台 = 真实对话画面的 0.7 倍缩放
    ///（证据：真实同侧多人间距 300px（NewTalkView.UpdatePosRoles）
    ///  ↔ 编辑器槽位间距 210px；立绘 localScale 也是 0.7）。
    /// 因此舞台位移 = 指令偏移 × 0.7。
    /// </summary>
    internal static class TalkActionTranslator
    {
        /// <summary>动作/效果码 → 中文名（对照 Config.TalkAnimeDefine 硬编码，配置表无名称字段）。</summary>
        private static readonly Dictionary<int, string> ActionNames = new Dictionary<int, string>
        {
            { 1001, "放置进场" },
            { 1002, "淡入进场" },
            { 1003, "底部升起进场" },
            { 2001, "退场" },
            { 2002, "淡出退场" },
            { 3000, "换表情/姿势" },
            { 3001, "跳跃" },
            { 3002, "抖动" },
            { 3003, "放大" },
            { 3004, "横向移动" },
            { 3005, "转身" },
            { 3006, "换装" },
            { 3007, "瞬间转身" },
            { 3008, "纵向移动" },
            { 3009, "表情气泡" },
            { 3010, "摇头" },
            { 3011, "点头" },
            { 3012, "变剪影" },
            { 3013, "取消剪影" },
            { 3014, "换发型" },
            { 4001, "屏幕震动" },
            { 4002, "屏幕模糊" },
            { 4003, "清除屏幕效果" },
            { 4004, "屏幕物品" },
            { 4005, "屏幕表情" },
            { 4006, "等待" },
            { 4007, "打开手机界面" },
            { 4008, "关闭手机界面" },
            { 4009, "泛黄滤镜" },
            { 4010, "底片反色" },
            { 4011, "苏醒效果" },
            { 4012, "白屏闪光" },
            { 4013, "彩带庆祝" },
            { 4014, "礼物弹窗" },
            { 4015, "显示CG" },
            { 4016, "漫画分格" },
            { 4017, "关闭CG" },
            { 4018, "屏幕图片" },
            { 4019, "迷你CG" },
            { 5001, "纸条" },
            { 5002, "歌词" },
        };

        /// <summary>把整个 roles 列表翻译成多行"人名：动作(参数)"。</summary>
        public static string Translate(List<List<float>> roles, Dictionary<int, PersonCfg> personCfgs)
        {
            if (roles == null || roles.Count == 0) return null;

            var sb = new StringBuilder();
            foreach (var entry in roles)
            {
                if (entry == null || entry.Count == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(TranslateEntry(entry, personCfgs));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// 翻译屏幕效果字段（screenEffect：[效果码, 参数...]，一条对话只有一条）。
        /// 参数语义对照 NewTalkView 中对 cfg.screenEffect 的各分支处理。
        /// </summary>
        public static string TranslateScreenEffect(List<float> se)
        {
            if (se == null || se.Count == 0) return null;

            int code = (int)se[0];
            if (!ActionNames.TryGetValue(code, out string name))
                return $"未知效果({code})";

            float P(int i) => se.Count > 1 + i ? se[1 + i] : float.NaN;
            bool Has(int i) => se.Count > 1 + i;

            var parts = new List<string>();
            switch (code)
            {
                case 4001: // 震动：[次数]
                    parts.Add($"{(Has(0) ? Mathf.Max((int)P(0), 1) : 1)}次");
                    break;
                case 4002: // 模糊：[程度0~1]
                    if (Has(0)) parts.Add($"程度{Num(P(0))}");
                    break;
                case 4004: // 屏幕物品：[物品/书籍编号, 消息图编号]
                    if (Has(0)) parts.Add($"物品/书籍{(int)P(0)}");
                    if (Has(1)) parts.Add($"消息图{(int)P(1)}");
                    break;
                case 4007: // 手机界面：[背景编号, 人物编号...]
                    if (Has(0)) parts.Add($"背景{(int)P(0)}");
                    for (int i = 1; Has(i); i++) parts.Add($"人物{(int)P(i)}");
                    break;
                case 4015: // 显示CG / 迷你CG：[CG编号]
                case 4019:
                    if (Has(0)) parts.Add($"CG编号{(int)P(0)}");
                    break;
                case 4016: // 漫画分格：[漫画编号, 图数]
                    if (Has(0)) parts.Add($"漫画{(int)P(0)}");
                    if (Has(1)) parts.Add($"图数{(int)P(1)}");
                    break;
                case 4018: // 屏幕图片：[背景图编号]
                    if (Has(0)) parts.Add($"图片编号{(int)P(0)}");
                    break;
                default:
                    for (int i = 0; Has(i); i++) parts.Add(Num(P(i)));
                    break;
            }
            return parts.Count > 0 ? $"{name} ({string.Join("、", parts)})" : name;
        }

        private static string TranslateEntry(List<float> entry, Dictionary<int, PersonCfg> personCfgs)
        {
            int personId = (int)entry[0];
            string person = ResolvePersonName(personId, personCfgs);

            if (entry.Count < 2)
                return $"{person}：（指令不完整）";

            int actionId = (int)entry[1];
            if (!ActionNames.TryGetValue(actionId, out string actionName))
                return $"{person}：未知动作({actionId})";

            string parms = DescribeParams(actionId, entry);
            return string.IsNullOrEmpty(parms)
                ? $"{person}：{actionName}"
                : $"{person}：{actionName} {parms}";
        }

        /// <summary>
        /// 按动作类型解释参数。参数语义对照 NewTalkView.HelpCheckRoleAction
        ///（反编译 L2440-2716），只解释高频动作，其余原样列出数字。
        /// </summary>
        private static string DescribeParams(int actionId, List<float> entry)
        {
            // entry: [人物ID, 动作码, p0, p1, ...]
            float P(int i) => entry.Count > 2 + i ? entry[2 + i] : float.NaN;
            bool Has(int i) => entry.Count > 2 + i;

            var parts = new List<string>();
            switch (actionId)
            {
                case 1001: // 放置：[层级, 方位, 延迟, 入场方向, 抖动]
                case 1002: // 淡入：[层级, 方位, 延迟]
                case 1003: // 底部升起：[层级, 方位, 延迟]
                    if (Has(0) && (int)P(0) != 0) parts.Add($"层级{(int)P(0)}");
                    if (Has(1)) parts.Add($"站{AxisName((int)P(1))}");
                    if (Has(2) && P(2) > 0f) parts.Add($"延迟{Num(P(2))}秒");
                    break;
                case 2001: // 退场：[退场方位, 延迟]
                    if (Has(0) && (int)P(0) != 0) parts.Add($"从{AxisName((int)P(0))}退");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 2002: // 淡出：[延迟]
                    if (Has(0) && P(0) > 0f) parts.Add($"延迟{Num(P(0))}秒");
                    break;
                case 3004: // 横移：[偏移, 延迟, 抖动时长, 移动用时]
                    parts.Add(Has(0) ? $"向{(P(0) >= 0f ? "右" : "左")}{Num(Mathf.Abs(P(0)))}" : "偏移0");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    if (Has(2) && P(2) > 0f) parts.Add($"抖动{Num(P(2))}秒");
                    if (Has(3) && P(3) > 0f) parts.Add($"用时{Num(P(3))}秒");
                    break;
                case 3008: // 纵移：[偏移, 延迟, 抖动时长]
                    parts.Add(Has(0) ? $"向{(P(0) >= 0f ? "上" : "下")}{Num(Mathf.Abs(P(0)))}" : "偏移0");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    if (Has(2) && P(2) > 0f) parts.Add($"抖动{Num(P(2))}秒");
                    break;
                case 3001: // 跳跃：[次数, 延迟, 力度]
                    if (Has(0)) parts.Add($"{Mathf.Max((int)P(0), 1)}次");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    if (Has(2)) parts.Add($"力度{Num(P(2))}");
                    break;
                case 3002: // 抖动：[时长, 延迟]
                    if (Has(0)) parts.Add($"{Num(P(0))}秒");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 3003: // 放大：[倍数(0=默认1.1), 延迟]
                    parts.Add($"{Num(Has(0) && P(0) != 0f ? P(0) : 1.1f)}倍");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 3000: if (Has(0)) parts.Add($"表情/姿势{(int)P(0)}"); break;
                case 3006: if (Has(0)) parts.Add($"服装{(int)P(0)}"); break;
                case 3014: if (Has(0)) parts.Add($"发型{(int)P(0)}"); break;
                case 3009: // 表情气泡：[编号, 延迟]
                    if (Has(0)) parts.Add($"编号{(int)P(0)}");
                    if (Has(1) && P(1) > 0f) parts.Add($"延迟{Num(P(1))}秒");
                    break;
                case 3005: // 转身：[延迟]
                case 3007:
                    if (Has(0) && P(0) > 0f) parts.Add($"延迟{Num(P(0))}秒");
                    break;
                default:
                    // 其余动作：原样列出参数，至少能对上动作名
                    for (int i = 0; Has(i); i++) parts.Add(Num(P(i)));
                    break;
            }
            return parts.Count > 0 ? $"({string.Join("、", parts)})" : null;
        }

        /// <summary>
        /// 累加某人物在当前对话中所有横移/纵移指令的偏移量（真实画面像素）。
        /// 供舞台位移示意使用。
        /// </summary>
        public static Vector2 GetTalkOffset(TalkCfg talk, int personId)
        {
            var offset = Vector2.zero;
            if (talk?.roles == null) return offset;

            foreach (var entry in talk.roles)
            {
                if (entry == null || entry.Count < 3) continue;
                if ((int)entry[0] != personId) continue;
                int actionId = (int)entry[1];
                if (actionId == 3004) offset.x += entry[2];
                else if (actionId == 3008) offset.y += entry[2];
            }
            return offset;
        }

        private static string ResolvePersonName(int personId, Dictionary<int, PersonCfg> personCfgs)
        {
            if (personCfgs != null && personCfgs.TryGetValue(personId, out var cfg) &&
                !string.IsNullOrEmpty(cfg?.name))
                return cfg.name;
            return $"人物{personId}";
        }

        /// <summary>TalkAxis：1=左 2=右 3=中（View.Evt.TalkAxis 枚举，已核实）。</summary>
        private static string AxisName(int axis)
        {
            switch (axis)
            {
                case 1: return "左侧";
                case 2: return "右侧";
                case 3: return "中间";
                case -1: return "场外";
                default: return $"方位{axis}";
            }
        }

        /// <summary>数字格式化：去掉多余小数位（300 而非 300.0）。</summary>
        private static string Num(float v) => v.ToString("0.##");
    }

    /// <summary>
    /// 为编辑器内预览加载当前 Mod 自己的辅助配置表。
    ///
    /// PreviewTalkView 的 3000 表情动作对图片型人物依赖 ModFaceCfg；若调用方
    /// 不传第 10 个参数，它只会回退到游戏当前已加载的全局表，尚在编辑的 Mod
    /// 自定义表情通常不在其中。原版 ModPreviewTipsView 会从当前 Mod 目录加载
    /// face/item/book 三张表并传满 12 个参数，这里复用相同语义，同时在文件缺失
    /// 或格式错误时安全回退全局表，不能让一张辅助表阻断整段剧情预览。
    /// </summary>
    internal static class EvtPreviewConfigLoader
    {
        private sealed class ViewConfigCache
        {
            public string ModRoot;
            public string Language;
            public string FacePath;
            public DateTime FaceWriteTimeUtc;
            public Dictionary<int, ModFaceCfg> Faces;
        }

        private static readonly ConditionalWeakTable<ModEvtEditView, ViewConfigCache> ViewCaches = new();

        /// <summary>供右侧静态小舞台使用；同一编辑器视图生命周期只读盘一次。</summary>
        internal static Dictionary<int, ModFaceCfg> GetFaces(ModEvtEditView view)
        {
            if (view == null) return Merge(null, Cfg.ModFaceCfgMap);

            ViewConfigCache cache = ViewCaches.GetValue(view, _ => new ViewConfigCache());
            string modRoot = null;
            try
            {
                modRoot = Traverse.Create(view).Field("modRoot").GetValue<string>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[EvtPreviewConfig] 无法读取当前 Mod 路径，表情回退全局配置: {e.Message}");
            }

            string language = LocalizationMgr.Lang;
            string facePath = string.IsNullOrEmpty(modRoot)
                ? null
                : Path.Combine(modRoot, "Cfgs/" + language, "ModFaceCfg.json");
            DateTime writeTime = facePath != null && File.Exists(facePath)
                ? File.GetLastWriteTimeUtc(facePath)
                : DateTime.MinValue;

            if (cache.Faces == null
                || !string.Equals(cache.ModRoot, modRoot, StringComparison.Ordinal)
                || !string.Equals(cache.Language, language, StringComparison.Ordinal)
                || !string.Equals(cache.FacePath, facePath, StringComparison.Ordinal)
                || cache.FaceWriteTimeUtc != writeTime)
            {
                cache.ModRoot = modRoot;
                cache.Language = language;
                cache.FacePath = facePath;
                cache.FaceWriteTimeUtc = writeTime;
                cache.Faces = LoadFaces(modRoot);
            }
            return cache.Faces;
        }

        internal static Dictionary<int, ModFaceCfg> LoadFaces(string modRoot)
        {
            return LoadMerged(modRoot, "ModFaceCfg.json", Cfg.ModFaceCfgMap, cfg => cfg.id);
        }

        internal static Dictionary<int, BgCfg> LoadBackgrounds(string modRoot)
        {
            return LoadMerged(modRoot, "BgCfg.json", Cfg.BgCfgMap, cfg => cfg.id);
        }

        internal static Dictionary<int, CGCfg> LoadCgs(string modRoot)
        {
            return LoadMerged(modRoot, "CGCfg.json", Cfg.CGCfgMap, cfg => cfg.id);
        }

        internal static Dictionary<int, AudioCfg> LoadAudios(string modRoot)
        {
            return LoadMerged(modRoot, "AudioCfg.json", Cfg.AudioCfgMap, cfg => cfg.id);
        }

        internal static Dictionary<int, EvtCfg> LoadEvents(string modRoot)
        {
            return LoadMerged(modRoot, "EvtCfg.json", Cfg.EvtCfgMap, cfg => cfg.id);
        }

        internal static Dictionary<int, ItemCfg> LoadItems(string modRoot)
        {
            return LoadMerged(modRoot, "ItemCfg.json", Cfg.ItemCfgMap, cfg => cfg.id);
        }

        internal static Dictionary<int, BookCfg> LoadBooks(string modRoot)
        {
            return LoadMerged(modRoot, "BookCfg.json", Cfg.BookCfgMap, cfg => cfg.id);
        }

        private static Dictionary<int, T> LoadMerged<T>(
            string modRoot,
            string fileName,
            Dictionary<int, T> global,
            Func<T, int> getId)
            where T : class
        {
            var result = new Dictionary<int, T>();
            if (!string.IsNullOrEmpty(modRoot))
            {
                string path = Path.Combine(
                    modRoot, "Cfgs/" + LocalizationMgr.Lang, fileName);
                if (File.Exists(path))
                {
                    try
                    {
                        Dictionary<string, T> local = ModCtrl.DeserializeJsonToCfgMap<T>(path);
                        if (local != null)
                        {
                            foreach (KeyValuePair<string, T> entry in local)
                            {
                                if (entry.Value == null) continue;
                                result[getId(entry.Value)] = entry.Value;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning(
                            $"[EvtPreviewConfig] 读取 {fileName} 失败，预览将回退全局配置: {e.Message}");
                    }
                }
            }

            if (global != null)
            {
                foreach (KeyValuePair<int, T> entry in global)
                {
                    // 当前 Mod 的同 ID 配置优先，与原版 ModPreviewTipsView 一致。
                    if (!result.ContainsKey(entry.Key)) result.Add(entry.Key, entry.Value);
                }
            }
            return result;
        }

        private static Dictionary<int, T> Merge<T>(
            Dictionary<int, T> local,
            Dictionary<int, T> global)
        {
            var result = new Dictionary<int, T>();
            if (local != null)
            {
                foreach (KeyValuePair<int, T> entry in local) result[entry.Key] = entry.Value;
            }
            if (global != null)
            {
                foreach (KeyValuePair<int, T> entry in global)
                {
                    if (!result.ContainsKey(entry.Key)) result.Add(entry.Key, entry.Value);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// 以单次资源请求渲染编辑器预览人物。原 TalkRoleItem.SetData 的图片分支只能
    /// 使用全局 ModFaceCfg；若先让它加载默认脸、再在 Postfix 改成当前 Mod 表情，
    /// 两个异步请求会竞态，后完成的旧默认图可能覆盖表情。本 helper 直接选择最终
    /// URL；L2D 也从首次 SetL2D 就传入正确表情和剪影颜色。
    /// </summary>
    internal static class PreviewRoleRenderer
    {
        internal static void Render(
            PersonCfg person,
            Cell_NewTalkRoleItemUI cell,
            int order,
            int colorId,
            float alpha,
            int cloth,
            int pose,
            bool flip,
            int hair,
            int gradeState,
            GenderDefine gender,
            Dictionary<int, ModFaceCfg> faces)
        {
            if (person == null || cell == null) return;
            Color color = Singleton<ColorCtrl>.Ins.Get(colorId);
            cell.canvasgroup_role.alpha = alpha;

            if (person.IsUseImg(gradeState, cloth))
            {
                cell.l2d_role.gameObject.SetActive(false);
                string expressionUrl = RoleMgr.GetExpressionIcon(
                    person, cloth, Mathf.Max(0, pose), gradeState, faces);
                string defaultUrl = person.GetFullIcon(cloth, gender, gradeState);
                // ModFaceCfg 记录存在但外部文件已被移动/删除时，不要发起一个
                // 注定失败的表情请求；回退该服装默认立绘。默认图也缺失时，
                // 请求守卫会清空旧 Sprite，绝不把上一人物/上一表情留在画面上。
                string url = IsMissingExternalFile(expressionUrl)
                    ? defaultUrl
                    : (string.IsNullOrEmpty(expressionUrl) ? defaultUrl : expressionUrl);
                cell.icon_role.SetTextureUrl(url);

                cell.icon_role.gameObject.SetActive(true);
                cell.icon_role.image.color = color;
                cell.canvasgroup_role.alpha = alpha;
                var (x, y, scale) = person.GetUrlParm(gradeState, gender);
                cell.icon_role.transform.localScale = new Vector3(
                    scale * (flip ? -1f : 1f), scale, scale);
                cell.icon_role.transform.SetPosX(x);
                cell.icon_role.transform.SetPosY(y);
            }
            else
            {
                cell.icon_role.gameObject.SetActive(false);
                cell.l2d_role.gameObject.SetActive(true);
                int expression = pose >= 0
                    ? RoleMgr.GetExpression(person.id, pose, gradeState, gender)
                    : -1;
                cell.l2d_role.SetL2D(
                    person.id, order, false, alpha, cloth, flip ? 1 : 0,
                    ColorCtrl.GetColorStr(colorId), expression, hair,
                    gradeState, gender, L2DLoadType.New);
            }

            if ((person.bubbleParm != null && person.bubbleParm.Count > 0)
                || (person.bubbleParm2 != null && person.bubbleParm2.Count > 0))
            {
                cell.img_bubble_face.rectTransform.anchoredPosition =
                    person.GetBubblePos(gradeState, gender);
            }
            else
            {
                cell.img_bubble_face.rectTransform.anchoredPosition =
                    new Vector2(0f, 1150f);
            }
        }

        private static bool IsMissingExternalFile(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string fullPath = null;
            if (Path.IsPathRooted(url))
                fullPath = url;
            else if (url.StartsWith("Mods", StringComparison.Ordinal))
                fullPath = Singleton<ModCtrl>.Ins.GetFullUrl(url);
            return fullPath != null && !File.Exists(fullPath);
        }
    }

    /// <summary>
    /// PreviewTalkView 和编辑器静态小舞台的图片人物在进场默认脸、3000 表情、
    /// 3006 服装或切换 Talk 时可能连续发起多个异步图片请求；原 UISprite 回调不
    /// 校验 URL，旧请求晚到会覆盖新状态。仅为这两个明确的角色预览池注册请求
    /// 代次，保证始终只有“最后一次有效请求”可以写回图片；其它 UI 不受影响。
    /// </summary>
    internal static class PreviewSpriteRequestGuard
    {
        private sealed class RequestState
        {
            public int Generation;
        }

        private static readonly ConditionalWeakTable<UISprite, RequestState> States = new();
        private static readonly FieldInfo UrlField =
            AccessTools.Field(typeof(UISprite), "<url>k__BackingField");

        internal static void Register(UISprite sprite)
        {
            if (sprite != null) States.GetValue(sprite, _ => new RequestState());
        }

        internal static bool TrySetTexture(
            UISprite sprite, string requestedUrl, bool showWhenComplete)
        {
            if (sprite == null || !States.TryGetValue(sprite, out RequestState state))
                return false;

            sprite.showWhenComp = showWhenComplete;
            if (requestedUrl == null)
            {
                // 清空也是一次新请求：必须让此前尚未完成的加载全部失效。
                unchecked { ++state.Generation; }
                SetUrl(sprite, null);
                if (sprite.image != null) sprite.image.sprite = null;
                return true;
            }

            bool external = false;
            string expected = requestedUrl;
            if (!string.IsNullOrEmpty(requestedUrl))
            {
                if (Path.IsPathRooted(requestedUrl))
                {
                    external = true;
                }
                else if (requestedUrl.StartsWith("Mods", StringComparison.Ordinal))
                {
                    external = true;
                    expected = Singleton<ModCtrl>.Ins.GetFullUrl(requestedUrl);
                }
            }
            if (!external)
            {
                // ResPath 是游戏程序集 internal，插件无法直接调用；其当前
                // ToTextureUrl 实现就是同一 Path.Combine("Textures/", url)。
                expected = Path.Combine(
                    "Textures/", LocalizationMgr.GetLocalizeUrl(requestedUrl));
            }

            return Start(sprite, state, expected, external, showWhenComplete,
                isAtlas: false);
        }

        internal static bool TrySetAtlas(
            UISprite sprite, string requestedUrl, bool showWhenComplete)
        {
            if (sprite == null || !States.TryGetValue(sprite, out RequestState state))
                return false;

            // 角色池实际使用非空占位 atlas；null/empty 沿用原 UISprite 行为，
            // 避免扩大补丁语义面。非空 atlas 仍会推进同一代次并淘汰旧贴图请求。
            if (string.IsNullOrEmpty(requestedUrl)) return false;
            sprite.showWhenComp = showWhenComplete;

            bool external = false;
            string expected = requestedUrl;
            if (!string.IsNullOrEmpty(requestedUrl))
            {
                if (Path.IsPathRooted(requestedUrl))
                {
                    external = true;
                }
                else if (requestedUrl.StartsWith("Mods", StringComparison.Ordinal))
                {
                    external = true;
                    expected = Singleton<ModCtrl>.Ins.GetFullUrl(requestedUrl);
                }
            }

            return Start(sprite, state, expected, external, showWhenComplete,
                isAtlas: true);
        }

        private static bool Start(
            UISprite sprite,
            RequestState state,
            string expected,
            bool external,
            bool showWhenComplete,
            bool isAtlas)
        {
            // 缺文件同样代表“最新状态已经改变”：必须先推进代次、清掉旧图，
            // 否则上一请求晚到后仍会把上一人物/表情写回当前槽位。
            if (external && !File.Exists(expected))
            {
                unchecked { ++state.Generation; }
                SetUrl(sprite, null);
                if (sprite.image != null)
                {
                    sprite.image.sprite = null;
                    sprite.image.gameObject.SetActive(false);
                }
                Plugin.Log.LogWarning($"[PreviewSpriteGuard] 找不到图片: {expected}");
                return true;
            }

            if (string.Equals(sprite.url, expected, StringComparison.Ordinal))
            {
                if (showWhenComplete && sprite.image != null)
                    sprite.image.gameObject.SetActive(true);
                return true;
            }

            // 只有真的开始新加载时才推进代次。同 URL 去重不能推进，
            // 否则第一次仍在途的合法回调会被自己判旧，图片将永远不落盘。
            int generation = unchecked(++state.Generation);
            SetUrl(sprite, expected);
            if (sprite.image != null && showWhenComplete)
            {
                // 精确保持 UISprite 原语义：内部资源请求开始时立即显示；
                // 外部文件请求开始时先隐藏，完成后 SetSprite 再显示。
                // showWhenComplete=false 时原方法不会主动改变现有显隐状态。
                sprite.image.gameObject.SetActive(!external);
            }

            Action<Sprite> completed = loaded =>
            {
                if (!States.TryGetValue(sprite, out RequestState current)
                    || !ReferenceEquals(current, state)
                    || current.Generation != generation
                    || !string.Equals(sprite.url, expected, StringComparison.Ordinal))
                    return;

                if (loaded == null)
                {
                    Plugin.Log.LogWarning($"[PreviewSpriteGuard] 找不到图片: {expected}");
                    SetUrl(sprite, null);
                    if (sprite.image != null)
                    {
                        sprite.image.sprite = null;
                        sprite.image.gameObject.SetActive(false);
                    }
                    return;
                }
                sprite.SetSprite(loaded);
            };

            if (external)
                ResMgr.LoadExternSpriteAsync(expected, completed, false);
            else if (isAtlas)
                AtlasMgr.GetSpriteAsync(expected, completed);
            else
                ResMgr.LoadSpriteAsync(expected, completed);
            return true;
        }

        private static void SetUrl(UISprite sprite, string value)
        {
            if (UrlField != null) UrlField.SetValue(sprite, value);
            else Traverse.Create(sprite).Property("url").SetValue(value);
        }
    }

    [HarmonyPatch(typeof(PreviewTalkView), "OnRenderRole", typeof(UICell))]
    internal static class PreviewTalkSpriteGuardInitPatch
    {
        private static bool Prefix(PreviewTalkView __instance, UICell _cell)
        {
            try
            {
                if (_cell is Cell_NewTalkRoleItemUI cell)
                {
                    PreviewSpriteRequestGuard.Register(cell.icon_role);
                    // 启动快照人物必须第一次就用最终服装/表情渲染，不能先发默认脸
                    // 请求再覆盖；否则两个异步加载仍可能竞态。
                    if (TalkPreviewBootstrapRuntime.TryRenderInjectedRole(__instance, cell))
                        return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PreviewSpriteGuard.OnRenderRole] {e}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(UISprite), nameof(UISprite.SetTextureUrl),
        new[] { typeof(string), typeof(bool) })]
    internal static class PreviewTextureRequestGuardPatch
    {
        private static bool Prefix(UISprite __instance, string _url, bool _showWhenComp)
        {
            try
            {
                return !PreviewSpriteRequestGuard.TrySetTexture(
                    __instance, _url, _showWhenComp);
            }
            catch (Exception e)
            {
                // 请求保护本身绝不能阻断图片加载；异常时放行游戏原方法。
                Plugin.Log.LogError($"[PreviewSpriteGuard.Texture] {e}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(UISprite), nameof(UISprite.SetAtlasUrl),
        new[] { typeof(string), typeof(bool) })]
    internal static class PreviewAtlasRequestGuardPatch
    {
        private static bool Prefix(UISprite __instance, string _url, bool _showWhenComp)
        {
            try
            {
                return !PreviewSpriteRequestGuard.TrySetAtlas(
                    __instance, _url, _showWhenComp);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[PreviewSpriteGuard.Atlas] {e}");
                return true;
            }
        }
    }

    /// <summary>
    /// 功能 1+2：动作指令 / 屏幕效果的悬浮翻译。
    /// InitUI 时给两个输入框注册鼠标进入/移出回调（游戏自带的
    /// Sdk.AddMouseEnter/AddMouseExit 扩展，原版未占用这两个输入框的回调）：
    /// 悬浮 → 输入框下方弹出提示框显示翻译（置顶渲染 + 屏幕边缘保护）；
    /// 移出 → 隐藏。onEndEdit 后若提示框正显示着，内容同步刷新。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtActionHintInitPatch
    {
        private const string TooltipObjName = "EvtActionTooltip";

        /// <summary>提示框（挂在编辑器根节点下，视图关闭时随之销毁）。</summary>
        private static GameObject _tooltip;
        private static Text _tooltipText;

        /// <summary>提示框当前锚定的输入框（null = 未显示），用于 onEndEdit 时判断是否要刷新内容。</summary>
        private static InputField _hoverInput;

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var view = __instance;
                var actionInput = view.input_action;
                var screenInput = view.input_screeneffect;

                // ── 动作指令：悬浮翻译 + 编辑后刷新舞台位移 ──
                if (actionInput != null)
                {
                    actionInput.gameObject.AddMouseEnter(() => ShowFor(view, actionInput));
                    actionInput.gameObject.AddMouseExit(HideTooltip);

                    // 追加监听（原版 OnEndEditAction 已先注册，UnityEvent 按注册
                    // 顺序执行，这里拿到的是解析后的最新 roles）
                    actionInput.onEndEdit.AddListener(_ =>
                    {
                        try
                        {
                            // 原版编辑动作指令后不刷新舞台，这里补上，
                            // 让位移示意（EvtStageOffsetPatch）即时生效
                            var curSelect = Traverse.Create(view).Field("curSelect").GetValue<TalkCfg>();
                            if (curSelect != null)
                                Traverse.Create(view).Method("RefreshRoles").GetValue();

                            if (_hoverInput == actionInput)
                                ShowFor(view, actionInput);
                        }
                        catch (Exception e) { Plugin.Log.LogError($"[EvtActionTooltip.onEndEdit] {e}"); }
                    });
                }

                // ── 屏幕效果：悬浮翻译 ──
                if (screenInput != null)
                {
                    screenInput.gameObject.AddMouseEnter(() => ShowFor(view, screenInput));
                    screenInput.gameObject.AddMouseExit(HideTooltip);
                    screenInput.onEndEdit.AddListener(_ =>
                    {
                        try
                        {
                            if (_hoverInput == screenInput)
                                ShowFor(view, screenInput);
                        }
                        catch (Exception e) { Plugin.Log.LogError($"[EvtScreenTooltip.onEndEdit] {e}"); }
                    });
                }

                Plugin.Log.LogInfo("[EvtActionVisual] 动作指令/屏幕效果悬浮翻译已挂载。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtActionHintInit] {e}");
            }
        }

        /// <summary>切换选中对话（含删除后 Select(null)）时隐藏提示框，避免残留旧内容。</summary>
        internal static void OnSelectChanged()
        {
            HideTooltip();
        }

        private static void ShowFor(ModEvtEditView view, InputField input)
        {
            try
            {
                var t = Traverse.Create(view);
                var curSelect = t.Field("curSelect").GetValue<TalkCfg>();
                if (curSelect == null) return;

                string text;
                try
                {
                    if (input == view.input_action)
                    {
                        var personCfgs = t.Field("personCfgs").GetValue<Dictionary<int, PersonCfg>>();
                        text = TalkActionTranslator.Translate(curSelect.roles, personCfgs)
                               ?? "（当前对话没有动作指令）";
                    }
                    else
                    {
                        text = TalkActionTranslator.TranslateScreenEffect(curSelect.screenEffect)
                               ?? "（当前对话没有屏幕效果）";
                    }
                }
                catch (Exception)
                {
                    // 数字串解析出问题不应打断编辑器
                    text = "内容格式有误，无法解析";
                }

                var tip = GetOrCreateTooltip(view);
                if (tip == null) return;

                _tooltipText.text = text;

                // 背景按文字实际尺寸收缩（preferredWidth/Height 由 TextGenerator
                // 直接计算，不依赖布局组件——动态对象上 ContentSizeFitter 不稳定）
                var bgRt = (RectTransform)tip.transform;
                bgRt.sizeDelta = new Vector2(
                    _tooltipText.preferredWidth + 24f,
                    _tooltipText.preferredHeight + 16f);

                PositionBelow(bgRt, input);

                tip.transform.SetAsLastSibling(); // 置顶渲染，盖在所有控件之上
                tip.SetActive(true);
                _hoverInput = input;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtActionTooltip.Show] {e}");
            }
        }

        /// <summary>
        /// 提示框定位：贴在输入框左下角的下方 6px（向下弹出——上一版向上弹出
        /// 时内容多会溢出屏幕上边缘）。再按编辑器根节点的矩形做边缘保护：
        /// 底部放不下就上抬，右侧超界就左移。全程在根节点本地坐标系计算，
        /// 不受分辨率与画布缩放影响。
        /// </summary>
        private static void PositionBelow(RectTransform bgRt, InputField input)
        {
            var rootRt = bgRt.parent as RectTransform;
            if (rootRt == null) return;

            var inputRt = (RectTransform)input.transform;
            var corners = new Vector3[4];
            inputRt.GetWorldCorners(corners); // 0=左下 1=左上 2=右上 3=右下

            var canvas = input.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera : null;

            Vector2 screenPt = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRt, screenPt, cam, out var local);
            local.y -= 6f;

            // 边缘保护（提示框 pivot 为左上角）
            Rect rr = rootRt.rect;
            Vector2 size = bgRt.sizeDelta;
            if (local.y - size.y < rr.yMin) local.y = rr.yMin + size.y; // 底部放不下 → 上抬
            if (local.x + size.x > rr.xMax) local.x = rr.xMax - size.x; // 右侧超界 → 左移
            if (local.x < rr.xMin) local.x = rr.xMin;

            bgRt.localPosition = new Vector3(local.x, local.y, 0f);
        }

        private static void HideTooltip()
        {
            _hoverInput = null;
            if (_tooltip != null)
                _tooltip.SetActive(false);
        }

        /// <summary>
        /// 提示框 = 深色半透明背景 Image + 文字 Text，挂在编辑器根节点下
        ///（跟随视图销毁）。整体不参与鼠标点击判定，避免遮住输入框后
        /// 触发"移出"回调造成闪烁。
        /// </summary>
        private static GameObject GetOrCreateTooltip(ModEvtEditView view)
        {
            if (_tooltip != null) return _tooltip;

            // group_content 的父节点即编辑器视图根节点
            var root = view.group_content != null ? view.group_content.parent : null;
            if (root == null) return null;

            var existing = root.Find(TooltipObjName);
            if (existing != null)
            {
                _tooltip = existing.gameObject;
                _tooltipText = existing.GetComponentInChildren<Text>();
                return _tooltip;
            }

            var go = new GameObject(TooltipObjName, typeof(RectTransform), typeof(Image));
            var bgRt = go.GetComponent<RectTransform>();
            bgRt.SetParent(root, false);
            bgRt.pivot = new Vector2(0f, 1f); // 左上角为基准，向右下展开
            bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0.5f);

            var bg = go.GetComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.1f, 0.95f);
            bg.raycastTarget = false;

            var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.SetParent(bgRt, false);
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(12f, 8f);
            textRt.offsetMax = new Vector2(-12f, -8f);

            var refText = view.input_action != null ? view.input_action.textComponent : null;
            var txt = textGo.GetComponent<Text>();
            txt.font = refText != null ? refText.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = refText != null ? refText.fontSize : 18;
            txt.alignment = TextAnchor.UpperLeft;
            txt.color = new Color(0.92f, 0.92f, 0.92f, 1f);
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;

            go.SetActive(false);
            _tooltip = go;
            _tooltipText = txt;
            return _tooltip;
        }
    }

    /// <summary>
    /// 路径或清场相关字段编辑完成后立即重算小舞台。原版这些处理函数只修改配置/
    /// 背景图，不会调用 RefreshRoles；若不补这一层，累计状态要等重新选择 Talk 才刷新。
    /// 动作输入框由 EvtActionHintInitPatch 自己的 onEndEdit 监听处理，避免重复刷新。
    /// </summary>
    [HarmonyPatch]
    internal static class EvtStageGraphEditRefreshPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            string[] names =
            {
                "OnEndEditCurBg",
                "OnEndEditCurId",
                "OnEndEditNextId",
                "OnEndEditCondTrue",
                "OnEndEditCondFalse",
                "OnEndEditTalk",
            };
            foreach (string name in names)
            {
                MethodInfo method = AccessTools.Method(typeof(ModEvtEditView), name);
                if (method != null) yield return method;
            }
        }

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                Traverse.Create(__instance).Method("RefreshRoles").GetValue();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtStageGraphRefresh] {e}");
            }
        }
    }

    /// <summary>
    /// 功能 3：舞台累计位移与表情示意。
    /// 每次 RefreshRoles 前由 TalkPreviewStateResolver 沿当前前驱链生成 after-current
    /// 快照；OnRenderRole 渲染完立绘后，把该人物整个出场生命周期累计的
    /// 3004/3008 偏移应用到槽位基准位置（偏移 × 0.7 舞台缩放比），并显示
    /// 3000 的当前表情/姿势。人物退场重进、换背景清场、换方位/层级时，
    /// 解析器会按真实播放器语义处理状态边界。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnRenderRole", typeof(Cell_ModEvtRoleItemUI))]
    internal static class EvtStageOffsetPatch
    {
        /// <summary>编辑器舞台相对真实对话画面的缩放比（间距 300→210px、立绘 scale 0.7，已核实）。</summary>
        private const float StageScale = 0.7f;

        private sealed class StagePreviewCache
        {
            public TalkCfg CurrentTalk;
            public TalkPreviewSnapshot Snapshot;
            public bool SnapshotPrepared;
            public readonly Dictionary<int, Vector2> BasePositions = new();
            public int LastAmbiguousWarningTalkId = int.MinValue;
            public int LastCycleWarningTalkId = int.MinValue;
            public int LastDuplicateWarningTalkId = int.MinValue;
            public int LastBackgroundContextWarningTalkId = int.MinValue;
        }

        // 缓存跟随 ModEvtEditView 生命周期释放，避免旧版静态 InstanceID 字典在
        // 视图销毁/Unity 重用 ID 后命中陈旧基准位置。
        private static readonly ConditionalWeakTable<ModEvtEditView, StagePreviewCache> Caches = new();

        internal static TalkPreviewSnapshot PrepareSnapshot(ModEvtEditView view, TalkCfg current)
        {
            if (view == null) return null;
            StagePreviewCache cache = Caches.GetValue(view, _ => new StagePreviewCache());
            cache.CurrentTalk = current;
            cache.Snapshot = null;
            cache.SnapshotPrepared = true;
            if (current == null) return null;

            try
            {
                var t = Traverse.Create(view);
                var talks = t.Field("talkCfgs").GetValue<List<TalkCfg>>();
                var options = t.Field("optionCfgs").GetValue<Dictionary<int, OptionCfg>>();
                var persons = t.Field("personCfgs").GetValue<Dictionary<int, PersonCfg>>();
                var customBgs = t.Field("customBgCfgs").GetValue<Dictionary<int, BgCfg>>();
                var validPersonIds = persons != null
                    ? new HashSet<int>(persons.Keys)
                    : null;
                var allBgs = new Dictionary<int, BgCfg>();
                if (customBgs != null)
                {
                    foreach (KeyValuePair<int, BgCfg> bg in customBgs)
                        allBgs[bg.Key] = bg.Value;
                }
                if (Cfg.BgCfgMap != null)
                {
                    foreach (KeyValuePair<int, BgCfg> bg in Cfg.BgCfgMap)
                        if (!allBgs.ContainsKey(bg.Key)) allBgs.Add(bg.Key, bg.Value);
                }
                var validBgIds = new HashSet<int>(allBgs.Keys);

                cache.Snapshot = TalkPreviewStateResolver.Resolve(
                    talks, options, current, validBgIds, validPersonIds,
                    TalkPreviewPlaybackMode.Game, null, allBgs);

                if (cache.Snapshot.DuplicateTalkId)
                {
                    if (cache.LastDuplicateWarningTalkId != current.id)
                    {
                        cache.LastDuplicateWarningTalkId = current.id;
                        Plugin.Log.LogWarning(
                            $"[EvtStageOffset] 对话列表存在重复 ID（当前{current.id}）；" +
                            "无法唯一还原历史状态，小舞台将回退当前句显示。");
                    }
                    cache.Snapshot = null;
                    return null;
                }

                if (cache.Snapshot.BackgroundContextDependent)
                {
                    if (cache.LastBackgroundContextWarningTalkId != current.id)
                    {
                        cache.LastBackgroundContextWarningTalkId = current.id;
                        Plugin.Log.LogWarning(
                            $"[EvtStageOffset] 对话{current.id}在首个有效背景前已有 bg=0 人物；" +
                            "实际清场取决于事件入口背景，小舞台将使用安全回退。");
                    }
                    cache.Snapshot = null;
                    return null;
                }

                if (cache.Snapshot.AmbiguousPredecessor
                    && cache.LastAmbiguousWarningTalkId != current.id)
                {
                    cache.LastAmbiguousWarningTalkId = current.id;
                    Plugin.Log.LogWarning(
                        $"[EvtStageOffset] 对话{current.id}存在多个前驱；" +
                        "小舞台按事件编辑器相同的首个前驱显示累计目标状态。");
                }
                if (cache.Snapshot.CycleDetected
                    && cache.LastCycleWarningTalkId != current.id)
                {
                    cache.LastCycleWarningTalkId = current.id;
                    Plugin.Log.LogWarning(
                        $"[EvtStageOffset] 对话{current.id}的前驱链存在循环；" +
                        "无法定义唯一历史状态，小舞台将回退当前句显示。");
                }
                if (cache.Snapshot.CycleDetected)
                {
                    cache.Snapshot = null;
                    return null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtStageOffset.Prepare] {e}");
            }
            return cache.Snapshot;
        }

        /// <summary>
        /// 读取本轮 RefreshRoles 已计算的结果；null 也会缓存，避免不可靠剧情图在
        /// 9 个人物槽渲染时重复回溯。需要响应编辑内容变化时由 RefreshRoles 显式
        /// 调 PrepareSnapshot 重新计算。
        /// </summary>
        internal static TalkPreviewSnapshot GetSnapshot(
            ModEvtEditView view,
            TalkCfg current)
        {
            if (view == null) return null;
            StagePreviewCache cache = Caches.GetValue(view, _ => new StagePreviewCache());
            if (!ReferenceEquals(cache.CurrentTalk, current) || !cache.SnapshotPrepared)
                return PrepareSnapshot(view, current);
            return cache.Snapshot;
        }

        private static void Postfix(ModEvtEditView __instance, Cell_ModEvtRoleItemUI _cell)
        {
            try
            {
                var keyObj = _cell.GetKeyObj<Cell_NewTalkRoleItemUI>("role");
                if (keyObj == null || keyObj.transform == null) return;

                StagePreviewCache cache = Caches.GetValue(__instance, _ => new StagePreviewCache());
                var curSelect = Traverse.Create(__instance).Field("curSelect").GetValue<TalkCfg>();
                GetSnapshot(__instance, curSelect);

                // 首次见到该视图中的立绘对象时记下槽位基准位置。
                int key = keyObj.gameObject.GetInstanceID();
                if (!cache.BasePositions.TryGetValue(key, out var basePos))
                {
                    basePos = keyObj.transform.anchoredPosition;
                    cache.BasePositions[key] = basePos;
                }

                var offset = Vector2.zero;
                TalkPreviewRoleState roleState = null;
                int personId = -1;
                if (_cell.data != null && curSelect != null)
                {
                    personId = (int)_cell.data;
                    if (cache.Snapshot?.AfterCurrent != null
                        && cache.Snapshot.AfterCurrent.TryGetValue(personId, out roleState))
                    {
                        offset = new Vector2(roleState.OffsetX, roleState.OffsetY) * StageScale;
                    }
                    else
                    {
                        // 异常/残缺图的兼容回退：至少保留旧版“显示当前句偏移”的能力。
                        offset = TalkActionTranslator.GetTalkOffset(curSelect, personId) * StageScale;
                    }
                }

                keyObj.transform.anchoredPosition = basePos + offset;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtStageOffset] {e}");
            }
        }

        internal static TalkPreviewRoleState GetAfterRoleState(
            ModEvtEditView view,
            int personId)
        {
            if (view == null || personId < 0) return null;
            StagePreviewCache cache = Caches.GetValue(view, _ => new StagePreviewCache());
            var current = Traverse.Create(view).Field("curSelect").GetValue<TalkCfg>();
            GetSnapshot(view, current);
            return cache.Snapshot?.AfterCurrent != null
                && cache.Snapshot.AfterCurrent.TryGetValue(personId, out TalkPreviewRoleState state)
                    ? state
                    : null;
        }
    }

    /// <summary>
    /// 原版 OnRenderRole 永远传 exp=-1，且图片型人物只能查全局 ModFaceCfg。
    /// Prefix 用一次最终渲染替代原方法，仅增加累计 3000 表情和当前 Mod 表情表；
    /// 服装、高亮、发型和朝向仍保持原小舞台语义，避免把并发 Tween 或运行时
    /// useCloth/isIcon 生命周期的近似状态扩散到静态编辑器。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "OnRenderRole", typeof(Cell_ModEvtRoleItemUI))]
    internal static class EvtStageRoleVisualPatch
    {
        private static bool Prefix(ModEvtEditView __instance, Cell_ModEvtRoleItemUI _cell)
        {
            try
            {
                var roleCell = _cell.GetKeyObj<Cell_NewTalkRoleItemUI>("role");
                if (roleCell == null) return true;
                roleCell.gameObject.SetActive(_cell.data != null);
                if (_cell.data == null) return false;

                int personId = (int)_cell.data;
                roleCell.SetData(personId);
                roleCell.img_bubble_face.gameObject.SetActive(false);
                // 同一槽位快速切换 Talk/表情时也可能有多个外部图片请求在途；
                // 注册代次保护，避免较早请求晚到后覆盖当前表情。
                PreviewSpriteRequestGuard.Register(roleCell.icon_role);

                var t = Traverse.Create(__instance);
                var current = t.Field("curSelect").GetValue<TalkCfg>();
                TalkPreviewRoleState state =
                    EvtStageOffsetPatch.GetAfterRoleState(__instance, personId);

                // ModFaceCfg 的键同时包含服装编号；若表情来自前文而服装也已
                // 在前文切换，必须用同一快照中的累计服装才能找到正确表情图。
                int cloth = state != null && state.Cloth >= 0 ? state.Cloth : 0;
                // 原版静态小舞台对 L2D 传 -1（模型默认表情）。只有状态机
                // 确认执行过 3000 时才改成具体编号；图片分支仍会把 -1 归为
                // 默认脸 0，与 TalkRoleItem.SetData 的原行为一致。
                int pose = state != null && state.PoseSet ? state.Pose : -1;
                bool foundCurrentCloth = state != null && state.Cloth >= 0;
                if (current?.roles != null)
                {
                    foreach (List<float> action in current.roles)
                    {
                        if (action == null || action.Count < 2
                            || (int)action[0] != personId) continue;
                        int code = (int)action[1];
                        // 原小舞台只取当前句第一条 3006；保持兼容。
                        if (!foundCurrentCloth && code == 3006 && action.Count > 2)
                        {
                            cloth = (int)action[2];
                            foundCurrentCloth = true;
                        }
                        if (state == null && code == 3000 && action.Count > 2)
                            pose = (int)action[2];
                    }
                }

                int colorId = 0;
                bool highlighted = current != null
                    && ((current.roleIds != null && current.roleIds.Contains(personId))
                        || (current.highlights != null && current.highlights.Contains(personId)));
                if (!highlighted) colorId = 6;

                var persons = t.Field("personCfgs").GetValue<Dictionary<int, PersonCfg>>();
                if (persons != null && persons.TryGetValue(personId, out PersonCfg person))
                {
                    int order = t.Field("order").GetValue<int>();
                    int gradeState = t.Field("gradeState").GetValue<int>();
                    var gender = t.Field("gender").GetValue<GenderDefine>();
                    PreviewRoleRenderer.Render(
                        person, roleCell, order, colorId, 1f, cloth, pose, false,
                        0, gradeState, gender, EvtPreviewConfigLoader.GetFaces(__instance));
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtStageRoleVisual] {e}");
                return true; // 任意异常回退原版 OnRenderRole。
            }
        }
    }

    /// <summary>
    /// 功能 4："预览本句"按钮。
    /// 原版预览入口在外层事件编辑页，且永远从事件第一句开始播
    ///（PreviewTalkView.OnOpen 取 talkId[0]），调中间某句的动画极其低效。
    /// 本补丁在对话编辑器右侧按钮列（group_btn，带 VerticalLayoutGroup，
    /// 克隆按钮会被自动排版）克隆"保存"按钮生成"预览本句"：
    /// 点击后把编辑器内存中的最新数据（含未保存修改）传给 PreviewTalkView，
    /// 并把起始句设为当前选中的对话。
    ///
    /// 数据来源（反编译核实）：
    ///   - talkCfgs/optionCfgs：编辑器内存里本事件的最新数据；
    ///   - personCfgs/audioCfgs：编辑器加载时已合并原版配置，直接传；
    ///   - customBgCfgs/customCGCfgs：仅含 mod 自定义项，需与原版合并后传；
    ///   - ModFaceCfg/ItemCfg/BookCfg：从当前 Mod 目录加载并与全局表合并；
    ///   - audioCfgs 是懒加载（LoadAudioCfg），传之前先确保已加载。
    ///
    /// 历史动作不会复制进当前 roles 列表，而是由 TalkPreviewStateResolver 计算
    /// BeforeCurrent 快照，在首次 ShowCurTxt 前直接恢复 PreviewTalkView 的内部状态。
    /// 当前句及后续仍使用原始 actions/screenEffect/audio、原顺序与原 delay。
    /// 多前驱或循环无法唯一判断上下文时直接阻止本句预览，不猜测分支。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "InitUI")]
    internal static class EvtTalkPreviewPatch
    {
        private const string BtnObjName = "btn_preview_talk";

        private static GameObject _previewBtn;
        private static bool _limitHintShown;

        private const string BtnLabel = "预览本句";

        private static void Postfix(ModEvtEditView __instance)
        {
            try
            {
                var view = __instance;
                var template = view.btn_save;
                if (template == null || template.gameObject == null) return;

                var parent = template.gameObject.transform.parent;
                if (parent == null) return;

                // 视图重开时按钮可能已存在（随视图销毁重建）
                var existing = parent.Find(BtnObjName);
                GameObject go;
                if (existing != null)
                {
                    go = existing.gameObject;
                }
                else
                {
                    go = UnityEngine.Object.Instantiate(template.gameObject, parent);
                    go.name = BtnObjName;

                    // 克隆体的标签 Text 挂着 LocalizeStringEvent（本地化组件），
                    // 会在本地化表就绪/刷新时把文字重置回源文案"保存"——
                    // 必须先删掉（复用小游戏字段补丁踩过同一个坑的清理方法），
                    // 再设置标签才不会被顶回去。
                    MiniGameUtil.StripBadComponents(go);

                    var label = go.GetComponentInChildren<Text>();
                    if (label != null) label.text = BtnLabel;

                    // 清掉克隆自模板的点击回调（运行时监听不随 Instantiate 复制，
                    // 这里是双保险，防止误触"保存"逻辑）
                    var unityBtn = go.GetComponent<Button>();
                    if (unityBtn != null) unityBtn.onClick.RemoveAllListeners();

                    go.transform.SetAsLastSibling(); // 排在按钮列最底部
                }

                var ub = new UIButton(go);
                ub.AddClick(() => OpenPreview(view));

                _previewBtn = go;
                go.SetActive(false); // 选中对话后才显示（EvtTalkPreviewSelectPatch 控制）

                Plugin.Log.LogInfo("[EvtActionVisual] 预览本句按钮已创建。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreviewInit] {e}");
            }
        }

        /// <summary>选中状态变化时联动按钮显隐（与原版 btn_delete 的显隐逻辑保持一致）。</summary>
        internal static void SetVisible(bool visible)
        {
            if (_previewBtn == null) return;
            _previewBtn.SetActive(visible);

            // 标签双保险：万一有其他本地化/界面刷新逻辑把文字改回去，
            // 每次显示时重设一遍（参照小游戏字段补丁的双保险做法）
            if (visible)
            {
                var label = _previewBtn.GetComponentInChildren<Text>();
                if (label != null && label.text != BtnLabel) label.text = BtnLabel;
            }
        }

        private static void OpenPreview(ModEvtEditView view)
        {
            TalkCfg current = null;
            try
            {
                current = Traverse.Create(view)
                    .Field("curSelect").GetValue<TalkCfg>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreview.Current] {e}");
            }
            TryOpenPreview(view, current, null, null);
        }

        /// <summary>
        /// 统一的“预览本句”入口。talkSource/optionSource 为空时读取原编辑器
        /// 内存；剧情图编辑模式则传入深拷贝草稿，使尚未保存的热修改也能预览。
        /// 返回 true 仅表示已把 PreviewTalkView 打开请求交给 UIMgr。
        /// </summary>
        internal static bool TryOpenPreview(
            ModEvtEditView view,
            TalkCfg requestedTalk,
            IEnumerable<TalkCfg> talkSource,
            IDictionary<int, OptionCfg> optionSource)
        {
            return TryOpenPreviewCore(
                view, requestedTalk, talkSource, optionSource, null);
        }

        internal static bool TryOpenStoryGraphPreview(
            ModEvtEditView view,
            TalkCfg requestedTalk,
            IEnumerable<TalkCfg> talkSource,
            IDictionary<int, OptionCfg> optionSource,
            StoryGraphEditSession storyGraphSession)
        {
            return TryOpenPreviewCore(
                view, requestedTalk, talkSource, optionSource,
                storyGraphSession);
        }

        private static bool TryOpenPreviewCore(
            ModEvtEditView view,
            TalkCfg requestedTalk,
            IEnumerable<TalkCfg> talkSource,
            IDictionary<int, OptionCfg> optionSource,
            StoryGraphEditSession storyGraphSession)
        {
            try
            {
                // 失败提示统一走路由：从剧情图进入预览时画布刚被暂隐、
                // 失败后立即恢复，原版 Toast 会被重新盖住看不见。
                if (view == null)
                {
                    StoryGraphToastRouter.Show("事件编辑器已关闭，无法预览本句");
                    return false;
                }

                var t = Traverse.Create(view);
                List<TalkCfg> talkList = talkSource != null
                    ? SnapshotTalkSource(talkSource)
                    : t.Field("talkCfgs").GetValue<List<TalkCfg>>();
                string validation = ValidatePreviewRequest(
                    talkList, requestedTalk);
                if (!string.IsNullOrEmpty(validation))
                {
                    StoryGraphToastRouter.Show(validation);
                    return false;
                }

                // List → Dictionary。每句都传“roles 列表浅拷贝”的副本：
                // 预览播放器每句播放前会对
                // roles 列表【就地排序】（进场条目提前），直接传编辑器内存对象
                // 会悄悄重排作者在动作指令框里看到的数字串顺序——数据侵入！
                var talkMap = new Dictionary<int, TalkCfg>();
                foreach (var talk in talkList)
                {
                    if (talk == null) continue;
                    TalkCfg previewTalk = ShallowCopyTalk(talk);
                    talkMap[talk.id] = previewTalk;
                    if (storyGraphSession != null
                        && StoryGraphLatexProbe.BindAuthorPreview != null)
                    {
                        try
                        {
                            StoryGraphLatexProbe.BindAuthorPreview(
                                storyGraphSession, previewTalk);
                        }
                        catch (Exception e)
                        {
                            Plugin.Log?.LogWarning(
                                "[EvtTalkPreview.Latex] 会话预览绑定失败：" + e.Message);
                        }
                    }
                }

                Dictionary<int, OptionCfg> optionCfgsRaw = optionSource != null
                    ? SnapshotOptionSource(optionSource)
                    : t.Field("optionCfgs")
                        .GetValue<Dictionary<int, OptionCfg>>();
                var optionMap = Merge(optionCfgsRaw, Cfg.OptionCfgMap);
                if (HasReachableEmptyTalkCycle(
                        talkMap, optionMap, requestedTalk.id))
                {
                    StoryGraphToastRouter.Show("当前对话后方存在空白对话循环，原预览器会同步递归直至崩溃；请先修正跳转关系");
                    return false;
                }

                var personMap = t.Field("personCfgs").GetValue<Dictionary<int, PersonCfg>>(); // 已含原版
                var bgMap = Merge(t.Field("customBgCfgs").GetValue<Dictionary<int, BgCfg>>(), Cfg.BgCfgMap);

                int gradeState = t.Field("gradeState").GetValue<int>();
                var gender = t.Field("gender").GetValue<GenderDefine>();
                string modRoot = t.Field("modRoot").GetValue<string>();

                // CG 表必须每次从磁盘重读（与 faces/items/books 同口径）：
                // customCGCfgs 是事件打开那一刻的一次性快照（ModEvtEditView.cs:161），
                // Cfg.CGCfgMap 是启动快照，本次会话里新增的 cgId 未必在这两张表
                // 里——用它们做预览，CGView.Refresh 的裸索引器
                // cgCfgMap[_id] 必抛 KeyNotFoundException，预览停在半开黑框态（L3-2）。
                var cgMap = EvtPreviewConfigLoader.LoadCgs(modRoot);

                // audioCfgs 懒加载，先确保加载（内部已含原版合并）
                t.Method("LoadAudioCfg").GetValue();
                var audioMap = t.Field("audioCfgs").GetValue<Dictionary<int, AudioCfg>>();

                // 必须与 ModPreviewTipsView.OnClickOK 一样传满 12 项。尤其第 10 项
                // ModFaceCfg 决定图片型人物的 3000 自定义表情；若省略，只会回退
                // 当前游戏已加载的全局表，看不到尚在编辑的 Mod 表情。item/book 也
                // 显式传入：PreviewTalkView 原版 OnOpen 对这两项的空值判断误用了
                // faceCfgMap，不能依赖它自动回退，否则相关屏幕效果可能拿到 null。
                var faceMap = EvtPreviewConfigLoader.LoadFaces(modRoot);
                var itemMap = EvtPreviewConfigLoader.LoadItems(modRoot);
                var bookMap = EvtPreviewConfigLoader.LoadBooks(modRoot);
                if (faceMap.Count == 0 && (itemMap.Count > 0 || bookMap.Count > 0))
                {
                    // PreviewTalkView.OnOpen 原版把 item/book 的空值判断误写成
                    // faceCfgMap.IsEmpty()：当游戏与当前 Mod 都没有表情表时，它会
                    // 丢弃已经传入的 item/book。放入绝不可能与合法表情编号冲突
                    // 的内部哨兵，只修正该错误分支；表情查找不会命中它。
                    faceMap[int.MinValue] = new ModFaceCfg { id = int.MinValue };
                }

                // 解析当前 Talk 执行前的完整播放器状态。它会在 ShowCurTxt 前直接
                // 写入 PreviewTalkView 内部状态，绝不把历史动作与当前动作塞进同一
                // Tween 时间轴；当前句原始动作/延迟随后照常执行。
                var validBgIds = new HashSet<int>(bgMap.Keys);
                var validPersonIds = personMap != null
                    ? new HashSet<int>(personMap.Keys)
                    : null;
                TalkPreviewSnapshot startSnapshot = TalkPreviewStateResolver.Resolve(
                    talkList, optionMap, requestedTalk, validBgIds, validPersonIds,
                    TalkPreviewPlaybackMode.Preview, audioMap, bgMap);
                if (!startSnapshot.Reliable)
                {
                    StoryGraphToastRouter.Show(
                        "当前对话的前驱存在循环，无法唯一恢复执行前状态；请先修正跳转或使用完整剧情预览");
                    return false;
                }
                if (startSnapshot.AmbiguousPredecessor)
                {
                    // 分支合流没有运行时选项记录，任意选择一个前驱都可能让人物/
                    // CG 状态来自错误分支。宁可阻止，也不展示看似正常的错误预览。
                    StoryGraphToastRouter.Show(
                        "当前对话存在多个前驱，无法判断要恢复哪条分支；请从完整剧情预览进入该分支");
                    return false;
                }
                var bootstrap = new TalkPreviewBootstrapContext
                {
                    StartTalkId = requestedTalk.id,
                    Snapshot = startSnapshot,
                };

                // 前 12 项与原版一致；第 13 项只由 EditorPlus 的 OnOpen 补丁读取，
                // 原 PreviewTalkView 不会索引它。
                UIMgr.OpenView<PreviewTalkView>(UILayerType.None, null, new object[13]
                {
                    talkMap, optionMap, new List<int> { requestedTalk.id },
                    gradeState, gender, personMap, bgMap, cgMap, audioMap,
                    faceMap, itemMap, bookMap, bootstrap
                });

                if (!_limitHintShown)
                {
                    _limitHintShown = true;
                    ToastHelper.Toast(
                        "已从当前对话开始，并恢复所选前驱路径上的人物、背景、表情、CG/滤镜/手机与音乐状态");
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreview] {e}");
                try { StoryGraphToastRouter.Show("预览本句打开失败；请查看 BepInEx 日志"); }
                catch { }
                return false;
            }
        }

        internal static string ValidatePreviewRequest(
            List<TalkCfg> talks, TalkCfg requestedTalk)
        {
            if (requestedTalk == null) return "请先选择一条对话";
            if (talks == null || talks.Count == 0)
                return "当前事件没有可预览的对话";
            bool found = false;
            for (int i = 0; i < talks.Count; i++)
            {
                if (ReferenceEquals(talks[i], requestedTalk))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return "该剧情图节点已经失效；请刷新剧情图后重试";
            if (string.IsNullOrWhiteSpace(requestedTalk.content))
                return "当前对话内容为空，会被预览器直接跳过；请选择一条有正文的对话预览";
            if (HasDuplicateTalkIds(talks))
                return "对话列表存在重复编号（ID），预览器无法确定对应关系；请先修正编号，再预览本句";
            return null;
        }

        private static List<TalkCfg> SnapshotTalkSource(
            IEnumerable<TalkCfg> source)
        {
            var result = new List<TalkCfg>();
            if (source != null)
                foreach (TalkCfg talk in source) result.Add(talk);
            return result;
        }

        private static Dictionary<int, OptionCfg> SnapshotOptionSource(
            IDictionary<int, OptionCfg> source)
        {
            var result = new Dictionary<int, OptionCfg>();
            if (source != null)
                foreach (KeyValuePair<int, OptionCfg> pair in source)
                    result[pair.Key] = pair.Value;
            return result;
        }

        private static bool HasDuplicateTalkIds(List<TalkCfg> talks)
        {
            var ids = new HashSet<int>();
            if (talks == null) return false;
            foreach (TalkCfg talk in talks)
            {
                if (talk != null && !ids.Add(talk.id)) return true;
            }
            return false;
        }

        /// <summary>
        /// PreviewTalkView 遇到空 content 会在 RefreshTalk 内同步 NextTalk；空节点
        /// 形成环时不会等待点击而会直接栈溢出。只检查从起始句下一跳可达的“纯空
        /// Talk 子图”；经过非空 Talk 后递归会停止，正常剧情循环不在此限制内。
        /// </summary>
        private static bool HasReachableEmptyTalkCycle(
            Dictionary<int, TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            int startTalkId)
        {
            if (talks == null || !talks.ContainsKey(startTalkId)) return false;

            // 先遍历完整可达图，不能在遇到下一条非空 Talk 时停止：玩家点击那句
            // 后仍可能进入更深处的纯空环并触发同步递归栈溢出。
            var reachable = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(startTalkId);
            while (pending.Count > 0)
            {
                int id = pending.Pop();
                if (!reachable.Add(id) || !talks.TryGetValue(id, out TalkCfg talk)) continue;
                foreach (int next in GetActualOutgoingTalkIds(talk, options))
                    if (!reachable.Contains(next)) pending.Push(next);
            }

            var visiting = new HashSet<int>();
            var finished = new HashSet<int>();
            foreach (int id in reachable)
            {
                if (HasEmptyCycleFrom(
                    id, talks, options, reachable, visiting, finished)) return true;
            }
            return false;
        }

        private static bool HasEmptyCycleFrom(
            int talkId,
            Dictionary<int, TalkCfg> talks,
            Dictionary<int, OptionCfg> options,
            HashSet<int> reachable,
            HashSet<int> visiting,
            HashSet<int> finished)
        {
            if (!reachable.Contains(talkId)
                || !talks.TryGetValue(talkId, out TalkCfg talk)
                || !string.IsNullOrWhiteSpace(talk.content)) return false;
            if (visiting.Contains(talkId)) return true;
            if (finished.Contains(talkId)) return false;

            visiting.Add(talkId);
            foreach (int next in GetSynchronousEmptyOutgoing(talk))
            {
                // 这里只走空正文 NextTalk 的同步边；带 check 的空 Talk 会先弹
                // 分支确认框，不会在当前调用栈继续递归。
                if (HasEmptyCycleFrom(
                    next, talks, options, reachable, visiting, finished)) return true;
            }
            visiting.Remove(talkId);
            finished.Add(talkId);
            return false;
        }

        private static IEnumerable<int> GetActualOutgoingTalkIds(
            TalkCfg talk,
            Dictionary<int, OptionCfg> options)
        {
            if (talk == null) yield break;
            var seen = new HashSet<int>();

            // 5001 纸条优先于 option；纸条关闭后事件 6 会直接调用 NextTalk。
            // 保守地把任一正 paperId 视为有效，宁可多检查也不能漏掉空环崩溃边。
            bool hasPaper = HasPotentialPaper(talk);

            // 非空、无纸条且有选项时，DoTextEnd 只展示选项，不走残留 nextTalk。
            if (!string.IsNullOrWhiteSpace(talk.content) && !hasPaper
                && talk.option != null && talk.option.Count > 0)
            {
                if (options == null) yield break;
                foreach (int optionId in talk.option)
                {
                    if (!options.TryGetValue(optionId, out OptionCfg option)
                        || option == null) continue;
                    int first = option.GetNextTalk();
                    int second = option.GetNextTalk2();
                    if (first > 1 && seen.Add(first)) yield return first;
                    if (second > 1 && seen.Add(second)) yield return second;
                }
                yield break;
            }

            int next = talk.GetNextTalk();
            if (next > 0 && seen.Add(next)) yield return next;
            if (talk.check != null && talk.check.Count > 0)
            {
                int next2 = talk.GetNextTalk2();
                if (next2 > 0 && seen.Add(next2)) yield return next2;
            }
        }

        private static bool HasPotentialPaper(TalkCfg talk)
        {
            if (talk?.roles == null) return false;
            foreach (List<float> action in talk.roles)
            {
                if (action != null && action.Count > 2
                    && (int)action[1] == 5001 && (int)action[2] > 0)
                    return true;
            }
            return false;
        }

        private static IEnumerable<int> GetSynchronousEmptyOutgoing(TalkCfg talk)
        {
            if (talk == null || !string.IsNullOrWhiteSpace(talk.content)) yield break;
            // NextTalk 遇 check 会显示确认框并返回，不形成同步递归边。
            if (talk.check != null && talk.check.Count > 0) yield break;
            int next = talk.GetNextTalk();
            if (next > 0) yield return next;
        }

        /// <summary>合并配置表：custom 优先，原版补缺（与游戏 mod 合并的"先到先得"语义一致）。</summary>
        private static Dictionary<int, T> Merge<T>(Dictionary<int, T> custom, Dictionary<int, T> global)
        {
            var result = new Dictionary<int, T>();
            if (custom != null)
            {
                foreach (var kvp in custom) result[kvp.Key] = kvp.Value;
            }
            if (global != null)
            {
                foreach (var kvp in global)
                {
                    if (!result.ContainsKey(kvp.Key)) result.Add(kvp.Key, kvp.Value);
                }
            }
            return result;
        }


        /// <summary>
        /// TalkCfg 浅拷贝：roles 用新列表（条目内层 List 共享引用——预览只读
        /// 条目内容，但会对 roles 列表本身就地排序，必须隔离），其余字段直传。
        /// </summary>
        private static TalkCfg ShallowCopyTalk(TalkCfg src)
        {
            return new TalkCfg
            {
                audio = src.audio,
                bg = src.bg,
                check = src.check,
                content = src.content,
                effect = src.effect,
                effect2 = src.effect2,
                highlights = src.highlights,
                id = src.id,
                maxoptions = src.maxoptions,
                miniGame = src.miniGame,
                nextTalk = src.nextTalk,
                nextTalk2 = src.nextTalk2,
                option = src.option,
                replace = src.replace,
                roleIds = src.roleIds,
                roleName = TalkRoleNameUtil.NormalizeOverride(src.roleName),
                screenEffect = src.screenEffect,
                showTxt = src.showTxt,
                time = src.time,
                vocals = src.vocals,
                roles = src.roles != null
                    ? new List<List<float>>(src.roles)
                    : new List<List<float>>(),
            };
        }

    }

    /// <summary>
    /// 选中对话变化时：隐藏悬浮提示框（防旧内容残留）+ 联动"预览本句"按钮显隐。
    /// </summary>
    [HarmonyPatch(typeof(ModEvtEditView), "Select", typeof(TalkCfg))]
    internal static class EvtTalkPreviewSelectPatch
    {
        private static void Postfix(TalkCfg _cfg)
        {
            try
            {
                EvtActionHintInitPatch.OnSelectChanged();
                EvtTalkPreviewPatch.SetVisible(_cfg != null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[EvtTalkPreviewSelect] {e}");
            }
        }
    }
}
