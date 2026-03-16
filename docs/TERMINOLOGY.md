# Terminology

## OpenModulePlatform (OMP)

Paraplybegreppet för hela plattformen.

## OMP Instance

En konkret installation av OMP för en viss miljö, organisation eller deployment-yta.

## Module

En moduldefinition. Beskriver en återanvändbar modul som kan installeras flera gånger.

## Module Instance

En konkret modulinstans i en viss OMP-instans.

## App

En appdefinition som tillhör en moduldefinition. En appdefinition kan representera en webbapp, portalapp eller serviceapp.

## App Instance

Den konkreta runtime-instansen av en app i en viss modulinstans.
En appinstans kan ha eget artifact, egen config, egen hostplacering,
eget route/path/url-värde och egen runtimepolicy.

## Artifact

En deploybar byggprodukt för en appdefinition, till exempel publicerad mapp, zip eller annan paketerad output.

## Host

Ett konkret runtime-target inom en OMP-instans. En host kan bära noll eller flera appinstanser.

## Instance Template

Mall för hur en OMP-instans är tänkt att se ut strukturellt.

## Host Template

Mall för en hostroll inom en instance template.

## Template topology

Samlingsnamn för de tabeller som beskriver önskad struktur i en template:

- `InstanceTemplateHosts`
- `InstanceTemplateModuleInstances`
- `InstanceTemplateAppInstances`

## HostDeploymentAssignment

Koppling mellan en konkret host och en host template.
Detta är en automationsrelaterad del av modellen, inte en nödvändig del
av manuell installation.

## HostDeployment

Representation av ett deploymentförsök eller deployment-tillstånd för en host.

## OMP Portal

Det delade webbgränssnittet för navigation och administration i OMP.

## OMP HostAgent

Framtida valfri automationskomponent som ska kunna läsa desired topology / deployment state och utföra eller verifiera åtgärder på hostar.
