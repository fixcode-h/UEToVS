using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Refactoring;
using UEToVS.Mcp.Data;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// v0.4.5 self-check and edit tools for agents: get_build_errors (last build, written by the VS extension),
    /// get_diagnostics (UEToVS's UE rules on whole files: the same DiagnosticsProvider as the squiggles), and rename_symbol
    /// (index-based rename; dry run by default; the planning is the host's RenamePlanner, compiled in as a linked source).
    /// </summary>
    public sealed partial class ToolCatalog
    {
        private static readonly Logger LB = Log.For("Mcp.Build");

        private const string SelfCheckHint = "After editing C++ call get_diagnostics on the changed files; after a build call get_build_errors.";

        private void RegisterBuildTools()
        {
            Register(BuildErrorsTool());
            Register(DiagnosticsTool());
            Register(RenameSymbolTool());
        }

        // ================================================================== get_build_errors

        private ToolDefinition BuildErrorsTool() => Define(
            "get_build_errors", "Errors of the last build",
            "Compiler / linker / UHT / UBT errors and warnings of the last build done in Visual Studio (parsed from the Build output; also what UEToVS " +
            "squiggles in the editor), with 1-based file:line:col, code (C2065, LNK2019 …), message and tool. Use it after a build instead of reading the " +
            "build log. Filter by 'file' (path, file name or part of it) and 'severity'. 'build' tells when it ran and whether it succeeded; " +
            "an old 'finishedUtc' means the code has not been built since.",
            Schema(
                "\"file\":{\"type\":\"string\",\"description\":\"Only items whose file path contains this (case-insensitive), e.g. 'MyActor.cpp'.\"}," +
                "\"severity\":{\"type\":\"string\",\"enum\":[\"error\",\"warning\",\"all\"],\"description\":\"Default all (errors and warnings).\"}," +
                "\"include_notes\":{\"type\":\"boolean\",\"description\":\"Also compiler notes ('see declaration of …'; default false).\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"description\":\"Max items (default 50).\"}," +
                "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000,\"description\":\"Items to skip.\"}"),
            BuildErrorsAsync, readOnly: true);

        private Task<ToolOutput> BuildErrorsAsync(ToolArgs a, CancellationToken ct)
        {
            string file = a.String("file", maxLength: 1024)?.Trim();
            string severity = a.Choice("severity", "all", "error", "warning", "all");
            bool notes = a.Bool("include_notes", false);
            int limit = a.Int("limit", 50, 1, 500);
            int offset = a.Int("offset", 0, 0, 10000);

            var f = BuildErrorsReader.Read(_index.SolutionDir);
            if (f == null)
            {
                var none = new JsonObject
                {
                    ["source"] = "build",
                    ["build"] = null,
                    ["count"] = 0,
                    ["note"] = "No build recorded for this solution yet (" + BuildErrorsReader.PathFor(_index.SolutionDir) + " missing). " +
                               "Build in Visual Studio with UEToVS installed, then call again.",
                    ["items"] = new JsonArray(),
                };
                return Task.FromResult(new ToolOutput { Payload = none, ResultCount = 0, Source = "build" });
            }
            string needle = string.IsNullOrEmpty(file) ? null : file.Replace('/', '\\');
            var items = f.Items.Where(i =>
                    (severity == "all" ? i.Severity == "error" || i.Severity == "warning" || notes && i.Severity == "note"
                                       : i.Severity == severity || notes && i.Severity == "note")
                    && (needle == null || i.File != null && i.File.Replace('/', '\\').IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(i => i.Severity == "error" ? 0 : i.Severity == "warning" ? 1 : 2)
                .ToList();
            var (page, more) = Slice(items, offset, limit);
            var arr = new List<JsonNode>();
            foreach (var i in page)
            {
                var o = new JsonObject { ["severity"] = i.Severity };
                if (i.File != null) o["file"] = i.File;
                if (i.Line > 0) o["line"] = i.Line;
                if (i.Column > 0) o["col"] = i.Column;
                if (i.Code != null) o["code"] = i.Code;
                o["message"] = i.Message;
                if (i.Tool != null) o["tool"] = i.Tool;
                arr.Add(o);
            }
            string note = null;
            if (f.Truncated) note = "The build had more diagnostics than were recorded (first 5000 kept).";
            if (f.Result == "running") note = Join(note, "A build is running; this is its output so far.");
            var payload = ResultJson.Page("build", true, arr, offset, more, note);
            var build = new JsonObject
            {
                ["result"] = f.Result,
                ["source"] = f.Source,
                ["startedUtc"] = f.StartedUtc == DateTime.MinValue ? null : f.StartedUtc.ToString("u"),
                ["finishedUtc"] = f.FinishedUtc == DateTime.MinValue ? null : f.FinishedUtc.ToString("u"),
                ["durationMs"] = f.DurationMs,
                ["errors"] = f.Errors,
                ["warnings"] = f.Warnings,
            };
            if (f.FinishedUtc > DateTime.MinValue) build["ageMinutes"] = Math.Round((DateTime.UtcNow - f.FinishedUtc).TotalMinutes, 1);
            payload.Insert(0, "build", build);
            payload.Insert(1, "matching", items.Count);
            return Task.FromResult(new ToolOutput { Payload = payload, ResultCount = page.Count, Source = "build" });
        }

        // ================================================================== get_diagnostics

        private const int MaxDiagnosticFiles = 50;

        private ToolDefinition DiagnosticsTool() => Define(
            "get_diagnostics", "UE rule diagnostics of files",
            "Runs UEToVS's Unreal checks on whole C++ files and returns the problems (1-based line/col, code UE0001…, message): unknown member on a fully " +
            "known type (UE0001), missing GENERATED_BODY (UE0003), hand-written body of a generated UFUNCTION (UE0004), ReplicatedUsing without an OnRep " +
            "UFUNCTION (UE0005), replicated property not in GetLifetimeReplicatedProps (UE0006), unknown gameplay tag / asset path (UE0007/8), UPROPERTY / " +
            "UFUNCTION types UHT rejects (UE0009/10). Only certain problems are reported. Use it to check your own edits before building. " +
            "Reads files as saved on disk; engine files are skipped.",
            Schema(
                "\"path\":{\"type\":\"string\",\"description\":\"One source file: absolute, or relative to the solution/project folder.\"}," +
                "\"paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":50,\"description\":\"Several files (max 50).\"}," +
                "\"min_severity\":{\"type\":\"string\",\"enum\":[\"info\",\"warning\",\"error\"],\"description\":\"Lowest severity returned (default info).\"}"),
            DiagnosticsAsync, readOnly: true);

        private async Task<ToolOutput> DiagnosticsAsync(ToolArgs a, CancellationToken ct)
        {
            var raw = new List<string>();
            if (a.Has("path")) raw.Add(a.String("path"));
            raw.AddRange(a.StringList("paths", MaxDiagnosticFiles));
            if (raw.Count == 0) throw ToolArgs.Invalid("pass 'path' or 'paths'");
            if (raw.Count > MaxDiagnosticFiles) throw ToolArgs.Invalid("at most " + MaxDiagnosticFiles + " files per call");
            string min = a.Choice("min_severity", "info", "info", "warning", "error");
            var minSev = min == "error" ? CodeDiagnosticSeverity.Error : min == "warning" ? CodeDiagnosticSeverity.Warning : CodeDiagnosticSeverity.Info;
            var paths = raw.Select(ResolvePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var ws = _index.Offline.Workspace();
            string projectRoot = ws?.ProjectRoot, engineRoot = ws?.EngineRoot;
            var skipped = new JsonArray();
            var check = new List<string>();
            foreach (var p in paths)
            {
                string why = null;
                if (!IsCppPath(p)) why = "not-cpp";
                else if (engineRoot != null && IsUnder(p, engineRoot) || projectRoot != null && !IsUnder(p, projectRoot)) why = "not-project-file";
                if (why != null) skipped.Add(new JsonObject { ["file"] = p, ["reason"] = why });
                else check.Add(p);
            }

            var files = new JsonArray();
            int total = 0, errors = 0, warnings = 0;
            string source = Sources.Index, note = null;
            if (check.Count > 0)
            {
                var answer = await _index.GetDiagnosticsAsync(check, ct).ConfigureAwait(false);
                source = answer.Source;
                note = answer.Note;
                foreach (var (path, skip, result) in answer.Value)
                {
                    if (result == null)
                    {
                        skipped.Add(new JsonObject { ["file"] = path, ["reason"] = skip ?? "unavailable" });
                        continue;
                    }
                    var items = new JsonArray();
                    foreach (var d in result.Items.Where(d => d.Severity >= minSev))
                    {
                        var o = new JsonObject
                        {
                            ["line"] = d.Line + 1,
                            ["col"] = d.Column + 1,
                            ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                            ["code"] = d.Code,
                            ["message"] = d.Message,
                        };
                        items.Add(o);
                        total++;
                        if (d.Severity == CodeDiagnosticSeverity.Error) errors++;
                        else if (d.Severity == CodeDiagnosticSeverity.Warning) warnings++;
                    }
                    var fo = new JsonObject { ["file"] = path, ["count"] = items.Count, ["checked"] = result.Checked, ["uncertain"] = result.Uncertain };
                    if (skip != null) fo["note"] = skip;
                    fo["items"] = items;
                    files.Add(fo);
                }
            }
            var payload = new JsonObject
            {
                ["source"] = source,
                ["files"] = files.Count,
                ["count"] = total,
                ["errors"] = errors,
                ["warnings"] = warnings,
            };
            if (total == 0 && files.Count > 0)
                note = Join(note, "No problems found by UEToVS's rules (they only report certain problems; the compiler may still fail — build, then get_build_errors).");
            if (!string.IsNullOrEmpty(note)) payload["note"] = note;
            if (skipped.Count > 0) payload["skipped"] = skipped;
            payload["results"] = files;
            return new ToolOutput { Payload = payload, ResultCount = total, Source = source };
        }

        private static bool IsCppPath(string p)
        {
            string ext = Path.GetExtension(p ?? string.Empty).ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".hxx" || ext == ".inl" || ext == ".cpp" || ext == ".cc" || ext == ".cxx" || ext == ".c";
        }

        // ================================================================== rename_symbol

        private ToolDefinition RenameSymbolTool() => Define(
            "rename_symbol", "Rename a C++ symbol (preview / apply)",
            "Safe index-based rename of a project class, struct, enum, function, property or variable: finds every reference, then by DEFAULT ONLY " +
            "PREVIEWS the edits (file, 1-based line/col, the source line). Call again with apply=true to write the files. Engine / third-party files " +
            "are never changed. Name-only matches (possibly another symbol with the same name) are left out unless include_name_only=true. " +
            "Reflected UCLASS/USTRUCT/UENUM/UPROPERTY/UFUNCTION renames add a [CoreRedirects] line to Config/DefaultEngine.ini (write_redirects, " +
            "default true) so existing Blueprints and assets keep loading. Files named after a class are not renamed. After applying, run get_diagnostics.",
            Schema(
                "\"symbol\":{\"type\":\"string\",\"description\":\"Current name, optionally qualified 'AMyActor::Health'.\"}," + CaretProps + "," +
                "\"new_name\":{\"type\":\"string\",\"description\":\"New identifier.\"}," +
                "\"apply\":{\"type\":\"boolean\",\"description\":\"Write the files (default false = preview only).\"}," +
                "\"include_name_only\":{\"type\":\"boolean\",\"description\":\"Also change name-only matches (default false).\"}," +
                "\"write_redirects\":{\"type\":\"boolean\",\"description\":\"Add Core Redirects for reflected symbols (default true).\"}," +
                "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000,\"description\":\"Max edits listed in the answer (default 200; all are applied).\"}",
                "new_name"),
            RenameSymbolAsync, readOnly: false);

        private async Task<ToolOutput> RenameSymbolAsync(ToolArgs a, CancellationToken ct)
        {
            string mode = RequireOne(a, "symbol");
            string newName = a.String("new_name", required: true, maxLength: 256).Trim();
            bool apply = a.Bool("apply", false);
            bool includeNameOnly = a.Bool("include_name_only", false);
            bool writeRedirects = a.Bool("write_redirects", true);
            int limit = a.Int("limit", 200, 1, 1000);
            if (!RenamePlanner.IsValidIdentifier(newName)) throw ToolArgs.Invalid("'" + newName + "' is not a valid C++ identifier");

            // ---- resolve the symbol
            SymbolInfo symbol;
            if (mode == "symbol")
            {
                var (name, container) = CppText.SplitQualified(a.String("symbol", required: true, maxLength: 512));
                var found = (await _index.FindSymbolsAsync(name, container, ct).ConfigureAwait(false)).Value;
                if (found.Count == 0) throw new ToolException("symbol_not_found", "No symbol '" + a.String("symbol") + "' in the index. Try search_symbols.");
                var project = found.Where(s => IsProjectOrigin(s.Origin)).ToList();
                if (project.Count == 0)
                    throw new ToolException("not_project_symbol", "'" + name + "' is declared in engine / third-party code, which rename_symbol never changes.");
                var groups = project.GroupBy(s => (s.Container ?? "") + "|" + KindFamily(s.Kind)).ToList();
                if (groups.Count > 1)
                    throw new ToolException("ambiguous_symbol", "'" + name + "' names several symbols; qualify it ('Owner::Name') or pass path + line + column. Candidates: " +
                        string.Join("; ", groups.Take(10).Select(g => Describe(g.First()))));
                symbol = groups[0].OrderBy(s => s.IsDefinition ? 1 : 0).First();
            }
            else
            {
                var caret = Caret(a);
                var nav = await _index.NavigateAsync(caret, NavigationKind.Definition, ct).ConfigureAwait(false);
                symbol = nav.Value.Resolved ?? nav.Value.Targets?.FirstOrDefault()?.Symbol;
                if (symbol == null) throw new ToolException("symbol_not_found", "No symbol at that position.");
                if (!IsProjectOrigin(symbol.Origin))
                    throw new ToolException("not_project_symbol", "'" + symbol.Name + "' is declared in engine / third-party code, which rename_symbol never changes.");
            }
            string oldName = symbol.Name;
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) throw ToolArgs.Invalid("new_name equals the current name");
            var redirectKind = RenamePlanner.RedirectFor(symbol);
            if ((redirectKind == RedirectKind.Class || redirectKind == RedirectKind.Struct) && oldName.Length > 1 && char.IsUpper(oldName[1]) &&
                (newName.Length < 2 || newName[0] != oldName[0] || !char.IsUpper(newName[1])))
                throw ToolArgs.Invalid("reflected type names keep their prefix: '" + oldName[0] + "' + an upper-case letter (e.g. " + oldName[0] + "Better" + "), UHT rejects others");

            // ---- references → plan
            var refs = await _index.FindReferencesAsync(new ReferencesRequest { Symbol = symbol, IncludeDeclarations = true, IncludeNonProject = true, MaxResults = 5000 },
                oldName, ct).ConfigureAwait(false);
            var ws = _index.Offline.Workspace();
            string projectRoot = ws?.ProjectRoot, engineRoot = ws?.EngineRoot;
            var hits = refs.Value.Hits.ToList();
            if (refs.Source != Sources.Engine)
            {
                // The on-disk occurrence index lists uses, not declarations: add the symbol's declarations / definitions. Name-only like
                // the uses, so an offline rename is all or nothing (include_name_only) and never renames just the declaration.
                var decls = (await _index.FindSymbolsAsync(oldName, string.IsNullOrEmpty(symbol.Container) ? null : OwnerClass(symbol.Container), ct).ConfigureAwait(false)).Value;
                foreach (var s in decls.Where(s => s.Location?.FilePath != null && (s.Container ?? "") == (symbol.Container ?? "") && KindFamily(s.Kind) == KindFamily(symbol.Kind)))
                {
                    var text = _index.Texts.Get(s.Location.FilePath);
                    hits.Add(new ReferenceHit
                    {
                        Location = s.Location, Length = oldName.Length, Kind = s.IsDefinition ? ReferenceKind.Definition : ReferenceKind.Declaration,
                        Confidence = ResolutionConfidence.NameOnly, Origin = s.Origin, Module = s.Module, LineText = text?.Line(s.Location.Line)?.Trim(),
                    });
                }
            }
            var locations = RenamePlanner.FromHits(hits, oldName);
            foreach (var l in locations)
            {
                // engine / third-party files are never edited, whatever the hit says (offline hits carry no origin)
                bool inProject = projectRoot != null ? IsUnder(l.FilePath, projectRoot) : refs.Source == Sources.Engine && l.IsEditable;
                if (engineRoot != null && IsUnder(l.FilePath, engineRoot)) inProject = false;
                l.IsEditable = l.IsEditable && inProject;
                if (!l.IsEditable) l.Checked = false;
                else if (l.Confidence == ResolutionConfidence.NameOnly) l.Checked = includeNameOnly;
            }
            var edits = RenamePlanner.Edits(locations, oldName, newName);
            int nameOnlyLeft = locations.Count(l => l.IsEditable && !l.Checked);
            int readOnly = locations.Count(l => !l.IsEditable);

            string ini = null, redirectLine = null;
            if (writeRedirects && redirectKind != RedirectKind.None && projectRoot != null)
            {
                string owner = OwnerClass(symbol.Container);
                redirectLine = RenamePlanner.RedirectLine(redirectKind, symbol.Module, oldName, newName, owner);
                if (redirectLine != null) ini = Path.Combine(projectRoot, "Config", "DefaultEngine.ini");
            }

            // ---- apply
            int applied = 0, skippedEdits = 0, filesChanged = 0;
            var failures = new JsonArray();
            if (apply)
            {
                if (edits.Count == 0) throw new ToolException("nothing_to_rename", "No editable project location to change (see the preview: name-only matches need include_name_only=true).");
                foreach (var g in edits.GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase))
                {
                    var (ok, n, skipped, error) = ApplyToFile(g.Key, g.ToList());
                    applied += n;
                    skippedEdits += skipped;
                    if (ok && n > 0) filesChanged++;
                    if (!ok) failures.Add(new JsonObject { ["file"] = g.Key, ["error"] = error });
                }
                if (ini != null && redirectLine != null)
                {
                    try
                    {
                        string text = File.Exists(ini) ? File.ReadAllText(ini) : string.Empty;
                        string updated = RenamePlanner.AddRedirects(text, new[] { redirectLine });
                        if (!string.Equals(updated, text, StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(ini));
                            File.WriteAllText(ini, updated, new UTF8Encoding(false));
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        failures.Add(new JsonObject { ["file"] = ini, ["error"] = ex.Message });
                        ini = null;
                    }
                }
            }

            // ---- answer
            var shown = locations.Where(l => l.IsEditable && l.Checked).Take(limit).ToList();
            var filesJson = new JsonArray();
            foreach (var g in shown.GroupBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                var edited = new JsonArray();
                foreach (var l in g)
                {
                    var o = new JsonObject { ["line"] = l.Line + 1, ["col"] = l.Column + 1 };
                    if (!string.IsNullOrEmpty(l.LineText)) o["text"] = l.LineText;
                    if (l.Confidence == ResolutionConfidence.NameOnly) o["nameOnly"] = true;
                    edited.Add(o);
                }
                filesJson.Add(new JsonObject { ["file"] = g.Key, ["edits"] = edited });
            }
            var payload = new JsonObject
            {
                ["source"] = refs.Source,
                ["symbol"] = ResultJson.Symbol(symbol, includeSignature: false),
                ["newName"] = newName,
                ["applied"] = apply,
                ["edits"] = edits.Count,
                ["files"] = edits.Select(e => e.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["nameOnlyNotIncluded"] = nameOnlyLeft,
                ["readOnlyReferences"] = readOnly,
            };
            if (redirectLine != null) payload["coreRedirect"] = new JsonObject { ["file"] = ini, ["line"] = redirectLine };
            if (apply)
            {
                payload["written"] = new JsonObject { ["edits"] = applied, ["skipped"] = skippedEdits, ["files"] = filesChanged };
                if (failures.Count > 0) payload["failures"] = failures;
            }
            string note = refs.Note;
            if (refs.Value.Truncated) note = Join(note, "More than 5000 references: the list is incomplete; rename in smaller steps or check with find_references.");
            if (nameOnlyLeft > 0) note = Join(note, nameOnlyLeft + " name-only match(es) not included (pass include_name_only=true after checking them with find_references).");
            note = Join(note, apply ? "Files written. " + SelfCheckHint : "Preview only: nothing was written. Call again with apply=true to change the files.");
            payload["note"] = note;
            payload["changes"] = filesJson;
            LB.Info("mcp.rename", null, ("name", oldName), ("newName", newName), ("apply", apply), ("source", refs.Source), ("edits", edits.Count),
                ("nameOnlyLeft", nameOnlyLeft), ("readOnly", readOnly), ("applied", applied), ("skipped", skippedEdits), ("files", filesChanged),
                ("redirect", redirectLine != null), ("failures", failures.Count));
            return new ToolOutput { Payload = payload, ResultCount = edits.Count, Source = refs.Source };
        }

        /// <summary>Applies the edits to one file on disk, keeping its encoding (BOM) and line endings. Each edit checks the old text first.</summary>
        private static (bool Ok, int Applied, int Skipped, string Error) ApplyToFile(string path, List<TextEdit> edits)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                string updated = TextEditApplier.Apply(text, edits, out int skipped);
                if (!string.Equals(updated, text, StringComparison.Ordinal)) File.WriteAllText(path, updated, new UTF8Encoding(bom));
                return (true, edits.Count - skipped, skipped, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return (false, 0, edits.Count, ex.Message);
            }
        }

        private static string KindFamily(SymbolKind k) =>
            k == SymbolKind.Method || k == SymbolKind.Function || k == SymbolKind.Constructor || k == SymbolKind.Destructor ? "function" : k.ToString();

        private static string Describe(SymbolInfo s) =>
            (string.IsNullOrEmpty(s.Container) ? "" : s.Container + "::") + s.Name + " (" + ResultJson.KindName(s.Kind) + ", " +
            Path.GetFileName(s.Location?.FilePath ?? "?") + ":" + ((s.Location?.Line ?? 0) + 1) + ")";

        /// <summary>"Ns::AMyActor" → "AMyActor" (the owner class of a property / function).</summary>
        private static string OwnerClass(string container)
        {
            if (string.IsNullOrEmpty(container)) return null;
            int i = container.LastIndexOf("::", StringComparison.Ordinal);
            return i < 0 ? container : container.Substring(i + 2);
        }
    }
}
