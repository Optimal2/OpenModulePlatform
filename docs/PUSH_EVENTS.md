# Push Events

OMP push events are lightweight wake-up hints that let web apps, service apps,
workers, and module code request UI refreshes without browser polling.

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

Service apps should reference `OpenModulePlatform.EventPublisher.Abstractions`
and `OpenModulePlatform.EventPublisher.Sql`, register `IPushEventPublisher`
with their SQL connection factory, and publish after committing their own state
changes. Service apps do not need to host SignalR.

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

- `topbar.notification-state-changed` - existing topbar notification/message
  refresh hint.
- `topbar.banner-state-changed` - reserved for future banner refresh support.
- `module.state-changed` - general module state refresh hint for module web
  surfaces.

Modules can also use module-specific categories when the consumer and producer
are both module-owned. Keep category names stable and neutral, and keep payloads
small. The event payload is optional JSON and is intended for routing hints, not
large data transfer.

## Banners

Banners are not wired to push yet. The reserved banner category allows the
backend model to stay stable, but live banner refresh requires a later web UI
pass that teaches the topbar client how to re-read and render banner state.

## Service-App And Consumer Module Hook Points

A service-backed module should publish only after its database transaction or
state mutation succeeds. For example, a checker service can update its module
tables, then publish `module.state-changed` to `broadcast` or `authenticated`
users so the module web app knows to refresh its dashboard.

Consumer modules adopt this by adding references and DI registration in their
own repositories. OpenModulePlatform stays customer-neutral; it provides the
contract, outbox writer, and web/worker host boundaries.

## Dispatcher And Browser Protocol

Portal can host the outbox dispatcher by calling `AddOmpPushEventDispatcher`
and setting `PushEvents:Dispatcher:Enabled` to `true`. Shared web defaults do
not register the dispatcher automatically, so module web apps that call
`AddOmpWebDefaults` do not compete for leases unless they explicitly opt in.

The dispatcher leases pending rows atomically from `omp.push_event_outbox`,
ordered by `push_event_id`, sends a lightweight SignalR envelope on
`/topbar/notifications/updates` with method `notificationStateChanged`, and
then marks the row `dispatched`. Failed dispatches are retried with a scheduled
delay until `max_retries` is exceeded, after which the row is marked
`dead-lettered`.

The browser treats push as a wake-up hint. The envelope contains `eventId`,
`deduplicationKey`, `category`, `targetKind`, `targetValue`, and optional
`payload`, but the topbar still re-reads state through its normal summary
endpoint. The client also still handles the legacy no-argument
`notificationStateChanged` signal by refreshing the summary.

## Multi-Node Delivery Boundary

SignalR is registered without a Redis or other backplane dependency. In a
multi-node IIS farm, enable sticky sessions for push mode or add a SignalR
backplane in a later deployment design. The SQL outbox keeps events durable, but
without sticky sessions or a backplane a browser connected to one node may not
receive a SignalR send performed by another node.
