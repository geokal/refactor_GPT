using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq; // Cast<>, ToList()

namespace RazorSectionLinter
{
    internal sealed class Program
    {
        public static int Main(string[] args)
        {
            var opts = Cli.Parse(args);

            var voidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "area","base","br","col","embed","hr","img","input","link","meta","param","source","track","wbr"
            };

            int exitCode = 0;

            foreach (var f in opts.Files)
            {
                var path = Path.GetFullPath(f);
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"ERROR: Missing file {f}");
                    exitCode = 1;
                    continue;
                }

                var original = File.ReadAllText(path, Encoding.UTF8);
                var text = original;

                Console.WriteLine($"[CHECK] {f}");

                if (!StartsWithIfBlock(text))
                {
                    Console.Error.WriteLine("  ERROR: Section does not start with '@if (...) {'");
                    exitCode = 1;
                }

                // Brace audit
                var braceAudit = BraceAudit(text);
                foreach (var e in braceAudit.Errors)
                    Console.Error.WriteLine("  " + e);

                if (opts.Diagnose)
                {
                    Console.WriteLine("  Brace depth changes:");
                    foreach (var s in braceAudit.Steps)
                        Console.WriteLine($"    line {s.Line,5}: depth {s.DepthBefore} -> {s.DepthAfter}");
                }

                // HTML <div> audit
                var htmlAudit = HtmlAudit(text, voidTags);
                foreach (var e in htmlAudit.Errors)
                    Console.Error.WriteLine("  " + e);

                if (opts.Diagnose)
                {
                    Console.WriteLine("  HTML tag depth changes (open=true/close=false):");
                    foreach (var s in htmlAudit.Steps)
                        Console.WriteLine($"    line {s.Line,5}: {(s.Open ? "open " : "close")} <{s.Tag}>  depth {s.DepthBefore} -> {s.DepthAfter}");
                }

                // Auto-fix braces (missing closers)
                if (opts.AutoFixBraces && braceAudit.FinalDepth > 0)
                {
                    Console.WriteLine($"  [autofix-braces] append {braceAudit.FinalDepth} closing brace(s) at EOF");
                    text = text.TrimEnd() + "\n" + new string('}', braceAudit.FinalDepth) + "\n";
                    braceAudit = BraceAudit(text);
                    if (braceAudit.Errors.Count == 0)
                        Console.WriteLine("  [autofix-braces] OK");
                    else
                        Console.Error.WriteLine("  [autofix-braces] still imbalanced");
                }

                // Auto-fix divs
                if (opts.AutoFixDivs && htmlAudit.Errors.Count > 0)
                {
                    bool changed;
                    text = AutoFixDivs(text, htmlAudit.Steps, voidTags, opts.Diagnose, out changed);
                    if (changed)
                    {
                        Console.WriteLine("  [autofix-divs] applied");
                        htmlAudit = HtmlAudit(text, voidTags);
                        foreach (var e in htmlAudit.Errors) Console.Error.WriteLine("  " + e);
                        if (htmlAudit.Errors.Count == 0)
                            Console.WriteLine("  [autofix-divs] OK");
                    }
                }

                // Indent if requested
                if (opts.Indent)
                {
                    text = IndentPretty(text);
                    Console.WriteLine("  [indent] applied");
                }

                // Write back if changed
                if (!string.Equals(text, original, StringComparison.Ordinal))
                {
                    var bak = path + ".lintbak";
                    if (!File.Exists(bak))
                        File.WriteAllText(bak, original, Encoding.UTF8);
                    File.WriteAllText(path, text, Encoding.UTF8);
                    Console.WriteLine($"  Saved. Backup: {Path.GetFileName(bak)}");
                }

                bool okNow = StartsWithIfBlock(text) && braceAudit.Errors.Count == 0 && htmlAudit.Errors.Count == 0;
                if (okNow)
                    Console.WriteLine("  OK");

                if (!okNow) exitCode = 1;
            }

            return exitCode;
        }

        private sealed record Options(
            bool Diagnose,
            bool AutoFixBraces,
            bool AutoFixDivs,
            bool Indent,
            List<string> Files
        );

        private static class Cli
        {
            public static Options Parse(string[] args)
            {
                bool diagnose = false, autofixBraces = false, autofixDivs = false, indent = false;
                var files = new List<string>();

                foreach (var a in args)
                {
                    switch (a)
                    {
                        case "--diagnose": diagnose = true; break;
                        case "--autofix-braces": autofixBraces = true; break;
                        case "--autofix-divs": autofixDivs = true; break;
                        case "--indent": indent = true; break;
                        default:
                            files.Add(a);
                            break;
                    }
                }

                if (files.Count == 0)
                {
                    Console.Error.WriteLine("Usage: dotnet run -- <razor file> [flags]");
                    Console.Error.WriteLine("  --diagnose        Show detailed depth changes");
                    Console.Error.WriteLine("  --autofix-braces  Append missing '}' at EOF");
                    Console.Error.WriteLine("  --autofix-divs    Fix trailing </div> or missing </div> at EOF");
                    Console.Error.WriteLine("  --indent          Re-indent HTML and brace scopes");
                    Environment.Exit(2);
                }

                return new Options(diagnose, autofixBraces, autofixDivs, indent, files);
            }
        }

        // Utility for start-check
        private static bool StartsWithIfBlock(string text)
        {
            var s = text;

            // strip BOM
            if (!string.IsNullOrEmpty(s) && s[0] == '\uFEFF')
                s = s.Substring(1);

            // skip leading whitespace
            int i = 0, n = s.Length;
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            s = s.Substring(i);

            // skip leading HTML or Razor comments
            while (true)
            {
                if (s.StartsWith("<!--", StringComparison.Ordinal))
                {
                    int end = s.IndexOf("-->", StringComparison.Ordinal);
                    if (end == -1) break;
                    s = s.Substring(end + 3);
                    while (s.Length > 0 && char.IsWhiteSpace(s[0])) s = s.Substring(1);
                    continue;
                }
                if (s.StartsWith("@*", StringComparison.Ordinal))
                {
                    int end = s.IndexOf("*@", StringComparison.Ordinal);
                    if (end == -1) break;
                    s = s.Substring(end + 2);
                    while (s.Length > 0 && char.IsWhiteSpace(s[0])) s = s.Substring(1);
                    continue;
                }
                break;
            }

            return Regex.IsMatch(s, @"^@if\s*\([^\)]*\)\s*\{");
        }

        private sealed record BraceStep(int Line, int DepthBefore, int DepthAfter);
        private sealed record BraceAuditResult(int FinalDepth, List<BraceStep> Steps, List<string> Errors);

        private static BraceAuditResult BraceAudit(string text)
        {
            int depth = 0;
            var steps = new List<BraceStep>();
            var errs  = new List<string>();

            var scrub = Regex.Replace(text, @"@\*.*?\*@", "", RegexOptions.Singleline);
            scrub = Regex.Replace(scrub, @"/\*.*?\*/", "", RegexOptions.Singleline);
            scrub = Regex.Replace(scrub, @"//.*", "", RegexOptions.Multiline);

            var lines = text.Replace("\r\n","\n").Split("\n");
            for (int i = 0; i < lines.Length; i++)
            {
                var ln = lines[i];
                var ls = ln.TrimStart();
                bool looksCode = ls.StartsWith("@") || ls.StartsWith("{") || ls.StartsWith("}") ||
                                 ls.Contains("@if") || ls.Contains("@foreach") || ls.Contains("@for ") ||
                                 ls.Contains("@switch") || ls.StartsWith("@code");
                if (!looksCode) continue;

                int before = depth;
                foreach (var ch in ln)
                {
                    if (ch == '{') depth++;
                    else if (ch == '}') depth--;
                }
                int after = depth;
                steps.Add(new BraceStep(i+1, before, after));
                if (after < 0)
                    errs.Add($"Line {i+1}: extra closing '}}' detected. depth {before} -> {after}");
            }

            if (depth != 0)
                errs.Add($"Brace imbalance: final depth={depth} (0 expected)");

            return new BraceAuditResult(depth, steps, errs);
        }

        private sealed record HtmlStep(int Line, string Tag, bool Open, int DepthBefore, int DepthAfter);
        private sealed record HtmlAuditResult(List<HtmlStep> Steps, List<string> Errors);

        private static HtmlAuditResult HtmlAudit(string original, HashSet<string> voidTags)
        {
            var errs = new List<string>();
            var steps = new List<HtmlStep>();
            var html = Regex.Replace(StripForHtmlScan(original), @"\r\n", "\n");

            var lineMap = new int[html.Length];
            int lnCount = 1;
            for (int i = 0; i < html.Length; i++)
            {
                lineMap[i] = lnCount;
                if (html[i] == '\n') lnCount++;
            }
            Func<int,int> GetLine = idx => idx < html.Length ? lineMap[idx] : lnCount;

            var tagRx = new Regex(@"<\s*\/?\s*([a-zA-Z0-9\-:_]+)([^>]*)>", RegexOptions.Compiled);
            var stack = new Stack<(string tag, int line)>();
            int depth = 0;

            foreach (Match m in tagRx.Matches(html))
            {
                string tag = m.Groups[1].Value;
                string raw = m.Value;
                int line = GetLine(m.Index);
                bool isClose = raw.StartsWith("</");
                bool selfClosing = raw.EndsWith("/>") || voidTags.Contains(tag);

                if (isClose)
                {
                    int before = depth;
                    if (voidTags.Contains(tag))
                    {
                        steps.Add(new HtmlStep(line, tag, false, before, before));
                        continue;
                    }
                    if (depth == 0 || stack.Count == 0)
                    {
                        errs.Add($"Line {line}: unexpected </{tag}> with empty stack.");
                        steps.Add(new HtmlStep(line, tag, false, before, before-1));
                        continue;
                    }
                    var top = stack.Pop();
                    depth--;
                    if (!top.tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                        errs.Add($"Line {line}: closing </{tag}> does not match <{top.tag}> opened at line {top.line}.");
                    steps.Add(new HtmlStep(line, tag, false, before, depth));
                }
                else
                {
                    if (!selfClosing)
                    {
                        stack.Push((tag, line));
                        steps.Add(new HtmlStep(line, tag, true, depth, depth+1));
                        depth++;
                    }
                    else
                    {
                        steps.Add(new HtmlStep(line, tag, true, depth, depth));
                    }
                }
            }

            while (stack.Count > 0)
            {
                var top = stack.Pop();
                if (!voidTags.Contains(top.tag))
                    errs.Add($"Unclosed <{top.tag}> opened at line {top.line}.");
            }

            return new HtmlAuditResult(steps, errs);
        }

        private static string StripForHtmlScan(string s)
        {
            s = Regex.Replace(s, @"@\*.*?\*@", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"@\{.*?\}", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"@code\s*\{.*?\}", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"@\((?:[^()]*|\((?>[^()]+|\([^()]*\))*\))*\)", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"@[A-Za-z_][A-Za-z0-9_\.]*", "", RegexOptions.Singleline);
            return s;
        }

        private static string AutoFixDivs(string original, List<HtmlStep> steps, HashSet<string> voidTags, bool diagnose, out bool changed)
        {
            changed = false;
            var lines = NormalizeNewlines(original).Split('\n').ToList();

            var html = StripForHtmlScan(original);
            var lineMap = new List<int>(html.Length);
            int lineNo = 1;
            for (int i = 0; i < html.Length; i++)
            {
                lineMap.Add(lineNo);
                if (html[i] == '\n') lineNo++;
            }
            int GetLine(int idx) => idx < lineMap.Count ? lineMap[idx] : lines.Count;

            var toksRx = new Regex(@"<\s*(/)?\s*([a-zA-Z0-9\-:_]+)([^>]*)>", RegexOptions.Compiled);
            var tokens = toksRx.Matches(html).Cast<Match>().ToList();

            int depth = 0;
            var closersToDelete = new HashSet<int>();

            for (int k = 0; k < tokens.Count; k++)
            {
                var m = tokens[k];
                var isClose = m.Groups[1].Success;
                var tag = m.Groups[2].Value;
                var raw = m.Value;

                if (raw.EndsWith("/>") || voidTags.Contains(tag))
                    continue;

                if (!isClose)
                {
                    depth++;
                }
                else
                {
                    if (depth == 0)
                    {
                        int ln = GetLine(m.Index);
                        if (diagnose) Console.WriteLine($"  [autofix-divs] remove extra </{tag}> at line {ln}");
                        closersToDelete.Add(ln); changed = true;
                    }
                    else
                    {
                        depth--;
                    }
                }
            }

            if (depth > 0)
            {
                if (diagnose) Console.WriteLine($"  [autofix-divs] append {depth} closing </div> at EOF");
                for (int i = 0; i < depth; i++)
                    lines.Add("</div>");
                changed = true;
            }

            if (changed)
                return string.Join("\n", lines);
            return original;
        }

        private static string IndentPretty(string text)
        {
            var lines = NormalizeNewlines(text).Split('\n');
            var sb = new StringBuilder(text.Length + 1024);
            int indent = 0;

            var openTag = new Regex(@"<\s*(div|section|main|header|footer|article|nav|ul|ol|li|table|tbody|thead|tr|td|th)(\s|>)", RegexOptions.IgnoreCase);
            var closeTag= new Regex(@"</\s*(div|section|main|header|footer|article|nav|ul|ol|li|table|tbody|thead|tr|td|th)\s*>", RegexOptions.IgnoreCase);
            var selfTag = new Regex(@"<[^>]+/>\s*$", RegexOptions.IgnoreCase);
            var openBrace = new Regex(@"\{", RegexOptions.Compiled);
            var closeBrace= new Regex(@"\}", RegexOptions.Compiled);

            foreach (var raw in lines)
            {
                var trimmed = raw.TrimStart();

                if (closeBrace.IsMatch(trimmed))
                    indent = Math.Max(0, indent - 1);
                if (closeTag  .IsMatch(trimmed))
                    indent = Math.Max(0, indent - 1);

                sb.Append(new string(' ', indent * 2));
                sb.AppendLine(trimmed);

                if (openBrace.IsMatch(trimmed))
                    indent++;
                if (openTag .IsMatch(trimmed) && !selfTag.IsMatch(trimmed))
                    indent++;
            }

            return sb.ToString();
        }

        private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n");
    }
}
