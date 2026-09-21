// File: OpenModulePlatform.Portal.Tests/Services/MessageServiceGroupTests.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;
using Xunit;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Editing a message and changing a group's membership, against a real SQL
/// Server: the rules live in the SQL (who may edit, who may remove, who becomes
/// admin), so they are proven there rather than mocked. The push publisher is
/// recorded, not sent.
/// </summary>
public sealed class MessageServiceGroupTests : IClassFixture<MessageServiceTestFixture>
{
    private readonly MessageServiceTestFixture _fixture;

    public MessageServiceGroupTests(MessageServiceTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Only_the_sender_edits_and_the_edit_is_marked_and_pushed()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Edit test", CancellationToken.None);
        var messageId = await service.SendMessageAsync(anna, conversationId, "first draft", [], CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EditMessageAsync(bertil, messageId, "not mine", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EditMessageAsync(anna, messageId, "   ", CancellationToken.None));

        var edited = await service.EditMessageAsync(anna, messageId, "  second draft ", CancellationToken.None);

        Assert.Equal(conversationId, edited);
        var row = Assert.Single(await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None));
        Assert.Equal("second draft", row.Content);
        Assert.NotNull(row.EditedAt);
        Assert.False(row.IsSystem);
        Assert.Equal(2, pushes.Count(p => p.PayloadJson!.Contains("\"action\":\"edited\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Only_the_sender_deletes_and_a_placeholder_without_text_or_files_remains()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Delete test", CancellationToken.None);
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }), 0, 6, "Attachments", "note.txt")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = "text/plain"
        };
        var messageId = await service.SendMessageAsync(anna, conversationId, "take this back", [file], CancellationToken.None);
        var keptId = await service.SendMessageAsync(bertil, conversationId, "still here", [], CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteMessageAsync(bertil, messageId, CancellationToken.None));
        Assert.Equal(1, await _fixture.CountAttachmentsAsync(messageId));

        var deleted = await service.DeleteMessageAsync(anna, messageId, CancellationToken.None);

        Assert.Equal(conversationId, deleted);
        Assert.Equal(0, await _fixture.CountAttachmentsAsync(messageId));
        var rows = await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        var placeholder = rows.Single(row => row.MessageId == messageId);
        Assert.True(placeholder.IsDeleted);
        Assert.Null(placeholder.Content);
        Assert.Empty(placeholder.Attachments);
        Assert.False(placeholder.CanEdit);
        Assert.False(placeholder.CanDelete);
        Assert.Equal("still here", rows.Single(row => row.MessageId == keptId).Content);
        Assert.Equal(2, pushes.Count(p => p.PayloadJson!.Contains("\"action\":\"deleted\"", StringComparison.Ordinal)));

        // Once is enough: a second delete, or an edit of the placeholder, is refused.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteMessageAsync(anna, messageId, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EditMessageAsync(anna, messageId, "resurrected", CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_creator_removes_others_and_the_group_hears_about_it()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil, cecilia], "Remove test", CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveParticipantAsync(bertil, conversationId, cecilia, CancellationToken.None));

        var removal = await service.RemoveParticipantAsync(anna, conversationId, cecilia, CancellationToken.None);

        Assert.False(removal.WasLeaving);
        Assert.Equal("Cecilia", removal.RemovedDisplayName);
        Assert.Null(removal.NewAdminUserId);
        Assert.Null(await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None));
        var detail = await service.GetConversationAsync(bertil, conversationId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal([anna, bertil], detail!.Participants.Select(p => p.UserId));
        Assert.True(detail.Participants.Single(p => p.UserId == anna).IsAdmin);
        var line = Assert.Single(await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None));
        Assert.True(line.IsSystem);
        Assert.Equal("Anna", line.SenderDisplayName);
        Assert.Equal("removed Cecilia from the group", line.Content);
        // Everyone still in the group and the one who was removed get a push.
        Assert.Equal(3, pushes.Count(p => p.PayloadJson!.Contains("\"action\":\"membership\"", StringComparison.Ordinal)));

        // The removed member cannot be removed twice, and cannot remove anyone.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveParticipantAsync(anna, conversationId, cecilia, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveParticipantAsync(cecilia, conversationId, bertil, CancellationToken.None));
    }

    [Fact]
    public async Task When_the_creator_leaves_the_earliest_joiner_becomes_admin()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil, cecilia], "Leave test", CancellationToken.None);

        var removal = await service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None);

        Assert.True(removal.WasLeaving);
        Assert.Equal(bertil, removal.NewAdminUserId);
        Assert.Equal("Bertil", removal.NewAdminDisplayName);
        Assert.Null(await service.GetConversationAsync(anna, conversationId, CancellationToken.None));
        var detail = await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(bertil, detail!.CreatedByUserId);
        Assert.Equal([bertil, cecilia], detail.Participants.Select(p => p.UserId));
        Assert.True(detail.Participants.Single(p => p.UserId == bertil).IsAdmin);
        var lines = await service.GetMessagesAsync(cecilia, conversationId, 10, null, CancellationToken.None);
        Assert.Equal(["Anna left the group", "Bertil is now the group admin"], lines.Select(l => $"{l.SenderDisplayName} {l.Content}"));

        // The new admin may now remove; the last one out takes the conversation
        // with them: no rows remain, in any of the tables.
        await service.RemoveParticipantAsync(bertil, conversationId, cecilia, CancellationToken.None);
        var last = await service.RemoveParticipantAsync(bertil, conversationId, bertil, CancellationToken.None);
        Assert.True(last.ConversationDeleted);
        Assert.Null(last.NewAdminUserId);
        Assert.Equal(0, await _fixture.CountRowsAsync("omp.conversations", conversationId));
        Assert.Equal(0, await _fixture.CountRowsAsync("omp.messages", conversationId));
        Assert.Equal(0, await _fixture.CountRowsAsync("omp.conversation_participants", conversationId));
        Assert.Null(await service.GetConversationAsync(bertil, conversationId, CancellationToken.None));
    }

    [Fact]
    public async Task The_last_member_leaving_deletes_the_attachments_too()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Attachment test", CancellationToken.None);
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }), 0, 6, "Attachments", "note.txt")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = "text/plain"
        };
        var messageId = await service.SendMessageAsync(anna, conversationId, "with a file", [file], CancellationToken.None);
        Assert.Equal(1, await _fixture.CountAttachmentsAsync(messageId));

        await service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None);
        var last = await service.RemoveParticipantAsync(bertil, conversationId, bertil, CancellationToken.None);

        Assert.True(last.ConversationDeleted);
        Assert.Equal(0, await _fixture.CountAttachmentsAsync(messageId));
    }

    [Fact]
    public async Task Only_the_creator_renames_the_group_and_the_group_hears_about_it()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Old name", CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameGroupAsync(bertil, conversationId, "Not mine", CancellationToken.None));

        var rename = await service.RenameGroupAsync(anna, conversationId, "  New name ", CancellationToken.None);
        Assert.Equal("New name", rename.Title);
        Assert.True(rename.Changed);

        var detail = await service.GetConversationAsync(bertil, conversationId, CancellationToken.None);
        Assert.Equal("New name", detail!.Title);
        Assert.Equal("New name", detail.DisplayTitle);
        var line = Assert.Single(await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None));
        Assert.True(line.IsSystem);
        Assert.Equal(anna, line.SenderUserId);
        Assert.Equal("renamed the group to \"New name\"", line.Content);
        Assert.Equal(2, pushes.Count(p => p.PayloadJson!.Contains("\"action\":\"membership\"", StringComparison.Ordinal)));

        // Saving the name it already has is a no-op: no line, no push.
        pushes.Clear();
        var same = await service.RenameGroupAsync(anna, conversationId, "New name", CancellationToken.None);
        Assert.Equal("New name", same.Title);
        Assert.False(same.Changed);
        Assert.Single(await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None));
        Assert.Empty(pushes);

        // An empty name takes the name away; the group is shown by its members again.
        var removed = await service.RenameGroupAsync(anna, conversationId, "   ", CancellationToken.None);
        Assert.Null(removed.Title);
        Assert.True(removed.Changed);
        detail = await service.GetConversationAsync(bertil, conversationId, CancellationToken.None);
        Assert.Null(detail!.Title);
        Assert.Contains("Anna", detail.DisplayTitle, StringComparison.Ordinal);

        // Removing the name of a nameless group is a no-op too.
        pushes.Clear();
        Assert.False((await service.RenameGroupAsync(anna, conversationId, null, CancellationToken.None)).Changed);
        Assert.Equal(2, (await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None)).Count);
        Assert.Empty(pushes);
    }

    [Fact]
    public async Task Only_the_creator_adds_participants_and_a_returning_member_starts_at_the_end()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Add test", CancellationToken.None);
        await service.SendMessageAsync(anna, conversationId, "before Cecilia", [], CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AddParticipantsAsync(bertil, conversationId, [cecilia], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddParticipantsAsync(anna, conversationId, [], CancellationToken.None));

        // Bertil is already in: only Cecilia joins, and only she is announced.
        var added = await service.AddParticipantsAsync(anna, conversationId, [cecilia, bertil], CancellationToken.None);
        var newcomer = Assert.Single(added);
        Assert.Equal(cecilia, newcomer.UserId);
        Assert.Equal("Cecilia", newcomer.DisplayName);
        Assert.False(newcomer.IsAdmin);

        var detail = await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None);
        Assert.Equal(3, detail!.Participants.Count);
        var rows = await service.GetMessagesAsync(cecilia, conversationId, 10, null, CancellationToken.None);
        Assert.Equal("added Cecilia to the group", Assert.Single(rows, row => row.IsSystem).Content);
        // The message from before she joined is not unread for her; the line announcing her is.
        Assert.Equal(1, await service.GetUnreadMessageCountAsync(cecilia, CancellationToken.None));
        Assert.Equal(
            new int?[] { anna, bertil, cecilia }.Order(),
            pushes.Where(p => p.PayloadJson!.Contains("\"action\":\"membership\"", StringComparison.Ordinal)).Select(p => p.TargetUserId).Order());

        // Leaving and being added again reopens her row rather than adding a
        // second one, and again she starts at the end: what was said while she
        // was out is not unread, only the line announcing her return.
        await service.MarkConversationReadAsync(cecilia, conversationId, CancellationToken.None);
        await service.RemoveParticipantAsync(cecilia, conversationId, cecilia, CancellationToken.None);
        await service.SendMessageAsync(anna, conversationId, "while Cecilia was out", [], CancellationToken.None);
        Assert.Single(await service.AddParticipantsAsync(anna, conversationId, [cecilia], CancellationToken.None));
        detail = await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None);
        Assert.Equal(3, detail!.Participants.Count);
        Assert.Single(detail.Participants, participant => participant.UserId == cecilia);
        Assert.Equal(1, await service.GetUnreadMessageCountAsync(cecilia, CancellationToken.None));

        // Adding only members already in the group changes nothing.
        Assert.Empty(await service.AddParticipantsAsync(anna, conversationId, [cecilia, bertil], CancellationToken.None));
    }

    [Fact]
    public async Task When_the_creator_leaves_a_disabled_member_is_passed_over_for_the_seat()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil, cecilia], "Succession test", CancellationToken.None);
        await _fixture.SetAccountStatusAsync(bertil, 0);

        var removal = await service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None);

        Assert.Equal(cecilia, removal.NewAdminUserId);
        var detail = await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None);
        Assert.Equal(cecilia, detail!.CreatedByUserId);

        // With nobody active left to take the seat, the group keeps none: the
        // row stays and nobody is announced as admin.
        await _fixture.SetAccountStatusAsync(cecilia, 1);
        var last = await service.RemoveParticipantAsync(cecilia, conversationId, cecilia, CancellationToken.None);
        Assert.Null(last.NewAdminUserId);
        Assert.False(last.ConversationDeleted);
        await _fixture.SetAccountStatusAsync(bertil, 1);
        detail = await service.GetConversationAsync(bertil, conversationId, CancellationToken.None);
        Assert.Equal(cecilia, detail!.CreatedByUserId);
        Assert.DoesNotContain(detail.Participants, participant => participant.IsAdmin);
    }

    [Fact]
    public async Task The_creator_hands_the_admin_seat_to_another_member()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var outsider = await _fixture.InsertUserAsync("David");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil, cecilia], "Transfer test", CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TransferGroupAdminAsync(bertil, conversationId, cecilia, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferGroupAdminAsync(anna, conversationId, outsider, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferGroupAdminAsync(anna, conversationId, anna, CancellationToken.None));

        // A disabled member could neither use the seat nor give it back.
        await _fixture.SetAccountStatusAsync(cecilia, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransferGroupAdminAsync(anna, conversationId, cecilia, CancellationToken.None));
        await _fixture.SetAccountStatusAsync(cecilia, 1);

        Assert.Equal("Bertil", await service.TransferGroupAdminAsync(anna, conversationId, bertil, CancellationToken.None));

        var detail = await service.GetConversationAsync(anna, conversationId, CancellationToken.None);
        Assert.Equal(bertil, detail!.CreatedByUserId);
        Assert.True(detail.Participants.Single(participant => participant.UserId == bertil).IsAdmin);
        Assert.False(detail.Participants.Single(participant => participant.UserId == anna).IsAdmin);
        var line = Assert.Single(await service.GetMessagesAsync(anna, conversationId, 10, null, CancellationToken.None));
        Assert.True(line.IsSystem);
        Assert.Equal(bertil, line.SenderUserId);
        Assert.Equal("is now the group admin", line.Content);

        // The seat went with the rights: Anna can no longer remove others, Bertil can.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveParticipantAsync(anna, conversationId, cecilia, CancellationToken.None));
        var removal = await service.RemoveParticipantAsync(bertil, conversationId, cecilia, CancellationToken.None);
        Assert.Equal(cecilia, removal.RemovedUserId);
    }

    [Fact]
    public async Task A_direct_conversation_has_no_one_to_remove()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.GetOrCreateDirectConversationAsync(anna, bertil, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None));
    }
}

public sealed class MessageServiceTestFixture : IAsyncLifetime
{
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("Messages");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public (MessageService Service, List<PushEvent> Pushes) CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = ConnectionString })
            .Build();
        var db = new SqlConnectionFactory(configuration);
        var pushes = new List<PushEvent>();
        var service = new MessageService(
            db,
            new OmpConfigurationService(db, new MemoryCache(new MemoryCacheOptions()), NullLogger<OmpConfigurationService>.Instance),
            new RecordingPushEventPublisher(pushes),
            NullLogger<MessageService>.Instance);
        return (service, pushes);
    }

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public async Task<int> CountRowsAsync(string table, long conversationId)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(1) FROM {table} WHERE conversation_id = @id;", conn);
        cmd.Parameters.AddWithValue("@id", conversationId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> CountAttachmentsAsync(long messageId)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(1) FROM omp.message_attachments WHERE message_id = @id;", conn);
        cmd.Parameters.AddWithValue("@id", messageId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task SetAccountStatusAsync(int userId, int accountStatus)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("UPDATE omp.users SET account_status = @status WHERE user_id = @id;", conn);
        cmd.Parameters.AddWithValue("@status", accountStatus);
        cmd.Parameters.AddWithValue("@id", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> InsertUserAsync(string displayName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("INSERT INTO omp.users (display_name) VALUES (@name); SELECT CAST(SCOPE_IDENTITY() AS int);", conn);
        cmd.Parameters.AddWithValue("@name", displayName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            // A failed drop must not fail the run, but it must not be silent either:
            // the shared log is what CI turns into a warning about the leaked database.
            OmpTestCleanupLog.RecordFailure(
                nameof(MessageServiceGroupTests),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    private sealed class RecordingPushEventPublisher(List<PushEvent> pushes) : IPushEventPublisher
    {
        public Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct)
        {
            pushes.Add(pushEvent);
            return Task.FromResult((long)pushes.Count);
        }
    }
}
