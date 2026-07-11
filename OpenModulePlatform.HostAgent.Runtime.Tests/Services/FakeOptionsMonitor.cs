using Microsoft.Extensions.Options;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class FakeOptionsMonitor<T> : IOptionsMonitor<T>
    where T : class, new()
{
    public T CurrentValue { get; set; } = new();

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => new NullDisposable();

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
