# Manual admin configuration

## Syfte

Portal ska vara den normala vägen för manuell konfiguration av OMP.
Efter bootstrap i SQL ska en administratör normalt inte behöva redigera
kärnobjekt direkt i databasen.

## Rekommenderad arbetsordning

### 1. Verifiera RBAC

Börja med att kontrollera att rätt principal har Portal-adminrättigheter.

Viktigt:

- byt ut alla `REPLACE_ME`-värden från SQL-bootstrapen
- verifiera att rätt användare eller grupp finns i `RolePrincipals`
- logga in i Portal och kontrollera att adminytorna är tillgängliga

### 2. Skapa eller justera Instance

En `Instance` är högsta manuella nivån.

Den bör normalt finnas innan du lägger till:

- hosts
- module instances
- app instances

### 3. Lägg till Hosts

Hosts hör till en viss `Instance`.

Vid manuell installation är detta en kärndel av modellen.
Template-relaterade hostobjekt behövs däremot inte för att få systemet
att fungera manuellt.

### 4. Verifiera eller skapa Modules och Apps

Detta är definitionsnivån.

- `Modules` beskriver moduldefinitioner
- `Apps` beskriver appdefinitioner
- `Artifacts` beskriver deploybara byggprodukter

### 5. Skapa ModuleInstances

Här placeras en moduldefinition in i en konkret OMP-instans.

### 6. Skapa AppInstances

Detta är den viktigaste runtime-nivån vid manuell drift.

På `AppInstance` anger du bland annat:

- vilken module instance den tillhör
- vilken host den kör på
- vilken appdefinition den representerar
- vilket artifact den använder
- vilken route/path/url som gäller
- vilken config som gäller
- vilken desired state och verifieringspolicy som gäller

## När template- och deploymentytorna behövs

Dessa delar hör främst till automation och framtida HostAgent-flöde:

- instance templates
- host templates
- template-topologi
- host deployment assignments
- host deployments

Vid ren manuell installation kan dessa oftast lämnas orörda.

## Praktiska råd

- använd Portal för löpande administration där det går
- använd SQL främst för initial installation och kontrollerad bootstrap
- behandla `AppInstance` som central runtime-enhet
- undvik att lägga runtime-data på `Modules` eller `Apps`
