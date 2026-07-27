using System;
using System.Collections.Generic;
using System.Text;

namespace StudentAgeTypeset.Latex
{
    /// <summary>
    /// 行内 LaTeX 烘焙结果。Baked 是可直接写入 cfg.content 的成品文本；
    /// Warnings 面向编辑器提示，烘焙本身从不抛异常（保存事务回滚只留给 IO 级意外）。
    ///
    /// BlockingReason 非空表示这一条“不能写盘”：$ 配对已经错位到会把作者的公式
    /// 降级成含花括号的原文（玩家侧 RecordMgr.Replace 会把花括号段整段删掉）。
    /// 此时 Baked 恒等于入参原文、Changed 恒为 false——调用方必须在提交点之前
    /// 中止本次保存（与块级公式物化失败同一纪律），绝不能写半成品。
    /// </summary>
    internal sealed class LatexBakeResult
    {
        internal readonly string Baked;
        internal readonly bool Changed;
        internal readonly IReadOnlyList<string> Warnings;
        internal readonly string BlockingReason;

        internal LatexBakeResult(
            string baked, bool changed, IReadOnlyList<string> warnings,
            string blockingReason = null)
        {
            Baked = baked;
            Changed = changed;
            Warnings = warnings;
            BlockingReason = blockingReason;
        }

        /// <summary>true = 必须在提交点之前拦下本次保存（见 BlockingReason）。</summary>
        internal bool HasBlockingIssue
        {
            get { return BlockingReason != null; }
        }
    }

    /// <summary>
    /// 行内 LaTeX（$...$）→ TMP 富文本转译器。纯 C#、零 Unity/零第三方依赖：
    /// 会被 StoryGraphEditSession 的保存烘焙调用，其测试工程不引用任何 Unity 程序集。
    /// 硬性约束（LATEX-DESIGN-2026-07-26.md §2）：
    /// - 输出绝不含花括号（含全角｛｝）：RecordMgr.Replace 按 (\{|｛)(.*?)(\}|｝) 把任何
    ///   括号段当占位符替换/删除（RecordMgr.cs:288-311）。数学段内花括号全部消耗，
    ///   数学段外文本（含 {0} 等游戏占位符）绝对不动。$ 配对错位会把作者本想写的
    ///   数学段降级成外层原文、连花括号一起带进产物——这种情况不返回半成品，改为
    ///   BlockingReason 阻断保存（见 LatexBakeResult）。
    /// - 数学段内遇到 {0} / {itemId,count} 这类游戏占位符形状：按字面保留并告警，
    ///   不当花括号组吃掉（吃掉会把占位符降级成裸数字，且不可逆）。
    /// - 幂等：Bake(Bake(x)).Baked == Bake(x).Baked。保底烘焙（ModEvtDeleteSavePatch）
    ///   必然会把已烘焙文本再过一遍，任何成对裸 $ 输出都会被二次烘焙误配对成新数学段，
    ///   所以字面美元符在 TMP 模式烘焙成 noparse 哨兵（TMP 照常显示 $），扫描器整体跳过。
    /// - 特殊 Unicode 限于 Source Han 覆盖集：上下标走 TMP 标签而非 U+2070 系字符；
    ///   希腊字母变体（\varepsilon 等）统一映射到基础字形。
    /// </summary>
    internal static class LatexInlineTranspiler
    {
        // 字面 $ 的唯一幂等安全形态（见类注释）；扫描/配对时按整体跳过。
        private const string DollarSentinel = "<noparse>$</noparse>";
        // TMP 遇到无法识别的 "<" 会连同后续内容按字面回吐、殃及相邻合法标签，必须隔离。
        private const string LessThanSentinel = "<noparse><</noparse>";
        // 递归下降的统一栈深上限（花括号组 + 命令参数共用一个计数），超限后展平继续。
        // 任何输入下递归深度都必须是常数上界：StackOverflowException 不可 catch。
        private const int MaxGroupDepth = 32;

        private static readonly string[] NoWarnings = new string[0];

        // 命令 → 单字符映射（§2 表格）。变体希腊字母映射到基础字形：
        // ϑ ϖ ϱ ϵ 等数学变体不在 Source Han 覆盖集，φ 统一用 U+03C6。
        private static readonly Dictionary<string, string> CommandChars =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "alpha", "α" }, { "beta", "β" }, { "gamma", "γ" }, { "delta", "δ" },
            { "epsilon", "ε" }, { "zeta", "ζ" }, { "eta", "η" }, { "theta", "θ" },
            { "iota", "ι" }, { "kappa", "κ" }, { "lambda", "λ" }, { "mu", "μ" },
            { "nu", "ν" }, { "xi", "ξ" }, { "omicron", "ο" }, { "pi", "π" },
            { "rho", "ρ" }, { "sigma", "σ" }, { "tau", "τ" }, { "upsilon", "υ" },
            { "phi", "φ" }, { "chi", "χ" }, { "psi", "ψ" }, { "omega", "ω" },
            { "varepsilon", "ε" }, { "vartheta", "θ" }, { "varpi", "π" },
            { "varrho", "ρ" }, { "varsigma", "ς" }, { "varphi", "φ" },
            // 大写全集：LaTeX 未定义 \Alpha 等，但作者直觉会写，一并支持。
            { "Alpha", "Α" }, { "Beta", "Β" }, { "Gamma", "Γ" }, { "Delta", "Δ" },
            { "Epsilon", "Ε" }, { "Zeta", "Ζ" }, { "Eta", "Η" }, { "Theta", "Θ" },
            { "Iota", "Ι" }, { "Kappa", "Κ" }, { "Lambda", "Λ" }, { "Mu", "Μ" },
            { "Nu", "Ν" }, { "Xi", "Ξ" }, { "Omicron", "Ο" }, { "Pi", "Π" },
            { "Rho", "Ρ" }, { "Sigma", "Σ" }, { "Tau", "Τ" }, { "Upsilon", "Υ" },
            { "Phi", "Φ" }, { "Chi", "Χ" }, { "Psi", "Ψ" }, { "Omega", "Ω" },
            { "times", "×" }, { "div", "÷" }, { "pm", "±" }, { "mp", "∓" },
            { "cdot", "·" }, { "leq", "≤" }, { "geq", "≥" }, { "neq", "≠" },
            { "approx", "≈" }, { "equiv", "≡" }, { "infty", "∞" }, { "to", "→" },
            { "Rightarrow", "⇒" }, { "in", "∈" }, { "cup", "∪" }, { "cap", "∩" },
            { "sum", "∑" }, { "prod", "∏" }, { "int", "∫" }, { "degree", "°" },
            { "ldots", "…" },
            // 常用别名
            { "le", "≤" }, { "ge", "≥" }, { "ne", "≠" },
            { "rightarrow", "→" }, { "cdots", "…" }, { "dots", "…" },
        };

        // legacy Text 降级模式的 Unicode 上/下标字符集（§2：无法表达的原样保留+警告）。
        private static readonly Dictionary<char, char> SupChars = new Dictionary<char, char>
        {
            { '0', '⁰' }, { '1', '¹' }, { '2', '²' }, { '3', '³' }, { '4', '⁴' },
            { '5', '⁵' }, { '6', '⁶' }, { '7', '⁷' }, { '8', '⁸' }, { '9', '⁹' },
            { '+', '⁺' }, { '-', '⁻' }, { 'n', 'ⁿ' },
        };

        private static readonly Dictionary<char, char> SubChars = new Dictionary<char, char>
        {
            { '0', '₀' }, { '1', '₁' }, { '2', '₂' }, { '3', '₃' }, { '4', '₄' },
            { '5', '₅' }, { '6', '₆' }, { '7', '₇' }, { '8', '₈' }, { '9', '₉' },
            { '+', '₊' }, { '-', '₋' },
        };

        /// <summary>TMP 标签模式：sup/sub/i 标签 + Unicode 运算符。</summary>
        internal static LatexBakeResult Bake(string source)
        {
            return BakeCore(source, true);
        }

        /// <summary>
        /// legacy Text 降级模式（选项按钮等非 TMP 组件）：不产出任何富文本标签，
        /// 上下标尽量用 Unicode 上标下标字符，无法表达的保留 ^/_ 形式并告警。
        /// 注意：字面 \$ 在此模式只能烘成裸 $（legacy Text 会把 noparse 哨兵按字面显示），
        /// 同一文本出现两个以上字面 $ 时二次烘焙可能误配对——已知取舍，TMP 主路径无此问题。
        /// </summary>
        internal static LatexBakeResult BakeUnicodeOnly(string source)
        {
            return BakeCore(source, false);
        }

        /// <summary>快速判断文本是否还含未烘焙的 $ 段（哨兵里的 $ 不算；\$ 转义算，它待烘焙）。</summary>
        internal static bool ContainsLatex(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (IsDollarSentinelAt(text, i))
                {
                    i += DollarSentinel.Length - 1;
                    continue;
                }
                if (text[i] == '$') return true;
            }
            return false;
        }

        /// <summary>
        /// 收集烘焙产物中的非 ASCII 字符（去重、码点升序），供编辑器做 TMP HasCharacter
        /// 字形探测。空白字符（窄空格等）排除：TMP 对 Unicode 空格自行合成，无需字形。
        /// </summary>
        internal static IReadOnlyCollection<char> CollectSpecialChars(string baked)
        {
            var set = new SortedSet<char>();
            if (string.IsNullOrEmpty(baked)) return set;
            for (int i = 0; i < baked.Length; i++)
            {
                char c = baked[i];
                if (c <= '\u007F') continue;
                if (char.IsWhiteSpace(c)) continue;
                set.Add(c);
            }
            return set;
        }

        private static LatexBakeResult BakeCore(string source, bool tmpTags)
        {
            if (string.IsNullOrEmpty(source))
                return new LatexBakeResult(source, false, NoWarnings);
            var warnings = new List<string>();
            var sb = new StringBuilder(source.Length + 16);
            int n = source.Length;
            int i = 0;
            // $ 配对错位的判据（见 BlockingReason）：出现了配不上对的 $，且第一个 $
            // 之后的外层原文里还留着花括号——那些花括号极可能来自被降级的数学段。
            bool sawDollar = false;
            bool sawUnpairedDollar = false;
            bool sawOuterBraceAfterDollar = false;
            while (i < n)
            {
                if (IsDollarSentinelAt(source, i))
                {
                    // 已烘焙哨兵整体跳过：其中的 $ 不参与配对（幂等关键）。
                    sb.Append(DollarSentinel);
                    i += DollarSentinel.Length;
                    continue;
                }
                char c = source[i];
                if (c == '\\' && i + 1 < n && source[i + 1] == '$')
                {
                    sb.Append(tmpTags ? DollarSentinel : "$");
                    i += 2;
                    continue;
                }
                if (c == '$')
                {
                    sawDollar = true;
                    int close = FindClosingDollar(source, i + 1);
                    if (close < 0)
                    {
                        sawUnpairedDollar = true;
                        warnings.Add("未闭合的 $，已原样保留（字面美元符请写 \\$）");
                        sb.Append('$');
                        i++;
                        continue;
                    }
                    if (close == i + 1)
                    {
                        warnings.Add("空的 $$ 段（不支持 $$...$$ 块级语法，块级公式请用剧情图附件），已原样保留");
                        sb.Append("$$");
                        i = close + 1;
                        continue;
                    }
                    string segment = source.Substring(i + 1, close - i - 1);
                    var parser = new MathParser(segment, tmpTags, warnings);
                    sb.Append(parser.ParseAll());
                    i = close + 1;
                    continue;
                }
                if (sawDollar && IsBraceChar(c)) sawOuterBraceAfterDollar = true;
                sb.Append(c);
                i++;
            }
            string baked = IsolateBackslashDollar(sb.ToString(), tmpTags);
            if (sawUnpairedDollar && sawOuterBraceAfterDollar)
            {
                // 半成品比原文更糟：作者本想写的公式已经被降级成外层原文，花括号
                // 会被 RecordMgr.Replace 整段删掉，而 $ 原文却已经被吃掉几个。
                // 一律原样返回 + 阻断保存，让作者先把字面美元符写成 \$。
                string reason =
                    "有一个 $ 找不到配对，同时数学段外还留着花括号 {…}：无法判断哪些 $ 是"
                    + "公式定界符，已放弃本次烘焙（花括号会被游戏的占位符机制整段删除，"
                    + "公式会当场消失）。字面美元符请写成 \\$，再检查 $…$ 是否成对。";
                // 排在最前：编辑器/toast 只展示前两条告警，阻断理由必须挤得进去。
                warnings.Insert(0, reason);
                return new LatexBakeResult(source, false, warnings, reason);
            }
            bool changed = !string.Equals(baked, source, StringComparison.Ordinal);
            return new LatexBakeResult(
                baked, changed, warnings.Count == 0 ? (IReadOnlyList<string>)NoWarnings : warnings);
        }

        private static bool IsBraceChar(char c)
        {
            return c == '{' || c == '}' || c == '｛' || c == '｝';
        }

        /// <summary>
        /// 幂等护栏：产物里若出现“\ 紧跟裸 $”，第二遍烘焙会把它当成 \$ 转义、吞掉那个
        /// 反斜杠并换成哨兵（未知命令 \\ 回吐 + 未闭合 $ / $$ 空段都可能拼出这个序列）。
        /// 把该 $ 换成哨兵后，反斜杠后面是 '&lt;'，二次烘焙不再改写。
        /// 降级模式没有哨兵可用（legacy Text 会把标签按字面显示），只能保持现状——
        /// 该模式的裸 $ 二次配对风险是类注释里已承认的取舍。
        /// </summary>
        private static string IsolateBackslashDollar(string baked, bool tmpTags)
        {
            if (!tmpTags || string.IsNullOrEmpty(baked)) return baked;
            StringBuilder sb = null;
            for (int i = 0; i < baked.Length; i++)
            {
                char c = baked[i];
                if (c == '$' && i > 0 && baked[i - 1] == '\\')
                {
                    if (sb == null)
                        sb = new StringBuilder(baked, 0, i, baked.Length + DollarSentinel.Length);
                    sb.Append(DollarSentinel);
                    continue;
                }
                if (sb != null) sb.Append(c);
            }
            return sb == null ? baked : sb.ToString();
        }

        /// <summary>
        /// 游戏占位符形状探测：{n} 与 {itemId,count}——RecordMgr.Replace 只对这两种
        /// 纯整数形状做替换（RecordMgr.cs:294-303），其余走 GetRecordTag 命名标签。
        /// 命中时 next 指向 '}' 之后。
        /// </summary>
        private static bool TryReadPlaceholder(string s, int start, out int next)
        {
            next = start;
            if (s == null || start >= s.Length || s[start] != '{') return false;
            int i = start + 1;
            if (!SkipDigits(s, ref i)) return false;
            if (i < s.Length && s[i] == ',')
            {
                i++;
                if (!SkipDigits(s, ref i)) return false;
            }
            if (i >= s.Length || s[i] != '}') return false;
            next = i + 1;
            return true;
        }

        private static bool SkipDigits(string s, ref int i)
        {
            int from = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            return i > from;
        }

        /// <summary>找配对的收尾 $；跳过 \$ 转义与已烘焙哨兵。找不到返回 -1。</summary>
        private static int FindClosingDollar(string s, int from)
        {
            int i = from;
            while (i < s.Length)
            {
                if (IsDollarSentinelAt(s, i))
                {
                    i += DollarSentinel.Length;
                    continue;
                }
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '$')
                {
                    i += 2;
                    continue;
                }
                if (c == '$') return i;
                i++;
            }
            return -1;
        }

        private static bool IsDollarSentinelAt(string s, int i)
        {
            // CompareOrdinal 对越界长度只比较可用部分，截断必不相等，无需预判长度。
            return string.CompareOrdinal(s, i, DollarSentinel, 0, DollarSentinel.Length) == 0;
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>
        /// 单个数学段的递归下降解析器。token：\命令 / ^ / _ / {..} / 字面字符。
        /// 花括号只做分组永不输出；未知内容原样保留 + 警告，从不抛异常。
        /// </summary>
        private sealed class MathParser
        {
            private struct Rendered
            {
                internal string Text;
                internal bool Simple;   // 单原子且无脚标：\frac、\sqrt 据此省括号
                internal bool Missing;  // 参数缺失（与"空组 {}"区分：空组渲染为空，缺参保留符号）
            }

            private readonly string _s;
            private readonly bool _tmp;
            private readonly List<string> _warnings;
            private int _i;
            private int _depth;
            private bool _depthWarned;

            internal MathParser(string segment, bool tmpTags, List<string> warnings)
            {
                _s = segment;
                _tmp = tmpTags;
                _warnings = warnings;
            }

            internal string ParseAll()
            {
                int atoms;
                bool scripts;
                bool soleSimple;
                return ParseList('\0', out atoms, out scripts, out soleSimple);
            }

            /// <summary>
            /// terminator 为 '}' 时解析花括号组内部，'\0' 时到段尾。
            /// soleAtomSimple：唯一原子自身是否简单——{\frac{1}{2}} 虽只有一个原子，
            /// 但作 \frac/\sqrt 参数时仍须加括号，不能只看原子数。
            /// </summary>
            private string ParseList(
                char terminator, out int atomCount, out bool anyScripts, out bool soleAtomSimple)
            {
                var sb = new StringBuilder();
                atomCount = 0;
                anyScripts = false;
                soleAtomSimple = false;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '}')
                    {
                        _i++;
                        if (terminator == '}') return sb.ToString();
                        _warnings.Add("数学段里有多余的 }，已忽略（输出不能含花括号）");
                        continue;
                    }
                    if (char.IsWhiteSpace(c))
                    {
                        // 空格原样保留（不做 LaTeX 吞空格，作者所见即所得），且不算原子：
                        // \frac{a } 不因空格被判为复合项加括号。
                        sb.Append(c);
                        _i++;
                        continue;
                    }
                    if (c == '^' || c == '_')
                    {
                        // 无基底的脚标：LaTeX 会报错，这里宽容渲染。
                        _i++;
                        sb.Append(RenderScript(c));
                        anyScripts = true;
                        continue;
                    }
                    bool simple;
                    // 原子位（不是命令/脚标参数位）才把 {0} 当游戏占位符看待。
                    string baseText = ParseBase(out simple, true);
                    if (atomCount == 0) soleAtomSimple = simple;
                    atomCount++;
                    while (_i < _s.Length && (_s[_i] == '^' || _s[_i] == '_'))
                    {
                        char op = _s[_i];
                        _i++;
                        baseText += RenderScript(op);
                        anyScripts = true;
                    }
                    sb.Append(baseText);
                }
                if (terminator == '}')
                    _warnings.Add("数学段里的 { 缺少配对的 }");
                return sb.ToString();
            }

            /// <summary>
            /// 单个原子。递归深度在这里统一计数：花括号组与命令参数
            /// （ParseCommand→RenderFrac/RenderSqrt→ParseArg→ParseBase）走的是同一条
            /// 递归，只对 '{' 计数时 "\sqrt\sqrt…" 这类深链会把栈打穿——StackOverflow
            /// 在 Mono/.NET 上无法 catch，保存事务的 try 全部失效、进程当场死。
            /// allowPlaceholder 仅在列表原子位为 true：参数位的 {12} 必须继续当数学
            /// 分组（x^{12} 是上标而不是占位符）。
            /// </summary>
            private string ParseBase(out bool simple, bool allowPlaceholder = false)
            {
                simple = false;
                if (_i >= _s.Length) return string.Empty;
                if (_depth >= MaxGroupDepth)
                {
                    if (!_depthWarned)
                    {
                        _warnings.Add("数学段嵌套过深（花括号或命令参数），已展平处理");
                        _depthWarned = true;
                    }
                    // 必须推进：调用方按“每次至少消耗一个字符”循环，否则死循环。
                    _i++;
                    return string.Empty;
                }
                _depth++;
                try { return ParseAtom(out simple, allowPlaceholder); }
                finally { _depth--; }
            }

            private string ParseAtom(out bool simple, bool allowPlaceholder)
            {
                simple = false;
                if (IsDollarSentinelAt(_s, _i))
                {
                    // 防御：正常拆段不会把哨兵留在段内，遇到则原样通过。
                    _i += DollarSentinel.Length;
                    return _tmp ? DollarSentinel : "$";
                }
                char c = _s[_i];
                if (c == '{')
                {
                    int placeholderEnd;
                    if (allowPlaceholder && TryReadPlaceholder(_s, _i, out placeholderEnd))
                    {
                        // {0} / {1,2} 是游戏占位符：当花括号组吃掉会把它降级成裸数字
                        // 且不可逆。按字面保留（RecordMgr 照常替换），并明确告警——
                        // 它出现在数学段里，多半说明 $ 配对错位了。
                        string literal = _s.Substring(_i, placeholderEnd - _i);
                        _warnings.Add("数学段里的 " + literal + " 是游戏占位符，已按字面保留；"
                            + "如果本意是数学分组，请检查 $ 是否配对错位");
                        _i = placeholderEnd;
                        return literal;
                    }
                    _i++;
                    int atoms;
                    bool scripts;
                    bool soleSimple;
                    string inner = ParseList('}', out atoms, out scripts, out soleSimple);
                    simple = atoms == 1 && !scripts && soleSimple;
                    return inner;
                }
                if (c == '\\') return ParseCommand(out simple);
                _i++;
                return RenderChar(c, _i - 1, out simple);
            }

            private string RenderChar(char c, int pos, out bool simple)
            {
                simple = true;
                if (c == '<' && _tmp) return LessThanSentinel;
                if (c == '$') return _tmp ? DollarSentinel : "$";
                if (c == '｛' || c == '｝')
                {
                    // 全角花括号同样会被 RecordMgr 当占位符吞掉，替换成圆括号。
                    _warnings.Add("数学段里的全角花括号 " + c + " 无法输出（会被游戏占位符机制吞掉），已替换为圆括号");
                    return c == '｛' ? "(" : ")";
                }
                if (_tmp && IsAsciiLetter(c) && IsIsolatedLetter(pos))
                    return "<i>" + c + "</i>";
                return c.ToString();
            }

            /// <summary>单个拉丁字母变量才斜体；相邻成串的字母（km/h、sin 之类）按字面透传。</summary>
            private bool IsIsolatedLetter(int pos)
            {
                bool prevLetter = pos > 0 && IsAsciiLetter(_s[pos - 1]);
                bool nextLetter = pos + 1 < _s.Length && IsAsciiLetter(_s[pos + 1]);
                return !prevLetter && !nextLetter;
            }

            private string ParseCommand(out bool simple)
            {
                simple = false;
                _i++; // 跳过 '\'
                if (_i >= _s.Length)
                {
                    _warnings.Add("孤立的 \\，已原样保留");
                    return "\\";
                }
                char c = _s[_i];
                if (!IsAsciiLetter(c))
                {
                    _i++;
                    switch (c)
                    {
                        case '$':
                            simple = true;
                            return _tmp ? DollarSentinel : "$";
                        case ',':
                            simple = true;
                            return _tmp ? "\u2009" : " ";
                        case ';':
                            simple = true;
                            return _tmp ? "\u2005" : " ";
                        case '{':
                        case '}':
                            // 字面花括号无法输出（RecordMgr 占位符机制），退而求其次。
                            _warnings.Add("\\" + c + " 无法输出花括号（会被游戏占位符机制吞掉），已替换为圆括号");
                            simple = true;
                            return c == '{' ? "(" : ")";
                        default:
                            _warnings.Add("未知命令 \\" + c + "，已原样保留，建议改用块级公式");
                            return "\\" + c;
                    }
                }
                int nameStart = _i;
                while (_i < _s.Length && IsAsciiLetter(_s[_i])) _i++;
                string name = _s.Substring(nameStart, _i - nameStart);
                string mapped;
                if (CommandChars.TryGetValue(name, out mapped))
                {
                    simple = true;
                    return mapped;
                }
                switch (name)
                {
                    case "quad":
                        simple = true;
                        return _tmp ? "\u2003" : "  ";
                    case "frac": return RenderFrac();
                    case "sqrt": return RenderSqrt();
                    case "text": return RenderText(out simple);
                }
                _warnings.Add("未知命令 \\" + name + "，已原样保留，建议改用块级公式");
                return "\\" + name;
            }

            private string RenderFrac()
            {
                Rendered a = ParseArg("\\frac");
                Rendered b = ParseArg("\\frac");
                if (a.Simple && b.Simple) return a.Text + "/" + b.Text;
                // 复合项两侧都加括号（§2 示例 (a+1)/(b)），避免 a+1/b 歧义。
                return "(" + a.Text + ")/(" + b.Text + ")";
            }

            private string RenderSqrt()
            {
                Rendered a = ParseArg("\\sqrt");
                if (a.Simple) return "√" + a.Text;
                return "√(" + a.Text + ")";
            }

            /// <summary>\text{..}：字面透传；花括号剥除（输出硬约束），嵌套括号按层级配对。</summary>
            private string RenderText(out bool simple)
            {
                simple = false;
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                if (_i >= _s.Length || _s[_i] != '{')
                {
                    _warnings.Add("\\text 缺少 {..} 参数");
                    return string.Empty;
                }
                _i++;
                var sb = new StringBuilder();
                int depth = 1;
                int visible = 0;
                while (_i < _s.Length)
                {
                    if (IsDollarSentinelAt(_s, _i))
                    {
                        sb.Append(_tmp ? DollarSentinel : "$");
                        _i += DollarSentinel.Length;
                        visible++;
                        continue;
                    }
                    char c = _s[_i];
                    if (c == '\\' && _i + 1 < _s.Length && _s[_i + 1] == '$')
                    {
                        sb.Append(_tmp ? DollarSentinel : "$");
                        _i += 2;
                        visible++;
                        continue;
                    }
                    if (c == '{')
                    {
                        depth++;
                        _i++;
                        continue;
                    }
                    if (c == '}')
                    {
                        depth--;
                        _i++;
                        if (depth == 0) break;
                        continue;
                    }
                    if (c == '<' && _tmp) sb.Append(LessThanSentinel);
                    else if (c == '｛') sb.Append('(');
                    else if (c == '｝') sb.Append(')');
                    else sb.Append(c);
                    visible++;
                    _i++;
                }
                if (depth != 0) _warnings.Add("\\text 的 { 缺少配对的 }");
                simple = visible == 1;
                return sb.ToString();
            }

            /// <summary>命令/脚标参数：{..} 组或单 token；参数前空格按 LaTeX 语义忽略。</summary>
            private Rendered ParseArg(string owner)
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
                var r = new Rendered { Text = string.Empty, Simple = true };
                if (_i >= _s.Length || _s[_i] == '}' || _s[_i] == '^' || _s[_i] == '_')
                {
                    _warnings.Add(owner + " 缺少参数");
                    r.Missing = true;
                    return r;
                }
                bool simple;
                r.Text = ParseBase(out simple);
                r.Simple = simple;
                return r;
            }

            private string RenderScript(char op)
            {
                Rendered arg = ParseArg(op.ToString());
                if (arg.Missing) return op.ToString(); // 已告警；^/_ 本身可安全字面输出
                if (arg.Text.Length == 0) return string.Empty; // 空组 x^{} 渲染为空
                if (_tmp)
                {
                    return op == '^'
                        ? "<sup>" + arg.Text + "</sup>"
                        : "<sub>" + arg.Text + "</sub>";
                }
                string mappedText;
                if (TryMapScript(arg.Text, op == '^' ? SupChars : SubChars, out mappedText))
                    return mappedText;
                _warnings.Add((op == '^' ? "上标 \"" : "下标 \"") + arg.Text +
                    "\" 无法用 Unicode 上下标字符表达，已保留 " + op + " 形式");
                return op + arg.Text;
            }

            private static bool TryMapScript(
                string text, Dictionary<char, char> map, out string mapped)
            {
                var sb = new StringBuilder(text.Length);
                for (int i = 0; i < text.Length; i++)
                {
                    char m;
                    if (!map.TryGetValue(text[i], out m))
                    {
                        mapped = null;
                        return false;
                    }
                    sb.Append(m);
                }
                mapped = sb.ToString();
                return true;
            }
        }
    }
}
