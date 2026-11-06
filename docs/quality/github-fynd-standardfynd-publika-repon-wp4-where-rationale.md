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

## Remaining validation and limitations

The existing test
`ConfigurationContinuityGateTests.CarryOver_LeavesKeyTheNewResolutionSetsItself`
characterizes the explicit-new-value-wins contract with different casing, but it was
only inspected here. The final implementation stage must run it and report its real
count and native exit code, using the repository's normal isolated validation setup:

```text
dotnet test OpenModulePlatform.HostAgent.Runtime.Tests/OpenModulePlatform.HostAgent.Runtime.Tests.csproj --filter FullyQualifiedName~ConfigurationContinuityGateTests.CarryOver_LeavesKeyTheNewResolutionSetsItself --logger "console;verbosity=normal" --verbosity minimal
```

That stage must also perform the separately specified actual `nextKeys.Add => true`
mutation/restoration proof and report its before, RED, and restored results. Until then,
this document provides source-level and patch-shape evidence only; it is not runtime
acceptance evidence and does not dismiss the original finding.
