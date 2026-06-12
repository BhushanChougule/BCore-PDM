# BCore PDM — Licensing, Super-Access & Distribution Design

> **STATUS: DESIGN ONLY — NOT IMPLEMENTED.** Pinned here so the decisions
> survive until development wraps. Nothing in this document is built yet;
> do not implement any of it until development is declared finished.
> This is an internal design document, NOT a software license grant.
>
> Captured 2026-06-12 from design discussions with the owner (bchougule).

---

## 1. Goals & locked-in decisions

| Question | Decision |
|---|---|
| Who are we defending against? | **Both** — own engineers / the company itself (if the owner leaves), AND future external customers |
| What happens on license expiry? | **Features-off-but-files-still-work** (graduated, never a hard CAD lockout) |
| License scope | **Company-wide** (no per-machine binding in-house — zero-maintenance) |
| Activation model | **Offline signed license file** (no server; online activation is a later commercial upgrade) |
| Vault data protection | **Out of scope** — not concerned about data; NTFS/share permissions are IT's job. Encrypting vault.xml/audit.csv would fight the Excel-openable audit trail, backup/restore recovery, and the cross-machine lock model |
| Code protection | **Binary-only deployment + obfuscation** — no source ever leaves the owner's hands |
| Super access ("Owner") | Tied to **the owner's password**, NOT to a machine or a Windows account (owner works from a company laptop that could be reclaimed) |

## 2. Core mechanism: one offline, signed license file

A single `license.lic` in `N:\PDM-SolidWorks\ADDIN\` carries everything:

- **Company name** (display + per-customer identity later)
- **Expiry date** (the subscription / kill switch)
- **Owner identity**: a label (the owner's PERSONAL email, `chougulebhushan93@gmail.com` — identity that survives losing the laptop/AD account) + an **owner password hash** (PBKDF2 with salt — `Rfc2898DeriveBytes`, built into .NET 4.8)
- Optional feature flags and a schema version for forward compatibility

The file is **signed with the owner's private key** (RSA or ECDSA). The add-in
embeds only the matching **public key** and verifies the signature at load.
Any edit (date pushed out, hash swapped) kills the signature and the license
is rejected. Renewal/revocation = the owner mints a new file and drops it in
ADDIN; no infrastructure.

Design secrecy is NOT required (Kerckhoffs's principle): this document being
readable changes nothing — security rests entirely on the private key.

### 2.1 Key custody — the most important operational rule

- The **private signing key** and the **license generator tool** live on
  PERSONAL hardware (personal PC and/or USB stick), with the key file itself
  password-protected. **Never on the office laptop, never on N:, never in
  this repo.**
- The repo and the shipped DLL contain ONLY the public key.
- If the owner leaves and the company keeps the laptop/repo clone, they hold
  nothing that can mint or extend a license.
- The generator is a small separate console app (company/expiry/owner-hash in,
  signed `.lic` out). Its CODE may live anywhere (it's useless without the
  key); the KEY is what's guarded. It is never shipped to anyone.

## 3. Super access — "Owner" role above Master

**Why not Windows username:** `Environment.UserName` is spoofable (anyone with
admin can create a local account named `bchougule`), and Masters are read from
vault.xml — a plaintext file on the share that anyone can edit. Neither is a
trustworthy "owner of the software" lever.

**Design:**

- The license names the Owner; Owner-level actions prompt for the **owner
  password**, verified against the hash in the signed license. Works on any
  machine, under any Windows account.
- `GetUserRole` consults the license FIRST: the Owner outranks vault.xml.
  Nobody becomes Owner by editing data.
- Owner = everything a Master can do + license-level actions (and lockout, if
  ever needed). Day-to-day the owner operates as a normal Master with NO
  prompts — the password gates only owner-level actions, and one successful
  entry elevates for the rest of the SOLIDWORKS session (low friction).
- Forgot password / suspected leak → mint a new license with a new hash.
  No reset infrastructure needed; that's also the rotation story.
- Masters stay exactly as today (vault.xml, editable) — "who runs PDM
  day-to-day" is separated from "who owns the software" (cryptographically
  fixed).

## 4. Expiry behaviour — graduated, never a production stoppage

This runs on the owner's own shop floor first; a hard lockout that blocks
opening/saving CAD files could halt engineering.

1. **Grace period** (~14 days before expiry): loud warning banner / dialog
   ("license expires in N days — contact bchougule"), everything still works.
2. **Expired**: GOVERNANCE features disable (Release, New Revision, approvals,
   vault tracking) with a persistent "license expired" banner. **Files still
   open and save.** Engineers are inconvenienced, never bricked.
3. Renewal = drop a new `.lic` on N:.

**Clock-rollback mitigation:** persist a "last seen" timestamp in a protected
spot; refuse (or warn loudly) if the clock moves backwards. Low risk on
domain-joined, time-synced machines; becomes MANDATORY for external customers.

**Honesty note:** any client-side check is ultimately patchable by a skilled
reverse-engineer with the DLL (NOP the check / swap the public key).
Obfuscation raises that bar far beyond anything in-house engineers would
attempt. For untrusted external customers, online activation with server-side
enforcement is the real answer (see §7). This scheme is "tamper-evident and
strongly discouraging", not "uncrackable" — that's the correct trade-off here.

## 5. Code protection — obfuscation, not the installer

C# IL decompiles back to readable source in seconds (dnSpy/ILSpy), so shipping
a plain DLL protects nothing. **ConfuserEx** (free, open-source, the standard
choice) renames symbols, encrypts strings, and tangles control flow.

**Critical exclusions for THIS add-in** (obfuscation must NOT touch, or the
add-in won't register/load):

- The COM-visible class `PDMLite.PDMLiteAddin`, its GUID/ProgId, and the
  `ISwAddin` implementation surface
- Anything SOLIDWORKS or WinForms reaches by name/reflection (event handler
  hookups via COM delegates are code-side and fine; verify dialogs/serialized
  names in the test pass)

Obfuscation gets a **dedicated test pass** (register, load, full smoke test)
before first shipped build. It bolts onto the deploy flow as a build step
between the Release build and the copy to ADDIN.

A copied DLL is doubly useless: unreadable (obfuscated) and disabled without
a valid signed license.

## 6. Distribution — single setup.exe (Inno Setup)

Replace the .bat-based rollout with one `BCorePDM-Setup.exe` built with
**Inno Setup** (free, battle-tested):

- Checks admin + .NET Framework 4.8
- Writes the COM + SOLIDWORKS registry keys DIRECTLY (no RegAsm dependency,
  no N:-mapping fragility in elevated sessions)
- Proper Add/Remove Programs entry with uninstaller; branding
- **Installer ≠ code protection.** It unpacks the same DLL; what protects the
  code is §5 + the license check. (CADlink's protection is in their binaries,
  not their installer.)

**Two modes from ONE script (a switch, not a fork):**

| Mode | DLL location | Use case |
|---|---|---|
| Network-register (RECOMMENDED in-house) | `N:\PDM-SolidWorks\ADDIN\PDMLite.dll` — installer only registers it | 5–10 seats; ONE copy, update once, everyone gets it next SW start; add-in is useless without N: anyway |
| Local-install | `C:\Program Files\BCore PDM\` | External customers (no access to our share); updates then require per-machine touch or an updater |

The C6 scripts (`DeployPDMLite.bat` / `InstallPDMLite.bat` / .reg files)
remain the DEVELOPMENT-ERA tooling and the dev machine's deploy path; the
installer is the END-STATE rollout deliverable.

## 7. Commercialization addendum — what ELSE is needed (beyond §2–§6)

The protection scheme is ~90% of commercial ENFORCEMENT; enforcement is the
smallest part of commercializing. In priority order:

1. **IP OWNERSHIP — THE GATE IN FRONT OF EVERYTHING. Act early, not at
   commercialization time.** Built by an employee, on a company laptop, for
   the employer's use: depending on the employment agreement
   (invention-assignment / work-for-hire clauses) and jurisdiction, the
   company may have a claim to the IP. Read the employment contract; ideally
   get written acknowledgment of ownership (or a license arrangement) from
   the company. Needs a real lawyer if serious. Keep personal-IP work on
   personal hardware/time where possible. (Also: trademark-check the
   "BCore PDM" name.)
2. **Per-customer licenses** — the generator already does this by design
   (company name, expiry, owner hash per customer). Trials fall out free:
   a 30-day license IS a trial.
3. **Machine-lock or online activation** for untrusted customers — offline +
   company-wide means customer A can hand DLL + .lic to company B. The design
   upgrades to both without rework; a small activation server
   (issue/verify/revoke, heartbeat) becomes the main new enforcement build.
   Clock-rollback protection becomes mandatory.
4. **Authenticode code-signing certificate** for installer + DLL (a few
   hundred $/yr; without it SmartScreen makes the product look like malware).
5. **The biggest engineering lift: de-Richards-Wilcox the product.**
   Hardcoded today and ALL per-customer-different: `N:\PDM-SolidWorks` paths,
   the eight WIP division folders, Masters bchougule/rkramarz, the
   required-property list (PartNo, Material1, FinishType, …), Material/Finish
   dropdown values, MM/dd/yyyy dates, the revision sequence (skips I,O,Q,S,X),
   export naming conventions, two-initials CheckedBy rule, Mailgun sender
   domain. Commercializing = a configuration layer (vault path, divisions,
   roles/users, property schema, revision sequence, naming patterns) + a
   first-run "create a new vault" wizard. Touches almost every file; bigger
   than the licensing system itself.
6. **Scale/infrastructure limits become product constraints**: the XML
   whole-file DB + SMB lock-file concurrency are proven for OUR LAN at OUR
   scale; customers bring weird NAS, WAN links and 200-seat ambitions.
   Eventually a real DB backend; at minimum publish supported-environment
   limits. Also a **SOLIDWORKS version support matrix** (currently built
   against one version's interop).
7. **Business wrapper**: EULA (the legal instrument that actually forbids
   copying — the technical check only raises the bar), an entity to sell
   through, pricing/invoicing, optionally the SOLIDWORKS Solution Partner
   program (credibility, not required).
8. **Third-party components**: PdfSharp is MIT — fine commercially, include
   its license notice. Remove the vestigial `System.Data.SQLite.dll` before
   shipping.
9. **Support machinery**: real versioning (the pinned 1.0.0.0 must become
   real versions — note the .reg-file version invariant), an update
   mechanism, crash/diagnostic logging readable remotely, documentation,
   onboarding, and a human answering emails — the cost that surprises
   one-person products most.

**Anti-goal:** do NOT build the configuration layer / activation server /
EULA speculatively. They wait until a real customer prospect exists.

## 8. Implementation plan (when development is declared DONE)

Build order — each step packages the previous:

1. **License system**: new `LicenseManager.cs` (load `license.lic`, verify
   signature against the embedded public key; expose `IsValid`,
   `IsOwner(user)`/owner-password check, `DaysRemaining`, feature flags) +
   a gate in `SwAddin.ConnectToSW` + `GetUserRole` consulting the license
   first. Rides the existing non-fatal philosophy: missing/expired license
   shows a dialog and DEGRADES (per §4), never crashes SOLIDWORKS.
   Plus the generator console app (kept with the private key, never shipped).
2. **Obfuscation**: ConfuserEx into the Release build with §5 exclusions +
   dedicated smoke-test pass.
3. **Installer**: Inno Setup script (network-register mode for in-house),
   replacing the .bat rollout.

### Final deliverables split

| Owner keeps (personal hardware, never shipped) | Company gets |
|---|---|
| Private signing key (password-protected) | `BCorePDM-Setup.exe` (one-click install) |
| License generator tool | Obfuscated `PDMLite.dll` + `PdfSharp.dll` |
| Owner password (in his head) | `license.lic` — signed, company-wide, expiry, owner hash |

### During development (NOW), only two habits

- When touching `GetUserRole`, startup code, or hardcoded conventions for
  other reasons: keep them centralized and don't build anything that fights
  this design. No speculative licensing work.
- Sort out the IP-ownership question (§7.1) early — it gates everything.
