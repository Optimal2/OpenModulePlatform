# WP2 console writer lifetime rationale

## Scope and finding

- Rule: `cs/local-not-disposed`.
- Source: `OpenModulePlatform.Installer/Program.cs`.
- The finding concerns the `StreamWriter` instances that wrap the standard output and standard error streams before they are passed to `Console.SetOut` and `Console.SetError`.
- Both writers must remain usable after the attachment method returns because later console output uses them. Disposing them in that method would break subsequent output; their intended lifetime is the remainder of the process.
- The previous rationale incorrectly said that `Console` takes ownership of the writers. The replacement rationale records the deliberate process lifetime without transferring cleanup or disposal responsibility to `Console`.

## Patch characterization

The C# change replaces only the two-line rationale comment. It does not change either `Console.Set*` call, writer construction, `AutoFlush`, exception handling, P/Invoke declarations, or any other executable code. The only other file in the patch is this evidence document. No component or version metadata is changed.

## Proposal-stage proof

The proposal was prepared at base `9a88513babab1019382d744ee8c5dd4c82aa3b84` using these repository-level checks:

```text
git diff -- OpenModulePlatform.Installer/Program.cs
git apply --check --whitespace=error-all <patch-file>
git diff --exit-code
git diff --cached --exit-code
git status --porcelain=v1
```

Observed outcomes:

- The source diff contained exactly the intended two-line comment replacement.
- The complete two-file patch passed `git apply --check --whitespace=error-all` against the clean base (exit code 0).
- After patch capture, the source was restored byte-for-byte. Its SHA-256 was `66ed88e963c45fab18ca8ee30bd517446c4402f70408a7e42d6115d950b3615a` both before the temporary edit and after restoration.
- `BASE` and `HEAD` both remained `9a88513babab1019382d744ee8c5dd4c82aa3b84`; the final worktree diff, staged diff, and porcelain status were empty (exit code 0).

## Limitations

This is a comment-only patch proposal, not an accepted implementation. No build, test assembly, test filter, CodeQL analysis, installer execution, or runtime console attachment was run, as required for this proposal. In particular, `AttachToParentConsole` was not called and no console was opened or attached. Preservation of executable behavior is supported by the unchanged executable source lines, not by runtime testing, and this document does not claim that the CodeQL alert has been resolved. Final integration owns the normal implementation validation and acceptance gates.
