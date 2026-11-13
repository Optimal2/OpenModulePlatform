using OpenModulePlatform.Web.ContextGuardModule.Models;

namespace OpenModulePlatform.Web.ContextGuardModule.Stores;

public interface IContextStore
{
    GuardContext Create(
        int userId,
        string clientId,
        string profileKey,
        string correlationHash,
        string scope,
        TimeSpan heartbeatTimeout);

    ContextStateResult Heartbeat(Guid contextId, int userId, TimeSpan heartbeatTimeout);

    ContextStateResult GetState(Guid contextId, int userId, TimeSpan heartbeatTimeout);

    int EndByCorrelation(string profileKey, string correlationHash);
}
