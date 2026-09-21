# J14 documentation validation

Run from the repository root with Python 3.9+ and ripgrep on PATH:

```powershell
python diagnostics/J14/check_docs.py
```

The checker discovers Markdown files with `rg --files`, checks repository-local inline and simple
reference links, validates Markdown heading fragments, and checks literal project/solution paths
in dotnet command examples. It prints a JSON summary and exits nonzero on any invalid target.
Git-ignored build/test output is excluded. External URLs are not fetched; fenced commands marked
`external` describe optional sibling checkouts and are excluded from local project-path checks.
This is an opt-in documentation check, not a CI neutrality gate or a complete Markdown parser.

The J14 audit replaced five broken links in the old extraction guide, corrected build-versus-test
provider selection, and checked the capability table against the provider declarations and shared
conformance coverage. The [current guide](../../docs/jseal.md) links to local implementation and
tests; [historical probes](../J00/README.md) retain their dated evidence separately.

Local audit output is under `test-results/j14/`: link results, comment-only source comparison,
XML documentation validation, and a build of the guide's C# example. The example is compiled but
not executed. Missing-file and missing-anchor controls exercise the link checker. Behavioral tests
are not rerun for this documentation slice. The human-review record remains unchanged and pending.
