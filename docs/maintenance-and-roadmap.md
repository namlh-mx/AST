# Maintenance evidence and next work

Evidence snapshot: 2026-09-17. Linked records describe specific releases or changes, not a
guarantee about every later commit. Current feature status is maintained in [README](../README.md).

## Feedback and maintenance

AST's maintainer tests with banking staff in an internal environment. Some testers do not use
GitHub, so the maintainer records their feedback publicly in sanitised form.

| Record | Public evidence |
|---|---|
| Org-unit gap message and replacement history | [Issue #3](https://github.com/namlh-mx/AST/issues/3) records the two parts of the report and completion of the remaining history work. |
| Save-button state and date-field interactions | [Issue #4](https://github.com/namlh-mx/AST/issues/4) links the completed work to public commit `3acd3f7`. |
| Parent-unit display and Add/load timing | [Issue #7](https://github.com/namlh-mx/AST/issues/7) records feedback relayed from two office testers and a description of both fixes. |

These records demonstrate a feedback-and-maintenance cycle. They do not establish broad banking
adoption, production deployment or quantified productivity gains. Future reports should link
the original scenario, the fixing commit and the result of retesting, as applicable.

## Verification evidence

- The [v0.1.0-alpha release](https://github.com/namlh-mx/AST/releases/tag/v0.1.0-alpha), published
  2026-08-24, reports 1,310 tests, 0 failed and 0 skipped, including 403 IAM integration tests
  against MySQL 9.7. These are the maintainer's recorded results for that build, not a new run
  against current `main`.
- The [2026-09-12 CodeQL run](https://github.com/namlh-mx/AST/actions/runs/34686457610) completed
  successfully. Its job performs C# analysis; it is not a full application test-suite report.
- The [IAM database test helper](../AST.Modules.IAM.Tests/TestSupport/TestDatabase.cs) skips when
  no test connection is configured. A configured but unreachable database fails. A report with
  skipped database tests cannot establish that those behaviours passed.

To record a new test run, use a disposable environment following the
[test setup](../README.md#running-the-tests). The repository's VSTest-based projects can write
TRX results using:

```text
dotnet test --logger trx --results-directory artifacts/test-results
```

`artifacts/` is gitignored. Keep each run's results separate from older reports when counting
outcomes. See Microsoft's [dotnet test reference](https://learn.microsoft.com/dotnet/core/tools/dotnet-test)
for runner options. Before sharing a report or log, remove connection details, secrets and other
private environment information.

For a release or verification report, record:

| Field | Required context |
|---|---|
| Source | Commit SHA, configuration and date |
| Environment | Windows, .NET SDK and MySQL versions |
| Execution | Exact commands and suite/project scope |
| Results | Total, passed, failed and skipped counts; reasons for skips |
| Database | Whether the real disposable MySQL database was used |
| Evidence | Sanitised report or artifact link and related issues |
| Limitations | Unrun checks, failed checks and manual scenarios still outstanding |

Publishing a current full-suite report and making database-backed verification reproducible are
next steps. This documentation update does not claim to add test CI or to rerun the alpha tests.

## Next work

The following is an ordered direction, not a promised release date or a completed-work list.

1. **Maintain the existing foundation.** Reproduce feedback on IAM screens, fix confirmed faults
   and add relevant regression coverage. Keep operator wording and history presentation aligned
   with actual behaviour.
2. **Make evaluation easier to repeat.** Refine the synthetic-data walkthrough from tester
   feedback, record aggregate evaluation scope and publish version-specific verification
   evidence with the next alpha when ready.
3. **Implement the operation-history model.** Follow the existing
   [design](design-operation-history.md), including write-path and read-model verification.
   Persisted version lifecycle status already exists and is not this pending feature.
4. **Build the business workflows.** Progress through transaction accounting, internal
   accounting, treasury and cash-vault, management reporting, then inspection and supervision.
   The current sidebar entries remain placeholders until their implementations are delivered.

For the next development cycle, prioritise the first two items before expanding scope. Issue
feedback and verification results should determine when a change is ready to release.
