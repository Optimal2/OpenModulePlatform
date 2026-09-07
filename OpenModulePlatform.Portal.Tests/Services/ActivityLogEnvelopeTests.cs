// File: OpenModulePlatform.Portal.Tests/Services/ActivityLogEnvelopeTests.cs
using System.Text.Json;
using OpenModulePlatform.Web.Shared.ActivityLog;
using Xunit;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The activity log envelope is the contract between every module's writer and the
/// Portal's viewer. These pin the stored shape (version 1) and the reader's rules:
/// additions within a version are fine, unknown versions fall back to the raw text,
/// and event plus summary are always required.
/// </summary>
public sealed class ActivityLogEnvelopeTests
{
    [Fact]
    public void Serialize_writes_version_one_with_camel_case_and_no_nulls()
    {
        var envelope = new ActivityEnvelope
        {
            Event = "manual_review.approved",
            Module = "ibs_packager",
            App = "ibs-packager-web",
            Outcome = ActivityOutcomes.Ok,
            Summary = "Approved manual review item 4711",
            Subject = new ActivitySubject("manual_review_item", "4711", "sas"),
            Actor = new ActivityActor(ActivityActorKinds.User, "Alfons")
        };

        var json = ActivityLogJson.Serialize(envelope);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("manual_review.approved", root.GetProperty("event").GetString());
        Assert.Equal("4711", root.GetProperty("subject").GetProperty("id").GetString());
        Assert.Equal("user", root.GetProperty("actor").GetProperty("kind").GetString());
        Assert.False(root.TryGetProperty("correlation", out _), "null fields are omitted");
        Assert.False(root.TryGetProperty("data", out _), "null fields are omitted");
    }

    [Fact]
    public void TryParse_round_trips_version_one_and_keeps_data_opaque()
    {
        const string json = """
            {"v":1,"event":"job.requeued","module":"ibs_packager","summary":"Requeued job 9","data":{"reason":"retry","attempt":2}}
            """;

        var envelope = ActivityLogJson.TryParse(json);

        Assert.NotNull(envelope);
        Assert.Equal("job.requeued", envelope!.Event);
        Assert.Equal("Requeued job 9", envelope.Summary);
        Assert.Null(envelope.Subject);
        Assert.Equal(2, envelope.Data!.Value.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public void TryParse_tolerates_fields_added_within_the_version()
    {
        const string json = """{"v":1,"event":"x.y","summary":"s","futureField":{"anything":true}}""";

        Assert.NotNull(ActivityLogJson.TryParse(json));
    }

    [Theory]
    [InlineData("""{"v":2,"event":"x.y","summary":"from the future"}""")]
    [InlineData("""{"v":1,"event":"","summary":"no event"}""")]
    [InlineData("""{"v":1,"event":"x.y"}""")]
    [InlineData("not json at all")]
    public void TryParse_returns_null_for_unknown_versions_and_incomplete_entries(string json)
    {
        Assert.Null(ActivityLogJson.TryParse(json));
    }

    [Fact]
    public void TryReadSummary_reads_the_summary_of_any_version_for_the_list_row()
    {
        Assert.Equal("from the future", ActivityLogJson.TryReadSummary("""{"v":7,"summary":"from the future"}"""));
        Assert.Null(ActivityLogJson.TryReadSummary("""{"v":7}"""));
        Assert.Null(ActivityLogJson.TryReadSummary("broken"));
    }

    [Fact]
    public void CreateTableStatement_is_idempotent_and_refuses_odd_identifiers()
    {
        var ddl = ActivityLogSql.CreateTableStatement("omp_ibs_packager");

        Assert.Contains("IF OBJECT_ID(N'[omp_ibs_packager].[ActivityLog]', N'U') IS NULL", ddl);
        Assert.Contains("CHECK (ISJSON(Entry) = 1)", ddl);
        Assert.Contains("IX_omp_ibs_packager_ActivityLog_OmpUserId", ddl);
        Assert.Throws<ArgumentException>(() => ActivityLogSql.CreateTableStatement("omp; DROP TABLE x"));
        Assert.Equal(
            "INSERT INTO [omp_ibs_packager].[ActivityLog] (LoggedUtc, OmpUserId, Entry) VALUES (SYSUTCDATETIME(), @OmpUserId, @Entry);",
            ActivityLogSql.InsertStatement("omp_ibs_packager"));
    }
}
