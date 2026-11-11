using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Protocol;
using UEToVS.Indexer.Index;

namespace UEToVS.Indexer.Text
{
    /// <summary>Full-text search. Uses ripgrep when available (bundled next to the indexer or on PATH).</summary>
    internal sealed class TextSearcher
    {
        private readonly IndexStore _store;
        private readonly string _rg;

        public TextSearcher(IndexStore store)
        {
            _store = store;
            _rg = FindRipgrep();
            Log.Info(_rg != null ? "Text search: ripgrep at " + _rg : "Text search: ripgrep not found, using managed search");
        }

        public bool UsesRipgrep => _rg != null;

        internal static string FindRipgrep()
        {
            string exe = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
            string local = Path.Combine(AppContext.BaseDirectory, exe);
            if (File.Exists(local)) return local;
            local = Path.Combine(AppContext.BaseDirectory, "tools", exe);
            if (File.Exists(local)) return local;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    string p = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public async Task<List<TextMatch>> SearchAsync(TextSearchRequest req, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(req.Query)) return new List<TextMatch>();
            var roots = _store.Layout.SourceRoots.Concat(_store.Layout.ConfigRoots)
                .Where(r => req.IncludeNonProject || r.IsProject)
                .Select(r => r.Path)
                .ToList();
            if (roots.Count == 0) return new List<TextMatch>();
            return _rg != null ? await SearchRipgrepAsync(req, roots, ct).ConfigureAwait(false) : SearchManaged(req, roots, ct);
        }

        private async Task<List<TextMatch>> SearchRipgrepAsync(TextSearchRequest req, List<string> roots, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_rg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--max-count=200");
            psi.ArgumentList.Add("--max-filesize=4M");
            psi.ArgumentList.Add(req.CaseSensitive ? "--case-sensitive" : "--ignore-case");
            if (req.WholeWord) psi.ArgumentList.Add("--word-regexp");
            if (!req.Regex) psi.ArgumentList.Add("--fixed-strings");
            foreach (var g in new[] { "!**/Intermediate/**", "!**/Binaries/**", "!**/Saved/**", "!**/DerivedDataCache/**" })
            {
                psi.ArgumentList.Add("--glob");
                psi.ArgumentList.Add(g);
            }
            foreach (var mask in SplitMask(req.FileMask))
            {
                psi.ArgumentList.Add("--glob");
                psi.ArgumentList.Add(mask);
            }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(req.Query);
            foreach (var r in roots) psi.ArgumentList.Add(r);

            var results = new List<TextMatch>();
            using var proc = Process.Start(psi);
            _ = proc.StandardError.ReadToEndAsync();
            using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
            try
            {
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var m = ParseRipgrepLine(line);
                    if (m == null) continue;
                    results.AddRange(m);
                    if (results.Count >= req.MaxResults)
                    {
                        try { proc.Kill(true); } catch { }
                        break;
                    }
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            if (results.Count > req.MaxResults) results.RemoveRange(req.MaxResults, results.Count - req.MaxResults);
            // project hits first
            return results.OrderByDescending(r => IsProjectPath(r.FilePath)).ToList();
        }

        private bool IsProjectPath(string path) =>
            _store.Layout.ProjectRoot != null && path.StartsWith(_store.Layout.ProjectRoot, StringComparison.OrdinalIgnoreCase);

        internal static List<TextMatch> ParseRipgrepLine(string line)
        {
            if (!line.StartsWith("{\"type\":\"match\"", StringComparison.Ordinal)) return null;
            using var doc = JsonDocument.Parse(line);
            var data = doc.RootElement.GetProperty("data");
            string path = data.GetProperty("path").TryGetProperty("text", out var pt) ? pt.GetString() : null;
            if (path == null) return null;
            string text = data.GetProperty("lines").TryGetProperty("text", out var lt) ? lt.GetString() : string.Empty;
            int lineNo = data.GetProperty("line_number").GetInt32() - 1;
            var bytes = Encoding.UTF8.GetBytes(text);
            var list = new List<TextMatch>();
            foreach (var sm in data.GetProperty("submatches").EnumerateArray())
            {
                int startByte = sm.GetProperty("start").GetInt32();
                int endByte = sm.GetProperty("end").GetInt32();
                int col = Encoding.UTF8.GetCharCount(bytes, 0, Math.Min(startByte, bytes.Length));
                int len = Encoding.UTF8.GetCharCount(bytes, Math.Min(startByte, bytes.Length), Math.Max(0, Math.Min(endByte, bytes.Length) - startByte));
                list.Add(new TextMatch { FilePath = path, Line = lineNo, Column = col, Length = len, LineText = text.TrimEnd('\r', '\n') });
            }
            return list;
        }

        private List<TextMatch> SearchManaged(TextSearchRequest req, List<string> roots, CancellationToken ct)
        {
            var opts = req.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            string pat = req.Regex ? req.Query : Regex.Escape(req.Query);
            if (req.WholeWord) pat = @"\b" + pat + @"\b";
            var regex = new Regex(pat, opts | RegexOptions.Compiled);
            var masks = SplitMask(req.FileMask).Select(GlobToRegex).ToList();
            var results = new List<TextMatch>();
            object gate = new object();
            var files = _store.Current.Files.Where(f => roots.Any(r => f.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                .Where(f => masks.Count == 0 || masks.Any(m => m.IsMatch(f.Name)))
                .OrderByDescending(f => f.IsProject)
                .ToList();
            Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct }, (f, state) =>
            {
                string[] lines;
                try { lines = File.ReadAllLines(f.Path); }
                catch (Exception) { return; }
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in regex.Matches(lines[i]))
                    {
                        lock (gate)
                        {
                            if (results.Count >= req.MaxResults) { state.Stop(); return; }
                            results.Add(new TextMatch { FilePath = f.Path, Line = i, Column = m.Index, Length = m.Length, LineText = lines[i] });
                        }
                    }
                }
            });
            return results;
        }

        private static IEnumerable<string> SplitMask(string mask) =>
            string.IsNullOrWhiteSpace(mask) ? Enumerable.Empty<string>() : mask.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim());

        private static Regex GlobToRegex(string glob) =>
            new Regex("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
    }
}
