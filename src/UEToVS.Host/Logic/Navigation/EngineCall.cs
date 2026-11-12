using UEToVS.Host.Logic.Localization;

namespace UEToVS.Host.Logic.Navigation
{
    /// <summary>Outcome of a navigation RPC. Anything but <see cref="Ok"/> degrades the feature (status bar text, no exception).</summary>
    public enum EngineCallStatus
    {
        Ok = 0,
        /// <summary>Engine not connected (not started, restarting, not installed, crashed).</summary>
        Unavailable = 1,
        Timeout = 2,
        /// <summary>Superseded by a newer request or the user moved on; silent.</summary>
        Canceled = 3,
        /// <summary>Remote error or unexpected exception (logged).</summary>
        Failed = 4,
    }

    /// <summary>Result of a navigation RPC: the status plus the value when <see cref="EngineCallStatus.Ok"/>.</summary>
    public readonly struct EngineCallResult<T>
    {
        public EngineCallResult(EngineCallStatus status, T value, double elapsedMs)
        {
            Status = status;
            Value = value;
            ElapsedMs = elapsedMs;
        }

        public EngineCallStatus Status { get; }
        public T Value { get; }

        /// <summary>Round trip measured by the host (includes IPC), for logs.</summary>
        public double ElapsedMs { get; }

        public bool Ok => Status == EngineCallStatus.Ok && Value != null;

        public static EngineCallResult<T> Success(T value, double ms) => new EngineCallResult<T>(EngineCallStatus.Ok, value, ms);

        public static EngineCallResult<T> Fail(EngineCallStatus status, double ms) => new EngineCallResult<T>(status, default(T), ms);

        public override string ToString() => Status + " (" + ElapsedMs.ToString("F1") + " ms)";
    }

    public static class EngineCallText
    {
        /// <summary>Status bar text for a failed call; null for Ok / Canceled (nothing to tell the user).</summary>
        public static string ForStatus(EngineCallStatus status)
        {
            switch (status)
            {
                case EngineCallStatus.Unavailable: return NavStrings.EngineUnavailable;
                case EngineCallStatus.Timeout: return NavStrings.EngineTimeout;
                case EngineCallStatus.Failed: return NavStrings.EngineFailed;
                default: return null;
            }
        }
    }
}
