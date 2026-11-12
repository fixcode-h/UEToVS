using System;
using System.IO;
using Xunit;

// Loc.Language is process-wide; tests that switch it must not interleave with others reading strings.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UEToVS.Host.Logic.Tests
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uetovs-host-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Sub(params string[] parts)
        {
            string p = System.IO.Path.Combine(Path, System.IO.Path.Combine(parts));
            Directory.CreateDirectory(p);
            return p;
        }

        public string File(string relative, string content = "")
        {
            string p = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            System.IO.File.WriteAllText(p, content);
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
