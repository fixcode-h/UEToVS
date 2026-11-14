using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.Link
{
    /// <summary>
    /// The card a running UELink writes for its process: &lt;Project&gt;/Saved/UELink/instances/&lt;PID&gt;.json
    /// (docs/protocol.md §1 of fixcode-h/UELink). UEToVS uses the push-stream port and the token from it; a card of a
    /// process that is gone (editor crashed) is ignored. Before UELink, UEToVSLink only wrote Saved/UEToVSLink/port.txt,
    /// which <see cref="UeEditorLinkClient"/> still reads when there is no card (old plugin, or UELink 2.0 compat copy).
    /// </summary>
    public sealed class LinkCard
    {
        public int Pid { get; set; }

        /// <summary>"editor" | "commandlet" | "game"; only an editor has a push stream.</summary>
        public string Kind { get; set; }

        public int StreamPort { get; set; }
        public int HttpPort { get; set; }
        public string Token { get; set; }
        public string Version { get; set; }
        public int Protocol { get; set; }
        public string StartedAt { get; set; }
        public string File { get; set; }

        public static string InstancesDir(string projectRoot) =>
            string.IsNullOrEmpty(projectRoot) ? null : Path.Combine(projectRoot, "Saved", "UELink", "instances");

        public static bool ProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public static LinkCard Parse(string json, string file = null)
        {
            try
            {
                var o = JObject.Parse(json ?? string.Empty);
                // UE's JSON writer may write whole numbers as 51234.0: accept both.
                int Int(string key) => o[key]?.Type == JTokenType.Integer || o[key]?.Type == JTokenType.Float ? (int)(double)o[key] : 0;
                return new LinkCard
                {
                    Pid = Int("pid"),
                    Kind = o["kind"]?.ToString(),
                    StreamPort = Int("stream"),
                    // UELink writes the HTTP port as "port" ("http" in later versions); either is the HTTP port.
                    HttpPort = Int("http") > 0 ? Int("http") : Int("port"),
                    Token = o["token"]?.ToString(),
                    Version = o["version"]?.ToString(),
                    Protocol = Int("protocol"),
                    StartedAt = o["startedAt"]?.ToString(),
                    File = file,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Cards of live processes that have a push stream, newest first (several editors of one project: the one started
        /// last wins, the same as port.txt, which the last editor overwrote).
        /// </summary>
        public static List<LinkCard> ReadLive(string projectRoot, Func<int, bool> alive = null)
        {
            alive = alive ?? ProcessAlive;
            var list = new List<LinkCard>();
            string dir = InstancesDir(projectRoot);
            if (dir == null || !Directory.Exists(dir)) return list;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return list;
            }
            foreach (var f in files)
            {
                LinkCard card;
                try
                {
                    card = Parse(System.IO.File.ReadAllText(f), f);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue; // being written right now: picked up on the next call
                }
                if (card == null || card.StreamPort <= 0 || card.StreamPort >= 65536 || !alive(card.Pid)) continue;
                list.Add(card);
            }
            return list.OrderByDescending(c => ParseTime(c.StartedAt)).ThenByDescending(c => c.Pid).ToList();
        }

        private static DateTime ParseTime(string s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;
    }
}
