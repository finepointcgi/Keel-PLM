# ADR-0053: Cross-document compare and redline review

Status: accepted (increment 9.5, 2026-10-30). Builds on ADR-0002 (master / revision / iteration), ADR-0004 (markings and access), ADR-0005 (audit), ADR-0015 (vault),
ADR-0026 (scanning and quarantine), ADR-0030 (comments and notifications), ADR-0033 (document review), ADR-0034 (accessibility) and ADR-0046 (model versioning). Assumptions
are A70 to A72 in [assumptions.md](../assumptions.md); the details are in [increments/9.5-redline.md](../increments/9.5-redline.md).

## Context
Reviewers of a specification, a work instruction or a bill-of-material spreadsheet ask "what changed?" about two versions of one document, or about two documents that should
agree. Model versioning (ADR-0046) answers it for CAD and mesh files by numbers and pictures. Text documents had only a download link per version. A redline must also be a
place to talk: reviewers pin comments on a change, the author answers, and the thread is closed when the change is accepted. All of it concerns controlled data from TWO
documents at once, which is where a careless feature leaks.

## Decisions
1. **Any two document versions, one rule for access.** A comparison names two (document, version) pairs: two versions of one document, or any two documents. The caller must
   be allowed to read BOTH. If either is not readable the answer is the same 404 as for a missing document, which side refused is not disclosed, and the denial is audited on the
   document that refused. Reading the CONTENT of a comparison needs permission to download both (viewing is downloading, VWR-1); a caller who may only read gets the facts table.
2. **Text is compared word by word, everything else honestly.** `Keel.Documents.Compare.TextExtractor` reads plain text and Markdown (UTF-8 or UTF-16, binary refused), DOCX and
   XLSX (ZIP of XML, bounded unpacking, DTDs prohibited, no external entities) and PDF (PdfPig 0.1.16, Apache-2.0, managed code, added to `Keel.Documents`; no OCR, so a scan is
   reported as having no text layer). For any other format, for two formats that differ in kind, for files over 50 MB and for unreadable files the result is a metadata comparison
   (name, type, size, SHA-256, version, state, who and when, change note) with a plain sentence beginning "Content not comparable" and the reason. Nothing is guessed. Identical
   bytes are reported as identical without reading anything.
3. **The diff.** `WordDiff` runs a Myers O(ND) diff over whole lines, then over words and punctuation marks inside each stretch that differs. Spacing and line breaks are not
   changes (a reflowed PDF is not an edit); the right-hand whitespace is kept for display. A change is a maximal run of insertions and deletions between two unchanged words,
   numbered from 1 in reading order; its deletion is shown before the insertion that replaces it. A stretch needing more than 4,000 edits is shown as one replacement and the
   result says it is coarse. More than 600,000 tokens on a side is refused. The result is deterministic, which is what lets a comment point at "change 7".
4. **Nothing is stored but comments.** The comparison is recomputed from the vault bytes (verified against their SHA-256) on every view. There is no cache of document text outside
   the vault, so there is no second copy that could outlive a disposal, a quarantine or a markings change.
5. **Review comments (migration 0032, schema `rdl`).** `rdl.review` is one shared row per ordered pair of iterations (the two SHA-256 of the compared files, the diff and extractor
   versions, who started it; a trigger checks each side is an iteration of the stated document with that primary file hash; immutable). `rdl.annotation` holds comments anchored to
   the n-th change of the diff, with a digest of that change: a thread is a root plus replies (one level, same change), a root can be resolved and reopened, nothing else changes,
   nothing is deleted (triggers; the app role has no UPDATE on a review and no DELETE anywhere). If a future diff version no longer has that change the comment is shown as detached
   and is never drawn on another change. There are no marking columns: reviews and comments inherit dynamically from BOTH documents. They are written and read only for a caller who
   can read and download both, and the review list joins both masters through `AccessPolicy.BuildReadFilter` (SQL oracle test over every subject and marking pair).
6. **Comments flow into the existing review.** Writing needs a role of viewer or higher (auditors and service accounts read only). Resolving needs to be the author of the thread
   or able to modify or sign the right-hand document. Watchers of the right-hand document get a `redline_comment` notification (pointer only, link `/compare/review/{id}`, which
   re-authorizes on open); the author of a thread is told of replies. The document's Review & release tab counts the open threads of its redlines and links to them. Open threads do
   not block a release (A72): the release decision stays with the workflow engine and the signatures.
7. **Reports.** The comparison exports as HTML (one self-contained file, no scripts or external resources) or PDF (PDFsharp with the embedded DejaVu faces). Both carry the aggregate
   banner of both documents at the top and bottom (every page of the PDF; every printed page of the HTML), every notice either document requires, both versions with their SHA-256,
   the counts, the redline, the change list and the threads. Insertions and deletions are marked by more than colour: underline or strike-through plus visible [+ +] and [− −]
   marks; on screen also text for screen readers. A very long text is shown in the PDF as changes with 30 words of context each (and says so); the HTML shows all of it.
8. **Interface.** `/compare` (picker, then the result), `/compare/review/{id}` (resolves a review pointer after the checks), a Compare tab on every document, a navigation entry and
   a palette action. Inline and side-by-side views, previous and next change by button and by the N and P keys on the focused redline, a live status ("Change 3 of 12"), a change
   list, and the thread panel. Colours come from existing tokens only; the banners are the page's own and are the aggregate of both documents.
9. **Audit.** `document.compare` and `document.redline_export` (views), `document.compare_versions` (view), `document.redline_comment` and `document.redline_resolve`
   (state-changing, in the activity feed). One event per distinct document of the pair, so each document's trail shows who compared or discussed it. Payloads carry version
   labels, checksums, counts and ids, never document text or comment text. The activity sentences name no other document: an event written on both documents must not tell a reader
   of one the number of the other.

## Consequences
- No new service and no new store of document text; the Cloud Run demo gets the feature unchanged.
- Comparing two documents of different markings produces output marked with the union, so the output is as restricted as the most restricted input.
- A reader who can read the right-hand document but not the left one gets a notification pointer that opens a 404 page (no content, no name of the other document).
- DOCX comparison ignores headers, footers, text boxes, footnotes and comments; PDF reading order follows the file's drawing order. Both are stated in the chapter.
