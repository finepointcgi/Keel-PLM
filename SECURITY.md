# Security policy

Keel PLM handles export-controlled and controlled unclassified data, so we take reports about security problems seriously.

## Report a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private reporting instead: open the [Security tab](https://github.com/finepointcgi/Keel-PLM/security/advisories/new) of this repository and choose **Report a vulnerability**. Include what you found, how to reproduce it, the version (image tag or commit) and what you think the impact is.

Never send real export-controlled or controlled data in a report. Describe the problem with synthetic data, as the project's own tests do.

## What to expect

We will acknowledge a report within a few working days, tell you whether we agree it is a vulnerability, and keep you informed until a fix is released. We will credit you in the release notes unless you prefer not to be named.

## What is in scope

The application and its container images, the database migrations, the scripts and Compose and Helm files in `deploy/`, and the documentation where it would lead an installer to an insecure setup. Problems in the access checks (markings, roles, supplier isolation), the audit chain, sign-in and step-up, the vault, and the build and release workflows are the most important.

The quickstart in `deploy/quickstart` and the demo image are deliberately not hardened for a network (plain HTTP, a development-mode sign-in service); issues about that are documentation questions, not vulnerabilities.

## Supported versions

Only the latest release receives security fixes while Keel PLM is below 1.0.

## A note on certification

Keel PLM is built to support export-control and CMMC work. The software is not certified by any body, and nothing in this policy is a statement that it is.
