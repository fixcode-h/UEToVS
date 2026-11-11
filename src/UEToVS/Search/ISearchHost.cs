using System.Collections.Generic;
using System.Threading.Tasks;
using UEToVS.Core.Protocol;
using UEToVS.Options;

namespace UEToVS.Search
{
    /// <summary>What the search popup needs from the package.</summary>
    internal interface ISearchHost
    {
        GeneralOptions Options { get; }

        /// <summary>The indexer proxy, starting the indexer if needed. Null when no UE project is open.</summary>
        Task<IIndexService> GetIndexAsync();

        /// <summary>Human readable index state for the footer, or null when ready.</summary>
        string IndexStatusText { get; }

        IReadOnlyList<string> RecentFilePaths { get; }

        ActionCatalog Actions { get; }

        /// <summary>Opens a location, recording it in navigation history.</summary>
        void NavigateTo(string path, int line, int column, OpenMode mode);

        void OpenAsset(AssetDto asset);

        void SaveOptions();
    }
}
