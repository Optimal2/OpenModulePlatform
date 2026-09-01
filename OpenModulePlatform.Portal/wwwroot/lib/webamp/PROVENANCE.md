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

## Beslut om grundskinnet (operatör, 2026-08-31)

**Gällande läge — läs bara det här stycket om du vill veta rättighetsläget i dag.**
Widgeten renderar `skins/winomp.wsz`: operatörens bearbetning av grundskinnet, där
WINAMP-texterna och logotypen är ersatta i Paint.NET (källfiler i det privata DEV-repot,
`Skins/base-winomp/`). Varumärkesdelarna är alltså borta; grafiken är i övrigt en
bearbetning av Nullsofts original.

**Webamps inbakade grundskin är gjort oåtkomligt i widgeten.** Tre mekanismer, alla i
koden och verifierade 2026-09-02:

- `initialSkin` sätts alltid till det första registrerade skinnet
  (`OpenModulePlatform.Portal/wwwroot/js/portal-dashboard.js:2312`), och den listan
  innehåller bara WINOMP
  (`OpenModulePlatform.Portal/Pages/Shared/_DashboardMusicPlayerWidget.cshtml:21`).
- Spelaren visas först när WINOMP-skinnet bekräftat laddats — ingen blink av originalet
  vid sidladdning. Ett laddningsfel faller till den klassiska spelaren, inte till
  originalskinnet.
- Menyvalet `<Base Skin>` saneras bort ur skinmenyn
  (`portal-dashboard.js:2379-2382`, exakt lövmatchning).

Originalets bytes ligger kvar i bundelfilen — den är avsiktligt bit-identisk med
npm-artefakten (939 674 byte, verifierat 2026-09-02) — men kan inte renderas i widgeten.

### Hur vi kom hit (historik — inte gällande läge)

Ordningen nedan är kronologisk. Varje steg är ersatt av det som står under det, och det
gällande läget står i stycket ovan. (Omdisponerat 2026-09-02: dokumentet hade tidigare ett
avsnitt märkt "SLUTLÄGE (rev 3)" med en senare rev 4 UNDER sig, så en läsare som stannade
vid ordet SLUTLÄGE fick fel bild av ett rättighetsläge.)

1. **Rev 1 — ersatt.** Widgeten renderade webamps inbyggda klassiska grundskin (inget
   `initialSkin` sattes). Ställningstagandet var att MIT-licensen täcker webamps kod, inte
   skinnet; att webamp-projektet distribuerat det öppet sedan 2018 med uttrycklig
   friskrivning utan åtgärd från rättighetshavarna; och att skinnet ändå ligger inbakat i
   bundeln, så beslutet gällde bara om det RENDERADES. Risken bedömdes som mycket låg, men
   grafiken var formellt olicensierad.
2. **Rev 2 — urkopplat, återskapbart.** WINOMP som helgenererat skin: varje pixel ritad av
   `scripts/skins/generate-omp-skins.py`. Fullt rättighetsrena pixlar. Kvar som utväg om
   hållningen behöver ändras.
3. **Rev 3.** Bytt till operatörens bearbetning av grundskinnet (varumärkesdelarna
   borttagna) i stället för det helgenererade.
4. **Rev 4 — gällande.** Grundskinnet gjort oåtkomligt i widgeten, enligt de tre
   mekanismerna ovan.

## Kända förbehåll

- Bundeln bär webamps inbakade default-skin (Nullsofts klassiska grundskin som
  base64-CSS). Det renderas aldrig — se de tre mekanismerna under "Gällande läge" —
  men det följer med i filen, eftersom vi medvetet håller bundeln bit-identisk med
  npm-artefakten. Webamp-projektet har distribuerat det öppet sedan 2018 med uttrycklig
  friskrivning i sin README ("the Winamp name, interface ... are surely property of
  Nullsoft").
- Uppgradering: hämta ny tarball med `npm pack webamp@<version>`, upprepa
  granskningen ovan, uppdatera sha256 här.
