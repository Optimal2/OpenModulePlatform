// File: OpenModulePlatform.Auth/Services/IOmpLocalPasswordHasher.cs
namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// Abstraction over the shared local-password hasher so the auth flows can be
/// tested with a counting or fake hasher. R7-F15 uses this to prove that hash
/// verification runs even when the account does not exist.
/// </summary>
public interface IOmpLocalPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string storedHash);
}
