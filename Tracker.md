# BCore PDM — Work Tracker

When I ask for **"the summary for {date}"**, produce BOTH deliverables below, in
exactly this format, then append the entry to the **Log** at the bottom.

---

## Deliverable 1 — Timelog table row

Tab-separated. Columns:

`Date (CDT)` ⇥ `CDT Window` ⇥ `Commits` ⇥ `Est. Hrs` ⇥ `Work Done (one line)`

Example:

```
6/17/2026	09:16–20:31	9	7.5	Merged PR #63 (Baselines) after an adversarial pre-merge review; rebased and extended PR #64 (reason-for-change)
```

Column rules:
- **Date (CDT)** — the calendar day in US Central time, `M/D/YYYY`.
- **CDT Window** — first commit time → last commit time that day, `HH:MM–HH:MM` (CDT).
- **Commits** — number of commits that landed that day (committer date, in CDT). Merge commits count.
- **Est. Hrs** — estimated **active** working hours. If the day has a long idle gap
  (a multi-hour break between blocks), exclude the gap — do **not** use the raw window span.
- **Work Done** — one headline sentence.

## Deliverable 2 — Short summary (6–7 lines)

6–7 short, punchy lines — one action each, past tense, no long clauses. Example:

```
Finished baseline viewer: Description + Config columns, mass rollup, flat parts list, open-on-double-click.
Centered the viewer header block; reworded the footer.
Adversarial pre-merge review of PR #63 (parallel agents); fixed 3 findings; merged to main.
Rebased PR #64 onto main; fixed the clipped reason prompt (now wraps/auto-sizes).
Reason now "Release only" (New Revision drops the prompt); release reason leads File History.
Added ECO-style reason dropdown + detail; reason capture on Rollback and Remove too.
Surfaced the reason on the as-released Baseline and a Vault Dashboard hover tooltip.
```

---

## How to derive the data (methodology)

Git/the container runs in **UTC** — convert to Central time:
- **CDT = UTC − 5** (daylight saving, ~mid-Mar to early-Nov). In winter use **CST = UTC − 6**.
- A commit's day is decided by its **committer date** converted to CDT — **not** the author
  date. Rebases / cherry-picks preserve the original author date, which misattributes the day.
  (e.g. a commit at `01:05` UTC belongs to the **previous** CDT day, `20:05`.)

Command (container TZ is UTC, so `%cd` prints UTC — subtract 5 h for CDT):

```
git --no-pager log --pretty=format:"%h | %cd | %s" --date=format-local:"%Y-%m-%d %H:%M" --all
```

Then: filter to the target CDT day, count the commits, take the first/last commit times for
the window, estimate active hours (drop idle gaps), and write the headline + 6–7 short lines.

---

## Log

### 6/16/2026 · 15:41–20:06 · 5 commits · 4.5 hrs
**Rebased PR #63 (Baselines) onto merged main; built the indented, expandable BOM viewer**
- Rebased PR #63 onto merged main (resolved menu + CLAUDE.md conflicts); now mergeable-clean.
- Baseline capture now reads the live tree → multi-level indented BOM with real quantities.
- Expandable tree: click ▸/▾ to collapse/expand, plus Expand All / Collapse All.
- Added "No." outline column (1, 1.1, 1.3.1); centered headers + Rev/Qty; MM/dd/yyyy dates.
- Added "Export All Revs" → one .xlsx, a sheet per release (new dependency-free XlsxWriter).

### 6/17/2026 · 09:16–20:31 · 9 commits · 7.5 hrs
**Merged PR #63 (Baselines) after an adversarial pre-merge review; rebased and extended PR #64 (reason-for-change)**
- Finished baseline viewer: Description + Config columns, mass rollup, flat parts list, open-on-double-click.
- Centered the viewer header block; reworded the footer.
- Adversarial pre-merge review of PR #63 (parallel agents); fixed 3 findings; merged to main.
- Rebased PR #64 onto main; fixed the clipped reason prompt (now wraps/auto-sizes).
- Reason now "Release only" (New Revision drops the prompt); release reason leads File History.
- Added ECO-style reason dropdown + detail; reason capture on Rollback and Remove too.
- Surfaced the reason on the as-released Baseline and a Vault Dashboard hover tooltip.

### 6/18/2026 · 08:58–20:24 · 7 commits · 7.5 hrs
**Merged PR #64; rebased and extended PR #65 (Obsolete lifecycle state) through supersession + several test-fix rounds**
- Added Tracker.md — daily work-summary format + log.
- Adversarial pre-merge review of PR #64 (parallel agents); SAFE TO MERGE; merged to main.
- Rebased PR #65 (Obsolete state) onto main (86 commits behind) — adapted to operation claims, ReloadOrReplace, categorized reason.
- Fixed the Obsolete search-card colour (grey, was WIP blue).
- Built supersession: "superseded by" replacement link (picker + display), design-time obsolete-component open warning, reason tooltip.
- Hardened it: PartNo→filename fallback, "(replaced by X)" history note, re-checking warning, update-already-obsolete, in-app-open warning.
- Investigating two reported supersession bugs (search-card display + search-open warning) — awaiting a vault.xml check.

### 6/19/2026 · 08:17–19:12 · 17 commits · 6.5 hrs
**Finished + merged the Obsolete feature (PR #65); built Where Used end-to-end and hardened it through two pre-merge reviews**
- Finished the Obsolete feature (deferred/forced in-app open warnings, path-based) and merged PR #65.
- Built the Where Used viewer: Single / All-Levels / Top-Level modes, Qty column, filter box, right-click menu.
- Added "Export All Levels" to one multi-sheet .xlsx; centered the header, fixed clipping and plural counts.
- Added a task-pane Where Used button + Find search box; moved Remove from Vault to the dashboard right-click.
- Made results config/part-number-based (not file-level) using the as-released baselines.
- Captured each assembly's components at save, so WIP assemblies also filter by the correct config.
- Fixed the Find dropdown height + selection lag; ran two pre-merge reviews (7 + 4 low findings) and pushed fixes.
