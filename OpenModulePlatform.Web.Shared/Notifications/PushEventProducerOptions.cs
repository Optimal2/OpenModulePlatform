namespace OpenModulePlatform.Web.Shared.Notifications;

public sealed class PushEventProducerOptions
{
    public const string SectionName = "PushEvents:Producers";

    public bool UseOutboxForNotificationStateChanges { get; set; }
}
