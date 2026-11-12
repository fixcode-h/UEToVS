using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Host.Logic.Commands;
using UEToVS.Host.Logic.Diagnostics;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Logic.Unreal;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    /// <summary>Fixes from the 2026-11-12 diagnostics package (v0.4.3, UE 5.8, VS 18.6).</summary>
    public class DiagnosticsFollowUpTests
    {
        private sealed class CountingEnv : IIncludeEnvironment
        {
            public int ModuleLookups, NameLookups;
            public HashSet<string> Existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Task<IReadOnlyList<string>> FindFilesByNameAsync(string fileName, CancellationToken ct)
            {
                NameLookups++;
                return Task.FromResult<IReadOnlyList<string>>(new[] { @"C:\E\" + fileName });
            }

            public string FindBuildCsForFile(string filePath) => null;

            public Task<string> FindBuildCsForModuleAsync(string module, CancellationToken ct)
            {
                ModuleLookups++;
                return Task.FromResult(module == "Unknown" ? null : @"C:\E\" + module + ".Build.cs");
            }

            public string ReadText(string path) => null;
            public bool FileExists(string path) => Existing.Contains(path);
        }

        [Fact]
        public async Task IncludeLookupCacheAnswersRepeatedModuleLookupsWithoutTheEngine()
        {
            var now = new DateTime(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc);
            var cache = new IncludeLookupCache(() => now);
            var inner = new CountingEnv();
            inner.Existing.Add(@"C:\E\Core.Build.cs");
            var env = new CachingIncludeEnvironment(inner, cache);

            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(@"C:\E\Core.Build.cs", await env.FindBuildCsForModuleAsync("Core", CancellationToken.None));
                Assert.Null(await env.FindBuildCsForModuleAsync("Unknown", CancellationToken.None));
                await env.FindFilesByNameAsync("Actor.h", CancellationToken.None);
            }
            Assert.Equal(2, inner.ModuleLookups);
            Assert.Equal(1, inner.NameLookups);

            // unknown modules and file-name results expire; a known Build.cs stays while the file exists
            now += IncludeLookupCache.NegativeTtl + TimeSpan.FromSeconds(1);
            await env.FindBuildCsForModuleAsync("Unknown", CancellationToken.None);
            await env.FindBuildCsForModuleAsync("Core", CancellationToken.None);
            await env.FindFilesByNameAsync("Actor.h", CancellationToken.None);
            Assert.Equal(3, inner.ModuleLookups);
            Assert.Equal(2, inner.NameLookups);

            inner.Existing.Clear(); // Build.cs deleted / moved → looked up again
            await env.FindBuildCsForModuleAsync("Core", CancellationToken.None);
            Assert.Equal(4, inner.ModuleLookups);

            cache.BindTo(new object()); // new engine client → everything dropped
            Assert.Equal(0, cache.ModuleCount);
        }

        [Fact]
        public void VsMisspelledBackspaceIsCanonical()
        {
            Assert.Equal("Ctrl+Shift+Bkspce", Shortcut.Normalize("Ctrl+Shift+Backsapce"));
            Assert.Equal(Shortcut.Normalize("Ctrl+Shift+Bkspce"), Shortcut.Normalize("Ctrl+Shift+Backsapce"));
        }

        [Fact]
        public void DesignerScopesDoNotConflictWithGlobalKeys()
        {
            Assert.True(Shortcut.IsDesignerScope("VC 对话框编辑器"));
            Assert.True(Shortcut.IsDesignerScope("VC Dialog Editor"));
            Assert.False(Shortcut.IsDesignerScope("Text Editor"));
            Assert.False(Shortcut.IsDesignerScope("解决方案资源管理器"));
            Assert.False(Shortcut.ScopesOverlap("Global", "VC 对话框编辑器"));
            Assert.True(Shortcut.ScopesOverlap("Global", "Text Editor"));
            Assert.True(Shortcut.ScopesOverlap("VC Dialog Editor", "VC Dialog Editor"));

            var ours = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["UEToVS.NavigateBack"] = new[] { "Global::Alt+Left Arrow" } };
            var all = new[]
            {
                new KeyValuePair<string, string[]>("Format.SpaceAcross", new[] { "VC 对话框编辑器::Alt+Left Arrow" }),
                new KeyValuePair<string, string[]>("Edit.Something", new[] { "Text Editor::Alt+Left Arrow" }),
            };
            var c = KeymapPlanner.FindConflicts(ours, all);
            Assert.Single(c);
            Assert.Equal("Edit.Something", c[0].OtherCommand);
        }

        [Fact]
        public void Vs18PropertyNamesAreCandidates()
        {
            var table = CppIntelliSenseSettings.All.Concat(CppIntelliSenseSettings.BackgroundCodeAnalysis).ToList();
            bool Has(string key, string name) => table.Single(d => d.Key == key).Candidates.Any(c => c.Name == name);
            Assert.True(Has(CppIntelliSenseSettings.KeyBackgroundCodeAnalysis, "DisableCodeAnalysisInBackground"));
            Assert.True(Has(CppIntelliSenseSettings.KeySemanticColorization, "DisableSemanticColoring"));
            Assert.True(Has(CppIntelliSenseSettings.KeyAutomaticUpdates, "DisableDatabaseAutoUpdates"));
            Assert.True(Has(CppIntelliSenseSettings.KeyAutoQuickInfo, "EnableQuickInfoToolTips"));
        }

        [Fact]
        public void LineSanitizerMatchesSanitizeAndPackageLogsAreSanitized()
        {
            var san = Sanitizer.CreateLineSanitizer("alice", @"C:\Users\alice");
            string line = "{\"file\":\"C:\\\\Users\\\\alice\\\\Desktop\\\\a.h\",\"who\":\"alice\",\"x\":\"malice\"}";
            Assert.Equal(Sanitizer.Sanitize(line, "alice", @"C:\Users\alice"), san(line));
            Assert.Equal("nothing here", san("nothing here"));
            Assert.DoesNotContain("alice\\\\", san(line));

            string root = Path.Combine(Path.GetTempPath(), "uetovs-san-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Logs"));
                Directory.CreateDirectory(Path.Combine(root, "Index"));
                File.WriteAllText(Path.Combine(root, "Logs", "host-1.log"), line + "\n" + line + "\n");
                File.WriteAllText(Path.Combine(root, "Index", "manifest.json"), "{\"projectFile\":\"C:\\\\Users\\\\alice\\\\P.uproject\"}");
                var r = DiagnosticsPackageBuilder.Create(root, Path.Combine(root, "out"), null, null, null, san);
                using (var zip = ZipFile.OpenRead(r.ZipPath))
                {
                    foreach (var name in new[] { "Logs/host-1.log", "Index/manifest.json" })
                    {
                        using (var rd = new StreamReader(zip.GetEntry(name).Open()))
                        {
                            string text = rd.ReadToEnd();
                            Assert.DoesNotContain("Users\\\\alice", text);
                            Assert.Contains("%USERPROFILE%", text);
                        }
                    }
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
