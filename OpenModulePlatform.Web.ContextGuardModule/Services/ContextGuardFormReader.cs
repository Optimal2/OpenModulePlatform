using Microsoft.AspNetCore.Http.Features;
using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;

namespace OpenModulePlatform.Web.ContextGuardModule.Services;

public enum FormReadStatus
{
    Success,
    UnsupportedMediaType,
    TooLarge,
    TooManyFields,
    FilesNotAllowed,
    InvalidForm
}

public sealed record FormReadResult(
    FormReadStatus Status,
    IReadOnlyList<ContextGuardField> Fields,
    IReadOnlyList<ContextGuardField> AllowedFields,
    int DroppedFieldCount)
{
    public static FormReadResult Failure(FormReadStatus status) => new(status, [], [], 0);
}

public sealed class ContextGuardFormReader
{
    public async Task<FormReadResult> ReadAsync(
        HttpRequest request,
        ContextGuardProfileOptions profile,
        ContextGuardOptions options,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return FormReadResult.Failure(FormReadStatus.UnsupportedMediaType);
        }

        if (request.ContentLength > options.MaxTotalFormBytes)
        {
            return FormReadResult.Failure(FormReadStatus.TooLarge);
        }

        var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = options.MaxTotalFormBytes;
        }

        try
        {
            var form = await request.ReadFormAsync(cancellationToken);
            if (form.Files.Count != 0)
            {
                return FormReadResult.Failure(FormReadStatus.FilesNotAllowed);
            }

            var fields = form.SelectMany(pair => pair.Value.Select(value =>
                new ContextGuardField(pair.Key, value ?? string.Empty))).ToArray();
            if (fields.Length > options.MaxFieldCount)
            {
                return FormReadResult.Failure(FormReadStatus.TooManyFields);
            }

            if (fields.Any(field => field.Value.Length > options.MaxFieldValueLength))
            {
                return FormReadResult.Failure(FormReadStatus.TooLarge);
            }

            var allowedNames = profile.AllowedFieldNames.ToHashSet(StringComparer.Ordinal);
            var allowed = fields.Where(field => allowedNames.Contains(field.Name)).ToArray();
            return new FormReadResult(
                FormReadStatus.Success,
                fields,
                allowed,
                fields.Length - allowed.Length);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
        {
            return FormReadResult.Failure(FormReadStatus.TooLarge);
        }
    }

    public static bool IsOriginAllowed(HttpRequest request, ContextGuardProfileOptions profile)
    {
        if (profile.AllowedOriginHosts.Count == 0)
        {
            return true;
        }

        var allowed = profile.AllowedOriginHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var headerName in new[] { "Origin", "Referer" })
        {
            if (request.Headers.TryGetValue(headerName, out var values)
                && Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out var uri))
            {
                return allowed.Contains(uri.IdnHost);
            }
        }

        return false;
    }
}
