-- RESULT (2026-08-17, run by the requester as DBA): step 1 returned **0 and 0**. Nothing was deleted;
-- step 2 was never run. The repair this file describes turned out to be unnecessary — kept as the
-- record that the check was made, and as the shape for the `org_unit` / `function` cleanup that
-- backlog 0.4b will owe. That zero does not mean the hole was imaginary: the pre-1c307a5 design could
-- leave an orphan whenever a first-version write failed, and its compensation was best-effort (it
-- logged `Log.Error` and continued). Zero rows means no failure ever outran that compensation on this
-- database — not that nothing could.
--
-- One-time data repair, NOT a migration: it changes no schema, is not versioned by `schema_version`,
-- and must never be re-run as part of a deploy. It exists because until 1c307a5 the role save path
-- minted an identity on a separate connection BEFORE the transaction that wrote its first version, so
-- a failure of that write left a header row with zero versions. That mechanism is gone
-- (docs/design-effective-period.md §7, backlog 0.4) — this file removes only what it already left behind.
--
-- ⚠️ RUN ONLY WITH NOBODY IN THE APP. This is not caution, it is correctness: `org_unit` and `function`
-- still mint the old way (backlog 0.4b), and for any table a live client sits, for a few milliseconds,
-- between its own mint and its first version insert. A header in that window is indistinguishable from an
-- orphan — it has no creation timestamp to filter on and holds no lock a reader could wait for — so
-- running this against a live system deletes another user's row mid-save.
--
-- Order: run step 1, read the numbers, then run step 2 only if step 1 found anything.

-- Step 1 — how many orphans exist. Expect small numbers; a large one is worth investigating before
-- deleting, because it would mean saves were failing far more often than anyone reported.
SELECT 'role' AS header_table, COUNT(*) AS orphan_headers
FROM role r
WHERE NOT EXISTS (SELECT 1 FROM role_version rv WHERE rv.role_id = r.id)
UNION ALL
SELECT 'role_permission', COUNT(*)
FROM role_permission rp
WHERE NOT EXISTS (SELECT 1 FROM role_permission_version rpv WHERE rpv.role_permission_id = rp.id);

-- Step 2 — delete them. `NOT EXISTS` rather than `NOT IN`: a NULL in the subquery makes `NOT IN` match
-- nothing at all, silently doing zero work and looking like success.
DELETE r FROM role r
WHERE NOT EXISTS (SELECT 1 FROM role_version rv WHERE rv.role_id = r.id);

DELETE rp FROM role_permission rp
WHERE NOT EXISTS (SELECT 1 FROM role_permission_version rpv WHERE rpv.role_permission_id = rp.id);

-- Step 3 — re-run step 1. Both counts must be 0.
--
-- Not covered here: `org_unit` and `function`, which still produce orphans until backlog 0.4b ships.
--
-- SUPERSEDED 2026-08-17 (same day, later): `function` migrated in backlog 0.4b-A -- its create now
-- mints inside the transaction, so it no longer produces orphans, and its own orphan count was
-- measured at 0, so it never needed a cleanup file of its own. `org_unit` is the only table still
-- minting ahead of the transaction. Read every `org_unit`/`function` pair above as `org_unit` alone.
-- Nothing above this note was edited: this file records a run that actually happened, and amending
-- its wording would falsify that record. (An earlier attempt at this note DID rewrite the line above
-- while claiming it had not -- caught in review. Appending is the whole point.)
-- Cleaning them now would buy nothing — they would start accumulating again the same day.
