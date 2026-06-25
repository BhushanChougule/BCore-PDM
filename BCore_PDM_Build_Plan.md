# BCore PDM — Build Plan (→ 50%, then → 80%)

**Paste this into the Claude Code project chat.** It is an instruction set, not background reading.

---

## How to work this plan (read first)

- **Goal:** raise BCore PDM's feature coverage from **28% → 50%** (Phase 1), then push toward **80%** (Phase 2), measured against the Super PDM feature list.
- **One task = one PR.** Work the tasks **in order**, one at a time. Open a branch `feature/<short-name>`, implement only that task, open the PR, then **stop** — I will review, test, and merge before you start the next.
- **Do not combine tasks.** Keep PRs small and reviewable.
- **Follow the repo's existing rule:** update `CLAUDE.md` in the same PR whenever behaviour or structure changes. Add/extend tests where the project already has them.
- **Stay on BCore conventions** (all already established in `CLAUDE.md`): one-form-one-file, DPI scaling `S(v)=v*_scale`, the house palette/title-bar style, non-fatal best-effort error handling (swallow + audit, never block a save/release), **no new NuGet/native dependencies** (vendor a managed DLL like PdfSharp/SQLite if unavoidable), `InvariantCulture` for all dates (MM/dd/yyyy), and RFC-4180 CSV with the formula-injection guard.
- Each task gives: **Goal / Current / Build / Acceptance / Marks-Done** (the checklist row I'll flip after merge).

---

## Status corrections (apply these to the tracker — no code needed)

Two items I had as "partial" are actually complete; treat them as **Done**:

- **Activity feed → Done.** The Audit Report (`AuditReportForm`) is already a full chronological, filterable, paginated event log with quick filters — that *is* an activity feed.
- **BI export → Done.** CSV everywhere + the multi-sheet `XlsxWriter` export across dashboard / audit / baseline already covers data export for BI/Excel.

One nuance to leave as-is:

- **Duplicate detection stays partial.** Your filename + part-number duplicate blocking (Rule 2.6 / `FindFileNameConflict` / intra-file PartNo check) is complete and solid. The remaining piece — surfacing *near-identical geometry* — is really a separate (novel) feature, so don't count it as done.
- *(Optional judgment call: "Get latest / specific version" is marked Done on the strength of rollback + released snapshots. There's no get-latest/sync because WIP is the canonical network copy, which is fine — leave it Done unless you later add a cache.)*

Net effect of the two corrections: **30% before any code.**

---

## PHASE 1 — reach 50%

This is the fast phase: most of it is **finishing things you've already half-built**. Tier 1 is the high-value quick wins; Tier 2 closes out the rest of the partial items to cross 50%. You can pause and ship at 50% before starting Phase 2.

### Tier 1 — fast, high value (do these first)

**PR1 — Sheet-metal calculated fields**
- Goal: auto-derive the numbers a sheet-metal shop always wants.
- Current: `PartWeight` is auto-filled from mass properties; sheet metal is detected via `GetFeatures(false)`; flat-pattern DXF export exists.
- Build: on save of a sheet-metal part, auto-fill config-specific custom props `FlatLength` / `FlatWidth` (flat-pattern bounding box) and `CutLength` (flat-pattern perimeter). Reuse the existing SM detection. Non-fatal.
- Acceptance: SM parts show populated values; non-SM parts skipped; a read failure never blocks the save.
- Marks Done: **Calculated fields**

**PR2 — KPI tiles on the Vault Dashboard**
- Goal: turn the dashboard summary strip into real KPIs.
- Current: `VaultDashboardForm` has a clickable Total/WIP/Released/Locked/Broken summary strip; `GetAllFiles` + `audit.csv` hold the data.
- Build: add tiles for avg WIP age (days), releases in the last 7 days (from audit), open requests, and broken-ref count, alongside the existing counts.
- Acceptance: tiles render, recompute when filters change, and match a manual count on a sample.
- Marks Done: **Dashboards & KPIs**

**PR3 — Cycle-time metrics**
- Goal: show how long parts sit in each state.
- Current: dashboard has a WIP-Days column; `audit.csv` records Lock / Release / NewRevision timestamps.
- Build: compute average WIP→Released duration over a selectable window from the audit log; show it as a KPI tile or a small report.
- Acceptance: the figure matches a hand calc on a few parts.
- Marks Done: **Cycle-time analytics**

**PR4 — Affected-items on the release confirm**
- Goal: the Master sees impact before releasing.
- Current: `GetWhereUsedTopLevel` / `GetWhereUsedTree` already compute usage.
- Build: on the release confirm dialog, list the top-level products that contain the file. "No parents" when none.
- Acceptance: confirm dialog lists affected products for a child part; clean message when standalone.
- Marks Done: **Affected-items list**

**PR5 — Batch print released drawings**
- Goal: one click to print a set of released drawings.
- Current: `EXPORTS\PDF` holds released PDFs; `ExportManager` handles PDF I/O.
- Build: a dashboard multi-select action "Print released drawings" that sends the matching released PDFs to the default printer (skip + report any missing).
- Acceptance: selected files' PDFs print; missing ones are reported, not fatal.
- Marks Done: **Print automation**

**PR6 — Part-number generator**
- Goal: stop hand-typing part numbers; close three checklist rows at once.
- Current: `PartNo` is manual in `PropertyForm`; revisions already auto-increment (base-21).
- Build: a "Generate Part No" button in `PropertyForm` that issues the next number from a **per-division scheme** (prefix + zero-padded sequence) stored and **atomically reserved** in `vault.xml` so two engineers never collide. Scheme editable in a small admin spot.
- Acceptance: two concurrent requests get distinct numbers; the scheme is editable; a generated number passes the existing duplicate check.
- Marks Done: **Auto-numbering / serial generators**, **Auto file naming/numbering**, **Intelligent vs sequential numbering**

**PR7 — Thumbnails + preview on cards**
- Goal: make search/dashboard visual; closes three rows.
- Current: search and dashboard cards are text-only; released PDFs are generated.
- Build: extract the SOLIDWORKS embedded preview bitmap (fallback: render page 1 of the released PDF) and show a thumbnail on each card; click to open a larger preview.
- Acceptance: cards show images; a placeholder icon appears when no preview exists.
- Marks Done: **Thumbnail / visual results**, **Auto thumbnails**, **Office / PDF / image preview**

**PR8 — Saved searches + recent/favorites**
- Goal: quality-of-life on top of the search you already have.
- Current: quick search + `AdvancedSearchForm`.
- Build: persist named searches, a recent-files list, and a favorite/star toggle per user (in `vault.xml` or a per-user config); surface them in the search panel.
- Acceptance: a saved search re-runs; recent list populates on open; favorite toggles and persists.
- Marks Done: **Saved searches**, **Favorites / recent / bookmarks**

### Tier 2 — close out the partial items to clear 50%

Each is one PR. Reference the classes named in `CLAUDE.md`; same conventions and acceptance discipline as Tier 1.

- **PR9 — Engineering Change Order (ECO):** add an ECO record in `vault.xml` that bundles affected items + from/to revs + the captured reason + a link to the as-released baseline; a simple ECO form at release and an ECO list view. (Builds on `ReasonForChangeForm`, `BaselineManager`, `BaselineCompareForm`.) *Marks Done: Engineering Change Order/Notice*
- **PR10 — Engineering Change Request (ECR):** formalize engineer requests (`RequestRevision/Release/Unlock`) into an ECR record (type, description, affected file, requester) a Master reviews and converts to action/ECO. *Marks Done: Engineering Change Request (ECR)*
- **PR11 — Electronic signatures at release:** require the Master to re-enter their password (verified against a **salted hash in `vault.xml`** — NOT the deferred license system in `LICENSING.md`) and write a signature record (user, time, doc, rev). *Marks Done: Electronic signatures*
- **PR12 — Reference designators + units in BOM:** capture component reference designators (when present) + a unit field into the `<Components>` snapshot, BOM export, and baseline. *Marks Done: Qty, units, reference designators*
- **PR13 — Custom report builder:** a column-picker + save-named-view on `VaultDashboardForm` (reuse the existing filter engine); export the saved view. *Marks Done: Custom report builder*
- **PR14 — Bulk import / migration:** a Master tool to ingest a folder of existing SW files into the vault (create records, set initial rev, validate props, optional release). *Marks Done: Bulk import / export*
- **PR15 — Conditional card logic:** make required fields conditional on PartType — Purchased ⇒ require Supplier + SupplierPN; Manufactured ⇒ require Material + Finish. *Marks Done: Conditional / card logic*
- **PR16 — mBOM view:** an mBOM layered on the eBOM, allowing manual/phantom lines + process notes, exported separately; eBOM untouched. *Marks Done: eBOM / mBOM views*
- **PR17 — Advanced query builder (OR + saved):** add OR grouping to `AdvancedSearchForm` and integrate the saved searches from PR8. *Marks Done: Advanced query builder*
- **PR18 — Records retention policy:** configurable retention (keep last N revs / purge archives older than X); a Master "purge by policy" with audit. *Marks Done: Records retention*
- **PR19 — Controlled distribution log:** log every export/print of a released file (who/when/what) + a distribution report. *Marks Done: Controlled distribution*
- **PR20 — Disaster-recovery backup:** scheduled full backup (vault.xml + audit.csv + RELEASED tree) to a DR path + a scripted/documented restore. (Extends the existing `vault.xml.bak`.) *Marks Done: Disaster recovery*
- **PR21 — Health-monitoring panel:** an admin panel surfacing recent DB health events (`LockDegraded` / `VaultSaveFallback` / `VaultRestoredFromBackup`), spill-file status, and a vault integrity check. *Marks Done: Health monitoring*
- **PR22 — ISO 9001 / AS9100 control report:** a compliance report mapping your existing controls (rev control, approval/signature records, audit, retention) to an auditor checklist with evidence links. *Marks Done: ISO 9001 / AS9100 controls*
- **PR23 — Serial & parallel approvals:** a configurable approval policy (require N approvers or a sequence) per release, stored in `vault.xml`; `PendingRequestsForm` honours it. *Marks Done: Serial & parallel approvals*
- **PR24 — Standard-parts / Toolbox library:** a managed library folder + "insert from library"; library parts flagged (you already detect Toolbox). *Marks Done: Standard-parts / Toolbox mgmt*
- **PR25 — Email released file:** an "Email released file" action attaching the released PDF/STEP to a Master-composed mail (reuse the Mailgun config). *Marks Done: Office & email integration*
- **PR26 — Comments & discussions:** a per-file comment thread in `vault.xml`, shown/added in the task pane. *Marks Done: Comments & discussions*
- **PR27 — Item master:** a lightweight item record keyed on PartNo (status + where-used rollup), distinct from the file. *Marks Done: Item master*
- **PR28 — Scheduled tasks + watch-folder import:** a scheduler hook (in-app timer or Windows Task Scheduler) for backups/reports + a watch-folder that auto-imports dropped SW files (reuse PR14). *Marks Done: Scheduled tasks, Watch-folder import*
- **PR29 — Project / area segregation:** per-division visibility/permission — engineers see/edit only their divisions, Masters all. (You already have the division folder structure.) *Marks Done: Project / area segregation*
- **PR30 — Escalation, deadlines & delegation:** request due-dates with reminder emails for overdue items + a delegate-approver setting. *Marks Done: Escalation & deadlines, Delegation*

**At the end of Tier 1 + Tier 2 you cross 50%.** Pause, ship, and let the team use it before Phase 2.

---

## PHASE 2 — push toward 80%

Bigger builds. Sequence them after 50% is solid. Still one PR per task. Tackle in roughly this order:

**2A. Finish the heavy partials that genuinely matter**
- Admin console (a real settings/users/roles/scheme UI), SSO via Active Directory (you already key off the Windows identity), full-text search (index document content + properties), automatic per-save versioning (alongside the revision model), large-assembly performance pass.

**2B. Easy Not-Started fill (quality-of-life)**
- Virtual/phantom BOM items, redline/markup capture, conditional routing + a simple visual workflow editor, field-level security.

**2C. Your differentiators — the real reason a shop switches (prioritise these)**
- AI auto-metadata from title blocks, natural-language search, design-reuse recommendation, auto-draft ECO descriptions, DFM check at check-in, material-compliance auto-check. LLM-backed; medium effort, high impact. These are the rows *none* of the incumbents have — they're where BCore wins, not where it catches up.

**2D. Integration layer**
- REST API & SDK, then webhooks/event triggers — these unlock ERP/MRP later without rebuilding anything.

**What you can skip and still hit 80%:** cloud/SaaS, web + mobile clients, multi-CAD, multi-site replication, HA/failover, MFA, encryption-at-rest, AR/voice/blockchain, federated vaults (~25–30 rows). They're off-strategy for an on-prem, sheet-metal-focused tool. Reaching 80% means finishing the partials + most easy Not-Started + ~10–12 of the AI/engineering-depth features — **not** building the enterprise-cloud stack.

---

### After each merge
Flip that feature's **Status** in the Super PDM checklist to **Done** (the BCore column and the coverage chart update themselves). Tier 1+2 → 50%; Phase 2 clusters → 80%.
