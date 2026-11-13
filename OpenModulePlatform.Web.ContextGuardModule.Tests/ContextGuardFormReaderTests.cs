using System.Text;
using Microsoft.AspNetCore.Http;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Services;

namespace OpenModulePlatform.Web.ContextGuardModule.Tests;

public sealed class ContextGuardFormReaderTests
{
    [Fact]
    public async Task ReadAsync_WithUnknownFields_DropsAndCountsThem()
    {
        var context = FormContext("allowed=one&correlation=case-1&unknown=secret");
        var profile = Profile();

        var result = await new ContextGuardFormReader().ReadAsync(
            context.Request, profile, new ContextGuardOptions(), CancellationToken.None);

        Assert.Equal(FormReadStatus.Success, result.Status);
        Assert.Equal(1, result.DroppedFieldCount);
        Assert.Equal(2, result.AllowedFields.Count);
        Assert.DoesNotContain(result.AllowedFields, field => field.Name == "unknown");
    }

    [Fact]
    public async Task ReadAsync_WithMultipartFile_RejectsRequest()
    {
        var context = new DefaultHttpContext();
        var boundary = "test-boundary";
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\nContent-Type: text/plain\r\n\r\na\r\n--{boundary}--\r\n"));

        var result = await new ContextGuardFormReader().ReadAsync(
            context.Request, Profile(), new ContextGuardOptions(), CancellationToken.None);

        Assert.Equal(FormReadStatus.FilesNotAllowed, result.Status);
    }

    [Fact]
    public async Task ReadAsync_AboveFieldCount_RejectsRequest()
    {
        var context = FormContext("allowed=one&correlation=two");
        var options = new ContextGuardOptions { MaxFieldCount = 1 };

        var result = await new ContextGuardFormReader().ReadAsync(
            context.Request, Profile(), options, CancellationToken.None);

        Assert.Equal(FormReadStatus.TooManyFields, result.Status);
    }

    [Theory]
    [InlineData("https://allowed.example/path", true)]
    [InlineData("https://other.example/path", false)]
    [InlineData(null, false)]
    public void IsOriginAllowed_WithConfiguredHosts_UsesOriginHost(string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        if (origin is not null) context.Request.Headers.Origin = origin;
        var profile = Profile();
        profile.AllowedOriginHosts.Add("allowed.example");

        Assert.Equal(expected, ContextGuardFormReader.IsOriginAllowed(context.Request, profile));
    }

    [Fact]
    public void IsOriginAllowed_WithEmptyList_AcceptsAnywhere()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Origin = "https://any.example";

        Assert.True(ContextGuardFormReader.IsOriginAllowed(context.Request, Profile()));
    }

    private static DefaultHttpContext FormContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context;
    }

    private static ContextGuardProfileOptions Profile() => new()
    {
        TargetUrl = "https://target.example/result",
        AllowedFieldNames = ["allowed", "correlation"],
        CorrelationFieldName = "correlation"
    };
}
