# Design: inbox sync state, Gmail folders and account identity

**Status:** implemented 2026-11-09, uncommitted. The owner agreed decisions 1 to 3 the same day. Decisions 4 to 6 were recommended by Claude and built; the owner confirmed them on 2026-11-10 (quarantine of unparseable files, failing the run on I/O errors and on a lock held over 5 seconds, and the account and tenant in the state key). Written from reading `OpenTail.Dolphin` at `e617634`. Source: the external review of 2026-11-09 (issues 1 to 5), with two further findings from reading the code (marked "found while reading"). Vendor facts were checked in Google's Gmail API reference on 2026-11-09; Graph delta behaviour is not relied on by this design.

**Where it fits:** the three inbox adapters and their state stores (`OpenTail.Dolphin.Inbox.Graph`, `.Gmail`, `.Imap`), a shared state-file helper in `OpenTail.Dolphin.Files`, and the docs. The core `FileSyncCursorStore` is not changed (see "Not in this design").

## The problems

1. **State updates can be lost.** The Graph, Gmail and IMAP stores each read the whole file, change one entry and write it back, with no lock. Two saves that overlap erase one of them. `FileSyncCursorStore` has the lock; the inbox stores do not.
2. **Found while reading:** a failed read inside a save makes the store write back only the new entry. In all three stores, `SaveAsync` catches a read error and starts from an empty dictionary, so every other mailbox's cursor is lost.
3. **Graph repeats an oversized page.** When a page is longer than the room left, the adapter saves the page's own link. The next run reads the same page and emits the same first messages. On the first page the link is null and nothing is saved.
4. **Gmail ignores folders.** The listing filters by label only for `INBOX`. The history path accepts every added message for other folders. Requesting `Sent` can return inbox mail.
5. **Found while reading:** Gmail's state is keyed by the user's email alone, so INBOX and Sent share one history cursor.
6. **IMAP state is not account-isolated.** The state key is the folder name. Two accounts sharing a state file collide on `INBOX`. The stable message id omits the port. Graph's state key omits the Entra tenant.
7. **Docs are out of date:** the crawler's private-address blocking, the calendar write gate, and the status of SFTP, Dropbox and Drive.

## Contracts this must satisfy

- **`IInboxAdapter.FetchRecentAsync(InboxFetchOptions, ct)`** yields `InboxEmailRecord`. `InboxFetchOptions.MaxMessages` is the cap. After this change it is exact for Gmail and IMAP, and for Graph it is a soft cap that stops at a page boundary (decision 1).
- **`IInboxSyncCommit`** (`HasPendingSyncState`, `CommitSyncStateAsync`) and **`InboxSyncCommit`** (`AfterEnumeration`, `Manual`). Unchanged. A store passed without a commit mode is still refused (`InboxSyncCommits.Require`).
- **`ISyncCursorStore`** is not used by these stores and is not changed.
- **`AtomicFile.WriteAllTextAsync`**: a reader sees the old or the new content, never half of either. The new helper relies on it.
- **Public store APIs.** Callers outside the repo: none (checked across `split`: the Host and the workbench do not reference these stores). Callers inside the repo: the adapters and the tests `AtomicFileTests`, `InboxSyncTests`, `GmailSyncTests`, `ImapSyncStateTests`.
- **Stable message ids** (`ImapMessageLookup.StableId`, `TryParseStableId`, `FindExactAsync`, `StableUid`). Ids for the standard ports 993 and 143 must not change, so that downstream stores keep matching messages that have no Message-ID header.
- **`ProviderMessageId`** for Graph and Gmail is the provider's id. Unchanged.
- **`ImapInboxAdapter.SelectUids`** (oldest-first for incremental runs, newest-first for a first run). Unchanged.

## Vendor facts

Checked in Google's Gmail API reference on 2026-11-09:

- **`users.history.list`, `labelId`**: "Only return messages with a label matching the ID." It takes one label. The reference does not say how it applies to `messagesAdded` records. **Decision:** the history path keeps filtering on each message's `labelIds` after fetching it, and does not use `labelId`.
- **`users.messages.list`, `labelIds[]`**: "Only return messages with labels that match all of the specified label IDs." The listing uses one label, so "all" and "any" are the same here.
- **System label ids in `labelIds[]`** (INBOX, SENT, DRAFT, SPAM, TRASH) are not listed in the reference. INBOX already works in the current code, but the others are **to verify against a live account**. The testing documentation says the inbox adapters are not verified against live accounts, so this stays open.
- **`history.list` with an invalid start id** returns HTTP 404, and the adapter already runs a full sync then. Unchanged.

## Existing behaviour, branch by branch

**Graph, `FetchRecentAsync`:**

| Branch | Today | Under this design |
|---|---|---|
| No saved state, first page | Fetched with `FirstPageAsync`; if the page is longer than the room, nothing is saved | Same request. The whole page is emitted; its next or delta link is saved |
| Saved state with a delta link | Fetched with `SendRawDeltaPageAsync` | Same |
| Saved state for the inbox under the old per-user key | Read as a fallback | Not read (decision 3): one full run |
| Page with a delta link | Saved as the resume link, loop stops | Same |
| Page with no next link and no delta link | Loop stops, nothing saved | Same |
| Emitted count reached `MaxMessages` | Saves the next link, loop stops | Same, checked at the page boundary |
| Page longer than the room left | Saves the page's own link, stops (**bug**) | Emits the whole page, then continues as above (decision 1) |
| `@removed` entries | Skipped | Same |

**Gmail, `FetchRecentAsync`:**

| Branch | Today | Under this design |
|---|---|---|
| Listing, first run, `Folder == "INBOX"` | `LabelIds = ["INBOX"]` | `LabelIds = [label]` for any supported folder |
| Listing, first run, other folder | `LabelIds = null`: searches all mail (**bug**) | Same as above; unsupported folders are refused |
| History, `Folder == "INBOX"` | Accepts a message only if its labels contain INBOX | Accepts a message only if its labels contain the folder's label |
| History, other folder | Accepts every added message (**bug**) | Same as above |
| Unsupported folder name | Accepted | Refused with `ArgumentException` before any request (decision 2) |
| State key | The user's email, for every folder (**bug**) | The user's email for INBOX (unchanged, so no re-read); `email|LABEL` for other folders |
| Backfill, 404 full sync, overlap | As before | Unchanged |

**IMAP, `FetchRecentAsync` and identity:**

| Branch | Today | Under this design |
|---|---|---|
| State load and save | Keyed by folder name (**bug**) | Keyed by account and folder: `username@host:port|FOLDER` |
| Stable id, port 993 or 143 | `imap-uid:user@host/folder/validity/uid` | Unchanged |
| Stable id, any other port | Same as above; port omitted (**bug**) | `imap-uid:user@host:port/folder/validity/uid` |
| `TryParseStableId` and `StableUid` | Match the same prefix | Match the same account part, which now depends on the port |
| `FindExactAsync` by Message-ID | Unchanged | Unchanged |

**State files, all three:** a missing file is empty. A save reads, changes one key and writes. Any read error counts as an empty file (problem 2).

## Decisions

Agreed by the owner, 2026-11-09:

1. **Graph emits the whole oversized page.** `MaxMessages` becomes a soft cap at a page boundary in that rare case. No position is kept inside a page, because the order of Graph's delta pages is not something this design depends on.
2. **Gmail folders are the system labels only:** INBOX, SENT, DRAFT, SPAM and TRASH, matched case-insensitively. Any other folder name is refused with `ArgumentException` before any request. Custom labels need a lookup of label names to ids and are not built.
3. **Changed identity keys get no fallback read.** A key that changes means one full run for that account and folder. Account identity goes into the state key, not the file name, because file paths are deployment configuration and keys change without it. Gmail's INBOX key already identifies the account (the email address), so INBOX keeps its key and does not re-read.

Recommended, for the owner to confirm:

4. **A shared state-file helper, `LockedJsonState`,** used by the three stores. Reads and writes happen under an in-process semaphore and a lock file, as `FileSyncCursorStore` does. A save reads, changes one key and writes, under the lock.
5. **An unreadable state file is never overwritten.** Two cases:
   - *The file exists but cannot be read* (an I/O error, such as a sharing violation): the operation throws. Nothing is written.
   - *The file exists and its JSON cannot be parsed:* it is moved to `<file>.corrupt-<UTC timestamp>` and treated as empty. Nothing is lost, the file is kept for inspection, and sync continues with a full run for the affected entries.
6. **`MaxMessages` for Graph** is documented as a soft cap at a page boundary.

## Cost of the changes

- **Graph:** each mailbox folder is re-read once, from the start of the folder, unless the caller sets `Since`. Some deployments will see a large one-off read.
- **IMAP:** each account and folder is re-read over the 30-day initial lookback. Messages with a Message-ID dedupe by that id; messages without one keep their stable id on the standard ports.
- **Gmail:** INBOX is not re-read. Sent gets its own state on its first run, which reads the newest `MaxMessages`.
- **Found while reading (operational):** the review's "Sent" problem means existing deployments may already hold inbox mail labelled as Sent in their downstream store. This change does not fix that data. The owner should decide whether to clean it up.

## Plan

**Phase 1: the shared helper and the three stores.** Add `LockedJsonState` (Files). Rewrite the three stores on it: keys as in decisions 1 to 3, and the unreadable-file rules of decision 5. Update the callers in the adapters and the tests. The new tests (below) must fail on the current code before the change is made.

**Phase 2: Graph pagination.** Emit the whole page; keep the existing link handling. Replace the test `A_page_longer_than_asked_is_read_again_next_time_rather_than_cut`, which checks only the first run, with a two-run test.

**Phase 3: Gmail folders.** Label mapping and refusal; the listing and the history filter use the folder's label; keys per folder. The Gmail test fake gains labels per message, honours `labelIds` in listing, and returns history records for messages added.

**Phase 4: IMAP identity.** Account in the state key; port in the stable id for non-standard ports only.

**Phase 5: docs.** Correct the crawler sentence in `safety-and-credentials.md`, the calendar sentence in `architecture.md`, and the status of SFTP, Dropbox and both Drive projects in `STATUS.md`. Their statuses must match what the code has: the SFTP project has one source file and the Dropbox project three, so "started", not "done".

## Tests

- **Concurrency:** many saves at once, across several store instances on one file, each with a different key. Every key must be present afterwards. Repeat for Graph, Gmail (two folders) and IMAP (two accounts with the same folder name).
- **Unreadable file:** a corrupt file is moved aside with its original content, the load returns no state, and a later save works. A file that cannot be read because of an I/O error is not moved and not overwritten.
- **Graph:** two runs over a page longer than `MaxMessages`: the first emits the whole page and saves the next link; the second continues from that link. Also: a first page longer than the cap saves a link.
- **Gmail:** a `Sent` listing requests `labelIds = [SENT]`; an inbox-only message is not returned for `Sent`, either in the listing or in history; unsupported folders are refused; INBOX state keeps its key; `Sent` state is kept apart from INBOX state.
- **IMAP:** two accounts with the same folder name keep separate state; stable ids for ports 993 and 143 are unchanged; a non-standard port changes the id, and `TryParseStableId` reads it back.

## Not in this design

- `FileSyncCursorStore` (core) has the same overwrite-on-unreadable-file behaviour in its `SaveAsync`, through `ReadAsync` treating a JSON error as an empty dictionary. Changing it changes self-healing for every file connector, so it needs the owner's decision. Recorded here so it is not forgotten.
- Custom Gmail labels, Graph folders by display name, and any change to the Host.
- The live-account checks listed under "Vendor facts".

## Implementation record (2026-11-09)

**Changed:**

- `OpenTail.Dolphin.Files/LockedJsonState.cs` (new): the shared state file, read and changed under one lock. The lock file is deleted on release.
- `OpenTail.Dolphin.Inbox.Graph`: `GraphSyncState` gains the tenant and is keyed by tenant, mailbox and folder. `GraphInboxAdapter` emits a whole oversized page, saves the next link after it, and no longer reads the old per-mailbox key.
- `OpenTail.Dolphin.Inbox.Gmail`: `GmailFolders.cs` (new) maps the five system labels and refuses other folders before any request. `GmailSyncState` gains a label; INBOX keeps its key (the address), other labels add the label. `GmailInboxAdapter` filters the listing and the history by the folder's label.
- `OpenTail.Dolphin.Inbox.Imap`: `ImapSyncState` gains the account and is keyed by account and folder. `ImapMessageLookup` adds the port to the stable id for ports other than 993 and 143.
- Tests: `InboxSyncTests.cs` (the pagination test rewritten as a two-run test; the legacy-key test now asserts the old key is not read), `GmailSyncTests.cs` (fake extended with labels and history records; 4 new tests), `ImapSyncStateTests.cs` (rewritten; 3 new tests), `AtomicFileTests.cs`, new `InboxStateStoreTests.cs` (5 tests) and `ImapAccountIdentityTests.cs` (6 test cases).
- Docs: `safety-and-credentials.md` (the crawler's private-address check is described as it is), `architecture.md` (calendar writes are behind `CalendarWritePolicy`), `STATUS.md` (SFTP, Dropbox and both Drive connectors are "in progress, unit-tested, not live").

**Verified here:**

- The connectivity solution builds with warnings as errors.
- The test suite: 1410 total, 0 failed, 8 skipped. The baseline before the change was 1391, 0 failed, 8 skipped, so the 19 new tests all pass.
- Mutation check: with the lock removed from the update path, both overlapping-save tests failed. The lock was restored and the tests pass again.

**Not verified:**

- Only the lock was checked by a mutation. The other new tests were run against the new code, not against the old code.
- Against live accounts: whether Gmail accepts SENT, DRAFT, SPAM and TRASH in `labelIds`; how long a Graph re-read takes; whether downstream stores dedupe re-read messages by their ids.

**Effects the owner should know about:**

- **Graph:** after deploy, each mailbox folder is re-read once from its start, unless the caller sets `Since`.
- **IMAP:** each account and folder is re-read over the 30-day initial lookback. Messages with a Message-ID dedupe by that id; messages without one keep their stable id on the standard ports.
- **Gmail:** INBOX is not re-read. Sent gets its own state on its first run, which reads the newest `MaxMessages`.
- **Gmail data already synced:** deployments that synced the folder "Sent" before this change may hold inbox mail labelled as Sent in their downstream stores. This change does not repair that.
- **Failures are louder:** a state file that cannot be read now fails the run. Before, it was treated as empty, which meant a silent full re-read and an overwrite. A damaged file is moved to `<file>.corrupt-<timestamp>`, and that run is a full one. A lock held for more than 5 seconds raises `TimeoutException`.
- **API:** the three stores' method signatures changed to take the tenant, account or folder. Nothing in the repo outside the adapters and tests calls them, and the Host and the workbench do not reference them. Any external caller will need the same change.

**Not in this change:**

- `FileSyncCursorStore` (core) still replaces a damaged file on save, as described under "Not in this design". Changing it changes self-healing for every file connector, so it needs the owner's decision.
- Custom Gmail labels.
