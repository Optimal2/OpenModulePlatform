# OMP-skins för dashboardspelaren

`omp-matrix.wsz` och `omp-ljus.wsz` är **egenproducerade** Winamp-classic-skins:
varje pixel ritas av `scripts/skins/generate-omp-skins.py` ur ett färgtema.
Inga sprites, fonter eller bilddata är hämtade från något befintligt skin —
5x6-pixelfonten och 7-segmentssiffrorna är egna original.

Geometrin (arknamn, sprite-positioner, mått) i `sprite-geometri.json` är
exporterad ur webamps källkod (MIT) och beskriver bara VAR saker ritas,
aldrig HUR de ser ut.

Upphovsrätt: Optimal2. Skinsen får användas och distribueras fritt inom
OMP-installationer och erbjuds som alternativa skins i widgeten.

Regenerera/lägg till tema: redigera temalistan i generate-omp-skins.py och kör
`python generate-omp-skins.py` — utdata hamnar i wwwroot/lib/webamp/skins/.
