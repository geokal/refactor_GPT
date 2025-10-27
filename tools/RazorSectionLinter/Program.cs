using System.Text;
using System.Text.RegularExpressions;

namespace RazorSectionLinter;

internal sealed class Program
{
    // -------- Entry --------
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

            // Start must be @if (...) {
            if (!StartsWithIfBlock(text))
            {
                Console.Error.WriteLine("  ERROR: Section does not start with '@if (...) {'");
                exitCode = 1;
            }

            // Braces
            var braceAudit = BraceAudit(text);
            foreach (var e in braceAudit.Errors) Console.Error.WriteLine("  " + e);

            if (opts.Diagnose)
            {
                Console.WriteLine("  Brace depth changes:");
                foreach (var s in braceAudit.Steps)
                    Console.WriteLine($"    line {s.Line,5}: depth {s.DepthBefore} -> {s.DepthAfter}");
            }

            // HTML divs
            var htmlAudit = HtmlAudit(text, voidTags);
            foreach (var e in htmlAudit.Errors) Console.Error.WriteLine("  " + e);

            if (opts.Diagnose)
            {
                Console.WriteLine("  HTML tag depth changes (open=true/close=false):");
                foreach (var s in htmlAudit.Steps)
                    Console.WriteLine($"    line {s.Line,5}: {(s.Open ? "open " : "close")} <{s.Tag}>  depth {s.DepthBefore} -> {s.DepthAfter}");
            }

            // Auto-fix braces (only if missing at tail)
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

            // Auto-fix braces for excess closers at EOF
            if (opts.AutoFixBraces && braceAudit.FinalDepth < 0)
            {
                int extra = -braceAudit.FinalDepth;
                Console.WriteLine($"  [autofix-braces] remove {extra} trailing closing brace(s) at EOF");
                var lines = NormalizeNewlines(text).Split('\n').ToList();

                int removed = 0;
                for (int i = lines.Count - 1; i >= 0 && removed < extra; i--)
                {
                    var t = lines[i].Trim();
                    if (t == "}" || t == "};" || t == "}" + "\r")
                    {
                        lines.RemoveAt(i);
                        removed++;
                    }
                    else if (t.EndsWith("}"))
                    {
                        // conservative: remove one trailing } if present
                        int pos = lines[i].LastIndexOf('}');
                        if (pos >= 0)
                        {
                            lines[i] = lines[i].Remove(pos, 1);
                            removed++;
                        }
                    }
                }

                text = string.Join("\n", lines);
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
                    if (htmlAudit.Errors.Count == 0) Console.WriteLine("  [autofix-divs] OK");
                }
            }

            // Indent
            if (opts.Indent)
            {
                text = IndentPretty(text);
                Console.WriteLine("  [indent] applied");
            }

            // Report status
            bool okNow = StartsWithIfBlock(text) && braceAudit.Errors.Count == 0 && htmlAudit.Errors.Count == 0;
            if (okNow) Console.WriteLine("  OK");

            // Write back only if changed
            if (!string.Equals(text, original, StringComparison.Ordinal))
            {
                var bak = path + ".lintbak";
                if (!File.Exists(bak)) File.WriteAllText(bak, original, Encoding.UTF8);
                File.WriteAllText(path, text, Encoding.UTF8);
                Console.WriteLine($"  Saved. Backup: {Path.GetFileName(bak)}");
            }

            if (!okNow) exitCode = 1;
        }

        return exitCode;
    }

    // -------- CLI model --------
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
                    case "-h":
                    case "--help":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    default:
                        files.Add(a);
                        break;
                }
            }

            if (files.Count == 0)
            {
                Console.Error.WriteLine("No files. Run with --help for usage.");
                Environment.Exit(2);
            }

            return new Options(diagnose, autofixBraces, autofixDivs, indent, files);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("""
RazorSectionLinter

Usage:
  dotnet run -- <file1.razor> <file2.razor> ... [flags]

Flags:
  --diagnose          Print depth charts for </div> and braces
  --autofix-braces    Append missing '}' at end when final brace depth > 0
  --autofix-divs      Fix simple HTML closing issues:
                        - trim trailing extra </div> that push depth < 0
                        - append missing </div> at EOF to close open tags
  --indent            Reindent HTML blocks and brace scopes
  --help              Show this help

Exit codes:
  0  OK
  1  Errors found
  2  Bad usage
""");
        }
    }

    // -------- Audits --------
    private sealed record BraceStep(int Line, int DepthBefore, int DepthAfter);
    private sealed record BraceAuditResult(int FinalDepth, List<BraceStep> Steps, List<string> Errors);

    private static BraceAuditResult BraceAudit(string text)
    {
        int depth = 0;
        var steps = new List<BraceStep>();
        var errs  = new List<string>();

        var scrub = StripCommentsForBraceScan(text);
        var lines = NormalizeNewlines(scrub).Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var ln = lines[i];
            var ls = ln.TrimStart();
            bool looksCode =
                ls.StartsWith("@") || ls.StartsWith("{") || ls.StartsWith("}") ||
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
            steps.Add(new BraceStep(i + 1, before, after));

            if (after < 0)
                errs.Add($"Line {i + 1}: extra closing '}}' detected. depth {before} -> {after}");
        }

        if (depth != 0) errs.Add($"Brace imbalance: final depth={depth} (0 expected)");
        return new BraceAuditResult(depth, steps, errs);
    }

    private sealed record HtmlStep(int Line, string Tag, bool Open, int DepthBefore, int DepthAfter);
    private sealed record HtmlAuditResult(List<HtmlStep> Steps, List<string> Errors);

    private static HtmlAuditResult HtmlAudit(string original, HashSet<string> voidTags)
    {
        var errs = new List<string>();
        var steps = new List<HtmlStep>();
        var html = NormalizeNewlines(StripForHtmlScan(original));

        // map index to line
        var lineMap = new List<int>(html.Length);
        int line = 1;
        for (int i = 0; i < html.Length; i++)
        {
            lineMap.Add(line);
            if (html[i] == '\n') line++;
        }
        int GetLine(int idx) => idx >= 0 && idx < lineMap.Count ? lineMap[idx] : 0;

        // Only DIVs
        var openDivRx  = new Regex(@"<\s*div(\s|>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var closeDivRx = new Regex(@"</\s*div\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        int depth = 0;
        // simple scan through string
        for (int i = 0; i < html.Length; )
        {
            var openM  = openDivRx.Match(html, i);
            var closeM = closeDivRx.Match(html, i);

            Match? m = null;
            if (openM.Success && closeM.Success) m = openM.Index < closeM.Index ? openM : closeM;
            else if (openM.Success) m = openM;
            else if (closeM.Success) m = closeM;

            if (m is null) break;

            int ln = GetLine(m.Index);
            if (m == openM)
            {
                steps.Add(new HtmlStep(ln, "div", true, depth, depth + 1));
                depth++;
            }
            else
            {
                int before = depth;
                depth--;
                steps.Add(new HtmlStep(ln, "div", false, before, depth));
                if (depth < 0)
                {
                    errs.Add($"Line {ln}: unexpected </div> with empty stack.");
                    depth = 0; // keep scanning
                }
            }
            i = m.Index + m.Length;
        }

        if (depth > 0)
            errs.Add($"Unclosed <div>: {depth} still open at EOF.");

        return new HtmlAuditResult(steps, errs);
    }


    // -------- Fixers / Formatting --------
    private static string AutoFixDivs(
        string original,
        List<HtmlStep> steps,
        HashSet<string> voidTags,
        bool diagnose,
        out bool changed)
    {
        changed = false;
        var lines = NormalizeNewlines(original).Split('\n').ToList();

        // Build an HTML-only stream and map to line numbers
        var html = StripForHtmlScan(original);
        var lineMap = new List<int>(html.Length);
        int lineNo = 1;
        for (int i = 0; i < html.Length; i++)
        {
            lineMap.Add(lineNo);
            if (html[i] == '\n') lineNo++;
        }
        int GetLine(int idx) => idx >= 0 && idx < lineMap.Count ? lineMap[idx] : lines.Count;

        // Tokenize tags to find extra closers that drop depth < 0
        var toksRx = new Regex(@"<\s*(/)?\s*([a-zA-Z0-9\-:_]+)([^>]*)>", RegexOptions.Compiled);
        var tokens = toksRx.Matches(html).Cast<Match>().ToList();

        var closersToDelete = new HashSet<int>(); // line numbers to delete
        int depth = 0;
        var openStack = new Stack<string>();

        foreach (var m in tokens)
        {
            var isClose = m.Groups[1].Success;
            var tag = m.Groups[2].Value;
            var raw = m.Value;

            if (raw.EndsWith("/>") || voidTags.Contains(tag)) continue;

            if (!isClose)
            {
                openStack.Push(tag);
                depth++;
            }
            else
            {
                if (depth == 0)
                {
                    int ln = GetLine(m.Index);
                    closersToDelete.Add(ln);
                    if (diagnose) Console.WriteLine($"  [autofix-divs] remove extra </{tag}> at line {ln}");
                    changed = true;
                    continue;
                }
                openStack.Pop();
                depth--;
            }
        }

        // Delete lines that are just the offending </div>
        if (closersToDelete.Count > 0)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (!closersToDelete.Contains(i + 1)) continue;
                var t = lines[i].Trim();
                if (Regex.IsMatch(t, @"^</\s*div\s*>\s*$"))
                    lines.RemoveAt(i);
            }
        }

        // Append missing closers for remaining open tags
        if (openStack.Count > 0)
        {
            int k = openStack.Count;
            if (diagnose) Console.WriteLine($"  [autofix-divs] append {k} closing </div> at EOF");
            for (int i = 0; i < k; i++)
                lines.Add("</div>");
            changed = true;
        }

        return string.Join("\n", lines);
    }

    private static string IndentPretty(string text)
    {
        var lines = NormalizeNewlines(text).Split('\n');
        var sb = new StringBuilder(text.Length + 1024);
        int indent = 0;

        // basic heuristics
        Regex openTag = new(@"<\s*(div|section|main|header|footer|article|nav|ul|ol|li|table|tbody|thead|tr|td|th)(\s|>)", RegexOptions.IgnoreCase);
        Regex closeTag = new(@"</\s*(div|section|main|header|footer|article|nav|ul|ol|li|table|tbody|thead|tr|td|th)\s*>", RegexOptions.IgnoreCase);
        Regex selfTag = new(@"<[^>]+/>\s*$", RegexOptions.IgnoreCase);
        Regex openBrace = new(@"\{", RegexOptions.Compiled);
        Regex closeBrace = new(@"\}", RegexOptions.Compiled);

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart();

            if (closeBrace.IsMatch(trimmed))
                indent = Math.Max(0, indent - 1);
            if (closeTag.IsMatch(trimmed))
                indent = Math.Max(0, indent - 1);

            sb.Append(new string(' ', indent * 2));
            sb.AppendLine(trimmed);

            if (openBrace.IsMatch(trimmed))
                indent++;
            if (openTag.IsMatch(trimmed) && !selfTag.IsMatch(trimmed))
                indent++;
        }

        return sb.ToString();
    }

    // -------- Utilities --------
    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n");

    private static string StripForHtmlScan(string s)
    {
        s = Regex.Replace(s, @"@\*.*?\*@", "", RegexOptions.Singleline); // Razor comments
        s = Regex.Replace(s, @"@\{.*?\}", "", RegexOptions.Singleline);  // code blocks
        s = Regex.Replace(s, @"@code\s*\{.*?\}", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"@\((?:[^()]*|\((?>[^()]+|\([^()]*\))*\))*\)", "", RegexOptions.Singleline); // @(...) expr
        s = Regex.Replace(s, @"@[A-Za-z_][A-Za-z0-9_\.]*", "", RegexOptions.Singleline); // @Identifier
        return s;
    }

    private static string StripCommentsForBraceScan(string s)
    {
        s = Regex.Replace(s, @"@\*.*?\*@", "", RegexOptions.Singleline); // Razor
        s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline); // C#
        s = Regex.Replace(s, @"//.*?$", "", RegexOptions.Multiline);     // C#
        return s;
    }

    private static bool StartsWithIfBlock(string text)
    {
        var s = NormalizeNewlines(text);

        // strip BOM
        if (s.Length > 0 && s[0] == '\uFEFF') s = s[1..];

        // skip leading whitespace
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;

        // skip REGION markers and HTML comments at head
        while (i < s.Length)
        {
            // REGION: <!-- REGION: ... -->
            if (s.AsSpan(i).StartsWith("<!-- REGION:".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                int end = s.IndexOf("-->", i, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 3;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                continue;
            }

            // generic HTML comment <!-- ... -->
            if (s.AsSpan(i).StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
            {
                int end = s.IndexOf("-->", i, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 3;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                continue;
            }
            break;
        }

        // check for @if (...) {
        var head = s[i..];
        return Regex.IsMatch(head, @"^@if\s*\([^\)]*\)\s*\{");
    }

}
