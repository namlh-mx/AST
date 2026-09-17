# Evaluating AST with synthetic data

This guide helps banking practitioners and developers evaluate the current foundation and
report useful feedback. AST is under development and internal testing; accounting workflows
shown as placeholders are not available for operational use.

## Choose an evaluation path

- **Banking staff in the internal test group:** use the build and disposable test environment
  prepared by the maintainer or authorised administrator. You do not need GitHub or Internet
  access in the application to try a screen. Follow your workplace's requirements.
- **Developers evaluating the public source:** follow the [README setup](../README.md#running-it)
  on Windows, with the documented prerequisites and a disposable database. `main` may have
  newer schema requirements than the downloadable alpha; keep the code and migrations together.
- **Readers interested in reuse:** start with the example below and the linked design documents.
  Running the WPF application is not required to review the data model.

For application setup, use the configuration-station and database-connection screens shown in
the [screenshots](../README.md#screenshots). The administrator must prepare the required
configuration and access. If startup or signing checks block access, record the message and ask
the maintainer for help; do not bypass those checks. The public developer setup is not a complete
guide to preparing an organisation's signed release configuration.

## A fictional effective-period example

Suppose a fictional organisation contains these units:

| Unit | Parent | Effective period |
|---|---|---|
| DEMO-PARENT | None | 2030-01-01 through 2030-12-31 |
| DEMO-CHILD | DEMO-PARENT | 2030-03-01 through 2030-06-30 |

The child's entire period is covered by the parent's. Extending the child through 2031-01-31
would take it beyond its parent's coverage and should be rejected. This illustrates why a
parent-child relationship needs dates as well as identifiers.

This is an illustrative scenario, not a claimed test run. In an administrator-prepared test
environment, use fictional records to try the valid relationship and then the invalid change.
Record the message, whether the save was refused, and whether the previously valid values remain
unchanged. If another validation rule prevents reaching the scenario, report that as an
evaluation obstacle rather than treating the scenario as passed.

For the exact rules, read the [effective-period design](design-effective-period.md). For
implementation evidence, see [temporal-FK edge tests](../AST.Modules.IAM.Tests/Data/IamTemporalFkEdgesTests.cs)
and [org-unit declaration tests](../AST.Modules.IAM.Tests/Integration/OrgUnitDeclarationServiceTests.cs).

## Observe the existing screens

Try selecting an organisational unit, switching between cards, starting an Add action, editing
effective dates and reviewing history. Use [issue #7](https://github.com/namlh-mx/AST/issues/7)
as an example of useful feedback about timing and misleading display state. Distinguish what
you saw from what you believe happened to stored data.

Use only fictional names, codes and records. Screenshots must not include customer or workplace
secrets. The [contribution guide](../CONTRIBUTING.md) explains how to report a problem, including
how feedback from testers without GitHub accounts can be relayed.

## Record useful evidence

An evaluation summary can use the following fields. Leave unknown values explicitly unknown.

| Field | What to record |
|---|---|
| Build and date | Release/commit and evaluation date |
| Scope | Screens or scenarios actually tried |
| Participants | Aggregate tester count and broad role, where permitted to share |
| Frequency | Sessions or repeated attempts, with the observation period |
| Result | Expected/actual behaviour and a link to any sanitised issue |
| Follow-up | Fix commit and result of repeating the original scenario |

When an end-to-end workflow becomes available, compare the same synthetic task before and after
using AST. Record task completion time, number of attempts and the definition of an error before
claiming an improvement. A small internal trial should be described with its sample size and
limitations. No measured productivity or error-reduction result is claimed by this guide.
