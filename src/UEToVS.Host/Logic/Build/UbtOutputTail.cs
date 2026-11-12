using System;
using System.Collections.Generic;
using System.Linq;

namespace UEToVS.Host.Logic.Build
{
    /// <summary>
    /// The end of a UBT run's output, kept to explain a failure (Generate Visual Studio project files): the last lines that look
    /// like errors, else the last lines at all. Bounded memory; thread-safe (output and error streams arrive on two threads).
    /// </summary>
    public sealed class UbtOutputTail
    {
        private readonly int _max;
        private readonly Queue<string> _all = new Queue<string>();
        private readonly Queue<string> _errors = new Queue<string>();
        private readonly object _gate = new object();

        public UbtOutputTail(int max = 15) => _max = Math.Max(1, max);

        public int Lines { get; private set; }

        public void Add(string line, bool fromStdErr = false)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_gate)
            {
                Lines++;
                Push(_all, line);
                if (fromStdErr || LooksLikeError(line)) Push(_errors, line);
            }
        }

        /// <summary>Lines to show after a failure: the error-looking ones when there are any, else the last ones.</summary>
        public List<string> FailureLines()
        {
            lock (_gate) return (_errors.Count > 0 ? _errors : _all).ToList();
        }

        public static bool LooksLikeError(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("0 error", StringComparison.OrdinalIgnoreCase) < 0) return true;
            return line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("失败", StringComparison.Ordinal) >= 0 || line.IndexOf("错误", StringComparison.Ordinal) >= 0;
        }

        private void Push(Queue<string> q, string line)
        {
            q.Enqueue(line.Length > 1000 ? line.Substring(0, 1000) + "…" : line);
            while (q.Count > _max) q.Dequeue();
        }
    }
}
