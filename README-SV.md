# OpenModulePlatform

OpenModulePlatform (OMP) är en generell modulär plattform för att definiera, köra
och administrera OMP-instanser, moduler, appdefinitioner, appinstanser, artifacts,
hosts och topologi.

Repo:t innehåller bara neutral plattformskod och neutrala exempelmoduler.
Det innehåller inte domänspecifika verksamhetsdelar från tidigare interna projekt.

## Innehåll

- `OpenModulePlatform.Portal` - Portal för navigation och manuell administration
- `OpenModulePlatform.Web.Shared` - delad webb-infrastruktur för Portal och webbmoduler
- `examples/WebAppModule` - enkel webbmodul som referensexempel
- `examples/ServiceAppModule/WebApp` - webbgränssnitt för service-backed exempelmodul
- `examples/ServiceAppModule/ServiceApp` - worker/service-exempel
- `sql/1-setup-openmoduleplatform.sql` och `sql/2-initialize-openmoduleplatform.sql` - neutralt core-schema, RBAC, default-instans, host och bootstrapdata
- `OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql` och `OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql` - Portal-ägt schema och Portal-registrering
- `examples/**/Sql/1-setup-*.sql` och `examples/**/Sql/2-initialize-*.sql` - valfria setup- och initieringsskript för exempelmoduler
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
- SQL-skripten följer en tvåstegsmodell per modul: setup och initiering

## Vad som fortfarande är pågående

- template-materialisering är ännu inte fullt genomförd
- HostAgent och worker-runtime behöver fortsatt härdning
- deploymenttabellerna är mer förberedande än fullt operationaliserade
- configmodellen är fortfarande modulägd och inte fullt formaliserad på core-nivå

## Snabbstart

### 1. Skapa databas

Skapa databasen `OpenModulePlatform` i SQL Server.

### 2. Installera core

Kör OMP:s root-skript i ordning:

```sql
sql/1-setup-openmoduleplatform.sql
sql/2-initialize-openmoduleplatform.sql
```

Innan `2-initialize-openmoduleplatform.sql` körs ska bootstrap-placeholdern `REPLACE_ME\UserOrGroup` ersättas med den lokala Windows-användare eller grupp som ska få initial Portal-adminroll.

### 3. Installera Portal-modulen

Kör Portalens modulägda SQL-skript i ordning:

```sql
OpenModulePlatform.Portal/sql/1-setup-omp-portal.sql
OpenModulePlatform.Portal/sql/2-initialize-omp-portal.sql
```

Innan `2-initialize-omp-portal.sql` körs ska bootstrap-placeholdern `REPLACE_ME\UserOrGroup` ersättas om den fortfarande finns kvar.

### 4. Installera eventuella exempelmoduler

Varje exempelmodul äger sin egen SQL-mapp och följer samma tvåfilsmönster:

```text
examples/<module>/Sql/1-setup-*.sql
examples/<module>/Sql/2-initialize-*.sql
```

Kör bara de exempelmoduler som uttryckligen ska finnas i den lokala miljön.

### 5. Konfigurera Portal

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
