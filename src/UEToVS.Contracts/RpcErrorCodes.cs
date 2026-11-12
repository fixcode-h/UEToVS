namespace UEToVS.Contracts
{
    /// <summary>JSON-RPC error codes returned by the engine (host switches on RemoteInvocationException.ErrorCode).</summary>
    public static class RpcErrorCodes
    {
        /// <summary>HandshakeRequest.ProtocolVersion != ProtocolInfo.Version: extension and engine come from different builds.</summary>
        public const int ProtocolMismatch = -32050;

        /// <summary>Engine is shutting down.</summary>
        public const int ShuttingDown = -32051;

        /// <summary>An argument value is invalid. Not -32602: StreamJsonRpc maps that to RemoteMethodNotFoundException.</summary>
        public const int InvalidArgument = -32052;
    }
}
