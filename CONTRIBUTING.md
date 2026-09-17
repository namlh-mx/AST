# Contributing to AST

AST is maintained by a banking practitioner with AI-assisted development. It is still under
development and internal testing. Contributions that make the existing foundation easier to
understand, test and use are welcome. Vietnamese and English reports are both useful.

## Feedback from banking practitioners

You do not need to write code to contribute. Describe the screen, the action you tried, the
expected result and what actually happened. Use fictional units, people and records.

If you have a GitHub account, check the [existing issues](https://github.com/namlh-mx/AST/issues)
and open a report if the problem is new. For internal testers without an account, the maintainer
can record a sanitised summary on their behalf, as in
[issue #7](https://github.com/namlh-mx/AST/issues/7). Feedback relayed this way should identify that
it is a maintainer summary, without identifying the tester or workplace unnecessarily.

Useful details:

- Application version or source commit and the screen involved.
- Numbered steps using synthetic data, including effective dates where relevant.
- Expected and actual behaviour, and whether the problem repeats.
- Whether a write was accepted or refused; distinguish a display problem from changed data.
- A cropped, sanitised screenshot if it helps, and relevant non-sensitive error text.

Keep customer information, real account numbers, credentials, connection strings and confidential
workplace material out of public reports. For suspected vulnerabilities, use
[the private reporting channel](SECURITY.md), not a public issue.

## Code and documentation changes

Discuss larger features in an issue first so that they fit the
[roadmap](docs/maintenance-and-roadmap.md#next-work). Keep pull requests focused on one problem.
Include the reason for the change, the resulting behaviour, related issue links and validation.
AI-assisted contributions are welcome; the contributor remains responsible for understanding
the change, checking its behaviour and identifying any unverified assumptions.

Start with [the architecture in README](README.md#architecture). For changes to data semantics,
consult [the effective-period design](docs/design-effective-period.md) and
[IAM schema](docs/design-iam-schema.md); use those documents as the technical source of truth.
Update affected documentation when behaviour changes, and follow `.editorconfig`.

Use the [developer setup](README.md#running-it). Relevant regression tests should cover a bug's
observable behaviour. Record commands, environment and passed/failed/skipped counts; explicitly
state when database-backed tests were not run. The test database is disposable and the IAM
integration suite drops its tables. See [verification reporting](docs/maintenance-and-roadmap.md#verification-evidence).

For documentation-only changes, check accuracy against the source, local links and examples;
there is no need to run database-destructive tests just to change prose.

## Licence

Contributions are made under the project's [MIT licence](LICENSE). Submit only material you
are entitled to share publicly, including when a suggestion originates in workplace feedback.
