using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UEToVS.Core.Diagnostics;

namespace UEToVS.Host.Agent
{
    /// <summary>
    /// API keys of the Agent / API page, encrypted with DPAPI for the current Windows user
    /// (%LocalAppData%\UEToVS\agent-keys.dat: {apiId: base64(ProtectedData)}). Keys never go into agent-apis.json, logs,
    /// VS's model file or the solution; copying the file to another user or machine makes it unreadable.
    /// </summary>
    internal sealed class AgentSecrets
    {
        private static readonly Logger L = Log.For("AgentSecrets");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UEToVS.AgentApi.v1");
        private readonly string _path;
        private readonly object _gate = new object();
        private Dictionary<string, string> _cache;

        public AgentSecrets(string path)
        {
            _path = path;
        }

        public bool Has(string apiId) => !string.IsNullOrEmpty(Get(apiId));

        public string Get(string apiId)
        {
            if (string.IsNullOrEmpty(apiId)) return null;
            lock (_gate)
            {
                var all = LoadLocked();
                if (!all.TryGetValue(apiId, out var protectedText)) return null;
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch (Exception ex)
                {
                    L.Warn("secrets.unprotect", "stored key cannot be decrypted (other user/machine?)", ex, ("api", apiId));
                    return null;
                }
            }
        }

        /// <summary>Sets (non-empty) or removes (null/empty) the keys in one write.</summary>
        public void Update(IReadOnlyDictionary<string, string> changes, IEnumerable<string> removedApis)
        {
            lock (_gate)
            {
                var all = new Dictionary<string, string>(LoadLocked(), StringComparer.Ordinal);
                foreach (var kv in changes)
                {
                    if (string.IsNullOrEmpty(kv.Value)) all.Remove(kv.Key);
                    else all[kv.Key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(kv.Value), Entropy, DataProtectionScope.CurrentUser));
                }
                foreach (var id in removedApis) all.Remove(id);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                _cache = all;
                L.Info("secrets.update", null, ("changed", changes.Count), ("stored", all.Count));
            }
        }

        private Dictionary<string, string> LoadLocked()
        {
            if (_cache != null) return _cache;
            try
            {
                _cache = File.Exists(_path)
                    ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_path, Encoding.UTF8)) ?? new Dictionary<string, string>()
                    : new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                L.Warn("secrets.load", "key file unreadable; treating as empty", ex);
                _cache = new Dictionary<string, string>();
            }
            return _cache;
        }
    }
}
