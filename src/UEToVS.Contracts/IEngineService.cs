using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UEToVS.Contracts
{
    /// <summary>
    /// RPC surface of the engine process (host → engine). Every call is cancellable; the engine must never block
    /// on the host. Methods are added, never changed; see <see cref="ProtocolInfo.Version"/>.
    /// </summary>
    public interface IEngineService
    {
        Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken ct);

        Task<EngineStatus> GetStatusAsync(CancellationToken ct);

        Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct);

        /// <summary>Declarations in one file, in source order (file structure view).</summary>
        Task<List<SymbolInfo>> GetFileSymbolsAsync(string filePath, CancellationToken ct);

        /// <summary>Exact-name lookup, optionally restricted to a container (class / namespace).</summary>
        Task<List<SymbolInfo>> FindSymbolsAsync(string name, string container, CancellationToken ct);

        Task NotifyFilesChangedAsync(List<FileChange> changes, CancellationToken ct);

        /// <summary>Change the engine's log level at runtime (Error/Warn/Info/Debug/Trace).</summary>
        Task SetLogLevelAsync(string level, CancellationToken ct);

        /// <summary>
        /// Data for the performance panel: current resources, a short resource history, index phase timings and
        /// per-operation latency since the engine started. Cheap (served from counters), safe to poll every second.
        /// </summary>
        Task<PerformanceSnapshot> GetPerformanceSnapshotAsync(CancellationToken ct);

        Task ShutdownAsync(CancellationToken ct);
    }

    /// <summary>Notifications engine → host.</summary>
    public interface IEngineCallbacks
    {
        Task OnStatusChangedAsync(EngineStatus status);
    }
}
