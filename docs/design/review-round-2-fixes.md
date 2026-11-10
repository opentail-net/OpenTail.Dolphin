# Review round 2: fixes and open decisions

**Status:** the fixes below are built, uncommitted, on top of `e617634`. The suite passes: 1,439 tests, 0 failed, 8 skipped. This is a record of a static review and the changes that followed, not a plan; it has no contracts section of its own because each change is local to the code it touches.

## Findings

| # | Finding | Status | Tests |
|---|---|---|---|
| 7 | IMAP: a changed UIDVALIDITY kept the old highest UID, so new messages below it were skipped | Fixed. The starting highest UID is zero unless the run is incremental (same UIDVALIDITY, highest seen above zero) | `ImapUidValidityTests` (4). The helpers are unit-tested; no live IMAP server was used |
| 9 | Memory: an always-write word (decision, approved, incident) overrode the sensitive-content rejection; scrubbing covered only assignments and bearer tokens | Fixed. Values of kinds that are never stored (private keys, SSNs, Luhn-valid card numbers, provider keys, JWTs) are refused before any signal is read, in content and summary. The keyword rule and the assignment scrubbing are unchanged | `MemoryNeverStoredValuesTests` (14 cases), and a pipeline test that nothing is persisted for a refusal |
| 10 | Memory operator: UpdateRetention set Expiring without an expiry date | Fixed. Expiring needs `ExpiresAtUtc` (new optional request field) in the future; the resulting record is validated before success is reported | `MemoryOperatorControlServiceTests` (updated) |
| 11 | Memory operator: ReEmbed wrote a zero-dimension placeholder or changed only the revision, and reported success | Fixed by refusal. No embedding service exists in this service, so ReEmbed refuses and changes nothing. A re-embedding worker is still to be built | `MemoryOperatorControlServiceTests` (updated) |
| 8 | Dropbox: no filter fingerprint, so a changed filter did not list excluded files again. Object storage: the fingerprint omitted `ReadColdStorage` | Fixed. Dropbox cursors are saved as `<fingerprint>:<cursor>`; a different fingerprint, or a plain cursor from before, emits `Reset` and lists the folder again. Object storage includes the cold-storage setting | `DropboxConnectorTests` (2 new, 3 updated), `ObjectStorageConnectorTests` (1 new) |
| 12 | Gmail: Google services were not disposed (calendar, sender, thread fetcher) | Fixed. Services the adapter builds are disposed with the operation (`ServiceLease`); a calendar service the caller supplies is not | `GmailServiceLeaseTests` (2) |
| 13 | Social connectors returned invented JSON for an item that was no longer cached | Fixed. A miss is a faulted `KeyNotFoundException`. Conformance check 3 is no longer skipped for the four connectors | `SocialConnectorsConformanceTests` (4 pass with check 3 on) |
| 14 | Memory maintenance ignored the request's workspace and project; groups and duplicates could combine two workspaces | Fixed. Records are limited to the request's scope; every group key carries the workspace | `MemoryMaintenanceScopeTests` (4) |
| Queue | A handler that ignored cancellation and returned successfully after the run-time limit, but before the grace period ended, completed the job | Fixed. The worker records whether the handler ended before the limit; a later end takes the timeout path and the job is retried | `WorkQueueWorkerTests` (1 new) |
| Social HTTP | Responses in the discovery methods were not disposed | Fixed with `using` at each acquisition | Covered by the social tests |

## Open: quarantine of a damaged inbox state file

The reviewer's point stands for the current behaviour. When a state file cannot be parsed, it is moved to `*.corrupt-<timestamp>` and the run is a full run. For Gmail and IMAP a full run is their first-run behaviour: the newest `MaxMessages` and no backfill. Older mail can then be skipped for good, and the new cursor is saved. The owner confirmed quarantine-and-full-run on 2026-11-10, but the reviewer recommends the other option: refuse the run, keep the file, and make the reset an explicit operation. That needs the owner's decision; nothing has been changed here.

## Decisions taken without the owner

- **Never-stored values:** the Luhn check accepts any 13-to-19-digit number that passes it, so a long order number can be refused. The provider and JWT patterns cover known formats only.
- **Residual gap in scrubbing:** a secret written in free text with no label, such as `password is hunter2`, is not detected. With an always-write word it is accepted unscrubbed, as before.
- **ReEmbed:** refusal rather than a real re-embedding. A worker that calls the embedding service and updates the vector and its model together is still to be built.
- **Dropbox:** a plain cursor saved before this change triggers one full listing.
- **Social:** the cache is still consumed on fetch, so a retry of a fetch after a failed one finds nothing. Discovery has to run again first, which is what the error message says.
