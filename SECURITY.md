# BCore PDM — Security Hardening Guide

BCore PDM is a client-side add-in over a shared network drive. The add-in
enforces the vault rules, but anything a user can reach with Explorer or
Notepad is only as protected as the NTFS permissions on the share. This
guide tells IT exactly which permissions to set and why. Apply it before
(or together with) the engineer-PC rollout.

## 1. Roles — make "Master only" real (audit H13)

Out of the box, roles live in `VAULT\vault.xml` (`<Users>`), which every
engineer must be able to WRITE (every save updates the vault database).
That means any engineer can open it in Notepad and promote themselves to
Master.

**Fix: create `N:\PDM-SolidWorks\VAULT\roles.config`** — when this file
exists and parses, it is the AUTHORITATIVE role source and vault.xml's
`<Users>` section is ignored:

```xml
<Roles>
  <User><Username>bchougule</Username><Role>Master</Role></User>
  <User><Username>rkramarz</Username><Role>Master</Role></User>
</Roles>
```

Any username not listed is an Engineer (same default as before). Then set
NTFS permissions on **roles.config only**:

| Principal              | Permission        |
|------------------------|-------------------|
| Engineers group        | Read              |
| Masters (bchougule, rkramarz) / IT | Read + Write |

Notes:
- The add-in caches role lookups for 5 minutes per machine, so a role
  change lands within 5 minutes (or after a SOLIDWORKS restart).
- A corrupt/empty roles.config falls back to vault.xml (so a bad edit can
  never lock the Masters out); the fallback is recorded in audit.csv as a
  `RolesFileUnreadable` health event.

## 2. Folder permissions — protect released artifacts (audit H13)

Engineers need write access to WIP and the VAULT folder (vault.xml,
vault.lock, audit.csv), but NOT to the published outputs. Recommended NTFS
layout on `N:\PDM-SolidWorks`:

| Folder      | Engineers           | Masters/IT     | Why |
|-------------|---------------------|----------------|-----|
| `WIP\`      | Modify              | Modify         | daily work |
| `VAULT\`    | Modify              | Modify         | DB + lock + audit appends come from every client |
| `RELEASED\` | Read                | Modify         | published snapshots — the OS read-only attribute alone can be cleared by anyone |
| `ARCHIVE\`  | Read                | Modify         | revision history snapshots |
| `EXPORTS\`  | Read                | Modify         | current released deliverables |
| `SCRAP\`    | Read                | Modify         | retired files awaiting purge |
| `ADDIN\`    | Read                | Modify         | nobody should swap the deployed DLL |

IMPORTANT: Release / New Revision / Rollback / Remove from Vault write into
RELEASED / ARCHIVE / EXPORTS / SCRAP **from the Master's machine**, so this
table assumes those operations are only ever run by Masters (which the
add-in already enforces in the UI). If a future feature lets engineers
write released artifacts, revisit this table.

The audit trail (`VAULT\audit.csv`) cannot be append-only on plain NTFS;
engineers can technically edit it. The Audit Report tolerates damage
(PR F), and the local spill keeps blocked events, but treat the log as
tamper-EVIDENT, not tamper-PROOF, until the vault-service design lands.

## 3. Email credential (audit H6)

`VAULT\email.config` holds the Mailgun SMTP password in plain text, and
every machine that sends notifications must be able to read it. Plain-text
is therefore unavoidable in the current architecture — contain the blast
radius instead:

1. **Use a Mailgun SMTP credential that can ONLY send** (a per-domain SMTP
   login, not the account API key), so a leaked password can spam but not
   touch the Mailgun account.
2. **ACL `email.config`: Engineers = Read, Masters/IT = Modify.** Engineers'
   add-ins need to read it to send request notifications.
3. **Server pin (in code, this PR):** the add-in refuses to send unless
   `SmtpServer` is `*.mailgun.org`. A tampered config pointing at an
   attacker's server (the classic credential-harvest trick) gets nothing —
   the credential is only ever transmitted to Mailgun, over TLS
   (`EnableSsl` is always on).
4. Rotate the password if email.config was ever world-writable (it was,
   before this guide).

## 4. What this does NOT solve

These are honest limits of a client-side add-in on a file share (see the
audit's Part 3 — a small vault service would remove them):

- Engineers can still edit `vault.xml` *data* (statuses, locks); the share
  must stay writable for the DB to work. roles.config closes the
  self-promotion hole, and the audit trail records the actions the add-in
  performs, but direct file edits bypass the add-in entirely.
- `audit.csv` is writable by every client (appends come from everywhere).
- The OS read-only attribute on WIP copies of released files is advisory;
  the RELEASED snapshot is the protected copy under the ACLs above.
