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

### Correction: the credential migration path is an administrative reset, and no credential is upgraded on sign-in

**What this entry corrects.** The *Operational consequence* paragraph of the
entry immediately above states that the adopted path is "re-hash on first
successful login, with an administrative reset as the fallback". That describes
an option that the delivered code cannot exercise. This file is append-only, so
that paragraph is left in place and superseded here rather than reworded; where
the two disagree, this entry is the one that matches the shipped behaviour.

**Why re-hash on first login is unreachable.** Upgrading a credential on
sign-in requires *first verifying it in its legacy form*, which requires the
Triple-DES key from `Website/release.config:L89-L93` and the legacy provider's
decryption path. Both are out of scope: the DotNetNuke 4.x authentication
providers are replaced wholesale, and `AspNetMembershipProvider.vb` and
`DNNMembershipProvider.vb` are read-only reference inputs that contribute no
target file. The delivered `IPasswordHasher` implementation therefore holds
exactly one verification algorithm - BCrypt - and no legacy-format branch exists
for a legacy digest to be verified by, let alone upgraded from. Carrying the
Triple-DES key into a new configuration file purely to enable a silent upgrade
would also reintroduce the committed-key weakness that the entry above exists to
remove.

**Target behaviour.** An account whose stored credential predates the migration
cannot authenticate. Its owner regains access through an **administrative
password reset**, which writes a BCrypt digest. That is the only supported path.
Password *retrieval* is not carried forward in any form: no endpoint, no screen
and no service method exposes it.

**Operational consequence.** Every pre-existing account requires one
administrative reset before its first sign-in after cut-over. For an
installation with a large user population this is a migration task to plan, not
an incident to discover: it should be sequenced with the cut-over and
communicated to users, because the failure mode a user sees is an ordinary
"invalid credentials" refusal with nothing to distinguish it from a mistyped
password. The legacy `PasswordFormat` enumeration is still ported member for
member with its ordinals `Clear = 0`, `Hashed = 1`, `Encrypted = 2` preserved, so
that a row's historical format remains expressible and reportable while the
resets are worked through.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Services/IPasswordHasher.cs`,
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/UserConfiguration.cs`,
`backend/src/DnnMigration.Domain/Entities/User.cs`,
`backend/src/DnnMigration.Domain/Enums/PasswordFormat.cs`,
`backend/src/DnnMigration.Application/Abstractions/IUserService.cs`,
`backend/src/DnnMigration.Application/Dtos/User/CreateUserRequest.cs`,
`backend/src/DnnMigration.Application/Dtos/User/ChangePasswordRequest.cs`,
`backend/src/DnnMigration.Application/Dtos/Auth/CurrentUserDto.cs`,
`backend/src/DnnMigration.Application/Validation/ChangePasswordRequestValidator.cs`,
`backend/src/DnnMigration.Infrastructure/DnnMigration.Infrastructure.csproj`.

### BCrypt's 72-byte input limit, and the enhanced hashing pair adopted to remove it

**Legacy behaviour.** The legacy scheme placed no length-dependent limit on how
much of a credential contributed to the stored value: Triple-DES encryption of
the whole string, and before that the whole string in plaintext, used every
character supplied.

**Target behaviour.** BCrypt's standard entry points hash at most the first
**72 bytes** of their input and *silently ignore* the remainder. Two credentials
that agree in their first 72 bytes and differ after them therefore verify against
one another - an equivalence class the legacy scheme did not have. This solution
does not accept that behaviour: it uses BCrypt's **enhanced** hashing and
verification pair, which pre-hashes the input with SHA-384 before the BCrypt
round so that every byte supplied contributes to the result.

**Why the difference is deliberate.** Silent truncation is a credential-strength
reduction that is invisible at the call site and invisible in the stored value.
The enhanced pair removes it at the cost of a digest that is not interchangeable
with a standard BCrypt digest.

**Operational consequence, and a maintenance obligation.** The two halves are a
matched pair and **must always be changed together**: a digest produced by the
enhanced path does not verify through the standard path and a digest produced by
the standard path does not verify through the enhanced one. Switching one side
alone silently locks out every account. This is the single most important
invariant in the password hasher and is stated at both call sites. Nothing else
in the target produces or consumes a BCrypt digest, so there is no third party to
coordinate with today - the obligation is on any future change.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Services/IPasswordHasher.cs`.

### A credential length ceiling, where the legacy stack had none

**Legacy behaviour.** No maximum credential length was enforced anywhere in the
legacy request path. The provider registration in
`Website/release.config:L236-L246` sets a *minimum* length of seven and nothing
else, and the legacy screens carried no maximum-length validator.

**Target behaviour.** A single shared ceiling of **256 UTF-8 bytes** is applied
to every credential-bearing field at the point of request validation, before any
service or hasher is reached. It is expressed in *bytes*, not characters,
because the algorithm's own limit is a byte limit: a character count would let a
multi-byte credential exceed a byte bound while passing the check. One constant
is the single source of truth and it is applied at sign-in, account creation,
password change and portal creation alike.

**Why the difference is deliberate.** Two reasons, and the second is the
stronger one. First, an unbounded credential is an unbounded amount of work for
a deliberately slow hash function, which is a denial-of-service surface on the
one endpoint that must remain available. Second, a bound stated *once* and
enforced *at every entry point* is the only way to be sure that no path reaches
the hasher with a value the algorithm would treat differently from the value the
user typed - it converts a property of the algorithm into a property of the API.

**Operational consequence.** A credential longer than 256 UTF-8 bytes is
refused with an ordinary field-level validation failure rather than being
truncated or accepted. No credential a person chooses approaches this bound, so
nothing legitimate is rejected; the bound exists for the machine-generated case.
This is a **tightening** with no legacy equivalent.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/CredentialBounds.cs`,
`backend/src/DnnMigration.Application/Validation/LoginRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/CreateUserRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/ChangePasswordRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/CreatePortalRequestValidator.cs`,
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`.

### Email address validation: one loosening, three tightenings, and a repaired substring defect

**Legacy behaviour.** The legacy screens validated an email address with a
regular expression whose final label - the top-level domain, or TLD - was
constrained to **two to four letters**. That pattern was authored when the
two-to-four-letter assumption held.

**Target behaviour, and the four differences.**

- **Loosened - final label (TLD) width.** The final label is accepted at **2 to
  63** letters rather than 2 to 4, so `.museum`, `.travel` and every other modern
  TLD are now accepted where DotNetNuke 4.9.0 refused them. 63 is the
  per-label maximum that the domain name system itself imposes, so this is a
  standards bound rather than an arbitrary one.
- **Tightened - empty labels.** A domain containing an empty label, such as a
  doubled separator, is now refused.
- **Tightened - oversized labels.** A label longer than 63 characters is now
  refused.
- **Tightened - oversized domains.** A domain longer than 253 characters is now
  refused.

**A defect repaired rather than ported.** The validation is now anchored so that
the **whole value** must satisfy the rule. An unanchored match would accept any
string that merely *contains* something address-shaped, so a value with leading
or trailing junk would have passed. Reproducing that would carry a validation
bypass into new code, so it is not reproduced.

**Why the differences are deliberate.** Refusing a legitimate modern address is
a functional regression that grows worse over time, and the three tightenings
reject only values the storage column and the domain name system could not
represent in the first place.

**Operational consequence.** The stored column is aligned with the terminal
schema: `Users.Email` is a **nullable `nvarchar(256)`**, per
`Website/Providers/DataProviders/SqlDataProvider/03.00.13.SqlDataProvider:L109-L110`,
and the validators, the value object and the request DTO documentation all state
that same width. The legacy provider's `requiresUniqueEmail="false"` is
**preserved**: email uniqueness is still not enforced, because enforcing it
mid-migration would reject existing rows that are already duplicated.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/ValueObjects/EmailAddress.cs`,
`backend/src/DnnMigration.Application/Validation/CreateUserRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/UpdateUserRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/CreatePortalRequestValidator.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/UserConfiguration.cs`,
`backend/src/DnnMigration.Application/Dtos/User/UpdateUserRequest.cs`.

### Password policy configuration is bounded, and two legacy flags are deliberately inert

**Legacy behaviour.** The provider registration in
`Website/release.config:L236-L246` set `minRequiredPasswordLength="7"`,
`minRequiredNonalphanumericCharacters="0"`,
`requiresQuestionAndAnswer="false"` and `requiresUniqueEmail="false"`, and any of
them could be edited to any value that the provider would accept.

**Target behaviour.** The same four values are carried over verbatim as the
configured defaults, and the *policy itself is unchanged*. What is new is that
the configuration is validated when the application starts:

- **`MinRequiredPasswordLength` may no longer be configured below 7.** The
  legacy value becomes a floor as well as a default. It may still be raised.
- **`RequiresQuestionAndAnswer` and `RequiresUniqueEmail` must both remain
  `false`.** This is the uncomfortable but honest option. No code in the target
  enforces either rule, so a `true` value would be an **inert control that looks
  active** - the most dangerous kind of security setting, because an operator
  would reasonably believe a protection was in force. Refusing the value at
  startup makes the absence visible instead.

**Why the difference is deliberate.** The legacy policy is preserved because
tightening a password policy during a migration locks out existing users, and
any hardening is left as a separate, explicit decision. The bounds exist to stop
the configuration from silently drifting *below* the legacy baseline or
advertising a control that does not exist.

**Operational consequence.** A deployment that sets any of these outside its
permitted range fails to start, with a message naming the setting. That is
preferable to starting with a weaker or imaginary policy.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Options/PasswordPolicyOptions.cs`,
`backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`,
`backend/src/DnnMigration.Application/Validation/ChangePasswordRequestValidator.cs`.

### Paging, filtering and sorting are bounded, and caller-chosen sorting is a new capability

**Legacy behaviour.** The legacy user and role administration grids paged
server-side through procedures taking a page index and page size, with the size
coming from a portal setting and no upper bound applied anywhere. The free-text
filter was likewise unbounded. Sorting was **not offered at all**: the grids
rendered a fixed column order with no sort affordance, so no caller could name a
sort field.

**Target behaviour.**

- **Page size is capped at 200.** A larger request is refused rather than
  served. The default page size is unchanged from the legacy figure.
- **Page index must be non-negative.**
- **The free-text filter is capped at 256 characters**, chosen as the width of
  the widest column any filter searches, so no legitimate query can be turned
  away by the bound.
- **Sorting is a new capability, restricted to a closed allowlist.** A caller may
  name a sort field and a direction, but only from an explicitly enumerated set
  per resource; anything else is refused. There is no free-form sort expression
  and no pass-through of caller text into a query.
- **An undefined enumeration value is refused** rather than stored. This applies
  to the paging direction and, on the portal contracts, to the user-registration
  mode and the banner-advertising mode. The legacy stack would have accepted and
  persisted an out-of-range integer in these columns.

**Why the differences are deliberate.** An unbounded page size is a
denial-of-service surface reachable by anyone who can call a list endpoint, and
an unbounded filter is an unbounded scan. The allowlist exists because the
alternative - accepting a caller-supplied field name and interpolating it into an
ordering clause - is an injection surface; enumerating the permitted fields makes
the capability safe to offer at all. Refusing undefined enumeration values keeps
a column's stored values inside the domain the enumeration claims to describe.

**Operational consequence.** A client that relied on requesting an unbounded
page must page. Because the legacy grids offered no sorting, no existing caller
can be broken by the allowlist; it can only refuse a value no legacy caller could
have sent.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/PagedRequestValidator.cs`,
`backend/src/DnnMigration.Application/Dtos/Common/PagedRequest.cs`.

### Portal create and update: field bounds, monetary range, and route-to-body identifier agreement

**Legacy behaviour.** `PortalController.UpdatePortalInfo` took **27 positional
arguments** and `CreatePortal` took 15, with the portal identifier supplied as
one argument among the rest and no cross-check against the page being edited.
Field widths were enforced only by the storage columns, so an oversized value
became a storage error rather than a validation failure. The portal
administrator's password minimum was checked *after* the portal, its aliases, its
roles and its initial tabs and modules had already been written, and a failure at
that point surfaced as an exception.

**Target behaviour.**

- **Every field is bounded to the width of its terminal column**, taken from the
  88-script upgrade chain rather than from the earliest `CREATE TABLE`. The
  values that matter most: `PortalName` `nvarchar(128)`, `HomeDirectory`
  `varchar(100)` and therefore **non-Unicode**, `Currency` `char(3)`,
  `DefaultLanguage` `nvarchar(10)` at its widened terminal size.
- **`HostFee` is range-checked against SQL Server's `money` domain.** An
  oversized fee is now a field error instead of an arithmetic overflow at the
  storage layer.
- **The portal administrator password minimum is a declarative field rule.** The
  *threshold is unchanged* - the legacy minimum of seven - but it is now reported
  as a `400` field error **before any portal artefact is written**, rather than
  as an exception raised after several tables had already been populated.
- **The route identifier and the body identifier must agree.** A request that
  addresses one portal in its path and names a different one in its payload is
  refused. The legacy Sub had no equivalent check because it had no route.
- **`ProcessorPassword` is bounded strictly**, and the request contract states
  plainly which layer is responsible for keeping it out of logs, rather than
  leaving that ownership implicit.

**Why the differences are deliberate.** Validating before writing is the whole
point of having a validation layer: a partially created portal is worse than a
refused one, because it leaves a tenant half-provisioned with no transaction to
undo it. Identifier agreement closes a confused-deputy shape in which a caller
authorised for one tenant submits a payload naming another.

**Operational consequence.** A caller that previously discovered a too-short
administrator password by receiving a server error now receives a field-level
`400` with the offending field named, and no portal rows are created. A caller
that relied on the route and body disagreeing - which no legacy caller could
have done - is refused.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/UpdatePortalRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/CreatePortalRequestValidator.cs`,
`backend/src/DnnMigration.Application/Dtos/Portal/UpdatePortalRequest.cs`.

### A session's total length is now bounded absolutely, not re-stamped on every use

**Legacy behaviour.** Authentication was a Forms authentication ticket. The
registration in `Website/release.config:L147` sets a 60-minute timeout, and the
ticket's expiry was *renewed on activity*, so a continuously active session had
no maximum length at all - it ended only when the user stopped using it for
longer than the timeout.

**Target behaviour.** A refresh-token **family** is created at sign-in with a
single absolute deadline computed once, and **every rotation child inherits that
same deadline unchanged**. Rotating a refresh token issues a new token but cannot
move the family's expiry, so the total length of a session is fixed at the moment
it begins. A rotation attempted at or after the deadline is refused and the family
is finished.

**Why the difference is deliberate.** A sliding expiry means a stolen refresh
token can be kept alive indefinitely simply by using it, which converts a
one-time theft into permanent access. Anchoring the deadline at family creation
puts a ceiling on the value of a stolen token.

**Operational consequence.** A user working continuously is signed out when the
absolute refresh lifetime elapses and must authenticate again, which the legacy
application never did to them. The access-token and refresh-token lifetimes are
configurable within the bounds recorded further below.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs`,
`backend/src/DnnMigration.Application/Abstractions/ITokenService.cs`.

### Refresh-token state is held in process: restart, replica and retention behaviour

This is an **operational limitation**, stated here as a deployment requirement
rather than described as a feature.

**Legacy behaviour.** There was no server-side session state to lose. A Forms
authentication ticket is a self-contained cookie: the server validated its
signature and did not record its existence, so an application restart or a second
web server changed nothing about who was signed in.

**Target behaviour, and its three consequences.** Refresh-token families are
held in the **memory of a single process**. Therefore:

- **Restart-lossy.** Restarting or redeploying the API discards every refresh
  family. Every signed-in user must authenticate again. Access tokens already
  issued remain valid until they expire, because they are verified by signature
  and not by lookup - so the visible symptom is that sign-in survives for up to
  the access-token lifetime and then stops.
- **Replica-local.** A refresh token can only be redeemed by the process that
  issued it. Running two or more API replicas without sticky routing produces
  refresh failures that look intermittent and are not.
- **Retention is bounded by the configured lifetime, not by process lifetime.**
  An expired family is **removed in full** - its token records, the family entry
  and its entry in the per-user index - and the consumed-token digests kept for
  replay detection are bounded to the replay-detection window rather than
  accumulating for the life of the process. Nothing grows without limit.

**Why it is built this way.** A durable store would need a table, and the
governing constraint of this migration is that the existing SQL Server schema is
immutable: no `CREATE TABLE`, `ALTER TABLE` or `DROP` reaches it, and the
target's entity inventory is a fixed set of 21 entities mapped onto legacy tables
with no refresh-token table among them. There is therefore no sanctioned place to
persist this state. The in-process store is the honest implementation of that
constraint, and the deployment topology it requires is the one the repository
already ships: `docker/docker-compose.yml` defines a **single** `api` service.

**Deployment requirement.** Run one API instance, or introduce a durable or
shared store before running more than one. Treat a restart as a sign-out event.

**A contract detail that follows from this.** The token contract publishes a
`TOKEN_STORE_UNAVAILABLE` failure reason. With the in-process store that reason
is **unreachable**: an in-memory write has no failure mode to report. It is
retained because it is the correct code for a durable substitute store to return,
and a caller that handles it today is handling a case it will need later. The
contract says exactly this rather than implying the store can fail.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Abstractions/ITokenService.cs`,
`backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs`.

### Rotation cannot change identity, tenant or host-level authority

**Legacy behaviour.** The Forms authentication ticket carried the user name, and
role membership was resolved separately on each request. There was no rotation
step, so the question did not arise.

**Target behaviour.** When a refresh token is redeemed, the identity and tenant
of the new token are taken **from the stored family record**, never from the
request. There is no parameter through which a caller could nominate a different
user, portal or login name, so redemption cannot be used to change who the
session belongs to or which tenant it addresses. Separately, the host-level
(`IsSuperUser`) flag is **re-read on every redemption** rather than copied
forward from the previous token, so authority that has been withdrawn does not
survive a rotation.

**Why the difference is deliberate.** A rotation endpoint that accepts identity
input is a privilege-escalation surface: the caller presents a token they hold
and asks for one describing someone else. Re-reading the host flag closes the
narrower version of the same problem, where a demoted account keeps its elevated
claims until its session happens to end.

**Operational consequence.** Withdrawing host-level authority now takes effect
at the next rotation rather than at the next sign-in. Role membership more
generally is still evaluated per request against the database, so it is not
cached in a token at all.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs`.

### Revoking a session is now an obligation of credential and account changes

**Legacy behaviour.** There was nothing to revoke, and the consequences were
real. `Library/Components/Users/UserController.vb:L103` changed a password and
left the caller's existing Forms authentication ticket valid;
`Website/admin/Users/Membership.ascx.vb:L243` cleared an account's approval flag
and likewise left every existing ticket valid. A stateless signed cookie cannot
be withdrawn, so the legacy application had no way to end a session it had
already granted - a password change did not evict an attacker who was already
signed in.

**Target behaviour.** A per-user index of refresh families exists specifically so
that **every** family belonging to an account can be revoked atomically in one
operation, and replaying an already-consumed refresh token revokes its entire
family rather than merely refusing the one token.

**The obligation this creates.** Revoke every refresh family of the account on
each of: **a credential change, an administrative password reset, withdrawal of
approval, and account deletion.** This is a **net-new requirement** with no
legacy counterpart, and it is recorded here because it is an obligation on code
that consumes the token service rather than a behaviour of the store itself - the
store supplies the capability, and the account and credential workflows must
invoke it.

**Operational consequence.** Because access tokens are verified by signature and
not by lookup, revocation ends the *renewal* of a session, not the current access
token. The window between revocation and effective lockout is therefore the
access-token lifetime, which is why that lifetime is capped rather than left to
configuration.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Abstractions/ITokenService.cs`,
`backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs`.

### Portal alias resolution: exact matching is preserved legacy behaviour, and ambiguity is refused

The alias story has two stages, and **only the second one contains the
divergence**. Both are stated, because reporting the superseded first stage as
though it were current would misdescribe both the legacy system and the target.

**Stage one, superseded.** The earliest tenant-resolution procedure,
`GetPortalSettings` at
`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600`,
selected the lowest portal key whose alias column satisfied a **substring
containment** predicate at line 4582, reading the `Portals.PortalAlias` column.
Because the supplied value was interpolated into the pattern, the wildcards were
caller-controlled, and a fragment of one tenant's alias could resolve to a
different tenant. **Neither the procedure nor the column survives**: the
procedure was dropped outright at `02.02.00.SqlDataProvider:L267` and the column
was dropped at `02.02.02.SqlDataProvider:L3925-L3926`.

**Stage two, terminal.** Once aliases lived in the `PortalAlias` table, the
lookups compared **whole values**: `GetPortalAlias` at
`02.02.02.SqlDataProvider:L3846-L3856` and `GetPortalByAlias` at
`02.02.02.SqlDataProvider:L3930-L3938` both write `HTTPAlias = @HTTPAlias`, and no
later script recreates either with different semantics. The schema agrees: a
unique non-clustered index on `HTTPAlias` is added at
`03.00.07.SqlDataProvider:L14-L18`.

**Therefore exact whole-value matching is preserved legacy behaviour, not a
change to it.** The replacement performs a plain equality lookup and leaves case
behaviour to the column's collation, exactly as those procedures left it.

**The genuine divergence.** The terminal lookup still collapsed multiple
candidates with `min(PortalId)`. The replacement **refuses ambiguity** instead of
silently serving whichever portal was created first: it probes for a second
matching row and, if one exists, ends the request rather than guessing.

**Why the difference is deliberate.** Serving the lowest-keyed candidate for an
ambiguous host is a silent cross-tenant data-exposure path, and its silence is
the problem - nothing distinguishes a correct resolution from a wrong one. The
unique index means a correctly configured installation can never present two
candidates, so this refusal can only fire on a database that is already
misconfigured, where failing is the right answer.

**A further limitation, stated rather than implied.** Resolution reads the
request host only. **Virtual-path aliases - the DotNetNuke child-portal form
where a tenant is addressed by a path segment - are not resolved.** An
installation that relies on them requires additional work before this middleware
can serve it.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Services/PortalContextAccessor.cs`,
`backend/src/DnnMigration.Domain/Entities/PortalAlias.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Services/IPortalContext.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IPortalAliasRepository.cs`,
`backend/src/DnnMigration.Api/Middleware/PortalAliasResolutionMiddleware.cs`.

### The `-1` "all portals" alias wildcard becomes an explicit member

**Legacy behaviour.** One provider member served two semantically different
questions, because the wildcard was baked into the SQL predicate itself.
`GetPortalAliasByPortalID` at
`Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3861`
reads `where (PortalID = @PortalID or @PortalID = -1)`, so passing `-1` returned
**every alias in the installation** rather than the aliases of the portal keyed
`-1`. The caller that wanted the whole inventory obtained it exactly that way:
`Library/Components/Portal/PortalAliasController.vb:L86-L88` is
`GetPortalAliases()` returning `GetPortalAliasByPortalID(-1)`.

**Why that is dangerous here specifically.** `-1` carries **three unrelated
meanings** in this codebase, and the legacy helper could not tell them apart.
It is the absence sentinel `Null.NullInteger`
(`Library/Components/Shared/Null.vb:L41-L45`), and `Null.IsNull(-1)` returns
`True` at `:L208-L211`. It is a **genuine portal key**, because `Portals.PortalID`
is declared `IDENTITY(-1, 1)` at `01.00.00.SqlDataProvider:L77`. And it is this
wildcard. A contract that accepted `-1`, or a nullable stand-in for it, would
leave every call site ambiguous as to which of the three was meant.

**Target behaviour.** The two questions are **two members**.
`GetByPortalIdAsync(int portalId, …)` returns the aliases of exactly the portal
bearing that identifier - including when the identifier legitimately is `-1` or
`0` - and `GetAllAsync(…)` returns every alias across every portal and takes no
identifier at all. **`GetByPortalIdAsync(-1)` means the portal whose key is `-1`;
it does not mean "all portals".** No magic value, and no nullable discriminator
standing in for one, appears in the contract.

**Why the difference is deliberate.** A shared member distinguished only by a
special argument value hides the caller's intent at the call site: a reader must
know that one particular integer is special before the code can be understood,
and a mistyped identifier silently widens a tenant-scoped query into an
installation-wide one. Splitting the member makes the intent syntactic, so the
compiler and the reader both see which question was asked. The Application layer
still offers an optional scope on its own listing endpoint, but it resolves that
option into one of the two explicit reads rather than forwarding a sentinel.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IPortalAliasRepository.cs`,
`backend/src/DnnMigration.Infrastructure/Repositories/PortalAliasRepository.cs`,
`backend/src/DnnMigration.Application/Services/PortalService.cs`.

### The install-time alias rewrite is not carried forward

**Legacy behaviour.** The provider declared two update members whose names are
**swapped relative to the procedures they execute**, which makes the shape easy to
misread. `UpdatePortalAlias` at
`Library/Components/Providers/Data/DataProvider.vb:L359` takes a single string and
executes the procedure named `UpdatePortalAliasOnInstall`
(`Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb:L1312-L1314`),
whose body at `02.02.02.SqlDataProvider:L4069` is
`update PortalAlias set HTTPAlias = @PortalAlias where HTTPAlias = '_default'` -
a blind rewrite of the placeholder row shipped with a fresh database.
`UpdatePortalAliasInfo` at `:L360` is the **real** per-row update: it executes the
procedure actually named `UpdatePortalAlias`
(`SqlDataProvider.vb:L1315-L1317`), whose body at
`02.02.02.SqlDataProvider:L4096` updates one identified row.

**Target behaviour.** The install-time member is **omitted**, and the per-row
member is surfaced as `UpdateAsync`. Its only caller in the entire repository is
the fresh-install branch at `Library/Components/Portal/PortalSettings.vb:L1128`,
guarded by a test that the alias table holds nothing but the `_default`
placeholder, and installation is out of scope for this migration.

**Why the difference is deliberate.** The omitted member matches rows **by
content rather than by key** and rewrites every row whose host name is the
placeholder. Outside the first-run condition it is unsafe by construction, and
retaining it would put a key-less bulk rewrite of the tenant-routing table on a
contract whose every other member addresses exactly one row.

**Operational consequence.** Provisioning the first alias of a new installation
is an installer or operator task, not something the API performs. An installation
whose alias table still holds the shipped `_default` placeholder must have it set
by whatever provisions the database, after which every later change goes through
`UpdateAsync`.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IPortalAliasRepository.cs`,
`backend/src/DnnMigration.Infrastructure/Repositories/PortalAliasRepository.cs`.

### Tenant context must be complete, and a resolution failure ends the request

**Legacy behaviour.** The per-request `PortalSettings` object was assembled from
whatever the lookup returned and carried the legacy absence sentinels forward
where a value was missing - an empty string for an absent string and `-1` for an
absent integer. A portal with an incomplete row was therefore served, with
sentinels standing in for the missing facts, and the consequences appeared later
and elsewhere.

**Target behaviour.** The tenant context is **immutable and complete or it does
not exist.** A portal missing any of the six facts the context requires is
refused with the reason code `PORTAL_CONTEXT_INCOMPLETE` rather than served with a
fabricated value. Resolution failure ends the request: an unknown host produces
`404`, and an ambiguous or incomplete portal produces `500`. In both cases the
response body **discloses nothing** about which host was requested, which portals
exist, or which fact was missing; the correlation identifier is the only link to
the internal record. Reading the context before resolution has run throws rather
than returning a partially populated object.

**Why the difference is deliberate.** A sentinel that survives into business
logic is a defect with a delayed fuse, and a tenant-resolution error message that
names hosts or portals is a tenant-enumeration oracle available to anyone who can
send a request.

**Operational consequence.** An installation with an incomplete `Portals` row
will see that tenant fail closed rather than behave oddly. The failing request
carries a correlation identifier, and the log entry names the missing fact.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Services/IPortalContextHolder.cs`,
`backend/src/DnnMigration.Infrastructure/Services/PortalContextHolder.cs`,
`backend/src/DnnMigration.Api/Middleware/PortalAliasResolutionMiddleware.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Services/IPortalContext.cs`.

### Portal administration is decided by role identifier and portal, not by role name

**Legacy behaviour.** Administrative screens tested membership of a role
identified **by name**. That is unsound in this schema, and provably so:
`Roles.RoleName` is an `nvarchar(50)` with **no unique index in any of the 88
upgrade scripts**, while `Roles.PortalID` is nullable. A name is therefore not an
identity - two portals may each own a differently privileged role with the same
name, and an administrator of one portal could satisfy a name-based check while a
different portal was being served.

**Target behaviour.** Authorisation for portal administration is decided by an
authoritative per-request database check anchored on the **role identifier
together with the portal identifier**, evaluated against the resolved tenant.
The framework's own name-based role requirement is deliberately **not** used, and
the policy is built from an explicit requirement and handler instead. A request
with no resolved tenant is **denied**, never allowed by default.

**Why the difference is deliberate.** This is a cross-tenant privilege
escalation in the legacy shape, not a stylistic preference. Anchoring on the
identifier pair is the only formulation the schema can guarantee.

**A legacy bypass deliberately not reproduced.** The canonical legacy site
contained a super-user branch whose condition was itself defective - it
redirected super users *away* from the screen they were entitled to use. Rather
than port a broken bypass, no super-user bypass exists: host-level accounts are
authorised through the same policy.

**Operational consequence.** One indexed existence check per protected request.
The role identifier for a portal's administrator role is a column on the portal
row itself (`Portals.AdministratorRoleId`), so the check needs no name lookup.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Authorization/PortalAdministratorRequirement.cs`,
`backend/src/DnnMigration.Api/Authorization/PortalAdministratorAuthorizationHandler.cs`,
`backend/src/DnnMigration.Api/Authorization/PolicyNames.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IRoleRepository.cs`.

### Role membership validity windows are evaluated in coordinated universal time

**Legacy behaviour.** `GetRolesByUser`, in its terminal form at
`04.00.04.SqlDataProvider:L429-L445`, compared a membership's effective and
expiry dates against `getdate()` - the **database server's local time** - and
legacy rows were written in local time to match.

**Target behaviour.** The window is evaluated in **coordinated universal time**,
against an injected clock, because every date this solution writes is produced in
that form. The window itself is applied exactly as the legacy procedure applied
it: membership counts when its start is absent or has passed and when its end is
absent or has not passed, and an absent bound means *unbounded in that
direction*, never "now".

**Why the difference is deliberate.** Mixing local and universal time inside one
comparison is how off-by-one-hour authorisation defects are made. A single time
base, supplied by an abstraction so it can be fixed in a test, is the only
formulation that is reproducible.

**Operational consequence, and it is a real one.** Against a legacy database
whose rows were written by a server whose local time is not universal time, **the
window is displaced by that server's offset.** A membership therefore appears to
begin or end by that offset earlier or later than it did before. Installations
with dated role memberships should expect this and, where it matters, normalise
the stored dates.

**A related distinction not to conflate.** The portal predicate differs between
two legacy procedures and they must not be merged: `GetRolesByUser` requires
`Roles.PortalId = @PortalId`, whereas `GetPortalRoles` at
`04.08.00.SqlDataProvider:L40` additionally admits a role with **no** owning
portal. The membership check reproduces the first.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IRoleRepository.cs`,
`backend/src/DnnMigration.Infrastructure/Repositories/RoleRepository.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Services/IClock.cs`,
`backend/src/DnnMigration.Infrastructure/Services/SystemClock.cs`.

### Token configuration is validated at startup, and what a deployment must supply

**Legacy behaviour.** There was no startup validation of security configuration
at all. The 3DES `decryptionKey` that protected every stored password was
**committed to source control** in `Website/release.config:L89-L93`, and the
application started regardless of what any of these values were.

**Target behaviour.** The token configuration is bound to a typed options object
and validated **when the application starts**, so a weak or missing value stops
the process instead of producing a silently insecure deployment.

**What a deployment must supply.**

- **Signing key.** Must be present, at least **32 UTF-8 bytes**, contain at least
  **8 distinct characters**, and must not contain any of twenty well-known
  placeholder fragments. The placeholder check matches by **containment**, case
  insensitively, rather than by equality - equality is trivially defeated by
  padding a placeholder with a few characters, which is exactly what someone in a
  hurry does.
- **Issuer and audience.** Both must be present and non-blank.
- **Access-token lifetime.** Between 1 and **60 minutes**. Sixty is not an
  invented figure: it is the documented default *and* exactly the legacy Forms
  authentication ticket timeout at `Website/release.config:L147`. So this is
  net-new *enforcement* of a figure the legacy application already used.
- **Refresh-token lifetime.** Between 1 and **30 days**. This bound has no legacy
  precedent; it exists because the refresh family's absolute deadline is the
  ceiling on a session's total length.

**A reviewer suggestion deliberately declined.** Requiring the issuer and the
audience to *differ* was considered and rejected. This API is a
backend-for-frontend with a single client, and the repository's own published
configuration - `docs/technical-specifications.md:L1036-L1037` and
`docker/.env.example` - sets both to `DnnMigration`. Enforcing distinctness would
make the sanctioned configuration fail to start, which is a worse outcome than
permitting a same-value pair in a single-audience deployment. The reasoning is
recorded at the validation site so that the omission reads as a decision rather
than an oversight.

**Operational consequence.** A deployment that has not set a signing key, or has
set a placeholder, or has configured a lifetime outside these bounds, **fails to
start with a message naming the setting.** This is intended: a container that
refuses to start is visible, and a container running on a guessable key is not.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Options/JwtOptions.cs`,
`backend/src/DnnMigration.Api/Extensions/AuthenticationExtensions.cs`.

### Cross-origin policy, forwarded headers and transport security

**Legacy behaviour.** The legacy application was same-origin by construction -
one IIS site served both the markup and the data - so it needed no cross-origin
policy and had none.

**Target behaviour, and three decisions.**

- **The allowed-origin list defaults to empty, and credentials are never
  enabled.** The containerised topology still needs no cross-origin access: the
  supplied `docker/nginx.conf` proxies `/api/` to the API service, so the browser
  addresses the API through the same origin that served the application. A named
  policy exists for the development case; permissive origins combined with
  credentials are refused outright, because that combination is the standard way
  a cross-origin policy becomes a vulnerability.
- **A forwarded host header is deliberately not honoured.** Tenant resolution
  reads the request host, so trusting a caller-supplied forwarded host would let a
  caller select which tenant to be served - a direct cross-tenant hazard. The
  forwarded protocol and client address are handled separately from the host for
  exactly this reason.
- **HTTPS redirection is off by default and HSTS is on outside development.**
  Redirection is deliberately configuration-gated with a default of **off**,
  because the container health probe requests `/health` over plain HTTP inside the
  container network: with redirection on, the probe receives a redirect to a port
  that is not listening, the container is marked unhealthy, and the frontend
  service - which waits on `service_healthy` - never starts. Transport security is
  the reverse proxy's responsibility in this topology, and HSTS is emitted so a
  browser will not downgrade.

**Operational consequence.** A deployment that terminates TLS at the API rather
than at a proxy must enable redirection explicitly *and* adjust the health probe.
A deployment that serves the SPA from a different origin than the API must add
that origin to the allowed list; nothing works cross-origin by default.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Extensions/CorsExtensions.cs`,
`backend/src/DnnMigration.Api/Extensions/ApplicationBuilderExtensions.cs`,
`backend/src/DnnMigration.Api/appsettings.json`,
`backend/src/DnnMigration.Api/appsettings.Production.json`.

### Rate limiting on credential endpoints, as the compensating control for the removed CAPTCHA

**Legacy behaviour.** The legacy sign-in screen carried an optional CAPTCHA
control, and `UserController.ValidateUser` took the CAPTCHA outcome as one of its
arguments. The DotNetNuke CAPTCHA control is a Web Forms server control and is out
of scope, so the argument disappears along with it - and with it the only
automated-guessing defence the sign-in path had. Removing a control without
replacing it would be a silent security reduction, so a replacement is provided.

**Target behaviour.** Credential endpoints are rate limited by two chained
limiters: a **fixed window of 30 write requests per minute** per observed client
address, and a **concurrency bound of 4** simultaneous requests. A refused
request receives `429` with a `Retry-After` header and a problem-details body
that **discloses nothing** - it does not reveal whether the account exists, nor
echo the submitted user name or credential.

**Why these numbers.** They are sized *pessimistically for the shared-address
case*, which is the honest way to choose them given the limitation below: a
window generous enough not to lock out a legitimate office behind one address,
and a concurrency bound low enough that a burst of parallel guesses is throttled
regardless of the window.

**A limitation to understand before relying on it.** The partition key is the
address **this process observes**. Behind a reverse proxy - which is the shipped
topology - that is the proxy's address, so the fixed window becomes a
**deployment-wide budget rather than a per-client one**. The concurrency bound is
unaffected by this, because it is a single process-wide limit by design. A
deployment that needs per-client windows must forward and trust the client
address at the proxy and configure the known-proxy set accordingly. Partitioning
by user name was considered and **not** adopted: it makes the limiter's own
behaviour an account-existence oracle.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Extensions/RateLimitingExtensions.cs`,
`backend/src/DnnMigration.Application/Dtos/Auth/LoginRequest.cs`,
`backend/src/DnnMigration.Application/Validation/LoginRequestValidator.cs`.

### Request size ceiling, and refusals of unsafe portal path configuration

**Legacy behaviour.** Request size was governed by the `httpRuntime`
`maxRequestLength` setting in the legacy configuration. Portal path settings were
accepted as configured, with no check that a per-tenant path format actually
distinguished one tenant from another.

**Target behaviour.**

- **A 1 MiB request-body ceiling** is set explicitly. The framework default is
  30 MB, which is far larger than any request this API accepts - the largest
  payload is a portal update - so the default is a needlessly wide surface.
- **A portal home-directory format missing its tenant placeholder is refused at
  startup.** A format string with no substitution point resolves to the *same*
  directory for every tenant, which is a cross-tenant file-exposure defect
  configured rather than coded. It is refused rather than tolerated.
- **A path value containing a parent-directory traversal segment is refused.**

**Why the differences are deliberate.** Each turns a configuration mistake that
would previously have produced quiet, wrong behaviour into a loud startup
failure.

**Operational consequence.** A deployment accepting genuinely large payloads
must raise the ceiling deliberately. A deployment whose portal path format lacks
a placeholder must fix it before the API will start.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`,
`backend/src/DnnMigration.Application/Options/PortalOptions.cs`.

### API documentation exposure is off by default outside development, and authenticated when on

**Legacy behaviour.** There was no API surface to document and no equivalent
artefact.

**Target behaviour.** The generated OpenAPI document and its user interface are
**enabled in development and off by default elsewhere**. Where a deployment
enables them deliberately, they are reachable **only to an authenticated
caller** - the fallback authorisation policy applies to them as it does to any
other path.

**Why the difference is deliberate.** A published schema of every endpoint,
parameter and error shape is a reconnaissance aid. Two protections are applied
rather than one, because either alone is a single misconfiguration away from
publishing the schema.

**An implementation detail worth recording.** The extension previously accepted
an arbitrary boolean saying whether to expose the documentation, which let any
caller assert exposure without reference to the environment or configuration.
That overload was **removed**; exposure is now derived from validated environment
and configuration policy only.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Extensions/SwaggerExtensions.cs`,
`backend/src/DnnMigration.Api/Extensions/ApplicationBuilderExtensions.cs`.

### A rejected value is never echoed, in a response or in a log

**Legacy behaviour.** `Website/ErrorPage.aspx` disclosed the product version in
its heading and echoed sanitised query-string values back to the caller. The
posture was to show the user what they had sent.

**Target behaviour, in four parts.**

- **No refusal echoes the value that caused it.** A validation failure names the
  clause that failed, never the text that failed it. This is a stronger guarantee
  than redaction: where a message is chosen from a closed set of fixed sentences,
  the rejected value influences *which* sentence is chosen and nothing at all
  about its content, so there is no value present to redact incorrectly.
- **A domain exception's own message is never published to a caller.** Every
  `400` raised from a broken invariant carries **one fixed sentence** plus the
  correlation identifier; the original text reaches the structured log only. The
  alternative - passing the message through because "our own code wrote it" -
  requires every author of every such message, now and in future, to omit every
  caller-supplied value. That is an unenforceable property of human discipline
  rather than a property of the code, and this solution had already failed to
  hold it. The guarantee is now **structural**.
- **Per-field validation messages are unchanged.** They still carry field names
  and their authored messages, because they travel a different path and their text
  is bounded validator prose rather than arbitrary exception text. The two paths
  are deliberately *not* harmonised.
- **Request logging records whether a query string was present, never its
  content**, and the framework's own per-request log is suppressed below warning
  outside development **as a privacy control** rather than as noise reduction -
  its default entries include the full request path and query.

**Why the differences are deliberate.** A message that echoes input is the
mechanism by which credentials, email addresses and injected log lines reach a
place they were never meant to be. The correlation identifier preserves the
ability to support a user without disclosing anything to them.

**A distinction the legacy sentinel module made impossible.** Parsing a portal
handle now distinguishes **absent** text from **malformed** text. The legacy
`Null` module represented an absent string *as the empty string*
(`Library/Components/Shared/Null.vb:L61-L65`), so the two cases were the same
value and could not be reported differently.

**Operational consequence.** Support requires the correlation identifier from
the response header to find the corresponding log entry. A caller cannot
self-diagnose a malformed value from the response text alone; that is the
intended trade.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/ValueObjects/PortalGuid.cs`,
`backend/src/DnnMigration.Domain/ValueObjects/EmailAddress.cs`,
`backend/src/DnnMigration.Domain/Common/DomainException.cs`,
`backend/src/DnnMigration.Api/ErrorHandling/GlobalExceptionHandler.cs`,
`backend/src/DnnMigration.Api/Middleware/RequestLoggingMiddleware.cs`,
`backend/src/DnnMigration.Api/Middleware/CorrelationIdMiddleware.cs`.

### Cache diagnostics identify an entry by category and keyed fingerprint, never by key

**Legacy behaviour.** The legacy `DataCache` produced **no diagnostics at all** -
no logging and no exception text mentioning a key - so there was nothing to
preserve. What it did have was keys built from user-supplied data: the user entry
is keyed by portal and **user name**.

**Target behaviour.** Where a cache entry has to be identified in a message or a
log, it is identified by its **declared category** plus a truncated **keyed
fingerprint** of the key, never by the key. The fingerprint is an HMAC computed
with a secret generated freshly **per process** and never configurable.

**Why an unkeyed digest was rejected.** These keys are short and highly
predictable - a user cache key is a guess, not a search - so a plain hash of one
is reversible by anyone holding the log and a list of candidate user names. A
keyed construction removes that. The secret is deliberately not configurable
because a configured value would be shared between deployments and could be
committed to source control, which is the failure this whole area exists to avoid.

**The trade-off, stated plainly.** Two log lines about the same key correlate
**within a process**, and deliberately **do not** correlate across a restart or
between replicas. Losing cross-restart correlation is the price of the property
and is accepted, not overlooked.

**A related decision.** The refresh-token store can report family and record
counts. Those statistics are deliberately **not** published on `/health`, because
that endpoint must remain anonymous for the container probe to work, and
publishing session counts there would disclose usage and scale to unauthenticated
callers.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Services/MemoryCacheService.cs`,
`backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs`.

### Rate-limit refusals carry the same problem-details vocabulary as every other error

**Legacy behaviour.** No equivalent; the legacy application had no rate limiting
and no machine-readable error contract.

**Target behaviour.** Every error response in this API is an RFC 7807 problem
document with the same member vocabulary. A `429` is no exception: it is built
through the registered problem-details factory rather than assembled by hand, so
it carries `type`, `title`, `status`, `detail` and the correlation trace
identifier exactly as a `400` or a `500` does.

**A framework detail that made this necessary.** .NET 8 ships a default
problem-type link for sixteen status codes and **429 is not among them** - the
built-in set covers the codes defined in RFC 9110, and 429 is defined in RFC 6585
instead. A mapping is therefore registered explicitly, pointing at RFC 6585
section 4, rather than left to a default that does not exist. This was found by
inspecting the framework's actual mapping table rather than assumed.

**Why it matters.** A client that parses error responses uniformly would
otherwise have to special-case the one status code that arrives without a `type`,
and the omission was invisible until a response was actually inspected.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Extensions/RateLimitingExtensions.cs`,
`backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`.

### Frontend environment configuration: the twin-file convention survives, its contents do not

**Legacy behaviour.** The legacy application shipped `Website/release.config` and
`Website/development.config` - one configuration file per environment, selected at
deployment. Those files held the database connection string, the 3DES decryption
key, the provider registrations and the machine keys, because a **server** read
them.

**Target behaviour.** The twin-file convention is preserved in *shape*:
`frontend/src/environments/environment.ts` is the production configuration and
`frontend/src/environments/environment.development.ts` replaces it in a
development build. Its *scope* is radically narrower. These files compile **into a
browser bundle**, so their contents are readable by anyone who loads the
application: a secret placed here is a published secret. They therefore carry
only values that are already public - a production flag and the API base URL - and
that constraint is stated at the top of both files. This is a **net-new constraint
with no legacy equivalent**, and it is the single most important thing to know
before adding a field to them.

**The production API base URL (`apiBaseUrl`) is relative by requirement, not by
preference.** `docker/nginx.conf` proxies `/api/` to the API service, so the
relative `apiBaseUrl` of `/api/v1` keeps every call same-origin. An absolute value would either name a
container host that a browser cannot reach, or point at another origin and thereby
require a cross-origin policy that this topology is deliberately configured not to
need - and the failure would appear only at runtime in a container, because no
build step can detect it. The development value is absolute
(`http://localhost:8080/api/v1`) precisely because there is no proxy in that
configuration; `localhost` rather than a loopback literal, because a browser
treats the two as different origins.

**A structural limitation to be aware of when editing them.** The two files
cannot share a type declaration. Angular's file replacement substitutes one for
the other, so a type imported from the production file would, after substitution,
resolve to the *substituted* file itself. The shape is therefore annotated
inline in both, and **the duplication is a real drift risk that nothing in the
toolchain detects** - the two must be edited together. They are annotated with
`boolean` and `string` rather than made literal constants, because literal types
would give the production flag the type `true` in one build and `false` in the
other, turning every branch that tests it into provably dead code in one
configuration.

**Annotated in code at.**
`frontend/src/environments/environment.ts`,
`frontend/src/environments/environment.development.ts`.

### Dependency feed trust, package source mapping, and why advisories are not build gates

**Legacy behaviour.** The legacy application had no package manager at all. Its
dependencies were pre-built assemblies referenced directly from the project file
and resolved from the global assembly cache and a private probing path declared in
`Website/release.config`. There was no manifest, no lock file and no notion of a
package source, so there was nothing to trust or distrust.

**Target behaviour - package source trust.** A repository-controlled
`NuGet.Config` now exists at the repository root. It clears every inherited
source, declares the single public source the dependency inventory was pinned
against, clears any inherited disabled-source list, and then constrains which
package identities that source may serve through **package source mapping**
covering the complete transitive closure of the six projects. There is
deliberately **no wildcard pattern**: an identity that matches no declared pattern
cannot be restored at all, which is what makes the mapping a control rather than a
comment. Adding a dependency therefore requires declaring its pattern in the same
commit - intended friction, because that is the moment a new package identity gets
reviewed.

**What that does not cover, stated rather than implied.** It does not govern the
restore performed inside the API container image. `docker/api.Dockerfile` is
reproduced verbatim from the requirements: it copies only the build props, the
solution file and the six project files before running `dotnet restore`, and the
later publish step suppresses restore entirely. No repository-root file is present
at the moment packages are resolved there, and the Dockerfile is not ours to
change. The mitigating fact is that the .NET SDK Alpine image ships with the same
single public source as its only feed, so the container restore already has the
narrow feed set; what it lacks is the mapping.

**Target behaviour - advisories are deliberately not build gates.** Neither the
npm nor the NuGet audit is wired into the build, and the reason is specific rather
than a general reluctance:

- A **pristine, freshly scaffolded** Angular workspace at the mandated version
  reports 48 advisories before a single line of application code exists, the
  overwhelming majority of them build-toolchain transitives that never reach a
  browser bundle.
- Exactly **one** root advisory touches a runtime dependency - a client-hydration
  advisory against the framework core whose affected range covers *every* release
  of the mandated minor version, with no remedy inside the mandated major version.
- That vector was verified **unreachable** rather than assumed so: the workspace
  is built without server-side rendering, the server platform package is not
  installed, and no hydration provider appears anywhere in the source.
- On the .NET side the same reasoning applies in the other direction: this
  repository builds with warnings as errors, so promoting a future advisory to an
  error would stop delivery on a transitive package that may not even be
  reachable.

**Operational consequence.** Advisory review is an explicit, human activity
against these manifests, not a gate that fails a build. The one runtime advisory
above must be re-checked whenever the framework major version moves, because the
unreachability argument depends on rendering configuration that a future change
could alter.

**Annotated in code at.** `NuGet.Config`.

### Intentional omissions: the users-online purge job, the localisation mechanism, and rich-text editing

Each of these is a **functional reduction**, recorded here rather than left to be
discovered.

- **The users-online purge job is not ported.** The legacy job was a scheduler
  client - the single in-scope consumer of the DotNetNuke scheduling subsystem,
  which is out of scope. No scheduler client is translated and **no background
  service is introduced in its place**, so nothing prunes the users-online table.
  The mechanism a replacement would use is a hosted background service; that is a
  deliberate future decision rather than a gap left by accident.
- **The localisation mechanism is not ported.** The legacy admin screens resolved
  every label through Web Forms local resource files - 40 of them across the
  in-scope screens, keyed by control identifier and property. That mechanism is
  specific to Web Forms and has no counterpart here, and no translation runtime is
  added to the Angular workspace. The resource files are still read as the
  **authoritative source of English wording**, so labels and messages stay
  recognisable to existing users, but the application is single-language and
  strings are authored directly in templates.
- **Rich-text editing is not available.** The legacy HTML editor provider is out
  of scope, so multi-line text fields are plain text areas within the shared form
  field component. Content that was authored as markup through the legacy editor
  remains stored as it was; it simply cannot be *edited* with formatting
  affordances here.

### Portal persistence contract: two reads omitted, two writes collapsed, and one call decomposed

**Legacy behaviour.** The portal data surface was a block of **fifteen members**
at `Library/Components/Providers/Data/DataProvider.vb` lines 92 to 107 - fifteen
among the **269** `MustOverride` members declared on a single 397-line abstract
class, reached through a reflection-resolved static singleton at lines 29 to 50.
Every caller therefore depended on the whole installation's data surface in order
to read one portal.

**Target behaviour.** The portal aggregate owns a narrow contract of its own, and
four of the fifteen legacy members do not survive as written.

- **`GetExpiredPortals` (L96) is omitted.** It served host-level, super-user
  administration, which is out of scope. Nothing enumerates portals by expiry
  date, and no replacement mechanism is introduced in its place.
- **`GetPortalSpaceUsed` (L103) is omitted.** It aggregated file-storage totals
  for a portal. The legacy controller had *already* marked its counterpart
  obsolete (`PortalController.vb` line 1596), the file-system subsystem is out of
  scope, and there is **no `File` entity** among the domain entities for such a
  total to be computed from. A portal's storage consumption is therefore not
  reportable here.
- **`UpdatePortalInfo` (L104, 27 positional arguments) and `UpdatePortalSetup`
  (L105, 9 positional arguments) collapse into one entity-oriented update.** Both
  legacy procedures wrote the *same* `Portals` row from opposite ends - one the
  descriptive and configuration columns, the other the administrator and the
  well-known page assignments. Splitting one row across two positional argument
  lists made every caller responsible for supplying every column in the right
  order, and made a partial update indistinguishable from an intentional
  overwrite with defaults.
- **`AddPortalInfo` (L93, 14 positional arguments) is decomposed rather than
  ported.** Its parameter list included a given name, surname, username, password
  and address: it did not merely insert a portal row, it **also created the
  portal's administrator account**. Two aggregates were written by one call. The
  portal insert now maps to the nine-argument, portal-row-only `CreatePortal`
  (L94), and administrator creation moves to the application service, which
  stages both aggregates and commits them through a single unit of work.

**Why the differences are deliberate.** Reproducing the 269-member surface would
have carried the coupling that made the legacy data layer impossible to test in
isolation. Collapsing the two update procedures is what lets an entity carry its
own modified state instead of every caller restating all 27 columns positionally.
Separating administrator creation from portal insertion is what allows the
five-table creation sequence - portals, aliases, roles, pages and modules, issued
by `PortalController.vb` line 980 as five independent statement sequences that
could leave a half-created portal unrecoverable - to commit indivisibly.

**Operational consequence.** No portal-storage figure and no expiring-portal list
is available from this contract. Portal creation and update are unchanged in
outcome, and are now atomic where they previously were not. The staged-write
contract also means an insert yields **no identifier**: the generated key becomes
readable on the entity only once the unit of work has committed, because the
store does not assign it before then.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IPortalRepository.cs`.

### Visual system: three token scales are net additions, and the legacy font stacks are consolidated

**Legacy behaviour.** The legacy portal stylesheets are the only design source
that exists for this migration - there is no design file. They use ad-hoc pixel
values with **no spacing system**, are effectively square-cornered with no radius
vocabulary, and use no elevation. They also declare **three overlapping font
stacks** for what is visually the same intent.

**Target behaviour.** Colour and typography tokens are taken from the *measured*
legacy values, so the application retains visual continuity with the portal it
replaces. Three token scales are **net additions** with nothing to translate from:
a spacing scale on a four-pixel step, a three-step radius scale, and three
elevation levels. The three legacy font stacks are **consolidated into one** base
stack, with a separate monospace stack retained for code and query display.

**Why the differences are deliberate.** A design system with no spacing, radius
or elevation vocabulary forces every component to invent its own values, which is
how visual drift begins; defining the scales once is what makes the "no hardcoded
values" rule enforceable at all. The font-stack consolidation removes an
inconsistency in the legacy authoring rather than a deliberate distinction - the
visual difference between the three is imperceptible.

**Operational consequence.** The application will look slightly softer than the
legacy portal wherever a radius or elevation token is applied. No behaviour
changes, and no legacy colour that carried meaning was altered: the load-bearing
values are matched exactly, and the only legacy colour deliberately left
untokenised styles decoration belonging to an out-of-scope feature.

**Annotated in code at.** `frontend/src/styles/_tokens.scss`.

### Module domain: `TempModuleID` is not modelled, and the desktop-module feature setters are removed

Both entries below are appended in the exact wording the annotating source files
prescribe, so that the code and this document cannot drift apart.

- **`ModuleDefinitionInfo.TempModuleID` is not modelled.** It is a
  manifest-parse-time correlation number with no column in
  `dbo.ModuleDefinitions`, never passed to any data-provider call, and referenced
  only by the excluded `ResourceInstaller` tree; the target expresses the same
  definition-to-control relationship with an object reference.
- **`DesktopModule.IsPortable`, `IsSearchable` and `IsUpgradeable` are read-only
  projections over the persisted `SupportedFeatures` bit field.** The legacy
  read-modify-write setters on `DesktopModuleInfo` were removed; their only caller
  in the entire legacy tree is `EventMessageProcessor.UpdateSupportedFeatures`,
  which belongs to the excluded module-loader infrastructure.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Entities/ModuleDefinition.cs`,
`backend/src/DnnMigration.Domain/Entities/DesktopModule.cs`.

### `TabModule.DisplaySyndicate`: the legacy object default and the store default disagree, and both are kept

**Legacy behaviour.** The two answers to "what does a new placement syndicate?"
were never the same, and the legacy system ran with both. In code,
`Library/Components/Modules/ModuleInfo.vb` sets `_DisplaySyndicate = False` in its
constructor (`L124`) and, independently, in its `Initialize(PortalId)` routine
(`L746`). In the database, the column was added as `DisplaySyndicate bit NOT NULL
CONSTRAINT DF_{objectQualifier}TabModules_DisplaySyndicate DEFAULT (1)` by
`03.00.08.SqlDataProvider:L158`; `03.01.01.SqlDataProvider` then dropped that
constraint by catalogue lookup (`L1206-L1213`), re-declared the column `NOT NULL`
(`L1217`) and re-added `DEFAULT (1)` (`L1223`), so the store default of `1` — that
is, *true* — survives into the terminal schema, asserted twice. An object the
legacy application constructed therefore did **not** syndicate, while a row any
other writer inserted without naming the column **did**.

**Target behaviour.** Each side keeps its own answer. The entity initialises
`DisplaySyndicate` to `false`, reproducing the constructor. The entity
configuration declares the column `bit NOT NULL` and deliberately does **not**
configure a default value, leaving the existing `DF_TabModules_DisplaySyndicate`
constraint in the database untouched. Every write that goes through this model
sends an explicit value, so the store default is reached only by a writer outside
the model — a stored procedure, an upgrade script, a hand-written statement — which
is exactly the population that saw `1` before.

**Why the difference is deliberate.** It is not one difference but the *absence* of
one: collapsing the mismatch would create a behavioural change on whichever side
lost. Initialising the entity to `true` would make placements created through the
API syndicate where every legacy-created placement did not. Configuring the column
default to `0` would alter the schema, which the database directive forbids, and
would change what an out-of-model insert does. The `false` value is also not the
`Null.NullBoolean` sentinel leaking through: `DisplayTitle` and `DisplayPrint` sit
either side of it in both legacy routines and are `True` in both, so all three
assignments are business defaults rather than null markers. `DisplayTitle` and
`DisplayPrint` need no note of their own, because for them the constructor and the
store agree on *true*.

**Operational consequence.** A placement created through `POST /api/v1/.../modules`
without an explicit syndication choice does not offer the syndication affordance,
matching the legacy administration screens. Existing rows are read back exactly as
stored and are unaffected. Anyone reconciling the two defaults in future must
change both sides in the same commit and record the decision here; changing one
alone silently moves behaviour for one population of rows.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Entities/TabModule.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/TabModuleConfiguration.cs`.


### `Portal.HostFee` is modelled as an exact decimal: a deliberate precision correction

**Legacy behaviour.** `PortalInfo.HostFee` was a `Single` - binary floating point
- even though the column it round-tripped had become exact. The column began life
as `nvarchar(10) NULL`
(`01.00.00.SqlDataProvider:L89`), was rebuilt as `money NOT NULL` through a
`CONVERT(money, HostFee)` during the temporary-table swap
(`01.00.05.SqlDataProvider:L1377`, the conversion at `L1414`), and was re-asserted
`money NOT NULL` with a zero default at `03.01.01.SqlDataProvider:L1118-L1129`.

**Target behaviour.** The property is a `decimal` and the entity configuration
maps it with the `money` column type.

**Why the difference is deliberate.** Binary floating point cannot represent a
currency amount exactly. Keeping `Single` would introduce rounding differences the
legacy *storage* did not have, so this is a **precision correction rather than a
like-for-like port** - one of the few places where reproducing the legacy type
would have been the less faithful choice, because the column is the authority and
the column is exact.

**Operational consequence.** A fee that previously round-tripped with a small
floating-point error now round-trips exactly. A consumer comparing a stored fee
against a previously observed value may see the corrected figure rather than the
rounded one.

**Annotated in code at.** `backend/src/DnnMigration.Domain/Entities/Portal.cs`.

### Absence at the boundary, deferred key visibility, and two preserved caching quirks

Three smaller decisions whose consequences are visible to callers.

- **Absence is a nullable type, never a reserved number - except at the wire
  boundary.** The identifier value objects carry **no absence marker of any
  kind**: no reserved instance, no "is a value present" predicate, no comparison
  against a reserved number. Absence is expressed only by the nullable form, which
  is a distinct type the compiler forces a caller to unwrap. This matters because
  the legacy absence marker for an integer is `-1` and `Portals.PortalID` is an
  identity seeded at `-1`, so the marker and a legitimate key are the *same value*
  - refusing `-1` would make the host-level path unrepresentable, and treating it
  as absent would silently mis-resolve a tenant. **Sentinel semantics survive only
  at the DTO and API boundary**, where a legacy wire contract is externally
  observable and a consumer may still be reading `-1` or an empty string as
  "absent". Serialisation therefore does not quietly convert one representation
  into the other on those contracts.
- **A generated key becomes visible after the commit, not from the add.** The
  legacy provider returned a generated key directly from each add member. The
  target stages the change and the key is observed on the entity **after the unit
  of work commits**. A caller that needs the key must commit first; the commit
  returns an affected-row count rather than an identifier.
- **Two legacy caching quirks are preserved rather than corrected**, because the
  Minimal Change Clause forbids opportunistic optimisation of ported logic. One
  legacy site uses the performance multiplier as the **whole** expiry rather than
  as a multiplier (`Library/Components/Users/UserController.vb:L665`), and two
  sites multiply a hardcoded literal instead of a named base lifetime
  (`Library/Components/Modules/ModuleController.vb:L1264` and `L1355`). Both are
  reproduced. The consequence for a future author is that **not every cache site
  derives its base lifetime from a named constant**, so the multiplier cannot be
  reasoned about uniformly. The multiplier is now bounded so that a mistyped
  configuration value cannot produce indefinitely stale data or an arithmetic
  failure on the first cache write.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/ValueObjects/PortalId.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IUnitOfWork.cs`,
`backend/src/DnnMigration.Application/Options/CachingOptions.cs`.

### Membership settings: three legacy enumerations are carried as integers, and two CAPTCHA keys are omitted

**Legacy behaviour.** The membership and user administration screens were driven
by four small enumerations declared on the legacy user module base -
`DisplayMode`, `UserVisibilityMode` and `UsersControl` - plus two settings keys,
`Security_CaptchaLogin` and `Security_CaptchaRegister`, which switched the
DotNetNuke CAPTCHA server control on.

**Target behaviour.**

- **`DisplayMode`, the profile default visibility mode and `UsersControl` are
  carried as plain integer discriminators**, with their legal values and meanings
  documented on the contract. They are *not* recreated as target enumerations,
  because the domain's enumeration set is closed at the nine types the
  architecture specifies and declaring competing copies in the application layer
  would fracture the single source of truth the domain owns. The profile
  visibility mode in particular is a **distinct concept** from the module
  visibility enumeration and must not be merged into it.
- **The two CAPTCHA settings keys are deliberately absent from the contract.**
  The control they switch on lives in an excluded tree and the login CAPTCHA is
  dropped, so the target honours neither setting anywhere. Surfacing them would
  advertise a control to the administration screen that cannot be applied. The
  compensating control for the removed CAPTCHA is the credential rate limiting
  recorded above.

**A legacy asymmetry preserved rather than tidied.** The profile default
visibility *setting* defaults to administrators-only while the stored
profile-value visibility *column* defaults to everyone. That asymmetry is legacy
behaviour and is reproduced, not reconciled.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Dtos/User/MembershipSettingsDto.cs`.

### A removal that changed nothing no longer reports success

**Legacy behaviour.** The legacy role-assignment removal reported **success when
it had done nothing**. `RoleController.vb:L330-L347` leaves its success flag true
and silently returns when the portal or the assignment cannot be found, so a
caller could not distinguish a real removal from a no-op.

**Target behaviour.** The operation reports a **distinct failure reason** when the
target does not exist, so a caller can tell the two apart.

**Why the difference is deliberate.** A write that silently succeeds without
writing is the shape that hides bugs in the caller: retry logic never fires,
audit trails record deletions that did not happen, and a user interface reports a
change that was never made. Behaviour preservation is the default in this
migration, and this is one of the narrow cases where preserving it would carry a
defect forward rather than a rule.

**Operational consequence.** A caller that treated any response as success now
sees a failure for a removal that had no target. That is the intended signal, but
it is a response-shape change for that path.

**A related contract change on the same surface.** Legacy members took the
ambient per-request portal composite as a parameter - nine parameters typed as the
legacy settings or portal object across the role surface. Every replacement member
takes its **portal identifier explicitly**, and the remaining per-request facts
come from the immutable scoped tenant context. **No member infers a tenant from
ambient state**, which is what makes a request's tenant auditable from its
arguments alone.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Abstractions/IRoleService.cs`.

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

## Application layer

### `backend/src/DnnMigration.Application/Dtos/Role/RoleListItemDto.cs` — one row of the security-roles grid

**Legacy source:** `Website/admin/Security/roles.ascx` (note the lower-case file name; its
code-behind `Roles.ascx.vb` is capitalised) declares `<asp:datagrid id="grdRoles">` at lines
22-25 and renders ten columns plus a row key. `Library/Components/Security/Roles/RoleInfo.vb`
supplies the legacy CLR types, and the terminal feeding procedure is
`{objectQualifier}GetRolesByGroup`, last defined by
`Website/Providers/DataProviders/SqlDataProvider/04.05.05.SqlDataProvider` (line 15).

**Target:** `public sealed class RoleListItemDto` with exactly eleven properties, returned
wrapped in `PagedResponse<T>` from `GET /api/v1/roles`.

**Eleven properties, not fifteen.** The DTO projects what the grid actually rendered rather
than the fifteen-property `RoleInfo` surface. Four real columns are deliberately absent:

- **`PortalID`** — every row in a response is already portal-scoped by the request's portal
  context, and the grid never rendered it.
- **`RoleGroupID`** — a *filter*, not a column. `roles.ascx` line 9 declares the
  `cboRoleGroups` drop-down and `Roles.ascx.vb` line 75 filters with
  `GetRolesByGroup(PortalId, RoleGroupId)`. The filter belongs to the query contract, not
  to the row.
- **`RSVPCode` and `IconFile`** — real columns, added by
  `03.02.03.SqlDataProvider` (lines 44-45) as `nvarchar(50) NULL` and `nvarchar(100) NULL`,
  but not rendered by this grid. They belong to the role *detail* contract.

Two further members that might be expected are absent by design. `RSVPLink` exists on no
DTO at all: `editroles.ascx` line 161 declares a `txtRSVPLink` textbox, but
`EditRoles.ascx.vb` lines 165-167 *compute* it from the request's domain name and the save
path at line 247 persists only `RSVPCode`. There is no `RSVPLink` column, and computing one
would require `System.Web`-coupled HTTP state that the Application layer must not touch, so
the client composes the link from the code and its own origin. `RoleStatus` is also absent:
that classification describes a *user's assignment*, derived from `UserRoles.EffectiveDate`
and `ExpiryDate`, so a role *definition* has no status.

**`ServiceFee` and `TrialFee`: legacy `Single` becomes `decimal?`.** This is the clearest
demonstration in the codebase of why only the *terminal* schema state may be trusted.
`RoleInfo.vb` declares both properties `As Single` (lines 164 and 233), and the baseline DDL
at `01.00.00.SqlDataProvider` line 119 declares `[ServiceFee] [decimal](5, 2) NULL` — a
declaration that would cap every fee at 999.99. Both are superseded. The table is recreated
with `ServiceFee money NULL` by `01.00.04.SqlDataProvider` (line 1326) and again by
`01.00.05.SqlDataProvider` (line 2752), and the terminal statement is
`ALTER TABLE ...Roles ALTER COLUMN [ServiceFee] [money] NULL` at
`03.01.01.SqlDataProvider` line 1173, whose line 1177 adds a `DEFAULT (0)` constraint. That
is the *only* `ALTER COLUMN` against the roles table anywhere in the eighty-eight-script
chain, so nothing supersedes it. `TrialFee` is `money` from birth
(`01.00.08.SqlDataProvider` line 6830) and is never altered. SQL `money` is a fixed-point
type, so the faithful CLR mapping is `decimal`; binary floating-point is not used for a
monetary amount. The legacy UI agrees independently — `editroles.ascx` validates both
fields with a comparison validator of currency type.

**`BillingPeriod` and `TrialPeriod`: resolved to `int?` on four-against-one evidence.** The
legacy provider signature suggested a string, but four sources outweigh it: the schema
declares `BillingPeriod int NULL` (`01.00.08.SqlDataProvider` line 6829) and
`[TrialPeriod] [int] NULL` (`01.00.00.SqlDataProvider` line 121); `RoleInfo.vb` declares
both `As Integer` (lines 218 and 203); the legacy UI validates both with a comparison
validator of integer type; and decisively the stored procedure itself emits a SQL null,
projecting
`case when convert(int,Roles.ServiceFee) <> 0 then Roles.BillingPeriod else null end`
(`01.00.08.SqlDataProvider` lines 7024 and 7054, and again at `02.00.00.SqlDataProvider`
line 2220). A free role therefore had its billing period projected as null *by the legacy
data layer itself*, whatever was stored, so `int?` reproduces the literal legacy wire value
rather than modernising it.

**`BillingFrequency` and `TrialFrequency`: legacy `String` becomes the shared Domain enum.**
Both were declared `As String` (`RoleInfo.vb` lines 149 and 188) over a `char(1)` column
(`01.00.05.SqlDataProvider` lines 2753 and 2755). Both now use the *same*
`DnnMigration.Domain.Enums.BillingFrequency`, and **no second enum and no local copy was
created.** One shared type is correct because the legacy queries resolve both columns
against one lookup, joined twice: the early era used
`join CodeFrequency C1 on Roles.BillingFrequency = C1.Code` alongside
`left outer join CodeFrequency C2 on Roles.TrialFrequency = C2.Code`
(`01.00.04.SqlDataProvider` lines 1524-1525), and the terminal era joins the `Lists` table
twice with `ListName = 'Frequency'`. The single-character codes are load-bearing data and
are never renamed. The measured switch at
`Library/Components/Security/Roles/RoleController.vb` lines 540-547 handles **six** codes,
not the four most often cited — `N`, `O`, `D`, `W`, `M`, `Y` — and the Domain enum already
declares all six, so no gap exists.

**The wire value changes from a display string to a stable code.** The terminal procedure
did not project the stored code at all: it projected the lookup's `Text` column
(`L1.Text` / `L2.Text`), blanked to an empty string for a free role or an unused trial. That
made the grid's contents presentation wording resolved server-side. The DTO carries the
*code* instead, because a stable machine value is what an API contract should expose and
because the client owns display formatting. Consumers that need the old wording must
localise the code themselves.

**The boolean columns change from text to real booleans, and a latent defect is annotated
rather than fixed.** `IsPublic` and `AutoAssignment` are `bit NOT NULL` with a default of 0
(`01.00.08.SqlDataProvider` lines 6831-6832, re-asserted under qualifier templating at
`03.01.01.SqlDataProvider` lines 1174-1175 and 1179-1181), but the procedure projected them
as the strings `'True'` and `'False'`, which the grid then compared against a *lower-case*
literal to choose a tick or a cross (`roles.ascx` lines 68-69 and 74-75). That comparison
was case-sensitive against a value the procedure controlled, which is a latent defect rather
than a feature. Per the minimal-change discipline the defect is annotated in place and *not*
fixed in the legacy code; adopting the `bool` primitive removes the comparison entirely.

**Sentinel mapping to null is behaviourally faithful, not a modernisation.**
`Library/Components/Shared/Null.vb` makes the absent integer -1 (lines 41-45), the absent
single `Single.MinValue`, and — importantly — the absent string the *empty string* rather
than null (lines 70-74), and `Null.SetNull` applies these on every read. Mapping them to
null preserves what the user saw, because the grid's own display helpers in
`Roles.ascx.vb` already rendered them blank: `FormatPeriod` returns an empty string when
the period equals the integer sentinel, and `FormatPrice` returns an empty string when the
price equals the single sentinel. `RoleController.vb` line 537 reinforces this by treating
the sentinel period as "no expiry". One distinction is preserved explicitly: the frequency
code `N` is a *real stored code* meaning "no expiry", and it is never conflated with a null
frequency meaning "no code stored". For `Description`, the legacy absent value is the empty
string, so a mapper — not this DTO — owns any empty-string-to-null decision.

**`RoleId` is a plain non-nullable integer, and `0` is a legitimate value.**
`01.00.00.SqlDataProvider` lines 114-115 declare `[RoleID] [int] IDENTITY (0, 1) NOT NULL`,
so the first real role has identifier 0. Separately, -1 is simultaneously the integer
absence sentinel and, elsewhere in this schema, a live identity seed, and the legacy edit
screen used -1 as its own add-marker (`EditRoles.ascx.vb` lines 131 and 251). Consequently
no absence test may be written against this property — not `<= 0`, not `== 0`, not
`== default`, and not `== -1`. The DTO contains no such test.

**Paging is a net addition, not a translation.** The legacy grid was unpaged and unsorted:
the `grdRoles` declaration carries no paging or sorting attribute of any kind, and the query
behind it returned an untyped, pre-generic collection (`RoleController.vb` line 208).
Serving this row type inside `PagedResponse<T>` is therefore a deliberate enhancement. The
envelope lives in the shared response type and the sort and filter arguments live in the
request contract; this row type declares neither.

## Infrastructure layer

### `backend/src/DnnMigration.Infrastructure/Persistence/Configurations/` — Entity Framework Core asserts seven foreign-key indexes the database does not have

**What it is.** Entity Framework Core 8 runs a built-in model-building convention,
`Microsoft.EntityFrameworkCore.Metadata.Conventions.ForeignKeyIndexConvention`, that creates
a supporting index for every foreign key not already covered by an index whose *leading*
column is the foreign-key column. It is unconditional, and nothing in this repository asks
for it. Building the model from the fifteen configuration classes yields thirty-six indexes
across seventeen entity types, and seven of those thirty-six have no counterpart anywhere in
the eighty-eight-script schema chain:

| Model index | Table | Why the database does not have it |
|---|---|---|
| `IX_Modules_PortalID` | `dbo.Modules` | The only surviving index is `IX_Modules` over `ModuleDefID` (`01.00.10.SqlDataProvider` lines 875-876, recreated nonclustered at `03.00.09.SqlDataProvider` line 293). `IX_Modules_1` indexed `TabID` (`01.00.10.SqlDataProvider` lines 883-884) and was dropped immediately before that column was removed (`03.00.01.SqlDataProvider` lines 201 and 204-205). Nothing ever indexed `PortalID`. |
| `IX_PortalAlias_PortalID` | `dbo.PortalAlias` | The only index is the unique `IX_PortalAlias` over `HTTPAlias` (`03.00.07.SqlDataProvider` lines 14-18). |
| `IX_PortalDesktopModules_DesktopModuleID` | `dbo.PortalDesktopModules` | The only index is the composite unique constraint over `PortalID, DesktopModuleID` (`02.02.02.SqlDataProvider` lines 3044-3049), which leads with `PortalID`. |
| `IX_Permission_ModuleDefID` | `dbo.Permission` | The only index is the unique `IX_Permission` over `PermissionCode, ModuleDefID, PermissionKey` (`04.05.02.SqlDataProvider` lines 334-340), which leads with `PermissionCode`. |
| `IX_ProfilePropertyDefinition_ModuleDefID` | `dbo.ProfilePropertyDefinition` | The two indexes are the unique index over `PortalID, ModuleDefID, PropertyName` and the index over `PropertyName` (`04.00.04.SqlDataProvider` lines 1127-1128); neither leads with `ModuleDefID`. |
| `IX_UserProfile_PropertyDefinitionID` | `dbo.UserProfile` | The only index is `IX_UserProfile` over `UserID` (`04.03.02.SqlDataProvider` lines 135-139). |
| `IX_ModuleDefinition_DesktopModuleId` | `dbo.ModuleDefinitions` | No configuration class exists for this entity yet, so its entire mapping — including its indexes — is still convention-derived. |

**Why it has no runtime consequence.** Entity Framework Core never consults index metadata
when translating a query or materialising a result; index metadata is consumed only by
migration generation and by scaffolding. Nothing in the model can therefore make the provider
issue a statement against an index that does not exist. Combined with the project-wide rule
that the baseline migration is generated and then emptied, no `CREATE INDEX` derived from this
convention can reach any schema, so the immutable-schema guarantee is not weakened.

**Why it is not suppressed in these files.** There is no per-configuration way to do it.
`EntityTypeBuilder<T>` exposes twenty-eight public instance methods in 8.0.29; its only
index-related member is `HasIndex`, which adds, and its only removal-shaped member is
`Ignore`, which excludes properties and navigations rather than indexes. The one remaining
path is the low-level mutable-metadata API, and whether that finds the index depends on
whether the convention has already contributed it at the moment `Configure` runs — an
ordering the provider does not document and which must not be relied upon.

**Where it belongs, and the exact change.** The single correct suppression point is the
context, which does not exist yet. `DnnDbContext` must override the virtual
`DbContext.ConfigureConventions(ModelConfigurationBuilder)` and remove the convention once
for the whole model:

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
{
    base.ConfigureConventions(configurationBuilder);

    // MIGRATION: the legacy schema is immutable, and the foreign-key support indexes it
    // really has are declared explicitly by the entity configurations under
    // Persistence/Configurations. EF Core's ForeignKeyIndexConvention would assert seven
    // further indexes that the DotNetNuke 4.9 schema does not contain.
    configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
}
```

`ModelConfigurationBuilder.Conventions` is a `ConventionSetBuilder` exposing both
`Remove(Type)` and a generic `Remove<T>()`; the convention type is public and both overloads
were verified present in 8.0.29 by reflection over the installed assembly. Removing the
convention cannot suppress a real index, because every index the schema actually has is
declared explicitly with `HasIndex` and named with `HasDatabaseName`.

**The complementary positive finding.** Because the convention adds an index only when no
existing index already leads with the foreign-key column, every explicitly declared
foreign-key support index was reused rather than duplicated. The eight permission-table
indexes added by `04.06.00.SqlDataProvider` (lines 1181-1229), the two `dbo.UserRoles` indexes
and the two `dbo.UserPortals` indexes each appear exactly once in the model, under their
legacy names. The same model build reports **zero shadow properties**, which is the positive
proof that every relationship declared across the fifteen configuration classes binds to a
real, explicitly mapped legacy column rather than to a synthesised one.


## API layer

### `backend/src/DnnMigration.Api/ErrorHandling/GlobalExceptionHandler.cs` — global exception handling is net-new, with no in-scope predecessor

**What it is.** The single, framework-native `IExceptionHandler` for the API host.
Every exception that escapes the request pipeline is translated into one RFC 7807
`application/problem+json` response carrying `type`, `title`, `status` and
`detail`, plus the framework-native `traceId` extension.

**Why this is a divergence.** It preserves no legacy behaviour, because there was
no centralised exception-to-response translator to preserve. The five in-scope
trees under `Library/Components/{Portal,Modules,Users,Security,Tabs}` report
failure per call site, inside a `Catch` block that logs and swallows; nothing
anywhere converts an exception into a response contract. The two centralised
analogues that do exist in the checkout are both out of scope and neither was
ported:

- `Library/HttpModules/Exception/ExceptionModule.vb` — an `IHttpModule` hooked to
  `HttpApplication.Error`, part of the excluded 24-file `Library/HttpModules/`
  tree and registered as the fifth of the eight HTTP modules in
  `Website/release.config:L67-L74`. `IHttpModule` has no ASP.NET Core
  counterpart, so there was nothing to port to even had it been in scope.
- `Website/ErrorPage.aspx` and `Website/ErrorPage.aspx.vb` — the client-facing
  half, a server-rendered `System.Web.UI.Page`, excluded with the rest of the Web
  Forms surface. Its wording came from `App_GlobalResources` keys, and
  localisation is not ported, so the `title` and `detail` text is authored
  directly in English.

**Three legacy behaviours deliberately inverted rather than reproduced.** Each
was measured in `ExceptionModule.vb` before being rejected:

| Legacy behaviour | Target behaviour |
| --- | --- |
| Filtered by file extension and skipped named installer pages, so most requests were never handled at all. | Every unhandled exception on every route is handled. There is no path- or extension-based special-casing, which is also what keeps the anonymous `/health` endpoint that `docker-compose.yml` waits on unaffected. |
| Discarded failures in two nested empty `catch` blocks. | Nothing is swallowed. Where a response cannot be produced the handler returns `false` so the framework's own default handling takes over, and the exception has always been logged before that happens. |
| Wrote no response body; the caller was redirected to a rendered page. | A well-formed problem-details payload is always written when the handler reports that it handled the exception. |

**One legacy habit deliberately kept.** `ErrorPage.aspx.vb:L32` and `:L51` ran
every echoed query-string value through `PortalSecurity.InputFilter` with
`NoScripting Or NoMarkup` before rendering it — legacy DotNetNuke already refused
to reflect unsanitised input into an error surface. The target goes further and
reflects no request content into a payload at all: `title` and `detail` are fixed
authored text, and the `instance` member is left unset rather than derived from
the request URL.

**One legacy disclosure deliberately dropped.** `ErrorPage.aspx:L17` rendered
`DotNetNuke Error: - Version <%=glbAppVersion %>`, publishing the product version
to any caller. No payload produced by this handler contains a product or
framework version, an assembly name, a host name, a file path, a line number, a
stack trace or an inner-exception chain.

**The response surface does not vary by environment — neither its shape nor its
text.** This is stricter than the minimum the migration plan asks for, which
permits the `detail` *text* to differ between deployments. The general 500 case
publishes fixed authored text everywhere and never the exception's own message,
for a reason that is specific to this codebase rather than general caution: an
Entity Framework Core or `Microsoft.Data.SqlClient` message routinely carries the
connection string, the server and database names and the values bound to a
statement, and an argument or key-lookup message routinely carries the value that
failed. None of that is recognisable from the base type, so the only safe rule is
to publish none of it. The legacy application set the precedent for holding one
error surface everywhere: `Website/release.config:L144` and
`Website/development.config:L142` declare the identical
`<customErrors mode="RemoteOnly"/>`, varying the richness of a message by caller
locality and never the error surface itself. Environment-conditional enrichment
was therefore considered and rejected, and no `IHostEnvironment` is injected.

**The one exception message that is published.** `DomainException.Message` is
passed through to `detail` exactly as authored, including an empty one, because
that text is written by our own domain code for a reader. `DomainException` maps
to 400, `UnauthorizedAccessException` to 403, and everything else to 500. No new
exception type was introduced: the four types named in
`docs/technical-specifications.md:L1402-L1410` — `ValidationException`,
`NotFoundException`, `UnauthorizedException` and `ForbiddenException` — do not
exist in this solution and were not created, because an expected, enumerated
failure travels as a failed `Result` and is translated by the controller that
received it.

**A cancelled request is not an error.** An `OperationCanceledException` raised
while the request's own abort signal is set means the caller disconnected. It is
recorded at information level and no response is written, so a client hanging up
cannot fill an error dashboard. An `OperationCanceledException` raised *without*
that signal — an internal timeout — remains a genuine server-side failure and
resolves to 500.

**The correlation identifier travels in the response header only.** It is not
added to the payload body. `Api/Filters/ValidationProblemDetailsFactory.cs`
already established that single source of truth for the same identifier, and a
second copy in the body would let the two disagree. The value is resolved from
`HttpContext.Items`, where `Api/Middleware/CorrelationIdMiddleware.cs` publishes
the validated identifier, and falls back to `HttpContext.TraceIdentifier`. The
raw inbound request header is deliberately never read: it is caller-controlled
and unvalidated, and that middleware substitutes a generated identifier when it
cannot be trusted, so reading the raw value here would reintroduce exactly the
response-splitting and log-forging the substitution prevents.

**Structured logging replaces nothing and is the only place detail appears.** The
19 in-scope `AddLog(` audit sites are *not* absorbed here; per the migration plan
they become Serilog events at the application-service layer. This handler records
the failure and nothing else: no audit semantics, no event-type mapping, no
persistence call, and no external error-tracking SDK — Sentry and Application
Insights are a later milestone in `docs/project-guide.md:L293` and are not in
scope. Each entry carries the exception object itself, so the message, the
inner-exception chain and the stack trace reach the log and only the log. The
request method, path, status code and correlation identifier are recorded; the
query string, the request body, the `Authorization` header and cookies are
deliberately omitted because any of them can carry a credential.

**Superseded guidance identified and rejected.** `docs/` is a prior-run artefact
and, on this file, actively misleading: it places global error handling in
`Middleware/ExceptionHandlingMiddleware.cs`
(`docs/technical-specifications.md:L408-L409` and `:L795`, and
`docs/project-guide.md:L381`). The migration plan supersedes all three. The file
is `ErrorHandling/GlobalExceptionHandler.cs`, it is the framework-native
`IExceptionHandler` rather than a hand-rolled middleware, and `Middleware/`
carries exactly three files, none of them an exception middleware.


## Configuration and options

### Bound configuration validates itself at start-up, where the legacy values could not be misconfigured at all

**Legacy behaviour.** The four values now grouped as configuration were not
configuration at all. The cache performance multiplier was a host-settings row
read through `Library/Components/Shared/Globals.vb:L227-L231`; the administration
template file name and the default home-directory pattern were literals compiled
into `Library/Components/Portal/PortalController.vb` (`:L1082` and `:L991-L992`);
the two special role names were the constants `glbRoleAllUsersName` and
`glbRoleUnauthUserName` at `Library/Components/Shared/Globals.vb:L100` and
`:L102`; and the password policy was a set of attributes on the
`AspNetSqlMembershipProvider` element at `Website/release.config:L236-L247`. A
compiled constant cannot be misconfigured, and the three settings that were
external were read with no validation whatsoever - the multiplier was cast
straight to an enumeration, unchecked.

**Target behaviour.** Each of `JwtOptions`, `CachingOptions`, `PortalOptions` and
`PasswordPolicyOptions` declares an `IReadOnlyList<string> Validate()` method
reporting every way in which its bound values are unusable, and the API layer
calls it while binding so that a misconfigured deployment fails before the host
serves traffic rather than at first use.

**Why this is a divergence.** Making a compiled constant configurable creates a
failure mode the legacy application did not have, so the guard is the price of the
flexibility rather than an addition to it. Three checks are worth naming because
they reject configurations the legacy code would have accepted and then
misbehaved on:

- **`PortalOptions.AdminTemplateFileName`** must be a bare file name, and
  **`PortalOptions.HomeDirectoryFormat`** must be a plain relative path
  containing the `{0}` placeholder and no `..` segment, drive or UNC prefix,
  leading separator or trailing separator. Each composed value is web-relative and
  is combined with the application path, so a rooted or navigating value would
  place every portal's content outside the application at once. A format string
  without the placeholder would give every portal lacking a stored home directory
  the same directory, so tenants would share one.
- **`PortalOptions.UnauthenticatedRoleName` and `AllUsersRoleName`** must each be
  present, no longer than the 50 characters `Roles.RoleName` stores
  (`01.00.00.SqlDataProvider:L117`, carried unchanged through both rebuilds at
  `01.00.04:L1324` and `01.00.05:L2750`), and different from each other. The
  legacy lookup matches a role by comparing this display name as a string, so an
  over-long name could never match a stored row, and the legacy
  role-name-to-role-id switch reads both arms of the pair, so collapsing them
  would resolve one role's identifier for the other.
- **`PasswordPolicyOptions`** rejects a minimum length below one, a negative
  non-alphanumeric minimum, a policy demanding more non-alphanumeric characters
  than the password has characters, and a strength pattern that does not parse.
  The legacy strength pattern was configured in neither
  `Website/release.config` nor `Website/development.config`, so an unparseable one
  would have surfaced at the first sign-up attempt rather than at start-up.
  Nothing here enforces the legacy figures as minimums: an operator remains free
  to harden the policy, which the notes on those members ask them not to do
  *during* the migration rather than making it impossible.

`JwtOptions` additionally requires a secret of at least 32 characters. That is a
technical floor, not a policy preference - HMAC-SHA256 signs with a key of at
least 256 bits, so a shorter secret is rejected by the signing library itself, at
the moment a token is first issued. No validation message ever contains the secret
or any part of it, only its length, because a start-up failure is written to the
log. `JwtOptions` also rejects a refresh-token lifetime no longer than the
access-token lifetime, since a refresh token that expires no later than the token
it renews cannot renew anything.

**Shipped defaults are unaffected.** Every default in all four classes passes its
own validation, with one intended exception: `JwtOptions.Secret` is empty by
default, so a deployment that supplies no secret fails at start-up. That is the
documented intent, not an oversight - this repository ships no secret value.

### A negative cache performance multiplier is a start-up failure, where the legacy application silently disabled caching

**Legacy behaviour.** `Library/Components/Shared/Globals.vb:L229` reads the
`PerformanceSetting` host-settings row and converts it with
`CType(Convert.ToInt32(...), PerformanceSettings)`. A Visual Basic conversion to
an enumeration is *unchecked*, so any integer at all became the multiplier: a
stored `4` produced a multiplier of `4` and scaled every lifetime accordingly,
and a stored `-1` produced a negative product that every guarded call site
(`If timeOut > 0`, for example `Library/Components/Portal/PortalController.vb:L221`)
treated as an instruction to skip the work.

**Target behaviour.** Any non-negative multiplier is accepted, including values
outside the four the legacy `PerformanceSettings` enumeration named. A negative
multiplier is rejected at start-up.

**Why.** Accepting only the legacy four would be a *tightening* of a
configuration the legacy installation accepted, which the Minimal Change Clause
forbids; the four constants on `MemoryCacheService` are therefore names for the
values an operator will normally choose, not an allow-list. Rejecting a negative
value loses no legacy outcome, because `0` expresses "no caching" exactly and
unambiguously, while an unguarded target call site would otherwise hand a negative
duration to the cache and fail mid-request. The rule is declared once, on
`CachingOptions.Validate`, and applied by both the API's start-up validation and
`MemoryCacheService`, so the published contract and the consumer cannot drift
apart - an earlier revision had exactly that drift, the documentation describing
any integer as legitimate while the cache service rejected everything outside the
four.

### The e-mail length bound is 256, not the 100 the legacy sign-up control enforced

**Legacy behaviour.** The portal sign-up screen capped the address in the browser:
`Website/admin/Portal/signup.ascx:L106` declares `txtEmail` with
`maxlength="100"`, and its only validator is the required-field validator at
`:L106-L107`. There is no format pattern on that field.

**Target behaviour.** Every e-mail bound in the solution is 256 characters -
`EmailAddress`, `CreateUserRequestValidator`, `CreatePortalRequestValidator`,
`UpdateUserRequestValidator` and the persistence configuration alike.

**Why.** The 100 is a control affordance, and the column it was guarding no longer
has that width. `dbo.Users.Email` has to be read as a chain rather than from the
baseline script: `01.00.00.SqlDataProvider:L107` creates it as
`[Email] [nvarchar] (100) NOT NULL`, both `Tmp_Users` rebuilds carry that width
forward (`01.00.05:L25`, `01.00.06:L193`), `02.02.01:L50-51` **drops the column
outright** along with eight others when credentials and contact details moved into
the ASP.NET membership tables, and `03.00.13:L109-110` **re-adds it as
`Email nvarchar(256) NULL`**, back-filled from `dbo.aspnet_Membership.Email` at
`:L113-117`. Nothing afterwards narrows it. Two independent authorities agree with
256: the legacy property editor declared `MaxLength(256)` at
`Library/Components/Users/UserInfo.vb:L121`, and the terminal procedures declare
`@Email nvarchar(256)` - `AddUser` at `04.00.04.SqlDataProvider:L704` and
`UpdateUser` at `:L1078`. Capping the create path at 100 would also have made it
stricter than the update path that maintains the very row it creates.

**Note on the same chain's nullability.** The column is terminally *nullable*, yet
the user-facing request and response contracts declare their e-mail members
non-nullable. That is deliberate and is the sentinel-boundary rule: the legacy
null-string sentinel is the empty string
(`Library/Components/Shared/Null.vb`), so the empty string has always been the
externally observable value for a missing address, and emitting a null would be a
silent change to an observable value. The domain entity models the column
honestly as nullable; the DTOs preserve the sentinel. `PortalDetailDto.Email` is
the one exception and *is* nullable, because the absence it represents is the
absence of the joined user row rather than an empty address.

### `Users.LastName` is required, and the baseline script says otherwise

**Legacy behaviour.** `01.00.00.SqlDataProvider:L100` declares
`[LastName] [nvarchar] (50) NULL`, one line below a `NOT NULL` `FirstName`, so the
baseline pair really is asymmetric. The legacy property editor disagreed, marking
the property `Required(True)` at
`Library/Components/Users/UserInfo.vb:L178`.

**Target behaviour.** The member is required and non-nullable everywhere - on the
domain entity, on all four user contracts and in both user validators - and the
persistence configuration declares `IsRequired()`.

**Why this is parity rather than divergence.** The baseline is superseded. The
`01.00.05` rebuild re-declares the column `LastName nvarchar(50) NOT NULL` at
`01.00.05:L18`, drops the real table at `:L54` and renames the copy into place at
`:L57`; the `01.00.06` rebuild preserves `NOT NULL` at `01.00.06:L186` with the
same drop and rename at `:L227` and `:L230`. No `ALTER COLUMN` in any of the 88
scripts touches it afterwards, and the terminal `AddUser` and `UpdateUser`
procedures declare `@LastName nvarchar(50)`
(`04.00.04.SqlDataProvider:L701` and `:L1077`). The terminal schema and the legacy
attribute therefore agree, and the baseline asymmetry does not survive the chain.
It is recorded here because reading the baseline alone gives the opposite answer,
and because an earlier revision of these contracts did exactly that - typing the
member as optional and reaching it through a null-forgiving operator in the
create validator on the stated ground that wire optionality would let an omitted
field and an explicitly blank one be told apart. `NotEmpty` cannot make that
distinction: it treats a null and an empty string alike and emits one message
either way.

### The Application project carries no options package, and that absence is load-bearing

**Target behaviour.** `DnnMigration.Application.csproj` references
`FluentValidation` and `FluentValidation.DependencyInjectionExtensions` and
nothing else. `Microsoft.Extensions.Options` is deliberately *not* referenced, so
no type in the Application layer may name `IOptions<T>`.

**Why.** The layer declares option *shapes* and their invariants; binding them to
a configuration section, validating them at start-up and registering them with a
container all belong to the API layer, which owns the composition root. Every
Application-layer consumer therefore takes a plain, already-bound snapshot -
`CreateUserRequestValidator` and `ChangePasswordRequestValidator` both take a
`PasswordPolicyOptions` directly - which also makes them constructible in a test
without an options wrapper. Because the package is absent rather than merely
unused, an attempt to reintroduce `IOptions<T>` in this layer fails to compile
rather than passing review unnoticed. `Validate()` is declared with
base-class-library types alone for the same reason.

## Request validation, credentials and wire contracts

### Password recovery by security question is not carried forward, and neither is a server-generated password

**Legacy behaviour.** The ASP.NET membership store held a question-and-answer pair
per account, and the legacy change-password path could replace them: the provider's
`ChangePasswordQuestionAndAnswer` member exists at
`Library/Providers/MembershipProviders/AspNetMembershipProvider/AspNetMembershipProvider.vb:L813`,
and the account-management screen could ask the server to invent a password and mail
it to the user rather than take one from the form.

**Target behaviour.** `CreateUserRequest` no longer carries `PasswordQuestion`,
`PasswordAnswer`, `GenerateRandomPassword` or `Notify`, and its `Password` and
`ConfirmPassword` members are non-nullable. `ChangePasswordRequest` no longer
carries `PasswordAnswer`, `NewPasswordQuestion`, `NewPasswordAnswer` or the
`OperationChangeQuestionAndAnswer` operation. The rules that had been written over
those members are gone with them.

**Why.** Every one of them promised an outcome nothing in this migration can
deliver, and a wire contract that advertises an unsupported operation is worse than
one that omits it: a caller gets a 400 or a 500 for a field the schema told it to
send. Recovery by security answer is a credential-reset channel, so exposing it
without a working verification, throttling and audit path is a way in rather than a
feature. A server-generated password needs a delivery channel, and mail is outside
this migration's boundary, so the generated secret could only ever be returned in
the response body or discarded - the first is a disclosure and the second leaves the
account unusable. `IUserService` already stated that none of these operations was
supported; the contracts now agree with it.

**What is retained, and why that is not a contradiction.** `User.PasswordQuestion`
and `User.PasswordAnswer` remain on the domain entity. They map real columns in the
external `aspnet_Membership` store, Rule T4 makes that schema authoritative, and an
installation's existing rows must round-trip intact. No DTO carries either member,
so neither value can cross the wire in either direction.

### Changing your own password and resetting somebody else's are separate operations

**Legacy behaviour.** One screen served both, and the reset branch did not require
the existing credential.

**Target behaviour.** `ChangePasswordRequest.Operation` selects between `change` and
`reset`. `CurrentPassword` is unconditionally **required** for `change` and
unconditionally **refused** for `reset` - supplying one on a reset is a field-level
failure with its own message rather than an ignored value. `NewPassword` and
`ConfirmPassword` are required for **both** operations, so a reset can no longer be
submitted as an empty body.

**Why.** The two operations differ in who is allowed to perform them and in what
proves the right to perform them, and collapsing them left a reset that proved
nothing. Requiring the current credential for a self-service change restores
proof-of-possession. Refusing it on a reset is not pedantry: accepting and ignoring
it would make a caller believe the value was checked. Requiring a new credential on
both closes the third gap, which was that a reset with no new password could only
have meant "invent one and deliver it somehow" - and there is no delivery channel.
The remaining half of the separation, that a reset requires administrative authority
while a change requires only an authenticated subject, is an authorisation decision
and is recorded on the service contract for the API edge to enforce; a validator
cannot see who is calling.

### A password may not exceed 256 UTF-8 bytes, where the legacy store accepted any length

**Legacy behaviour.** The membership provider registration at
`Website/release.config:L236-L246` sets a minimum length and no maximum, so a
credential of any length the store could hold was accepted. The store imposed no
useful bound either: the original plaintext column was
`[Password] [nvarchar] (20) NOT NULL`
(`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L97-L110`)
and it was dropped outright at `02.02.01.SqlDataProvider:L50-51` when credentials
moved into the ASP.NET membership objects. No measured limit on a *submitted*
password exists anywhere in the legacy sources, so this ceiling is introduced
rather than ported.

**Target behaviour.** Every credential boundary refuses a password whose UTF-8
encoding exceeds **256 bytes**. The number, the unit and the wording are declared
**once**, on `Application/Validation/CredentialBounds.cs`, and applied at five
places: `CreateUserRequestValidator`, `ChangePasswordRequestValidator` - which
bounds the current password as well as the new one, because both are credential
inputs - `CreatePortalRequestValidator`, `LoginRequestValidator`, and
`BcryptPasswordHasher` itself, which raises on `Hash` and reports a non-match on
`Verify`. `PasswordPolicyOptions.Validate()` refuses a configured minimum length
that could not be satisfied within the same ceiling, so the policy cannot be set
to something no password could meet.

**Why the ceiling is 256 and not 72.** BCrypt's own algorithm considers only the
first 72 bytes of its input. The hasher therefore does **not** pass the credential
to BCrypt directly: it uses `BCrypt.Net-Next`'s enhanced entry points,
`EnhancedHashPassword` and `EnhancedVerify`, with a `HashType.SHA384` pre-hash, so
the whole credential is digested to a fixed-width value before BCrypt sees it and
every byte of the input contributes to the result. The algorithm's 72-byte
significance limit consequently does not reach the caller's password, and the
truncation equivalence it would otherwise create - two different credentials
sharing a 72-byte prefix verifying against one hash - cannot arise. What remains
is the ordinary reason to bound an unauthenticated input at all: without a
ceiling the amount of work a deliberately expensive one-way hash performs is
chosen by the caller. 256 bytes is generous enough that no credential a person
chooses can reach it, so it locks nobody out, and the companion control is the
request rate limiting configured in `Api/Extensions/RateLimitingExtensions.cs`.

**Why the bound is measured in bytes.** UTF-8 bytes are what the hashing
algorithm consumes, so the bound is stated in the unit that is actually
constrained. A passphrase written in a script whose characters encode to two,
three or four bytes each therefore reaches the ceiling at proportionally fewer
characters - worth stating in user-facing guidance.

**Why the declaration sits in the Application layer.** `IPasswordHasher` states
that the algorithm and its package are named only by Infrastructure, so the
choice cannot leak inward, which rules out the Domain layer. Application is the
innermost layer that has to know the bound regardless, because without it an
over-long password becomes an exception deep inside the hasher and so a 500 for
what is a field-level problem. Infrastructure references Application, so the
hasher reads the same constant rather than holding a private copy. Every private
copy the validators once held, and the earlier
`PasswordPolicyOptions.MaximumPasswordByteLength`, were deleted.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/CredentialBounds.cs`,
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`,
`backend/src/DnnMigration.Application/Validation/CreateUserRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/ChangePasswordRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/CreatePortalRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/LoginRequestValidator.cs`.

### The administrator password chosen while creating a portal is now held to the password policy

**Legacy behaviour.** The sign-up screen validated the administrator password only
for presence, and the value went on to become the new portal administrator's
membership credential by way of `Website/admin/Portal/Signup.ascx.vb:L274` and
`Library/Components/Portal/PortalController.vb:L1005`.

**Target behaviour.** `CreatePortalRequestValidator` takes a bound
`PasswordPolicyOptions` and enforces the configured minimum length, the configured
minimum count of non-alphanumeric characters, the 256-byte ceiling, and the optional
strength pattern.

**Why.** The hasher throws for each of those four conditions, and a throw from the
hasher is an unhandled invariant failure - a 500 - for a problem the caller could
have been told about as a 400 naming the field. The screen genuinely carried no such
rule, but the flow behind it always did; validating at the boundary reports the same
outcome in the form a client can act on.

**A second gap closed at the same root cause.** The non-alphanumeric rule was also
missing from `CreateUserRequestValidator` and `ChangePasswordRequestValidator`, both
of which already substituted the configured count into their message text. A
deployment configuring a count above zero therefore received a message asserting a
requirement nothing checked, followed by a 500 from the hasher. The rule was added to
both, so all four of the hasher's conditions are pre-checked at all three credential
boundaries.

**The classification is ASCII, deliberately.**
`Library/Components/Users/UserController.vb:L1078` declares
`New Regex("[^0-9a-zA-Z]")` and `:L1079` compares the match count against the
configured minimum, so an accented letter counted as non-alphanumeric in the legacy
application. The migrated helpers use `char.IsAsciiLetterOrDigit` to preserve that
exactly, rather than the broader Unicode classification a fresh implementation would
reach for.

**Note on an earlier entry.** The entry recording that the Application project holds
no options package names two consumers of a bound `PasswordPolicyOptions`. There are
now three: `CreatePortalRequestValidator` joins them, on the same terms and with the
same plain-snapshot constructor shape.

### A portal home directory must be a contained relative path

**Legacy behaviour.** The sign-up screen accepted the home directory as free text.
`Website/admin/Portal/Signup.ascx.vb:L252-L256` was the only check: it appended a
trailing separator to whatever was supplied and probed it through
`FolderController.GetMappedDirectory`, reporting a single generic message when that
came back empty. Nothing rejected a rooted path, a drive qualifier, a UNC prefix or a
`..` segment.

**Target behaviour.** Both `CreatePortalRequest.HomeDirectory` and
`UpdatePortalRequest.HomeDirectory` are validated by one shared rule set,
`Application/Validation/PortalHomeDirectoryRules`. It refuses a whitespace-only
value, any control character, any of `\ : * ? " < > |`, a leading `/` - which
covers both a rooted path and a `//server/share` form - an empty or blank segment,
and any `.` or `..` segment. It then confirms lexically that the combined path
remains under a probe root. A single trailing `/` is tolerated.

**Why.** The value names a directory the application will read and write, so a
rooted or traversing form is a path-traversal vector. Sharing one rule set across
the two contracts is the point: a create path and an update path that maintain the
same column cannot be allowed to disagree about what that column may hold, and two
copies of a security predicate are two things to keep in step. The trailing
separator is tolerated because the legacy code appended one itself, so refusing it
would reject values the legacy application produced.

**Two platform facts that shaped the implementation.**
`Path.GetInvalidPathChars()` is deliberately not used: on Linux it contains only the
null character, so a rule built on it would be weakest on the very platform the
container runs. `Path.IsPathRooted("C:\\x")` returns `false` on Linux, which is why
the volume separator is refused by name rather than left to the framework. The
containment check is lexical and needs no I/O, because the combined path is already
rooted and so is independent of the working directory.

**These rules are not the configuration helpers.** `PortalOptions` validates the
*configured* home-directory format and administrative template name at start-up.
This rule set validates a *caller-supplied* directory per request. The two are
deliberately separate types with separate rules, because conflating them would let a
change to one silently alter the other.

### The portal identifier travels in the route only, and no longer in the update body

**Legacy behaviour.** Not applicable - the Web Forms screen carried the identifier
in view state, and there was no second copy for a caller to disagree with.

**Target behaviour.** `UpdatePortalRequest` declares no `PortalId` member. The
portal being updated is named once, by the route, and reaches the service as the
separate first argument that `IPortalService.UpdatePortalAsync` already took.
`CreatePortalRequest` likewise carries no identifier, so the two contracts now agree.

**Why.** Two copies of an identity, one in the route and one in the body, is an
authorisation hazard: whichever copy the service reads, the other is either ignored
or - worse - trusted. Enforcing equality instead was considered and rejected, but
not on mechanical grounds: `Api/Filters/FluentValidationActionFilter.cs` publishes
every route value into the validation context's root data, so a comparison rule is
perfectly implementable. It was rejected because a rule protects only the requests
that reach it, and only for as long as nobody adds an entry point that binds the
body without it, whereas a contract with no second copy has nothing to disagree on
any path. Removal is the fix that cannot be forgotten.

**The new update validator.** `Application/Validation/UpdatePortalRequestValidator`
enforces twelve terminal column widths, every one measured from the upgrade-script
chain rather than from the baseline script alone. It deliberately requires
*nothing*: a census of `Website/admin/Portal/sitesettings.ascx` finds exactly two
validators in the entire screen, both `CompareValidator` with
`Operator="DataTypeCheck"`, and no required-field or pattern validator at all. Those
two are type checks that model binding to `DateTime?` and `decimal?` already
satisfies, so a rule over them could never fail. No sign or range rule is asserted
over the money and integer members either, because the legacy code parsed them with
plain `Parse` and the columns permit negatives; the genuine rule over those members
is an authorisation rule at `SiteSettings.ascx.vb:L759-L770`, which rejects a save
in which a non-super-user altered the fee, space, quota, log or expiry values, and
that belongs to the service and the API edge rather than to a validator.

### Collections are page-size bounded and sort by an allowlisted field, where the legacy grids bounded neither

**Legacy behaviour.** The page size was an operator setting with no ceiling:
`Library/Components/Users/UserModuleBase.vb:L134-L135` defaults `Records_PerPage` to
ten and `Website/admin/Users/Users.ascx.vb:L114-L119` reads it, while
`Website/admin/Users/UserSettings.ascx` - the screen that maintains those settings -
declares no records-per-page control and no validator of any kind. The account
search box at `Website/admin/Users/users.ascx:L7` carries no `maxlength`, and the
grid's pager is a bare `<pagerstyle>` with no sorting affordance anywhere in the
markup.

**Target behaviour.** `Application/Validation/PagedRequestValidator` refuses a
negative page index, a page size of zero or below, a page size above **100**, a
query longer than **256** characters, and an unrecognised sort direction. Sorting is
**closed by default**: the base validator accepts no sort field, and each collection
endpoint has its own sealed derivation naming the fields it can actually order by -
seven for portals, eleven for roles, three for a role's members and eleven for
users.

**Why the bounds are net-new.** Nothing legacy bounded either value, so these are
additions rather than translations, and both are additions the target architecture
requires. An unbounded page size is a denial-of-service lever against a JSON API in
a way it was not against a server-rendered grid whose size an operator set once. The
query bound comes from the widest column any of these endpoints filters on -
`dbo.Users.Email`, terminally `nvarchar(256)` after the drop-and-re-add at
`03.00.13.SqlDataProvider:L109-110` - so it cannot reject text the store could have
matched. The ceiling of 100 is ten times the measured legacy default of ten.

**Why the sort allowlists exclude some visible columns.** Each list is derived from
the columns the corresponding legacy grid actually bound, then reduced where a name
could not be an ordering. Portal aliases are excluded because the member projects a
collection. A role's members exclude the effective and expiry dates because the
projection does not carry them, and exclude the role identifier because the route
fixes it. Users exclude the portal identifier for the same reason, exclude the
online flag because presence is not stored, and exclude the super-user and
locked-out flags because no legacy grid offered them - adding them would smuggle a
new capability into an allowlist. Where a legacy column heading differs from the
contract's property name, the property name is authoritative, because that is what a
caller reads back and echoes: `UserName` becomes `Username`, `LastLogin` becomes
`LastLoginDate` and `Authorized` becomes `IsApproved`.

**Rejection, never substitution.** A page size above the ceiling is reported, not
clamped, and a negative index is refused rather than reinterpreted - including the
legacy integer null sentinel of minus one. An unrecognised sort field names the
accepted set in its message and never echoes the value it was sent. Note the one
asymmetry: a page size of zero means *unpaged* on a **reply**, because the server's
own factory produces that shape, but is **invalid on a request**, because it is not
something a caller may ask for.

**The fault this prevents.** `PagedResult<T>.Create` calls
`ArgumentOutOfRangeException.ThrowIfNegative` on the total count, the page index and
the page size. An unvalidated negative index therefore became an unhandled invariant
failure - a 500 - for what is a field-level 400.

### A read that can answer "absent" says so in its type

**Legacy behaviour.** Not applicable; the legacy readers returned `Nothing` and the
caller checked.

**Target behaviour.** The four `IUserService` reads that can legitimately answer
"there is no such thing" now carry a nullable payload: `GetUserAsync`,
`GetMembershipSettingsAsync`, `GetProfileAsync` and
`GetProfilePropertyDefinitionAsync` all return `Result<TDto?>`.

**Why.** Each already documented that a successful result may carry no value -
absent is not failed - but the compiler could not hold that promise while the payload
was non-nullable, so a consumer dereferencing it saw nothing wrong. Making the type
say what the prose said moves the check to compile time. The writes and the list
reads deliberately stay non-nullable: a write that succeeded produced a record, and
a list read promises an empty page or an empty sequence rather than a null. This is
also consistent rather than novel - eleven members across the sibling service
contracts already carried nullable payloads for the same reason.

### Sign-in verifies the password before it discloses anything about the account

**Legacy behaviour.** Reconstructed from three files.
`Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L160-L197` calls
`UserController.ValidateUser`, which calls
`AspNetMembershipProvider.UserLogin` at `:L1429-L1513`. That provider evaluates
lockout at `:L1454-L1463`, then approval at `:L1466`, approving the account outright
at `:L1470` and persisting it at `:L1473` when the supplied verification code equals
`portalId & "-" & userId` (`:L1468`) - and only then, at `:L1481`, guarded by
`If loginStatus <> LOGIN_USERLOCKEDOUT And loginStatus <> LOGIN_USERNOTAPPROVED`,
does it check the credential at all. The sign-in was stopped for those two states
only because the provider returned nothing at `:L1505-L1507`; the page's own
`authenticated` expression at `Login.ascx.vb:L187` is
`loginStatus <> LOGIN_FAILURE`, which is **true** for a locked-out account.

**Target behaviour.** `Application/Abstractions/IAuthService` fixes the order:
resolve the account within the tenant, **verify the password**, then evaluate lockout
and approval, then - only when a correct verification code accompanied a correct
password - approve the account and persist it, then issue the token pair. The
outcomes are named: `INVALID_CREDENTIALS`, `ACCOUNT_LOCKED_OUT`,
`VERIFICATION_REQUIRED`, `VERIFICATION_CODE_INVALID` and `ACCOUNT_NOT_APPROVED`,
with `TOKEN_STORE_UNAVAILABLE` propagated unchanged from the token service, and the
weak-default-password caveat attached as an advisory reason on a **successful**
result rather than as a failure.

**Why, and what changes for whom.** Two consequences of the legacy order are
genuine defects rather than behaviour worth preserving. Deciding approval before the
password meant anyone who knew an account name learned whether it was pending, which
is account enumeration. Approving at `:L1470` before any password had been checked
meant a guessable pair of integers permanently approved somebody else's pending
account without their credential. Verifying first closes both. A legitimate user's
experience is unchanged: the same code still approves the same account, and the same
messages still distinguish "enter your code" from "that code is wrong".

**The verification code's composition is preserved exactly.** It is still
`portalId` and `userId` joined by a hyphen, because the value is never stored - it is
recomputed at check time - so any account awaiting verification at migration time
still holds an emailed code that must continue to work. Changing the composition
would strand every one of them. Rate limiting on the authentication endpoints is the
compensating control for its guessability, and is recorded as a requirement of the
API edge.

**A locked-out account is refused outright**, rather than relying on a null return
to contradict a boolean that said the caller was authenticated. The automatic-unlock
window is preserved: an account whose lockout has elapsed is unlocked and the
sign-in continues, exactly as `:L1454-L1461` did.

**Refresh and revocation are deliberately not duplicated here.** They remain on
`ITokenService`. A pass-through on the authentication contract would be a second
place for the same contract to drift. `GetCurrentUserAsync` takes no identifier at
all - the subject is the caller, read through `ICurrentUser` - and its roles and
permissions are read from the store rather than copied out of the token.

### A portal alias is refused rather than silently rewritten

**Legacy behaviour.** `Website/admin/Portal/editportalalias.ascx` declares **no
validator of any kind** and one input with `MaxLength="255"` at `:L7`, fifty-five
characters wider than the column it feeds. Its handler at
`EditPortalAlias.ascx.vb:L206-L245` did nothing at all - silently - when the box was
empty (`:L209`), stripped everything up to and including `"://"` (`:L210-L212`),
stripped everything up to and including a doubled backslash (`:L213-L215`), and
learned about a collision only by catching the exception the unique index raised
(`:L223-L228`).

**Target behaviour.** Alias writes take dedicated contracts -
`CreatePortalAliasRequest` and `UpdatePortalAliasRequest`, each carrying the one
value a caller decides - validated by a shared rule set,
`Application/Validation/PortalAliasRules`. An alias is required, is at most **200**
characters, and must be a host with optional port and optional path: dot-separated
labels of letters, digits and hyphens with no empty label and no label beginning or
ending with a hyphen; a port of one to five digits between 1 and 65535; and path
segments of letters, digits, hyphen, underscore and dot with no empty, `.` or `..`
segment. Whitespace, control characters, `\`, `@`, `?`, `#` and the substring
`"://"` are refused outright. `PortalAliasDto` becomes a response projection only.

**Why refusing beats stripping.** The screen's own help text already instructed
operators to omit the prefix -
`EditPortalAlias.ascx.resx:L123-L125` names the four acceptable forms, "a local
address", "an IP address", "a full URL" and "a server name", and says not to include
the `http://` prefix - so refusing enforces the documented contract rather than
tightening it. For a JSON caller the difference matters more than it did for a
form: stripping means a caller receives success and then reads back a value it never
sent, and silence on an empty box means a caller receives success and nothing
happened at all.

**Why two request types rather than one.** The rule *set* is shared; the types are
not. One shared type would make every future member of one contract a member of the
other by default, which is how an update quietly acquires the ability to set
something only a create should decide.

**Why a projection was the wrong shape for a write.** It reported the host name as
*nullable*, because the column is nullable and a reader must represent what it
finds, so nothing in the type system stopped an absent alias reaching the call; it
carried a database-assigned identifier a create cannot supply; and it carried an
owning-portal identifier the route already fixes. Both write members previously had
to document two of its three members as ignored. There is now nothing to declare
ignored.

**Case is accepted as submitted**, because the help text's own server-name example
is upper case. Lower-casing on write remains a storage decision, made where the
legacy code made it, at `PortalAliasController.vb:L31` and `:L97`.

**The scan is hand-written, with no regular expression.** The accepted vocabulary is
small and positional and the scan cannot backtrack, so there is no
catastrophic-matching exposure to bound with a timeout. Uniqueness remains the
service's answer, because only the store knows which aliases are already bound.

**A pre-existing disagreement, recorded not fixed.** The legacy screen's
`MaxLength="255"` contradicts the column's `nvarchar(200)`
(`02.02.02.SqlDataProvider:L3807`). The column governs. The screen is a read-only
reference input and is left exactly as it is.

### A billing frequency travels as its legacy character, and a permission key as its name

**Legacy behaviour.** `Roles.BillingFrequency` and `Roles.TrialFrequency` are
`char(1)` columns holding `N`, `O`, `D`, `W`, `M` or `Y`, and the codes are
load-bearing data rather than an internal encoding: `04.08.00.SqlDataProvider` gates
the trial columns behind `case when R.TrialFrequency <> 'N'`, and
`Library/Components/Security/Roles/RoleController.vb:L521` tests
`TrialFrequency.ToString() <> "N"`. `Permission.PermissionKey` is terminally
`varchar(50) NOT NULL` - `varchar(20)` at `02.02.00.SqlDataProvider:L688`, widened by
`04.06.00.SqlDataProvider:L397-L398` - holding `VIEW`, `EDIT`, `READ` or `WRITE`; no
number for that concept is stored anywhere.

**Target behaviour.** Two explicit converters pin both wire forms.
`DnnMigration.Application.Serialization.BillingFrequencyJsonConverter` emits and
accepts the single legacy character; `PermissionKeyJsonConverter` emits and accepts
the member name. Both are registered as one unit through
`Serialization.DnnJsonConverters`. On the persistence side,
`Infrastructure.Persistence.ValueConverters.BillingFrequencyToStringConverter`
replaces the conversion that had been declared inline, and both frequency columns
bind the same shared instance.

**Why.** The frequency enumeration is backed by `ushort` and gives each member the
code point of its own character, so the default treatment of an enumeration would
put `77` on the wire where the contract, the column and the client all expect `"M"`.
The permission enumeration gives no member an explicit value, so the default
treatment would publish incidental ordinals that a harmless reordering of the
declaration would change. Both failures are silent in both directions - a caller
would send the number back and nothing would report a problem - which is why the
wire form is pinned rather than left to a default.

**Why not the framework's string-enumeration converter.** It emits the member
*name*, which for a frequency is `"Month"` - a value the legacy vocabulary does not
contain. Registering it as a blanket policy would therefore have to be ordered behind
the specific converter to stay correct, and correctness that depends on registration
order is a trap. One explicit converter per type is registered and no blanket policy
is, which also leaves every other enumeration's wire form an explicit decision at
the API edge rather than one taken by accident.

**The two halves disagree about case, deliberately.** The persistence converter
resolves a stored `'m'` to the enumeration's "no frequency" member, not to `Month`,
because that is what the legacy application did: VB compares strings with
`Option Compare Binary` by default, so `RoleController`'s `Select Case` matched only
the upper-case spellings. SQL Server's default collation is case-insensitive, so a
legacy installation could hold `'m'` and the schema's own
`FK_Roles_CodeFrequency` would accept it, yet the application never read it as a
month - resolving it now would change behaviour rather than preserve it. The wire
converter takes the opposite position and upper-cases an inbound character, because
caller text is not stored data, no round-trip through the column can produce a
lower-case code, and the six codes are six distinct letters so no spelling is
ambiguous.

**Unrecognised input is refused on the wire and tolerated from the store**, for the
same reason and in opposite directions. Rule T4 makes an existing database
authoritative and only one of the two columns is constrained against the legacy
vocabulary table, so an arbitrary character in `TrialFrequency` is a row a legacy
installation already accepts; throwing while materialising it would make a legitimate
row unreadable and take an entire result set with it, so it degrades to "no
frequency". A caller carries no such authority, so reading `"Q"` as "no billing
frequency" would create a role the caller did not ask for, and it is refused as a
field-level failure instead. Serialising a value outside the declared vocabulary is
refused in both converters, because it can arise only from server code casting an
arbitrary number and shipping an uninterpretable value to a client is worse than a
visible failure.

**Note on the read direction.** Both converters accept input as well as producing
it. For the billing frequency that is a present-tense requirement: `CreateRoleRequest`
and `UpdateRoleRequest` are inbound contracts carrying the value. For the permission
key it is not - no request or response contract carries that enumeration, which is
published as a plain string, so the converter is registered against the first contract
that ever carries it rather than against one that does. Its read half is kept because a
converter that emits a name has to accept one. It matches the four names explicitly
rather than through `Enum.TryParse`, which would also have accepted numeric text - so
`"3"` would quietly have resolved to `WRITE`, reintroducing the very ordinal dependence
the converter exists to remove.

**No value conversion is needed for the permission key on the persistence side.**
`Permission.PermissionKey` is deliberately a plain string on the domain entity, so
that an installation carrying a key this codebase has not seen still round-trips
intact. Only the wire form is pinned.

### Six upgrade-script citations were wrong, and were repaired by measurement

**What happened.** Six inline citations named the wrong line of an upgrade script.
Each was found by reading the cited script rather than by trusting or re-deriving the
citation, and each repair names the column that actually occupies the line that had
been cited, so the same mistake is not made again.

**The repairs.**
`UpdateUserRequestValidator` cited `02.02.01:L51-L52` for the nine-column drop; the
`ALTER` is `L50` and the `DROP` `L51`. `EmailAddress` cited `01.00.05:L19`, which is
`Street nvarchar(20) NULL`; the address column is `L25`. `UpdatePortalRequest` cited
four lines of the baseline `Portals` block wrongly: `FooterText` as `L81`, which is
`LogoFile`; `LogoFile` as `L80`, which is `UploadDirectory`; `Currency` as `L87`,
which is `PayPalId`; and `AdministratorId` as `L85`, which is `BannerAdvertising`.
The correct lines are `L82`, `L81`, `L88` and `L86`.

**Why this is recorded.** A citation is the only evidence a later reader has that a
mapped width or nullability was measured rather than guessed, so a wrong citation is
worse than none: it invites the next author to trust it. Solution-wide sweeps
afterwards confirmed that every `02.02.01` drop citation reads `L50-51` and every
`03.00.13` re-add citation reads `L109-110`.

## Session lifetime, token storage and service wiring

> This file is append-only. Add a new `###` entry under the section below; do not
> restructure, reword or remove an existing entry.

### A portal that names no administrator, and no roles, is now representable

**Legacy behaviour.** The portal record has always permitted these facts to be absent.
`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L86` declares
`[AdministratorId] [int] NULL`, `L91` declares `[AdministratorRoleId] [int] NULL` and
`L92` declares `[RegisteredRoleId] [int] NULL`; the rebuild at
`01.00.05.SqlDataProvider:L1379-L1380` reaffirms the two role keys as `NULL`. A portal
mid-creation, or one whose administrator account has been removed, legitimately holds
nothing in these columns.

**Target behaviour.** The per-request tenant snapshot now carries `AdministratorId`,
`AdministratorRoleId`, `AdministratorRoleName`, `RegisteredRoleId` and
`RegisteredRoleName` as nullable, and the snapshot's constructor accepts a portal that
names none of them. Previously all five were non-nullable, and the constructor
additionally refused a null or empty role name outright, which made such a portal
impossible to represent at all rather than merely awkward. A half-populated pair — a role
key with no name, or a name with no key — is still refused, because one without the other
would leave a role either unnameable or unresolvable.

**Why.** These are authorisation facts. Coercing a database null onto zero does not lose
information quietly: zero is a legitimate role key in this schema, seeded by
`01.00.00.SqlDataProvider:L115`, so the coercion manufactures a real, valid-looking
administrator role where the portal named none. The existing rule that negative one and
zero are legitimate keys and must never be read as absence is the same rule that forces
this change — precisely because no integer is free to mean "absent", absence has to be
carried by null instead. A blank name is refused rather than accepted as "no role" for the
same reason: the legacy sentinel helper used empty text as its absent marker
(`Library/Components/Shared/Null.vb:L71-L75`) while the legacy checks compared role names
for equality (`Library/Components/Security/PortalSecurity.vb:L519`), so a blank name would
be a value capable of matching inside an authorisation decision.

### A renewed session now ends at a fixed ceiling, however continuously it is used

**Legacy behaviour.** The sign-in credential was a Forms authentication ticket set at
`Library/Components/Users/UserController.vb:L1033`, with an optional long-lived variant at
`L1036-L1052` governed by the `PersistentCookieTimeout` setting at
`Website/release.config:L51`. The ticket was a plain bearer value: it was not single-use,
it was not rotated, and no server-side record of it existed, so it remained valid for the
whole of its window and a single capture yielded unlimited reuse.

**Target behaviour.** Session renewal is single-use and rotated, and each token family now
carries an absolute ceiling fixed when the family is created — `Jwt:RefreshTokenAbsoluteExpirationDays`,
defaulting to 30 days. A renewal restarts the sliding window but copies the ceiling forward
untouched, and every issued expiry is the earlier of the two bounds. A session in
continuous use therefore still ends, at which point the holder must authenticate again.
Configuration is validated at startup: the ceiling must be positive and must not be below
the sliding window, since a ceiling beneath it would silently truncate every token and make
the sliding setting unreachable.

**Why.** Rotation without an absolute bound converts a time-limited credential into an
unlimited one, because each renewal grants a fresh window. Re-authentication is also the
only moment at which a password, an approval state and a lockout state are re-examined, so
a session that never ends is a session that never revisits whether it should still exist.
The 30-day default is net-new rather than ported; the closest legacy analogue is the
persistent-cookie timeout, which bounded a cookie that was neither single-use nor
revocable.

### A replayed refresh token now ends every session that user holds

**Legacy behaviour.** Not applicable — there was no refresh credential to replay, and
consequently no replay detection. Signing out reached only the browser's copy of the
ticket: `Library/Components/Security/PortalSecurity.vb:L77-L95` calls
`FormsAuthentication.SignOut()` at `L79` and expires cookies at `L82-L94`, every statement
targeting the response. A ticket already copied from the browser stayed valid for the whole
of its remaining window.

**Target behaviour.** Presenting an already-redeemed refresh token is treated as evidence
that token material has been copied. Every token family belonging to that user is revoked
— not merely the family that was replayed — including live replacements the legitimate
holder is using. The same user-wide revocation follows an administrative credential reset.

**Why.** A replay proves a token was copied but says nothing about which of that user's
sessions the copy came from. Ending only the replayed family would leave an attacker
holding whichever other family it had also copied. Ending every session forces the
legitimate holder to sign in again, which is the correct trade: the alternative leaves a
working credential in an attacker's hands. Revoking after an administrative reset follows
the same reasoning — a reset that left an existing session renewable would not be a reset.

### Stored session material is bounded, and the identity snapshot is released early

**Legacy behaviour.** Nothing was stored server-side, so nothing accumulated and nothing
needed releasing. The trade-off was the one recorded in the entry above: a captured ticket
could not be invalidated.

**Target behaviour.** Server-side token records now exist, and their retention is bounded
three ways. A total-entry cap is enforced, and issuing refuses with a service-level failure
rather than growing without limit once the cap is reached; the refusal is never reported as
a credential rejection, which would be untrue and unactionable. Whole families are
discarded once their ceiling has passed. And the sign-in name, role list and permission
list recorded against a token are released as soon as that token can no longer be redeemed
— on redemption, on revocation and on expiry — leaving only the family, generation, owner
key, lifecycle flags and expiries that replay detection, revocation and pruning still
require. Cleanup is opportunistic, inside operations that already hold the store's lock; no
timer and no background worker is introduced.

**Why.** An unbounded in-memory store reachable from an unauthenticated endpoint is a
denial-of-service lever, and refusing a sign-in while the process stays healthy is
preferable to exhausting memory and failing every session at once. Discarding a redeemed
entry early would be a security regression, because it would make a replay of that entry
indistinguishable from an unknown token — so entries are kept until their family's ceiling
passes and no longer. That is safe precisely because of the ceiling introduced above: once
it passes, every generation of the family is refused on expiry anyway, so forgetting the
family cannot turn a refusal into an acceptance. The two changes are one design and were
made together. Holding a name and a role list against a token that can never be honoured
again serves nothing, which is why the snapshot is released at the moment the entry becomes
unusable rather than when the family is finally dropped.

### Infrastructure implementations are reachable only through the container

**Legacy behaviour.** Collaborators were resolved by reflection from configuration.
`Library/Components/Providers/Data/DataProvider.vb:L38-L40` declares a shared constructor
that delegates to a private `CreateProvider` method, which calls
`Framework.Reflection.CreateObject` at `L44`. The provider type, namespace and assembly
name it passes are compile-time constants at `L30-L32`; the type is what maps the call onto
a configuration section group, as the comment on `L30` records, so the concrete type
resolved is configuration-driven even though those three arguments are not. The resulting
singleton is exposed through the static `Instance()` accessor at `L48`, and
`Website/release.config` declares fourteen such providers. A mistyped provider name failed
at first use rather than at startup, the static accessor could be reached from anywhere so
nothing declared what it depended on, and assembly probing left the set of loadable
implementations open.

**Target behaviour.** Every concrete Infrastructure type is internal, and the assembly
exports exactly one public type: the registration entry point `AddInfrastructure`. Nothing
outside the assembly can name an implementation, let alone construct one. The clock, cache
service, password hasher, refresh-token store and tenant-snapshot factory are each
registered there against a Domain-layer contract. Because those contracts must be visible
from the Domain layer, the refresh-token vocabulary — the subject snapshot, the three result
types and the outcome enumeration — moved out of the Infrastructure implementation file and
into the Domain layer alongside the store abstraction. The token service is deliberately
not registered: its contract exists but no implementation of it exists in this solution
yet, and registering a placeholder would satisfy the container while failing every caller
at runtime.

**Why.** A caller that cannot name the password hasher cannot come to depend on which
algorithm is in use, and a caller that cannot name the token store cannot come to depend on
its storage medium, locking strategy or retention policy; swapping either then costs one
line. An unresolvable dependency is also reported at startup with the name of what is
missing, which is strictly better than a placeholder that defers the failure to a request.

**Note on test visibility.** No `InternalsVisibleTo` attribute is declared anywhere in the
solution, and this is a decision rather than an omission. Tests build a container by
calling `AddInfrastructure` and resolve the same contracts production code resolves, so
they exercise the wiring rather than bypassing it. Granting a test assembly access to the
internals would let a test construct a concrete type directly, allowing a test to pass
while the registration is broken, and would quietly turn private implementation detail into
a contract that could not be changed without breaking tests. Anything a test genuinely
needs to observe belongs on the Domain contract, where production callers can rely on it
too.


## API edge, composition root and observability

### Every problem-details payload now carries a status, a title and a detail

**Legacy behaviour.** The legacy application returned whatever `customErrors` produced - an
HTML error page, with no machine-readable body of any kind. The migrated target specified
RFC 7807 payloads, but the framework only fills a payload's `title` and `type` from
`ApiBehaviorOptions.ClientErrorMapping`, which by default holds CLIENT-error rows only. Every
5xx response therefore carried a null `title` AND a null `type`, and `detail` was never
defaulted on any path, at any status code.

**Target behaviour.** `Api/Filters/ValidationProblemDetailsFactory.cs` carries a status
vocabulary covering 400, 401, 403, 404, 405, 406, 409, 415, 422, 429, 500, 501 and 503, each
with its RFC 9110 or RFC 6585 definition link. Resolution order is: a value the caller
supplied, then the framework's own mapping, then that vocabulary, then
`ReasonPhrases.GetReasonPhrase` for a status nothing covers, then a fixed fallback. `detail`
is guaranteed unconditionally. A caller-supplied empty title is still preserved, because the
tests are against null rather than emptiness.

**Why.** A consumer cannot branch on a field that is sometimes absent. Guaranteeing the three
members means one client-side shape handles every failure the API can produce.

**Note on `type`.** It deliberately receives no blanket fallback. Where no definition document
exists for a status code, the member is left absent - which RFC 7807 permits - rather than
filled with a URI that documents nothing. An invented link is worse than an honest omission,
because a reader would follow it.

### An invariant violation is answered 500 with fixed text, not 400 with its own message

**Legacy behaviour.** The migrated target answered a `DomainException` with 400 and published
the exception's own message, on the reasoning that the text was authored by our own domain code
for a reader.

**Target behaviour.** The case is removed. A `DomainException` now falls to the general case in
`Api/ErrorHandling/GlobalExceptionHandler.cs`: status 500, and the same fixed
`UnexpectedFailureDetail` every other unexpected failure receives. The type and stack are
recorded in the log against the correlation identifier.

**Why.** Both halves of the original reasoning were wrong, and either alone is sufficient.

The message is not reliably free of caller-supplied text. `PortalGuid.Parse` interpolates the
rejected value straight into its message at
`backend/src/DnnMigration.Domain/ValueObjects/PortalGuid.cs:L233-L237`, with the
interpolation itself on L234, so whatever a caller
submitted was reflected back in a published response - and a value submitted to the wrong field
can be a credential or personal data. Two of the three construction sites happen to be safe:
the invalid-handle message at `PortalGuid.cs:L159` is fixed text, and `EmailAddress.Create`
documents at `EmailAddress.cs:L336` that its message names the failing clause and never repeats
the rejected value. Safety that holds at two sites of three, and is guaranteed at none, is not
a property a public boundary can publish against.

The status code was wrong independently of the text.
`Domain/Common/DomainException.cs:L31-L48` sets out a two-part error model as a list. Its first
item describes an invariant violation as "a defect in the calling code" that "has no meaningful
recovery at the call site" (L36); its second requires every EXPECTED failure - a validation
miss, an absent record, a duplicate name, a wrong password - to be RETURNED as a failed
`Result`, and states that it "is never thrown" (L45). A `DomainException` arriving at the boundary therefore means a defect escaped,
which is a server fault. Nothing legitimate is reclassified: the failures a caller should see
as 400 never reach this handler as exceptions at all.

### Log entries name a route template and a redacted failure, never the exception or the path

**Legacy behaviour.** The four log call sites in the unhandled-exception handler passed the
exception OBJECT as the first argument - which records its message, every inner exception's
message and the stack trace - and recorded `httpContext.Request.Path` as the operation
identifier.

**Target behaviour.** Both are replaced. The operation is identified by its declared route
template, read from the selected endpoint's route pattern, substituting a fixed
`(no matched endpoint)` where no pattern is available. The failure is described by
`DescribeForDiagnostics`, which emits each exception's type name and full stack trace, outermost
first, bounded to eight links - and NO message from any of them. Every entry carries a fixed,
named event identifier: 1001 client disconnected, 1002 response already started, 1003 unhandled
server fault, 1004 request refused. The allowlist is closed at event identifier, request method,
route template, failure description, status code and correlation identifier.

**Why.** An exception message is not always authored text. Framework and library messages
routinely quote the input that failed - a value that would not parse, a key that was not found,
a connection string a provider rejected - and application messages do too. Which messages do
cannot be established by inspecting the handler, so an allowlist admitting only what is known
to be safe is the only shape that stays correct as exceptions the handler has never seen reach
it. A request path is caller input for the same reason: identifiers in it are frequently
personal, and a mistyped credential lands there.

**Note on what is kept.** Stack traces are retained in full. They are method, type and, where
symbols are published, file and line - all authored by us or by a library, none of it caller
input. The redaction is of messages specifically, not of diagnostic detail generally, so an
entry remains actionable. A route template is also strictly better than a path for aggregation,
because every request to one operation reports the same value.

### The API version segment is mandatory

**Legacy behaviour.** Versioning was configured to read the version from the URL path and
nowhere else, while also accepting a request that named no version at all and answering it with
the default version.

**Target behaviour.** `AssumeDefaultVersionWhenUnspecified` is false. An address without the
version segment does not match. The default version is retained, because the API explorer uses
it to name the document.

**Why.** The two settings were incoherent together. The version is a path SEGMENT, so a request
without it is a request to a different address - not the same address with a field left blank.
Accepting it meant the application answered both `/api/v1/portals` and `/api/portals`, with the
second form silently pinned to whatever the default happened to be. Every such caller would then
be broken by the arrival of v2, and could never have been warned: the framework advertises
supported and deprecated versions per response, which can only describe a version the request
actually named.

### The bearer requirement is published per operation, not across the whole document

**Legacy behaviour.** The OpenAPI document declared the bearer security requirement globally, so
every documented operation appeared to require a token.

**Target behaviour.** An operation filter attaches the requirement only where the operation does
not allow anonymous access, reading the same endpoint metadata the authorization middleware
reads.

**Why.** Some endpoints must be reachable without a token, and the document said the opposite of
the contract for every one of them. Sign-in and refresh are called precisely when the caller has
no token yet, and `/health` is probed by the container orchestrator with no credential at all.
The interactive console also sent an `Authorization` header where none belongs.

**Note on the direction of the test.** The filter tests for the ABSENCE of `IAllowAnonymous`
rather than the presence of an authorization attribute. That is the conservative direction: an
operation protected by a fallback policy or a convention the filter cannot observe is still
documented as requiring a token. Testing for presence would have published such an operation as
open, which is a false contract; being wrong in this direction costs a reader one redundant
header.

### Portal administration is decided per request, against the portal the request addresses

**Legacy behaviour.** The legacy screens tested
`PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)` against the ambient per-request
portal settings; the canonical site is
`Website/admin/Security/SecurityRoles.ascx.vb:L322`. The migrated target specified the
equivalent policy as being registered with the framework-native role requirement,
`RequireRole`, reading the administrator role name from bound configuration.

**Target behaviour.** The policy is registered with `AddRequirements` and an empty
`PortalAdministratorRequirement`, and decided by a scoped
`PortalAdministratorAuthorizationHandler` that reads the addressed portal's administrator role
from `IPortalContext`.

**Why.** `RequireRole` closes over the role names given to it at start-up, and there is one
registration for the whole process - whereas the administrator role is a per-portal column in a
multi-tenant installation. One portal's role name would have gated every portal. Both failure
directions are severe: where the configured name matched another tenant's administrator role, a
caller who administers portal A would pass the gate on portal B, which is a cross-tenant
privilege escalation; where it matched no role the caller held, a legitimate administrator was
locked out of their own portal.

**Note on case sensitivity.** Role names are matched with `OrdinalIgnoreCase` by enumerating
role claims directly, rather than through `ClaimsPrincipal.IsInRole`. `IsInRole` compares the
claim value with `StringComparison.Ordinal`, and role names are `nvarchar` columns read under SQL
Server's default case-insensitive collation, so the legacy test matched without regard to case.
Using `IsInRole` would have refused an administrator whose token spelled the role name in a
different case from the portal row. Each identity is inspected through its own role claim type,
because that type is a property of the identity rather than of the principal.

**Note on the role identifier.** The portal's `AdministratorRoleId` is deliberately not
consulted. No role-identifier claim is issued, so a comparison against it could never match.

### The process refuses to start on a bad setting, and no secret is committed

**Legacy behaviour.** Configuration was read loosely at the point of use, so a missing or
malformed setting surfaced as a failure during a request rather than at start-up - and the 3DES
key that decrypted every stored password was committed to source control at
`Website/release.config:L89-L93`.

**Target behaviour.** `Api/Extensions/ServiceCollectionExtensions.cs` binds all four options
sections with `ValidateOnStart`, routing each options type's own `Validate` method through
`ValidateOptionsResult.Fail` so that EVERY broken rule is reported, not merely the first. The
committed `appsettings.json` declares `Jwt:Secret` as an empty string, which fails validation, so
the process refuses to start until the secret is supplied from the deployment's secret store or
through the `Jwt__Secret` environment variable.

**Why.** A configuration fault should stop a deployment, not degrade it. Reporting every failure
at once means one restart reveals all of them instead of one per restart. And a signing secret
that lives in the repository is the same defect the legacy password store had; declining to
commit one, even for convenience, is the whole point of moving away from it.

**Note on transport security.** No HTTPS redirection is configured. TLS terminates at the reverse
proxy, and the container health check probes the API over plain HTTP on the internal network, so
a redirect would break it. `UseForwardedHeaders` is configured instead, so the application sees
the original scheme and caller address from the headers the proxy sends.

**Note on the portal-alias resolution stage.** Its position in the pipeline is documented and
deliberately left empty rather than filled with a stub, because the stage is not yet part of the
solution. The consequence is explicit: the scoped `IPortalContext` registration throws with a
message naming the missing stage and the request-item key it must publish under, so an endpoint
that depends on a portal fails immediately and says why - instead of quietly serving one tenant's
data against another tenant's context.

**Note on the permission policies.** The four permission policy names remain in the catalogue but
are deliberately NOT registered, because the handler that consults the module and tab permission
triads is not yet part of the solution. A policy registered with no handler able to satisfy it
denies every request that uses it while the registration still reads as legitimate; an absent
policy fails loudly at start-up the moment an endpoint names it, which is far easier to diagnose.


## Front-end shell, routing and test harness

### The application declares a single catch-all route, and could not have declared any other

**Legacy behaviour.** The legacy application had no client-side router. Every address was a real
server resource: `Default.aspx` resolved a tab from the requested alias and tab identifier, and the
page pipeline composed a skin, containers and user controls for it. An address that matched no tab
was answered by the server.

**Target behaviour.** `frontend/src/app/app.routes.ts` declares exactly one route - a `**` wildcard
that lazily loads the shared empty-state component and gives it the sentence *"No administration
screen is available at this address."* Every address therefore renders the shell with an explanatory
panel inside it.

**Why.** Both alternatives were tried and both are unavailable, for different reasons.

Declaring the twenty-five feature routes the plan specifies is a BUILD failure, not a runtime one.
The bundler resolves dynamic import specifiers statically, so a route naming a feature route file
that does not yet exist fails the production build outright - it cannot be deferred to the moment a
reader visits that address.

Declaring an empty route array is a runtime failure. The router raises NG04002 on every load when no
route matches, so the console would carry an error on the very first paint of every session.

The wildcard is the only arrangement that both builds and runs clean, and it degrades honestly: a
reader who follows a bookmark to a screen that is not yet part of the application is told so, rather
than being shown an empty frame or a console error.

**Note on ordering.** The wildcard MUST remain the last element of the array for as long as the file
exists. The router matches in declaration order, so a wildcard placed above a real route silently
shadows it - and shadowing produces no error at build time and no error at runtime, only a screen
that never appears. The file carries this constraint inline for the same reason it is recorded here.

### The production API base path is relative and the development one is absolute

**Legacy behaviour.** The legacy application read `SiteSqlServer` and its provider settings from
`web.config` and served its markup from the same origin as its data, so no notion of an API base
address existed anywhere in the source.

**Target behaviour.** `frontend/src/environments/environment.ts` publishes the RELATIVE
`apiBaseUrl: '/api/v1'`. `frontend/src/environments/environment.development.ts` publishes the
ABSOLUTE `apiBaseUrl: 'http://localhost:8080/api/v1'`.

**Why.** The two environments reach the API through genuinely different mechanisms, so a single
value cannot serve both.

In a container the reverse proxy owns the join: its configuration proxies `/api/` to the API service
by its compose service name. That name resolves only on the container network, never in the reader's
browser. An absolute `http://api:8080/api/v1` would compile, bundle, produce a clean image and report
both containers healthy - and then fail every single request at runtime, because the browser cannot
resolve a compose service name. This is the most breakage-prone value in the front-end tree precisely
because every automated check upstream of a real browser passes.

In development there is no proxy. A relative path would resolve against the development server,
which answers unmatched paths with `index.html` and a status of 200. The request would therefore
SUCCEED and the JSON parse would fail afterwards on markup - a failure that reports itself as a
parser error at a point unrelated to its cause.

**Note on the host spelling.** The development value uses `localhost` rather than `127.0.0.1`
although the two reach the same interface, because the API's cross-origin policy names the Angular
development origin by its `localhost` spelling. The two spellings are different origins to the
browser's same-origin rules, so substituting one for the other turns every request into a rejected
cross-origin request.

### The document title is fixed markup rather than composed per request

**Legacy behaviour.** The legacy page composed its title on the server per request, from the portal
and the resolved tab, so a reader saw the current portal and page in the browser's title bar.

**Target behaviour.** `frontend/src/index.html` ships the fixed title *DotNetNuke Administration*,
and the router supplies a per-route title where a route declares one - the wildcard route sets
*Not Found - DotNetNuke Administration*, which was observed replacing the shipped title at runtime.

**Why.** The shipped title is what a reader sees during the interval between the document arriving
and the application bootstrapping, and it is what a crawler or a link preview sees. Leaving it as a
generator placeholder would publish that placeholder; composing it from portal state is impossible
at that instant, because no portal has been resolved and no request has been made. A fixed product
title is accurate at every moment, and the router refines it once a route is known.

### The shell carries the grid class on its own host element

**Legacy behaviour.** Not applicable. The legacy skin system had no component host; a skin was a
user control loaded into a placeholder declared in the page.

**Target behaviour.** `ShellComponent` declares `host: { class: 'shell' }`, so the grid class lands
on the `<app-shell>` element itself and the four regions are its direct children. No wrapper element
is interposed.

**Why.** The paired stylesheet settles it rather than leaving it to preference. Its rejection of
`display: contents` reasons that erasing this box "would leave the shell's REGIONS as children of the
root component instead". That consequence only follows if the regions are direct children of the host
and the host itself carries the grid class. Had the template rendered a wrapper, erasing the host
would have promoted the WRAPPER and the grid would have survived intact - which would have made the
stylesheet's stated reasoning false. The host arrangement is the one the stylesheet was written
against.

**Note on the rule this switches off.** The stylesheet's `:host(:not(.shell))` rule can now never
match, and that is the correct outcome rather than dead code. The rule exists to give the host a
plain block box only when it is NOT itself the grid, and it is deliberately written as a negated
functional selector so that it stands down instead of competing. Had it been written flat, it would
have been a same-specificity competitor to the grid declaration resolved by injection order -
component styles are injected after the global sheet - which would have silently replaced the grid
with a block box and collapsed the entire page layout, with no error reported anywhere.

### The shell renders three regions, not four, and the fourth is omitted rather than stubbed

**Legacy behaviour.** A legacy skin composed whatever panes its author declared, and the
administration skins declared a navigation menu alongside the content pane.

**Target behaviour.** The shell renders the skip link, the banner, the main region and the
contentinfo band. There is no sidebar element and no navigation region anywhere in the shell.

**Why.** The layout partial was authored to tolerate exactly this. Its sidebar rule declares
placement only - a grid area and an empty-state collapse - with no inline size and no breakpoint
branch, and the wide arrangement gives the sidebar an `auto` track. An `auto` track with nothing
placed in it resolves to zero width, so the main region takes the full measure and the two-column
arrangement degrades to one column with no override required. This was confirmed in a real browser:
the computed columns resolved to `0px 1440px`, and the skip row likewise to `0px` while the link is
clipped.

Rendering an empty sidebar to "hold the place" would have been worse than omitting it. It would
publish a navigation landmark with nothing in it, which assistive technology announces as a region a
reader can enter and then find empty.

**Note on why no navigation exists to render.** The legacy menu was produced by a navigation provider
against the tab hierarchy, and both the provider family and the tab administration screens are
outside this boundary. There is no route list to render a menu from: the application declares one
wildcard route. A menu would therefore have had to be invented rather than migrated.

### The skip link's activation is handled, and its default deliberately prevented

**Legacy behaviour.** The legacy stylesheets contain no focus selectors at all, in either reference
sheet, and no skip affordance of any kind. This whole mechanism is a net addition.

**Target behaviour.** The skip link is a real anchor whose `href` names the main region's fragment,
and `ShellComponent.focusMainRegion` handles its activation, calls `preventDefault`, and focuses the
main region directly. No address change occurs at all.

**Why.** Because letting the fragment resolve performs a FULL DOCUMENT RELOAD, which was measured
rather than reasoned about.

A fragment-only `href` is resolved against the document's BASE address, not against the address
currently showing. `index.html` declares a base of `/`, which deep links require, so
`#shell-main-content` resolves to `/#shell-main-content` - a different PATH from any route below the
root. The browser therefore treats activation as a cross-document navigation.

Activating the link from `/portals/3/settings` against the production bundle in a real browser
changed the address to `/`, re-bootstrapped the application and re-fetched every JavaScript chunk.
Focus still landed on the main region afterwards, because the browser applies the fragment steps
after loading and the target carries a negative tab index - which is exactly what makes the defect
dangerous. The affordance appeared to work while discarding the reader's screen and any unsaved
state on it, in exchange for skipping one banner. On the current build the loss is invisible because
the single wildcard route renders the same shell at every address; it would become plainly visible
the moment real routes exist.

Two alternatives were considered and rejected. Binding the anchor as a router link with a fragment
keeps the navigation inside the application, but the router changes the address through the history
API and a history update does not perform the browser's navigate-to-a-fragment steps, so focus would
stay on the link - replacing a visible regression with a silent one, in the one mechanism that exists
solely to move focus. Composing a path-qualified href from the current address would restore the
browser's own behaviour but would have to be recomputed on every navigation, and a stale value would
resolve to another route and reintroduce the reload.

Handling activation has neither problem: no address changes, so no route is discarded and nothing
needs recomputing, and focus is moved explicitly rather than as a side effect. `focus()` scrolls its
target into view by default, so the scroll the fragment used to provide is preserved.

**Note on keeping an href that is never followed.** It is retained deliberately. It is what makes the
anchor a tab stop and what makes assistive technology announce it as a link; an anchor without one is
neither. It also still names the correct element, so the relationship between the link and its target
stays inspectable. The consequence is that the guard is entirely the `preventDefault` call, so the
paired spec asserts `defaultPrevented` directly - a markup assertion cannot distinguish the working
arrangement from the broken one, because the href, the target and the tab order are identical in
both.

**Note on the router's anchor-scrolling option.** It remains enabled, but its justification changed
with this fix and the file was corrected rather than left asserting something disproved. It no longer
serves the skip link, which performs no navigation at all. It is kept because it is what makes the
supported form of in-page linking work - a router link carrying a fragment, which the router does
observe - and without it such a link would be scrolled to the top by the restoration setting beside
it.

**Note on the focus indicator.** No ring is authored for the main region, and none should be. The
reset applies the shared ring to natively focusable elements and explicitly excludes elements made
programmatically focusable only, which is what a skip target is; that exclusion is the reset's
decision and re-declaring the ring from the component would contradict it. Browser testing confirmed
the destination still carries a plainly painted indicator, because the user agent draws its own
around an element focused programmatically. It is the user agent's default rather than the token
ring, which is a knowing consequence of respecting the reset rather than an oversight.

### The banner is net-new, and identifies the product with text rather than an image

**Legacy behaviour.** The legacy banner was a skin object that composed an `<img>` from the portal's
configured logo file, hid itself when no logo was configured, and carried the portal name as its
tooltip and a link to the portal home. Its wording came from resource files, and its session cluster
paired a display-name control with a sign-in or sign-out caption chosen at runtime.

**Target behaviour.** `HeaderComponent` renders a single text identity affordance reading the product
designation, linked to the application root through a router link, plus a session cluster gated
entirely behind a single condition.

**Why.** A configured logo is per-portal state that arrives with a resolved portal, and no portal is
resolved: the alias-resolution stage is not part of the solution and no route carries a portal. An
`<img>` would therefore have had to point at either a guessed path or an empty source, and an image
with an empty source is a broken-image affordance in the banner of every screen. Text is accurate
without portal state, and the legacy tooltip and home link are preserved on it.

**Note on the session cluster.** It is published as inputs and an output, and deliberately not
supplied by anything. There is no authentication store, no token service and no signed-in reader in
the front-end tree. A band with no session shows no session controls; the alternative - rendering an
empty name beside a sign-out button that reports to nobody - would look finished and behave
incorrectly. The cluster is gated on a derived condition rather than on the stylesheet's
empty-element collapse, because that collapse is defeated by a single whitespace text node and
whether one survives depends on the compiler's whitespace handling rather than on anything the
component states.

### The Karma configuration probes for a browser binary instead of assuming one

**Legacy behaviour.** The legacy solution contains no automated tests of any kind and no test
runner configuration.

**Target behaviour.** `frontend/karma.conf.js` resolves a browser binary through three tiers, and
declares four resilience settings: a capture timeout, a disconnect timeout, a disconnect tolerance
and a no-activity timeout.

**Why.** The probing is load-bearing here rather than defensive, which was established by
measurement: in a non-login shell both browser environment variables are UNSET, because the
environment file that exports them runs only for login shells. Without probing, the run fails to
launch a browser at all - and it fails at launch, so no assertion result of any kind is produced.

The three tiers are ordered deliberately. An explicitly supplied binary is honoured WITHOUT
verification, because silently replacing an operator's stated choice with a different browser would
be worse than failing on it. A path-style variable is promoted next. Only then is a list of known
distribution locations tried. If none resolves, the resolver returns nothing and sets nothing, rather
than setting a path that does not exist - which would replace a recoverable situation with an
unrecoverable one. All three tiers were proven by execution rather than by reading.

**Note on raising timeouts.** It cannot mask a failing assertion. A failed expectation is reported by
the assertion framework and never reaches the disconnect machinery; the timeouts govern only the
launch and transport of the browser itself, where the failure mode is a run that produces no result
rather than a wrong result.

### Six feature stylesheets remain without components, and say so in their own text

**Legacy behaviour.** Not applicable. These files have no legacy counterpart; they are part of the
migrated target.

**Target behaviour.** Six component stylesheets under the feature tree have no component, template
or spec beside them. Each now opens with a note naming the three absent files, the route it styles,
the selector vocabulary a future template must emit, and an inventory of the dependencies that block
authoring it - each marked present or absent as measured.

**Why.** An orphan stylesheet is indistinguishable from an abandoned one, and the risk is that a
later reader deletes it as dead weight. It is not dead: it is the specification for a screen whose
prerequisites - shared components, models, services, stores, guards and the feature route files -
are largely absent, and re-deriving it would mean re-reading the legacy screens it was measured
from. Each note therefore carries an explicit instruction not to delete the file.

**Note on what they cost.** Nothing, and this was measured rather than assumed. No component
references any of the six, so none reaches a bundle and none is measured against the per-component
style budget. Each was additionally compiled directly against the workspace's own Sass
implementation to prove the claim positively: all six compile clean, every partial reference
resolves, and the largest emits a little over three kilobytes - comfortably inside the budget that
would apply once a component adopts it.
## Infrastructure layer

### `backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs` — the ceiling is an addition to the policy, not a change to it

**This entry qualifies one sentence written earlier in this document.** The
password entry above records that the legacy password *policy* is "carried over
verbatim". That remains true of everything the legacy provider actually
specified — the minimum length of seven, the zero required non-alphanumeric
characters, the absent question-and-answer requirement and the unenforced email
uniqueness are all reproduced exactly. The 256-byte ceiling described under
*A password may not exceed 256 UTF-8 bytes* is an *addition* to that policy, not
a change to any part of it, and it is the only one.

**The ceiling applies on both sides of the hasher, and on the sign-in path too.**
`Hash` raises rather than truncating, so a credential can never be stored under a
hash that represents only part of it, and `Verify` refuses an over-long candidate
outright rather than comparing a prefix of it. The create-user, change-password,
portal-administrator **and sign-in** request validators apply the same ceiling
first, so an ordinary caller receives a field validation failure rather than an
exception. Sign-in is deliberately included: the value is generous enough that no
password a person chooses reaches it, so refusing an over-long submission
discloses nothing an attacker did not already supply, and bounding the input
before it reaches a deliberately expensive hash is the whole point of having a
bound on an unauthenticated endpoint.

**Operational consequence.** A passphrase longer than 256 UTF-8 bytes cannot be
registered or set, and must be shortened.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`,
`backend/src/DnnMigration.Application/Validation/CredentialBounds.cs`.

### `backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs` — refresh-token sessions have no legacy ancestor

**Legacy behaviour.** There is nothing to preserve, because DotNetNuke 4.9.0 had
no refresh mechanism at all. It renewed access by *sliding* the Forms
Authentication ticket configured at `Website/release.config:L146-L147`, whose
window was sixty minutes, and the `PersistentCookieTimeout` setting at
`Website/release.config:L51` shipped switched off, at zero. The browser never
held a token, never learned when its own window closed and never called a
renewal endpoint. Every rotation, replay and retention rule below is therefore
new behaviour rather than a translation, and is recorded here for that reason.

**Four rules that are deliberate choices rather than obvious defaults.**

| Rule | Why it was chosen |
| --- | --- |
| A chain of refresh tokens is given its deadline once, when it begins, and every replacement inherits that deadline rather than receiving a fresh lifetime of its own. | A used marker on its own bounds nothing. Handing each replacement a full lifetime looks almost identical and behaves entirely differently: a caller that simply kept refreshing would hold a credential that never expired, and the configured lifetime would describe only the longest a holder may sit idle. |
| A detected replay revokes **every** refresh token the same subject holds, not merely the chain that was replayed. | A value having been copied says nothing reassuring about the others. Revoking only the one chain would end a single session and leave every other session that subject had opened untouched. |
| A record is kept for a bounded further margin after its deadline and then retired, taking its entries with it. | Both halves matter. Keeping it for a while means a replay arriving late is still recognised *as* a replay and still ends the subject's other sessions; retiring it eventually means ordinary traffic, rather than an attack, cannot exhaust memory. After retirement the same value is reported as unrecognised rather than as a replay. |
| The refresh lifetime is bounded above at 365 days, and a configured value beyond it is refused when the host starts. | Two guarantees are derived from that one setting — the deadline a chain may not outlive, and the margin for which a dead chain is retained — so an unbounded setting would quietly remove both while appearing to configure them. The legacy platform sanctioned no bearer credential longer than the sixty-minute window cited above, so a one-year ceiling is already generous. |

**Sign-out semantics, restated because this is where they are enforced.**
`FormsAuthentication.SignOut` has no stateless counterpart. Signing out revokes
refresh state, which ends the caller's ability to *continue* a session; it
cannot retract an access token already issued, which stays valid until its own
expiry. The short access-token lifetime and the rotation rules above are the
mitigation, and the client is expected to discard its copy.

**Operational consequence for an integrator.** A client that races itself —
presenting the same refresh value from two tabs, or retrying a request whose
response it never saw — can trigger the replay response and find itself signed
out everywhere. That cost is accepted knowingly: the alternative is to treat a
duplicated single-use credential as routine. A client should therefore serialise
its own refresh calls rather than rely on the server to tolerate concurrency.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Security/RefreshTokenStore.cs`,
`backend/src/DnnMigration.Application/Options/JwtOptions.cs`,
`backend/src/DnnMigration.Application/Abstractions/ITokenService.cs`.

## Cross-layer request contracts

### The portal identifier in the route and in the payload — there is no payload copy to disagree

**This entry records a hazard that was closed by removing its precondition rather
than by detecting it.** An earlier revision of the update contract carried the
portal identifier twice, once in the route and once in the body, and proposed to
refuse a disagreement under a failure code. The concern behind that proposal was
correct and is restated here, because it is the reason the contract has the shape
it now has.

**Legacy behaviour.** There was no second copy to disagree with. A Web Forms admin
page carried the identity of the record it was editing in its own state and posted
back to itself, so no request could name one portal in its address and a different
one in its body.

**Target behaviour.** An update addresses `/api/v1/portals/{portalId}` and the
route value is the *only* place the subject is named. `UpdatePortalRequest`
declares no `PortalId` member, `CreatePortalRequest` declares no identifier at all,
and `CreatePortalAliasRequest` and `UpdatePortalAliasRequest` each carry the host
name and nothing else. A disagreement between route and body is therefore not
refused at run time — it is unrepresentable.

**Why removal rather than a mismatch rule.** Two weaker policies were considered
and rejected before the third was chosen. Ignoring a payload value would let a
caller believe it had edited the portal it named while a different portal changed:
a silent wrong-record write, which is worse than an error. Deciding the question
after authorisation would authorise against one identifier and act on another,
which is the shape of a confused-deputy defect even when both identifiers belong
to the caller. A comparison rule ahead of authorisation avoids both, and it is
mechanically available — `Api/Filters/FluentValidationActionFilter.cs` publishes
every route value into the validation context's root data — but it protects only
the requests that reach it, and only for as long as every future entry point
remembers to apply it. Deleting the duplicate closes the class of defect by
construction instead, on every path, with nothing left to remember.

**One discard that would not have been this policy.** On insert, a payload that
carried a database-assigned surrogate key would not have been refused for doing
so: that key does not yet exist, and disregarding it would be correct rather than
lenient. The distinction is recorded so the two cases are not conflated by anyone
reinstating a body identifier.

**Operational consequence.** A client must not send a portal identifier in the
body of an update; the member does not exist, so a client that previously relied
on the body value winning has nothing to send. Requests are addressed by route
alone.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Dtos/Portal/UpdatePortalRequest.cs`,
`backend/src/DnnMigration.Application/Abstractions/IPortalService.cs`,
`backend/src/DnnMigration.Application/Validation/UpdatePortalRequestValidator.cs`.

### Portal administrator authorisation — the role is resolved per request, not once at start-up

**Legacy behaviour.** Portal administrator role membership, rather than any
permission key, is the dominant gate on the legacy administration screens, and
it was always evaluated against the portal the *current request* had resolved
to. The canonical site is `Website/admin/Security/SecurityRoles.ascx.vb`
L318-L324, whose `DataBind` override tests
`PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName)` at L322 and
redirects to the access-denied page at L323. Nine further occurrences of the
same test were measured across the screens in scope, in
`Website/admin/Modules/ModuleSettings.ascx.vb` (L191, L215, L333) and
`Website/admin/Tabs/ManageTabs.ascx.vb` (L170, L476, L555, L586, L601, L622).
The name it tested came off the per-request `PortalSettings` object, so it was a
per-tenant value by construction.

**Target behaviour.** The named policy exists in the policy catalogue, but the
administration role itself is deliberately **not** declared there and is not
configuration. It is read per request from the request-scoped portal context,
`IPortalContext`, which carries both `AdministratorRoleId` and
`AdministratorRoleName` precisely so this decision has a tenant-specific source.

**Why this is recorded as a divergence.** The mechanism changed shape, and the
obvious translation is wrong in a way that is easy to ship and hard to see. A
role-name requirement bound once when the service container is built is, by
definition, the same value for every tenant — while the fact it stands for
differs per tenant. A member of one tenant's administration role would then
satisfy the policy on a request that had resolved to a *different* tenant. That
is a tenant-isolation defect rather than a stylistic preference, and it is the
one outcome the legacy code could not produce, because it never had a
tenant-agnostic place to read the name from.

**A related dead end, recorded so it is not retried.** The value is also not
available from the bound portal options: those carry `AdminTemplateFileName`,
`HomeDirectoryFormat`, `UnauthenticatedRoleName` and `AllUsersRoleName`, and no
administration role of any kind. A registration written against that description
could not compile, so the compiler happens to catch this particular mistake —
whereas it cannot catch a fixed role name, which compiles perfectly and
mis-authorises quietly.

**Operational consequence.** The portal context must be populated before the
first authorisation decision of a request, which places the obligation on the
request pipeline rather than on start-up registration. Where the context has not
been settled, the policy fails closed and denies rather than assuming a tenant.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Authorization/PolicyNames.cs`,
`backend/src/DnnMigration.Api/Authorization/PermissionRequirement.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Services/IPortalContext.cs`,
`backend/src/DnnMigration.Infrastructure/Services/PortalContextAccessor.cs`.

## Data access and the legacy schema

### Two composite-key child tables declare an out-of-range value-generation sentinel

**Legacy behaviour.** Two of the identity columns this schema depends on are seeded
below one: `Modules.ModuleID` is `IDENTITY(0,1)` and `Portals.PortalID` is
`IDENTITY(-1,1)`. The first module of an installation therefore holds the
identifier `0`, and the first tenant holds `-1` while the second holds `0`. Those
are ordinary row identifiers to the legacy code, which never treated them as
anything else.

**Target behaviour.** The Fluent configurations for the composite-key child tables
whose key includes one of those columns declare an explicit sentinel outside the
possible value range, so that the identifier `0` is understood as an identifier.

**Why the difference is deliberate.** Entity Framework decides whether a key value
has been *set* by comparing it against the CLR default, which for `int` is `0`.
Without an explicit sentinel, a settings row belonging to module `0` and a
membership row belonging to tenant `0` would both be read as "key not yet
assigned". The observable consequence would be that the first module of an
installation could hold no settings and the second tenant could hold no members —
a defect that would appear only on the very first rows created, which is the
hardest place to notice it.

**Preserved unchanged.** No column, key, constraint or seed value is altered. This
is a mapping declaration only, and the schema remains exactly as the 88-script DDL
chain leaves it.

### `aspnet_Membership.Email` and `LoweredEmail` are left null when a credential is created

**Legacy behaviour.** The ASP.NET membership store holds its own copy of a user's
e-mail address alongside the credential, and the legacy provider populated it
because the membership API it wrapped accepted an address at creation time.

**Target behaviour.** Credential creation writes the credential and leaves the
membership store's `Email` and `LoweredEmail` columns null.

**Why the difference is deliberate.** The credential-creation contract in the
target takes no address, because the address is not a credential. `dbo.Users.Email`
is the authoritative address in this application and always was: it is the column
the administration screens read and write, the column the search filters match on,
and the column every migrated data path already uses. Writing a second copy into
the membership store would create two sources of truth for one value, with no
mechanism keeping them in step.

**Operational consequence.** Any external tool that reads the address out of
`aspnet_Membership` rather than out of `dbo.Users` will see null for accounts
created by the new API. Nothing in this solution reads it.

### The rolling failed-attempt window is preserved exactly as measured

**Legacy behaviour.** The lockout bookkeeping that DotNetNuke patched into
Microsoft's own membership procedures — `ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser`
at `Website/Providers/DataProviders/SqlDataProvider/04.00.00.SqlDataProvider:L31`
and `dbo.aspnet_Membership_UpdateUserInfo` at `:L119` — moves the start of the
attempt window forward on *every* failure rather than holding it fixed from the
first failure.

**Target behaviour.** Reproduced exactly: each failed attempt moves the window
start, so the window is rolling rather than fixed.

**Why the difference is deliberate.** It is not a difference; it is a decision *not*
to make one. A fixed window is the more common design and would have been the
natural thing to write, but it changes when an account locks and when it recovers.
The measured legacy behaviour is preserved and recorded here precisely so that a
later reader does not mistake it for a defect and "fix" it into a different
lockout policy.

## Authentication, tokens and sign-out

> The removal of reversible password storage and of password retrieval is
> documented in full under **Password storage, and the removal of password
> retrieval** above and is not repeated here.

### Refresh-token classification reports an already-used token ahead of a revoked one

**Legacy behaviour.** There was no refresh token. Sessions were a forms
authentication cookie, and there was nothing to classify.

**Target behaviour.** When a presented refresh token cannot be accepted, the
classification order reports *already used* before *revoked*.

**Why the difference is deliberate.** A single presentation cannot be both, but a
token that was legitimately rotated and is then presented a second time is
simultaneously used *and* revoked, because rotation revokes it. Reporting "already
used" first is what makes a replay visible as a replay rather than as an ordinary
expired-session event, which is the distinction the token contract exists to draw.

### A detected replay revokes the account's entire refresh-token set

**Legacy behaviour.** `FormsAuthentication.SignOut` cleared one cookie in one
browser. Nothing was invalidated anywhere else.

**Target behaviour.** When a refresh token is presented a second time, every
refresh token held for that account is revoked, not merely the replayed token's own
rotation family.

**Why the difference is deliberate.** A replay is evidence that a token reached
someone who should not hold it, and at that moment it is not knowable which of the
account's other tokens the same party also holds. Revoking the family alone would
leave a second stolen family working. The cost is borne by the legitimate holder,
who is signed out of their other sessions and must sign in again; the alternative
is leaving an active intruder with a working session, which is not a trade this
migration makes.

**Operational consequence.** Wider than the legacy per-cookie sign-out, and
deliberately so.

### Sign-out has no server-side effect on an already-issued access token

**Legacy behaviour.** `FormsAuthentication.SignOut` cleared the authentication
cookie immediately, so the very next request was unauthenticated.

**Target behaviour.** Sign-out revokes the refresh token and the client discards
its copy of the access token. The access token itself remains valid until it
expires.

**Why the difference is deliberate.** A bearer token cannot be recalled. It is
self-contained and verified by signature, so no server-side state exists to clear;
introducing one — a revocation list consulted on every request — would convert a
stateless design into a stateful one and give every request a lookup it does not
need. The mitigation is the token's lifetime, which is why the access-token
lifetime is deliberately short and the refresh token, which *is* revocable, carries
the long-lived part of the session.

**Operational consequence.** In the window between sign-out and access-token expiry
a holder of the already-issued token can still call the API. Shortening
`Jwt:ExpirationMinutes` shortens that window directly.

### The authentication-type and CAPTCHA arguments are gone from the sign-in call

**Legacy behaviour.** `Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L164`
calls `UserController.ValidateUser` passing, among other arguments, the literal
string `"DNN"` as the authentication type and a CAPTCHA verification value.

**Target behaviour.** The sign-in contract takes a username, a password and an
optional tenant. Neither argument survives.

**Why the difference is deliberate.** The authentication-type argument existed to
select between provider implementations; there is one JWT path in the target, so
the argument has exactly one possible value and carries no information. The CAPTCHA
argument was supplied by the legacy Captcha control, which the system boundaries
place out of scope, so there is nothing to supply it.

**Operational consequence.** Sign-in is not CAPTCHA-protected. What replaces it is
rate limiting on the authentication endpoints, which is applied per caller and is
exercised by the integration suite.

## Tenant resolution

### Portal aliases are matched exactly, replacing a substring match

**Legacy behaviour.** Tenant resolution ran through the `GetPortalSettings`
procedure created at
`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600`,
whose body resolves the tenant with
`select @PortalID = min(PortalID) from Portals where PortalAlias like '%' + @PortalAlias + '%'`.
Two properties of that statement matter: the match is a *substring* match, and ties
are broken by `min(PortalID)`.

**Target behaviour.** `PortalAliasResolutionMiddleware` resolves the request host by
exact alias match.

**Why the difference is deliberate.** A substring match means an alias that is a
substring of another tenant's alias resolves to whichever of them has the lower
identifier. That is a cross-tenant mis-resolution: a request for one tenant can be
served the settings, and therefore the data, of another. Carrying it into new code
would carry a data-exposure defect into new code, which the migration's
behaviour-preservation default explicitly does not require. This is the second place
after password storage where preservation is overridden on security grounds.

**Operational consequence.** An installation that has been relying — knowingly or
not — on a partial alias resolving to a tenant must configure that alias
explicitly. No alias that resolves exactly today changes behaviour.

### Portal-alias resolution is deliberately not cached

**Legacy behaviour.** The legacy code cached aggressively, including tenant
settings, and cleared caches by portal or by host.

**Target behaviour.** The host-to-tenant lookup is performed per request and is not
cached, even though the caching service exists and is used elsewhere.

**Why the difference is deliberate.** A host-keyed cache entry has no existing
invalidation path: aliases are edited through the alias endpoints, which have no
reason to know about a middleware cache, so a stale entry would survive an alias
being moved or removed. The failure mode of a stale host-to-tenant mapping is that a
request is served the wrong tenant's data. Correctness is preferred to a saved
lookup, and the omission is recorded here rather than left to look like an oversight.

### Tenant resolution is best effort, and a failure does not fail the request

**Legacy behaviour.** The page pipeline resolved the tenant before anything else and
had nowhere to go if it could not.

**Target behaviour.** A host that matches no configured alias, and a tenant lookup
that throws, both leave the request without a resolved tenant. The request
continues.

**Why the difference is deliberate.** This was found by running the host, not by
reading it: with the lookup failure propagating, *every* `/api` request became a 500
including requests that never needed a tenant at all. Endpoints that require a
tenant fail on their own terms with their own message, which is both more accurate
and more useful than a blanket server error, and the health endpoint continues to
report the state of the database rather than the state of the alias table. A failed
lookup is logged.

**Operational consequence.** A misconfigured alias produces a specific failure from
the endpoint that needed the tenant, not a total outage.

## Request pipeline

### HTTPS redirection is deliberately not enabled

**Legacy behaviour.** Transport security was configured at the web server.

**Target behaviour.** The host does not perform in-process HTTPS redirection.

**Why the difference is deliberate.** TLS terminates at the reverse proxy in the
delivered topology, so the API receives plain HTTP on its container port by design.
In-process redirection would answer the container's own health probe — which calls
`http://127.0.0.1:8080/health` — with a redirect to a port nothing listens on,
making the container permanently unhealthy and, through the compose dependency
condition, preventing the front end from ever starting. Enforcement belongs at the
proxy, which is where the certificate is.

### The forwarded-for header is deliberately not honoured

**Legacy behaviour.** Not applicable; there was no rate limiting to partition.

**Target behaviour.** Forwarded headers are processed for scheme and host, but the
authentication rate limiter partitions by the real socket address rather than by a
client-supplied forwarded-for value.

**Why the difference is deliberate.** A forwarded-for header is caller-controlled.
Partitioning a rate limit by it means an attacker changes one header per request and
the limit never applies to them, while a legitimate caller behind a shared proxy is
throttled on someone else's behalf. Partitioning by the socket address cannot be
spoofed by the caller.

## Authorisation

### A permission-gated route answers 403, not 404, for a resource the gate cannot resolve — including for a host account

**Legacy behaviour.** The administration pages checked permissions imperatively and
redirected to an access-denied page.

**Target behaviour.** When the authorisation handler cannot resolve the module or
page a route names, the request is refused with `403 Forbidden`. This holds for a
host account as well, which is the part worth stating.

**Why the difference is deliberate.** The same route reaches resources in other
tenants. Splitting the response into 404 for "does not exist" and 403 for "exists
but is refused" turns the endpoint into an existence oracle: a caller enumerates
identifiers and learns which ones are real in tenants they cannot read. One status
for both cases leaks nothing. Exempting the host account would reintroduce the
oracle for the one account that can enumerate the most, and would also mean the
authorisation handler behaved differently depending on who asked — a second code
path to keep correct for no gain.

**Operational consequence.** A genuinely absent resource is reported as refused
rather than as missing. A caller who legitimately holds the permission and names a
resource that exists is unaffected.

## Module lifecycle

### Module business controllers resolve from a closed, code-registered map

**Legacy behaviour.** `Modules.BusinessControllerClass` is a database column holding
an assembly-qualified type name, which the server loaded and constructed by name
through reflection — `Framework.Reflection.CreateObject(objModule.BusinessControllerClass)`
at `Library/Components/Modules/ModuleController.vb:L231` and `:L431`, and at
`Library/Components/Modules/EventMessageProcessor.vb:L32`, `:L52` and `:L77`.

**Target behaviour.** A factory resolves a business controller from a closed map
registered in code. There is no assembly probing and no construction of a type named
by data.

**Why the difference is deliberate.** A stored string that instructs the server to
load an assembly and construct a type is a remote-code-execution primitive for
anyone who can write that column. The capability is not carried forward. The
lifecycle behaviour it supported — the portable, searchable and upgradeable
contracts, and the install, configure and remove-per-portal sequence — is preserved
as an explicit set of interfaces resolved through dependency injection.

**Operational consequence.** An unrecognised stored controller name resolves to
nothing and the lifecycle operation completes successfully as a no-op, which is the
same observable outcome the legacy path produced when the named type could not be
loaded. A module that genuinely needs a business controller in the target is
registered in code.

### The searchable capability bit is still reported although no searchable operation exists

**Legacy behaviour.** `DesktopModules.SupportedFeatures` is a bitmask in which the
portable, searchable and upgradeable capabilities are recorded per module.

**Target behaviour.** The stored bitmask is read and reported unchanged, including
the searchable bit, even though the target exposes no searchable operation.

**Why the difference is deliberate.** The bit is stored data describing a module, not
a claim about this API's own capabilities. Suppressing it on read would rewrite the
meaning of a persisted value and would make a module's own declaration disagree with
what the database holds. Search itself is deferred by the system boundaries; when it
arrives, the declarations are already correct.

### Content upgrade takes one version per call

**Legacy behaviour.** The upgrade path read a comma-separated version attribute and
looped over every version in one operation.

**Target behaviour.** The upgrade operation takes a single version per call.

**Why the difference is deliberate.** A loop over several versions inside one
operation has no useful failure semantics: a failure part-way leaves the caller
unable to tell which versions were applied. One version per call makes each step
independently reportable and independently retryable, and the caller — which already
knows the version list — performs the iteration.

**Operational consequence.** Migrating content does not update the stored capability
bitmask, so a caller that needs the post-migration capability state re-reads it after
the last version.

## Application-layer behaviour

### A page write does not store the submitted ordinal

**Legacy behaviour.** A page's position among its siblings is held in `Tabs.TabOrder`,
and the administration screens posted an ordinal.

**Target behaviour.** A page write uses the submitted ordinal to *place* the page among
its siblings and then renumbers the whole tenant's tree from the desktop order seed of
`-1` in steps of two, recomputing every depth and every path. The submitted value itself
is not stored verbatim.

**Why the difference is deliberate.** The ordinal is only meaningful relative to the other
pages at the same level. Storing the submitted number would look correct on the page that
was edited and would corrupt the ordering across the siblings that were not, because
nothing would have moved to make room for it. Renumbering from a seed in steps of two is
the legacy scheme's own arrangement — the gap between steps is what allows an insertion —
and recomputing depth and path in the same operation is what keeps the hierarchy
self-consistent.

**Operational consequence.** A caller that reads a page back immediately after writing it
may see a different ordinal from the one it submitted. The page's *position* is the one
that was asked for.

### The host-only-field guard compares the value that will be written, not the value that was submitted

**Legacy behaviour.** The site-settings screen never rendered the host-only fields — the
hosting fee, the disk quota and the page and user quotas — to a non-host account, so a
legacy post from a tenant administrator never carried them and never zeroed them either.

**Target behaviour.** The tenant-update guard compares the *effective* value, meaning the
value the mapper will actually write, against the value currently stored, and refuses the
write when a non-host caller would change one of those fields.

**Why the difference is deliberate.** This is the subtle case, and getting it wrong is a
privilege hole rather than a cosmetic defect. The update request is a whole-row
replacement and the columns concerned cannot hold null, so an omitted numeric term is not
"leave it alone" — the mapper substitutes zero. A guard written the obvious way, testing
whether the request *carried* a value, would therefore have admitted precisely the change
it exists to refuse: a tenant administrator could have waived the hosting charge and
lifted every quota simply by leaving those fields out of the request. Comparing the
effective value restores the legacy outcome, in which those columns could not be changed
from that screen at all.

### Role creation and role update validate differently, on purpose

**Legacy behaviour.** Both operations were reached from screens carrying their own
validators.

**Target behaviour.** The role *creation* member performs no shape checking of its own,
while the role *update* member performs a full set: name required and at most 50
characters, description at most 1000, RSVP code at most 50, icon at most 100, fees not
negative, periods greater than zero, and frequencies drawn from the defined set.

**Why the difference is deliberate.** It compensates exactly where the request pipeline
does not. The creation route has a registered request validator; the update route has
none, by the same design decision that leaves several other read-oriented and
lookup-oriented routes unvalidated. Duplicating the creation rules in the service would
give one user-visible message two sources of truth, and omitting them from the update path
would leave it unchecked.

**Operational consequence.** One asymmetry is observable and is stated so it cannot
surprise: a negative fee is *clamped to zero* on create, because that is what the legacy
screen's own guard did, and *refused* on update, because the service raises a domain
failure rather than silently rewriting a value the caller stated.

### Module alignment, colour and border are carried as opaque persisted strings

**Legacy behaviour.** `Modules.Alignment` (`nvarchar(10)`), `Color` (`nvarchar(20)`) and
`Border` (`nvarchar(1)`) held values that the legacy renderer interpolated into the markup
it produced. The legacy screen validated the border: it accepted null, or a single ASCII
digit, and otherwise reported *Invalid Border (must be a number between 0 and 9)*.

**Target behaviour.** All three are stored and returned verbatim as the strings they are,
and the border validation is reproduced exactly. None of the three is ever interpolated
into a style declaration by this application.

**Why the difference is deliberate.** The API's job is to preserve the values a legacy
renderer consumed, not to become that renderer. Interpolating a stored string into CSS
would make the column an injection vector, and re-interpreting the values into a modern
styling vocabulary would silently change how existing modules present.

**Operational consequence.** The module *container* selector, `Modules.ContainerSrc`, is
not surfaced for editing, because containers are skin objects and skinning is out of
scope. The stored value is preserved untouched on every update rather than being cleared,
so a later skinning implementation finds it intact.

## Validation and error reporting

### The tenant-creation validator deliberately does not require a tenant name, and now shape-checks the administrator e-mail address

**Legacy behaviour.** The sign-up screen at `Website/admin/Portal/Signup.ascx` required
neither a tenant name nor a well-formed administrator e-mail address: `signup.ascx:L106-L107`
declares `txtEmail` with a `maxlength` attribute and one required-field validator, and no
expression validator anywhere on the screen. The account-creation screen, by contrast, did
check the shape of an address.

**Target behaviour, and the two halves are opposite.** The tenant name is still not
required, reproducing the legacy screen exactly. The administrator e-mail address, however,
is now checked for shape as well as presence, which the legacy screen did not do.

**Why the tenant name stays unrequired.** It is a decision *not* to diverge. Requiring it
would refuse a provisioning request the legacy site accepted, and an existing tenant may
already carry a blank name that a later save has to be able to round-trip.

**Why the e-mail address is now checked.** Two measured reasons, and both point the same
way. The address this path stores is a real account's contact address, and it belongs to the
sole administrator of a tenant that has no other account able to administer it - so a
malformed address means no password-recovery message can ever reach the only account that
could repair the situation. And the same field submitted to the account-creation endpoint
*is* checked, so leaving this one unchecked would give a single column two different notions
of a valid address; the review findings on e-mail width and match semantics required exactly
that kind of divergence be eliminated rather than preserved. This is the same shape of
decision as the substring alias match recorded elsewhere in this document: the legacy
omission carried a real harm forward, so it is corrected and the correction is written down.

**What the check does and does not refuse.** It delegates to
`Domain/ValueObjects/EmailAddress.cs`, the single authority, rather than restating a
pattern, so this path inherits the same 256-character bound and the same modern top-level
domain loosening as every other path. A blank value satisfies the shape rule, so the
required-field rule alone reports an omission; the only inputs newly refused are non-blank
malformed ones.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/CreatePortalRequestValidator.cs`.

### Migrated validation and help wording preserves the legacy resource strings character for character, including two spaces after a sentence period

**Legacy behaviour.** The shared and per-screen resource files —
`Website/App_GlobalResources/SharedResources.resx` and the 37
`Website/admin/**/App_LocalResources/*.resx` files — carry wording that uses two spaces
after a sentence period, and carry a small number of other irregularities.

**Target behaviour.** The wording is reproduced character for character. The one message
that has no legacy counterpart, a display-name length bound, follows the same convention
deliberately so that the set reads consistently.

**Why the difference is deliberate.** An operator recognises these strings. Normalising the
spacing would change text a person has read for years for no functional gain, and would
make a diff against the legacy resources unreadable.

**Preserved deliberately, and not defects to fix.** The misspelling in the banners label
(*hostingprovider*), and the missing trailing periods on the user-registration and
home-directory help strings, are carried across as they are.

**Annotated in code at.** Every migrated string is bound from a component constant rather
than written as template text, for the reason recorded under **Migrated wording is bound
through component constants** below.

### A contradictory search filter is reported as a request to correct, not as a conflict

**Legacy behaviour.** The user-listing screens combined filters and produced an empty grid.

**Target behaviour.** A malformed or self-contradictory search filter is reported with the
failure code `user.list.filter-invalid`, which the API translates to `400 Bad Request`.

**Why the difference is deliberate.** The obvious alternative name for this condition
carries the word *conflict*, and a failure code containing that word is translated to `409`
by the status mapping. `409` means the caller's request conflicts with the current state of
the resource — a concurrent edit — which is a different situation with a different remedy.
Naming the code for what it is keeps a query the caller should correct distinguishable from
a write the caller should retry.

### The advertised problem-details media type is `application/json`, not `application/problem+json`

**Legacy behaviour.** Not applicable. There was no centralised error contract; failures
were reported per call site.

**Target behaviour.** Every problem document the API produces is a correct RFC 7807
document carrying `type`, `title`, `status` and either `detail` or a per-field `errors`
object, plus the framework-native `traceId`. The *advertised* content type is
`application/json; charset=utf-8` rather than RFC 7807 section 3's
`application/problem+json`.

**How this was established.** By measurement at runtime, not by reading. Three distinct
problem-producing paths were probed against a running host and all three returned
`application/json; charset=utf-8`: a validator-driven `400`, a model-binding `400`, and a
domain-failure `401` funnelled through the result-to-status translator. A response-start
probe showed the framework's own pipeline choosing the media type.

**Why it is documented rather than corrected.** The mandated error envelope is a contract
on the response *body*, and that contract is satisfied. The framework's problem-details
write path bypasses the content-type negotiation an ordinary object result would perform,
so correcting the header would mean overriding a negotiated response header from a
response-start callback — a speculative change to the host's error path, for a deviation
that traces to none of the review findings, that blocks no build, test or gate, and that
nothing in this solution or in the Angular client reads. A previous attempt at exactly that
change was written and then deliberately reverted; the two tests it produced were kept,
rewritten to assert the envelope rather than the media type, because a test that pins the
value a framework currently emits cements the deviation and makes correcting it later look
like a regression.

**Superseded claim.** Two earlier doc comments in the API project, and the earlier entry in
this file describing the global exception handler, state that the framework guarantees or
selects `application/problem+json`. Measurement supersedes that claim. The two comments have
been corrected in place; the earlier entry in this file is left as written because this file
is append-only, and this entry is its correction.

## Front end

### The profile screen is one component in two modes

**Legacy behaviour.** `Website/admin/Users/ViewProfile.ascx` is five lines whose entire
content is the same profile editor control that the edit screen uses, configured with
`EditorMode="View"` and `ShowUpdate="False"`.

**Target behaviour.** One component with an `edit` mode and a `view` mode.

**Why the difference is deliberate.** It is not a difference in behaviour; it is the same
arrangement the legacy markup already expressed, stated once instead of through a
five-line wrapper. Two components would have meant two places to change one field set.

**One accessibility repair.** The legacy control's collapse affordance carried
`tabIndex="-1"`, removing it from the tab order, so the sections could not be collapsed
from the keyboard. It is promoted to a real `button`, which restores keyboard operability
at no visual cost.

### The profile-property catalogue replaces a pair of legacy pages

**Legacy behaviour.** Two screens: `Website/admin/Users/ProfileDefinitions.ascx`, a grid
with four per-row affordances, and `Website/admin/Users/EditProfileDefinition.ascx`, a
separately navigated editor page.

**Target behaviour.** One screen whose editor collapses inline beneath the list.

**Why the difference is deliberate.** Navigating away to amend one declaration lost the
operator's place in the list and their scroll position, and returning re-fetched the whole
catalogue. Everything the two screens did is still reachable.

**Operational consequence.** Disclosure state survives a re-seed of the list, so amending
a declaration does not collapse the editor the operator is working in.

### No legacy raster asset is carried across

**Legacy behaviour.** The administration screens used small images as their action
affordances — `save.gif`, `help.gif`, `delete.gif` and others — several of them with no
alternative text.

**Target behaviour.** The only static asset shipped is the favicon. Those affordances are
text, and the one place a marker is genuinely needed — the profile section's disclosure
indicator — uses an inline vector rather than an image.

**Why the difference is deliberate.** An image affordance with no alternative text is
invisible to assistive technology, and a text affordance needs no asset pipeline, no cache
policy and no additional request. The visual change is deliberate and is confined to the
affordances themselves.

### Migrated wording is bound through component constants, never written as template text

**Legacy behaviour.** The resource strings carry meaningful whitespace, including two
spaces after a sentence period in the site-settings and keywords help text.

**Target behaviour.** Every migrated string is declared as a component constant and
interpolated into the template.

**Why the difference is deliberate.** This one is a trap rather than a preference. The
Angular template compiler runs with whitespace preservation disabled, which collapses every
run of whitespace inside a text node — so a legacy string written directly into a template
is silently rewritten, and the character-for-character preservation promised above would
quietly fail. Interpolated values are not collapsed. Binding the string is what makes the
promise true.

### A select always offers the value currently held

**Legacy behaviour.** The settings screens selected a stored value in a drop-down by
calling `FindByValue`, and selected *nothing* when the stored value was absent from the
list. The next post then wrote the list's first entry over the stored value.

**Target behaviour.** A select always includes the currently-held value as an option, even
when the supplied lookup list has forgotten it.

**Why the difference is deliberate.** The legacy behaviour meant that opening a settings
screen and pressing Update could silently change a field the operator never touched — for
example after a locale or a skin was removed. Preserving that would preserve silent data
loss.

### A native date control replaces the text box and calendar pop-up

**Legacy behaviour.** The start and end date fields paired a text box with a link that
opened a calendar pop-up.

**Target behaviour.** A native date input.

**Why the difference is deliberate.** The native control provides the same affordance —
including a picker — with keyboard support, locale awareness and no component to maintain,
so no calendar component is introduced.

**Implementation note.** The stored value is an instant; it is narrowed to a date by
slicing the ISO text rather than by parsing it into a date object, because parsing and
re-formatting would apply the browser's time zone and could move the value by a day.

### The inherit-permissions label is plain text, losing its embedded bold markup

**Legacy behaviour.** The label read *Inherit &lt;b&gt;View&lt;/b&gt; permissions from
&lt;b&gt;Page&lt;/b&gt;*, with the emphasis embedded in the resource string as markup.

**Target behaviour.** The same words, rendered as plain text.

**Why the difference is deliberate.** Migrated wording is bound rather than written into
the template, and a bound value is not parsed as HTML — which is precisely what stops any
of these strings being usable as an injection vector. Losing two spans of bold is the price
of that guarantee, and it is paid knowingly.

### The module "Move To Page" affordance and the container selector are not carried across

**Legacy behaviour.** The module settings screen offered a page-move affordance and a
container selector.

**Target behaviour.** Neither is present.

**Why the difference is deliberate.** The page-move affordance has no endpoint in the
agreed API inventory, and adding one would be a new capability rather than a migrated one.
The container selector addresses a skin object, and skinning is out of scope by the system
boundaries.

**Operational consequence.** The stored container value is preserved untouched on every
update rather than being cleared, so nothing is lost by the affordance's absence.

## Deployment

### The delivered API container cannot open a database connection as built

**What was found.** Running the composed topology end to end, the API container answers
`503` on `/health` for its entire life, and because the front-end service depends on the
API being healthy, the front end never starts. The cause is not the connection string, the
network or the database: `Microsoft.Data.SqlClient` throws
`System.NotSupportedException: Globalization Invariant Mode is not supported` inside
`SqlConnection.TryOpen`, **before any socket is opened**.

**Why it happens.** The mandated runtime base image
`mcr.microsoft.com/dotnet/aspnet:8.0-alpine` sets
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, because Alpine's musl-based userland ships no
ICU. The 5.x client library deliberately refuses to open a connection in that mode rather
than risk locale-dependent behaviour.

**The remedy, which is two lines.** In the runtime stage of `docker/api.Dockerfile`:
`RUN apk add --no-cache icu-libs icu-data-full` and
`ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`. This was proved by building a throwaway
image with exactly those two lines, outside the repository so that no delivered container
artefact was modified; with them the container becomes healthy and every end-to-end
assertion passes.

**Why the remedy is not applied.** The four container artefacts are frozen verbatim by the
migration plan's preserved-example directive, whose only sanctioned substitution is the two
name placeholders. Three in-scope alternatives were each tried and rejected on measured
grounds: disabling invariant globalisation in the project file converts the `503` into a
boot crash, because the runtime then demands an ICU library the image does not contain;
shipping an application-local ICU package adds a dependency outside the frozen dependency
inventory; and abandoning `Microsoft.Data.SqlClient` contradicts the migration's own
data-access goal.

**What an operator must do.** Apply the two lines above to the runtime stage before
deploying, or run the API on a glibc-based runtime image. Everything else in the topology
is correct: both images build, the entry point and the copied output path are both proven
correct against real builds, the proxy configuration resolves, and the health endpoint
itself is anonymous and reachable.

## Test environment

### The integration database is provisioned from explicit DDL scripts, never from the model

**Legacy behaviour.** Not applicable; the legacy tree contains no automated tests of any
kind.

**Target behaviour.** The integration fixture creates a uniquely named database per run on a
real SQL Server instance and provisions it from explicit DDL scripts, then drops it. The
model is never used to create schema.

**Why the difference is deliberate.** Three independent reasons, each sufficient on its own.
The DotNetNuke schema is externally owned and must not be generated from the target model,
which is the same rule that makes the baseline migration intentionally empty. The
`aspnet_*` membership objects are not mapped entity types at all — they are reached through
explicit parameterised statements — so no model-generated schema could ever contain them,
and the credential paths would be untestable. And `dbo.HostSettings` is likewise reached
through explicit statements rather than through an entity.

**Why a relational engine rather than an in-memory provider.** Two delivered code paths rule
the in-memory provider out on their own: a set-based delete used when removing a user's
permissions, and a raw-SQL query behind the approved-users projection. Neither is expressible
against a non-relational test double, and substituting one would mean the suite proved
something other than what ships.

### The sequentially numbered enumerations travel as integers, not as member names

**Artefacts:** `backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`,
`frontend/src/app/core/models/portal.model.ts`, `frontend/src/app/core/models/module.model.ts`

**What travels.** Only two enumerations carry a textual wire form, and both are covered above:
`BillingFrequency` travels as its legacy `char(1)` code and `PermissionKey` as its stored word.
Every other enumeration on the wire — `UserRegistrationMode`, `BannerAdvertisingMode` and
`ModuleVisibility` — travels as the integer discriminator its column stores. No blanket
string-enumeration converter is registered on either serialisation surface: not on the controller
formatters, and not on the minimal-API options object the problem-details writer uses.

**Why integers for these three.** The Angular models are the consumer, and they pin the numbers
explicitly. `USER_REGISTRATION_MODE` is declared `{none: 0, private: 1, public: 2, verified: 3}`,
`BANNER_ADVERTISING_MODE` is `{none: 0, site: 1, host: 2}` and `MODULE_VISIBILITY` is
`{maximized: 0, minimized: 1, none: 2}`, each as a numeric literal union. A server that emitted
`"Maximized"` to a client whose type admits only `0`, `1` or `2` would be sending a value the
contract does not contain. The numbers are also the values the columns hold, so the wire form and
the stored form agree, and the named members exist in the domain model where the meaning belongs.

**Why no blanket converter, beyond the contract.** A general converter could only stay correct by
being registered *after* the two specific ones, because a converter is chosen by walking the
registered collection and taking the first entry that reports it can handle the type. Correctness
that depends on the order of two adjacent registrations is a hazard rather than a design:
reordering them would silently change the published wire form of `BillingFrequency` from `"M"` to
`"Month"` while every typed round-trip in the test suite continued to pass, because the client and
the server would still agree with each other. Registering one explicit converter per type removes
the dependency entirely and makes each enumeration's wire form a decision recorded here.

**How this is held.** Two integration tests assert the RAW response text rather than a
deserialised object, since a typed round-trip cannot observe this class of defect.
`RoleApiTests.GetRole_SpellsTheBillingFrequencyWithItsLegacyCode` requires
`"billingFrequency":"M"` and forbids `"Month"`;
`PortalApiTests.NumericEnumerations_TravelAsTheirStoredIntegers` requires `userRegistration` and
`bannerAdvertising` to be numeric and forbids the member spellings a general converter would
produce.

### `backend/src/DnnMigration.Application/Dtos/Role/CreateRoleRequest.cs` — the role-creation request contract

**Legacy source:** `Website/admin/Security/editroles.ascx` (note the lower-case file name; its
code-behind `EditRoles.ascx.vb` is capitalised) declares fourteen input controls; the save path at
`EditRoles.ascx.vb` lines 232-248 assigns thirteen of their values onto a new `RoleInfo` and hands
it to `RoleController.AddRole` (`Library/Components/Security/Roles/RoleController.vb` line 100).
Legacy CLR types come from `Library/Components/Security/Roles/RoleInfo.vb` (backing fields at lines
43-57), the sentinel contract from `Library/Components/Shared/Null.vb`, and the terminal column
types from the upgrade chain under
`Website/Providers/DataProviders/SqlDataProvider/`.

**Target:** `public sealed class CreateRoleRequest` with exactly thirteen properties, posted to
`POST /api/v1/portals/{portalId}/roles`.

**Thirteen properties, derived from the screen rather than the entity.** `RoleName`,
`Description`, `ServiceFee`, `BillingPeriod`, `BillingFrequency`, `TrialFee`, `TrialPeriod`,
`TrialFrequency`, `IsPublic`, `AutoAssignment`, `RoleGroupId`, `RsvpCode` and `IconFile`. Four
things a reader may expect are deliberately absent:

- **`RoleID`** — an identity column the store assigns, and one the legacy creating member *returns*
  (`RoleController.vb` line 100 declares `As Integer` and yields the new value at line 107). The
  legacy screen carried it only because one postback served both creating and editing, overloading
  `-1` as an add-versus-edit switch (`EditRoles.ascx.vb` lines 131 and 251). Creating and editing
  are now distinct routed endpoints, so accepting a caller-supplied identifier would be an
  identity-injection vector with no legacy precedent.
- **`PortalID`** — ambient. `EditRoles.ascx.vb` line 234 takes it from page state and no control
  posts it; the target resolves it from the route and the scoped portal context. Accepting it in the
  body would give the tenant a second, contradictable source of truth.
- **`RSVPLink`** — display-only, and it has no column in any of the eighty-eight upgrade scripts.
  `editroles.ascx` lines 158 and 161 declare a read-only companion box that
  `EditRoles.ascx.vb` lines 165-167 *compute* from the request's domain name, the default page and
  the code; the save path at line 247 persists only `RSVPCode`. Composing it needs `System.Web`-style
  ambient request state the Application layer must not see, so the client composes the URL from
  `RsvpCode` and its own origin.
- **Assignment members** — `RoleStatus`, `ExpiryDate`, `EffectiveDate`, `IsTrialUsed`, `Subscribed`,
  `UserRoleID` and `UserID` describe a *user's membership of* a role, are columns of
  `dbo.UserRoles`, and belong to `RoleAssignmentRequest`.

**`ServiceFee` and `TrialFee`: legacy `Single` becomes `decimal?`, on the terminal `money` type.**
`RoleInfo.vb` declares both `As Single` (lines 164 and 233) and the save path parses both with
`Single.Parse` (`EditRoles.ascx.vb` lines 217 and 227). The baseline column was
`[ServiceFee] [decimal](5, 2) NULL` (`01.00.00.SqlDataProvider` line 119), which would cap a fee at
999.99. Both are superseded: `01.00.04.SqlDataProvider` line 1326 recreates the table with
`ServiceFee money NULL`, converting existing values at line 1341; `01.00.05.SqlDataProvider` line
2752 repeats it; and `03.01.01.SqlDataProvider` line 1173 settles it terminally with
`ALTER COLUMN [ServiceFee] [money] NULL`, defaulted to zero at line 1177. `TrialFee` was `money`
from birth (`01.00.08.SqlDataProvider` line 6830). SQL `money` is fixed-point, so `decimal` is its
faithful counterpart and a `float` or `double` would reintroduce binary rounding into a currency
amount. The user interface agrees independently: both fees are compared as `Type="Currency"` and
rendered to two decimals (`EditRoles.ascx.vb` line 147).

**`BillingPeriod` and `TrialPeriod`: `int?`, on four-way evidence.** The legacy properties are
`As Integer` (`RoleInfo.vb` lines 218 and 203) and the save path uses `Integer.Parse`
(`EditRoles.ascx.vb` lines 218 and 228); the columns are `int NULL`
(`01.00.08.SqlDataProvider` line 6829 and `01.00.05.SqlDataProvider` line 2754, backfilled to one at
lines 6900 and 6915); the screen validates both as `Type="Integer"`; and decisively the terminal
projection itself emits SQL `NULL` —
`'BillingPeriod' = case when convert(int,Roles.ServiceFee) <> 0 then Roles.BillingPeriod else null
end` (`01.00.08.SqlDataProvider` lines 7024 and 7054, templated identically at
`02.00.00.SqlDataProvider` line 2220). Only the legacy provider signature spelled these values as
text, and it is the outlier. So `null` is the *literal* legacy value for a free role, not a
modernisation, and it is not interchangeable with zero: the legacy expiry derivation short-circuits
to no expiry when the period equals the integer null sentinel of `-1` (`RoleController.vb` line 537
against `Null.vb` lines 41-43).

**Both frequency columns share one six-member enumeration, and the codes are never renamed.** The
legacy properties were `String` (`RoleInfo.vb` lines 149 and 188) over `char(1) NULL`
(`01.00.05.SqlDataProvider` lines 2753 and 2755, never retyped afterwards). Both map to the single
`DnnMigration.Domain.Enums.BillingFrequency`; no second enumeration and no local copy exists. The
legacy queries prove the sharing by joining one lookup twice from a single role row
(`01.00.04.SqlDataProvider` lines 1524-1525, and still twice against the generic `Lists` table after
`03.00.01.SqlDataProvider` line 1300 drops that lookup).

*Reported honestly: there are six codes, not four.* Earlier planning named only `D`, `W`, `M` and
`Y`. The measured switch has six — `RoleController.vb` lines 540-546 handles `N`, `O`, `D`, `W`, `M`
and `Y` — and two further sources agree: `01.00.08.SqlDataProvider` lines 6842-6889 rewrites the
lookup table's six numeric codes `'0'`-`'5'` onto exactly those six letters, and `RoleInfo.vb`
lines 136-146 documents all six. `Domain/Enums/BillingFrequency.cs` was read before this contract
was authored and declares all six (`None='N'`, `OneTime='O'`, `Day='D'`, `Week='W'`, `Month='M'`,
`Year='Y'`), so there is no gap to record and no divergent local enumeration was invented.

*Also note:* the terminal schema carries **no** foreign key and **no** check constraint on either
frequency column — `03.00.01.SqlDataProvider` line 1297 drops `FK_Roles_CodeFrequency` and line 1300
drops the lookup table — so confining the two members to the six codes is the application's
responsibility, discharged by `CreateRoleRequestValidator`.

**`TrialFrequency` carries no initialiser, and the legacy default is documented rather than
applied.** The legacy screen pre-selected `"N"` in *both* frequency drop-downs
(`EditRoles.ascx.vb` lines 121 and 125) and its save path substituted `"N"`, a zero fee and a period
of one whenever the corresponding block was left empty (lines 212-214 and 222-224), so a legacy
create always wrote the character and never a SQL `NULL`. The contract leaves the member `null`
instead, for three reasons: it keeps the member symmetrical with `BillingFrequency`; an initialiser
would be a policy decision inside an inert carrier and would make an *omitted* member behave
differently from an explicitly null one; and it costs nothing observable, because `null` and
`BillingFrequency.None` are already treated identically downstream — the Application service lets
the trial terms govern an expiry only when the member holds a code that is not `None`, and in SQL the
projections' `TrialFrequency <> 'N'` test is UNKNOWN for a `NULL` and falls to the same `else`
branch. `None` remains a *real stored value* meaning "no trial" and must never be conflated with
absence: `RoleController.vb` line 521 tests against it to choose between the trial and billing
terms, and `01.00.08.SqlDataProvider` lines 7026-7028 gate the whole trial triple behind the same
test.

**`RoleGroupId` is `int?` where `null` means "Global Roles".** This is the subtlest decision in the
folder, and it reconciles two facts that only look contradictory. The interface treated `-1` as a
real selectable choice: the group-binding helper adds it as the drop-down's first entry, labelled
from the localised `GlobalRoles` resource, and `EditRoles.ascx.vb` line 236 parses it straight onto
the legacy property. Yet `-1` can never reach the column, because `RoleGroups.RoleGroupID` is itself
`IDENTITY(0,1) NOT NULL` (`03.02.03.SqlDataProvider` line 18, repeated at
`04.00.04.SqlDataProvider` line 51) and `Roles.RoleGroupID int NULL` carries a foreign key to it
(lines 34 and 37, repeated at lines 67 and 70) that would reject the value. The two facts are one
fact seen from two layers: the legacy reader turned SQL `NULL` into `-1` via `Null.SetNull`, and the
provider turned `-1` back into `NULL`. So `-1`, "Global Roles" and SQL `NULL` are a single value.
Three consequences, each a real defect if ignored:

- **Zero is a legitimate group.** `RoleGroups.RoleGroupID` is seeded `IDENTITY(0,1)`, so the first
  group a portal creates bears identifier `0`. Never test for absence with `<= 0` or `== 0`; use
  `HasValue`.
- **`-2` must never appear on this contract.** `Roles.ascx.vb` line 112 adds
  `New ListItem(Localization.GetString("AllRoles"), "-2")` to the *listing* screen's group filter.
  It is transient query state meaning "do not filter", it is never stored, and it belongs to the
  listing query contract.
- **Do not conflate this with the permission pseudo-principals.** In `ModulePermissions` and
  `TabPermissions` a `RoleID` of `-1` means All Users, `-2` Superuser and `-3` Unauthenticated
  Users; there those negatives are real principals and must **never** be mapped to `null`. That rule
  is separate, about a different column. Both rules hold; merging them is a bug.

Normalising a submitted `-1` into `null`, should a legacy client send one, belongs to `RoleService`,
which also reports a group absent from the owning portal as `role_group.not_found`.

**Strings: the legacy "absent" value was `""`, not `null`.** `Description`
(`nvarchar(1000) NULL`), `RsvpCode` (`nvarchar(50) NULL`) and `IconFile` (`nvarchar(100) NULL`, both
added by `03.02.03.SqlDataProvider` line 45 and `04.00.04.SqlDataProvider` line 80) are `string?`.
`Null.vb` lines 71-73 returns `""` from its string sentinel, and `EditRoles.ascx.vb` line 166 tests
the invitation code with `<> ""`, so a legacy row could not distinguish "no value" from "empty
value". This contract can, and no serialisation attribute forces either reading:
`Application/Mapping/RoleMappings.cs` owns the `""`-versus-`null` decision and currently passes
either through unchanged. `RoleName` is `nvarchar(50) NOT NULL` (`01.00.00.SqlDataProvider` line
117), so it is a non-nullable `string` initialised to `string.Empty`.

**`IsPublic` and `AutoAssignment` are `bool`, not `bool?`.** Both columns are `bit NOT NULL` with a
zero default (`01.00.08.SqlDataProvider` lines 6831-6832, retyped and re-defaulted under the
qualifier-templated form at `03.01.01.SqlDataProvider` lines 1174-1175 and 1179-1181), the legacy
properties are `As Boolean` (`RoleInfo.vb` lines 248 and 263), and a checkbox always posts a definite
state. The CLR default of `false` agrees with the store default, which is what makes the
non-nullable member safe. `AutoAssignment` is the one member with a side effect: setting it makes the
service enrol the portal's existing members in the same operation, reproducing the auto-assign helper
the legacy invoked immediately after a successful insert (`RoleController.vb` line 106).

**Other divergences.** Every legacy XML serialisation attribute is dropped — `RoleInfo.vb` decorated
its class and twelve of its fifteen properties for the portal-template export, a mechanism this
migration does not carry — so member names alone express the wire contract. The contract declares no
base type and inherits from nothing, even though `UpdateRoleRequest` overlaps it almost entirely:
the legacy tree shows the cost of the alternative, where `UserRoleInfo` inherits `RoleInfo` and an
eight-column assignment presents twenty-three effective properties. And the legacy conditional-fee
gating (`EditRoles.ascx.vb` line 216 for the billing block, line 226 for the trial block) is
reproduced by the validator and the service, never by the contract, which stays inert.

**Two legacy validator defects, annotated and NOT fixed.** `editroles.ascx` declares nine
validators — one `RequiredFieldValidator` and eight `CompareValidator`s — and two of the eight
carry a message that contradicts their operator. `valBillingPeriod2` (markup lines 112-114) has
`Operator="GreaterThan" ValueToCompare="0"` but reads "Billing Period Must Be Greater Than or Equal
to Zero"; `valTrialFee2` (lines 126-128) has `Operator="GreaterThanEqual"` but reads "Trial Fee Must
Be Greater Than Zero". Per the minimal-change discipline a discovered defect is annotated, not
repaired: the enforced rule is the *operator*, and `CreateRoleRequestValidator` reproduces the
operators — fees at `>= 0`, periods at `> 0`. Nothing about the mismatch is implemented in the
contract itself.

## Role membership assignment contract

### `RoleAssignmentRequest` carries the four measured inputs, and not the legacy read shape

**What the legacy screen actually posted.** `Website/admin/Security/SecurityRoles.ascx.vb` lines 528
to 542 parse the two date textboxes - substituting `Null.NullDate` whenever a box is blank - and then
call `RoleController.AddUserRole(User, Role, PortalSettings, datEffectiveDate, datExpiryDate, UserId,
chkNotify.Checked)`. That seven-argument member is declared at
`Library/Components/Security/Roles/RoleController.vb` line 647. Three of its arguments are not caller
input: the role arrives in the route, the ambient `PortalSettings` composite is replaced by the scoped
tenant context, and `userId` is the *assigning administrator* recorded for audit at line 656 - its own
parameter documentation at line 641 says so - which the migration reads from the authenticated
principal. Four genuine inputs remain, and the contract declares exactly those four: `UserId`,
`EffectiveDate`, `ExpiryDate` and `NotifyUser`.

**`UserRoleInfo` is deliberately not projected.** It is the obvious-looking source and the wrong one.
`Library/Components/Users/UserRoleInfo.vb` lines 42 and 43 declare `Public Class UserRoleInfo` /
`Inherits RoleInfo`, so its surface is its own eight properties plus the fifteen it inherits -
twenty-three in all. Projecting them would restate every role-definition column `CreateRoleRequest`
and `UpdateRoleRequest` already own and would let a caller edit a role while merely adding a member to
it. It would also carry members that are not input in any sense: `FullName` and `Email` are grid
display denormalisations, `UserRoleID` is an identity column, and `IsTrialUsed` and `Subscribed` are
facts the service records. `UserRoleInfo` is what the screen *read*; a request is a command.

**The free-text username path is now a client-side lookup.** The legacy screen offered two routes to
one user - a textbox plus a validate button resolving a name through `UserController.GetUserByName`
(lines 104 to 107, and again at 477 to 484), and a query-string identifier (lines 417 and 418) - which
converged on a resolved `UserInfo` before anything was saved. The contract accepts only the resolved
integer. Name resolution is a repository read and stays in the service; the client resolves a name
through the user lookup endpoint and posts the identifier it gets back. No dual-purpose
"identifier or username" string is offered, because a single field carrying two meanings is the
untyped contract this migration exists to remove.

**`NotifyUser` is preserved as an instruction, not as a promise.** `chkNotify.Checked` is a measured
caller input: it is the seventh argument at line 542, is passed again on both removal calls at lines
569 and 574, is a declared parameter `notifyUser` at line 647 documented at line 642, and is consumed
at lines 659 and 660. The legacy markup even pre-selects it - `securityroles.ascx` line 49 declares
the checkbox `Checked="True"` - so notifying was the legacy default rather than an edge case. Outbound
mail is nevertheless out of scope, so the application layer has no notifier to delegate to and **does
not act on this flag**, and a successful response must never be read as evidence that a notification
was sent. The two states are deliberately kept apart rather than collapsed: declaring the member keeps
the legacy affordance expressible and makes supplying a notifier later a purely additive change, while
dropping it would delete a user-facing choice from the contract and make restoring it a breaking one.
No column backs the member. It is `bool` rather than `bool?` because a checkbox always posts a
definite value, and it carries no initialiser - the pre-checked default is a presentation concern that
now belongs to the Angular screen exactly as it belonged to the legacy markup, and the fail-safe wire
default is not to notify anyone the caller did not ask to notify.

**Both no-expiry encodings survive the boundary distinctly.** The six-code expiry switch at
`RoleController.vb` lines 540 to 547 produces two different "no expiry" states. Code `N` assigns
`Null.NullDate` at line 541, which is absence and is carried as `null`. Code `O` assigns
`New System.DateTime(9999, 12, 31)` at line 542, which is an ordinary, in-range, externally observable
`datetime` and round-trips **verbatim**; it is never normalised to `null`, to `DateTime.MaxValue`, or
to any notion of "unbounded". The legacy code agrees: `Null.IsNull` compares only the date component
against the sentinel (`Null.vb` lines 222 to 224), so `9999-12-31` reads as present while the sentinel
reads as absent. This is AAP Rule T7 applied at the one place it bites hardest.

**A refinement to the recorded billing codes.** The plan names four codes, `D`, `W`, `M` and `Y`. The
measured switch carries **six**: `N` and `O` precede them, and they are the two that matter for the
expiry contract. Recorded here rather than quietly reconciled. No frequency member appears on the
assignment contract - the codes belong to the role definition - so the file imports nothing from
`DnnMigration.Domain.Enums`.

**`EffectiveDate` is a `03.02.03`-era column.** It has zero occurrences in the `01.00.00` baseline,
whose `UserRoles` table declares five columns and not this one. It is added by a templated
`ALTER TABLE {databaseOwner}{objectQualifier}UserRoles ADD EffectiveDate datetime NULL` at
`03.02.03.SqlDataProvider` line 380 and re-added under a `fn_GetVersion(3,2,3)` guard at
`04.00.04.SqlDataProvider` line 418, with stored-procedure parameters defaulting to `null` at lines 463
and 569 of the former and 501 and 607 of the latter. A baseline-only search would have concluded the
column does not exist and dropped a real member from the contract - only the terminal schema counts.
A stored `null` is also load-bearing: the membership window is
`(EffectiveDate <= getdate() or EffectiveDate is null)` at `03.02.03` line 405 and `04.00.04` line 443,
so absence means "already in force" rather than "unknown".

**`DateTime?`, deliberately not `DateTimeOffset?`.** The columns are SQL `datetime`, which stores no
offset, and the legacy screen parsed them with a culture-dependent, zone-free `Date.Parse`. An offset
type would invent information the schema cannot store and the existing rows do not carry, and would
silently re-interpret every legacy row it round-tripped. The values are wall-clock and unzoned, and
are serialised without an offset.

**What the contract omits, and why.** `RoleId` and `PortalId` arrive in the route, which is what keeps
them authoritative - a tenant a caller can restate is a tenant a caller can contradict. `UserRoleId` is
server-assigned (`UserRoles.UserRoleID` is `IDENTITY (1, 1)` at `01.00.00` line 239) and nothing needs
it on the wire: the grid's static `datakeyfield="UserRoleID"` (`securityroles.ascx` line 56) is
overwritten at runtime with either `UserId` or `RoleId` (code-behind lines 244 and 251), so even the
legacy delete path identified an assignment by the user-and-role pair. `IsTrialUsed` is excluded as
server-managed state and as a genuine abuse vector - it guards the trial-versus-billing decision at
line 521, and a client able to set it could re-claim a consumed trial period. `Subscribed`, `FullName`,
`Email` and `UserName` are derived or display-only. The acting administrator comes from claims, never
from the body.

**No rule, and no date arithmetic, lives in the contract.** It is inert. The effective-before-expiry
ordering, the temporal normalisation at lines 530 to 535, the cancellation branch at lines 493 to 501
and the upsert decision at lines 550 to 554 all belong to `RoleService`. The single
`Imports Microsoft.VisualBasic` at line 25 is removed and its `DateAdd` calls are rewritten inside the
service.

**Two gaps recorded for their owners rather than filled here.** First, the legacy `grdUserRoles` grid
rendered a members list - `UserName`, `RoleName`, `EffectiveDate`, `ExpiryDate` - and **no response DTO
for that shape exists** in the role contract folder, which the plan fixes at six files. It is served
either by a user-side list filtered by role or by a member collection on the role detail contract, and
that is a decision for those owners; nothing was added here to compensate. Second, the
effective-before-expiry ordering rule **is** declaratively specified in the legacy markup -
`securityroles.ascx` line 47 declares a `valDates` compare-validator with `operator="GreaterThan"`
comparing the expiry box against the effective box - and has no validator counterpart, so it currently
rests entirely on the service.

**An asymmetry downstream agents must not unify.** In `ModulePermissions` and `TabPermissions` a
`RoleID` of `-1` means All Users, `-2` Superuser, `-3` Unauthenticated Users and `-4` an in-memory
marker: those are real principals and must never map to `null`. A legacy `UserID` of `-1`, by contrast,
genuinely is absence. The legacy permission signature passed `-1` with two different meanings by
position. Nothing about this is implemented in the assignment contract, which treats `UserId` as a
plain required integer and performs no absence test at all - `-1` and `0` are real identifiers in this
schema, since `Portals.PortalID` is `IDENTITY(-1,1)` and `Roles.RoleID` is `IDENTITY(0,1)`.

## Module registration catalogue — the repository contract

These notes concern
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IModuleDefinitionRepository.cs`, the
Domain contract that realises the module-definition block of the legacy data surface
(`Library/Components/Providers/Data/DataProvider.vb:L156-L183`). Each divergence below is also
annotated inline in that file.

### The obsolete friendly-name package lookup is not carried across

**Legacy behaviour.** `DataProvider.vb:L158` declares `GetDesktopModuleByFriendlyName`, and
`Library/Components/Modules/DesktopModuleController.vb` exposes it through two wrappers, at `L80`
and `L85`.

**Target behaviour.** The provider member is omitted, and only
`GetDesktopModuleByModuleName` (`DataProvider.vb:L159`) is surfaced, as
`GetDesktopModuleByModuleNameAsync`.

**Why.** The legacy source retires the lookup itself. Both wrappers carry
`<Obsolete("As the FriendlyName is not guaranteed to be the same as when the module is created,
this method has been replaced by GetDesktopModuleByModuleName(moduleName)")>`, so the obsolescence
message nominates its own replacement. The two wrappers are also not distinct: `L81` and `L86` call
the same provider member, making `GetDesktopModuleByName` an exact duplicate of
`GetDesktopModuleByFriendlyName` under a second name. Preserving a key the source declares
unreliable would carry a known defect forward. The friendly name is genuinely unsafe as a package
key because `03.01.00.SqlDataProvider:L34` dropped the unique constraint that once backed it; the
equivalent constraint on `dbo.ModuleDefinitions` survives, which is why
`GetModuleDefinitionByNameAsync` keeps its name-based lookup.

### Cache invalidation is not a parameter and not a member

**Legacy behaviour.** Three controllers pair each write with a cache clear and expose a second
overload to suppress it: `DesktopModuleController.vb:L74` (`Friend`, so already internal),
`ModuleDefinitionController.vb:L57` and `ModuleControlController.vb:L139`. The clears themselves sit
at `DesktopModuleController.vb:L42`, `L47` and `L76`, `ModuleDefinitionController.vb:L38` and `L59`,
and `ModuleControlController.vb:L116` and `L141`.

**Target behaviour.** No member of the contract accepts a cache hint, and none clears a cache.

**Why.** Cache management is a separate concern with its own abstraction, and a `clearCache`
argument makes a caller responsible for the correctness of a subsystem it cannot see.

### Long positional argument lists collapse onto entities

**Legacy behaviour.** `AddDesktopModule` takes twelve positional arguments
(`DataProvider.vb:L162`), `UpdateDesktopModule` thirteen (`L163`), `AddModuleControl` nine (`L181`)
and `UpdateModuleControl` ten (`L182`).

**Target behaviour.** Each becomes a single entity parameter plus a cancellation token, and no
member of the contract exceeds three parameters plus that token.

**Why.** A positional list of that length is ordered by convention alone, and adding a column
changes every call site. Passing the entity moves that concern to the entity, where the schema
already describes it.

### No write returns the generated key

**Legacy behaviour.** Every `Add` member returns `Integer`, because each stored procedure ends with
`SCOPE_IDENTITY()`.

**Target behaviour.** Every `Add...Async` stages the insertion and returns a non-generic `Task`. The
identity is readable from the entity once `IUnitOfWork.SaveChangesAsync` has committed.

**Why.** Under an object-relational mapper the key is assigned during the commit, so a member that
returned one would have to commit on the caller's behalf. That would dissolve the unit-of-work
boundary and split the multi-table portal creation at
`Library/Components/Portal/PortalController.vb:L980` into one transaction per row. The legacy code
had already stopped relying on the value in places: `DesktopModuleController.vb:L36` wraps a
provider function that yields an identity in a `Sub` that discards it.

### The sentinel-to-`NULL` conversion in the legacy data layer is not reproduced

**Legacy behaviour.** `Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb` passes
several of these arguments through the sentinel converter at
`Library/Components/Shared/Null.vb:L155`, so a sentinel argument reached the store as SQL `NULL`:
`GetPortalDesktopModules` and `DeletePortalDesktopModules` (`L799` and `L805`, both arguments),
`GetModuleControls` (`L832`), `GetModuleControlsByKey` (`L834`, both arguments) and
`GetModuleControlByKeyAndSrc` (`L837`, all three). The terminal procedures then give that `NULL`
two different meanings. `GetPortalDesktopModules`
(`02.02.02.SqlDataProvider:L3161-L3162`) reads `((PortalId = @PortalId) or @PortalId is null)`,
which is a match-all wildcard. `GetModuleControlsByKey` (`02.02.00.SqlDataProvider:L544-L545`)
reads `((ControlKey is null and @ControlKey is null) or (ControlKey = @ControlKey))`, which instead
matches the rows whose own column is null — the mechanism by which
`04.05.00.SqlDataProvider:L1491` identifies a definition's default control.

**Target behaviour.** Identifiers and lookup keys are plain, required values on the contract.
Absence is expressed by a null entity or an empty list, never by a numeric or empty-string
sentinel, and no member applies a range or sign constraint to an identifier. A caller needing the
wildcard or the null-row reading asks for it through a member of its own rather than by encoding it
in an argument value.

**Why.** Two incompatible meanings behind one argument value is precisely the ambiguity the
sentinel system created. `Null.vb:L41-L45` sets the integer sentinel to `-1` and
`Null.vb:L208-L211` reports `-1` as absent, yet `dbo.Portals.PortalID` is declared
`IDENTITY(-1, 1)`, so the helper could not tell a real tenant from a missing one. The string
sentinel was the empty string rather than null, so an empty key was legally representable and must
not be folded into "no key supplied".

### Two legacy readings are documented rather than expressed as parameters

`GetModuleControlsByKey` also carries a fixed exclusion in the store —
`02.02.00.SqlDataProvider:L546` excludes one reserved negative control ordinal — and
`GetDesktopModulesByPortal` (`04.05.00.SqlDataProvider:L1041-L1064`) excludes every package flagged
as belonging to the administration experience before applying its premium test. Both are properties
of the legacy query rather than of the contract, so they are recorded on the members that carry
them and are honoured by the implementation, not surfaced as arguments a caller could vary.

### The reflection hydrator and the hand-written reader both disappear

**Legacy behaviour.** `DesktopModuleController.vb` hydrates rows through `CBO.FillObject` (`L51`,
`L55`, `L81`, `L86`) and `CBO.FillCollection` (`L59`, `L63`, `L67`), returning `ArrayList`.
`ModuleControlController.vb` carries a second, hand-written path — a collection filler and two
overloads whose ten sentinel-translating assignments run from `L89` to `L98`.

**Target behaviour.** Neither produces a target file. Every multi-row read returns
`IReadOnlyList<T>` and every single-row read a nullable entity.

**Why.** `Library/Components/Shared/CBO.vb` is 729 lines of reflection-driven materialisation that
the object-relational mapper performs natively, and the hand-written path duplicated it less
safely — `ModuleControlController.vb:L94` read `IconFile` while passing `ControlKey` as the value
selecting the substituted sentinel, which was correct only because both happen to be strings.

### The catalogue and the placements are separate contracts

`IModuleDefinitionRepository` owns the catalogue block (`DataProvider.vb:L156-L183`):
`DesktopModule`, `PortalDesktopModule`, `ModuleDefinition` and `ModuleControl`.
`IModuleRepository` owns the module block (`L125-L154`): `Module`, `TabModule` and the two settings
tables. The division follows the legacy provider's own grouping, so no member of either contract
reaches into the other aggregate and the settings tables are not split into repositories of their
own.

## The account aggregate — `UserInfo` and `UserMembership` merged onto one root

**Target:** `backend/src/DnnMigration.Domain/Entities/User.cs`, asserted by
`backend/tests/DnnMigration.UnitTests/Domain/UserTests.cs`
**Legacy sources:** `Library/Components/Users/UserInfo.vb` (class at line 41, fourteen properties)
and `Library/Components/Users/Membership/UserMembership.vb` (class at line 41, fifteen properties)

### The merge, and why it is not a flattening

`UserInfo` owned a `UserMembership` instance and a `UserProfile` instance and hydrated each of them
from inside a property getter — lines 196-204 call `UserController.GetUserMembership` and lines
237-245 call `ProfileController.GetUserProfile` — so reading a property performed database I/O. The
target keeps the merged property set but deletes the mechanism: every getter is a field read, and
composing the account row with the external credential store is an explicit, inspectable step in a
repository method. Rule T8 applies: the workaround produced no target artefact.

Where the merged properties landed is worth stating because it is easy to guess wrongly. Nine
properties are the terminal `dbo.Users` columns; eleven are a read-only snapshot of facts held
elsewhere; the per-tenant facts went to `UserPortal`, not here.

### Credential facts are nullable, and absence is a third state

The legacy membership object initialised its approval flag to `True` (`UserMembership.vb` line 45)
and its lockout flag to `False` (line 53) — the only two inline initialisers it had. A port to
non-nullable C# booleans would have defaulted approval to `false` and silently un-approved every
account it constructed.

The target does not merely avoid that trap, it removes the condition that created it.
`User.IsApproved` and `User.IsLockedOut` are `bool?`. The account row lives in `dbo.Users` while the
credentials live in the external ASP.NET membership tables, so an account materialised from its own
row genuinely does not yet know whether it is approved, and `null` says exactly that.

This is a deliberate divergence from the letter of the legacy default, and it is a strengthening.
With three states each flag fails safe in *its own* direction from the same absent value:

- `IsApproved == true` is `false` when unread, so an account nobody vouched for is never admitted.
- `IsLockedOut == true` is `false` when unread, so an account nobody locked is never refused.

A single non-nullable boolean cannot express both, because whichever value it defaults to is wrong
for one of them — which is precisely why the legacy class needed two different initialisers to paper
over the problem. An explicit `false` also stays distinguishable from "not read", so a caller can
tell "reviewed and refused" from "nobody has looked".

The one place a non-sentinel legacy default *does* survive as an initialiser is
`UserPortal.IsAuthorised = true`. That flag is a column on a row which either exists with an answer
or does not exist at all, so it has no third state to model, and the shipped baseline agrees with the
default (`01.00.00.SqlDataProvider` line 7229 inserts the administrator's membership with the flag
set).

### `Email` — a legacy dual-write collapses to one property

The legacy setter (`UserInfo.vb` lines 121-134) assigned the private field **and** assigned
`Me.Membership.Email`, the source's own comment explaining the second write existed "in case
developers have used this in their own code". The getter returned only the private field. The write
was symmetric and the read was not, so assigning the membership copy directly left the account's own
answer stale and the two could disagree indefinitely.

Per Minimal Change Clause item 1 the asymmetry is recorded rather than characterised as a defect to
be repaired in the legacy record: it was a deliberate backward-compatibility shim, and it has
nothing left to be compatible with once there is one property. The target declares exactly one
address member, and the test asserts that reflectively so a shadow copy cannot return.

### `FirstName` and `LastName` are columns, not profile delegates

The legacy properties had no backing fields — the given name at `UserInfo.vb` lines 144-151 read and
wrote `Profile.FirstName`, and the family name at lines 178-185 did the same — so reading a name
could trigger a profile fetch. Both are nevertheless genuine columns on the account table
(`01.00.00.SqlDataProvider` lines 99-100), and the terminal view at `04.00.04.SqlDataProvider` lines
777-778 selects both straight from it. The delegation was therefore pure cost: a database round trip
for data already in hand. The target models the table.

### The hydration flags and the composed objects are not reproduced

Four legacy members existed only to service lazy hydration and none is carried forward: the
membership object's `ObjectHydrated` (`UserMembership.vb` line 243), which the address setter at
lines 344-354 flipped as a side effect of assigning a value; the profile's own `ObjectHydrated` and
`IsDirty` (`UserProfile.vb` lines 271 and 237); and the account's `_RolesHydrated` field
(`UserInfo.vb` line 58). The two composed objects they guarded go with them, as does the raw
role-name array at line 261 — role membership is the assignment entity — and the credential at
`UserMembership.vb` line 263, which is now a hash held in the external store.

The account also declares no tenant identifier of its own. The legacy class carried one
(`UserInfo.vb` line 219) and seeded it to `-1`, which is a real portal key; since the terminal view
takes the tenant identifier and the authorisation flag from the membership join, a copy on the
account row would be a second, contradictable source of truth.

### `IPropertyAccess` and the Web Forms property-editor attributes are dropped

The legacy class implemented `DotNetNuke.Services.Tokens.IPropertyAccess` (`UserInfo.vb` line 42,
with `GetProperty` at line 426 and `Cacheability` at line 484) and decorated its properties with
`Browsable`, `SortOrder`, `Required`, `MaxLength`, `IsReadOnly` and `RegularExpressionValidator`
imported from `DotNetNuke.UI.WebControls`. Both the token-replacement subsystem and the control
library are out of scope. The obligations those attributes expressed do not vanish: the wire shape
belongs to the DTOs and every length, required and format rule belongs to a FluentValidation
validator in the Application layer. They must not reappear as DataAnnotations — the Domain project
declares no package reference at all, by design. The tests assert attribute emptiness at both type
and property level.

### Text is never seeded with the empty string, resolving a legacy self-contradiction

The legacy tree disagreed with itself. The account constructor set six fields and left its four
string fields alone (`UserInfo.vb` lines 64-73 over the fields at lines 48-51), so a freshly
constructed account carried `Nothing` for its login name, display name and address. The module and
page classes did the opposite and seeded their string fields to `Null.NullString`, which
`Library/Components/Shared/Null.vb` line 71 defines as the **empty string**. Two aggregates in one
codebase therefore disagreed about what "no text yet" looks like.

The target resolves this in one direction rather than picking a winner: no string property on
`User`, `Module` or `Tab` carries an initialiser, so a freshly constructed instance holds `null`
everywhere and an empty string is always a value somebody stored. The nullable annotation states
which columns may be absent in the store; the unannotated ones are `NOT NULL` and are assigned by
whatever materialises the row, which is the case `CS8618` is suppressed for.

### `UserCreateStatus` — the default value is deliberately not success

**Target:** `backend/src/DnnMigration.Domain/Enums/UserCreateStatus.cs`
**Legacy source:** `Library/Components/Users/Membership/UserCreateStatus.vb` lines 24-41

Unlike `UserLoginStatus`, **no member of this enumeration was renamed** and all eighteen numeric
values carry across unchanged, so there is no mapping table to give. What needs recording is a trap
in the numbering itself: `Success` is **13**, and the zero member is `AddUser`.

`UserController.vb` line 158 opens `CreateUser` with
`Dim createStatus As UserCreateStatus = UserCreateStatus.AddUser` and line 163 then tests
`If createStatus = UserCreateStatus.Success`, so zero is the deliberate "the provider has not
answered yet" state and thirteen is the only affirmative one. C# initialises an enum field, array
element or unassigned local to zero without complaint, so renumbering to put `Success` first — the
ordering a reader who sorts members by importance would naturally produce — would make every
default-initialised status report a created account.

`Null.vb` lines 141-150 compound it: the legacy sentinel reader sorts an enumeration's values and
returns the lowest, so the numerically lowest member is also what a null column reads back as. The
lowest member and the default member therefore have to be the same non-affirmative one, and the tests
assert that as well as the eighteen values individually.

### `Null.NullByte` (255) has no target surface

`Null.vb` line 46 defines the byte absence marker as `255` — the only entry in the legacy table that
is a large positive number rather than a negative one, a type minimum or an empty value. No property
on `User`, `UserPortal`, `UserProfileValue` or `ProfilePropertyDefinition` is byte-typed, so the
marker has nowhere to land. This is asserted structurally rather than assumed, so that introducing a
byte column later re-raises the question instead of inheriting an unexamined answer.

The legacy dispatcher could not have applied the marker anyway: `Null.vb` line 125 reads
`Case "system.Byte"` with a lower-case initial letter, which can never match the `"System.Byte"`
that the reflected property type reports, and `Library/DotNetNuke.Library.vbproj` line 22 sets
`<OptionCompare>Binary</OptionCompare>`. That branch is unreachable dead code. Recorded, not
repaired, per Minimal Change Clause item 1 — and the file it lives in produces no target artefact.

## Profile visibility and the profile definition

**Targets:** `backend/src/DnnMigration.Domain/Entities/UserProfileValue.cs` and
`backend/src/DnnMigration.Domain/Entities/ProfilePropertyDefinition.cs`
**Legacy sources:** `Library/Components/Users/Profile/UserProfile.vb`,
`Library/Components/Users/Profile/ProfilePropertyDefinition.vb`,
`Library/Components/Users/UserVisibilityMode.vb`

### Nineteen named properties become rows

`UserProfile` exposed nineteen properties (lines 103-451) but held only three backing fields, because
sixteen of them read and wrote through a single keyed collection (line 82). `UserProfileValue` makes
that indirection explicit: the named properties become rows keyed by account and definition, and
adding a twentieth profile question is a row in the definition table rather than a change to a class.

The distinction between an empty answer and an unanswered question matters more here than anywhere
else on the aggregate. Under the legacy textual marker, `''` and no answer at all were the same
state, so a member who deliberately cleared a profile field could not be told from one who never
filled it in. The target keeps them apart.

### `UserVisibilityMode` has no target enumeration, and the visibility column stays an integer

`UserVisibilityMode.vb` lines 23-26 declare three explicitly valued members — `AllUsers` 0,
`MembersOnly` 1, `AdminOnly` 2. No counterpart is generated, and `UserProfileValue.Visibility` is a
plain `int`. The reason is that the persisted column is **wider than the enumeration**: the legacy
interpreter maps 0, 1, 2 and the legacy `-1` marker onto those three members and leaves every other
number unmapped (`ProfilePropertyDefinition.vb` lines 353-358), so an enumeration here would publish
that gap as though it were a complete contract and would let a number the database really holds
surface as an undeclared member. The raw value is kept and its interpretation belongs to the layer
that presents it.

The only enumeration in the Domain project whose name contains the word is `ModuleVisibility`, which
classifies whether a module is shown on a page and is unrelated. The absence of the account-visibility
enumeration is asserted by name against the assembly, so it cannot be reintroduced unnoticed.

### The visibility default: a three-way disagreement, resolved in favour of the store

This is security-relevant and the framing matters, because the obvious reading of it is wrong.

- The legacy **constructor** initialised the field to the most restrictive member,
  `UserVisibilityMode.AdminOnly` (`ProfilePropertyDefinition.vb` line 61).
- The **database** disagrees: the column is `int NOT NULL DEFAULT 0` (`04.00.04.SqlDataProvider`
  line 1418), which is `AllUsers`.
- The legacy **sentinel reader** agrees with the database and not with the constructor: for an
  enumeration it returns the numerically lowest member (`Null.vb` lines 141-150), which is again
  `AllUsers`.

So two of the three legacy mechanisms say `0` and only the constructor said `2`, and real rows
already depend on the `0` default — the upgrade that moved the flat address columns into this table
inserts only four columns and leaves this one to the database (`03.02.03.SqlDataProvider` line 2094).
The target follows the store.

The consequence is stated plainly rather than left implicit: **an answer whose visibility nobody set
is visible to everyone**, exactly as it already is for every row that upgrade path created.
Restricting it is an application decision applied on write. No default assigned in the entity could
express it without also being written on every insert, which would stop the column being the
database's answer.

`ProfilePropertyDefinition` stores no per-account visibility at all. The legacy class exposed a
`Visibility` property of the enumeration type at line 336 that **no column of the definition table
backs**, so it was never state of that entity; the definition stores only whether the question
appears on the form, renamed from the legacy `Visible` (line 318) to `IsVisible` to read as a
predicate while the column keeps its name.

### `ProfilePropertyDefinition`'s parameterless constructor cannot be ported

The legacy class had two constructors and the parameterless one is a layering violation. Lines 65-71
call `PortalController.GetCurrentPortalSettings()` to discover which tenant the new definition
belongs to, and that method is `CType(HttpContext.Current.Items("PortalSettings"), PortalSettings)`
(`PortalController.vb` lines 1209-1210) — a domain constructor reading per-request web state. It then
called an initialiser that read a module setting to choose the default visibility (lines 348-359).

Neither survives, and neither could: the Domain project cannot reference `System.Web` or its
ASP.NET Core equivalent, and Rule T1 makes that a compile-time guarantee rather than a convention.
The target has the implicit parameterless constructor and nothing else — the tenant is assigned by
the caller that knows it, and the per-tenant default visibility is configuration supplied by an
Application service. The constructor surface is asserted reflectively (exactly one constructor,
zero parameters) so the ambient read cannot creep back in.

The legacy `-1` field initialisers at lines 47, 51 and 54 are likewise not reproduced: the two
foreign keys become nullable, so absence is `null` and no real key value is reserved. That matters
concretely for the module definition, since `dbo.ModuleDefinitions.ModuleDefID` is
`int IDENTITY(1, 1)` (`01.00.00.SqlDataProvider` line 66) and `0` is not a real value there either.

### The anchored name pattern and the editor metadata are not Domain concerns

`PropertyName` carried `RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")` at line 228 — anchored,
in contrast to the email pattern's word boundaries, and using the same character class as an address
local part. `DataType` carried `Editor("DotNetNuke.UI.WebControls.DNNListEditControl, DotNetNuke")`
and `List(...)` at lines 88-90. The first is a validation rule and moves to a FluentValidation
validator in the Application layer; the second named a control from the excluded control library. The
Domain accepts any string, which the tests prove by storing a name that violates the legacy pattern
and asserting it round-trips.

## User profile contract — the repository contract

### One contract spans both legacy provider stacks

`IUserProfileRepository` is the only repository contract assembled from two different abstract
provider classes, and the split is measured rather than assumed. The core provider at
`Library/Components/Providers/Data/DataProvider.vb` declares 269 abstract members across 397 lines,
of which the profile surface is the six *declaration* members at lines 251 to 256 alone — not one of
its members reads or writes a profile *value*. The values come from a separate 131-line abstract
class, `Library/Providers/MembershipProviders/DataProvider/DataProvider.vb`, whose `'Profile` block
at line 117 declares exactly two members and which resolves through its own reflection-created
accessor under the namespace `DotNetNuke.Security.Membership.Data` rather than the core
`DotNetNuke.Data`. The 1,773-line `AspNetMembershipProvider` settles it: it invokes no stored
procedure directly, and line 59 reads `Private Shared dataProvider As dataProvider =
dataProvider.Instance()`, delegating entirely through that second accessor. Reading either provider
alone would have produced a materially incomplete contract, so the two concerns are combined and no
separate profile-declaration repository exists. Neither singleton is translated; constructor
injection replaces both.

### The serialized-blob personalization path is omitted entirely

Four core members are dropped: `GetAllProfiles` (line 245), `GetProfile(UserId, PortalId)` (246),
`AddProfile(UserId, PortalId)` (247) and `UpdateProfile(UserId, PortalId, ProfileData As String)`
(248). The last argument names the representation — one opaque serialized string per user and portal
— which was superseded by the per-property row model reached through membership lines 118 and 119,
where each answer is its own `dbo.UserProfile` row keyed by `PropertyDefinitionID`. Two details
confirm they are different generations of the same feature rather than complements: the block's own
header at line 244 reads `' personalization`, not `' profile`, and the blob path is portal-scoped
while the surviving reader takes the account alone. The `Personalization` subsystem is out of scope,
and a blob payload cannot express the per-answer visibility or timestamp the row model carries as
columns.

### A cleared answer is blanked, not deleted — restoring the legacy write

**This is the one behavioural difference in this area and it is a fidelity restoration.** No
value-deletion member exists on the contract because none existed in the legacy surface: the
membership `'Profile'` block declares only a reader and an upsert, and a case-insensitive sweep of
all eighty-eight upgrade scripts across every naming form those scripts use finds no procedure that
deletes a profile value and no `DELETE` statement against `dbo.UserProfile` at all. The write path
was a single upsert, `UpdateUserProfileProperty`, which resolves a null or `-1` `@ProfileID` against
the `(UserID, PropertyDefinitionID)` natural key (`04.00.04.SqlDataProvider` lines 1616 to 1620) and
then branches to an `UPDATE` arm at line 1622 or an `INSERT` arm at line 1634. Clearing an answer was
therefore an upsert carrying an empty value, which the legacy code could express because
`Null.NullString` was the empty string rather than null.

`UserService.UpdateProfileAsync` accordingly blanks a stored answer the submitted set omits instead
of removing its row. The effective value reads back identically — `UserMappings.ToProfile` emits one
entry per declaration and an absent or blank answer both surface as an empty string — while the row's
visibility is carried forward and its timestamp refreshed, exactly as the legacy write did.
Deleting the row would instead have reset the visibility to the tenant default and dropped the
timestamp to null. Withdrawing a whole *declaration* does still discard its answers, but through the
schema rather than through a member: `FK_UserProfile_ProfilePropertyDefinition` is declared
`ON DELETE CASCADE` (`04.00.04.SqlDataProvider` line 1429), and the repository loads the dependent
answers before staging the removal so the cascade is issued by the change tracker as well, which
keeps the outcome identical on a provider that does not enforce the constraint itself.

### The single upsert becomes two explicit members

Membership line 119 `UpdateProfileProperty(ProfileId, UserId, PropertyDefinitionID, PropertyValue,
Visibility, LastUpdatedDate)` splits into `AddProfileValueAsync` and `UpdateProfileValueAsync`,
because the object-relational mapper tracks entity state explicitly and a caller that has just read
a row already knows whether it exists. Both take the entity rather than six positional arguments.
The `LastUpdatedDate` argument becomes a property rather than a parameter — `dbo.UserProfile` is the
only in-scope table carrying that column, which arrived with the table at
`03.02.03.SqlDataProvider` line 1372 — so no member of the contract accepts a date, and the caller
stamps the row from the injected clock, which is what keeps time-dependent behaviour testable.
Neither add member yields the generated key: the legacy procedures ended in `SCOPE_IDENTITY()`,
whereas returning a key here would force a save and dissolve the unit-of-work commit boundary that
tenant provisioning depends on.

### The name lookup answers with the declaration rather than a boolean

Core line 254 `GetPropertyDefinitionByName(portalId, name)` returned the row, and
`GetDefinitionByNameAsync` preserves that. It matters for the caller it principally serves: refusing
a duplicate name while *editing* a declaration means comparing the found declaration's identifier
with the one being edited, and a boolean cannot express "found, but it is the row I am editing". The
exclusion therefore stays in the service, where the edit is happening, rather than being pushed into
persistence as an extra argument.

### The catalogue read drops its `includeDeleted` argument

`GetDefinitionsByPortalIdAsync` replaces core line 255 `GetPropertyDefinitionsByPortal(portalId)`,
which took the tenant alone. Withdrawn declarations are excluded, which the contract states as an
expectation of the implementation instead of exposing as a parameter; every call site had passed the
same value, so no behaviour changes. A caller that needs a withdrawn declaration addresses it by key
or by name, neither of which filters the flag. Filtering by category is likewise not offered:
`ProfileController.vb` line 485 `GetPropertyDefinitionsByCategory` iterated an already-loaded
collection and kept the matching entries, and `ProfilePropertyDefinitionCollection.GetByCategory`
carried the same in-memory predicate a second time. Neither had a backing stored procedure, so a
category view is an Application-layer projection and adding a repository member would invent
persistence behaviour that never existed.

### Orchestration and the one `ByRef` site move out

`ProfileController.vb` line 226 `GetUserProfile(ByRef objUser As UserInfo)` is the only `ByRef`
member in that 561-line controller, and it is orchestration rather than persistence — it mutated a
`UserInfo` the caller already held. The contract answers with values and lets the caller compose
them; no `out` or `ref` parameter appears anywhere in it. Line 334 `AddDefaultDefinitions` is
excluded on the same principle: it resolved a data-type list and then wrote a batch of declarations
one at a time, which makes it a sequence of calls to `AddDefinitionAsync` under one unit of work.
`ProfilePropertyDefinitionCollection`, a 313-line `CollectionBase` subclass, and the controller's two
reflection-hydrator call sites produce no target file; `IReadOnlyList<T>` and the mapper's
materialiser replace them.

## `UpdateRoleRequest` — the role name is not updatable, so the contract declares no `RoleName`

**The update contract carries twelve members, one fewer than the creation contract, and the missing
member is `RoleName`.** Renaming a role was never a workflow this application offered, so the
migrated contract does not offer one either. Five independent findings establish that, and the last
of them is decisive.

The edit screen made the name read-only. At `Website/admin/Security/EditRoles.ascx.vb` lines
131-134, whenever the screen was editing an existing role it revealed a display label, hid the name
textbox and disabled `valRoleName` — the screen's only `RequiredFieldValidator` — then filled the
label from the stored value at line 140. The legacy membership data contract declared no parameter
for the name on its update member, at
`Library/Providers/MembershipProviders/DataProvider/DataProvider.vb` line 97. The provider
implementing that contract never passed one, at
`Library/Providers/MembershipProviders/DNNMembershipProvider/DNNRoleProvider.vb` line 325, which
forwards thirteen values and omits `role.RoleName`. The terminal stored procedure omits the column
from its assignment list: `Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider`
line 454 declares `@RoleId` plus exactly the twelve writable values, and its
`UPDATE dbo.Roles SET ...` names twelve columns, none of them `RoleName`. And the screen applied its
duplicate-name guard only when inserting — at `EditRoles.ascx.vb` lines 251-257 the add branch looks
the name up and refuses on a hit, while the edit branch updates with no such check, an asymmetry that
is coherent only because the name could not change.

**The destructive DDL chain is what makes the fifth finding a terminal-state argument rather than a
baseline one.** `UpdateRole` is created seven times across the eighty-eight scripts — at
`01.00.00:L1790`, `01.00.04:L1439`, `01.00.08:L5294` and `:L7098`, `02.00.00:L4317`, `03.02.03:L416`
and `04.00.04:L454` — under all four naming forms and in both letter cases. The `02.00.00` form
**did** carry `@RoleName` and **did** write `set RoleName = @RoleName`; the later recreations removed
it. Only the terminal form is meaningful, and a case-sensitive search for a single naming form finds
none of these objects at all.

### A latent legacy defect, annotated and NOT fixed

The legacy save block still assigned the name unconditionally: `EditRoles.ascx.vb` line 237 reads
`objRoleInfo.RoleName = txtRoleName.Text`, reading a textbox the same screen had hidden at line 133.
A hidden Web Forms control renders nothing and therefore posts nothing, so the value assigned on
every edit was the empty string — the `NullString` sentinel, which
`Library/Components/Shared/Null.vb` lines 70-74 define as `""` rather than null. Written through to
a `nvarchar(50) NOT NULL` column that would have been data corruption.

**It never corrupted data, and the reason is worth recording, because it closes the question rather
than leaving it open.** The layers beneath the assignment had nowhere to put the value: the
membership contract declared no parameter for it and the terminal procedure assigned no such column.
The empty string was a dead store, discarded before it reached SQL. The defect is annotated in place
and deliberately not reproduced, as the Minimal Change Clause requires.

Omitting the member means the migrated contract **cannot** express that store, which is the outcome
the screen's own read-only label always expressed. The consequences are borne by the layers above:
`Application/Mapping/RoleMappings.cs` passes the tracked entity's own `role.RoleName` back through
`ApplyCore`, so the projection stays total while preserving the stored name;
`Application/Services/RoleService.cs` supplies the stored name to its shape check, which also
reproduces the disabled validator exactly; and the update path performs **no** portal-scoped
uniqueness read, because a name that cannot change cannot begin to collide. The
`role.name_duplicate` reason code is consequently unreachable from the update member and is reported
only by the creating member, and `IRoleService` documents that asymmetry. The behavioural difference
from the literal legacy code path is the dead store's disappearance, and nothing else.

### The other divergences this contract carries

`RoleId` and `PortalId` are both absent. Both arrive in the route
`PUT /api/v1/portals/{portalId}/roles/{roleId}`, which makes the route authoritative: a value that
does not exist cannot contradict it, so no reconciliation check is needed. The legacy screen carried
a role identifier only because one postback served both creating and editing, overloading the value
minus one as its add-versus-edit switch at lines 131 and 251; distinct routed endpoints remove that
ambiguity. Accepting either would be an identity-tampering vector and, for the portal, a cross-tenant
write vector. Note that the route identifier may legitimately be **zero**: `dbo.Roles.RoleID` is
seeded `IDENTITY (0, 1)` at `01.00.00.SqlDataProvider` line 115, so no non-positive test may ever
stand in for an absence test.

`RsvpLink` is absent because it was never an input. `editroles.ascx` line 161 declares it
`ReadOnly="True"` with no validator, the code-behind composed it at lines 165-167 from the redemption
code and the request's domain name purely for display, the save path at line 247 wrote only the code,
and no such column exists in any of the eighty-eight scripts. The client composes it from `RsvpCode`
and its own origin.

`RoleGroupId` is `int?`, where null means the role belongs to no group — the state the screen
labelled "Global Roles". The screen posted minus one for that choice, yet minus one can never be
stored, because `RoleGroups.RoleGroupID` is seeded `IDENTITY(0,1) NOT NULL` at `03.02.03:L18` and the
foreign key added at `03.02.03:L37` would reject it. Minus one was the presentation encoding of a
database null, translated in both directions by the provider layer. Zero is a **legitimate** group
identifier, and the separate negative value the roles list screen used as an all-roles filter
(`Roles.ascx.vb` line 112) is transient and never stored. None of this may be conflated with the
unrelated permission rule, where a negative role identifier in `dbo.ModulePermissions` and
`dbo.TabPermissions` denotes a real pseudo-principal that must never become null.

Both fees are `decimal?`, superseding the legacy single-precision properties at `RoleInfo.vb` lines
164 and 233 and the screen's single-precision parse at line 226. The chronology is the clearest Rule
T4 demonstration in this contract: the baseline column was `decimal(5, 2)` at `01.00.00:L119`, a
ceiling of 999.99, and `03.01.01:L1173` widened it to `money` with a store default of zero at
`:L1177`. Both period members are `int?` on four-way evidence, the decisive strand being that the
terminal projection emits SQL null for the billing period itself whenever the fee converts to zero,
so null is the literal legacy value for a free role rather than a modernisation. Both flags are
non-nullable `bool`, matching `bit NOT NULL` with a zero default at `01.00.08:L6831-L6832`,
re-asserted at `03.01.01:L1174-L1175` and `:L1179-L1181`.

`TrialFrequency` deliberately carries **no property initialiser**, which differs from how the
creation path behaves. The screen defaulted the list to the no-trial code when adding a role
(`EditRoles.ascx.vb` line 125) but **loaded the stored code** when editing one and gated the trial
block's visibility on it (line 154). Defaulting here would overwrite a stored choice whenever a
caller omitted the member, which on a replacement contract is the wrong direction. The no-trial code
remains a real stored value and is never conflated with null; the legacy assignment path tests
against it at `RoleController.vb` line 521 to decide whether the trial or the billing term governs
expiry.

Both frequency members share the one six-member Domain enumeration, and the codes are never renamed.
The three string members are nullable, with the legacy absent value being `""` rather than null;
`RoleMappings.cs` owns that translation and no serialisation attribute forces either. The legacy XML
serialisation attributes are dropped. The contract declares no base type and does **not** inherit
from `CreateRoleRequest`, even though it overlaps almost entirely — deriving from it would smuggle in
the very member that must not exist, and the legacy tree shows the wider cost of that shortcut where
`UserRoleInfo` inherits `RoleInfo` and an eight-column assignment presents twenty-three effective
properties. No optimistic-concurrency member is invented, because no row-version, entity-tag or
last-modified column exists on `dbo.Roles` in any of the eighty-eight scripts. The conditional-fee
gating stays with the validator and the service; the contract is inert.

## The permission triad — one inheritance hierarchy becomes three independent entities

The three legacy permission classes are the only place in the migrated surface where a
single inheritance chain, four separate defects and two contradictory readings of the same
integer all meet on the same three tables. Each difference below is deliberate, each is
asserted by
`backend/tests/DnnMigration.UnitTests/Domain/PermissionTests.cs`, and each carries an
inline `// MIGRATION:` annotation at the assertion that pins it.

### The hierarchy is flattened, so a grant is no longer a kind of catalogue entry

**Legacy shape.** `Library/Components/Security/Permissions/ModulePermission.vb` declares
`Public Class ModulePermissionInfo` (line 28) followed immediately by
`Inherits PermissionInfo` (line 29), and
`Library/Components/Security/Permissions/TabPermission.vb` does the same at lines 29 and
30. Each derived class declared eight properties of its own and inherited five more, so
each presented **thirteen accessible members** and every grant instance carried a private
copy of `PermissionCode`, `ModuleDefID`, `PermissionKey` and `PermissionName`.

**Target shape.** `Permission`, `ModulePermission` and `TabPermission` each derive from
`Entity<int>` and are each `sealed`. The two grant entities reference the catalogue by
`PermissionId` plus a required `Permission` navigation, and `Permission` exposes the two
inverse collections.

**Why.** The tables were never in an inheritance relationship — they are related by a
foreign key. `Website/Providers/DataProviders/SqlDataProvider/02.02.00.SqlDataProvider`
creates the three tables separately (the catalogue at lines 684-690 with `PK_Permission`
added at lines 723-728) and declares the grant-to-catalogue keys at lines 744, 768 and 777;
`03.00.09.SqlDataProvider` rebuilds them with cascade delete at lines 482, 488 and 492.
Modelling that as inheritance made a grant indistinguishable from the action it grants and
duplicated four catalogue columns onto every grant row. Under the flattened model the
joined columns are reassembled by an Application-layer projection when a caller needs them,
and nowhere else.

**Observable consequence.** Code that relied on reading a catalogue column straight off a
grant must now traverse the navigation. That is the intended cost: the values are
single-sourced, so they cannot drift from the row that owns them.

### The legacy copy constructor and the three flattened display columns are gone

`ModulePermissionInfo` carried a second constructor at lines 55-63 —
`Public Sub New(ByVal permission As PermissionInfo)` — which chained to the
sentinel-initialising constructor and then copied `ModuleDefID`, `PermissionCode`,
`PermissionID`, `PermissionKey` and `PermissionName` off a catalogue entry onto itself. It
existed only because the inheritance gave the grant somewhere to put them.
`TabPermissionInfo` never had the overload at all — it declares one constructor, at line 44
— so the legacy pair was already inconsistent about it. **Neither target type declares such
a constructor**, and the absence is asserted rather than assumed.

Both legacy grant classes also declared `RoleName`, `Username` and `DisplayName`
(`ModulePermission.vb` lines 93, 120 and 129; `TabPermission.vb` lines 85, 112 and 121),
each a join-flattened copy of a column belonging to `dbo.Roles` or `dbo.Users`. **None of
the six survives.** The principal is reached through the optional `Role` and `User`
navigations instead, so a grant carries no stale copy of a name the principal row can
change underneath it.

### Four legacy defects, two of which could not be reproduced

The minimal-change discipline annotates a discovered defect rather than fixing it — *unless
reproducing it would block delivery*. Two of the four below fall squarely inside that
carve-out, so each becomes a documented divergence rather than a silent correction.

**Defect 1 — equality ignored its own primary key.** `ModulePermission.vb` lines 158-165
override `Equals` and return
`(AllowAccess = perm.AllowAccess) And (ModuleID = perm.ModuleID) And (RoleID = perm.RoleID) And (PermissionID = perm.PermissionID)`
— four columns, deliberately **excluding** `ModulePermissionID`. The documentation at lines
149-153 says why: it existed solely to stop duplicates being added to the pre-generics
grant collection, whose `Contains` used it. That collection type produces no target file,
so the *reason* for the rule is gone while its consequences are not. The target compares by
identity, and the two rules genuinely disagree in **both** directions: two distinct rows
that share all four legacy columns were equal and are now not, and one row read twice and
edited in memory was unequal and is now equal. The second direction is the one change
tracking depends on.

**Defect 2 — comparing against nothing threw.** Line 159 reads
`If obj Is Nothing Or Not Me.GetType() Is obj.GetType() Then`. The operator is `Or`, not
`OrElse`, and VB's `Or` does not short-circuit, so when `obj` really was `Nothing` the
right-hand side was evaluated anyway and `obj.GetType()` dereferenced it: the legacy
`Equals(Nothing)` **threw a `NullReferenceException`** instead of returning `False`,
violating the `Object.Equals` contract that every framework collection relies on. This
cannot be reproduced under nullable reference types, so the target returns false through
the typed overload, the object override and both operators, from either side.

**Defect 3 — no hash code accompanied the equality override.** A search of
`ModulePermission.vb` finds no `GetHashCode` member, so two instances the type called equal
could hash to different buckets and become unfindable in a collection that still contained
them. In C# that omission raises the compiler's "overrides `Object.Equals` but does not
override `Object.GetHashCode`" diagnostic, which this solution promotes to a build failure
and whose suppression list is closed at two entries. The shared base therefore overrides
it, and the agreement — equal instances share a hash code — is asserted rather than
assumed.

**Defect 4 — a shadowed backing field made a constructor write dead.**
`TabPermission.vb` line 35 declares `Dim _permissionKey As String`, which shadows the
identically named field `PermissionInfo` declares at line 34. The inherited `PermissionKey`
property reads the **base** field while `TabPermissionInfo`'s constructor writes the
**derived shadow** at line 47, so that write was dead: `New TabPermissionInfo().PermissionKey`
returned `Nothing` rather than the empty string the constructor plainly intended, which is
why that type declares nine backing fields for only eight properties. In the target this
defect has **nowhere to exist** — the flattened grant carries no `PermissionKey` at all, so
there is no field to shadow and no dead write to reproduce. It was not fixed, patched or
annotated around; the restructuring removed its habitat.

**One point of agreement, worth stating.** Line 159's `Me.GetType() Is obj.GetType()` is an
*exact runtime type* test, not an "is a kind of" test, and `Entity<TId>` compares types the
same way. That technique matters more here than anywhere else in the model: the legacy
triad was one of only two inheritance hierarchies in the migrated surface, and all three
tables are keyed by their own `IDENTITY` column, so colliding identifiers are the normal
case rather than a corner one. A subtype-tolerant comparison would have let a grant equal
the catalogue entry it inherited from whenever their numbers happened to coincide.

**A fifth difference by omission.** `TabPermissionInfo` overrode neither `Equals` nor
`GetHashCode` — its last member is `DisplayName` at line 121 and the class ends at line 132
— so a page grant compared by **reference** and two instances read from the same row were
never equal. Porting it onto the shared base gives it identity equality, which also ends
the legacy inconsistency whereby one grant type used a four-column tuple and its sibling
used object identity.

### The constructors disagreed about sentinels, and the target keeps only the safe default

`PermissionInfo`'s own constructor is **empty** — `Public Sub New()` at
`Permission.vb` lines 38-39 with no body — so every field took its CLR default. Both
derived constructors, by contrast, sentinel-initialised everything they touched.
`ModulePermission.vb` lines 43-53 assign, in order, `_modulePermissionID = Null.NullInteger`
(-1), `_moduleID = Null.NullInteger` (-1), `_roleID = Integer.Parse(glbRoleNothing)` (-4),
`_AllowAccess = False`, `_RoleName = Null.NullString` (the **empty string**, not null),
`_userID = Null.NullInteger` (-1), `_Username = Null.NullString` and
`_DisplayName = Null.NullString`; `TabPermission.vb` lines 44-54 do the same for the page
scope, adding the dead `_permissionKey` write and omitting the explicit `MyBase.New()` call
that VB then inserts for it.

The target translates all eight rather than carrying any:

| Legacy constructor default | Target |
| --- | --- |
| surrogate key `-1` | CLR `0`, with persisted identity **declared** rather than deduced |
| owning `ModuleID` / `TabID` `-1` | required data; the owner must be stated explicitly |
| `RoleID` `-4` | `null` |
| `UserID` `-1` | `null` |
| `AllowAccess` `False` | `false` — reproduced exactly |
| `RoleName` / `Username` / `DisplayName` `""` | properties removed; the optional navigation reports absence |

`AllowAccess` is the one legacy default that was never a sentinel: a grant row created
without an explicit decision must withhold access rather than confer it, so it is preserved
byte for byte. On the catalogue the two text properties default to the empty string rather
than to the legacy `Nothing`, which is a hardening rather than a change of meaning — both
columns are `NOT NULL`, so no caller should have to null-check them.

**Why the owning identifiers are required rather than defaulted.**
`dbo.Modules.ModuleID` and `dbo.Tabs.TabID` are both declared `IDENTITY(0, 1)`
(`01.00.00.SqlDataProvider` lines 221 and 140), so the zero a bare instance reports is a
**real identifier** — the first module and the first page of an installation — and is
indistinguishable from a deliberate reference to them. Nothing may read zero here as
"unset"; the enforced foreign key behind each column is what rejects a grant naming no
owner.


### The same integer means three different things, and the collisions are security-relevant

This is the sharpest sentinel boundary in the migrated surface, and it is concentrated on
these three tables.

**Zero may never be read as "not set".** `dbo.Roles.RoleID`, `dbo.Tabs.TabID` and
`dbo.Modules.ModuleID` are all declared `IDENTITY(0, 1)` (`01.00.00.SqlDataProvider` lines
115, 140 and 221), and the shipped `Administrators` role really is `RoleID` zero — inserted
verbatim as `(0, 0, 'Administrators', 'Portal Administration', …)` at line 7192 under
`SET IDENTITY_INSERT`. A grant addressed to role zero is the single most common grant a real
installation holds, so a "zero means unsaved" convenience anywhere in the model would
silently discard portal administration itself. That is why `Entity<TId>` **declares**
persisted identity instead of deducing it from the value, and why it exposes no member that
tests for a default.

**Minus one points the other way, and this is the security-relevant part.**
`Null.NullInteger` is -1, and the legacy constructors used it to mean "absent" for the
surrogate key, the owning module or page, and the account. But for a **role subject** -1 is
`glbRoleAllUsers` (`Library/Components/Shared/Globals.vb` line 95), which means *grant to
everyone* — the widest possible subject, not the absence of one. A nullable mapping that
collapsed -1 to null would convert an all-users grant into a grant naming no role, and the
consequence would depend entirely on how the evaluator then read the null, so the failure
could as easily widen access as narrow it. It would also happen silently, and to the rows a
real installation holds most of. The target keeps the two apart absolutely: absence is
`null`, and every negative subject is left as the integer it is.

`dbo.ModulePermission.RoleID` never acquires a foreign key to `dbo.Roles` across any of the
eighty-eight scripts — it gets an index only, at `04.06.00.SqlDataProvider` line 1226 —
which is precisely why `vw_ModulePermissions` reaches roles through a `LEFT OUTER JOIN`
(`04.05.00.SqlDataProvider` line 685) and synthesises names for the values that resolve to
nothing: -1 reads "All Users", -2 "Superuser" and -3 "Unauthenticated Users" (lines
670-672). **Those pseudo-principals are persisted, externally observable data.** Nothing in
the domain rewrites, clamps or normalises them.

**Minus one is safe on two other columns, which completes the picture.**
`dbo.ModuleDefinitions.ModuleDefID` and `dbo.Users.UserID` are `IDENTITY(1, 1)`
(`01.00.00.SqlDataProvider` lines 66 and 98), so neither table ever issues zero and -1
collides with nothing either issues. The legacy code exploited exactly that, testing
`Null.IsNull(objModulePermission.UserID)` to tell a role grant from an account grant. On
`Permission.ModuleDefinitionId` the -1 that appears in real data is therefore **not** a
sentinel at all: the column is `NOT NULL`, so -1 cannot mean absent — it means *system
level*, an entry belonging to no particular module definition, which is what a `SYSTEM_TAB`
or `SYSTEM_FOLDER` scope entry is. That property is consequently a plain `int` rather than
an `int?`, because a nullable property would invite the one conversion that must never
happen.

The net effect is one integer carrying three unrelated meanings across four columns of the
same three tables — legacy absence marker, all-users principal, and safe out-of-range marker
— alongside a zero that is real in three columns and unissued in two. A third, unrelated
meaning exists nearby and is deliberately left where it is:
`Library/Components/Security/PortalSecurity.vb` lines 45-53 declare
`SecurityAccessLevel As Integer` with `Anonymous = -1`, which types a module control's
required access level and names no catalogue row.

### `glbRoleNothing` is a seventh `Globals` member reached from in-scope code

The technical specification lists six distinct members of the excluded `Globals` module that
in-scope code reaches — the host-settings reader, the performance multiplier, the
application path, the application map path, the map-path helper and `glbRoleUnauthUserName`.
**`glbRoleNothing` is not among them, and it should be.** Both permission constructors reach
it directly (`ModulePermission.vb` line 47, `TabPermission.vb` line 48), which makes it a
seventh member and the only one reached from a domain *constructor* rather than from a
service or a path helper. The list is incomplete rather than wrong, and no behaviour depends
on the omission, because the constant's only use was to supply a default that the target
expresses as `null`.

The full measured vocabulary, at `Library/Components/Shared/Globals.vb` lines 95-102, is
recorded here because **no target constant holds it**: a test may not invent the production
surface it is meant to be checking, so the vocabulary is documented and what is asserted is
its observable consequence.

| Legacy constant | Value | Meaning |
| --- | --- | --- |
| `glbRoleAllUsers` | `"-1"` | the grant reaches every visitor |
| `glbRoleSuperUser` | `"-2"` | the grant reaches the installation's super users |
| `glbRoleUnauthUser` | `"-3"` | the grant reaches signed-out visitors only |
| `glbRoleNothing` | `"-4"` | no role chosen — an in-memory placeholder, never a row |
| `glbRoleAllUsersName` | `"All Users"` | display name synthesised for -1 |
| `glbRoleSuperUserName` | `"Superuser"` | display name synthesised for -2 |
| `glbRoleUnauthUserName` | `"Unauthenticated Users"` | display name synthesised for -3 |

All seven are declared `As String` — the identifiers spell integers but hold text — and they
are compared as text, at `Globals.vb` lines 2303 and 2305, through
`Convert.ToString(RoleID)` rather than numerically. The right home for this vocabulary in
the target is a domain constant, exactly as the specification treats
`glbRoleUnauthUserName`. Until one exists, a grant carrying `-4` still round-trips
unmodified rather than being corrected, because a domain entity that silently repaired its
own data would hide a real defect in an installation instead of surfacing it — and
distinguishing "the row said -4" from "the row said nothing" is precisely what the nullable
property buys.

### The catalogue table is singular, and its text columns are ANSI

Recorded because a convention-driven mapping gets both wrong. The table is `dbo.Permission`
— **singular** — which is anomalous in a schema that is otherwise plural (`Portals`,
`Roles`, `Tabs`, `Modules`, `Users`, `UserRoles`, `RoleGroups`, `ModuleDefinitions`), so the
Fluent configuration must name it explicitly. Its three text columns are `varchar`, never
`nvarchar`: `04.06.00.SqlDataProvider` widens the key with
`ALTER TABLE …Permission ALTER COLUMN PermissionKey varchar(50) not null` at lines 397-398,
then recreates `AddPermission` at line 404 with `@PermissionCode`, `@PermissionKey` and
`@PermissionName` all `varchar(50)` (lines 405-408), inserting four columns at lines 411-415
and returning `SCOPE_IDENTITY()` at line 423. The terminal width is therefore **50, not the
baseline 20**, and letting the provider default those columns to Unicode would make every
parameter a different type from the column it is compared against — which costs the unique
index its usefulness for lookup and changes comparison behaviour under a case-sensitive
collation.

The catalogue class rename belongs with this. The legacy type is `PermissionInfo`, not
`Permission`, even though its file is already named `Permission.vb` (line 28; the class
spans lines 28-88), and its five properties are declared over `Dim` backing fields at lines
31-35 — `Dim`, not `Private`, so a search for private fields finds none of them. The target
keeps all five and renames two: `ModuleDefID` becomes `ModuleDefinitionId`, mapped back to
the legacy column name, and the free-text `PermissionKey` becomes the closed enumeration
described under *Domain enumerations* above. The legacy spellings are **absent rather than
retained as aliases**, which matters because property lookup is case-sensitive and
`PermissionID` differs from `PermissionId` only by casing: keeping both would give the
entity two names for one column.

All XML-serialisation decoration is dropped from the triad. `PermissionID`,
`PermissionCode` and `PermissionKey` carried `<XmlElement("permissionid")>`,
`<XmlElement("permissioncode")>` and `<XmlElement("permissionkey")>`, and the two members
hidden behind `<XmlIgnore()>` were `ModuleDefID` and `PermissionName`. The wire contract now
belongs to the DTOs at the API boundary, so the lower-case element names disappear with the
attributes that carried them and the two formerly concealed members are plainly visible to
callers that legitimately need them.

### What the permission suite deliberately does not cover

`FolderPermission.vb`, `FolderPermissionController.vb` and their collection wrapper sit in
the same legacy directory as the three in-scope files, and file management is out of scope,
so no test for them exists. The two pre-generics grant-collection wrappers produce no target
file — `IReadOnlyList<T>` subsumes them — which is what removes the reason the tuple
equality existed in the first place, though the divergence is still documented above. The
reflection-based row hydrator and its hydration interface produce no target file either, and
neither does the sentinel module: the object-relational materialiser and nullable reference
types replace all three. Permission *evaluation* is a separate concern with its own suite,
and the persistence mapping described in this section — including the singular table name —
is verified against a real schema by the integration persistence suite rather than here. The
domain suite asserts entity and enumeration invariants only, and touches no database.

## Module domain invariants — the sentinel boundary the Domain unit suite pins

The entries below record the divergences that
`backend/tests/DnnMigration.UnitTests/Domain/ModuleTests.cs` asserts. Each is
annotated inline at the point of departure with a `// MIGRATION:` comment, and the
test that holds it is named so a reader can reach the executable statement of the
decision rather than only its description.

### The capability bit field keeps its negative-value guard, and dropping that guard would invert all three capabilities

**Legacy behaviour.** `Library/Components/Modules/DesktopModuleInfo.vb` declares
`DesktopModuleSupportedFeature` at `L30-L34` as `IsPortable = 1`,
`IsSearchable = 2`, `IsUpgradeable = 4` — a flags enumeration in everything but
declaration, since it carries no `<Flags>` attribute. Every capability question was
answered by `GetFeature` at `L220-L229`, whose single condition at `L224` reads
verbatim:

```vb
If SupportedFeatures > Null.NullInteger AndAlso (SupportedFeatures And Feature) = Feature Then
```

The guard is `SupportedFeatures > -1`, evaluated first, with short-circuiting
`AndAlso`. A bit field holding the legacy integer sentinel therefore never reached
the mask test and every capability read as `False`.

**Target behaviour.** `backend/src/DnnMigration.Domain/Entities/DesktopModule.cs`
reproduces the guard exactly: each of `IsPortable`, `IsSearchable` and
`IsUpgradeable` is `SupportedFeatures > -1 && (SupportedFeatures & mask) == mask`
over the private masks `1`, `2` and `4`.

**Why the guard is load-bearing.** A naive port that keeps only
`(SupportedFeatures & bit) == bit` inverts all three answers simultaneously,
because `-1` is all-ones in two's complement: `-1 & 1 == 1`, `-1 & 2 == 2` and
`-1 & 4 == 4` are each true. A module whose capability mask was never written would
be reported portable, searchable **and** upgradeable, so content export, indexing
and version-driven upgrade would all be offered for a module that implements none
of them. This is a silent inversion with no failure signal, which is why it is
pinned by a nine-row truth table rather than left to review.

**Where it is asserted.** `SupportedFeatures_ReportsEachCapabilityFromItsOwnBit`
covers the whole reachable table — `0` through `7` plus `-1` — and
`SupportedFeatures_LegacySentinelReportsNoCapabilitiesDespiteEveryBitBeingSet`
demonstrates the two's-complement arithmetic inline before asserting that the guard
overrides it. The guard is a lower bound rather than an equality test, so
`int.MinValue` is rejected as well as `-1`.

**A second, different route to the same answer.** The legacy constructor at
`DesktopModuleInfo.vb:L58-L59` is **empty**, so a freshly constructed package left
`SupportedFeatures` at `0` rather than at the sentinel — unlike `ModuleInfo`, whose
constructor seeded five identifiers with `-1`. Zero passes the guard and fails the
masks. Both routes are asserted together by
`SupportedFeatures_DefaultConstructedPackageReportsNoCapabilitiesByTheZeroRoute`,
because a port that collapsed them — by seeding the field at `-1` to mean "unset" —
would be indistinguishable until the first real bit was written.

### The 58-property `ModuleInfo` splits four ways, and three of its properties survive nowhere

**Legacy behaviour.** `Library/Components/Modules/ModuleInfo.vb` declared 57
properties in its `Public Properties` region at `L131-L627` plus `Cacheability` at
`L925`. It was never the shape of a `dbo.Modules` row: it was the materialised
result of a join across `Modules`, `TabModules`, `ModuleDefinitions` and
`ModuleControls`, with a permission collection and per-request presentation state
carried alongside.

**Target behaviour.** The observed split, as the generated entities implement it:

| Target entity | Legacy properties it receives |
|---|---|
| `Module` (11) | `ModuleID`, `ModuleDefID`, `ModuleTitle`, `AllTabs`, `IsDeleted`, `InheritViewPermissions`, `Header`, `Footer`, `StartDate`, `EndDate`, `PortalID` |
| `TabModule` (15) | `TabModuleID`, `TabID`, `ModuleID`, `PaneName`, `ModuleOrder`, `CacheTime`, `Alignment`, `Color`, `Border`, `IconFile`, `Visibility`, `ContainerSrc`, `DisplayTitle`, `DisplayPrint`, `DisplaySyndicate` |
| `ModuleDefinition` (4) | `ModuleDefID`, `FriendlyName`, `DesktopModuleID`, `DefaultCacheTime` |
| `ModuleControl` (10) | `ModuleControlID`, `ModuleDefID`, `ControlKey`, `ControlTitle`, `ControlSrc`, `IconFile`, `ControlType`, `ViewOrder`, `HelpUrl`, `SupportsPartialRendering` |
| `DesktopModule` (13 stored + 3 computed) | `DesktopModuleID`, `ModuleName`, `FriendlyName`, `Description`, `FolderName`, `Version`, `IsPremium`, `IsAdmin`, `BusinessControllerClass`, `SupportedFeatures`, `CompatibleVersions`, `Dependencies`, `Permissions`, and the three capability flags |

**The three that survive nowhere.** `AuthorizedEditRoles`, `AuthorizedViewRoles`
and `AuthorizedRoles` have **no target member on any entity**. `03.00.01` dropped
both `Modules` columns when grants became rows in `dbo.ModulePermission`, yet the
legacy class kept exposing all three — and the legacy author's own comment above
`AuthorizedRoles` at `ModuleInfo.vb:L626` reads
`'should be deprecated due to roles being abstracted`. A semicolon-delimited
role-identifier string is not carried forward in any form; grants are reached
through `Module.ModulePermissions`. The same applies to `Tab`: `03.00.01` dropped
`Tabs.AuthorizedRoles` and `Tabs.AdministratorRoles`, and grants are reached
through `Tab.TabPermissions`.

**Where it is asserted.** `Module_CarriesTheElevenTerminalColumnsAndNoOthers` is
the positive half; `Module_OmitsTheMembersTheLegacyClassFlattenedIntoOneRow` is the
negative half, covering all 46 names by reflection so that reinstating any one of
them fails the build.

### `IPropertyAccess`, the five Web Forms rendering properties, and the untyped page lists are dropped

**Legacy behaviour.** Both `ModuleInfo` (`L37`) and `TabInfo` (`L41`) declared
`Implements IPropertyAccess`, which obliged each to expose a name-keyed
`GetProperty(..., ByRef PropertyNotFound As Boolean)` accessor and a `Cacheability`
member. `ModuleInfo` additionally carried five `<XmlIgnore()>` properties at
`L563-L599` — `ContainerPath`, `PaneModuleIndex`, `PaneModuleCount`,
`IsDefaultModule` and `AllModules` — that a skin populated while laying out its
panes. `TabInfo` carried three untyped `ArrayList` properties at `L365`, `L374` and
`L383` — `BreadCrumbs`, `Panes` and `Modules` — plus the read-only computed
`TabType` (`L406`), `FullUrl` (`L412`) and `IsAdminTab` (`L435`).

**Target behaviour.** None of these produces a domain member.

**Why the difference is deliberate.** `IPropertyAccess` existed solely for the
token-replacement subsystem, which the system boundaries exclude. The five
`ModuleInfo` properties and the three `TabInfo` lists were Web Forms rendering
state: there is no server-side rendering in the target, navigation is the Angular
router's concern, and a page's placements are reached through the typed
`Tab.TabModules` collection. `IsAdminTab` is the sharpest case — its getter reached
the portal settings, then the cache, then the portal controller, so **reading a
property issued a database query**, which a domain entity may not do.

**Where it is asserted.**
`Module_OmitsTheMembersTheLegacyClassFlattenedIntoOneRow` and
`Tab_OmitsTheWebFormsRenderStateAndTheComputedNavigationMembers`.

### Every XML serialisation attribute is dropped from the module and page entities, and nothing replaces it

**Legacy behaviour.** `ModuleInfo` was `<XmlRoot("module", IsNullable:=False)>`
with an `<XmlElement>`, `<XmlArray>` or `<XmlIgnore>` on all 58 members, and
`TabInfo` was `<XmlRoot("tab", IsNullable:=False)>` with the same treatment across
all 36, because portal templates were serialised straight off the entity.

**Target behaviour.** No entity in the module aggregate carries an attribute of any
kind — not a serialisation attribute, not a JSON replacement, not a validation
attribute and not a persistence attribute. The wire contract belongs to the
Application DTOs and the mapping to the Infrastructure entity configurations.

**Why it is a compile-time fact rather than a convention.** The Domain project
declares no package reference at all, so a persistence or validation attribute is
unavailable there by construction.

**Where it is asserted.**
`ModuleAggregate_CarriesNoSerialisationOrValidationAttributes`, which filters the
compiler-emitted `System.Runtime.CompilerServices.*` attributes that enabling
nullable reference types stamps onto every reference-typed member.

### Two sibling classes over two identically seeded tables disagreed about "no key yet", and neither convention is adopted

**Legacy behaviour.** `dbo.Modules.ModuleID` and `dbo.Tabs.TabID` are both declared
`IDENTITY(0, 1)` — `01.00.00.SqlDataProvider:L221` and `:L140` respectively — so
zero is a real, persisted key for each. Yet the two classes over those tables
represented "not saved yet" differently. `ModuleInfo`'s constructor seeded
`_ModuleID = Null.NullInteger` (`L108`), while `TabInfo`'s constructor
(`L86-L105`) seeded sixteen fields but **not** `_TabID`, leaving it at `0` — which
made a freshly constructed page indistinguishable from the first real page of the
installation.

**Target behaviour.** Neither convention is adopted. No entity carries a sentinel
identity initialiser, and whether a row exists is **declared** by the persistence
layer through `Entity<TId>.MarkIdentityPersisted` and read back through
`IdentityIsPersisted`. Until that declaration, two separately constructed entities
are two entities and only their references distinguish them.

**Why no value test could work.** Every candidate marker for "no row yet" is a real
key: `Portals.PortalID` seeds at `-1`, and `Roles.RoleID`, `Tabs.TabID` and
`Modules.ModuleID` each seed at `0`. An `IsTransient()`, `IsNew` or
`Identity == default` predicate would report the first module, the first page and
the first portal of every installation as unsaved. `dbo.ModuleDefinitions`,
`dbo.DesktopModules` and `dbo.PortalDesktopModules` seed at `1` instead, so the
decision is genuinely per column and no single value rule spans the aggregate.

**Preserved, not corrected.** The asymmetry itself is recorded as knowledge rather
than repaired in the legacy tree, per Minimal Change Clause item 1.

**Where it is asserted.**
`Tab_ZeroIsARealPageAndTheLegacyConstructorCouldNotSayOtherwise`,
`IdentitySeeds_DifferPerTableSoZeroIsAKeyForSomeTablesAndNotOthers` and
`Entities_DeclareNoWayToDeduceWhetherARowExistsFromItsKey`.

### `ModuleInfo._DesktopModuleID` defaulted to `0` while its five sibling identifiers defaulted to `-1`

**Legacy behaviour.** The constructor at `ModuleInfo.vb:L102-L125`, under the
author's own comment "initialize the properties that can be null in the database",
seeded `_PortalID`, `_TabID`, `_TabModuleID`, `_ModuleID` and `_ModuleDefID` with
`Null.NullInteger`. A sixth identifier field, `_DesktopModuleID` (declared at
`L72`), was **not** seeded and therefore started at `0` — a value that is a
legitimate key elsewhere in the schema. One class thus treated five identifiers as
absent at `-1` and a sixth as absent at `0`.

**Target behaviour.** Annotated in place and not fixed, per Minimal Change Clause
item 1 — and there is nothing to fix, because `DesktopModuleID` is not a
`dbo.Modules` column. It belongs to `dbo.ModuleDefinitions` and appears on
`ModuleDefinition`, so the four-way split removed the surface the inconsistency
lived on and it is unobservable in the target.

**Where it is asserted.**
`Module_PreservesTheLegacyDesktopModuleIdInconsistencyAsKnowledgeOnly`.

### `Tab.IsVisible` follows the store default, not the legacy object default

**Legacy behaviour.** `TabInfo`'s constructor left `_IsVisible` unseeded, so a
legacy in-memory page started **hidden**, while any row inserted without naming the
column started **visible**: `DF_Tabs_IsVisible DEFAULT (1)` at
`01.00.00.SqlDataProvider:L497`, reasserted at `03.01.01.SqlDataProvider:L1286`.

**Target behaviour.** `Tab.IsVisible` is initialised to `true`, reproducing the
store default.

**Why the store wins here.** A store default is only reached for a column omitted
from the insert, and a `bool` cannot distinguish "not supplied" from "explicitly
false", so expressing the default in the mapping instead would silently store a
request for a hidden page as visible. This is the same object-versus-store
disagreement already recorded for `TabModule.DisplaySyndicate`, resolved in the
opposite direction and for that reason.

**Where it is asserted.** `Tab_IsVisibleAndUndeletedByDefault`.

### `SecurityAccessLevel` and `TabType` have no target enumeration

**Legacy behaviour.** `ModuleInfo.ControlType` was typed `SecurityAccessLevel`,
declared in `Library/Components/Security/PortalSecurity.vb` as
`Public Enum SecurityAccessLevel As Integer` with explicit negative members:
`ControlPanel = -3`, `SkinObject = -2`, `Anonymous = -1`, `View = 0`, `Edit = 1`,
`Admin = 2`, `Host = 3`. `TabInfo.TabType` was typed `TabType`, declared at
`TabInfo.vb:L32-L38` with implicit ordinals `File = 0`, `Normal = 1`, `Tab = 2`,
`Url = 3`, `Member = 4`, so `default(TabType)` was `File` rather than `Normal`.

**Target behaviour.** Neither type exists in `backend/src/DnnMigration.Domain/Enums/`.
`ModuleControl.ControlType` is carried as the plain `int` the column declares, and
`TabType` has no counterpart at all because it was never stored — it was computed
from the page's URL.

**Why nothing is fabricated.** Naming an enumeration the technical specification
never listed would be an invention, and the measured legacy values are recorded
here and inline instead. `Anonymous = -1` is worth stating explicitly: it is a
further distinct meaning of `-1` in this domain, alongside the generic integer
sentinel, a real portal key, the all-users role identifier, a root page's parent
and the capability-mask guard. Because the legacy sentinel convention treated the
numerically lowest enumeration member as the absent value, an enumeration here
would have made `ControlPanel(-3)` the absent access level rather than `View(0)` —
an accident an untyped integer cannot cause.

**Where it is asserted.** `ModuleControl_AccessLevelStaysAStoredInteger` round-trips
all seven measured values through the integer property, and
`DomainEnums_DeclareNoTypeForTheTwoLegacyEnumerationsThatStayedBehind` asserts the
absence of both types by assembly lookup, so adding either one later is a
deliberate decision that must revisit that test.

### `PortalDesktopModuleInfo.FriendlyName` and `PortalName` were join projections, not columns

**Legacy behaviour.** The class declared five properties, but
`dbo.PortalDesktopModules` has three columns.
`02.02.02.SqlDataProvider` creates it with `PortalDesktopModuleID`, `PortalID` and
`DesktopModuleID` and nothing else, while its `GetPortalDesktopModules` procedure in
the same script selects `PortalDesktopModules.*, PortalName, FriendlyName` across
joins to `Portals` and `DesktopModules`. The legacy reflection hydrator filled all
five properties indiscriminately, which is how a result-set shape came to be
mistaken for an entity shape.

**Target behaviour.** `PortalDesktopModule` declares the three columns only.
Neither name may be added back as a scalar, because a scalar would be a column the
table does not have. Their only legacy purpose was display text for an
administrator's picker, so they belong on an Application-layer DTO composed from
the entity's `Portal` and `DesktopModule` references.

**Where it is asserted.** `PortalDesktopModule_CarriesExactlyTheThreeTableColumns`,
which also shows the display name still reachable through the principal.

### The legacy empty-string sentinel is not reinstated as a property initialiser

**Legacy behaviour.** `Null.NullString` is the **empty string**, not `Nothing`, so
once a value was read back a SQL `NULL` and a zero-length string were
indistinguishable. `ModuleInfo.vb:L110-L121` seeded ten string members with it, and
`TabInfo.vb:L86-L105` seeded eleven.

**Target behaviour.** Absence is `null`; no string property in the module aggregate
carries a `string.Empty` initialiser, and none may acquire one. A value of
`string.Empty` that a caller or a row genuinely supplies is preserved exactly and is
never collapsed to `null` — which is what keeps "stored, but blank" recordable,
notably in the two settings stores, whose value columns are `NOT NULL`.

**Where it is asserted.** `Module_DoesNotReinstateTheLegacyEmptyStringSentinel`,
`Placement_ChromeStringsAreNullWhenUnsetAndPreserveAStoredEmptyString`,
`Tab_ReplacesTheSixteenSentinelInitialisationsWithNullability` and
`SettingValues_KeepTheEmptyStringRatherThanBecomingNull`. The date half of the same
rule is asserted by
`Module_ExpressesDateAbsenceAsNullRatherThanAsTheLegacyMinimumDate`, which also
records that the legacy absence test compared **only the date part**, so any time of
day on `DateTime.MinValue` read as absent; that truncation is not reproduced.
