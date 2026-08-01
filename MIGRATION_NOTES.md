# Migration Notes

This document itemises every deliberate behavioural difference introduced by
migrating the DotNetNuke 4.9.0 platform off VB.NET on .NET Framework 2.0 ASP.NET
Web Forms and onto the C# 12 / .NET 8 API under `backend/` and the Angular 19
single-page application under `frontend/`.

Behaviour is preserved by default: identical inputs are expected to produce
identical outcomes. Where exact equivalence was impossible, or possible but
unsafe to reproduce, the divergence is itemised here rather than absorbed
silently, and the same decision is annotated inline at the point of departure
with a `// MIGRATION:` comment.

The legacy `Library/` and `Website/` trees, both legacy solution files and the
legacy configuration files are read-only reference inputs. They are unmodified by
this work, so the legacy application remains buildable and deployable exactly as
it was.

> This file is append-only. Add a new `###` entry under the section below; do not
> restructure, reword or remove an existing entry.

## Deliberate behavioural differences

### Password storage, and the removal of password retrieval

**Legacy behaviour.** The DotNetNuke installation stored passwords in a
*reversible* form. `Website/release.config:L236-L246` registers
`AspNetSqlMembershipProvider` with `passwordFormat="Encrypted"` (Triple-DES) and
`enablePasswordRetrieval="true"`, and `Website/release.config:L89-L93` commits
the `machineKey` element whose Triple-DES decryption key is checked into source
control in plaintext. Any holder of the source could therefore decrypt every
stored password. Earlier in the platform's history the storage was not even
encrypted: the original schema declared the password column as
`[Password] [nvarchar] (20) NOT NULL` directly on `[dbo].[Users]` in
`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L97-L110`,
and that script's seed rows populated it with literal values.

**Target behaviour.** Passwords are hashed one way with BCrypt. Reversible
storage is not reproduced, the committed decryption key is not carried into any
new configuration file, and **password retrieval is removed entirely** - no API
endpoint and no screen exposes it. Password *reset* remains available.

**Why the difference is deliberate.** Reproducing reversible storage would carry
a known credential-disclosure weakness into new code. This is the one place where
the migration's behaviour-preservation default is overridden on security grounds,
which is why BCrypt is a dependency of the target rather than an optional
convenience.

**Operational consequence.** A one-way hash cannot verify a credential that was
stored under the legacy reversible scheme, so legacy passwords cannot be
validated as-is. The adopted path is **re-hash on first successful login, with an
administrative reset as the fallback** for accounts that never sign in again.
Until a given row has been re-hashed it is still held in its legacy format, so
that format must remain expressible: the legacy `PasswordFormat` enumeration is
ported member for member as
`backend/src/DnnMigration.Domain/Enums/PasswordFormat.cs`, keeping its persisted
ordinals `Clear = 0`, `Hashed = 1` and `Encrypted = 2` unchanged. Once a row has
been re-hashed, the value held there is purely historical.

**Preserved unchanged.** The legacy password *policy* from the same provider
registration is carried over verbatim as validation rules - minimum length seven,
no required non-alphanumeric characters, no question-and-answer requirement, and
email uniqueness not enforced. Tightening the policy during a migration would
lock out existing users, so any hardening is left as a separate, explicit
decision.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Enums/PasswordFormat.cs`.

## Domain enumerations
### `PermissionKey` — permission keys are strings, and the member name is the value

**Artefact:** `backend/src/DnnMigration.Domain/Enums/PermissionKey.cs`

**Legacy representation.** Permission keys existed only as bare upper-case string
literals scattered through the permission controllers and the security helper —
`"VIEW"` and `"EDIT"` at `Library/Components/Modules/ModuleController.vb:L130,L134,L136,L1108`,
`Library/Components/Tabs/TabController.vb:L109,L110` and
`Library/Components/Security/PortalSecurity.vb:L522,L618,L623,L628`; `"READ"` at
`Library/Components/Portal/PortalController.vb:L1416`; and `"READ"`/`"WRITE"` seeded
against the `SYSTEM_FOLDER` scope by the upgrade scripts
(`03.00.11:L15`, `03.02.04:L28,L33`, `03.03.03:L31,L36`, `04.00.04:L2584,L2589`).
The legacy `PermissionInfo` type exposed `PermissionKey` as a plain `String`.

**Target representation.** Those literals are centralised into a four-member
enumeration: `VIEW`, `EDIT`, `READ`, `WRITE`.

**The column contract is text, not a number.** `Permission.PermissionKey` is declared
`varchar(20) NOT NULL`
(`Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider:L688`), and
the `AddPermission` procedure in the same script (L843-L862) accepts
`@PermissionKey varchar(20)`. No integer for this concept is persisted anywhere.

**Deliberate decision — upper-case identifiers.** The members are spelled exactly as
the legacy literals are spelled, in upper case, rather than in PascalCase. This makes
the persisted value exactly recoverable with no casing transform:
`PermissionKey.VIEW.ToString()` and `nameof(PermissionKey.VIEW)` both yield `"VIEW"`.
A PascalCase spelling would oblige every caller to remember an upper-casing step, and
forgetting it would yield `"View"`, which silently matches no stored row and no
server-side permission check. Upper-case identifiers remove that silent-failure mode
mechanically. The names are load-bearing data that production databases already
contain and must never be renamed or re-cased.

**Requirement placed on the persistence layer.** The ordinals are incidental: they are
never persisted and never serialised, and no member carries an explicit value.
`Infrastructure/Persistence/Configurations/PermissionConfiguration.cs` must therefore
apply a **string-based** value conversion over the `varchar(20)` column — converting
each member to and from its name — and must never convert to an integer. The
conversion is deliberately not declared in the Domain layer, which takes no dependency
on any persistence technology.

**Exhaustiveness.** Four keys is the complete set for this DotNetNuke generation.
Measured occurrence counts across the legacy tree were EDIT 13, VIEW 12, WRITE 3 and
READ 2. Independently, a sweep of every value compared or assigned to `PermissionKey`
across all 88 upgrade scripts yields only `READ` and `WRITE` literals and no other key.
No later-version or general-vocabulary key — `DEPLOY`, `ADD`, `DELETE`, `MANAGE`,
`FULLCONTROL` and their kin — was added, because none exists here. Apparent sightings
are different concepts: `"ADD"` is a selection value on an admin screen
(`Website/admin/Tabs/Import.ascx.vb:L161`), while `'Manage'` and `'Import'` are
`ModuleControls.ControlKey` values (`02.00.00:L6056`, `04.06.00:L1051,L1054`).

**Not modelled as flags.** A `Permission` row names exactly one key, and an access
grant is a separate `ModulePermission` or `TabPermission` row bound to a role. The
enumeration is deliberately not a bit mask, because combining members would invent a
data model the schema does not have.

**Sibling concept left as text.** `Permission.PermissionCode` scopes a key to a
subsystem and carries `SYSTEM_TAB`, `SYSTEM_MODULE_DEFINITION` and `SYSTEM_FOLDER`
(14, 6 and 5 occurrences respectively across the 88 scripts, with no fourth value).
Declared `varchar(50)`, it remains a plain `string` property on the `Permission`
entity; **no `PermissionCode` enumeration was created**, so an installation carrying a
scope this codebase has not seen still round-trips intact.

**No absent value.** The column is `NOT NULL`, so no placeholder or "unset" member was
declared and no member carries a negative value — notable because `-1` is doubly
overloaded in this codebase, being both the legacy integer null-sentinel and the
`IDENTITY(-1,1)` seed of `Portals.PortalID`. Absence, where a caller needs it, is
expressed as a nullable projection (`PermissionKey?`) on that caller's own property.
The legacy text null-sentinel was the empty string rather than a null, so an empty key
is not modelled either.

**Client-side gating is not enforcement.** The Angular `hasPermission` directive
consumes these exact strings to show and hide affordances, but authorisation is decided
on the server by `PermissionEvaluator` and the permission authorisation policies.

**Behavioural impact:** none. This is a representation change only. The values written
to and read from `Permission.PermissionKey`, and the strings sent to the browser, are
byte-identical to the legacy literals.

### `BillingFrequency` — role billing and trial frequency codes

**File:** `backend/src/DnnMigration.Domain/Enums/BillingFrequency.cs`
**Legacy sources:** `Library/Components/Security/Roles/RoleController.vb`
(L25, L521-L527, L540-L547), `Library/Components/Security/Roles/RoleInfo.vb`
(L149, L188), `Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider`,
`.../01.00.05.SqlDataProvider`, `.../01.00.08.SqlDataProvider`

The legacy role billing and trial frequencies were untyped `String` properties.
They are replaced by a single explicitly-valued enum. **There is no behavioural
change**: every persisted character is preserved exactly, and each enum member's
underlying value is that character's code point, so the value round-trips to and
from the legacy `char(1)` column without loss.

| Member | Persisted code | `dbo.CodeFrequency` description | Expiry behaviour |
|---|---|---|---|
| `None` | `N` | `None` | No expiry — legacy assigns `Null.NullDate` |
| `OneTime` | `O` | `One-time Fee` | Perpetual — legacy assigns `9999-12-31` |
| `Day` | `D` | `Day(s)` | `AddDays(period)` |
| `Week` | `W` | `Week(s)` | `AddDays(period * 7)` |
| `Month` | `M` | `Month(s)` | `AddMonths(period)` |
| `Year` | `Y` | `Year(s)` | `AddYears(period)` |

Points of record:

1. **The characters are load-bearing data, not an internal numbering.** They are
   the literal bytes already stored in the two role columns of every existing
   DotNetNuke database. They must never be renamed or renumbered; the C# member
   identifiers are readability spellings only. Renaming one would not fail a
   build — it would silently mis-read live rows.

2. **The lookup table and its foreign key are GONE from the terminal schema, and
   the columns are consequently unconstrained.** This is worth stating plainly
   because the obvious reading of the early scripts is the opposite. The codes
   began as the key of `dbo.CodeFrequency`, declared `[char] (1) NOT NULL` in
   `01.00.00.SqlDataProvider` and enforced on the billing column by
   `FK_Roles_CodeFrequency`. `03.00.01.SqlDataProvider` then copies the six rows
   into the generic `Lists` table (`SELECT 'Frequency', Code, Description FROM
   CodeFrequency`), drops the foreign key, drops the table, and drops both
   `GetBillingFrequencyCode` accessor procedures. Nothing recreates any of them.
   In the terminal schema a code resolves through a `Lists` row with
   `ListName = 'Frequency'`, `Value` = the character and `Text` = the description
   — `04.08.00.SqlDataProvider` joins it as
   `LEFT OUTER JOIN Lists L1 ON R.BillingFrequency = L1.Value AND L1.ListName='Frequency'`.
   **Consequence:** the terminal columns carry no foreign key and no check
   constraint, so the database accepts any single character. Validity is the
   application's responsibility, and this enum is what supplies it — confining
   the columns to six members restores a guarantee the schema stopped providing.
   Downstream code must therefore *not* model a relationship to a `CodeFrequency`
   table: it does not exist.

3. **The numeric codes in the baseline DDL script are superseded and dead.**
   `01.00.00.SqlDataProvider` seeds the lookup table with digits `'0'`–`'5'` in
   upper-case, bracketed, `dbo.`-qualified SQL. `01.00.08.SqlDataProvider` then
   inserts each letter code in lower-case, unqualified SQL, rewrites both role
   columns onto the letters, and deletes each digit row. No later script re-seeds
   the table. A case-sensitive search of one naming form finds only the dead
   digits and produces a wrong code set, so inspection of this DDL chain must be
   case-insensitive and must cover all four naming forms in use (bare,
   `dbo.`-qualified, `[dbo].[…]`-bracketed, and
   `{databaseOwner}{objectQualifier}`-templated).

4. **One enum serves both columns; there is deliberately no separate trial
   frequency type.** The schema joins a single frequency lookup twice from one
   role row, and does so in *both* eras — early on as
   `join CodeFrequency C1 on Roles.BillingFrequency = C1.Code` plus
   `left outer join CodeFrequency C2 on Roles.TrialFrequency = C2.Code`, and
   terminally as two `Lists` joins against the same `'Frequency'` list, one per
   column. `RoleInfo.vb` independently documents the same six codes against both
   properties (L140-L145 and L179-L184). The two columns share one code set, so
   one type covers both.

5. **`N` carries a second responsibility beyond naming a frequency.** It is the
   no-trial guard: `RoleController.vb:L521` tests
   `role.TrialFrequency.ToString <> "N"` to decide whether the trial period or
   the billing period governs expiry, and the SQL applies the same test when
   projecting a role — right through to the terminal schema, where
   `04.08.00.SqlDataProvider` gates the trial fee, trial period and trial
   frequency behind `case when R.TrialFrequency <> 'N'`. Omitting the member
   would silently break trial-period selection in both layers, which is why the
   enum has six members and not the four the transformation table cites.

6. **The `Microsoft.VisualBasic` dependency is removed.** `RoleController.vb:L25`
   is the only in-scope import of that runtime. Its
   `DateAdd(DateInterval.Day/Month/Year, …)` calls are rewritten as
   `DateTime.AddDays` / `AddMonths` / `AddYears` in
   `backend/src/DnnMigration.Application/Services/RoleService.cs`. The week case
   keeps the legacy formulation of a day interval multiplied by seven rather than
   adopting any week-based helper, so the computed dates stay identical. The enum
   itself declares no methods: it carries the codes, and the Application layer
   owns the arithmetic.

7. **Nullability moved to the entity property, not into the enum.** Both
   `dbo.Roles.BillingFrequency` and `dbo.Roles.TrialFrequency` are
   `char(1) NULL`, so the `Role` entity exposes nullable properties. No
   `Unset`-style member with an invented value was added, and `None = 'N'` is a
   real legacy code rather than a stand-in for a missing value — the two must not
   be conflated.

8. **Legacy decoration is dropped.** The legacy properties carried XML
   serialisation and property-editor attributes (`RoleInfo.vb` L149 and L188).
   These are not carried across: the wire contract belongs to the DTO and API
   boundary, and the `char(1)` column conversion belongs to
   `Infrastructure/Persistence/Configurations/RoleConfiguration.cs`. The Domain
   project declares no package or project references at all, so it could not
   express such an attribute even if one were wanted.

### `BannerAdvertisingMode`

Target: `backend/src/DnnMigration.Domain/Enums/BannerAdvertisingMode.cs`

**No legacy enumeration existed for this setting.** The legacy codebase stored and
manipulated the portal banner-advertising mode as a bare `Integer` throughout:
`Library/Components/Portal/PortalInfo.vb:L39` declares the backing field and `L133` the
property. The named member set was therefore recovered from the only place the legacy
system ever gave these values names, the administration option list at
`Website/admin/Portal/sitesettings.ascx:L123-L125`, whose three items are labelled
`None`, `Site` and `Host` for the values `0`, `1` and `2`. (Note the lower-case markup
filename; `Website/admin/Portal/SiteSettings.ascx` does not exist, while the
code-behind `SiteSettings.ascx.vb` beside it is PascalCase.)

**The ordinals are unchanged: `None = 0`, `Site = 1`, `Host = 2`.** They are persisted
in the `Portals.BannerAdvertising` column, whose terminal schema state is
`int NOT NULL` with a database default of `0`. Verified case-insensitively across all
four object-naming forms in the DDL chain: created nullable at
`01.00.00.SqlDataProvider:L85` (bracketed form), tightened at
`01.00.05.SqlDataProvider:L1373` (bare form), and re-asserted at
`03.01.01.SqlDataProvider:L1117` with the zero default added at `L1127`
(`{databaseOwner}{objectQualifier}`-templated form). Because the column is `NOT NULL`,
no absent-value member exists on the enum; `None = 0` is a real mode meaning "banner
advertising disabled" and is also the column's own default, not a null stand-in.

Declaration order is load-bearing in addition to the values. The legacy administration
screen assigned the persisted integer directly to the option list's zero-based selected
index at `Website/admin/Portal/SiteSettings.ascx.vb:L291` and wrote that same index
straight back on save at `L774`, so each member's value is also its declaration
position. Neither the values nor their order may be changed.

**Behavioural rule carried forward.** `SiteSettings.ascx.vb:L295` evaluates
`optBanners.Enabled = objPortal.BannerAdvertising <> 2`, disabling portal-level banner
editing when the mode is host-managed; `L296` makes a companion explanatory label
visible for the same mode. Both checks are taken only when the caller is not a super
user. The magic literal `2` is replaced by a comparison against
`BannerAdvertisingMode.Host`, which is what "magic integers become named enum members"
means for this column. **This is a change of expression only, not of behaviour**: the
rule itself is implemented unchanged in the Application layer
(`DnnMigration.Application.Services.PortalService`) and in the Angular
`portal-settings` feature. It is deliberately not encoded in the enum, which declares
no methods.

The legacy XML serialisation decoration on the originating property is dropped, per the
rule that the domain holds no serialisation concern; the wire contract belongs to the
Application-layer DTOs and the column binding to the Infrastructure entity
configuration. No behavioural difference arises from this.

### `UserRegistrationMode` — type renamed from `PortalRegistrationType`

- **Target:** `backend/src/DnnMigration.Domain/Enums/UserRegistrationMode.cs`
- **Legacy source:** `Public Enum PortalRegistrationType` at `Library/Components/Shared/Globals.vb:L84-L89`

**What changed: the type name only.** The legacy type was named after the registration concept;
the target type is named after the `Portals.UserRegistration` column it discriminates, per the target
enumeration inventory. Nothing else about the type changed.

**What did not change: the members and their values.** All four legacy identifiers are carried across
verbatim, in the legacy declaration order, with their ordinals written explicitly:

| Member | Value | Legacy label |
| --- | --- | --- |
| `NoRegistration` | `0` | None |
| `PrivateRegistration` | `1` | Private |
| `PublicRegistration` | `2` | Public |
| `VerifiedRegistration` | `3` | Verified |

The ordinals are **persisted data**, not incidental. `Portals.UserRegistration` is declared
`[UserRegistration] [int] NULL` at
`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L84` and reaches a terminal
shape of `int NOT NULL` with a `DEFAULT (0)` constraint at `03.01.01.SqlDataProvider:L1116` and
`:L1125`. The legacy administration screen bound the stored integer straight to a radio-button list
index — `optUserRegistration.SelectedIndex = objPortal.UserRegistration` at
`Website/admin/Portal/SiteSettings.ascx.vb:L277`, written back from `optUserRegistration.SelectedIndex`
at `:L773` — so the **declaration order is part of the stored contract** as much as the values are.
Renumbering or reordering the members would silently reinterpret every existing row. The values are
independently corroborated by the option list at `Website/admin/Portal/sitesettings.ascx:L230-L233`
(note the lower-case file name; the code-behind is `SiteSettings.ascx.vb`).

**No `Unknown`/`NotSet` member was added.** The column's terminal state is `int NOT NULL` and its
database default is `0`, so there is no absent case to model and `NoRegistration = 0` is a real,
chosen mode rather than a stand-in for a missing value. Any nullable projection belongs to a
consuming property, never to this member list.

**Scope note — reading `Globals.vb` was reference-only.** `DotNetNuke.Common.Globals` is an excluded
subsystem, but the exclusion targets its static utility *behaviour* (the six members reached from
in-scope code, replaced by a host-settings service, bound configuration and environment paths).
`PortalRegistrationType` is an enum *type* — reference data — and was read solely to recover the
correct identifiers and ordinals. **No `Globals` member was ported and no `Globals.cs` exists.** The
two sibling enums in the same legacy region, `PerformanceSettings` and `UpgradeStatus`, are out of
scope and were not created.

**Attributes dropped.** The property this enum types, `PortalInfo.UserRegistration` at
`Library/Components/Portal/PortalInfo.vb:L125`, carried `<XmlElement("userregistration")>` on a class
carrying `<XmlRoot("settings")>`. No XML-serialisation, mapping, validation or display attribute is
carried into the Domain layer: column mapping belongs to the Infrastructure entity configuration, the
wire contract to the Application-layer DTOs, and the displayed label to the Angular templates.

## `UserLoginStatus` — enum members renamed, numeric values preserved

**Target:** `backend/src/DnnMigration.Domain/Enums/UserLoginStatus.cs`
**Legacy source:** `Library/Components/Users/Membership/UserLoginStatus.vb`
(`DotNetNuke.Security.Membership.UserLoginStatus`)

### What changed

The seven members were renamed from `SCREAMING_SNAKE_CASE` to PascalCase, because the legacy
naming is not idiomatic C# and the migration requires idiomatic C# 12 rather than a
transliteration. The type name `UserLoginStatus` is unchanged.

| Legacy VB member | Ordinal | Target C# member |
| --- | --- | --- |
| `LOGIN_FAILURE` | 0 | `Failure` |
| `LOGIN_SUCCESS` | 1 | `Success` |
| `LOGIN_SUPERUSER` | 2 | `SuperUser` |
| `LOGIN_USERLOCKEDOUT` | 3 | `UserLockedOut` |
| `LOGIN_USERNOTAPPROVED` | 4 | `UserNotApproved` |
| `LOGIN_INSECUREADMINPASSWORD` | 5 | `InsecureAdminPassword` |
| `LOGIN_INSECUREHOSTPASSWORD` | 6 | `InsecureHostPassword` |

### What did not change

The seven numeric values `0`–`6` are preserved exactly and are written out explicitly in the
target so that a future reordering cannot shift them silently. No member was added and none was
removed: the set stays closed at seven, with no invented "unset" or "not known" value, because
none exists in the legacy enum and the zero value already provides a safe default.

The ordinals are preserved because they are externally observable in three independent ways:

1. **They cross a serialisation boundary.** `Website/admin/Authentication/Login.ascx.vb:221`
   reads the status back out of ViewState with a cast, and ViewState round-trips an enumeration
   as its underlying integer.
2. **The original author wrote every value out explicitly**, even though the implicit
   declaration order would have produced the same numbers — a deliberate signal that the
   numbers are part of the contract rather than an accident of ordering.
3. **Eight published signatures pass the value by reference,** making it a cross-assembly
   contract: `Library/Components/Providers/Users/MembershipProvider.vb:90` and `:91`;
   `Library/Components/Users/UserController.vb:991`, `:1110` and `:1132`;
   `Library/Providers/MembershipProviders/AspNetMembershipProvider/AspNetMembershipProvider.vb:1408`
   and `:1429`; plus the audit helper `UserController.vb:66`.

`Failure` also remains the **zero** value, which is a security property rather than a
formatting preference. Seven separate legacy sites seed a status variable to failure before
attempting authentication so that any path which neglects to assign a result fails closed —
`Library/Components/Authentication/UserAuthenticatedEventArgs.vb:49`,
`Library/Components/Users/UserController.vb:993` and `:1133`,
`.../AspNetMembershipProvider.vb:1434`,
`Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:163`, and
`Website/admin/Authentication/Login.ascx.vb:219` and `:674`. Because failure is zero,
`default(UserLoginStatus)` is a refusal too. Reordering so a successful outcome took zero would
make an unassigned value mean "authenticated".

### Consequence of the rename: audit log type keys change

This is the one observable behavioural difference the rename produces, and it is recorded here
in full.

`Library/Components/Users/UserController.vb:80` assigns the status's `ToString()` directly to
the audit log type key:

```vb
objEventLogInfo.LogTypeKey = loginStatus.ToString
```

The member *name*, not only its value, therefore reaches the audit trail. The legacy
application writes keys such as `LOGIN_FAILURE` and `LOGIN_USERLOCKEDOUT`; the renamed members
produce `Failure` and `UserLockedOut`. The legacy audit sites are re-expressed as structured log
events in the target, and mapping these outcomes onto stable log event names — so that audit
intent survives the change of mechanism — is owned by the application layer, not by this
enumeration. Consumers of the legacy audit trail should expect the new key strings.

### Behavioural notes carried forward unchanged

- **Any value other than `Failure` that reaches the sign-in decision is an authenticated
  outcome.** `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:187` tests the
  status for inequality against failure, so `SuperUser`, `InsecureAdminPassword` and
  `InsecureHostPassword` are all completed sign-ins.
- **`UserNotApproved` never reaches that test.** The same control intercepts it first at
  `Login.ascx.vb:168` to drive the verification-code flow, so it is neither a refusal nor a
  completed sign-in. Callers must reproduce this three-way split rather than assuming a
  two-way one.
- **The two insecure-password outcomes are promotions of an already successful
  authentication,** not refusals: `UserController.vb:1144`–`:1147` promotes `Success` when the
  built-in administrator account presents the product's well-known default credential, and
  `:1149`–`:1152` promotes `SuperUser` for the built-in host account. They report a completed
  sign-in that carries a security warning.

### Contract shape change: by-reference argument becomes a result value

The legacy status is delivered through a `ByRef` argument, for example
`UserController.ValidateUser(..., ByRef loginStatus)` called at
`Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:164`. No `out` or `ref`
parameter appears in any target public API, so this enumeration becomes the failure-reason
channel carried by `Result<T>`: the call becomes
`Task<Result<LoginResponse>> LoginAsync(LoginRequest, CancellationToken)`. This is a signature
change only — the set of reportable outcomes and their meanings are unchanged.

The enumeration carries no serialisation, mapping or validation attributes, because the Domain
project declares no package or project references. Wire formatting of this status is owned by
the DTO boundary and message wording by the application layer.

## Domain type renames

### `VisibilityState` to `ModuleVisibility`

**Legacy source:** `Library/Components/Modules/ModuleInfo.vb` (lines 30-34) declares
`Public Enum VisibilityState` with the members `Maximized`, `Minimized` and `None`.

**Target:** `backend/src/DnnMigration.Domain/Enums/ModuleVisibility.cs` declares
`public enum ModuleVisibility`.

**What changed:** the *type name only*. `VisibilityState` was too generic to sit beside the
unrelated profile-property visibility concept in the same Domain assembly, and the target
name is the one fixed by the technical specification for this Domain enum.

**What did not change:**

- The three member names are byte-identical to the legacy source: `Maximized`,
  `Minimized`, `None`. They are not merely internal identifiers — the legacy portal
  template importer at `Library/Components/Modules/ModuleController.vb` (lines 304-307)
  matches them *as text*, so renaming any of them would break template import.
- The three ordinals are unchanged. VB.NET assigned them implicitly from declaration
  order; the C# declaration writes them out explicitly as `Maximized = 0`,
  `Minimized = 1`, `None = 2`. This is a faithfulness improvement rather than a change:
  the numbers are exactly what VB.NET produced, and making them explicit prevents a later
  re-ordering, alphabetical sort or inserted member from silently re-pointing existing
  rows at a different meaning.

**Why the ordinals are load-bearing:** they are persisted. The backing column is
`TabModules.Visibility`, declared `int NOT NULL` by
`Website/Providers/DataProviders/SqlDataProvider/03.00.01.SqlDataProvider` (line 31), and
the legacy reader at `Library/Components/Modules/ModuleController.vb` (lines 80-84) decodes
it by number: `0` to `Maximized`, `1` to `Minimized`, `2` to `None`. Renumbering would
reinterpret every existing row.

**No "unset" member was added.** The same legacy reader folds the legacy integer sentinel
for an absent value, -1, into `Maximized` rather than treating it as a distinct state, so
`None = 2` remains what it has always been — a real display state meaning "not rendered" —
and is not repurposed as an "absent" marker. Where a column genuinely permits an absent
value, that is expressed by a nullable property on the consuming entity
(`ModuleVisibility?`), never by an extra enum member.

**Dropped legacy artefacts (no behavioural effect):** the adjacent legacy declarations
carry serialisation attributes and a token-replacement contract that the target Domain
layer deliberately omits, because the wire contract belongs to the API-boundary DTOs and
the token-replacement subsystem is out of scope. The enum itself carried no attribute and
no behaviour, so nothing was lost.

## Domain layer

### `backend/src/DnnMigration.Domain/Enums/RoleStatus.cs` — net-new derived classification, no legacy ancestor

**What it is.** A three-member enumeration — `Pending`, `Active`, `Expired` —
naming the temporal state of one user-to-role assignment.

**Why this is a divergence.** It is the only enumeration in
`Domain/Enums/` with no legacy counterpart of any kind. Three independent
negative proofs were re-run against this checkout before it was authored, and a
fourth was found in the process:

1. **The identifier exists in no legacy source file.** Searching every `*.vb`
   file beneath `Library/` and `Website/` for `RoleStatus` returns zero hits, as
   does an extension-agnostic sweep of both trees.
2. **The identifier exists nowhere in the schema.** Searching all 88
   `*.SqlDataProvider` upgrade scripts for `RoleStatus` returns zero hits.
3. **Neither table has a status column and neither legacy type has a status
   property.** The terminal `UserRoles` shape is `UserRoleID`, `UserID`,
   `RoleID`, `ExpiryDate`, `IsTrialUsed` from the baseline at
   `01.00.00.SqlDataProvider:L238`, plus `EffectiveDate datetime NULL` added at
   `03.02.03.SqlDataProvider:L380` and re-applied under an existence guard at
   `04.00.04.SqlDataProvider:L418`. Every other statement touching the table
   only adds or drops a constraint — verified case-insensitively across the bare,
   `dbo.`-qualified, bracketed and `{databaseOwner}{objectQualifier}`-templated
   naming forms. `RoleInfo.vb` exposes exactly 15 properties and
   `UserRoleInfo.vb` exactly 8; not one of the 23 is named `Status`.
4. **The legacy screen shows no status either.** `securityroles.ascx` and its
   resource file label only "Effective Date" and "Expiry Date".

**What it replaces.** Not a named legacy type, but the inline date comparisons
the legacy code used in place of one, so that those comparisons are written down
once, under names, instead of being repeated at each call site. Three sources
are authoritative and agree:

- `RoleController.vb:L530-L535` normalises both dates on every write —
  `If EffectiveDate < Now Then EffectiveDate = Null.NullDate` and
  `If ExpiryDate < Now Then ExpiryDate = Now` — establishing that the effective
  date gates the start of an assignment and the expiry date gates its end.
- `RoleController.vb:L493-L501` is the cancellation path. Where the role carries
  a service fee and the trial has been used, the assignment is *expired* by
  back-dating its expiry date one day rather than deleted, so the trial-usage
  row survives; otherwise the row is deleted outright. An expired assignment is
  therefore a real, retained state and not merely the absence of a row — which
  is why `Expired` must exist as a member.
- The stored procedure `GetRolesByUser` expresses the in-force test in SQL at
  `03.02.03.SqlDataProvider:L405-L406` and again at
  `04.00.04.SqlDataProvider:L443-L444`:
  `(EffectiveDate <= getdate() or EffectiveDate is null)` and
  `(ExpiryDate >= getdate() or ExpiryDate is null)`. Both bounds are inclusive.
  That procedure only ever *selects* the in-force rows; it never *names* the
  other two states, and naming them is precisely what this enumeration adds.

**The three members and their derivation rules**, evaluated in this order so the
classification is both total and mutually exclusive:

| Member | Rule |
| --- | --- |
| `Expired` | The expiry date is set and falls strictly before now. Terminal, so it outranks a start date that has not yet arrived — a combination genuinely reachable, because the cancellation path back-dates the expiry date without touching the effective date. |
| `Pending` | Otherwise, the effective date is set and falls strictly after now. |
| `Active` | Otherwise. This is also the answer when both dates are unset. |

**Computed, never persisted, never mapped.** No column backs this value, so no
entity configuration may map it: `UserRoleConfiguration` and `RoleConfiguration`
must not call `HasColumnName`, `HasConversion` or `Property` for it. Mapping it
would oblige the baseline migration to create a column the 88-script chain never
creates, violating the immutable-schema rule. The consuming entity exposes it as
a read-only computed property with no setter, which EF Core ignores of its own
accord. The `[NotMapped]` annotation is deliberately *not* used, because the
Domain project takes no package or project reference at all; the prohibition is
carried in the type's XML documentation instead. No explicit numeric values are
assigned — the ordinal is meaningless and must never be persisted, and the value
is serialised by name at the outward boundary through the central serialisation
options.

**Time is read from the injected clock.** The classification uses the `IClock`
abstraction, never `DateTime.Now` or `DateTime.UtcNow`. The legacy code read the
ambient clock inline, which is why its date handling was untestable.

**"No date set" is two values, not one.** The legacy columns are nullable but the
legacy properties are not, so absence was carried as the sentinel
`Null.NullDate`, which is `Date.MinValue` (`Null.vb:L66-L70`). The legacy
emptiness test at `Null.vb:L183-L186` compares only the *date part* — commented
there as avoiding subtle time differences — and `SecurityRoles.ascx.vb:L281-L285`
applies exactly that test before displaying either date. The computed property
must therefore treat both `null` and `DateTime.MinValue` as "not set", comparing
the date part, or a migrated row will classify differently from its legacy self.

**Members considered and deliberately rejected:**

- **`Cancelled` / `Canceled`** — rejected. Cancellation resolves at
  `RoleController.vb:L493-L501` to *either* an expiry *or* a deleted row, never
  to a state of its own. Adding it would invent a lifecycle the data model does
  not have.
- **`Trial` / `TrialUsed`** — rejected. `IsTrialUsed` is a nullable bit on
  `UserRoles` and an orthogonal fact: an assignment can be in force *and* have
  used its trial simultaneously. It remains a separate `bool?` property on the
  assignment entity.
- **`Unknown` / `NotSet` / `Undefined` / `None`** — rejected. The three rules
  above cover every combination of the two dates, including both unset, so such
  a member would be unreachable dead code.
- **`Deleted` / `Suspended` / `Approved` / `Rejected`** — rejected. None has any
  counterpart anywhere in the legacy code.
- **No member is valued `-1`**, a number already overloaded in this codebase as
  both the integer absence sentinel (`Null.NullInteger`) and a live identity
  seed (`Portals.PortalID` is `IDENTITY(-1,1)`).

**Testing guidance.** Unit tests should assert the three-way derivation against a
fake `IClock`: a future effective date yields `Pending`, both dates null yields
`Active`, a past expiry date yields `Expired`, a past expiry date together with a
future effective date yields `Expired`, and `DateTime.MinValue` is treated as
"no date set" on either date.
