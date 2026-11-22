using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Host.Logic.Localization;
using UEToVS.Host.Logic.Vcs;

namespace UEToVS.Host.Vcs
{
    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public byte[] Stdout { get; set; } = Array.Empty<byte>();
        public string Stderr { get; set; } = string.Empty;
        public double ElapsedMs { get; set; }

        /// <summary>标准输出按 UTF-8 解码（git 的输出都是 UTF-8；p4 的 -ztag 输出不是 UTF-8 时退回 ANSI）。</summary>
        public string Text => TextDecoding.Decode(Stdout, Encoding.Default) ?? string.Empty;
    }

    /// <summary>
    /// 跑 git / p4 这类命令行：一律在后台线程、带超时、响应取消（超时或取消时结束进程），不弹窗口，标准输入立刻关掉
    /// （p4 要密码时直接失败而不是卡住）。输出超过上限时截断并结束进程，避免一个异常大的输出吃掉 VS 内存。
    /// </summary>
    internal static class ProcessRunner
    {
        public const int MaxOutputBytes = 64 * 1024 * 1024;

        /// <param name="onChunk">标准输出每读到一块就回调（在后台线程上），给“已找到 N 次提交”这类进度用。</param>
        public static async Task<ProcessResult> RunAsync(string exe, string args, string workDir, TimeSpan timeout, CancellationToken ct,
                                                         string what, Action<byte[], int> onChunk = null, IDictionary<string, string> env = null)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = workDir ?? string.Empty,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // git 不要分页器、不要弹凭据框；p4 只用自己的票据（不传 -P）
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GIT_PAGER"] = "cat";
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                p.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                p.Dispose();
                throw new VcsException(what, VcsFailure.NotFound);
            }
            using (p)
            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
            {
                try { p.StandardInput.Close(); } catch (IOException) { }
                var stdout = new MemoryStream();
                var readOut = ReadAllAsync(p.StandardOutput.BaseStream, stdout, onChunk, linked.Token);
                var readErr = p.StandardError.ReadToEndAsync();
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                p.Exited += (s, e) => exited.TrySetResult(true);
                if (p.HasExited) exited.TrySetResult(true);
                using (linked.Token.Register(() => exited.TrySetCanceled()))
                {
                    try
                    {
                        await exited.Task.ConfigureAwait(false);
                        await readOut.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Kill(p);
                        if (ct.IsCancellationRequested) throw;
                        throw new VcsException(VcsStrings.TimedOut(what), VcsFailure.TimedOut);
                    }
                    catch (OutputTooLargeException)
                    {
                        Kill(p);
                    }
                }
                // 进程已退出：等管道读完（上限 2 秒，防止子进程继承了句柄一直不关）
                string err = string.Empty;
                if (await Task.WhenAny(readErr, Task.Delay(2000)).ConfigureAwait(false) == readErr) err = await readErr.ConfigureAwait(false);
                int code;
                try { code = p.ExitCode; } catch (InvalidOperationException) { code = -1; }
                return new ProcessResult { ExitCode = code, Stdout = stdout.ToArray(), Stderr = err ?? string.Empty, ElapsedMs = sw.Elapsed.TotalMilliseconds };
            }
        }

        private sealed class OutputTooLargeException : Exception
        {
        }

        private static async Task ReadAllAsync(Stream source, MemoryStream target, Action<byte[], int> onChunk, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int n = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (n <= 0) return;
                if (target.Length + n > MaxOutputBytes) throw new OutputTooLargeException();
                await target.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                onChunk?.Invoke(buffer, n);
            }
        }

        public static void Kill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (Exception)
            {
                // 已经退出 / 没权限：不管
            }
        }
    }
}
