using Anode.Contracts;

namespace Anode.Server;

/// <summary>Log messages of the server's own (source-generated, allocation-light).</summary>
static partial class StartupLog {
    [LoggerMessage(Level = LogLevel.Information, Message = "Start-up: {Phase} after {Ms} ms")]
    public static partial void Phase(ILogger logger, string phase, long ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine host: {State} {Detail}")]
    public static partial void EngineState(ILogger logger, EngineState state, string? detail);
}
