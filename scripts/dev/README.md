# Developer Helpers

These scripts are for repository maintenance and local test data only. They are
not production installers.

- `embed-module-definition-sql.ps1` refreshes embedded SQL inside module
  definition JSON files from the module-owned SQL files in this repository.
- `test-deterministic-web-publish.ps1` publishes a web project twice with
  the `wwwroot` timestamps moved in between and compares the artifact
  SHA-256 of both outputs; `-DisableDeterministicStaticWebAssets` shows the
  state without the pinning target.
- `pack-determinism-fixture.ps1` drives the installer or canonical artifact
  producer for the cross-producer determinism fixture used by
  `scripts/omp/test-artifact-producers.py`.
- `seed-content-webapp-test-pages.ps1` inserts optional Content module smoke
  test pages into a local OMP database.
- `test-content-webapp-local-install.ps1` checks Content file mirroring, stale
  file deletion, installed-file loading, and server-report rendering through the
  focused `ContentWebAppRuntimeProbe` project.

Normal installation and upgrade flows should use the HostAgent-first installer
or Portal/HostAgent object import.
