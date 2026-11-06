# WP4 `cs/missed-readonly-modifier` rationale

## Disposition

This proposal leaves `LsaUnicodeString` and all executable behavior unchanged. It adds a
short rationale above the private interop structure: the type mirrors the native
`LSA_UNICODE_STRING` sequential layout and follows the writable-field DTO convention used
by the adjacent `LsaObjectAttributes` structure.

The comment is documentation of layout and local convention, not a claim that `ref`
marshaling forbids read-only fields. No CodeQL suppression, analyzer configuration change,
or general rule about interop mutability is proposed.

## Scope

The complete candidate changes only:

- `OpenModulePlatform.Installer/Install/NativeAccountInterop.cs`: two added comment lines.
- `docs/quality/github-fynd-standardfynd-publika-repon-wp4-readonly-rationale.md`: this new
  quality note.

There are no field, modifier, layout, character-set, constructor, P/Invoke, or logic changes.
There are no new tests and no component-version or module-definition changes in this
independent proposal.

## Characterization and proof outcomes

Source inspection established that `LsaUnicodeString` has sequential layout, Unicode
character-set metadata, writable `Length`, `MaximumLength`, and `Buffer` fields, and an
instance constructor that assigns those fields. The adjacent `LsaObjectAttributes` also uses
writable fields. `LsaOpenPolicy` receives an `LsaUnicodeString` instance by `ref`, while
`LsaAddAccountRights` receives an array of the structures. These observations characterize
the existing interop DTO; they are not runtime proof of native behavior.

The following repository-local checks passed against the proposal base:

```text
git diff --check
git apply --check --whitespace=error-all <patch-file>
git apply --numstat <patch-file>
```

`git diff --check` accepted the tracked source edit. After the source was restored,
`git apply --check --whitespace=error-all` accepted the complete two-path patch without
changing the worktree, and `git apply --numstat` reported additions only for both declared
paths. The original and restored source bytes had identical SHA-256 hashes, and the final
worktree status was empty with `HEAD` equal to the recorded base commit.

## Limitations

Builds and tests were not run because this comment-only proposal explicitly excludes them.
No account lookup, account-rights operation, `LogonUser` call, protected-process interaction,
SQL operation, service operation, installation, or deployment was performed. Consequently,
this proposal makes no claim of compiler, analyzer, runtime, or native-account verification.
The final integration stage remains responsible for rebasing the complete candidate,
preparing repository and component identities, and running the required implementation and
artifact gates.
