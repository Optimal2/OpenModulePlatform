# Push Events

OMP push events are lightweight wake-up hints that let web apps, service apps,
workers, and module code request UI refreshes with less browser polling.

Push events are not the source of truth. Module state remains in module-owned
tables or shared OMP tables. A push event only tells a web surface that some
state category changed and should be re-read through its normal API or page
model.

## Shared Contract

The shared event contract lives in `OpenModulePlatform.EventPublisher.Abstractions`:

- `PushEvent` is the normalized envelope.
- `IPushEventPublisher` records a push event.
- `PushTargetKind` defines `user`, `role`, `broadcast`, `authenticated`,
  `app`, and `module` targets.
- `PushEventCategory` defines common platform categories.

The SQL implementation lives in `OpenModulePlatform.EventPublisher.Sql`. It
writes to `omp.push_event_outbox` and does not depend on ASP.NET Core, SignalR,
or `OpenModulePlatform.Web.Shared`.

## Publishing Model

Web apps should use the `IPushEventPublisher` service registered by
`AddOmpWebDefaults`. The SQL publisher stores events durably in
`omp.push_event_outbox`; it does not deliver browser messages by itself.

Platform producers that currently write push events:

- `NotificationService` publishes `topbar.notification-state-changed` for
  notification create/read/read-all mutations when
  `PushEvents:Producers:UseOutboxForNotificationStateChanges` is `true`.
  When the flag is `false`, the same service uses the legacy direct SignalR
  path instead. The migration wrapper calls exactly one path per event.
- `MessageService` publishes `topbar.message-state-changed` after message
  sends and read-state updates.
- `BannerService` publishes `topbar.banner-state-changed` after global or
  role-targeted banner create/update/disable mutations.

Service apps should reference `OpenModulePlatform.EventPublisher.Abstractions`
and `OpenModulePlatform.EventPublisher.Sql`, register `IPushEventPublisher`
with their SQL connection factory, and publish after committing their own state
changes. Service apps do not need to host SignalR.

Do not expose an HTTP publish endpoint unless the deployment has a clear
service-to-service authentication model. The supported local OMP path is the
trusted SQL outbox writer above.

Workers should reference `OpenModulePlatform.EventPublisher.Abstractions` from
worker plugins when they need the contract. `OpenModulePlatform.WorkerProcessHost`
shares the abstraction assembly with plugins so the host and plugin agree on
the same contract type. A worker host or plugin can later register a SQL-backed
publisher when the worker scenario needs it.

HostAgent, WorkerManager, and other headless processes should use the same
abstraction when they need to request UI refreshes. They should not reference
the web shared project only to publish events.

## Categories

Platform categories currently include:

- `topbar.notification-state-changed` - topbar notification refresh hint.
- `topbar.message-state-changed` - topbar message refresh hint.
- `topbar.banner-state-changed` - topbar banner refresh hint.
- `module.state-changed` - general module state refresh hint for module web
  surfaces.
- `module.specific` - module-owned event category for module-owned consumers.

Modules can also use module-specific categories when the consumer and producer
are both module-owned. Keep category names stable and neutral, and keep payloads
small. The event payload is optional JSON and is intended for routing hints, not
large data transfer.

## Service-App And Consumer Module Hook Points

A service-backed module should publish only after its database transaction or
state mutation succeeds. For example, a checker service can update its module
tables, then publish `module.state-changed` to `broadcast` or `authenticated`
users so the module web app knows to refresh its dashboard.

Consumer modules adopt this by adding references and DI registration in their
own repositories. OpenModulePlatform stays customer-neutral; it provides the
contract, outbox writer, and web/worker host boundaries.

Minimal service-app registration pattern:

```csharp
services.AddSingleton<IPushEventPublisher>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SqlPushEventPublisher>>();
    return new SqlPushEventPublisher(CreateSqlConnection, logger);
});
```

The service app can then publish:

```csharp
await publisher.PublishAsync(
    PushEvent.ForAuthenticatedUsers(PushEventCategory.ModuleStateChanged),
    ct);
```

The service app writes only an outbox row. It does not host SignalR, map hubs,
or send browser messages directly.

## Direct Database Writes

Direct inserts or updates to `omp.notifications`, `omp.messages`,
`omp.banners`, or module-owned state tables do not automatically produce push
events. SQL triggers are intentionally not part of the push model: they would
couple persistence to browser delivery, make service-app behavior harder to
test, and still could not send SignalR messages from SQL Server.

Every writer that wants push behavior must call `IPushEventPublisher` after its
state change succeeds. If a maintenance script or emergency SQL statement
modifies state directly, clients will see the change through page reloads or
fallback polling only.

## Dispatcher And Browser Protocol

Portal owns dispatch in the default OMP deployment. It hosts the outbox
dispatcher by calling `AddOmpPushEventDispatcher` and setting
`PushEvents:Dispatcher:Enabled` to `true`. Shared web defaults do not register
the dispatcher automatically, so module web apps that call `AddOmpWebDefaults`
do not compete for leases unless they explicitly opt in.

The dispatcher leases pending rows atomically from `omp.push_event_outbox`,
ordered by `push_event_id`, sends a lightweight SignalR envelope with method
`pushEvent`, and then marks the row `dispatched`. The same authorized hub is
available at `/push/events` for neutral module-owned consumers and at the
legacy `/topbar/notifications/updates` path used by the shared topbar.
Failed dispatches are retried with a scheduled delay until `max_retries` is
exceeded, after which the row is marked `dead-lettered`.

When the dispatcher is enabled, it also performs bounded retention cleanup for
terminal outbox rows. By default, dispatched rows are retained for 7 days,
failed or dead-lettered rows are retained for 30 days, and each cleanup pass
deletes at most 500 rows every 15 minutes. These values can be adjusted under
`PushEvents:Dispatcher` with `CleanupEnabled`, `CleanupIntervalMinutes`,
`CleanupBatchSize`, `DispatchedRetentionDays`, and `FailedRetentionDays`.
Cleanup never deletes `pending` or `processing` rows.

Authenticated browser clients connected to `TopBarNotificationHub` receive
SignalR messages. On connect, the hub joins per-user, effective-role,
broadcast, authenticated, app, and module groups. The dispatcher maps outbox
targets to those groups.

The browser treats push as a wake-up hint. The envelope contains `eventId`,
`deduplicationKey`, `category`, `targetKind`, `targetValue`, and optional
`payload`. Module-owned clients should subscribe to `pushEvent` and filter by
their own category and payload contract. OMP only owns the neutral envelope and
target groups.

For topbar notification and message categories, the dispatcher also sends the
legacy `notificationStateChanged` method so existing topbar clients refresh
their normal summary endpoint. The topbar client deduplicates recent push
envelopes and still handles the legacy no-argument `notificationStateChanged`
signal by refreshing the summary. Banner and module events are delivered as
generic `pushEvent` envelopes only; live banner repainting remains future UI
work.

Fallback polling remains part of the design. If push mode is disabled,
unavailable, disconnected, or fails to start, the topbar falls back to polling
`/topbar/summary`. Push is an optimization for faster refresh, not the only
path to correctness.

## Multi-Node Delivery Boundary

SignalR is registered without a Redis or other backplane dependency. In a
multi-node IIS farm, enable sticky sessions for push mode or add a SignalR
backplane in a later deployment design. The SQL outbox keeps events durable, but
without sticky sessions or a backplane a browser connected to one node may not
receive a SignalR send performed by another node.
