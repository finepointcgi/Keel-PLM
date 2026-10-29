# axe-core (vendored, test-only)

| | |
|---|---|
| Library | axe-core by Deque Systems |
| Version | 4.10.2 (npm tarball `axe-core-4.10.2.tgz`) |
| File | `axe.min.js` (unmodified, 553,290 bytes) |
| SHA-256 | `b511cd9dec01c76f4b2ad1723b66b6db37d4c2eb4ed199076e1829d9ee7b75e3` (equals the upstream `sri-history.json` entry `sha256-tRHNnewBx29LKtFyO2a22zfUwutO0ZkHbhgp2e57deM=`) |
| Licence | Mozilla Public License 2.0 (file `LICENSE`, unmodified). Note: axe-core is MPL-2.0, not MIT. MPL-2.0 is file-level copyleft; the file is used unmodified and is never distributed with the product. |
| Used by | `tests/Keel.E2E/AccessibilityTests.cs` only (injected through the DevTools protocol, not as a script element, so the strict CSP applies) |
| Shipped? | No. Not referenced by any `src/` project, not copied into the container image or offline bundle (a test asserts both). |

To update: download the new release on a connected machine, replace `axe.min.js` and `LICENSE`, update the version and hash here and in `AccessibilityTests.AxeSha256`.
