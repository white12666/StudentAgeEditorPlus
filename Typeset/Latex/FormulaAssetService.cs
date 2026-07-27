using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StudentAgeTypeset.Latex
{
    /// <summary>块级公式物化结果；Error 为 null 即成功，其余字段才有意义。</summary>
    internal sealed class FormulaMaterializeResult
    {
        internal int CgId;
        internal string Url;
        internal string PngPath;
        internal string Error;

        internal bool Success
        {
            get { return Error == null; }
        }
    }

    /// <summary>公式资产 GC 结果；Error 非 null 表示整体跳过（一个条目/PNG 都没动）。</summary>
    internal sealed class FormulaGcResult
    {
        internal int RemovedEntries;
        internal int DeletedPngs;
        internal readonly List<string> Warnings = new List<string>();
        internal string Error;

        internal bool Success
        {
            get { return Error == null; }
        }
    }

    /// <summary>渲染接缝：运行时默认 FormulaRenderer.TryRenderCard，测试注入假渲染器。</summary>
    internal delegate bool FormulaCardRenderer(
        string latex, FormulaCardMode mode, out byte[] png, out string error);

    /// <summary>
    /// 块级公式资产服务（LATEX-DESIGN §4）：内容寻址 PNG 写入 modRoot\Textures\Formula\，
    /// CGCfg.json 只增改公式段条目后单文件原子替换。写入顺序恒为 PNG → CGCfg.json →
    /// （调用方的）TrySave：引用最后落盘，中途失败最坏留下未引用孤儿（无害、可 GC）。
    /// 与游戏静态表解耦：Cfg.CGCfgMap 的占用集由调用方经参数传入，本类只碰文件，可单测。
    /// </summary>
    internal static class FormulaAssetService
    {
        /// <summary>
        /// 公式专用 CG id 段：3_000_000..9_999_999，段内**任意**整数都能安全走完
        /// 玩家侧取值链路（见 IsScreenEffectSafeCgId）。与内置（实测 ≤999,004）、DLC、
        /// 三方实证段（1,999,100+）永不相交，且 1e6..1e7 区间在内置表里一条都没有。
        ///
        /// 曾用过 100_000_000 + evtId*100 + n 的「窗口寻址」，已废弃：1 亿段过不了
        /// 原版编辑器保存（MessagePack 把 float 按 G7 转文本，9 位 id 被抹平，
        /// 详见 IsScreenEffectSafeCgId 与 REVIEW-latex-2026-07-27.md 的 LATEX-D2-1）。
        /// 旧段仅保留识别能力（IsLegacyFormulaCgId）用于回收既有落盘数据，不再分配。
        /// </summary>
        internal const int FormulaCgIdBase = 3_000_000;
        internal const int FormulaCgIdMax = 9_999_999;

        /// <summary>已废弃的旧段（1 亿段）：只识别、只回收，绝不再分配。</summary>
        internal const int LegacyFormulaCgIdBase = 100_000_000;
        internal const int LegacyFormulaCgIdMax = 299_999_999;

        internal const int MaxEventId = 1_999_999;

        // 与 StoryGraphCgFlowAnalyzer 相同的 screenEffect 码位（ShowCG / ShowMiniCG）。
        private const int ShowCg = 4015;
        private const int ShowMiniCg = 4019;

        private const string CgCfgFileName = "CGCfg.json";
        // 瞬态/备份文件形状是"写方（本类）↔ 清扫方（EditorPlus 的 ModLoadRecoveryPatch）"
        // 共同的识别契约，提 internal 供清扫方直接引用，杜绝字符串双处维护。
        internal const string CfgTempInfix = ".formula.tmp.";
        internal const string CfgBackupSuffix = ".formula.bak";
        internal const string PngPrefix = "formula_";
        internal const string PngTempInfix = ".png.tmp";

        /// <summary>当前段（新分配只用这一段）。</summary>
        internal static bool IsFormulaCgId(int id)
        {
            return id >= FormulaCgIdBase && id <= FormulaCgIdMax;
        }

        /// <summary>已废弃的 1 亿段：识别既有落盘数据用。</summary>
        internal static bool IsLegacyFormulaCgId(int id)
        {
            return id >= LegacyFormulaCgIdBase && id <= LegacyFormulaCgIdMax;
        }

        /// <summary>
        /// 新旧两段的并集。**回收/识别路径必须用这个**：旧段条目与 PNG 只有被认出来
        /// 才可能被 GC 清掉，只认新段会让已落盘的 9 位数据永久残留。
        /// </summary>
        internal static bool IsAnyFormulaCgId(int id)
        {
            return IsFormulaCgId(id) || IsLegacyFormulaCgId(id);
        }

        /// <summary>
        /// cgId 能否安全走完玩家侧取值链路。两道关，缺一不可：
        ///
        /// 1) 二进制无损：TalkCfg.screenEffect 是 List&lt;float&gt;，玩家侧按
        ///    (int)screenEffect[1] 取值（NewTalkView.cs:921/942）。float ulp 在 1 亿段
        ///    ≥ 8，写进 [4019,cgId] 的瞬间就被就近取整成另一个数。
        ///
        /// 2) G7 文本无损：原版编辑器保存走
        ///    MessagePackSerializer.SerializeToJson（ModEvtEditView.cs:804），且是**整表
        ///    回写**——一次普通保存重写全 mod 每一条 talk 的 screenEffect。float 在这条
        ///    路径上被按 G7（7 位有效数字）转成文本，9 位 id 直接被抹平：实测
        ///    100000104 与 100000112 双双塌缩成 100000100，玩家侧 CGView.cs:73 是裸字典
        ///    索引 cgCfgMap[_id]，缺键必抛 KeyNotFoundException → MiniCG 空白、该句断流，
        ///    且卸载插件后数据仍是坏的。插件自己写盘走 Newtonsoft（"R"，无损），所以
        ///    只有走原版保存路径才会触发——这正是它躲过全部单测的原因。
        ///
        /// 显式用 "G7" 而不是默认 ToString()：默认格式在 .NET Framework / Unity Mono 上
        /// 是 G7，但在 .NET Core 3.0+（测试工程的 net8.0）是最短往返表示，依赖默认值会让
        /// 校验在测试与运行时之间口径漂移。写死 G7 与真实运行时一致，且对更宽松的
        /// 运行时只会更严格，不会漏放。
        /// </summary>
        internal static bool IsScreenEffectSafeCgId(int id)
        {
            float single = id;
            if ((int)single != id) return false;
            string text = single.ToString("G7", CultureInfo.InvariantCulture);
            float parsed;
            if (!float.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out parsed)) return false;
            return (int)parsed == id;
        }

        /// <summary>照原版 ModPageUploadView 的口径：packageId = mod 根目录名（:179）。</summary>
        internal static string ResolvePackageId(string modRoot)
        {
            if (string.IsNullOrWhiteSpace(modRoot)) return null;
            try
            {
                return Path.GetFileName(Path.GetFullPath(modRoot).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>sha256(latex + 样式版本 + 渲染环境指纹) 前 12 字节 hex：
        /// 样式常量参与寻址，改样式必须递增 StyleVersion 才会重渲；渲染环境
        /// （中文字体可用性，FormulaStyle.EnvironmentTag）同样参与，装上字体后
        /// 旧豆腐图才会被换掉（LTX-D1-02）。</summary>
        internal static string ComputeContentHash(string latex)
        {
            return ComputeContentHash(latex, FormulaCardMode.Math);
        }

        /// <summary>排版模式同样参与寻址：同一段 LaTeX 换模式必须出新图。</summary>
        internal static string ComputeContentHash(string latex, FormulaCardMode mode)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                (latex ?? string.Empty) + "\n" + FormulaStyle.StyleVersion
                + "\n" + (FormulaStyle.EnvironmentTag ?? string.Empty)
                + "\n" + (int)mode);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var result = new StringBuilder(24);
                for (int i = 0; i < 12; i++) result.Append(hash[i].ToString("x2"));
                return result.ToString();
            }
        }

        internal static string PngFileName(string latex)
        {
            return PngFileName(latex, FormulaCardMode.Math);
        }

        internal static string PngFileName(string latex, FormulaCardMode mode)
        {
            return PngPrefix + ComputeContentHash(latex, mode) + ".png";
        }

        internal static string FormulaDirectory(string modRoot)
        {
            return Path.Combine(modRoot, "Textures", "Formula");
        }

        /// <summary>
        /// 磁盘名必须精确 CGCfg.json（运行时 Type.GetType("Config."+文件名) 大小写敏感，
        /// ModCtrl.cs:166-170）；语言目录跟插件持久层现状对齐（硬编码 zh-cn）。
        /// </summary>
        internal static string CgCfgPath(string modRoot)
        {
            return Path.Combine(modRoot, "Cfgs", "zh-cn", CgCfgFileName);
        }

        /// <summary>
        /// GetFullUrl 按 Substring("Mods/".Length) 后 Split('\\') 解析（ModCtrl.cs:213）：
        /// Mods 后必须用反斜杠分隔，订阅玩家侧才能重定向到工坊安装目录。
        /// </summary>
        internal static string BuildModUrl(string packageId, string pngFileName)
        {
            return "Mods\\" + packageId + "\\Textures\\Formula\\" + pngFileName;
        }

        /// <summary>
        /// 在公式专用段内线性扫描首个空位；对调用方传入的占用集避让（运行时应传
        /// Cfg.CGCfgMap ∪ 本 mod CGCfg.json 的键，MaterializeBlock 自动并入后者）。
        /// 返回 0 表示整段用尽（7 位段共 700 万个编号，实际不可能）。
        ///
        /// 不再按 evtId 分窗：7 位段放不下 evtId≤1,999,999 所需的 2 亿跨度，且窗口寻址
        /// 本就是旧段的产物。事件归属由 CGCfg 条目的 name 承载，稳定复用由
        /// FindReusableEntry 的内容寻址 url 比对承担（它必须全段扫描，见该方法注释）。
        /// </summary>
        internal static int AllocateCgId(ICollection<int> occupiedIds)
        {
            for (int candidate = FormulaCgIdBase; candidate <= FormulaCgIdMax; candidate++)
            {
                // 段内全部整数都 <2^24 且恰好 7 位有效数字，这道校验必然通过；
                // 保留它是为了将来若有人挪动段位，越界值会当场被拒而不是静默写坏。
                if (!IsScreenEffectSafeCgId(candidate)) continue;
                if (occupiedIds != null && occupiedIds.Contains(candidate)) continue;
                return candidate;
            }
            return 0;
        }

        /// <summary>
        /// 物化一条块级公式：渲染 → PNG（内容寻址、重名幂等跳过）→ CGCfg.json 合并。
        /// 任一步失败返回 Error 且不写 CGCfg 条目（PNG 孤儿允许，可 GC）。
        /// 同事件窗口内已有指向同一内容寻址 PNG 的条目时直接复用其 cgId，不重复分配。
        /// </summary>
        internal static FormulaMaterializeResult MaterializeBlock(
            string modRoot, string packageId, int evtId, string latex,
            ICollection<int> occupiedIds, FormulaCardRenderer renderCard = null,
            FormulaCardMode mode = FormulaCardMode.Math)
        {
            var result = new FormulaMaterializeResult();
            try
            {
                if (string.IsNullOrWhiteSpace(modRoot))
                {
                    result.Error = "无法确定 Mod 根目录，公式无法物化。";
                    return result;
                }
                if (string.IsNullOrWhiteSpace(packageId)
                    || packageId.IndexOf('\\') >= 0 || packageId.IndexOf('/') >= 0)
                {
                    result.Error = "packageId 无效（应为 Mod 目录名）：" + (packageId ?? "<null>");
                    return result;
                }
                if (string.IsNullOrWhiteSpace(latex))
                {
                    result.Error = "公式为空。";
                    return result;
                }
                if (evtId < 0 || evtId > MaxEventId)
                {
                    result.Error = "事件编号 " + evtId + " 超出游戏支持范围（0.."
                        + MaxEventId + "）。";
                    return result;
                }

                // 渲染环境指纹参与内容寻址（LTX-D1-02），必须在算 hash 之前完成渲染器的
                // 惰性初始化，否则首次保存会用空指纹命名、下次保存又换名造成无谓重渲。
                // 只在真实渲染路径触碰渲染器：测试注入假渲染器时不加载 SkiaSharp 原生链路。
                if (renderCard == null)
                {
                    bool ignored = FormulaRenderer.IsAvailable;
                }

                string pngName = PngFileName(latex, mode);
                string pngDir = FormulaDirectory(modRoot);
                string pngPath = Path.Combine(pngDir, pngName);
                string url = BuildModUrl(packageId, pngName);
                string cfgPath = CgCfgPath(modRoot);

                JObject root;
                string readError;
                if (!TryLoadCgCfg(cfgPath, out root, out readError))
                {
                    result.Error = readError;
                    return result;
                }
                SweepStaleCfgTemps(cfgPath);
                SweepStalePngTemps(pngDir, null);

                int reuseId = FindReusableEntry(root, url);

                // PNG 先于 CGCfg 条目落盘（引用最后写）；已存在即幂等跳过，永不原地覆盖。
                if (!File.Exists(pngPath))
                {
                    byte[] png;
                    string renderError;
                    bool rendered = renderCard != null
                        ? renderCard(latex, mode, out png, out renderError)
                        : FormulaRenderer.TryRenderCard(latex, mode, out png, out renderError);
                    if (!rendered || png == null || png.Length == 0)
                    {
                        result.Error = "公式渲染失败：" + (renderError ?? "未知错误");
                        return result;
                    }
                    string writeError;
                    if (!TryWritePngDurable(pngDir, pngPath, png, out writeError))
                    {
                        result.Error = writeError;
                        return result;
                    }
                }

                if (reuseId > 0)
                {
                    result.CgId = reuseId;
                    result.Url = url;
                    result.PngPath = pngPath;
                    return result;
                }

                var occupied = new HashSet<int>();
                if (occupiedIds != null)
                    foreach (int id in occupiedIds) occupied.Add(id);
                AddFileIds(root, occupied);
                int cgId = AllocateCgId(occupied);
                if (cgId == 0)
                {
                    result.Error = "块级公式编号已用尽（专用段 " + FormulaCgIdBase + ".."
                        + FormulaCgIdMax + " 全部被占用），请先删除不再使用的公式。";
                    return result;
                }

                var entry = new CGCfg
                {
                    id = cgId,
                    // 事件归属只由 name 承载（编号不再按事件分窗）。
                    name = "[公式] evt" + evtId + "-" + cgId,
                    urls = new List<string> { url },
                    // CG 图鉴跳过 group<0（CGLibraryView.cs:66），公式图不进图鉴。
                    group = -1,
                };
                // 与剧情图 TrySave 相同的序列化机制（默认设置的 JsonConvert），
                // 保证条目形状与 ModCtrl 读取兼容。
                root[cgId.ToString(CultureInfo.InvariantCulture)] =
                    JObject.Parse(JsonConvert.SerializeObject(entry));
                string saveError;
                if (!TrySaveCgCfg(cfgPath, root, out saveError))
                {
                    result.Error = saveError;
                    return result;
                }
                result.CgId = cgId;
                result.Url = url;
                result.PngPath = pngPath;
                return result;
            }
            catch (Exception e)
            {
                result.Error = "公式资产写入异常：" + e.GetType().Name + ": " + e.Message;
                TypesetLog.Error?.Invoke("[Latex.Assets] " + e);
                return result;
            }
        }

        /// <summary>
        /// 收集全量 talk 里公式段的被引用 cgId：screenEffect [4019/4015, id]，
        /// 与玩家侧相同的 (int) 截断转换（NewTalkView.cs:921/942）。
        /// </summary>
        internal static HashSet<int> CollectReferencedFormulaCgIds(IEnumerable<TalkCfg> talks)
        {
            var result = new HashSet<int>();
            if (talks == null) return result;
            foreach (TalkCfg talk in talks)
            {
                List<float> effect = talk != null ? talk.screenEffect : null;
                if (effect == null || effect.Count < 2) continue;
                int code = (int)effect[0];
                if (code != ShowMiniCg && code != ShowCg) continue;
                int id = (int)effect[1];
                // 新旧两段都算「被引用」：旧段条目在迁移到新段之前必须受保护，
                // 否则 GC 会先把它连同 PNG 删掉，玩家侧立刻破图。
                if (IsAnyFormulaCgId(id)) result.Add(id);
            }
            return result;
        }

        /// <summary>
        /// 公式条目的本机绝对路径：url 只取文件名，与 modRoot 下我们自己的固定布局
        /// 拼接。文件不存在返回 null。
        /// </summary>
        internal static string TryResolveLocalPngPath(string modRoot, string url)
        {
            if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrEmpty(url))
                return null;
            string name = UrlFileName(url);
            if (string.IsNullOrEmpty(name)
                || !name.StartsWith(PngPrefix, StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                string path = Path.Combine(FormulaDirectory(modRoot), name);
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把 CG 表里公式条目的相对 url 换成本机绝对路径，返回改写条数。
        ///
        /// **只用于作者端**（编辑器预览与本局运行时表），落盘的 CGCfg.json 必须保持
        /// 相对 url —— 订阅者要靠它经 GetFullUrl 重定向到自己的工坊安装目录。
        ///
        /// 为什么要这么做：`UISprite.SetTextureUrl` 对相对 url 走
        /// `ModCtrl.GetFullUrl`（UISprite.cs:107-110），而后者依赖
        /// `modPackageIds`——那张表只在启动时按**已启用**的 mod 由 Steam 元数据
        /// 异步建立（ModCtrl.cs:41-56）。作者正在编辑的作品未必启用、元数据未必已
        /// 回调，解析不到就静默回退到 persistentDataPath 并 File.Exists 失败；
        /// 而 `CGView.ShowMiniCG` 随后照常 `SetAlpha(0)+DOFade(1)`，于是 Image 带着
        /// **空 sprite 淡入成一个纯白方块**（作者看到的就是这个）。
        /// 绝对路径直接命中 `Path.IsPathRooted` 分支，绕开整条映射链。
        /// </summary>
        internal static int LocalizeFormulaUrls(
            IDictionary<int, CGCfg> map, string modRoot)
        {
            if (map == null || string.IsNullOrWhiteSpace(modRoot)) return 0;
            int rewritten = 0;
            foreach (KeyValuePair<int, CGCfg> pair in map)
            {
                if (!IsAnyFormulaCgId(pair.Key)) continue;
                CGCfg entry = pair.Value;
                List<string> urls = entry != null ? entry.urls : null;
                if (urls == null || urls.Count == 0) continue;
                if (Path.IsPathRooted(urls[0])) continue;
                string local = TryResolveLocalPngPath(modRoot, urls[0]);
                if (local == null) continue;
                urls[0] = local;
                rewritten++;
            }
            return rewritten;
        }

        /// <summary>
        /// 从 mod 自己的 TalkCfg.json（磁盘现状）收集公式段被引用的 cgId。
        /// GC 的引用集基础：调用方还要并上边车登记与内存里的草稿/视图对话。
        /// 读失败返回 false —— 反查不到引用就一张 PNG 都不能删。
        /// </summary>
        internal static bool TryCollectModTalkFormulaReferences(
            string modRoot, out HashSet<int> referenced, out string error)
        {
            referenced = null;
            error = null;
            if (string.IsNullOrWhiteSpace(modRoot))
            {
                error = "无法确定 Mod 根目录";
                return false;
            }
            // 与持久层同口径：插件固定读写 Cfgs/zh-cn（游戏运行时也只合并该目录）。
            string path = Path.Combine(modRoot, "Cfgs", "zh-cn", "TalkCfg.json");
            try
            {
                if (!File.Exists(path))
                {
                    referenced = new HashSet<int>();
                    return true;
                }
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    referenced = new HashSet<int>();
                    return true;
                }
                Dictionary<string, TalkCfg> map =
                    JsonConvert.DeserializeObject<Dictionary<string, TalkCfg>>(json);
                referenced = CollectReferencedFormulaCgIds(
                    map != null ? map.Values : null);
                return true;
            }
            catch (Exception e)
            {
                error = "读取 TalkCfg.json 失败：" + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 公式资产 GC。删除范围硬限定：CGCfg 公式段中无引用（referencedIds =
        /// talk screenEffect ∪ 边车登记，由调用方并好）的条目，以及
        /// Textures\Formula\formula_*.png 中不再被任何剩余条目 urls 反查到的文件。
        /// 绝不动非公式段条目与 Textures 其他目录；CGCfg 不可读时整体跳过
        /// （无法反查引用就一张 PNG 都不能删）。
        /// </summary>
        internal static FormulaGcResult GcOrphans(string modRoot, ICollection<int> referencedIds)
        {
            var result = new FormulaGcResult();
            try
            {
                if (string.IsNullOrWhiteSpace(modRoot))
                {
                    result.Error = "无法确定 Mod 根目录，公式清理已跳过。";
                    return result;
                }
                string cfgPath = CgCfgPath(modRoot);
                JObject root;
                string readError;
                if (!TryLoadCgCfg(cfgPath, out root, out readError))
                {
                    result.Error = "公式清理已跳过：" + readError;
                    return result;
                }
                SweepStaleCfgTemps(cfgPath);
                string pngDir = FormulaDirectory(modRoot);
                SweepStalePngTemps(pngDir, result.Warnings);

                var removeKeys = new List<string>();
                foreach (JProperty property in root.Properties())
                {
                    int id;
                    if (!int.TryParse(property.Name, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out id)) continue;
                    // 认新旧两段：迁移后旧段条目失去引用，正是要靠这里清掉。
                    if (!IsAnyFormulaCgId(id)) continue;
                    if (referencedIds != null && referencedIds.Contains(id)) continue;
                    removeKeys.Add(property.Name);
                }
                foreach (string key in removeKeys) root.Remove(key);
                if (removeKeys.Count > 0)
                {
                    string saveError;
                    if (!TrySaveCgCfg(cfgPath, root, out saveError))
                    {
                        // 条目移除没写成盘，引用仍在：此时删 PNG 会玩家侧破图，整体中止。
                        result.Error = saveError;
                        return result;
                    }
                    result.RemovedEntries = removeKeys.Count;
                }

                if (Directory.Exists(pngDir))
                {
                    var referencedNames =
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JProperty property in root.Properties())
                    {
                        var entry = property.Value as JObject;
                        var urls = entry != null ? entry["urls"] as JArray : null;
                        if (urls == null) continue;
                        foreach (JToken urlToken in urls)
                        {
                            if (urlToken == null || urlToken.Type != JTokenType.String)
                                continue;
                            string name = UrlFileName((string)urlToken);
                            if (!string.IsNullOrEmpty(name)) referencedNames.Add(name);
                        }
                    }
                    foreach (string file in Directory.GetFiles(pngDir, PngPrefix + "*.png"))
                    {
                        string name = Path.GetFileName(file);
                        // GetFiles 的 8.3 短名通配可能误配（如 .png.tmp* 残留），双重校验。
                        if (name == null
                            || !name.StartsWith(PngPrefix, StringComparison.OrdinalIgnoreCase)
                            || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (referencedNames.Contains(name)) continue;
                        try
                        {
                            File.Delete(file);
                            result.DeletedPngs++;
                        }
                        catch (Exception deleteError)
                        {
                            result.Warnings.Add("孤儿公式 PNG 删除失败（下次保存重试）："
                                + name + " — " + deleteError.Message);
                        }
                    }
                }
                return result;
            }
            catch (Exception e)
            {
                result.Error = "公式清理异常：" + e.GetType().Name + ": " + e.Message;
                TypesetLog.Error?.Invoke("[Latex.Assets.Gc] " + e);
                return result;
            }
        }

        /// <summary>
        /// 容错读：**只有文件不存在**才按空表（新 mod 的第一条公式）。文件存在但内容
        /// 为空/全空白，与解析失败同样拒绝合并：那是异常状态（原版 SaveJsonAsync 是
        /// 非原子的截断覆盖写，崩在写入中途就会留下 0 字节文件），按空表重建会在原子
        /// 替换时把「作者手建 CG 条目」这一层信息永久抹平，而拒写能把问题暴露给作者。
        /// 「绝不动非公式段条目」的前提是能真的读到它们（LTX-D1-03）。
        /// </summary>
        private static bool TryLoadCgCfg(string path, out JObject root, out string error)
        {
            root = null;
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    root = new JObject();
                    return true;
                }
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "CGCfg.json 存在但内容为空（可能是上次保存中途崩溃留下的"
                        + "截断文件），已取消写入以免抹掉其中原有的 CG 条目。"
                        + "请确认该文件内容后删除或修复它，再重试。";
                    return false;
                }
                root = JToken.Parse(json) as JObject;
                if (root == null)
                {
                    error = "CGCfg.json 根节点不是对象，已取消写入以免破坏现有数据。";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                root = null;
                error = "CGCfg.json 读取失败（" + e.Message
                    + "），已取消写入以免覆盖损坏前的数据，请先修复该文件。";
                return false;
            }
        }

        private static bool TrySaveCgCfg(string path, JObject root, out string error)
        {
            error = null;
            string temp = path + CfgTempInfix + Guid.NewGuid().ToString("N");
            string backup = path + CfgBackupSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                WriteDurable(temp, new UTF8Encoding(false).GetBytes(
                    JsonConvert.SerializeObject(root, Formatting.Indented)));
                if (File.Exists(path))
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Replace(temp, path, backup, true);
                }
                else
                {
                    File.Move(temp, path);
                }
                return true;
            }
            catch (Exception e)
            {
                error = "写入 CGCfg.json 失败：" + e.GetType().Name + ": " + e.Message;
                TypesetLog.Error?.Invoke("[Latex.Assets.CgCfg] " + e);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        private static bool TryWritePngDurable(
            string pngDir, string pngPath, byte[] png, out string error)
        {
            error = null;
            string temp = pngPath + PngTempInfix + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(pngDir);
                WriteDurable(temp, png);
                try
                {
                    File.Move(temp, pngPath);
                }
                catch (IOException)
                {
                    // 内容寻址：同名成品必然同内容，并发/残留下的既有文件即视为已写好。
                    if (!File.Exists(pngPath)) throw;
                }
                return true;
            }
            catch (Exception e)
            {
                error = "写入公式 PNG 失败：" + e.GetType().Name + ": " + e.Message;
                TypesetLog.Error?.Invoke("[Latex.Assets.Png] " + e);
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { }
            }
        }

        /// <summary>临时文件必须真实落盘（WriteThrough+Flush(true)，与剧情图 WriteDurable 同法）。</summary>
        private static void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>
        /// 按内容寻址 url 复用既有条目，**全段扫描**：编号不再按事件分窗，若还按窗口
        /// 过滤就永远找不到复用目标，每次保存都新分配一个 id（旧条目变孤儿、编号单调
        /// 膨胀）。只认新段：旧段（1 亿段）条目即使 url 相同也不复用——它们本身就是
        /// 会被原版保存写坏的坏编号，必须让调用方重新分配到新段完成迁移。
        /// </summary>
        private static int FindReusableEntry(JObject root, string url)
        {
            foreach (JProperty property in root.Properties())
            {
                int id;
                if (!int.TryParse(property.Name, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out id)) continue;
                if (!IsFormulaCgId(id)) continue;
                // 历史坏条目（取值链路不安全的 id）不复用，让新分配换到可用编号。
                if (!IsScreenEffectSafeCgId(id)) continue;
                var entry = property.Value as JObject;
                var urls = entry != null ? entry["urls"] as JArray : null;
                if (urls == null || urls.Count == 0) continue;
                JToken first = urls[0];
                if (first == null || first.Type != JTokenType.String) continue;
                if (string.Equals((string)first, url, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            return 0;
        }

        /// <summary>本 mod CGCfg.json 的占用 id：键与条目 id 字段都算（键才是游戏认的）。</summary>
        private static void AddFileIds(JObject root, HashSet<int> occupied)
        {
            foreach (JProperty property in root.Properties())
            {
                int keyId;
                if (int.TryParse(property.Name, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out keyId))
                    occupied.Add(keyId);
                var entry = property.Value as JObject;
                JToken idToken = entry != null ? entry["id"] : null;
                if (idToken != null && idToken.Type == JTokenType.Integer)
                {
                    long fieldId = (long)idToken;
                    if (fieldId >= int.MinValue && fieldId <= int.MaxValue)
                        occupied.Add((int)fieldId);
                }
            }
        }

        private static string UrlFileName(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            int cut = url.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 ? url.Substring(cut + 1) : url;
        }

        /// <summary>崩溃残留的 CGCfg 临时文件清理：留在 Cfgs/ 会随工坊整目录分发。</summary>
        private static void SweepStaleCfgTemps(string cfgPath)
        {
            try
            {
                string directory = Path.GetDirectoryName(cfgPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
                string prefix = Path.GetFileName(cfgPath) + CfgTempInfix;
                foreach (string file in Directory.GetFiles(directory, prefix + "*"))
                {
                    if (!Path.GetFileName(file).StartsWith(
                            prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>崩溃残留的 PNG 临时文件清理：留在 Textures/ 会随工坊整目录分发。</summary>
        private static void SweepStalePngTemps(string pngDir, List<string> warnings)
        {
            try
            {
                if (!Directory.Exists(pngDir)) return;
                foreach (string file in Directory.GetFiles(
                             pngDir, PngPrefix + "*" + PngTempInfix + "*"))
                {
                    string name = Path.GetFileName(file);
                    if (name == null || name.IndexOf(
                            PngTempInfix, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { File.Delete(file); }
                    catch (Exception deleteError)
                    {
                        if (warnings != null)
                            warnings.Add("公式 PNG 临时残留删除失败：" + name
                                + " — " + deleteError.Message);
                    }
                }
            }
            catch { }
        }
    }
}
