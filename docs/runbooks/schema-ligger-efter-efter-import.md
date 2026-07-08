# Schema ligger efter efter import — operatör-runbook

## Symtom
- Portal eller applikation ger 500-fel som nämner en saknad kolumn, t.ex.
  `Invalid column name '...'`.
- En modultabell saknar kolumner som finns beskrivna i SQL-skriptet.
- Valideringen på `/admin/moduledefinitions` visar rött för en modul.
- Ett paket med "senaste koden" har nyligen importerats, men databasschemat
  verkar inte uppdaterat.

## Rotorsak
Import av ett artifact-zip är **versionsgated**, inte schemagated. Importen
skapar eller uppdaterar bara `omp.Artifacts`; den kör INTE moduldefinitionens
SQL-skript. SQL körs bara när en moduldefinition med ett **nyare**
`definitionVersion` tillämpas. Om `definitionVersion` redan hade höjts vid ett
tidigare importtillfälle, men SQL:en inte exekverades då, kommer nästa import
inte att köra det nya SQL:et även om paketet innehåller det.

## Åtgärd
1. Öppna Portal → Admin → Module Definitions och kontrollera vilken
   `definitionVersion` som är aktiv för modulen.
2. Identifiera det idempotenta setup/repair-SQL som motsvarar modulen. För
   OMP core är det:
   `sql/1-setup-openmoduleplatform.sql`
   För andra moduler finns sökvägen i moduldefinitionens `sqlScripts`.
3. Kör SQL-skriptet mot `OpenModulePlatform`-databasen med `sqlcmd` eller
   SSMS. Skriptet är idempotent, så det är säkert att köra igen.
4. Verifiera att felet försvinner och att `/admin/moduledefinitions` visar
   grönt.
5. Om nödvändigt, lägg på eller tillämpa den aktuella moduldefinitionen så
   att den tillämpade versionen matchar det avsedda kontraktet.

## Förebygg
- Varje gång en moduldefinition-ägd `.sql`-fil ändras måste samma
  commit/ändring också:
  1. Höja `definitionVersion` i aktuell `.module-definition.json`.
  2. Köra `scripts/dev/embed-module-definition-sql.ps1` för att bädda in det
     uppdaterade SQL:et och uppdatera `sha256`.
  3. Uppdatera relevanta `minModuleDefinitionVersion` i
     `omp-components.json` om en komponent behöver det nya kontraktet.
