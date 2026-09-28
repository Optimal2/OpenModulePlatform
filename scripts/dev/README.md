# Developer Helpers

These scripts are for repository maintenance and local test data only. They are
not production installers.

- `embed-module-definition-sql.ps1` refreshes embedded SQL inside module
  definition JSON files from the module-owned SQL files in this repository.
- `test-deterministic-web-publish.ps1` publishes a web project twice with
  the `wwwroot` timestamps moved in between and compares the artifact
  SHA-256 of both outputs; `-DisableDeterministicStaticWebAssets` shows the
  state without the pinning target.
- `seed-content-webapp-test-pages.ps1` inserts optional Content module smoke
  test pages into a local OMP database.

Normal installation and upgrade flows should use the HostAgent-first installer
or Portal/HostAgent object import.
