using System.Text;

namespace LightTextEditor.Core.Text;

/// <summary>可以展开（还原为多行）和压缩（合并为单行）的文件类型。</summary>
public enum CodeLanguage { Json, Css, JavaScript }

/// <summary>展开 / 压缩的结果：成功时 <see cref="Text"/> 为新文本；失败时为 null，<see cref="Error"/> 说明原因。</summary>
public readonly record struct LayoutResult(string? Text, string? Error, int KeptLineBreaks = 0)
{
    public bool Succeeded => Text is not null;

    internal static LayoutResult Fail(string error) => new(null, error);

    /// <summary>失败，并指出位置（第几行第几列）。</summary>
    internal static LayoutResult Fail(string error, string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset; i++)
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        return new(null, $"{error.TrimEnd('。')}（第 {line} 行第 {offset - lineStart + 1} 列）。");
    }
}

/// <summary>
/// 展开（还原为多行）和压缩（合并为单行）JSON、CSS、JavaScript。
/// <para>
/// 只增删字符串、注释、正则表达式以外的空白（JavaScript 压缩时可能补分号，见下），不改动其他任何字符；遇到拿不准的写法（没有结束的字符串、
/// 括号不配对、JavaScript 里的 JSX 等）就不改动并说明原因，保证不会改变代码的含义：
/// </para>
/// <list type="bullet">
/// <item>JSON：先完整校验（<see cref="JsonText"/>）。</item>
/// <item>CSS：在 { ; } 处换行；括号里（如 url(data:…;…)）和自定义属性（--name: 值）的值不动；
///   冒号前后不加减空白（选择器里 a:hover 和 a :hover 含义不同）。</item>
/// <item>JavaScript：只在 { ; } 和对象里的逗号处换行（不会触发自动插入分号）；原有的换行全部保留。
///   压缩时只去掉确定不影响含义的换行（如 { ; , 之后、} 之前），} 后面换行接新语句时换成分号（与自动插入分号相同）；
///   依赖换行的地方（省略分号、return 后换行、行注释等）保留并计数。</item>
/// </list>
/// HTML / XML 没有支持：标签之间加减空白会改变显示的文字。
/// </summary>
public static class CodeLayout
{
    /// <summary>
    /// 按后缀和内容判断文件类型：.json / .css / .js .mjs .cjs，其他后缀的内容以 { 或 [ 开头时按 JSON 处理。不支持时为 null。
    /// </summary>
    public static CodeLanguage? LanguageFor(string extension, string text)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".json": return CodeLanguage.Json;
            case ".css": return CodeLanguage.Css;
            case ".js" or ".mjs" or ".cjs": return CodeLanguage.JavaScript;
        }
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length && text[i] is '{' or '[' ? CodeLanguage.Json : null;
    }

    public static string DisplayName(CodeLanguage language) => language switch
    {
        CodeLanguage.Json => "JSON",
        CodeLanguage.Css => "CSS",
        _ => "JavaScript",
    };

    /// <summary>展开为多行。<paramref name="indent"/> 为一级缩进，<paramref name="newLine"/> 为换行符。</summary>
    public static LayoutResult Expand(CodeLanguage language, string text, string indent, string newLine) =>
        Run(language, text, indent, newLine, expand: true, dryRun: false);

    /// <summary>压缩为单行。不能去掉的换行（见类说明）用 <paramref name="newLine"/>。</summary>
    public static LayoutResult Compact(CodeLanguage language, string text, string newLine = "\n") =>
        Run(language, text, "", newLine, expand: false, dryRun: false);

    /// <summary>能否展开（只分析，不生成结果；用于打开文件时判断要不要提示）。</summary>
    public static bool CanExpand(CodeLanguage language, string text) =>
        Run(language, text, "", "\n", expand: true, dryRun: true).Error is null;

    private static LayoutResult Run(CodeLanguage language, string text, string indent, string newLine, bool expand, bool dryRun)
    {
        switch (language)
        {
            case CodeLanguage.Json:
                if (!JsonText.Validate(text, out var error)) return LayoutResult.Fail("不是有效的 JSON：" + error);
                if (dryRun) return new LayoutResult("", null);
                return new LayoutResult(expand ? JsonText.Format(text, indent, newLine) : JsonText.Minify(text), null);
            case CodeLanguage.Css:
                return new CssLayout(text, new Writer(dryRun, indent, newLine)).Run(expand);
            default:
                return new JsLayout(text, new Writer(dryRun, indent, newLine)).Run(expand);
        }
    }

    // ───────────────────────── 输出 ─────────────────────────

    /// <summary>输出：只分析时不保存文字，只记录是否在行首。</summary>
    private sealed class Writer(bool dryRun, string indent, string newLine)
    {
        private readonly StringBuilder? _sb = dryRun ? null : new StringBuilder();
        private int _lineStart;

        /// <summary>当前在一行的开头（只有缩进）。</summary>
        public bool AtLineStart { get; private set; } = true;

        public bool IsEmpty { get; private set; } = true;

        public char Last { get; private set; }

        public void Append(char c)
        {
            _sb?.Append(c);
            AtLineStart = false;
            IsEmpty = false;
            Last = c;
        }

        public void Append(string s, int start, int length)
        {
            if (length <= 0) return;
            _sb?.Append(s, start, length);
            AtLineStart = false;
            IsEmpty = false;
            Last = s[start + length - 1];
        }

        public void Append(string s) => Append(s, 0, s.Length);

        /// <summary>换行并缩进到 <paramref name="depth"/> 层；已经在行首时只调整缩进（不产生空行）。</summary>
        public void NewLine(int depth)
        {
            if (IsEmpty) return;
            if (AtLineStart)
            {
                if (_sb is not null) _sb.Length = _lineStart;
            }
            else
            {
                TrimEndSpaces();
                _sb?.Append(newLine);
                _lineStart = _sb?.Length ?? 0;
            }
            for (int d = 0; d < depth; d++) _sb?.Append(indent);
            AtLineStart = true;
            Last = '\n';
        }

        /// <summary>压缩时保留的换行（不缩进）。</summary>
        public void RawNewLine()
        {
            TrimEndSpaces();
            _sb?.Append(newLine);
            _lineStart = _sb?.Length ?? 0;
            AtLineStart = true;
            Last = '\n';
        }

        public void TrimEndSpaces()
        {
            if (_sb is null) return;
            int length = _sb.Length;
            while (length > _lineStart && _sb[length - 1] is ' ' or '\t') length--;
            _sb.Length = length;
        }

        public string Result() => _sb?.ToString() ?? "";
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v' or ' ' or '﻿';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '\\' || c >= 0x80 && !char.IsWhiteSpace(c);

    /// <summary>引号括起的字符串（不能跨行，除非用 \ 续行）。返回结尾引号的位置，没有结束时为 -1。</summary>
    private static int SkipString(string s, int i)
    {
        char quote = s[i];
        for (int j = i + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (c == '\\') { j++; continue; }
            if (c == quote) return j;
            if (c is '\n' or '\r') return -1;
        }
        return -1;
    }

    /// <summary>/* … */ 注释，返回结尾 / 的位置，没有结束时为 -1。</summary>
    private static int SkipBlockComment(string s, int i)
    {
        int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return end < 0 ? -1 : end + 1;
    }

    // ───────────────────────── CSS ─────────────────────────

    private sealed class CssLayout(string s, Writer w)
    {
        public LayoutResult Run(bool expand)
        {
            int depth = 0, paren = 0, kept = 0;
            bool declarationStart = true; // 在 { ; 之后（或开头），下一个是声明的开头
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (declarationStart && !IsSpace(c) && paren == 0 && c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    // 自定义属性 --name: 值：值原样保存（浏览器按原文保存，空格也算值的一部分），一个字也不改
                    int end = CustomPropertyEnd(i);
                    if (end < 0) return LayoutResult.Fail("自定义属性的值没有结束。", s, i);
                    if (!expand) kept += CountLineBreaks(s, i, end - 1);
                    w.Append(s, i, end - i);
                    i = end - 1;
                    declarationStart = false;
                    continue;
                }
                if (!IsSpace(c)) declarationStart = c is '{' or ';';
                if (c is '"' or '\'')
                {
                    int end = SkipString(s, i);
                    if (end < 0) return LayoutResult.Fail("有没有结束的字符串。", s, i);
                    w.Append(s, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = SkipBlockComment(s, i);
                    if (end < 0) return LayoutResult.Fail("有没有结束的注释。", s, i);
                    if (!expand) kept += CountLineBreaks(s, i, end);
                    w.Append(s, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (IsSpace(c))
                {
                    int j = i;
                    bool lineBreak = false;
                    while (j < s.Length && IsSpace(s[j])) lineBreak |= s[j++] is '\n' or '\r';
                    i = j - 1;
                    if (w.IsEmpty || w.AtLineStart || j >= s.Length) continue;
                    char next = s[j];
                    if (expand)
                    {
                        if (paren == 0 && next is '{' or '}' or ';') continue; // 由那个符号处理
                        if (paren == 0 && lineBreak) w.NewLine(depth);
                        else w.Append(' ');
                    }
                    // 逗号前后的空格不去掉：含 var() 的值浏览器按原文保存，空格也算值的一部分
                    else if (!(w.Last is '{' or '}' or ';' || next is '{' or '}' or ';'))
                    {
                        w.Append(' ');
                    }
                    continue;
                }
                if (paren > 0)
                {
                    if (c == '(') paren++;
                    else if (c == ')') paren--;
                    w.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '(':
                        paren++;
                        w.Append(c);
                        break;
                    case ')':
                        return LayoutResult.Fail("括号不配对。", s, i);
                    case '{':
                        if (expand)
                        {
                            w.TrimEndSpaces();
                            if (!w.AtLineStart) w.Append(' ');
                        }
                        w.Append('{');
                        int next = NextNonSpace(i + 1);
                        if (next < s.Length && s[next] == '}')
                        {
                            w.Append('}');
                            i = next;
                            if (expand) AfterBlock(ref i, depth);
                        }
                        else
                        {
                            depth++;
                            if (expand) w.NewLine(depth);
                        }
                        break;
                    case '}':
                        if (--depth < 0) return LayoutResult.Fail("大括号不配对。", s, i);
                        if (expand) w.NewLine(depth);
                        w.Append('}');
                        if (expand) AfterBlock(ref i, depth);
                        break;
                    case ';':
                        w.Append(';');
                        if (expand)
                        {
                            int k = NextNonSpace(i + 1);
                            if (k < s.Length && s[k] != '}') { w.NewLine(depth); i = k - 1; }
                        }
                        break;
                    default:
                        w.Append(c);
                        break;
                }
            }
            if (depth != 0 || paren != 0) return LayoutResult.Fail("括号不配对：有没有闭合的括号。");
            return new LayoutResult(w.Result(), null, kept);
        }

        /// <summary>自定义属性（--name: 值）的结尾：值之后的 ; 或 } 的位置（括号、字符串、注释里的不算）。没有结束时为 -1。</summary>
        private int CustomPropertyEnd(int i)
        {
            int nesting = 0;
            for (int j = i; j < s.Length; j++)
            {
                char c = s[j];
                switch (c)
                {
                    case '"' or '\'':
                        j = SkipString(s, j);
                        if (j < 0) return -1;
                        break;
                    case '/' when j + 1 < s.Length && s[j + 1] == '*':
                        j = SkipBlockComment(s, j);
                        if (j < 0) return -1;
                        break;
                    case '(' or '[' or '{':
                        nesting++;
                        break;
                    case ')' or ']':
                        if (--nesting < 0) return -1;
                        break;
                    case '}':
                        if (nesting == 0) return TrimEndSpaces(i, j);
                        nesting--;
                        break;
                    case ';':
                        if (nesting == 0) return j;
                        break;
                }
            }
            return nesting == 0 ? TrimEndSpaces(i, s.Length) : -1;
        }

        /// <summary>值结尾的空白不算值（由后面的 } 处理换行）。</summary>
        private int TrimEndSpaces(int start, int end)
        {
            while (end > start && IsSpace(s[end - 1])) end--;
            return end;
        }

        /// <summary>} 之后：后面还有内容（不是另一个 }）时换行。</summary>
        private void AfterBlock(ref int i, int depth)
        {
            int k = NextNonSpace(i + 1);
            if (k < s.Length && s[k] != '}') { w.NewLine(depth); i = k - 1; }
        }

        private int NextNonSpace(int i)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
            return i;
        }
    }

    private static int CountLineBreaks(string s, int start, int end)
    {
        int count = 0;
        for (int i = start; i <= end; i++)
            if (s[i] == '\n' || s[i] == '\r' && (i + 1 > end || s[i + 1] != '\n')) count++;
        return count;
    }

    // ───────────────────────── JavaScript ─────────────────────────

    private sealed class JsLayout(string s, Writer w)
    {
        private enum Prev { None, Operand, Keyword, Punct, CloseBrace }

        /// <summary>这些关键字之后的 / 是正则表达式的开头。</summary>
        private static readonly HashSet<string> RegexKeywords =
            ["return", "typeof", "case", "do", "else", "in", "instanceof", "new", "delete", "void", "throw", "yield", "await", "of"];

        /// <summary>} 之后紧跟这些词时不换行（} else、} catch、} while 等）。</summary>
        private static readonly HashSet<string> SameLineAfterBrace = ["else", "catch", "finally", "while"];

        /// <summary>这些关键字后面的 (…) 是条件，) 之后可以是正则表达式（if (x) /re/.test(y)）。</summary>
        private static readonly HashSet<string> ConditionKeywords = ["if", "while", "for", "with"];

        /// <summary>未闭合的括号：{ ( [；条件的 ( 记为 c，箭头函数体的 { 记为 a。</summary>
        private readonly Stack<char> _brackets = new();
        private string? _lastWord;
        private bool _arrowPending; // 上一个记号是 =>

        /// <summary>刚才的 } 是箭头函数体的结尾（箭头函数后面不能接 ( [ 和运算符，换行时会自动插入分号）。</summary>
        private bool _closedArrowBody;
        private int _depth; // 大括号层数（缩进）
        private Prev _prev = Prev.None;
        private bool _afterLineComment;
        private int _kept;

        public LayoutResult Run(bool expand)
        {
            int i = 0;
            if (s.StartsWith("#!", StringComparison.Ordinal)) // 第一行的 #!/usr/bin/env node
            {
                int end = s.IndexOf('\n');
                if (end < 0) end = s.Length;
                w.Append(s, 0, end);
                _afterLineComment = true;
                i = end;
            }

            while (i < s.Length)
            {
                char c = s[i];
                if (IsSpace(c))
                {
                    i = Whitespace(i, expand);
                    continue;
                }
                _afterLineComment = false;
                char next = i + 1 < s.Length ? s[i + 1] : '\0';
                bool afterArrow = _arrowPending;
                _arrowPending = false;

                if (c == '/' && next == '/')
                {
                    int end = i;
                    while (end < s.Length && s[end] is not ('\n' or '\r')) end++;
                    w.Append(s, i, end - i);
                    _afterLineComment = true;
                    i = end;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int end = SkipBlockComment(s, i);
                    if (end < 0) return LayoutResult.Fail("有没有结束的注释。", s, i);
                    if (!expand) _kept += CountLineBreaks(s, i, end);
                    w.Append(s, i, end - i + 1);
                    i = end + 1;
                    continue;
                }
                if (c is '"' or '\'')
                {
                    int end = SkipString(s, i);
                    if (end < 0) return LayoutResult.Fail("有没有结束的字符串。", s, i);
                    Copy(ref i, end, Prev.Operand);
                    continue;
                }
                if (c == '`')
                {
                    int end = SkipTemplate(s, i);
                    if (end < 0) return LayoutResult.Fail("有没有结束的模板字符串。", s, i);
                    Copy(ref i, end, Prev.Operand);
                    continue;
                }
                if (c == '/' && RegexAllowed)
                {
                    int end = SkipRegex(s, i);
                    if (end < 0) return LayoutResult.Fail("有无法识别的正则表达式。", s, i);
                    Copy(ref i, end, Prev.Operand);
                    continue;
                }
                if (IsOperatorChar(c))
                {
                    var op = ReadOperator(i);
                    if (op == "<" && RegexAllowed && (IsWordChar(next) || next is '>' or '!'))
                        return LayoutResult.Fail("含有 JSX 或 HTML 注释，无法可靠地处理。", s, i);
                    w.Append(op);
                    _arrowPending = op == "=>";
                    // 后置的 i++ / i-- 仍是操作数（之后的 / 是除号）
                    if (!(op is "++" or "--" && _prev == Prev.Operand)) _prev = Prev.Punct;
                    i += op.Length;
                    continue;
                }
                if (IsWordChar(c))
                {
                    int end = i;
                    while (end + 1 < s.Length && IsWordChar(s[end + 1])) end++;
                    var word = s.Substring(i, end - i + 1);
                    Copy(ref i, end, RegexKeywords.Contains(word) ? Prev.Keyword : Prev.Operand);
                    _lastWord = word;
                    continue;
                }

                switch (c)
                {
                    case '{':
                    {
                        _brackets.Push(afterArrow ? 'a' : '{');
                        w.Append('{');
                        _prev = Prev.Punct;
                        int k = NextOnLine(i + 1);
                        if (k < s.Length && s[k] == '}')
                        {
                            _closedArrowBody = _brackets.Pop() == 'a';
                            w.Append('}');
                            _prev = Prev.CloseBrace;
                            i = AfterBrace(k, expand);
                            continue;
                        }
                        _depth++;
                        if (expand) { w.NewLine(_depth); i = k; continue; }
                        break;
                    }
                    case '}':
                        if (_brackets.Count == 0 || _brackets.Peek() is not ('{' or 'a')) return LayoutResult.Fail("大括号不配对。", s, i);
                        _closedArrowBody = _brackets.Pop() == 'a';
                        _depth--;
                        if (expand) w.NewLine(_depth);
                        w.Append('}');
                        _prev = Prev.CloseBrace;
                        i = AfterBrace(i, expand);
                        continue;
                    case '(' or '[':
                        _brackets.Push(c == '(' && _prev == Prev.Operand && _lastWord is { } w0 && ConditionKeywords.Contains(w0) ? 'c' : c);
                        w.Append(c);
                        _prev = Prev.Punct;
                        break;
                    case ')' or ']':
                    {
                        char open = _brackets.Count == 0 ? '\0' : _brackets.Pop();
                        if (c == ']' ? open != '[' : open is not ('(' or 'c')) return LayoutResult.Fail("括号不配对。", s, i);
                        w.Append(c);
                        _prev = open == 'c' ? Prev.Punct : Prev.Operand;
                        break;
                    }
                    case ';':
                    {
                        w.Append(';');
                        _prev = Prev.Punct;
                        if (expand && Top is not ('(' or 'c'))
                        {
                            int k = NextOnLine(i + 1);
                            if (k < s.Length && s[k] is not ('}' or '\n' or '\r')) { w.NewLine(_depth); i = k; continue; }
                        }
                        break;
                    }
                    case ',':
                    {
                        w.Append(',');
                        _prev = Prev.Punct;
                        if (expand && Top == '{')
                        {
                            int k = NextOnLine(i + 1);
                            if (k < s.Length && s[k] is not ('}' or '\n' or '\r')) { w.NewLine(_depth); i = k; continue; }
                        }
                        break;
                    }
                    default:
                        w.Append(c);
                        _prev = Prev.Punct;
                        break;
                }
                i++;
            }

            if (_brackets.Count > 0) return LayoutResult.Fail("括号不配对：有没有闭合的括号。");
            return new LayoutResult(w.Result(), null, _kept);
        }

        private char Top => _brackets.Count > 0 ? _brackets.Peek() : '\0';

        /// <summary>运算符（最长匹配），用来区分 a&lt;&lt;1 和 JSX 的 &lt;div&gt;、i++ 和 ++i。</summary>
        private static readonly string[] Operators =
        [
            ">>>=", "===", "!==", "**=", "<<=", ">>=", ">>>", "&&=", "||=", "??=",
            "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "++", "--", "+=", "-=", "*=", "%=", "&=", "|=", "^=", "**", "<<", ">>",
        ];

        private static bool IsOperatorChar(char c) => c is '+' or '-' or '*' or '%' or '&' or '|' or '^' or '!' or '~' or '<' or '>' or '=' or '?' or ':';

        private string ReadOperator(int i)
        {
            foreach (var op in Operators)
                if (string.CompareOrdinal(s, i, op, 0, op.Length) == 0) return op;
            return s[i].ToString();
        }

        /// <summary>
        /// 这里的 / 是不是正则表达式的开头。拿不准时（} 之后）按正则表达式处理：正则里的内容原样复制，不会被插入换行。
        /// </summary>
        private bool RegexAllowed => _prev is not Prev.Operand;

        private void Copy(ref int i, int end, Prev prev)
        {
            w.Append(s, i, end - i + 1);
            _prev = prev;
            i = end + 1;
        }

        /// <summary>同一行里下一个不是空格的位置（遇到换行就停在换行上）。</summary>
        private int NextOnLine(int i)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            return i;
        }

        /// <summary>
        /// } 之后：展开时，后面同一行紧跟一条新语句（词、字符串、! ~ { # ++ --）就换行；else / catch / finally / while 以及
        /// in / instanceof / of / from / as 前加一个空格。合法的代码里 } 后面紧跟这些时，} 一定是语句块的结尾，换行不会触发自动插入分号。
        /// 返回下一个要处理的位置。
        /// </summary>
        private int AfterBrace(int brace, bool expand)
        {
            int k = NextOnLine(brace + 1);
            if (!expand || k >= s.Length) return brace + 1;
            if (IsWordChar(s[k]))
            {
                if (ContinuesAfterBrace(Word(k)))
                {
                    w.Append(' ');
                    return k;
                }
            }
            else if (!StartsStatementOnly(k))
            {
                return brace + 1;
            }
            w.NewLine(_depth);
            return k;
        }

        /// <summary>} 之后紧跟这些词时不是新语句（} else、for ({a} of b)、import {a} from …）。</summary>
        private static bool ContinuesAfterBrace(string word) =>
            word is "else" or "catch" or "finally" or "while" or "in" or "instanceof" or "of" or "from" or "as";

        /// <summary>这个位置只能是一条新语句的开头（不能接在表达式后面）：! ~ { 引号 # ++ --。词另外判断。</summary>
        private bool StartsStatementOnly(int k)
        {
            char c = s[k];
            if (c is '!' or '~' or '{' or '"' or '\'' or '#') return true;
            return c is '+' or '-' && k + 1 < s.Length && s[k + 1] == c;
        }

        private string Word(int k)
        {
            int end = k;
            while (end < s.Length && IsWordChar(s[end])) end++;
            return s[k..end];
        }

        /// <summary>空白：展开时原有的换行都保留（重新缩进）；压缩时只去掉确定不影响含义的换行。</summary>
        private int Whitespace(int i, bool expand)
        {
            int j = i;
            bool lineBreak = false;
            while (j < s.Length && IsSpace(s[j])) lineBreak |= s[j++] is '\n' or '\r';
            if (w.IsEmpty || j >= s.Length) return j;
            char next = s[j];

            if (expand)
            {
                if (lineBreak) w.NewLine(next == '}' ? Math.Max(0, _depth - 1) : _depth);
                else if (!w.AtLineStart) w.Append(' ');
                return j;
            }

            if (!lineBreak)
            {
                if (!(IsTight(w.Last) || IsTight(next))) w.Append(' ');
                return j;
            }
            switch (_afterLineComment ? Join.Keep : DecideJoin(w.Last, j))
            {
                case Join.Direct:
                    if (IsWordChar(w.Last) && IsWordChar(next)) w.Append(' ');
                    return j;
                case Join.Semicolon:
                    w.Append(';');
                    return j;
                default:
                    w.RawNewLine();
                    _kept++;
                    return j;
            }
        }

        private enum Join { Keep, Direct, Semicolon }

        /// <summary>
        /// 压缩时这里的换行怎么处理（只在确定不改变含义时去掉）：
        /// <list type="bullet">
        /// <item>{ ; , ( [ : = ? 之后，或 } ) ] 和 .（不是小数点）之前：直接去掉，自动插入分号不会发生在这里。</item>
        /// <item>} 之后接 else / catch / finally / while / in / instanceof / of、( [ ` . 和二元运算符：直接去掉（换行时也是接着写）。</item>
        /// <item>} 之后接一条新语句（词、引号、! ~ { # ++ --）：换成分号，与自动插入分号的效果相同（} 是语句块结尾时多一个空语句，没有影响）。</item>
        /// <item>箭头函数体的 } 之后接 , ; : 以外的任何内容：换成分号（箭头函数后面不能接 ( [ 或运算符，换行处会自动插入分号）。</item>
        /// <item>} 之后接 from / as，以及其他所有情况：保留换行（可能依赖自动插入分号、return 后换行等）。</item>
        /// </list>
        /// </summary>
        private Join DecideJoin(char last, int j)
        {
            char next = s[j];
            if (last is '{' or ';' or ',' or '(' or '[' or ':' or '=' or '?') return Join.Direct;
            if (next is '}' or ')' or ']') return Join.Direct;
            if (next == '.' && !(j + 1 < s.Length && char.IsAsciiDigit(s[j + 1]))) return Join.Direct;
            if (last != '}') return Join.Keep;
            // 箭头函数体之后只能接 , ) ] } ; :（上面已处理 ) ] }），其他的都是新语句
            if (_closedArrowBody) return next is ',' or ';' or ':' ? Join.Direct : Join.Semicolon;
            if (IsWordChar(next))
            {
                var word = Word(j);
                if (word is "from" or "as") return Join.Keep;
                return ContinuesAfterBrace(word) ? Join.Direct : Join.Semicolon;
            }
            return StartsStatementOnly(j) ? Join.Semicolon : Join.Direct;
        }

        /// <summary>这些符号前后的空格可以去掉。</summary>
        private static bool IsTight(char c) => c is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',';

        /// <summary>正则表达式 /…/flags，返回最后一个字符的位置；没有结束（遇到换行）时为 -1。</summary>
        private static int SkipRegex(string s, int i)
        {
            bool inClass = false;
            int j = i + 1;
            for (; j < s.Length; j++)
            {
                char c = s[j];
                if (c is '\n' or '\r') return -1;
                if (c == '\\') { j++; continue; }
                if (inClass) { if (c == ']') inClass = false; }
                else if (c == '[') inClass = true;
                else if (c == '/') break;
            }
            if (j >= s.Length) return -1;
            while (j + 1 < s.Length && char.IsAsciiLetter(s[j + 1])) j++;
            return j;
        }

        /// <summary>模板字符串 `…${…}…`，返回结尾 ` 的位置，没有结束时为 -1。</summary>
        private static int SkipTemplate(string s, int i)
        {
            for (int j = i + 1; j < s.Length; j++)
            {
                char c = s[j];
                if (c == '\\') { j++; continue; }
                if (c == '`') return j;
                if (c == '$' && j + 1 < s.Length && s[j + 1] == '{')
                {
                    j = SkipInterpolation(s, j + 2);
                    if (j < 0) return -1;
                }
            }
            return -1;
        }

        /// <summary>${ … } 里的代码，返回结尾 } 的位置，没有结束时为 -1。</summary>
        private static int SkipInterpolation(string s, int j)
        {
            int depth = 0;
            char previous = '{'; // 上一个不是空白的字符，用来判断 / 是除号还是正则表达式
            for (; j < s.Length; j++)
            {
                char c = s[j];
                if (IsSpace(c)) continue;
                char before = previous;
                previous = c;
                switch (c)
                {
                    case '/' when j + 1 < s.Length && s[j + 1] != '*' && s[j + 1] != '/' &&
                                  before is '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or ';'
                                      or '+' or '-' or '*' or '%' or '<' or '>' or '~' or '^':
                        j = SkipRegex(s, j);
                        break;
                    case '"' or '\'':
                        j = SkipString(s, j);
                        break;
                    case '`':
                        j = SkipTemplate(s, j);
                        break;
                    case '/' when j + 1 < s.Length && s[j + 1] == '*':
                        j = SkipBlockComment(s, j);
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        if (depth == 0) return j;
                        depth--;
                        break;
                }
                if (j < 0) return -1;
            }
            return -1;
        }
    }
}
