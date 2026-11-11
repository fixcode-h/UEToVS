using System;

namespace UEToVS.Indexer
{
    internal static class Log
    {
        public static bool Verbose { get; set; }

        // stdout may be the RPC channel in some hosts; always log to stderr.
        public static void Info(string msg) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

        public static void Warn(string msg) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARN {msg}");

        public static void Debug(string msg)
        {
            if (Verbose) Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
    }
}
