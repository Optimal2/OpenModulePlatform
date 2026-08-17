// File: OpenModulePlatform.Auth/Services/OmpLocalPasswordHasher.cs
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// Default <see cref="IOmpLocalPasswordHasher"/> backed by the shared PBKDF2
/// local-password hasher.
/// </summary>
public sealed class OmpLocalPasswordHasher : IOmpLocalPasswordHasher
{
    private readonly LocalPasswordHasher _inner;

    public OmpLocalPasswordHasher(LocalPasswordHasher inner)
    {
        _inner = inner;
    }

    public string Hash(string password)
        => _inner.Hash(password);

    public bool Verify(string password, string storedHash)
        => _inner.Verify(password, storedHash);
}
