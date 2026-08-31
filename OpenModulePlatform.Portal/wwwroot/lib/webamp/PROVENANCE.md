# Provenans: webamp.bundle.min.js

| Fält | Värde |
|---|---|
| Paket | `webamp` 2.3.1 (npm registry) |
| Tarball sha256 | `d45a3d46cbd3e4bdd39a9b71656d87341ddac48b4a4c4a61356ec065a6bba7c0` |
| Fil | `package/built/webamp.bundle.min.js` (939 674 byte), kopierad oförändrad |
| Licens | MIT (Jordan Eldredge) — se WEBAMP-LICENSE.txt |
| Uppströmskälla | https://github.com/captbaritone/webamp |
| Hämtad + granskad | 2026-08-31 |

## Säkerhetsgranskning (2026-08-31)

- `package.json`: **noll runtime-beroenden**, **inga install-skript**.
- Egress-grep av bundeln: inga XMLHttpRequest/WebSocket/sendBeacon;
  http(s)-strängarna är XML-namespaces, dokumentationslänkar och Reacts
  felmeddelande-URL:er. `musicbrainz`-strängarna är ID3-taggnamn, inte nätverk.
- Exakt ETT `fetch(`-ställe — används endast för resurser vars URL:er VI skickar
  in (skin, spår, milkdrop-preset). Ingen telemetri i biblioteket
  (webamp.org-sajtens analytics ligger utanför npm-paketet).
- Källkodsgranskning på djupet: se katalognoten `inspiration/webamp.md` i
  DEV-vaulten (fil:rad-referenser för embed-API, skinmotor, ljudgraf, egress).

## Beslut om grundskinnet (operatör, 2026-08-31 — reviderat samma kväll)

SLUTLÄGE (rev 3, samma kväll): widgeten renderar operatörens WINOMP-bearbetning
av grundskinnet — WINAMP-texterna och logotypen ersatta i Paint.NET
(källfiler: DEV-repot, Skins/base-winomp/). Varumärkesdelarna är därmed borta;
grafiken är en bearbetning av Nullsofts original enligt resonemanget nedan.
Mellanläget rev 2 (helgenererat skin) är urkopplat men återskapbart.

Rev 2-läget: widgeten renderar WINOMP — vårt eget genererade skin i klassiskt
visuellt språk (varje pixel ritad av scripts/skins/generate-omp-skins.py) — som
default. Nullsofts inbakade grundskin renderas aldrig. Det tidigare
mellanbeslutet nedan behålls som historik.

## Tidigare mellanbeslut (ersatt)

Widgeten renderar webamps INBYGGDA klassiska grundskin som standardutseende
(inget initialSkin sätts). Ställningstagandet: grafiken är Nullsofts original;
MIT-licensen täcker webamps kod, inte skinnet. Webamp-projektet har distribuerat
det öppet sedan 2018 med uttrycklig friskrivning i sin README, utan åtgärd från
rättighetshavarna, och skinnet ligger oavsett inbakat i bundeln — beslutet gäller
endast om det RENDERAS. Praktisk risk bedömd som mycket låg; formellt olicensierad
grafik. De egenproducerade OMP-skinsen finns kvar under skins/ som fullt
rättighetsrena alternativ om hållningen behöver ändras.

## Kända förbehåll

- Bundeln bär webamps inbakade default-skin (Nullsofts klassiska grundskin som
  base64-CSS). Vår widget sätter ALLTID `initialSkin` till ett eget skin, så
  det materialet renderas aldrig; det följer dock med i filen. Webamp-projektet
  har distribuerat det öppet sedan 2018 med uttrycklig friskrivning i sin
  README ("the Winamp name, interface ... are surely property of Nullsoft").
- Uppgradering: hämta ny tarball med `npm pack webamp@<version>`, upprepa
  granskningen ovan, uppdatera sha256 här.
