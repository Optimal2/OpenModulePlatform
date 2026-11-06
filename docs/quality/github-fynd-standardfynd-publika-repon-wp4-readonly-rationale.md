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

## Proposal-stage limitations

Builds and tests were not run because this comment-only proposal explicitly excluded them.
No account lookup, account-rights operation, `LogonUser` call, protected-process interaction,
SQL operation, service operation, installation, or deployment was performed. Consequently,
the proposal made no claim of compiler, analyzer, runtime, or native-account verification.

## Final source integration

The reviewed patch with SHA-256
`472d6e99fd2980ecd96bfe5911d4876d0740916328700bfdad0227bfaa98c753` was
applied without content changes and committed as
`5539f4aa894e45e6b586cdcef5cabf4949868135` on integration base
`cb9ac63ccc2b21a30f76ca16900042a5681b90a6`. No ABI, layout, field modifier,
P/Invoke, call-site, or executable-logic change was introduced. No real Win32
account operation or redundant runtime test was performed for this comment-only
edit.
