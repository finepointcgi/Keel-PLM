# Contributing to Keel PLM

Thank you for helping. Keel PLM is free software under the [AGPL-3.0](LICENSE); by contributing you agree that your contribution is licensed the same way.

## Before you start

- Look through the open [issues](https://github.com/finepointcgi/Keel-PLM/issues) and open one to discuss a larger change before you build it.
- Read [docs/CLAUDE.md](docs/CLAUDE.md): the conventions, the glossary and the current status. [docs/adr](docs/adr/README.md) explains why things are built the way they are.
- Report security problems privately: see [SECURITY.md](SECURITY.md).

## Set up

You need the .NET SDK pinned in `global.json` (exactly that version) and Docker. The steps are in the [README](README.md#build-and-run-from-source) and [docs/setup.md](docs/setup.md).

```bash
scripts/test.sh                  # unit and integration tests
KEEL_E2E=1 scripts/e2e.sh        # browser tests on an isolated stack
```

Run the suites one at a time. The build treats warnings as errors.

## Rules that matter most

- **Access first.** Every read over marked data goes through `AccessPolicy.BuildReadFilter`, every single-object action calls `AccessPolicy.Evaluate`, and each change writes its audit event in the same transaction. Look-and-feel changes must never alter markings, banners, access rules or the audit trail.
- **No network at runtime.** No CDN, no external fonts or scripts; the app runs air-gapped under a strict content security policy.
- **Synthetic data only.** Never commit real or export-controlled data, real credentials or keys.
- **Migrations** are hand-written, forward-only SQL in `db/migrations`.
- **Plain, mixed-case wording** in the interface and docs. All capitals are only for marking banners and portion marks.
- **Don't claim certification.** Say "built to support", never "certified" or "compliant", about the software.
- Add or update tests with your change, and update the user guide chapter (and its screenshots) when a screen changes.

## Pull requests

Keep a pull request to one change. Explain what and why, say how you tested it, and note anything you did not verify. CI must pass: build, unit tests, secret scan, licence policy, link check and the browser suite.
