# Standard-finding followups: final integration evidence

Campaign: `github-fynd-standardfynd-publika-repon`

## Integration base and source stages

The actual rebased integration base was
`cb9ac63ccc2b21a30f76ca16900042a5681b90a6`. That base already contained the
successful CTS cleanup and all three factual WP2 Console-ownership corrections.
The four independently reviewed artifacts were decoded, SHA-256 verified,
scope-checked, and applied with native `git apply --check` before application.
Each rule remains a separate source commit:

| Rule | Patch SHA-256 | Source commit |
| --- | --- | --- |
| `cs/local-not-disposed` | `70eb5477abe1a5798c4ede502271aadf3edceb5ef74528df70f89d968e265f60` | `cc55ca32fd5156f3290be3cc6f7a815840aa07fd` |
| `cs/linq/missed-select` | `8d23afc4b4faf2197a15f1afb5431d5ddcb1eb379c6c6041b637514aa02bbfc1` | `52dd748948a2e1c0c8eef5d642c68aad2c6588a5` |
| `cs/linq/missed-where` | `483bd0abf7fa78e935d5c5f8feda0c7058fe7b0bc0032df59a7830904fe86ffb` | `e2ec6841223708d0f3014102e7e402b9a64b1b55` |
| `cs/missed-readonly-modifier` | `472d6e99fd2980ecd96bfe5911d4876d0740916328700bfdad0227bfaa98c753` | `5539f4aa894e45e6b586cdcef5cabf4949868135` |

The Select artifact is the conductor's archival correction of the original
13,864-byte patch. Its payload retains the original declared SHA-256 and the
ASCII space at byte offset 12,479; the incorrect copied payload was not used.
No source was inferred or independently reimplemented.

## Proposal proof versus final proof

Proposal-stage commands and limitations remain in the four rule documents.
They are not represented as final implementation or scanner-acceptance proof.

The final Select source is byte-identical to the candidate whose two causal
mutations already produced genuine assertion failures and exact-hash
restorations. Those mutations were therefore not repeated. The integrated
candidate was rebuilt and rerun with Release configuration and TRX logging:

- `MaintenanceQuarantineTests`: native exit 0; 20 total, 20 executed, 20
  passed, 0 failed. The direct owned-junction test executed and passed.
- `PackageLibraryDefinitionGateTests`: native exit 0; 21 total, 21 executed,
  21 passed, 0 failed.

For Where, the existing
`CarryOver_LeavesKeyTheNewResolutionSetsItself` case passed 1/1 before mutation.
Changing only `nextKeys.Add(property.Key)` to `true` then produced native exit
1 and a genuine `Assert.Empty` precedence failure with
`["AllowedHosts", "Logging"]` (1 total, 0 passed, 1 failed). Restoring the
original expression returned SHA-256
`2609a13945a0a6dfcbd1923e609ffa3522e5fe060e146e8d33b0a720ea559147`
exactly, and the same test again passed 1/1. No mutation was retained.

Console and Readonly received no new runtime tests because their retained C#
changes are comments only. No console was attached or opened and no Win32
account operation was invoked.

## Package identity reconciliation

Actual project-reference closure showed that the changed
`OpenModulePlatform.HostAgent.Runtime` output is packaged by only one of the 14
manifest components: `omp-hostagent-service`. The Bootstrapper also includes
that runtime, and the Bootstrapper/Installer package takes its identity from
`repositoryVersion`. One canonical helper pass therefore covers all changed
package identities without inventing unrelated component or dependency-minimum
bumps.

| Identity | Before | Final |
| --- | --- | --- |
| `repositoryVersion` | `0.3.1110` | `0.3.1111` |
| `omp-hostagent-service` component | `0.3.336` | `0.3.337` |
| `omp_core` module definition | `0.3.290` | `0.3.291` |
| `omp_core` compatible artifact maximum for `omp_hostagent` | `0.3.336` | `0.3.337` |

The other 13 component versions, all component minimum constraints, the other
eight module definitions, and their compatible-artifact constraints remain
unchanged relative to the rebased baseline. The complete before/after matrix
and final metadata commit are recorded in the operator report.

Before preparation, strict component validation completed both deterministic
Web.Shared builds successfully and reported exactly one error: the required
`omp-hostagent-service` bump was still absent. That expected error established
the affected-package requirement rather than being counted as a passing final
validation. Final strict validation and the actual pre-push gate belong to the
publication stage and are reported with their native results in the operator
report.

## Publication and measurement boundaries

The two public scans, full pre-push gate, guarded push, final strict validator,
and exact-SHA GitHub workflows are completion requirements, not results
pre-claimed by this source-stage document. The final operator report binds the
actual final commit, native full-gate counters, scan results, pushed SHA, and
workflow conclusions.

No fresh CodeQL clearance is inferred from source inspection or tests. Browser
finding counts, retained-finding disposition, and any CodeQL dismissal remain
with the conductor. No release, tag, deployment, runtime package import,
installation, service/IIS/scheduler change, or customer-environment operation
is part of this integration.
