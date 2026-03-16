# OpenModulePlatform

OpenModulePlatform (OMP) är en generell modulär plattform för att definiera, köra
och administrera OMP-instanser, moduler, appdefinitioner, appinstanser, artifacts,
hosts och topologi.

Repo:t innehåller bara neutral plattformskod och neutrala exempelmoduler.
Det innehåller inte domänspecifika verksamhetsdelar från tidigare interna projekt.

## Innehåll

- `OpenModulePlatform.Portal` - Portal för navigation och manuell administration
- `OpenModulePlatform.Web.Shared` - delad webb-infrastruktur för Portal och webbmoduler
- `OpenModulePlatform.Web.ExampleWebAppModule` - enkel webbmodul som referensexempel
- `OpenModulePlatform.Web.ExampleServiceAppModule` - webbgränssnitt för service-backed exempelmodul
- `OpenModulePlatform.Service.ExampleServiceAppModule` - worker/service-exempel
- `sql/SQL_Install_OpenModulePlatform.sql` - core-schema, RBAC, Portal och bootstrapdata
- `sql/SQL_Install_OpenModulePlatform_Examples.sql` - exempelmoduler, exempelinstanser, template-topologi och sample jobs
- `docs/` - arkitektur, terminologi och handfasta guider

## Nuvarande modell

Nuvarande modell separerar uttryckligen:

- **definitioner** - `Modules`, `Apps`, `Artifacts`
- **konkreta instanser** - `ModuleInstances`, `AppInstances`
- **manuell drift** - `Instances`, `Hosts`, artifacts, appinstanser och RBAC
- **framtida automation/topologi** - `InstanceTemplates`, `HostTemplates`, template-topologitabeller och deploymenttabeller

`omp.AppInstances` är den centrala runtime-tabellen i nuvarande modell. Det är där OMP idag lägger:

- hostplacering
- artifactval
- configreferens
- route/path/url
- desired state
- observed state / heartbeat / verifieringsdata

## Vad som fungerar idag

- Portal kan användas för manuell administration av kärnmodellen
- RBAC kan administreras från Portal
- Portal bygger appkatalogen från `AppInstances`, inte från `Apps`
- Exempelmodulerna visar både rent webbscenario och service-backed scenario
- Service-exemplet läser runtime-state från `AppInstances` och uppdaterar heartbeat/observed identity
- SQL-skripten bootstrappar både core och exempeldata

## Vad som fortfarande är pågående

- template-materialisering är ännu inte fullt genomförd
- HostAgent finns ännu inte
- deploymenttabellerna är mer förberedande än fullt operationaliserade
- configmodellen är fortfarande modulägd och inte fullt formaliserad på core-nivå

## Snabbstart

### 1. Skapa databas

Skapa databasen `OpenModulePlatform` i SQL Server.

### 2. Installera core

Kör:

```sql
sql/SQL_Install_OpenModulePlatform.sql
```

Efter körning måste alla `REPLACE_ME`-värden granskas och ersättas innan Portal eller serviceappar kan användas på riktigt.

### 3. Installera exempel

Kör:

```sql
sql/SQL_Install_OpenModulePlatform_Examples.sql
```

Detta lägger till neutrala exempelmoduler, modulinstanser, appinstanser, template-topologi och sample jobs.

### 4. Konfigurera Portal

Sätt `ConnectionStrings:OmpDb` för Portal och starta `OpenModulePlatform.Portal`.

Portal är den primära vägen för manuell administration. Normal drift ska inte kräva direkt SQL-editing efter initial bootstrap.

## Manuell administration kontra automation

I nuvarande läge är Portal uppdelad i två spår:

- **Core/manual administration** - Instances, Hosts, Modules, ModuleInstances, Apps, Artifacts, AppInstances och RBAC
- **Advanced automation** - templates, deployment assignments och deployments

Tanken är att vanlig manuell installation ska kunna göras utan att användaren behöver arbeta med den framtida HostAgent-/template-modellen.

## Dokumentation

- [Architecture](docs/ARCHITECTURE.md)
- [Terminology](docs/TERMINOLOGY.md)
- [Manual admin configuration](docs/ADMIN_CONFIGURATION.md)
- [Current project status](docs/PROJECT_STATUS.md)

## Licens

Projektet publiceras under MIT-licens.
