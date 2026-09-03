// File: OpenModulePlatform.Portal.Tests/Web/OmpRequestCorrelationMiddlewareTests.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using OpenModulePlatform.Web.Shared.Web;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Tests.Web;

/// <summary>
/// Contract tests for UseOmpRequestCorrelation. They host a minimal in-memory
/// pipeline (no database) and pin the inbound-id contract that the private
/// IsValidCorrelationId enforces, observed through the only public surface the
/// middleware exposes: the X-Correlation-ID response header.
/// </summary>
/// <remarks>
/// The middleware honours a caller-supplied X-Correlation-ID only when it is a
/// short, safe token (non-empty after trim, at most 128 characters, drawn from
/// [A-Za-z0-9._-]); otherwise it generates a fresh id so the value that reaches
/// the logs is never attacker-controlled beyond that bounded shape. A generated
/// id is a 32-character lowercase-hex GUID ("N" format). Every comparison here
/// is ordinal (culture-insensitive): the id is a wire token, not natural-language
/// text, so casing and digit shapes must be matched byte-for-byte.
/// </remarks>
public sealed class OmpRequestCorrelationMiddlewareTests
{
    // Anchored, culture-invariant: the generated fallback is exactly 32 lowercase
    // hex characters. Any honoured caller value that is not itself 32-hex is
    // therefore distinguishable from a freshly generated one.
    private static readonly Regex GeneratedIdShape =
        new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private static async Task<(WebApplication app, HttpClient client)> StartAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseOmpRequestCorrelation();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<string> ResolvedIdForAsync(string? inboundHeader)
    {
        var (app, client) = await StartAppAsync();
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (inboundHeader is not null)
        {
            // TryAddWithoutValidation keeps the raw bytes intact so the
            // middleware's own allowlist -- not HttpClient's header validation --
            // decides the outcome.
            request.Headers.TryAddWithoutValidation(
                OmpRequestCorrelationMiddleware.HeaderName, inboundHeader);
        }

        var response = await client.SendAsync(request);

        return Assert.Single(
            response.Headers.GetValues(OmpRequestCorrelationMiddleware.HeaderName));
    }

    [Fact]
    public async Task NoInboundHeader_GeneratesHexGuid()
    {
        var id = await ResolvedIdForAsync(null);

        Assert.Matches(GeneratedIdShape, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankInbound_FallsBackToGeneratedId(string inbound)
    {
        // Empty or whitespace-only is rejected by IsNullOrWhiteSpace, so the
        // resolved id is a fresh GUID regardless of how the transport trims it.
        var id = await ResolvedIdForAsync(inbound);

        Assert.Matches(GeneratedIdShape, id);
    }

    [Fact]
    public async Task OverlongInbound_FallsBackToGeneratedId()
    {
        // 129 characters, every one from the allowlist: only the length is wrong,
        // so the > MaxLength (128) guard is the sole reason it is rejected.
        var tooLong = new string('a', 129);

        var id = await ResolvedIdForAsync(tooLong);

        Assert.NotEqual(tooLong, id);
        Assert.Matches(GeneratedIdShape, id);
    }

    [Fact]
    public async Task MaxLengthInbound_IsHonoredVerbatim()
    {
        // Exactly 128 characters (the inclusive upper bound) is still accepted and
        // echoed back unchanged, pinning the boundary as <= MaxLength.
        var atLimit = new string('a', 128);

        var id = await ResolvedIdForAsync(atLimit);

        Assert.Equal(atLimit, id);
    }

    [Theory]
    [InlineData("bad value")]       // space
    [InlineData("abc/def")]         // path separator
    [InlineData("id:with:colons")]  // colon
    [InlineData("id@host")]         // at-sign
    [InlineData("id#frag")]         // hash
    public async Task InboundWithDisallowedCharacter_FallsBackToGeneratedId(string inbound)
    {
        var id = await ResolvedIdForAsync(inbound);

        Assert.NotEqual(inbound, id);
        Assert.Matches(GeneratedIdShape, id);
    }

    [Theory]
    [InlineData("abcDEF0123")]
    [InlineData("a.b_c-d.9")]
    [InlineData("A")]
    public async Task InboundOfAllowedShape_IsHonoredVerbatim(string inbound)
    {
        // Letters (both cases), digits, and the three punctuation marks in the
        // allowlist are accepted and returned byte-for-byte.
        var id = await ResolvedIdForAsync(inbound);

        Assert.Equal(inbound, id);
    }

    [Fact]
    public async Task HonoredInbound_IsNotReplacedByGeneratedShape()
    {
        // A caller value that looks nothing like the 32-hex fallback is echoed
        // exactly, proving the honoured path returns the caller token rather than
        // silently generating a new id.
        const string caller = "upstream-service.42_ABC";

        var id = await ResolvedIdForAsync(caller);

        Assert.Equal(caller, id);
        Assert.DoesNotMatch(GeneratedIdShape, id);
    }
}
