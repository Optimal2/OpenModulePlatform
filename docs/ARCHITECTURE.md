# Architecture

## Översikt

OpenModulePlatform är uppbyggt kring en enkel men tydlig kärna:

1. **Definitioner** beskriver vad som kan finnas.
2. **Instanser** beskriver vad som faktiskt finns i en OMP-instans.
3. **Runtime** beskriver vad som körs, var det körs och hur det observeras.
4. **Automation/topologi** beskriver framtida desired state och hur en HostAgent senare kan materialisera den.

Det viktigaste arkitekturvalet i nuvarande kodbas är att `AppInstance`
används som runtimecentrum i stället för att låta `App` eller `Module`
bära instansansvar.

## Lösningens delar

### OpenModulePlatform.Portal

Portal är både startyta och administrativt gränssnitt.

Den gör tre huvudjobb:

- visar tillgängliga webbappar via en instansbaserad appkatalog
- tillhandahåller manuell administration av kärnmodellen
- tillhandahåller RBAC-administration

Portalens katalog byggs från `omp.AppInstances`, inte från `omp.Apps`.
Det är viktigt eftersom route, hostkoppling och artifactval hör hemma
på instansnivå.

### OpenModulePlatform.Web.Shared

Det delade webbprojektet samlar:

- hosting-defaults
- auth / forwarded headers
- RBAC-upplösning
- gemensamma bas-klasser för Razor Pages
- SQL-anslutningsfabrik för webbappar

Det gör att Portal och modulernas webbgränssnitt följer samma grundmönster.

### ExampleWebAppModule

Detta är det enklaste referensexemplet:

- en moduldefinition
- en webbappdefinition
- modulägd konfiguration
- webb-UI för att visa och ändra moduldatan

Det visar hur en modul kan använda OMP utan någon service/processdel.

### ExampleServiceAppModule

Detta är ett mer komplett referensexempel:

- en webbapp för administration och insyn
- en service/worker-del
- modulägd konfiguration
- jobs-tabell och job processing
- runtimekoppling till `omp.AppInstances`

Det visar hur en modul kan bestå av både webbgränssnitt och service/processdel men ändå använda samma övergripande plattformsmodell.

## Datamodellen idag

### Definitioner

- `omp.Modules`
- `omp.Apps`
- `omp.Artifacts`

Definitioner ska inte bära runtime-specifika värden som route, install path eller hostplacering.

### Instanser

- `omp.Instances`
- `omp.ModuleInstances`
- `omp.AppInstances`
- `omp.Hosts`

Här finns den konkreta miljön. `ModuleInstances` och `AppInstances` är
särskilt viktiga eftersom de gör det möjligt att köra flera instanser av
samma definition.

### Runtime

`omp.Hosts` kan bära en valfri `BaseUrl` för Portalens länkgenerering när en
`AppInstance` använder relativ `RoutePath` på en annan host än Portalen.

`omp.AppInstances` innehåller idag bland annat:

- `HostId`
- `ArtifactId`
- `ConfigId`
- `RoutePath`
- `PublicUrl`
- `InstallPath`
- `InstallationName`
- `DesiredState`
- verifieringspolicy (`ExpectedLogin`, `ExpectedClientHostName`, `ExpectedClientIp`)
- observed state (`LastSeenUtc`, `LastLogin`, `LastClientHostName`, `LastClientIp`, `VerificationStatus`)

Detta gör `AppInstance` till den naturliga runtime-enheten för både webbappar och serviceappar.

### Security

RBAC bygger på fyra core-tabeller:

- `omp.Permissions`
- `omp.Roles`
- `omp.RolePermissions`
- `omp.RolePrincipals`

Portal och modul-UI läser effektiva rättigheter via `RbacService`, som mappar användare och Windows-grupper till rättigheter i databasen.

### Template och deployment

Följande tabeller finns redan:

- `omp.InstanceTemplates`
- `omp.HostTemplates`
- `omp.InstanceTemplateHosts`
- `omp.InstanceTemplateModuleInstances`
- `omp.InstanceTemplateAppInstances`
- `omp.HostDeploymentAssignments`
- `omp.HostDeployments`

Detta visar den tänkta riktningen, men den fulla materialiseringsmodellen är ännu inte färdig.

## Request-flöde i webbapparna

1. Applikationen startar med gemensamma hosting-defaults från `OpenModulePlatform.Web.Shared`.
2. Windows-integrerad auth används om anonym access inte är tillåten.
3. `RbacService` läser användarens effektiva permissions från databasen.
4. Razor Pages bygger vyerna utifrån rättigheter och repository-data.
5. Portalens startsida filtrerar appkatalogen baserat på rättigheter.

## Request-flöde i serviceexemplet

1. Worker startar med ett konfigurerat `AppInstanceId`.
2. `AppInstanceRepository` läser runtime-state från `omp.AppInstances`.
3. Heartbeat uppdaterar observerad state på både `AppInstance` och, när möjligt, `Host`.
4. Config laddas från modulägd tabell med `ConfigId` som brygga.
5. Job processor arbetar endast när appinstansen är aktiv, tillåten och verifierad mot förväntad runtimeidentitet.

## Styrkor i nuvarande arkitektur

- tydlig rörelse bort från sammanblandning av definition och instans
- `AppInstance` fungerar redan som gemensam runtime-modell för både webb och service
- Portal använder instansnivån på riktigt i sin appkatalog
- RBAC är enkel och begriplig
- SQL-skripten är pedagogiska och använder avsiktliga placeholders i stället för att gissa miljöspecifika värden
- exempelprojekten visar både ett enkelt och ett mer avancerat integrationsmönster

## Kända svagheter och begränsningar

- template-modellen finns i schema men är ännu inte fullt operationaliserad
- det saknas ännu tydlig origin-spårning mellan template-rader och materialiserade runtime-rader
- `ConfigId` är funktionell men semantiskt tunn på core-nivå
- Portalens administrativa arbetsflöden är bättre än tidigare men fortfarande tabellcentrerade
- det finns ännu ingen riktig reconcile-/materialiseringsmotor mellan desired topology och faktisk verklighet

## Rekommenderad arkitekturriktning framåt

1. Behåll `AppInstance` som central runtime-enhet.
2. Fullfölj materialiseringsmodellen från template till verkliga `Hosts`, `ModuleInstances` och `AppInstances`.
3. Gör origin/spårbarhet explicit i databasen.
4. Definiera desired state kontra observed state ännu tydligare.
5. Bygg HostAgent först när dessa delar är stabila.
