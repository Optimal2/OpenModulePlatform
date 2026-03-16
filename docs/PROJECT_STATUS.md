# Project status

## Sammanfattning

OMP är längre än proof-of-concept men ännu inte färdig som komplett plattform.
Nuvarande repo är bäst beskrivet som ett plattformsskelett med fungerande
referensexempel och en tydlig riktning mot en robust instansmodell.

## Det som är starkt idag

- neutral open source-riktning utan verksamhetsspecifikt innehåll
- konsekvent separation mellan definitioner och instanser i stora delar av modellen
- Portal för manuell administration
- RBAC-administration i Portal
- två neutrala referensmoduler
- serviceexempel som använder `AppInstance` som runtimecentrum
- SQL-skript för både core och exempel

## Det som fortfarande är ofärdigt

- slutlig template-materialisering
- desired topology kontra faktisk verklighet
- tydlig origin-länkning mellan template-objekt och materialiserade objekt
- mer komplett operationalisering av deploymenttabellerna
- eventuellt starkare core-modell för config-konceptet

## Bedömning av modellmognad

### Redan relativt stabilt

- `Modules` som definitioner
- `Apps` som definitioner
- `Artifacts` som byggprodukter
- `ModuleInstances` som konkreta modulinstanser
- `AppInstances` som runtime-instansnivå

### Fortfarande under design

- template-kedjan från `InstanceTemplate` till verkliga rows
- deployment- och assignmentflöden
- HostAgent-kontraktet

## Rekommenderad prioritering

1. Slutför datamodellen kring topology/materialisering.
2. Tydliggör desired state kontra observed state.
3. Först därefter: full automation / HostAgent.
4. Fortsätt samtidigt förbättra dokumentation och Portal-adminflöden.
