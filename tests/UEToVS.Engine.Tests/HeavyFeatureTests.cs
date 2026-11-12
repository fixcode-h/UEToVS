using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Deprecation;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Inspection;
using UEToVS.Engine.Navigation;
using UEToVS.Engine.Unreal;
using Xunit;
using Xunit.Abstractions;

namespace UEToVS.Engine.Tests
{
    /// <summary>v0.4.5 whole-project inspection (over the IntelliSense corpus) and its mechanical fixes / results file.</summary>
    public sealed class ProjectInspectionTests : IClassFixture<IntelliSenseFixture>
    {
        private readonly IntelliSenseFixture _f;
        private readonly ITestOutputHelper _out;

        public ProjectInspectionTests(IntelliSenseFixture f, ITestOutputHelper output)
        {
            _f = f;
            _out = output;
        }

        private ProjectInspectionService NewService(string dir, int threads = 2) =>
            new ProjectInspectionService(_f.Service, plugins => ProjectInspectionService.ProjectFiles(_f.Files, plugins), () => threads, () => true, dir, _f.Root);

        [Fact]
        public void Run_StreamsFindingsToJsonLines_WithCountsAndFixes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-inspect", Guid.NewGuid().ToString("N"));
            try
            {
                using (var svc = NewService(dir))
                {
                    var started = svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Start }, CancellationToken.None);
                    Assert.Equal(HeavyJobState.Running, started.State);
                    Assert.True(started.FilesTotal > 5);
                    Assert.True(svc.Wait(TimeSpan.FromSeconds(60)));
                    var st = svc.Status();
                    _out.WriteLine($"{st.State} files={st.FilesDone}/{st.FilesTotal} findings={st.Findings} rules={string.Join(",", st.Rules.Select(r => r.Code + "=" + r.Count))} peakMB={st.PeakWorkingSetBytes >> 20}");
                    Assert.Equal(HeavyJobState.Completed, st.State);
                    Assert.Equal(st.FilesTotal, st.FilesDone);
                    Assert.StartsWith(dir, st.ResultsPath);
                    Assert.EndsWith(".jsonl", st.ResultsPath);
                    Assert.True(st.Findings > 0);
                    Assert.Equal(st.Findings, st.Rules.Sum(r => r.Count));

                    // the hand-written Server RPC body: UE0004 with a rename fix
                    var page = svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Results, Rule = CodeDiagnosticCodes.GeneratedFunctionBody }, CancellationToken.None);
                    var server = Assert.Single(page.Items, i => i.Args.FirstOrDefault() == "ServerUse");
                    Assert.EndsWith("Gadget.cpp", server.FilePath);
                    var edit = Assert.Single(server.Fix);
                    Assert.Equal("ServerUse", edit.OldText);
                    Assert.Equal("ServerUse_Implementation", edit.NewText);
                    Assert.Contains("AGadget::ServerUse", server.LineText);

                    // paging and filters over the file
                    var all = svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Results, Limit = 2000 }, CancellationToken.None);
                    Assert.Equal(st.Findings, all.Matched);
                    var second = svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Results, Offset = 1, Limit = 1 }, CancellationToken.None);
                    Assert.Single(second.Items);
                    Assert.Equal(all.Items[1].Line, second.Items[0].Line);
                    var byFile = svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Results, File = "gadget.cpp", Limit = 2000 }, CancellationToken.None);
                    Assert.All(byFile.Items, i => Assert.EndsWith("Gadget.cpp", i.FilePath));
                    var fixable = svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Results, FixableOnly = true }, CancellationToken.None);
                    Assert.All(fixable.Items, i => Assert.NotEmpty(i.Fix));
                    Assert.Equal(st.Rules.Sum(r => r.Fixable), fixable.Matched);

                    var lines = File.ReadAllLines(st.ResultsPath);
                    Assert.Contains("\"type\":\"inspection\"", lines.First());
                    Assert.Contains("\"type\":\"summary\"", lines.Last());
                    Assert.Equal(st.Findings, lines.Count(l => l.Contains("\"type\":\"finding\"")));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void Disabled_RefusesEverything_AndCancelsARun()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-inspect", Guid.NewGuid().ToString("N"));
            try
            {
                using (var svc = NewService(dir, 1))
                {
                    svc.ShouldPause = () => true; // hold the run so it is still going when switched off
                    Assert.Equal(HeavyJobState.Running, svc.Start(false).State);
                    svc.Enabled = false;
                    svc.ShouldPause = null;
                    Assert.True(svc.Wait(TimeSpan.FromSeconds(30)));
                    Assert.Equal(HeavyJobState.Disabled, svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Start }, CancellationToken.None).State);
                    Assert.Equal(HeavyJobState.Disabled, svc.Handle(new ProjectInspectionRequest { Action = HeavyJobAction.Status }, CancellationToken.None).State);
                    svc.Enabled = true;
                    Assert.Equal(HeavyJobState.Cancelled, svc.Status().State);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void NotReadyIndex_IsRefused()
        {
            using (var svc = new ProjectInspectionService(_f.Service, _ => new[] { "x.h" }, () => 1, () => false, Path.GetTempPath(), null))
            {
                var r = svc.Start(false);
                Assert.Equal(HeavyJobState.Failed, r.State);
                Assert.Equal("index-not-ready", r.Error);
                Assert.Equal(HeavyJobState.Idle, svc.Status().State);
            }
        }
    }

    public sealed class InspectionFixesTests
    {
        private static CodeDiagnostic Diag(DocumentModel doc, string word, string code, params string[] args)
        {
            for (int line = 0; line < doc.Lines.LineCount; line++)
            {
                string text = doc.LineText(line);
                int col = text.IndexOf(word, StringComparison.Ordinal);
                if (col >= 0 && (code != CodeDiagnosticCodes.GeneratedFunctionBody || text.Contains("::" + word)) &&
                    (code != CodeDiagnosticCodes.MissingGeneratedBody || text.StartsWith("class", StringComparison.Ordinal) || text.StartsWith("struct", StringComparison.Ordinal)))
                    return new CodeDiagnostic { Line = line, Column = col, Length = word.Length, Code = code, Args = args.ToList() };
            }
            throw new InvalidOperationException(word);
        }

        [Fact]
        public void GeneratedBody_InsertedAsFirstBodyLine_WithIndentAndNewline()
        {
            var doc = DocumentModel.Parse("C:/p/Thing.h", 0, "UCLASS()\r\nclass AThing : public AActor\r\n{\r\npublic:\r\n\tvoid Do();\r\n};\r\n");
            var fix = InspectionFixes.For(doc, Diag(doc, "AThing", CodeDiagnosticCodes.MissingGeneratedBody, "UCLASS", "AThing"));
            var e = Assert.Single(fix);
            Assert.Equal(3, e.Line);
            Assert.Equal(0, e.Column);
            Assert.Equal("\tGENERATED_BODY()\r\n", e.NewText);

            var oneLine = DocumentModel.Parse("C:/p/One.h", 0, "USTRUCT()\nstruct FOne { int32 X; };\n");
            Assert.Null(InspectionFixes.For(oneLine, Diag(oneLine, "FOne", CodeDiagnosticCodes.MissingGeneratedBody, "USTRUCT", "FOne")));
        }

        [Fact]
        public void Rename_OnlyOutOfLineAndOnlyForGeneratedImplementationSpecifiers()
        {
            var doc = DocumentModel.Parse("C:/p/Thing.cpp", 0, "void AThing::ServerDo(int32 A)\n{\n}\nvoid AThing::OnHit()\n{\n}\n");
            var fix = InspectionFixes.For(doc, Diag(doc, "ServerDo", CodeDiagnosticCodes.GeneratedFunctionBody, "ServerDo", "Server", "AThing"));
            Assert.Equal("ServerDo_Implementation", Assert.Single(fix).NewText);
            Assert.Null(InspectionFixes.For(doc, Diag(doc, "OnHit", CodeDiagnosticCodes.GeneratedFunctionBody, "OnHit", "BlueprintImplementableEvent", "AThing")));

            var withImpl = DocumentModel.Parse("C:/p/T2.cpp", 0, "void AThing::ServerDo()\n{\n}\nvoid AThing::ServerDo_Implementation()\n{\n}\n");
            Assert.Null(InspectionFixes.For(withImpl, Diag(withImpl, "ServerDo", CodeDiagnosticCodes.GeneratedFunctionBody, "ServerDo", "Server", "AThing")));

            var inClass = DocumentModel.Parse("C:/p/T3.h", 0, "class AThing\n{\n\tvoid ClientDo() {}\n};\n");
            var d = new CodeDiagnostic { Line = 2, Column = 6, Length = 8, Code = CodeDiagnosticCodes.GeneratedFunctionBody, Args = new List<string> { "ClientDo", "Client", "AThing" } };
            Assert.Null(InspectionFixes.For(inClass, d));
            Assert.True(InspectionFixes.IsFixableRule("UE0003"));
            Assert.False(InspectionFixes.IsFixableRule("UE0001"));
        }

        [Fact]
        public void ResultsFile_ParsesFindingsAndSkipsBrokenLines()
        {
            string path = Path.Combine(Path.GetTempPath(), "uetovs-jsonl-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                var f = new InspectionFinding
                {
                    FilePath = "C:/p/中文.cpp", Line = 3, Column = 4, Length = 5, Code = "UE0004", Args = new List<string> { "A", "Server" }, Message = "m", LineText = "t",
                    Fix = new List<FileTextEdit> { new FileTextEdit { FilePath = "C:/p/中文.cpp", Line = 3, Column = 4, Length = 1, OldText = "A", NewText = "A_Implementation" } },
                };
                using (var stream = File.Create(path))
                using (var w = new System.Text.Json.Utf8JsonWriter(stream))
                {
                    InspectionResultsFile.WriteFinding(w, f);
                    w.Flush();
                    stream.WriteByte((byte)'\n');
                    stream.Write(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"finding\",\"file\":\"trunc"));
                }
                var page = InspectionResultsFile.ReadPage(path, "ue0004", "中文", true, 0, 10, out int matched, CancellationToken.None);
                Assert.Equal(1, matched);
                var back = Assert.Single(page);
                Assert.Equal(f.FilePath, back.FilePath);
                Assert.Equal("A_Implementation", back.Fix[0].NewText);
                Assert.Equal(new[] { "A", "Server" }, back.Args);
                Assert.Empty(InspectionResultsFile.ReadPage(path, "UE0001", null, false, 0, 10, out _, CancellationToken.None));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Prune_KeepsNewestFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "uetovs-prune", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(dir, "2026010" + i + "-000000.jsonl"), "");
                Assert.Equal(2, InspectionResultsFile.Prune(dir, 3));
                Assert.Equal(new[] { "20260102-000000.jsonl", "20260103-000000.jsonl", "20260104-000000.jsonl" },
                    Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
                string p = InspectionResultsFile.NewPath(dir, new DateTime(2026, 1, 4));
                Assert.EndsWith("20260104-000000-2.jsonl", p);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>v0.4.5 engine upgrade assistant over a tiny engine + project index.</summary>
    public sealed class DeprecationScanTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uetovs-upgrade", Guid.NewGuid().ToString("N"));
        private readonly SegmentedIndexBackend _backend = new SegmentedIndexBackend();
        private readonly FileTable _files = new FileTable();

        private static readonly Dictionary<string, string> Sources = new Dictionary<string, string>
        {
            ["Engine/Public/Thing.h"] =
                "#pragma once\nclass ENGINE_API UThing\n{\npublic:\n" +
                "\tUE_DEPRECATED(5.1, \"Use GetNewName() instead.\")\n\tFString GetOldName() const;\n" +
                "\tFString GetNewName() const;\n" +
                "\tUE_DEPRECATED(5.3, \"Use SetValueEx instead.\")\n\tvoid SetValue(int32 A);\n" +
                "\tvoid SetValueEx(int32 A, int32 B);\n" +
                "\tUE_DEPRECATED(4.27, \"No longer used.\")\n\tvoid Legacy();\n" +
                "};\n",
            ["Game/Private/User.cpp"] =
                "#include \"Thing.h\"\nvoid UseIt(UThing* T)\n{\n\tT->GetOldName();\n\tT->SetValue(1);\n\tT->GetNewName();\n}\n",
            ["Game/Private/Other.cpp"] =
                "struct FMine { void Legacy(); };\nvoid Mine(FMine& M)\n{\n\tM.Legacy();\n}\n",
        };

        public DeprecationScanTests()
        {
            _backend.TryLoad(Path.Combine(_root, ".index"), "upgrade");
            var parser = new SyntaxFileParser();
            foreach (var kv in Sources)
            {
                string path = Path.Combine(_root, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kv.Value);
                bool engine = kv.Key.StartsWith("Engine/", StringComparison.Ordinal);
                var listed = new ListedFile
                {
                    Path = path, Name = Path.GetFileName(path), Module = engine ? "Engine" : "Game", Origin = engine ? ItemOrigin.Engine : ItemOrigin.Project,
                    Kind = path.EndsWith(".h", StringComparison.Ordinal) ? SourceFileKind.Header : SourceFileKind.Source,
                };
                _files.Upsert(listed);
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                _backend.UpdateParsed(path, parser.Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
        }

        [Fact]
        public void Scan_FindsUsagesGroupedWithCompatibleReplacements()
        {
            var svc = new DeprecationScanService(_backend, _files, () => true);
            Assert.Equal(HeavyJobState.Running, svc.Handle(new DeprecationScanRequest { Action = HeavyJobAction.Start }).State);
            Assert.True(svc.Wait(TimeSpan.FromSeconds(30)));
            var r = svc.Handle(new DeprecationScanRequest { Action = HeavyJobAction.Results });
            Assert.Equal(HeavyJobState.Completed, r.State);
            Assert.True(r.IndexHasDeprecations);
            Assert.Equal(3, r.DeprecatedSymbols);

            // newest deprecation version first; FMine::Legacy is a different symbol and not reported
            Assert.Equal(new[] { "SetValue", "GetOldName" }, r.Apis.Select(a => a.Symbol.Name).ToArray());
            var oldName = r.Apis[1];
            Assert.Equal("5.1", oldName.Version);
            Assert.Equal("GetNewName", oldName.Replacement);
            var u = Assert.Single(oldName.Usages);
            Assert.Equal(3, u.Line);
            Assert.Equal(4, u.Column);
            Assert.Equal(ResolutionConfidence.Exact, u.Confidence);
            Assert.Equal("T->GetOldName();", u.LineText);

            var setValue = r.Apis[0];
            Assert.Null(setValue.Replacement); // SetValueEx takes two parameters
            Assert.Equal("signature", setValue.ReplacementNote);
            Assert.Equal(2, r.Usages);
        }

        [Fact]
        public void Disabled_Refuses()
        {
            var svc = new DeprecationScanService(_backend, _files, () => true) { Enabled = false };
            var r = svc.Handle(new DeprecationScanRequest { Action = HeavyJobAction.Start });
            Assert.Equal(HeavyJobState.Disabled, r.State);
            Assert.False(string.IsNullOrEmpty(r.Error));
            Assert.Equal(HeavyJobState.Disabled, svc.Handle(new DeprecationScanRequest { Action = HeavyJobAction.Status }).State);
        }

        public void Dispose()
        {
            _backend.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
