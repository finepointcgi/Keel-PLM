# ADR 0063: One review step in the header and one task page

Status: Accepted

## Context

Review and release lived on a "Review & release" tab of every object page, and the person who had to decide used one of two pages (the desktop task page and a separate phone approval page with its own list). That was more places and more clicks than the job needs.

## Decision

- **Review strip.** Parts, documents, changes, baselines, specifications, MBOMs, quality records, gates, suppliers and process plans show a strip in the page header: where the review stands and the one button that moves it on (submit, submit again, review and sign for the assigned person, cancel). It calls the same workflow service as before, so role, separation-of-duties, markings checks, locking and audit are unchanged. The tab keeps the progress map and the signature history. Pages without a header (holds, dispositions, schedules, list entries) keep the panel's own button.
- **One task page.** `/tasks/{id}` carries the summary the phone page had (what is signed, the submitter's note, the content hash) and large decision buttons at phone width. My tasks shows cards at phone width. `/approve` and `/approve/{id}` redirect, so saved links work.

## Consequences

- Tests that used the phone pages now use `/tasks`; the review tab no longer has a start button.
- Visual only: no access rule, marking, banner or audit entry changed.
