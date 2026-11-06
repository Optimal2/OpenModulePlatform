# WP4: rationale for stateful case-insensitive membership

Campaign: `github-fynd-standardfynd-publika-repon`
Owned rule: `cs/linq/missed-where`
Source base: `9a88513babab1019382d744ee8c5dd4c82aa3b84`
Proposal status: **comment-only; not integrated or accepted**

## Finding characterization

Direct source review covered the `nextKeys` construction and the host-owned-key loop in
`OpenModulePlatform.HostAgent.Runtime/Services/ConfigurationContinuityGate.cs`.
The review treats the `cs/linq/missed-where` location as a request to explain an
intentional stateful predicate, not as evidence of a runtime defect. No CodeQL scan was
run for this proposal, so it makes no claim about post-change finding closure.

`nextKeys` starts with the new resolution's property names and uses
`StringComparer.OrdinalIgnoreCase`. For each previous host-owned property,
`nextKeys.Add(property.Key)` therefore does two things beside the copy:

- it returns `false` when the new resolution already supplies that name in any casing,
  so the explicit new value wins; and
- when it returns `true`, it registers the name immediately as part of the same
  membership operation.

The proposal adds this rationale beside the code. It does not add a `Where` clause,
change a predicate, or make a general claim that LINQ operators cannot participate in
state mutation. It also does not guarantee behavior for every possible duplicate-input
shape. In particular, `JsonObject`'s default exact-key duplicate rejection does not
reject distinct-case names, so the explicit ordinal-ignore-case membership remains
meaningful.

## Scope and logic preservation

The only C# change is a four-line comment. A scratch-only byte comparison removed that
exact comment block from the candidate and matched the remaining bytes with the saved
base file. Original and comment-stripped candidate SHA-256:

```text
fdadd6f0804589ebf8a6be8418a8c683357a99d82989bfad78712adb88213adc
```

Thus executable C# text is byte-identical. No test source, component version, module
definition, or repository version is changed by this proposal.

## Proof commands and outcomes

The following checks completed with native exit code **0**:

```text
git diff --check -- OpenModulePlatform.HostAgent.Runtime/Services/ConfigurationContinuityGate.cs
git apply --check --whitespace=error <patch-file>
```

The first check covered the edited source before restoration. The second checked the
complete two-file patch against the clean base after restoration. A separate scratch
helper performed the comment-only byte comparison described above and also exited **0**.

No build or test command was run, as required for this comment-only proposal. Native
test count is therefore **0**, not a passing runtime result.

## Proposal-stage remaining validation and limitations

The existing test
`ConfigurationContinuityGateTests.CarryOver_LeavesKeyTheNewResolutionSetsItself`
characterizes the explicit-new-value-wins contract with different casing, but it was
only inspected during the proposal. The final implementation stage therefore had to
run it and perform the separately specified actual `nextKeys.Add => true`
mutation/restoration proof with explicit native counts and exit codes. The
proposal-stage evidence did not dismiss the original finding.

## Final source integration and preservation proof

The reviewed patch with SHA-256
`483bd0abf7fa78e935d5c5f8feda0c7058fe7b0bc0032df59a7830904fe86ffb` was
applied without content changes and committed as
`e2ec6841223708d0f3014102e7e402b9a64b1b55` on integration base
`cb9ac63ccc2b21a30f76ca16900042a5681b90a6`.

The final source before mutation had SHA-256
`2609a13945a0a6dfcbd1923e609ffa3522e5fe060e146e8d33b0a720ea559147`.
The existing focused test passed with native exit 0 and TRX counters 1 total,
1 executed, 1 passed, and 0 failed. Changing only the production
`nextKeys.Add(property.Key)` check to `true` produced mutant SHA-256
`e0249ef2c6bfb01c81b2af188a4e7d2b2bee210bc00dd2518f208e5f9c339bf6`.
The same test then failed with native exit 1 and TRX counters 1 total, 1
executed, 0 passed, and 1 failed. Its precedence assertion reported
`Assert.Empty() Failure: Collection was not empty` with
`["AllowedHosts", "Logging"]`; this was a genuine test assertion, not a build,
fixture, discovery, or setup failure. Restoring the original expression returned
the exact pre-mutation SHA-256, and the focused test again passed 1/1 with
native exit 0. No executable change was retained.
