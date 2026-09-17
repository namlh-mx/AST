# Security policy

AST is an alpha project under development and internal testing. It is not represented as a
production-ready banking system or as having received an independent security certification.

## Reporting a vulnerability

Use GitHub's [private vulnerability reporting](https://github.com/namlh-mx/AST/security/advisories/new)
for suspected security problems in AST. Private reporting is enabled for this repository.
Do not put exploit details or sensitive data in a public issue or pull request.

Include the affected release or commit, a description of the impact and the smallest reproducer
you can provide using synthetic data in an environment you control. For example, identify the
authorization boundary, configuration-signing check or recovery operation involved. Do not send
customer records, private signing keys, passwords or internal connection details, even in a
private report.

The maintainer will review reports on a best-effort basis. This one-maintainer project does not
promise a fixed response time. Coordinate disclosure through the private report while a fix is
being assessed. If the form is unavailable, open a public issue requesting a private reporting
channel without disclosing vulnerability details.

## Versions and fixes

Security work currently focuses on `main`. Reports against the latest alpha are also welcome;
please identify the exact version. There is no long-term-support or backport commitment for
older alpha builds. A fix on `main` does not mean that an earlier downloadable build contains it.

## Evaluation boundaries

Use synthetic data and a disposable database for development and testing. Integration tests
drop database tables. Development credentials in sample configuration are for disposable local
setups. Workplace deployment requires the organisation's own review and approval; the public
setup instructions are not a production deployment guide.

CodeQL checks and automated tests provide different evidence. Neither is a guarantee that a
release is free of vulnerabilities. See [verification evidence](docs/maintenance-and-roadmap.md#verification-evidence).
