using OpenModulePlatform.Web.ContextGuardModule.Models;

namespace OpenModulePlatform.Web.ContextGuardModule.Stores;

public interface IHandoffStore
{
    HandoffCreateResult Create(
        string profileKey,
        string correlationHash,
        IReadOnlyList<ContextGuardField> fields,
        int droppedFieldCount,
        TimeSpan ttl,
        int capacity);

    HandoffRedemption Redeem(string key);

    int EndByCorrelation(string profileKey, string correlationHash);
}
