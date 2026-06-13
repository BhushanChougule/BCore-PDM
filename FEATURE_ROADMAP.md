# BCore PDM — Feature Roadmap (Gap-Analysis Build Plan)

This is the implementation plan for closing the PART-2 feature gaps and making
BCore PDM best-in-class for a 5–10 seat SOLIDWORKS shop. It sequences the work
into focused, stackable PRs and records cross-impact so each change is made with
its effect on the rest of the system in mind.

## Ground rules established by analysis

- **Build only on a merged baseline.** Eight hardening PRs (C–J, #52/#56–#62)
  are open. Each is individually clean against `main`, but they overlap on the
  core files (DatabaseManager touched by 6, VaultManager by 3, TaskPaneControl
  by 5), so they must be **merged in order with rebasing**:
  `52 → 56 → 57 → 58 → 59 → 62 → 60 → 61`. Feature PRs land *after* this set so
  they don't collide with in-flight fixes.
- **Every core file is touched by some pending PR**, so a feature touching
  DatabaseManager / VaultManager / TaskPaneControl / VaultDashboardForm will
  need a small rebase. The mitigation is to keep features as **new files** with
  the **smallest possible touchpoint** in existing files (e.g. Feature 1 adds
  two new files and a single ~10-line call site in ReleaseFile).
- **No build/test here.** This repo is .NET Framework 4.8 + SOLIDWORKS COM; it
  can only be compiled and exercised on a SOLIDWORKS workstation. Every feature
  PR ships with a concrete on-machine test plan and follows the existing
  conventions (DPI `S()`, font disposal, degraded-lock rules, audit logging,
  one-form-one-file) so the on-machine pass is a verification, not a debug.

## Sequenced feature PRs

Priorities mirror the PART-2 table. "Depends on" lists the *pending* PR a feature
builds on (so it rebases cleanly once that merges).

### Phase 1 — Traceability & lifecycle (the biggest functional gaps)

1. **As-Released Baselines** — *DONE in this PR.* Persist the exact resolved
   child file/revision set at every assembly release (`<Baselines>` in
   vault.xml) + a read-only viewer from the dashboard. Closes the gap the table
   calls *"the biggest functional PDM gap."* New files: `BaselineManager.cs`,
   `BaselineViewerForm.cs`. Touchpoints: one call in `ReleaseFile`, two DB
   methods, one dashboard menu item. Depends on: nothing hard (rebases over #56
   which splits ReleaseFile into a `*Core`).

2. **Reason-for-change capture + audit "why".** A required reason prompt on
   Release / New Revision / Rollback / Remove, stored on the history entry and
   the baseline, surfaced in the Audit Report. Closes *Audit "why"* and feeds
   *Change management*. Touchpoints: `VaultManager` flow heads, `SetFileStatus`
   note, `AuditLogger`. Depends on: **#56** (operation `*Core` split is the
   natural place to inject the prompt), **#58** (audit durability/escaping).

3. **Obsolete lifecycle state.** A `Status = Obsolete` that blocks new use
   (can't be added to assemblies / released) but preserves history and stays
   referenceable — distinct from Remove-from-Vault (which scraps). Touchpoints:
   `SetFileStatus` validation, `StatusColor` (dashboard/search/audit), release &
   save gates. Depends on: **#62** (status reads consolidated in
   `GetActiveFileInfo`), **#60** (status colour paths).

### Phase 2 — BOM & structure intelligence (reuses Phase 1 dependency walk)

4. **Where-used index.** Persist parent→child edges on assembly save so
   `GetParentAssemblies` is an O(1) DB read, not a disk re-scan; add a
   "Where Used" view. Shares the dependency-walk primitive with Feature 1.
   Touchpoints: `UpsertFile` (write edges), new query, dashboard menu item.
   Depends on: **#62** (the search/refresh one-load work), **#59** (`SetOrAdd`).

5. **Multi-level / indented BOM + BOM-in-DB + BOM compare.** Extend
   `ExportManager.ExportBom` to recurse (it already resolves lightweight
   components), store the BOM with the baseline (Feature 1 already captures the
   resolved set — promote it to a true indented BOM), and add a rev-to-rev BOM
   compare in the baseline viewer. Depends on: **#57** (BOM `FileSafe`/exclude
   fixes), Feature 1.

### Phase 3 — Findability & numbering

6. **Property-wide indexed search.** Extend `SearchFiles` + the dashboard to
   match Material, DrawnBy, PartType and date ranges (the dashboard column
   filters already do part of this). Depends on: **#62** (search refactor),
   **#59** (culture-safe matching).

7. **Per-division auto-numbering suggester.** Suggest the next part number per
   division from existing PartNos (cheap, additive; the docs call full
   auto-numbering unfeasible, a *suggester* is not). New helper + a PropertyForm
   affordance. Depends on: nothing hard.

### Phase 4 — True check-out & notifications

8. **Auto-lock-on-open check-out.** Promote soft presence (OpenSessions) to a
   real exclusive edit reservation: reserve on open, release on close, with a
   read-only-borrow path for others. The infrastructure (OpenSessions, LockFile,
   operation claims from #56) already exists — this is enforcement + UX.
   *Highest-risk feature: it changes the daily editing contract, so it ships
   behind a vault.xml toggle, defaulting OFF, and lands last.* Depends on:
   **#56** (operation claims), **#61** (roles for override rights).

9. **Subscriptions / targeted notifications.** "Notify me when X is released"
   and parent-assembly-owner notification on child rev. Extends `EmailManager`
   (now async) + a `<Subscriptions>` section. Depends on: **#61** (async email +
   live Master list).

### Operational (not code in this repo, but required for "best in industry")

- **NTFS ACLs + roles.config** for real RBAC — **#61** ships the roles file and
  the SECURITY.md ACL table; the ACLs are an IT rollout step, tracked there.
- **ERP hand-off contract** — once the indented BOM (Feature 5) and baselines
  (Feature 1) are in the DB, a stable CSV/JSON drop-folder export is a thin
  addition.

## Explicitly deferred (per PART-2 "Nice to have" / out of scope)

Multi-site collaboration, supplier portal, PDF redline/markup, full offline
conflict resolution, and a network API surface — defensibly out of scope for a
single-share, 10-seat deployment. Reporting/analytics is already best-covered
(dashboard + audit report).
