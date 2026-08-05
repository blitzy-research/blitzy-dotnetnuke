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

### A role can be renamed, and a colliding rename is refused rather than faulted

**Legacy behaviour.** The legacy edit screen could not rename a role, and five
measurements agree on it. `Website/admin/Security/EditRoles.ascx.vb:L131-L134`
reveals a read-only label, hides the name text box and disables the screen's only
required-field validator whenever an existing role is being edited; its save block
nevertheless assigns that hidden box at `:L237`, and a hidden Web Forms control
posts nothing, so the value assigned on every edit was the empty string. That store
was dead, because the layers beneath had nowhere to put it: the membership data
contract declares no name parameter on its update member
(`Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L97`), the
provider implementing it never passes one (`DNNRoleProvider.vb:L325`), and the
terminal `UpdateRole` procedure omits the column from its assignment list
(`04.00.04.SqlDataProvider:L454-L488`), having carried it as recently as
`02.00.00.SqlDataProvider:L4317`. Consistently with all of that, the screen applied
its portal-scoped uniqueness guard on the **insert** branch alone
(`:L251-L257`, with no equivalent at `:L259-L261`).

**Target behaviour.** `PUT /api/v1/roles/{roleId}` carries a
required `roleName` and applies it, so a role can be renamed. The same
portal-scoped uniqueness read the legacy insert branch performed is applied on this
path, **excluding the role being edited**, and a collision is reported as
`role.name_duplicate`, which the shared status table answers as `409 Conflict` -
the identical outcome the creation path already produced. Resubmitting a role's own
current name is therefore a no-op rather than a self-collision.

**Why the difference is deliberate.** Two facts decide it. The library-level member
this application service replaces takes the whole role **including its name** on
update - `Library/Components/Security/Roles/RoleController.vb:L254` is
`Public Sub UpdateRole(ByVal objRoleInfo As RoleInfo)` - so a name has always
travelled at the boundary the service layer occupies. And the terminal schema
constrains the pair: `03.00.09.SqlDataProvider:L304` adds
`UNIQUE NONCLUSTERED ([PortalID], [RoleName])`, which is a data-model fact this
migration is required to honour. Without the guard on the update path a rename onto
an existing name would violate that constraint at the provider and reach the caller
as a server fault naming no field, which is strictly worse than a conflict it can
correct. A replacement contract that could not express the resource's own name would
also be dishonest about being a replacement.

**Operational consequence.** A caller amending one field must resubmit the name it
read, exactly as it must resubmit every other member of a replacement contract; an
omitted name is a field-level `400`, not a silent preservation. The legacy asymmetry
in which only insertion guarded uniqueness is closed rather than reproduced, and no
submission the legacy screen could produce behaves differently, because that screen
could only ever have sent the stored name or an empty one.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Dtos/Role/UpdateRoleRequest.cs`,
`backend/src/DnnMigration.Application/Validation/UpdateRoleRequestValidator.cs`,
`backend/src/DnnMigration.Application/Mapping/RoleMappings.cs`,
`backend/src/DnnMigration.Application/Services/RoleService.cs`,
`backend/src/DnnMigration.Application/Abstractions/IRoleService.cs`,
`backend/src/DnnMigration.Api/Controllers/RolesController.cs`.

### The invented ceiling on a role's billing and trial period is removed

**Legacy behaviour.** Each period carried exactly one rule, and only one:
`valBillingPeriod2` at `editroles.ascx:L114` and `valTrialPeriod2` at `:L146` both
compare **greater than zero**. The columns are plain `int`
(`01.00.08.SqlDataProvider:L6829` and `01.00.05.SqlDataProvider:L2754`), the terminal
`UpdateRole` procedure bounds neither, and no configuration key bounded them either.
Every positive `Int32` was accepted.

**Target behaviour.** Every positive `Int32` is accepted again. A net-new
ten-thousand-unit ceiling had been introduced on both role write validators and is
withdrawn; the strictly-positive rule the legacy screen declared is what remains, with
its measured wording preserved.

**Why the difference is deliberate.** The ceiling was justified as generous, and
generosity is not the test - domain-logic preservation is. It refused a band of values
the legacy application accepted, which is a functional reduction rather than a
hardening. Nothing is left unprotected by its removal: `DeriveAssignmentDates` in
`RoleService` routes every offset through clamping helpers, so a period large enough to
overflow the date arithmetic yields the storable bound instead of a wrapped or faulted
expiry. That guarantee is the helpers' own and always was, which is precisely why the
field rule was not what kept it.

**Operational consequence.** A period the stored calendar cannot accommodate produces a
**clamped** expiry rather than a field-level refusal. That is the legacy outcome for the
same input, and it is asserted directly by
`RoleServiceTests.Assign_ClampsATermThatOutrunsTheStoredCalendar` for `int.MaxValue`.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/CreateRoleRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/UpdateRoleRequestValidator.cs`,
`backend/src/DnnMigration.Application/Services/RoleService.cs`.

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

- **The shipped allowed-origin list contains exactly
  `http://localhost:4200`, and credentials are never enabled.** That explicit
  local-development origin is declared in `appsettings.json`; compose supplies
  the same value by default and a deployment may replace it with its own exact
  origin. If configuration deliberately clears the list, the registered policy
  contains no origins and therefore refuses every cross-origin caller. The
  containerised topology still needs no cross-origin access:
  `docker/nginx.conf` proxies `/api/` to the API service, so the browser
  addresses the API through the same origin that served the application.
- **The forwarded protocol and client address ARE honoured; the forwarded host is
  not.** TLS terminates at the browser-facing edge and the API is reached over
  plain HTTP on an internal network, so the caller's address and the scheme the
  browser actually used arrive as headers rather than as properties of the
  connection. `UseForwardedHeaders()` is therefore installed in the pipeline
  immediately after the exception handler - ahead of transport security, the
  correlation and request-logging stages, and the credential rate limiter, each of
  which reads one of those two values. Without it the limiter partitions every
  caller behind the proxy into one shared budget, so a single caller can exhaust
  the credential allowance for everybody, and the transport check cannot see that a
  request reached the edge in clear text. The forwarded **host** is excluded from
  the honoured set, because tenant resolution reads the request host and trusting a
  caller-supplied forwarded host would let a caller select which tenant to be
  served - a direct cross-tenant hazard. `docker/nginx.conf` sets that host itself,
  from `$http_host`, so the port survives into the alias lookup.
  **Trust is explicit and narrow.** With no configuration the framework trusts the
  loopback address alone, which inside a container means the headers are ignored
  and the stage is inert, so `appsettings.Production.json` supplies
  `Proxy:KnownNetworks` as the address range a container runtime allocates its
  bridge networks from (`172.16.0.0/12`) and the hop limit stays at one. A
  deployment on a named network, an overlay network, or an orchestrator that
  assigns addresses from elsewhere replaces that entry with its own; the narrower
  it is, the less a caller reaching the API directly can claim. A deployment that
  publishes the API port to the public internet should stop doing so - the proxy is
  the only intended path - because a directly reachable caller appears as the
  bridge gateway and would then fall inside the trusted range.
- **HTTPS redirection is enforced in production, with two named exemptions, and
  HSTS is on outside development.** The redirect is branched rather than blanket:
  it is withheld from the anonymous `/health` endpoint, which answers a container
  health probe over plain HTTP before any credential exists and which the compose
  topology waits on before starting the frontend, and from a loopback-addressed
  request, which never traverses a network and so has nothing for a redirect to
  protect - the same carve-out the framework's own strict transport security makes
  by default. Every other request, meaning every request addressed by a real host
  name, must arrive over HTTPS or be redirected until it does; that is what makes a
  directly reachable plain-HTTP API port unusable for credentials rather than
  merely discouraged. The target port is configured (`Https:Port`, default 443)
  because the redirection stage otherwise cannot build a target authority and
  **logs once and forwards the request unchanged** - a transport control that
  silently does nothing. The status code is the framework default 307 rather than a
  permanent redirect, so a deployment that has to answer over plain HTTP again
  during a certificate replacement is not left unreachable by cached answers.
- **Transport security that is switched off outside development is announced at
  startup.** A deployment serving production traffic with neither redirection here
  nor a TLS-terminating proxy in front carries credentials in clear text while every
  probe still answers 200, so the composition logs a warning on every start rather
  than failing to start - refusing would make the correct proxied deployment
  impossible.

**Operational consequence.** Browser traffic is HTTPS-only, and a deployment
reached at a real host name **must** provide TLS at the edge - either by fronting
the SPA container with a TLS-terminating ingress, or by mounting a server fragment
into `/etc/nginx/transport-policy/` that adds a TLS listener and certificate paths
to the shipped nginx server. Without either, every browser request is answered
with a redirect the browser cannot satisfy, which is the intended loud failure
rather than a silent downgrade to clear text. Certificates are never committed and
never baked into an image. A deployment that terminates TLS *at the API* instead
must additionally keep the health endpoint reachable over plain HTTP, which the
pipeline's exemption already guarantees. A deployment that serves the SPA from a
different origin than the API must add that origin to the allowed list. The base
configuration file names exactly one origin, the local development origin
`http://localhost:4200`, and no other; the shipped container topology overrides
even that from the compose file, and the policy never enables credentials, so the
wildcard-plus-credentials combination is unreachable from configuration at all.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Extensions/CorsExtensions.cs`,
`backend/src/DnnMigration.Api/Extensions/ApplicationBuilderExtensions.cs`,
`backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`,
`docker/docker-compose.yml`, `docker/nginx.conf`,
`docker/nginx.tls.conf.example`, `docker/.env.example`. The base
`appsettings.json` and `appsettings.Production.json` are strict RFC 8259 and
therefore carry no inline annotation; their contract is recorded under
*Configuration and options* instead.

**Proved by.** `backend/tests/DnnMigration.IntegrationTests/Api/TransportSecurityTests.cs`
- a cleartext API request is redirected, the health probe is not, a request
forwarded as https by a trusted hop is served rather than redirected, and the same
header from an untrusted hop is ignored.

### Content-security headers, and the TLS termination the proxy can now perform

**Legacy behaviour.** The legacy application emitted no security response headers
of any kind. There was no content security policy, no framing policy and no
transport policy, because ASP.NET 2.0 shipped none and the application added none.

**Target behaviour.** `docker/nginx.conf` - which AAP 0.9.6 makes the home of the
"content-security headers" requirement - now emits five response headers on every
response, on the document, on the static assets and on the SPA fallback alike:
`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`,
**`Content-Security-Policy`** and **`Strict-Transport-Security`**. Four points are
worth recording:

- **The policy is written to the bundle this image actually serves.**
  `script-src 'self'` with no `'unsafe-inline'` and no `'unsafe-eval'`, because the
  Angular production build emits external hashed bundles and no inline script -
  that directive is what stops an injected `<script>` from executing.
  `style-src 'self' 'unsafe-inline'` is unavoidable and measured rather than
  assumed: Angular injects component styles as inline `<style>` elements at run
  time, so a policy without it renders the application unstyled. Inline *style* is
  a far narrower concession than inline *script*, and it is confined to that one
  directive. `connect-src 'self'` is sufficient only because the production
  `apiBaseUrl` is the relative `/api/v1` proxied by this same origin.
- **Transport security is emitted only over TLS.** The header is meaningless on a
  plain-HTTP response, so its value is mapped from `$scheme` and nginx omits an
  `add_header` whose value is empty. The same configuration is therefore correct
  both for the plain-HTTP listener gate 7 probes and for a TLS server, and
  mounting a certificate is what starts the policy. `preload` is deliberately
  excluded: it commits an entire domain, including sibling hosts, and that is an
  operator's decision about a domain rather than a container's about itself.
- **Both values are declared once, in `map` directives, and referenced.** nginx's
  inheritance rule makes an `add_header` inside a `location` *replace* the
  server-level set, so every location that sets a header of its own must restate
  the others; restating a policy string by hand three times is how three copies
  drift apart.
- **TLS can now be terminated in the delivered proxy.** The `http` block ends with
  `include /etc/nginx/tls.d/*.conf;`. A wildcard include matching nothing is not an
  error, so the shipped image starts with no certificate - which is what keeps the
  plain-HTTP gate working - and the *same image* serves HTTPS the moment a
  deployment mounts `docker/nginx.tls.conf.example` alongside a certificate and
  key. A `listen 443 ssl` directive naming an absent certificate makes nginx fail
  to **start**, not to serve, which is why the TLS server is a mounted file rather
  than a block in the main configuration. No certificate, key or passphrase is
  committed to this repository.

**What is deliberately still absent.** No cross-origin response header, because
every request is same-origin through the proxy; no rate limiting, which belongs to
the API; no health location, because the API's probe is anonymous on its own port
and is probed directly.

### The permitted delta from the supplied container examples

**The conflict, stated plainly.** AAP 0.9.3 says the four supplied container
artefacts are reproduced *verbatim* except for the two name placeholders. AAP
0.9.6 separately assigns non-functional requirements to two of those files that
their supplied text does not express - content-security headers to
`docker/nginx.conf`, and a hardened transport posture to the topology - and the
supplied runtime base cannot start this application unchanged. Both statements
cannot be literally true at once. Rather than leave that for a reader to
discover, the delta is **closed and itemised**, in each file's own header and
here.

**`docker/api.Dockerfile`.** Everything the example specifies is present and
unchanged: both Alpine bases, the UID 1000 non-root account,
`ASPNETCORE_URLS=http://+:8080`, `EXPOSE 8080`, the wget `HEALTHCHECK` against
`/health`, and `ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]`. Three additions:
`apk add icu-libs icu-data-full` paired with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`, without which
`Microsoft.Data.SqlClient` cannot open a connection and the container fails its
own health probe; build-stage layer ordering that restores before copying sources,
which changes cache efficiency and nothing else; and a `chown` of `/app` before the
account switch, without which the published output is unreadable to the account the
example itself mandates.

**`docker/nginx.conf`.** Everything the inventory names is present and unchanged:
`worker_connections 1024`, the SPA fallback, the `/api/` proxy with its six
forwarded headers, and the one-year immutable asset policy. Four additions: the
security headers required by 0.9.6; the optional TLS include; an access-log format
that omits the query string and the caller's metadata; and the `events` block, the
`mime.types` include and the IPv6 listener, each required for nginx to start or to
serve correctly in this image.

**`docker/docker-compose.yml`.** The two services, their images, the health
condition and the SPA's `4200:80` publication are unchanged. Two additions, both
security-motivated and both annotated in the file: the API port is published on the
**loopback interface** rather than on every interface, and `Https__RedirectEnabled`
is set to `false` **explicitly**, because the API now enforces HTTPS by default and
this topology is plain HTTP by gate-7 mandate. The opt-out is visible in the
topology that needs it instead of being the shipped default for every deployment.

**`docker/nginx.tls.conf.example`.** A new file, not a modified one. It is the
mountable TLS server block described above, and it is what makes "terminate TLS in
the delivered proxy" a configuration change rather than a code change.

**Deployment configuration is never baked into an image.** The connection string,
the signing key, the permitted browser origin, the trusted-proxy list and the
transport switches all arrive as environment variables from compose or from the
orchestrator. That division is what keeps every tracked file free of secrets while
still letting a deployment be configured.

### What may reach a log: exception objects, provider errors, personal data and control characters

**Legacy behaviour.** The legacy application's audit trail wrote a portal name, a
user name, a user identifier and an event type into an `EventLog` table, and its
exception plumbing lived at HTTP-module and page level. There was no request log,
no readiness probe and no redaction of any kind.

**Four defects are closed here, and each one had the same shape: a value that was
carefully kept out of one log reached another one anyway.**

- **The request log no longer receives the exception object.** `RequestLoggingMiddleware`
  used to pass the escaped exception to the logger, which records its message, the
  message of every inner exception and the stack trace. A message is not always
  authored text - a provider quotes the statement or the connection it failed on, an
  argument fault quotes the value that was rejected, a serialisation fault quotes the
  payload - so the highest-volume log the application writes could carry a
  connection string, a bound credential or one tenant's data. `GlobalExceptionHandler`
  already logs the same failure through an allow-list that admits type names and
  stack traces and drops every message, so passing the object added no information
  and defeated that redaction from a second call site. The entry now carries a
  `FailureType` property holding the exception TYPE CHAIN only, computed from
  `Type.FullName` and nothing else, bounded at five positions. The stack trace is
  deliberately not repeated: the global handler records it once.
- **The readiness probe no longer carries the provider exception, and no longer
  reports cancellation as a dependency failure.** `DatabaseHealthCheck` attached the
  raised exception to its unhealthy result on the stated ground that the
  infrastructure could then log it privately - but "privately" was not a property of
  the result: the health-check infrastructure logs that exception, messages and all,
  and a connection failure's message routinely quotes the server, the database, the
  login and the network error underneath. That published deployment topology and
  account names through the one endpoint that answers anonymously and is polled
  several times a minute for the life of the deployment. The result now carries fixed
  authored text and no exception, and the probe writes the sanitized TYPE CHAIN
  itself - which is the part that distinguishes a login failure from a
  name-resolution failure from a timeout. Separately, the single `catch (Exception)`
  swallowed `OperationCanceledException`, so a probe deadline, a disconnected caller
  or a **graceful shutdown** was reported as "the database is unavailable": the last
  verdict a stopping application published blamed its store for a fault that had not
  occurred, and a report produced by abandoning the attempt says nothing about the
  store in any case. Cancellation of the supplied token now propagates, which is also
  what the health-check contract asks of a check that is given a token. A
  cancellation raised for any other reason - a provider's own internal timeout - is a
  genuine fault and is still reported.
- **The tenant-installation audit no longer copies personal data or caller free text
  into the general log.** The record carried the administrator's first name, last
  name, user name and email address, plus the caller's description and keywords. Two
  problems followed. Personal data was copied into a store chosen for diagnostics
  rather than records management, whose retention this codebase does not control and
  which a subject-access or erasure request cannot reach; and the two free-text
  members were caller-shaped, bounded in length by the validators but not in content.
  What is recorded now is the portal identifier and name, the alias, whether it is a
  child portal, the administrator's USER IDENTIFIER, and whether each free-text
  member was supplied. The identifier is the stable key that resolves to the name and
  address in the store whenever an operator legitimately needs them, which is the
  difference between a record that points at a person and a record that copies them;
  the event's own subject identifier carries it too, so "who can now sign in to this
  tenant" remains answerable. An audit trail that legally requires personal data
  belongs in a dedicated, access-controlled store with its own retention policy, and
  adding one is a deployment decision rather than a side effect of porting a legacy
  entry.
- **No rendered property can break out of its own line.** `LoggingAuditSink` renders
  an event's properties as a `key=value` list, and rendering to text is the moment a
  control character stops being data and becomes structure: a value carrying a
  carriage return and a line feed produces something a reader, a log shipper and a
  detection rule all parse as an additional, forged record. Every key and every value
  is now passed through a sanitiser that replaces each control character with a
  visible placeholder. The test is `char.IsControl` rather than a list of newline
  characters, because a vertical tab, a form feed, a NEL and the ANSI escape that
  repaints a terminal are all controls and a hand-written list would age badly.
  Characters are replaced rather than removed so that two different submitted values
  cannot render identically, and printable text is left exactly as submitted - this
  is a sanitiser, not an encoder, because a record whose text no longer matches what
  was submitted is a record an auditor cannot rely on. Fixing it at the renderer is
  what makes it true for every property of every event, rather than for the call
  sites somebody remembered.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Middleware/RequestLoggingMiddleware.cs`,
`backend/src/DnnMigration.Infrastructure/HealthChecks/DatabaseHealthCheck.cs`,
`backend/src/DnnMigration.Infrastructure/Services/LoggingAuditSink.cs`,
`backend/src/DnnMigration.Application/Services/PortalService.cs`.

**Proved by.**
`backend/tests/DnnMigration.UnitTests/Security/AuditLogRenderingTests.cs`,
`backend/tests/DnnMigration.UnitTests/Security/DatabaseHealthCheckTests.cs`,
`backend/tests/DnnMigration.UnitTests/Services/PortalServiceTests.cs` and
`backend/tests/DnnMigration.IntegrationTests/Api/AuditTrailContractTests.cs`, the
last of which reads what the configured sink actually recorded and asserts that
none of the withheld values appears in it.

### A content security policy is served to the browser, and the production build was changed so its script directive needs no exception

**Legacy behaviour.** None. The legacy portal predates the content security policy
entirely and emitted no response-header security policy of any kind.

**Target behaviour.** `docker/nginx.conf` emits this policy on the document, on
every hashed static asset and on proxied API responses:

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';
img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none';
frame-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'
```

It is defined once, in an `http`-context map, and referenced by variable from all
three header sets - because nginx **replaces** rather than merges an `add_header`
set in a location, so a location declaring a `Cache-Control` of its own must repeat
every security header, and three literal copies of a long policy string are three
copies that can drift. A map also always resolves to its default, so the header
cannot vanish through a phase-ordering mistake, which an unset `set` variable
could.

**Why `script-src` carries no exception, and what that cost.** The Angular
production build enables critical-CSS inlining by default, and that optimisation
emits two things into the document: an inline `<style>` block, and
`<link rel="stylesheet" … media="print" onload="this.media='all'">` - an inline
event-handler attribute, which a content security policy governs under `script-src`.
Under `script-src 'self'` that handler is blocked, the deferred stylesheet never
becomes `media="all"`, and the application renders with only its critical styles
**while still returning 200** - a failure a status-code probe cannot see. The two
alternatives were `script-src 'unsafe-inline'`, which defeats the directive
entirely, and `'unsafe-hashes'` plus a SHA-256 of a builder-generated attribute,
which couples the proxy configuration to an Angular implementation detail and
degrades silently if the builder's text ever changes. Instead
`frontend/angular.json` disables the optimisation for the production configuration
(`optimization.styles.inlineCritical: false`), which removes both artefacts:
the built document now contains zero inline `<style>` elements and zero inline
handlers, so the script directive needs no exception at all. The cost is one
first-paint optimisation on an internal administration console.

**The one exception, and why it is unavoidable.** `style-src` permits
`'unsafe-inline'`. Angular injects each component's styles at run time as a
`<style>` element, which a content security policy counts as an inline stylesheet;
without the exception every component loses its styling. Removing it requires a
per-response nonce threaded into the document and the framework's nonce attribute,
which needs a templated index document this image does not serve. Inline style
cannot execute script, so the exception does not weaken the script directive.

**`upgrade-insecure-requests` is deliberately absent.** It would rewrite every
subresource request of a plain-HTTP document to HTTPS, which on the exempted
loopback path means rewriting them to a port nothing is listening on. The redirect
described above is the right tool for that job and already applies.

**Verified, not assumed.** The policy was checked against the real bundle in a
browser: it is delivered in enforcing mode, the console is silent, and every module
chunk and the stylesheet load with status 200 while the application renders fully
styled. A positive control confirmed the policy actually blocks - an injected
inline script, an off-origin script, a string-compiled timer and an off-origin
fetch were each refused, while the application's own same-origin API call reached
the network.

**Annotated in code at.** `docker/nginx.conf`, `frontend/angular.json`.

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
`backend/src/DnnMigration.Application/Validation/LoginRequestValidator.cs`,
`backend/src/DnnMigration.Application/Services/AuthService.cs`, which is the surface the
removed CAPTCHA gated and which names this control as its replacement.

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

### CORRECTION: the bounded first-login legacy credential migration is implemented

**This entry supersedes two earlier corrections in this append-only record.** The sections titled
“Correction: the credential migration path is an administrative reset” and “Clarification: a stored
credential IS replaced on sign-in when its work factor is superseded” accurately described an
intermediate implementation that had no legacy verifier. They no longer describe the delivered
system. The frozen action plan requires **re-hash on first successful legacy login, with
administrative reset as fallback**, and that primary path now exists.

**Delivered path.** The credential read now carries the membership row’s stored representation,
`PasswordFormat` discriminator and `PasswordSalt`. `AuthService` first performs one current-cost
BCrypt comparison on every structurally valid attempt, using the existing unmatchable decoy for a
legacy row so that the transition does not reintroduce an account-timing oracle. If the stored value
is not BCrypt, `ILegacyPasswordVerifier` may verify the submitted credential. A successful legacy
answer is replaced immediately through `IPasswordHasher.Hash` and
`IUserRepository.SetPasswordHashAsync`; the row is rewritten as format `Hashed` with an empty
external salt because BCrypt embeds its own. The caller receives the ordinary successful login and
the next login uses only BCrypt.

**Compatibility is isolated and expires.** The legacy verifier is a separate Infrastructure
singleton rather than a branch on the permanent BCrypt hasher. It is disabled by default and
requires three deployment settings to be supplied together: an enable switch, an absolute UTC
deadline and the legacy decryption key. The key has no functional default and is supplied through
the deployment secret store; it appears in no tracked application settings file, compose value, log,
audit event, exception message or response. The deadline is absolute rather than relative to process
start, so restarting the API cannot renew the window. Disabled, expired, malformed and unsupported
states fail closed. After the deadline, or when a row cannot be verified, the existing
administrator-reset operation remains the fallback.

**Legacy formats covered.** The bridge recognises the three persisted ordinals retained in
`PasswordFormat`: clear text for the earliest schema, salted SHA-1 for the one-way membership
format, and the installation’s encrypted membership format. The encrypted compatibility shape was
validated independently against `System.Web.Security.SqlMembershipProvider` in a Mono 6.12 runtime:
the provider prepends a random eight-byte IV and encrypts the sixteen-byte salt plus UTF-16LE
credential bytes using Triple-DES/CBC with PKCS7 padding. The verification-only implementation is
confined to the expiring bridge; no target write uses any legacy format and password readback remains
impossible.

**Failure and audit behaviour.** A successful replacement emits
`LEGACY_CREDENTIAL_MIGRATED` with the tenant, account and former format only. It never records the
submitted password, stored value, salt, BCrypt replacement or deployment key. A transient failure to
store the replacement does not turn a credential already proved correct into a refusal; it emits the
closed `LegacyCredentialMigrationFailed` security diagnostic so an operator can act before the
deadline. Administrative reset remains enabled for exactly that contingency.

**Configuration and container contract.** `appsettings.json` declares the migration section disabled
with an empty key and no deadline. `docker-compose.yml` maps the three optional environment variables,
and `docker/.env.example` documents how to source the key from a managed secret store without
committing it. Startup validation requires a zero-offset deadline and the exact hexadecimal key
shape and rejects a cryptographically weak key before serving traffic.

**Annotated in code at.**
`backend/src/DnnMigration.Domain/Abstractions/Services/ILegacyPasswordVerifier.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Services/IPasswordHasher.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IUserRepository.cs`,
`backend/src/DnnMigration.Application/Options/LegacyCredentialMigrationOptions.cs`,
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Infrastructure/Security/LegacyPasswordVerifier.cs`,
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/MembershipStore.cs`,
`backend/src/DnnMigration.Infrastructure/Repositories/UserRepository.cs`,
`backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`,
`backend/src/DnnMigration.Api/appsettings.json`,
`docker/docker-compose.yml`,
`docker/.env.example`.

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

**The production restore now carries the same controls.**
`docker/api.Dockerfile` copies `NuGet.Config`, all six manifests and all six
`packages.lock.json` files before its restore layer, and runs the solution-wide
restore with the repository configuration and locked mode explicitly selected.
The later publish still suppresses restore. A package identity outside the
source map, a changed transitive version or a manifest/lock mismatch therefore
fails the container build instead of silently changing the production graph.

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
- **Writes are now bounded and tenant-checked before they reach the settings
  rows.** The legacy property editor supplied only the declared discriminator
  choices but performed no server-side range check, page-size bound, pattern
  compilation check or tenant check on the three redirect page identifiers. The
  target validator closes the three discriminator sets, requires a page size
  from 1 through 100, bounds the raw display-name format at 128 UTF-16 code units,
  and bounds and compiles the email expression under a timeout. The application
  service resolves each non-null redirect page and accepts it only when its
  `PortalId` equals the route portal. No sign-based shortcut is used: page key 0
  is real, an explicitly supplied -1 is looked up rather than treated as absent,
  and `null` alone means that no redirect is configured.
- **Token expansion is checked before an account row is mutated or committed.**
  A short format such as two `[USERNAME]` tokens can expand beyond the
  `nvarchar(128)` `Users.DisplayName` column even though the raw format itself is
  within 128 characters. The account update path now computes the prospective
  value first and returns a field-level failure when it exceeds 128 UTF-16 code
  units, preventing the predictable provider exception and HTTP 500 the
  unguarded path produced.

**A legacy asymmetry preserved rather than tidied.** The profile default
visibility *setting* defaults to administrators-only while the stored
profile-value visibility *column* defaults to everyone. That asymmetry is legacy
behaviour and is reproduced, not reconciled.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Dtos/User/MembershipSettingsDto.cs`,
`backend/src/DnnMigration.Application/Validation/MembershipSettingsDtoValidator.cs`,
`backend/src/DnnMigration.Application/Services/UserService.cs`.

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

### Module settings: six placement values are edited but not transported, and two member spellings are retired

**Legacy behaviour.** The legacy module-settings screen edited and persisted six
placement values in addition to the ones the modern update contract carries.
`Website/admin/Modules/modulesettings.ascx` renders the pane selector, the
alignment choice, the colour and border inputs and the print and syndication check
boxes, and `Website/admin/Modules/ModuleSettings.ascx.vb:L345` stored
`cboAlign.SelectedItem.Value` verbatim, so the "Not Specified" choice was written
as an empty string rather than as an absence. All six round-tripped through the
postback and reached the database.

**Target behaviour.** `PaneName`, `Alignment`, `Color`, `Border`, `DisplayPrint`
and `DisplaySyndicate` are all **mapped columns on the `TabModule` entity** and are
all **absent from `Dtos/Module/UpdateModuleRequest.cs`**, whose surface is exactly
sixteen properties. The client contract now declares those same sixteen and nothing
else. The six values remain **form-only state**: the module-settings screen reads
and renders them through its own view model at
`frontend/src/app/features/module/module-settings/module-settings.view-model.ts`,
and the adapter in that file drops them when it composes the request. The empty
string for "Not Specified" is still preserved in the form's own state, so the
distinction the legacy screen recorded is not lost client-side; it simply has
nowhere to go on the wire.

**Why the difference is deliberate.** The client contract previously declared all
six as optional, deprecated members. That gave every caller **compile-time
permission to send values the API discards**, which is a worse failure than a
compile error: the screen appeared to save settings that were never persisted, and
nothing in either stack reported the loss. Narrowing the contract to what the
server actually reads makes the projection gap visible at the boundary instead of
at a support desk. Closing the gap properly is a **server-side projection change**
— adding the six properties to the update contract and its service — and is not
attempted here, because widening a request contract to carry values the server
ignores would reintroduce exactly the defect being removed.

**Two member spellings retired at the same time.** `isDefaultModule` and
`allModules` were superseded spellings of `setAsDefaultSettings` and
`applyToAllModules`. Both old names were declared alongside the new ones and the
screen was emitting the **old** pair, so the two instruction flags the server reads
were never populated: naming a module as the portal default and applying its
appearance to every module both silently did nothing. The adapter now maps the form
controls — which keep their legacy names, because that is what the template and its
labels say — onto the members the server reads.

**One further correction on the same surface.** `tabId` was optional on the client
contract while the server declares it as a non-nullable `int`. An omitted member
deserialises to `0`, and `dbo.Tabs.TabID` is `IDENTITY(0, 1)`
(`01.00.00.SqlDataProvider:L140`), so `0` is a legitimate page the server cannot
distinguish from a caller who said nothing. It is now **required**, and the screen
supplies it from the state it was seeded with. `isDeleted` is supplied the same way
and for a related reason: the update is a whole-row replacement, so omitting the
recycle-bin flag would clear it as a side effect of saving an unrelated field.

**Operational consequence.** An operator editing a module's pane, alignment,
colour, border, print or syndication affordance sees the value they chose, and it is
not saved. That was already true before this change; what changes is that the
contract no longer implies otherwise. The two instruction check boxes now take
effect, where previously they did not.

**Annotated in code at.**
`frontend/src/app/core/models/module.model.ts` (the `UpdateModuleRequest` contract
note) and
`frontend/src/app/features/module/module-settings/module-settings.view-model.ts`
(the view model, the form-state shape and the adapter that crosses the boundary).

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

### Consequence of the rename: the audit log type keys are deliberately NOT changed

The rename reaches further than a rename normally would, and the mitigation is recorded here in
full because the failure mode it avoids is silent.

`Library/Components/Users/UserController.vb:80` assigns the status's `ToString()` directly to
the audit log type key:

```vb
objEventLogInfo.LogTypeKey = loginStatus.ToString
```

The member *name*, not only its value, therefore reaches the audit trail. The legacy application
writes keys such as `LOGIN_FAILURE` and `LOGIN_USERLOCKEDOUT`, so a trail accumulated before this
migration holds those strings and every query, alert and report written against it matches on
them. Letting the audit key follow the members into PascalCase would have emitted `Failure` and
`UserLockedOut` instead and orphaned all of them — without any error, since nothing fails when a
query simply stops matching.

**The two concerns are therefore kept apart.** The enumeration members are renamed for the target
language, and the audit names are pinned to the legacy strings in
`Application/Abstractions/AuditEventNames.cs` — `LOGIN_FAILURE`, `LOGIN_SUCCESS`,
`LOGIN_SUPERUSER`, `LOGIN_USERLOCKEDOUT` and `LOGIN_USERNOTAPPROVED` — with
`AuthService.AuditEventNameFor` mapping each outcome onto one. Existing consumers of the legacy
audit trail need no change, which is what "audit intent survives the change of mechanism" is
required to mean. The mapping is owned by the application layer rather than by the enumeration,
and it is held in place by
`UnitTests/Application/AuthServiceTests.AuditEventNames_PreserveTheLegacyLogTypeKeyStrings`.

**The two promoted outcomes are recorded under the outcome each was promoted from** —
`InsecureAdminPassword` as `LOGIN_SUCCESS` and `InsecureHostPassword` as `LOGIN_SUPERUSER` — with
the advisory carried on the record's `Advisory` property rather than in its name. The legacy left
no name to inherit here: it audited only two of its seven members, the failure and locked-out
members grouped at `UserController.vb:1138`, and that test ran *before* the promotion at `:1144`,
so no promoted status ever reached a legacy trail. Naming them for the sign-in that actually
occurred keeps an administrator sign-in visible as one and keeps every emitted name a string the
legacy could have written. Recording them as the failure event would have been worse than
imprecise: it would describe a caller who *was* admitted as one who was refused.

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

### `SiteSqlServer` is renamed to `ConnectionStrings:Default`, and the legacy key is not kept for compatibility

**Legacy behaviour.** The database was named twice, under one key.
`Website/release.config:L21-L26` declares a `<connectionStrings>` entry named `SiteSqlServer`
pointing at a local, file-attached SQL Server Express database, and `:L36` repeats the identical
value as an `appSettings` entry commented "kept for backwards compatability - legacy modules".
Read the two lines in place for the value itself; it is not reproduced here, because a connection
string is the shape of thing that should be quoted by reference rather than copied into a second
tracked file. Every provider registration then referred to the key by name -
`connectionStringName="SiteSqlServer"` appears on both the membership provider at `:L238` and
the data provider at `:L351`.

**Target behaviour.** One key, `ConnectionStrings:Default`, read in exactly two places:
`Infrastructure/DependencyInjection.cs:L184` through `GetConnectionString(ConnectionStringName)`
where `ConnectionStringName` is the constant `"Default"` at `:L57`, and
`Infrastructure/HealthChecks/DatabaseHealthCheck.cs:L90`. The base `appsettings.json` declares
the key as an **empty string**, and `:L186` refuses to start the host when it resolves to null or
whitespace. `SiteSqlServer` does not appear anywhere in the new configuration.

**Why.** Retaining the legacy name would have created a key nothing reads. Unconsumed
configuration is worse than absent configuration: it looks authoritative, so an operator who
edits it changes nothing and has no way to discover that from the file. The duplicate
`appSettings` copy existed only to serve third-party modules loaded by assembly probing, and that
loading mechanism is not carried forward, so the second name has no remaining consumer either.
The value is empty rather than a sample because a sample connection string in a tracked file is
how a real one eventually gets committed - `ConnectionStrings__Default` supplies it per
deployment, and `docker/docker-compose.yml` sets exactly that from `${DB_CONNECTION_STRING}`.

### The release-versus-development `objectQualifier` divergence is answered by Fluent mapping, not by a configuration key

**Legacy behaviour.** The two shipped configurations disagree about object naming.
`Website/release.config:L354` sets `objectQualifier=""` while `Website/development.config:L352`
sets `objectQualifier="dnn_"`; both set `databaseOwner="dbo"`. The qualifier was concatenated into
every object name at the call site - `SqlDataProvider.vb` builds procedure names as
`DatabaseOwner & ObjectQualifier & "<ProcName>"` - and it reached further than table names,
appearing inside generated constraint names in the upgrade scripts.

**Target behaviour.** No `ObjectQualifier` key and no `DatabaseOwner` key exist in any
`appsettings` file. Schema binding lives in the twenty-one `IEntityTypeConfiguration<T>` classes
under `Infrastructure/Persistence/Configurations/`, each pinning its table with
`ToTable("<Name>", "dbo")` and each column with `HasColumnName`.

**Why.** A configuration key is only honest if something reads it, and nothing in this
application would: Entity Framework Core resolves names when the model is built, not when a
command is composed, so a qualifier supplied through configuration would have to be threaded into
every one of the twenty-one configurations to have any effect. Declaring the key without that
plumbing would produce a setting that silently does nothing - precisely the failure mode the
legacy duplicate `SiteSqlServer` entry demonstrates. The observed release value is the empty
string, so the delivered mapping is faithful to the installation this migration targets; a
differently-qualified installation is a change to the configuration classes, which is a visible,
reviewable, compile-checked edit rather than a value in a file whose effect nobody can test.

### Provider indirection is removed, not reproduced: fourteen `defaultProvider` declarations collapse into four bound options classes

**Legacy behaviour.** `Website/release.config` declares **fourteen** independently swappable
provider families, each with a `defaultProvider` attribute and a `<providers>` list naming
concrete types and their on-disk `providerPath`: membership at `:L218`, HTML editor `:L258`,
navigation control `:L299`, search index `:L325`, search data store `:L335`, data `:L345`,
logging `:L359`, scheduling `:L374`, friendly URL `:L386`, caching `:L397`, authentication
`:L411`, members `:L419`, roles `:L427` and profiles `:L435`. Each was resolved at run time by
reflection over the assembly name in the registration.

**Target behaviour.** No `Providers` section, no `DefaultProvider` key, and no provider-path
setting of any kind. Configuration is four bound, self-validating options classes - `Jwt`,
`PasswordPolicy`, `Portal` and `Caching` - plus a small number of directly-read keys
(`Cors:AllowedOrigins`, `RateLimiting:Authentication`, `Swagger:Enabled`,
`Https:RedirectEnabled`, `Proxy:KnownProxies`, `Proxy:KnownNetworks`). Substitution, where it is
still wanted, is a container registration in the composition root.

**Why.** Nine of the fourteen families belong to subsystems this migration excludes outright -
HTML editing, navigation menus, search indexing and its data store, scheduling, friendly URLs,
caching providers, and the separate authentication and profile provider stacks - so reproducing
their configuration would describe capabilities that do not exist. The remaining families have
first-class replacements that are chosen by referencing a package and registering a service, not
by naming a type in a configuration file: dependency injection for substitution, `IMemoryCache`
behind `ICacheService` for caching, Serilog for logging, and one Entity Framework Core provider
for data. Reflection over a configured type name also defeats every compile-time check and every
static analyser, which is the specific property that made the legacy provider model expensive to
reason about.

### The authenticated-response cacheability switch is not ported

**Legacy behaviour.** `Website/Default.aspx.vb:L119-L133` read the host setting
`AuthenticatedCacheability` on every authenticated request and mapped its stored string onto the
`System.Web` client-cache policy - `"0"` to `NoCache`, `"1"` to `Private`, `"2"` to `Public`,
`"3"` to `Server`, `"4"` to `ServerAndNoCache`, `"5"` to `ServerAndPrivate` - defaulting to
`ServerAndNoCache` when the setting was blank.

**Target behaviour.** No `Cacheability` key, and no configurable response-cache policy. The API
sets no client-cache policy from configuration; `/health` is explicitly `no-store, no-cache` and
data responses are not cached at the client.

**Why.** The setting existed to tune the caching of **server-rendered HTML pages** assembled per
request from skins, containers and controls - the single most expensive thing the legacy
application did, and the thing this migration removes. The target serves a static, immutably
cached bundle from the reverse proxy and returns JSON from the API, so the two halves of the
legacy trade-off are now made in different places by different mechanisms: the proxy's own
`expires 1y` rule for fingerprinted assets, and no caching for authenticated data. Offering an
option numbered `"0"` to `"5"` whose values map onto a page pipeline that no longer exists would
be a setting that could not be honoured. Any authenticated response for which caching becomes
worthwhile can carry its own cache headers at its own endpoint, which is a decision visible at
the endpoint rather than a global mode.

### The base configuration file is strict RFC 8259, declares both secrets as empty strings, and omits one section on purpose

**Target behaviour.** `backend/src/DnnMigration.Api/appsettings.json` is strict JSON - no
comments, no trailing commas, no byte-order mark, LF endings, two-space indentation, one trailing
newline - and holds twelve sections: `ConnectionStrings`, `Jwt`, `PasswordPolicy`, `Cors`,
`RateLimiting`, `Caching`, `Portal`, `Swagger`, `Https`, `Proxy`, `Serilog` and `AllowedHosts`.
Every key in it binds to a real consumer and every settable property of the four options classes
has exactly one key: `Jwt` six, `PasswordPolicy` eight, `Portal` four, `Caching` one. The two
environment overlays keep the release-versus-development twin-file convention the legacy
`release.config` and `development.config` pair established, and every section they declare also
exists in the base.

**The two secrets are declared, and declared empty.** `ConnectionStrings:Default` and
`Jwt:Secret` are both present as `""`. Neither is omitted, because an omitted key documents
nothing; neither carries a sample, because a sample in a tracked file is how a real value
eventually arrives there. Both are startup-validated, so the shipped file cannot boot a host: with
neither supplied the process refuses to start naming `ConnectionStrings__Default`, and with the
connection string alone it refuses naming `Jwt__Secret`. That is the intended, verified outcome -
the legacy installation committed the 3DES key that decrypted every stored password
(`Website/release.config:L89-L93`, and `Website/development.config` additionally hard-codes the
validation key), and the point of declaring these two as empty is that the same mistake cannot be
made by editing this file.

**One section is deliberately absent.** There is no framework `Logging` section: `Program.cs`
installs Serilog as the logging provider and reads its levels and sinks from the `Serilog`
section, and the framework's `LoggerFilterOptions` are not consulted once that happens, so a
`Logging:LogLevel` block would look like it controls log levels while controlling nothing.

**The `Proxy` section is declared with both lists EMPTY.** `Proxy:KnownProxies` and
`Proxy:KnownNetworks` are read by the forwarded-header registration, and an empty list means the
framework's default trust - the loopback address alone - stands, which inside a container is this
process itself. The addresses of a deployment's own proxies are the definition of an
environment-specific value, so the base file declares the two keys and supplies neither: the keys
are discoverable where every other key is, and the value is left to the deployment that knows it.
The production overlay supplies `Proxy:KnownNetworks` for the shipped container topology; see the
forwarded-headers section above for why an empty list there would leave the pipeline stage inert
rather than merely unconfigured.

**The `Https` section carries two keys, and they are useless apart.**
`Https:RedirectEnabled` decides whether the pipeline installs the redirection stage, and
`Https:Port` (default 443) is the port that stage redirects to. The stage resolves its target port
from these options, then from the host's own configuration, then from a single HTTPS address the
server is listening on - and this process listens on plain HTTP only, so the last of those can
never succeed. When none of them yields a port the stage logs once and forwards the request
unchanged, so without `Https:Port` a deployment could switch redirection on, see no error, and
still serve every request in clear text. 443 is also the only value for which the framework builds
an authority with no port at all (`https://host/path` rather than `https://host:443/path`), so the
redirect a browser follows is the address an operator published.

**Why strict JSON rather than the commented form the framework tolerates.** The .NET
configuration provider does skip `//` comments, so a commented file works at run time; nothing
else in the toolchain promises to. Strict RFC 8259 is what `jq`, JSON Schema validators, generic
`check-json` hooks and any future editing tool can all read without exception, and the file's
rationale is not lost by being here rather than inline - it is longer, better cross-referenced and
version-controlled alongside every other migration decision. The one caveat worth stating for
whoever validates this file next: a naive `grep '//'` will match `"http://localhost:4200"` in the
`Cors` section, so the presence of comments must be judged with a parser rather than a substring
search.

### The development overlay carries three log levels and nothing else, and the legacy development file's committed validation key is not among them

**Legacy behaviour.** `Website/release.config` and `Website/development.config` are the same
444-line and 442-line file differing in only six places, and exactly two of those six carry
meaning. `Website/development.config:L89` commits validation material identified only by the
non-secret fingerprint
`sha256:64b7990f63e537b28824e35321b5339db436427ff46755f7de36e484169519f4`; the release
setting at `:L90` has fingerprint
`sha256:da4916cea319caa4c6fddecf69a434cdfbceb5ea6fc280922a7eca7b3293afe4`. Both files
reference the same 3DES decryption material, identified only by fingerprint
`sha256:4886c93e723fe42dc604e4cca065708c304f05308a17cc6aa2af9d40204a64ac`
(`development.config:L90-L91`), which combined with `passwordFormat="Encrypted"` and
`enablePasswordRetrieval="true"` (`release.config:L239-L245`) decrypts every stored password. The
development file is therefore the *more* exposed of the two: it commits the signing key as well as
the decryption key. The other four differences are `objectQualifier="dnn_"` against `""`
(`development.config:L352` against `release.config:L354`), `<trust level="Medium" originUrl=".*" />`
active at `development.config:L121` but commented out at `release.config:L122`,
`<compilation debug="true" strict="false">` against `debug="false" strict="false"`
(`development.config:L123`, `release.config:L125`), and two whitespace-only hunks.

**Target behaviour.** `backend/src/DnnMigration.Api/appsettings.Development.json` is eleven lines
and declares one section:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Information",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  }
}
```

No signing key, no connection string, no `Jwt` section of any kind. `ConnectionStrings__Default`
and `Jwt__Secret` arrive from the environment in development exactly as they do in production, and
both are startup-validated, so a developer who supplies neither is told which key is missing rather
than being handed a working default that hides the requirement. The section that is declared exists
in the base file, which keeps the overlay to genuine deltas: everything else - the two empty
secrets, the password policy, the single permitted cross-origin caller, the credential rate limit,
the console sink and its compact-JSON formatter - is inherited unchanged.

**Why each of the six legacy differences produces no key here.** The committed keys are the whole
point of not carrying them forward, and "it is only development" is precisely the reasoning that put
a 3DES key into source control in the first place. `objectQualifier` is answered by Fluent mapping
rather than configuration, for the reasons set out two sections above. `<trust level="Medium">` has
no counterpart at all: code access security was removed from .NET with the move off the .NET
Framework, partial trust is not a concept the modern runtime implements, and a `Trust` or
`TrustLevel` key would describe a sandbox that cannot exist - isolation is now the container
boundary and the unprivileged user the API image runs as. The `strict="false"` half of the
compilation element is not a setting to port but a warning about the *source* it governed: the
thirty-nine admin code-behinds compiled with Option Strict off while
`Library/DotNetNuke.Library.vbproj:L23-L24` compiled the class library with `OptionExplicit` and
`OptionStrict` on, so those code-behinds may legally contain late binding and implicit narrowing
that C# rejects outright, and every such conversion had to be made explicit during translation. The
`debug="true"` half maps to the build configuration and to the developer exception page the
framework installs from the environment name alone; it is deliberately **not** expressed as a
`DetailedErrors` key, because `GlobalExceptionHandler` takes `IProblemDetailsService` and
`ProblemDetailsFactory` and no environment abstraction, so the RFC 7807 response shape is identical
in every environment by construction, and a key nothing reads is worse than no key.

**Why the SQL-command channel stays closed even in development.** At `Information` the
`Microsoft.EntityFrameworkCore.Database.Command` category writes the command text together with its
parameter list, and on the authentication and user-management paths that list is where credentials
and tokens would be. `Warning` is therefore restated in the overlay rather than merely inherited,
so that the level appears in the same block that relaxes the two levels around it - the place a
reviewer looks to check it was not relaxed as well. The consequence is accepted deliberately:
generated SQL does not appear in a development log, and a developer who needs it enables
sensitive-data logging locally rather than lowering a level in a tracked file. Verified by running
the host in development against a live SQL Server: with the shipped value the login path produced
twenty `Database.Connection`, twelve `Query` and one `ChangeTracking` event at `Debug` and **zero**
`Database.Command` events, and with the level lowered to `Information` the same request produced
four `Executed DbCommand` entries carrying `Parameters=[...]` and the full statement. The two
levels that *are* relaxed are bounded: `Default` at `Debug` buys the value-free Entity Framework
Core diagnostics that make a mapping against the legacy schema inspectable, and the
`Microsoft.AspNetCore` override at `Information` restores the framework's per-request entry while
simultaneously holding the whole ASP.NET Core namespace above `Debug`, since a Serilog override
applies to the longest matching prefix. Neither appears in the production overlay, and neither may
be copied into it.

### The production overlay enforces the transport, re-asserts two hardened log levels, and keeps its global level at `Information` so the audit trail survives

**Legacy behaviour.** `Website/release.config` is the release half of the twin-file pair, and it is
the half that shipped. It committed the 3DES key that decrypted every stored password - the
`machineKey` element at `:L89-L93`, whose `decryptionKey` is quoted in full in the section above
and is not repeated here - together with a `SiteSqlServer` connection string naming a data source
and an attachable database file (`:L24-L26`), `enablePasswordRetrieval="true"` alongside
`passwordFormat="Encrypted"` (`:L236-L246`), fourteen `defaultProvider` declarations, and
`objectQualifier=""` with `databaseOwner="dbo"` (`:L345-L355`). Nothing in it was
environment-supplied: the release configuration *was* the secret store.

**Target behaviour.** `backend/src/DnnMigration.Api/appsettings.Production.json` declares two
sections:

```json
{
  "Https": {
    "RedirectEnabled": true,
    "Port": 443
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  }
}
```

Both sections exist in the base file, and the overlay does two distinct jobs. It *pins* the
logging levels whose accidental relaxation is most costly, restating values the base already
holds so a value left behind in the base cannot follow the build into production. And it
*changes* the one transport decision only a real deployment can make: browser traffic becomes
HTTPS-only, on the port the edge terminates TLS on. Both are explained at length in the two
subsections below.
`docker/docker-compose.yml:L26` and `docker/api.Dockerfile:L70` both set
`ASPNETCORE_ENVIRONMENT=Production`, so this file is live in every containerised deployment.

**No secret appears, and neither secret key appears at all.** There is no `ConnectionStrings`
section and no `Jwt` section - not empty, not a placeholder, not a comment. The base declares both
as `""` and both are startup-validated, so the only supply route is the environment:
`docker-compose.yml:L28` sets `ConnectionStrings__Default` from `${DB_CONNECTION_STRING}` and
`:L29` sets `Jwt__Secret` from `${JWT_SECRET}`, both templated in `docker/.env.example` and read
from the git-ignored `docker/.env`. Verified in both directions against the published Release
build: with the two variables supplied the host reported `Hosting environment: Production` and
answered `/health/ready` with `200 Healthy`, and with `ConnectionStrings__Default` removed it aborted
before binding a port, raising `InvalidOperationException` from
`DnnMigration.Infrastructure.DependencyInjection.ReadConnectionString` and naming both spellings
of the key. `TrustServerCertificate=True` is absent for the reason set out in the next section.

**Why the global level is `Information` and not `Warning`.** A production overlay that quietens the
host is the obvious instinct, and here it would destroy the audit trail. Serilog's
`MinimumLevel:Default` is a global floor, no `MinimumLevel:Override` exists for the
`DnnMigration` namespaces, and the migrated audit records are written at `Information`:
`Infrastructure/Services/AuditLog.cs:L73` calls `LogInformation` under event id 1000, and
`Infrastructure/Services/LoggingAuditSink.cs:L99-L103` selects `LogLevel.Information` for a
succeeded outcome and returns early when `IsEnabled` is false for it. `Default: "Warning"` would
therefore have dropped every succeeded audit record - the whole of the nineteen legacy `AddLog(`
call sites the trail replaces - while leaving failures visible, which is the worst of the three
possible outcomes because the gap is invisible. Verified by running the published build under
`ASPNETCORE_ENVIRONMENT=Production` and signing in: the request emitted
`[Information] LoggingAuditSink … AuditEvent=LOGIN_SUCCESS` and the correlated
`[Information] RequestLoggingMiddleware` entry, and neither would have been recorded at `Warning`.
Log volume was the lesser concern and was given up.

**Why the two overrides are restated rather than inherited.** Both are the settings the development
overlay deliberately relaxes - `Microsoft.AspNetCore` to `Information` and the global default to
`Debug` - so they are the two most likely to be copied into the base by someone reproducing a
development convenience. `Microsoft.EntityFrameworkCore.Database.Command` at `Information` writes
the command text with its parameter list, which on the authentication and user-management paths is
where credentials and tokens would be; restating `Warning` here puts it in the same block a
reviewer reads to confirm production was not relaxed alongside development. Confirmed in the
running container: four `Information`-level lifetime events were recorded, zero
`Information`-level `Microsoft.AspNetCore` events, zero request-pipeline entries and zero
`Database.Command` events, and no connection string, database password or signing key appeared
anywhere in the log.

**Why `Https:RedirectEnabled` is set to true here and left false in the base.** This is the one key
in the file whose careless handling is an outage rather than a disclosure, in either direction:
left false, browser credentials and bearer tokens cross a network in clear text; applied without a
branch, the container's own health probe is answered with a redirect and the whole deployment stops
coming up. Both are avoided at once. `Extensions/ApplicationBuilderExtensions.cs` installs the
redirection stage behind `UseWhen(TransportSecurityApplies, …)`, and that predicate withholds the
redirect from exactly two classes of request: the anonymous `/health` endpoint, and a
loopback-addressed request. The compose health check uses
`wget --spider http://127.0.0.1:8080/health` over plain HTTP, and the frontend service declares
`depends_on: condition: service_healthy`, so answering that probe with a 307 would leave the API
permanently unhealthy and the frontend permanently unstarted. The loopback exemption is the same
carve-out the framework's own strict transport security makes by default - such a request never
leaves the machine that issued it - and it is also what keeps the shipped topology usable, because
nginx forwards the browser's own host, which on that topology is a loopback address.

Verified end to end in the running containers: `docker compose up -d` reported both services
`healthy`; `curl -f http://localhost:8080/health` and `curl -f http://localhost:4200` both returned
200; signing in through the proxy at `http://localhost:4200/api/v1/auth/login` returned 200 with an
access token and `GET /api/v1/auth/me` resolved the tenant; and the same API addressed directly
with a public host name over plain HTTP was answered `307 Location: https://…`, which is the
plain-HTTP bypass being closed. On the Kestrel host the near-miss path `/healthz` was still
enforced, confirming the exemption is an equality test rather than a prefix test, and an untrusted
`X-Forwarded-Proto: https` was ignored rather than believed. Strict transport security needs no key
of its own, since that stage ignores requests that did not arrive over HTTPS and ignores loopback
hosts.

**Why this overlay names NO trusted proxy, and where the trust is named instead.** The
forwarded-header stage trusts nothing a deployment has not named
(`Extensions/ServiceCollectionExtensions.cs`, `AddForwardedHeaders`), and naming a hop is a
DEPLOYMENT fact rather than a build-time one - which is why it is configured where the topology is
described. `docker/docker-compose.yml` names the front-end container's exact address in
`Proxy__KnownProxies__0`, and that is the narrowest trust that works. An earlier revision of this
overlay instead trusted the whole `172.16.0.0/12` range that a container runtime allocates bridge
networks from, and that entry is WITHDRAWN: the compose subnet also contains the network gateway,
and a caller reaching the API's *published* port directly is source-translated to that gateway, so
the range trusted a directly reachable caller exactly as if it were the proxy - letting it name its
own forwarded address and therefore choose its own rate-limit partition. Trusting nothing at all was
the other alternative and is also worse: every caller behind the proxy then shares one credential
budget, so one caller can lock out everybody. Naming the single proxy address avoids both. A
deployment on its own network replaces that one value; a deployment that publishes the API port
should stop publishing it, or bind it to the loopback interface, so the proxy is the only path.

**Three sections are deliberately absent, on one standard.** A key nothing reads is worse than no
key, and it is applied here without exception. There is no `Logging` section, for the reason given
two sections above - `Program.cs:L32-L35` installs Serilog through `UseSerilog` and reads levels
from the `Serilog` section, so a `Logging:LogLevel` block would appear to hold the
`Database.Command` channel closed while holding nothing, which is worse than omitting it precisely
because it reads as a control. There is no `DetailedErrors` key: `GlobalExceptionHandler` produces
the same RFC 7807 shape in every environment and the developer exception page is installed from
the environment name alone, so the key has no observable effect outside development - the same
judgement the development overlay records. There is no `Swagger` section: the base already
disables the console and the reader falls back to false when the key is missing, so restating it
bought nothing that `Https:RedirectEnabled` does not already demonstrate the value of, and the
production overlay is not the place to accumulate inert re-assertions. No `Proxy` section appears
here, for the reason given above: the trusted hop is named by the topology that operates it. No
`Kestrel`, `Urls` or port key appears either: the image fixes `ASPNETCORE_URLS=http://+:8080` and
runs as the unprivileged `appuser`, which cannot bind a port below 1024, and request limits are set
in code rather than configuration. `Https:Port` IS declared, and it is the one addition to the base:
the redirect stage needs a target authority, and 443 is the port the edge terminates TLS on.

**And nothing here can touch the schema.** No `EnsureCreated`, `AutoMigrate`,
`RunMigrationsOnStartup` or migration key of any kind is present, and no code path exists that one
could reach: the only occurrences of those names in `backend/src` are the comments in
`Infrastructure/DependencyInjection.cs:L184` and `Persistence/DnnDbContext.cs:L28` forbidding
them. No JSON-serialisation key is present either - no `DefaultIgnoreCondition`,
`WhenWritingNull` or `WhenWritingDefault` - because the legacy sentinels are observable at the API
boundary: `Library/Components/Shared/Null.vb:L36-L85` defines `NullString` as the empty string and
`NullInteger` as `-1`, and `-1` is simultaneously that sentinel, the `IDENTITY(-1,1)` seed of
`Portals.PortalID` (`01.00.00.SqlDataProvider:L77`) and the `glbRoleAllUsers` pseudo-role
(`Globals.vb:L95`), so `""` must never serialise as `null` and `-1` must never serialise as
absent. Serialisation policy is stated deliberately in code, at
`Extensions/ServiceCollectionExtensions.cs:L303`, and not left to a configuration key.

### `TrustServerCertificate=True` belongs to a development connection string only

**Legacy behaviour.** Transport security did not arise. `Website/release.config:L25` and `:L36`
both point at `Data Source=.\SQLExpress;Integrated Security=True;User Instance=True;`
`AttachDBFilename=|DataDirectory|Database.mdf;` - a local file-attached SQL Server Express 2005
instance - and the commented alternative at `:L30` is `Server=(local);Database=DotNetNuke;uid=;pwd=;`.
The .NET Framework 2.0 SQL client did not encrypt by default, so no certificate was validated and
no setting existed to relax.

**Target behaviour.** `Microsoft.Data.SqlClient` 6.1.6, which `DnnMigration.Infrastructure` pins,
defaults `Encrypt` to `True`, so the client validates the server certificate unless told otherwise.
A developer running SQL Server locally with a self-signed certificate may add
`TrustServerCertificate=True` to the `ConnectionStrings__Default` value they export, and that is the
only place it is acceptable. It must never appear in a production connection string, where it
silently disables the validation that makes encryption worth having, and it appears in no tracked
file in this repository: `appsettings.json` declares the connection string as an empty value,
`appsettings.Development.json` declares no connection string at all, and
`appsettings.Production.json` declares none either. Neither `User Instance` nor `AttachDBFilename`
is carried forward in any form; both are SQL Server Express 2005 features with no counterpart in a
containerised deployment.

**Why the development overlay ships no connection string, credential-free or otherwise.** A sample
value in a tracked file is how a real one eventually arrives there, which is the reasoning already
recorded for the base file. A credential-free
`Server=localhost;Database=DotNetNuke;Trusted_Connection=True` template was considered and rejected
for a second, concrete reason: integrated authentication is not how this project's own development
database is reached - that is a SQL Server container addressed over TCP with a password supplied
from an untracked file - so the template would fail on the very machine it was meant to help, and
the only edit that would make it work is the one edit that must never be made. Leaving the key out
means the startup guard in `AddInfrastructure` names both `ConnectionStrings:Default` and
`ConnectionStrings__Default` and stops, which is a better first experience than a driver-level login
failure.

## Code-review security corrections completed on August 5, 2026

This section records the coordinated corrections made after the original migration notes were
written. Where an earlier paragraph conflicts with this section, this section is authoritative.

### Transport security, proxy trust and the frozen validation topology

AAP 0.9.3 fixes `docker/docker-compose.yml` as the plain-HTTP validation topology: the API is
published on port 8080 so the prescribed health probe can address it directly, and the SPA is
published on port 4200. Production transport requirements are therefore applied by the additive
`docker/docker-compose.tls.yml` and `docker/nginx.tls.conf` overlay rather than by weakening or
rewriting that frozen gate:

- nginx terminates TLS 1.2 or 1.3, redirects browser traffic for the deployment host from port 80
  to 443, mounts the certificate and key read-only, and publishes the SPA on ports 80 and 443;
- the API's host port is withdrawn, leaving Kestrel reachable only on the compose network;
- `Https__RedirectEnabled=true` and `Https__Port=443` protect any browser-facing request that
  reaches Kestrel without the proxy, while `/health` remains exempt so the loopback container
  probe still works;
- `Proxy__KnownNetworks__0=172.16.0.0/12` makes forwarded protocol and client-address headers
  authoritative only when they came from the compose network. With no configured trusted proxy
  or network, forwarded headers are not processed at all.

The base production overlay deliberately leaves `Https:RedirectEnabled` false because the base
compose file is the plain validation topology. Outside development the API emits HSTS on requests
it knows arrived securely and writes one startup warning when neither application redirection nor
the documented TLS proxy posture is active.

### Proxy response headers, CSP and Angular output are one contract

`docker/nginx.conf` now emits `Content-Security-Policy`, `X-Content-Type-Options`,
`X-Frame-Options`, `Referrer-Policy`, `Cross-Origin-Opener-Policy`,
`X-Permitted-Cross-Domain-Policies` and `Permissions-Policy` on the document, static-asset and TLS
paths. HSTS is non-empty only on HTTPS.

The CSP intentionally permits scripts only from the serving origin and carries no
`script-src 'unsafe-inline'`. The Angular production builder therefore sets
`optimization.styles.inlineCritical=false`; enabling critical-style inlining would silently make
the generated document depend on an inline-style exception at build time. `style-src` still carries
the narrower inline-style allowance required by Angular's component styling, but executable inline
content remains prohibited. The proxy policy and `frontend/angular.json` must be reviewed together.

### Proxy and application request logs no longer retain request data

The nginx access format records the request method and the original path with everything from the
first question mark removed. It never records query values. The error log remains at `warn` because
nginx controls its own failure format. Both paths are standard container streams; retention and
read access are therefore properties of the deployment's log collector, and a deployment with a
retention obligation must configure it there.

`RequestLoggingMiddleware` records the matched route template rather than the raw path, a boolean
stating whether a query was present rather than its value, and a bounded failure type rather than
the exception object. Bodies, headers, credentials, bearer tokens, account names, addresses and
free-form exception messages are absent. The correlation identifier links that redacted completion
record to the protected diagnostic channel.

### Credential responses are never cacheable, including refusals

`CredentialCacheControlMiddleware` runs immediately after routing and before rate limiting,
authentication and authorisation. Every endpoint marked as credential-bearing therefore receives
`Cache-Control: no-store`, `Pragma: no-cache` and `Expires: 0` on successful token responses and on
429, 401, 403 or validation responses that short-circuit before the action.

The caller-description read, `GET /api/v1/auth/me`, no longer spends the credential-write budget.
It uses the separate `session-read` policy and the `session-read-window:` partition-key prefix.
Login, refresh and sign-out remain under the credential window and process-wide concurrency bound,
so polling the current-user projection cannot lock legitimate clients out of signing in and a
credential flood cannot consume the projection's own read allowance.

### Host filtering is restrictive by default

The shipped `AllowedHosts` value is exactly `localhost;127.0.0.1;[::1]`. That is suitable for the
validation topology and refuses an unrecognised host before tenant resolution. A deployment serving
a real DNS name must add that exact name, as the TLS overlay demonstrates; failing to do so produces
an intentional HTTP 400 rather than silently accepting an arbitrary Host header.

### Administrative module definitions and generic settings are separate privilege surfaces

The portal-placeable module-definition catalogue excludes every definition whose desktop-module
package has `IsAdmin=true`, and `CreateModuleAsync` independently repeats that check before writing.
The privileged `GetAdministrativeDefinitionByFriendlyNameAsync` repository member exists only for
the application workflow that must locate the administrative User Accounts package; it is not
published through the ordinary catalogue.

Raw module settings require `ModuleEdit` or portal administration. The generic projection omits
`Security_*` and `Column_*` keys, and generic replacement refuses security-owned modules and reserved
keys with `module.settings_protected`; omission never deletes a protected row. Module import uses an
`XmlReader` with DTD processing prohibited, no resolver, zero entity expansion, bounded document
size and bounded nesting, then requires the payload's type attribute to match the target package.

### Account creation, deletion and profile work are tenant-scoped and atomic

`IUnitOfWork.JoinOrBeginTransactionAsync` lets account workflows join a caller-owned transaction or
start exactly one transaction of their own. Account creation now enlists the user, portal
membership, profile and external membership credential write in that transaction. The former
best-effort compensation path has been removed because it could not undo every already-committed
write. Account deletion likewise performs the profile, grant, role, portal-membership, credential
and final account changes under one transaction without nested commits.

`IUserProfileRepository.GetProfileValuesAsync(portalId, userId, ...)` and
`DeleteProfileValuesAsync(portalId, userId, ...)` bind profile values to definitions owned by the
addressed portal. A foreign definition identifier is refused, and removing a portal membership
removes only that portal's values.

Tenant-authored validation expressions are accepted at a maximum of 512 characters even though the
unchanged legacy column can hold 2,000. Evaluation uses `RegexOptions.NonBacktracking`, a 50 ms
timeout and a bounded cache. This is a write-bound security narrowing, not a schema change.

`RestrictedSessionMiddleware` re-reads the account's mandatory-password and mandatory-profile state
after authentication and permits only endpoints explicitly marked `[RemediationAllowed]` until the
condition is corrected. The marker bypasses no endpoint policy. `IUserService.IsEmailValidAsync`
centralises `Security_EmailValidation`, and sign-in additionally enforces
`RequireValidProfile`/`RequireValidProfileAtLogin`.

User-list rows honour the portal's `Column_*` settings. Fields whose columns are disabled are removed
from the projection rather than merely hidden in Angular, so tenant-configured PII minimisation is
enforced at the API boundary.

### Portal references and processor credentials fail closed

The CLR property formerly named `Portal.ProcessorPassword` is now
`Portal.ProcessorCredentialReference` while Fluent mapping still targets the immutable legacy
`ProcessorPassword` column. New values must be bounded `secret://` managed-secret references; the
credential itself is never accepted, returned or logged.

The update contract has explicit keep/replace/clear semantics: `null` keeps the stored reference, a
non-empty `secret://...` value replaces it, and the empty string clears it. A pre-existing plaintext
value is not silently retained and fails with `portal.processor_reference_invalid` until an operator
replaces or clears it.

Before mapping or saving, the service proves that `AdministratorId` is a member of the addressed
portal and that every configured tab reference belongs to that portal. Failures use
`portal.administrator_invalid` and `portal.tab_reference_invalid`. The reference checks, secret
decision and update execute under a serializable transaction so the validated relationships cannot
change between validation and commit.

### Legacy credential, refresh-token and audit corrections remain authoritative

The existing sections **Security correction: legacy credential cut-over, durable refresh state and
minimal claims** and **Security correction: minimised audit data, structured metadata and delivery
health** remain part of this document unchanged. They are the authoritative record for bounded
legacy verification with immediate BCrypt replacement, SQL-backed refresh rotation, minimal access
token claims, audit-data minimisation, structured metadata and degraded audit-pipeline health.

### Reproducible restore and current dependency verification

Every backend project now owns a committed `packages.lock.json`, including the package-free Domain
project. `Directory.Build.props` enables lock-file generation and locked mode globally.
`docker/api.Dockerfile` copies the repository `NuGet.Config`, all six manifests and all six lock
files before restoring with the explicit repository configuration and locked mode. A clean
solution restore and the API image restore both succeeded in locked mode.

The authoritative NuGet checks were rerun on **August 5, 2026** against nuget.org with transitive
packages included:

- vulnerability inventory: **zero**;
- deprecation inventory: the only remaining deprecated identity is the test-only `xunit` 2.9.3
  family (`xunit`, `xunit.assert`, `xunit.core`, `xunit.extensibility.core` and
  `xunit.extensibility.execution`) in the two test projects;
- outdated inventory: newer versions exist across the graph, predominantly .NET 9/10 or other
  framework-major lines outside the frozen net8.0 plan. An outdated result without an advisory or
  deprecation is not silently treated as compatible with the AAP's exact version pins.

The runtime-relevant deprecated graph was removed by moving `Microsoft.Data.SqlClient` from 5.2.3
to 6.1.6 and pinning `System.Collections.Immutable` 8.0.0. The container test harness moved from
`Testcontainers.MsSql` 3.10.0 to 4.13.0; its new `BouncyCastle.Cryptography` transitive identity is
explicitly source-mapped. The obsolete parameterless `MsSqlBuilder` call was replaced with
`new MsSqlBuilder(ContainerImage)`. The full 841-test integration suite passed both against the
configured SQL Server and with `DNN_TEST_SQLSERVER` removed so that Testcontainers provisioned its
own SQL Server.

The xUnit deprecation is a bounded accepted residual, not a runtime dependency. The AAP pins the
2.9.3 test framework, and controlled `xunit.v3` trials produced 194 warnings-as-errors and contract
errors across the established test surface, including its changed `IAsyncLifetime` shape. Rewriting
thousands of verified tests as an incidental package update would violate the frozen plan and
obscure the security changes those tests protect. No vulnerability was reported for xUnit 2.9.3;
its migration belongs in a dedicated test-platform change with its own review.

### Base-image and produced-image CVE disposition

Both Microsoft base tags were pulled afresh on August 5, 2026. The resolved amd64 digests were:

- SDK build stage: `mcr.microsoft.com/dotnet/sdk@sha256:5f12aa62868b69dcb41de9cd7f8759822f7d1f56c3b31908048ad65df0981e67`;
- ASP.NET runtime stage:
  `mcr.microsoft.com/dotnet/aspnet@sha256:b02ab6637e02dfe07d4205d557cbce7e2ab0e4a1d7d1285868b4f31eed20bd10`.

The ASP.NET runtime base and the produced `dnnmigration-api` image each reported **zero
vulnerabilities**. The SDK base scanner reported build-stage-only entries, which were evaluated
rather than copied into the release verdict:

- its NuGet defense-in-depth advisory is a scanner false positive for this SDK; Microsoft's
  advisory identifies .NET SDK 8.0.420 as patched and the image contains 8.0.423;
- its MSBuild spoofing advisory is likewise superseded by the vendor's patched .NET 8 SDK floor of
  8.0.410; this image contains 8.0.423, and the scanner keyed on an internal file-version branch;
- `System.Security.Cryptography.Xml` findings occur in bundled `dotnet-format`, F# and PowerShell
  tool payloads. The Docker build invokes none of those tools and the multi-stage copy excludes
  them from the runtime image;
- curl/libcurl findings occur only in the SDK stage. The Dockerfile invokes neither curl nor git,
  and neither package is copied into the runtime stage.

The release decision is therefore based on the produced image, not on unused files in a discarded
builder filesystem. Re-run both the NuGet and image scans whenever a lock file, base-image digest or
direct dependency changes.

### CORS documentation correction

The earlier statement that the allowed-origin list “defaults to empty” was stale. The shipped base
configuration explicitly contains only `http://localhost:4200`; compose supplies the same default,
and the TLS overlay replaces it with the deployment's HTTPS origin. Clearing the list remains
fail-closed because the named policy is still registered with no permitted origins. Credential
support is never enabled.

## Security correction: minimised audit data, structured metadata and delivery health

The legacy event log copied account names, tenant names, aliases, administrator names and electronic-mail
addresses, role names, page names, filenames and other free text into a store with a retention lifecycle
separate from the records those values described. The target audit contract no longer does that. It records
stable portal, actor, subject and resource identifiers, the preserved event name and outcome, a stable failure
code, and a closed set of bounded machine-readable metadata. Unknown metadata keys are discarded. Values over
128 characters, values carrying control characters, and values carrying the former `key=value; ...`
delimiters are replaced by the fixed scalar `rejected`. Metadata is emitted as individual structured logger
properties instead of one flattened string, so a value can neither forge another field nor amplify a record
without a hard ceiling. Unknown-account sign-in attempts are intentionally anonymous in the audit trail; the
request correlation and credential-rate controls remain the mechanisms for grouping those probes.

**Purpose.** These records exist only to investigate authentication outcomes, privileged administrative
mutations and security-control failures, and to reconcile a committed change with the stable database
identifiers it affected. They are not application analytics, a user-profile mirror, a search index or a
general change log. Adding a descriptive value because it might be convenient later is not an audit purpose
and is prohibited by the sink allowlist.

**Access.** The application exposes no audit-reading endpoint. Records leave through the configured Serilog
pipeline, so production access must be restricted at that sink to the security and operations roles that
investigate incidents or administer the service. Application administrators do not receive access merely
because they can mutate a portal. Export, forwarding and backup permissions must be no broader than the sink's
read permission, and raw console access in the orchestrator is audit access for this purpose.

**Retention and deletion.** The application does not own the external logging provider and therefore cannot
enforce its lifecycle in code. A production deployment must configure an explicit maximum retention at the
sink; the baseline is 90 days unless a documented legal, contractual or incident-response requirement sets a
different period. Expiry must cover searchable indexes, exports and backups rather than only the active view.
Where a deletion obligation applies to the remaining pseudonymous account identifiers, the operator must purge
records addressed by `AuditActorUserId` and `AuditSubjectUserId` from every retained copy. A deployment that
leaves retention at an orchestrator or vendor default has not completed this control.

**Delivery failure.** `IAuditSink.Record` still never fails the business operation after it has committed.
Instead, a failed logger call increments a saturating process-local counter, emits the closed
`AuditRecordNotWritten` security diagnostic using only tenant/account identifiers and the exception type name,
and changes the named `audit-pipeline` health check to `Degraded`. The anonymous health document exposes the
named status but not the exception or counter data. A restart clears the process-local count; the external
monitoring system must retain and alert on the degraded observation across restarts.

## Security correction: legacy credential cut-over, durable refresh state and minimal claims

### Republished machine-key material has been removed

Two duplicated sections in this document previously copied the legacy validation and decryption
values into a second tracked file. They now identify the historical material only by non-secret
SHA-256 fingerprints:

- development validation material:
  `sha256:64b7990f63e537b28824e35321b5339db436427ff46755f7de36e484169519f4`;
- release validation setting:
  `sha256:da4916cea319caa4c6fddecf69a434cdfbceb5ea6fc280922a7eca7b3293afe4`;
- shared 3DES decryption material:
  `sha256:4886c93e723fe42dc604e4cca065708c304f05308a17cc6aa2af9d40204a64ac`.

No tracked modern configuration or test fixture contains the material. Its historical disclosure
means it must be treated as compromised: any surviving legacy deployment must rotate its machine
key, and every remaining format-2 credential must be migrated or administratively reset. Repository
history and the frozen read-only legacy configuration remain sensitive records; never copy their
values into a ticket, log, sample, test or deployment manifest.

### The first successful legacy sign-in now performs the bounded BCrypt cut-over

This entry supersedes both the earlier “administrative reset is the only path” correction and every
source comment that repeated it. The primary AAP 0.7.5.5 path now exists:

- `MembershipStore` reads the external membership row's stored value, `PasswordFormat` and
  `PasswordSalt`; no entity or EF migration owns those legacy objects.
- `ILegacyCredentialVerifier` is implemented by a migration-only verifier that is disabled by
  default, accepts configuration only through `LegacyCredentials:*`, bounds every input, supports
  legacy clear, SHA-1 and encrypted rows, performs fixed-time comparisons, and clears temporary
  cryptographic buffers.
- The verifier returns only whether a recognised legacy representation matched. It exposes no
  plaintext recovery or general decryption operation.
- `AuthService` still performs one current-cost BCrypt comparison for every structurally valid
  request, pairing legacy rows with the hasher's decoy so migration does not recreate the account
  timing oracle.
- On the first accepted legacy presentation, `AuthService` hashes the submitted credential with the
  current BCrypt implementation and immediately replaces the legacy value through
  `SetPasswordHashAsync` before issuing tokens.
- Administrative reset remains the fallback for disabled migration, malformed or unverifiable rows,
  and accounts whose owners do not sign in before the migration window closes.

The migration secret is supplied only by an environment-backed secret while legacy rows remain.
After cut-over, disable the verifier, remove that secret and reset any residual legacy credentials.

### Refresh-token state is durable, shared and consumed only after fallible reads

The process-local singleton store has been removed. `IRefreshTokenStore` and `ITokenService` are
scoped asynchronous services backed by the additive application-owned table
`DnnMigration.RefreshTokens`. Production applies
`backend/src/DnnMigration.Infrastructure/Persistence/Scripts/CreateRefreshTokenStore.sql`
explicitly; application startup and EF migrations do not create or alter any legacy object.

Only SHA-256 token and client-binding digests are stored. Issue, inspect, rotation and revocation
are shared across restarts and replicas. Rotation runs under a serializable transaction with update
locks; a bounded five-second same-client retry is classified as concurrent use without revoking the
account, while a replay from another client revokes every family for that account. Spent
fingerprints remain until the family's absolute expiry rather than being trimmed to a generation
count, and expired cleanup is bounded to 500 rows per operation.

`AuthService.RefreshAsync` first performs a non-consuming inspection, then completes every tenant,
account, credential-state, advisory and profile read, and only then requests the atomic rotation.
A dependency failure therefore cannot consume the caller's usable token without returning its
successor.

### Access tokens carry identity, not mutable authority

The custom claim vocabulary is now exactly `sub`, `portal_id` and `jti`; user names, host status,
roles and permission keys are absent. Server-side authorization re-reads authoritative state, and
the Angular client follows login and refresh with `GET /api/v1/auth/me` to obtain its display and
affordance snapshot. The token service accepts only account ID, portal ID and cancellation on issue,
and opaque refresh material plus the server-observed client binding on rotation.

## API checkpoint review closure — contract changes made explicit

The corrections below were made while closing the final sixteen findings on the HTTP API
checkpoint. They supersede any earlier description of a permissive request body, an implicit module
placement, module-level paging, a read-only portal-settings resource or duplicate public route
families.

### Unknown JSON request members are refused across the whole API

**Earlier target behaviour.** `System.Text.Json` silently discarded properties a request contract
did not declare. The module-settings client consequently sent `isDefaultModule` and `allModules`
while the server accepted `setAsDefaultSettings` and `applyToAllModules`; both far-reaching
instructions vanished, six additional unsupported fields vanished with them, and the request still
answered successfully.

**Target behaviour.** MVC deserialisation uses
`JsonUnmappedMemberHandling.Disallow` for every request body. An undeclared member is a model-state
failure and produces the same field-keyed `ValidationProblemDetails` shape as any other invalid
request. The setting is global because hand-maintained client/server mirrors exist throughout the
API; limiting it to the module request would leave the identical silent-loss trap on every other
contract.

**Operational consequence.** A client compiled against an obsolete or wider request shape receives
`400` instead of a partial success. Response serialisation is unaffected.

### A module update names the exact page placement it changes

**Earlier target behaviour.** `UpdateModuleRequest.tabId` was optional on the client and unread by
the service. When a module appeared on several pages, the service selected the placement with the
lowest `TabModuleId`, so an edit submitted for one page could rewrite another page and return a
description of the placement the caller had not addressed.

**Target behaviour.** `tabId` is required and is used for an exact module-and-page lookup. A module
that is not placed on the named page is refused with `module.placement_not_found`; there is no
fallback to another placement. The two instruction members use the names accepted by the server and
shown by the legacy screen: `setAsDefaultSettings` and `applyToAllModules`.

**A deliberate surface reduction.** The update request and the Angular module-settings view no
longer expose `paneName`, `alignment`, `color`, `border`, `displayPrint` or
`displaySyndicate`. Those six values belong to Web Forms pane layout, server-side rendering or the
excluded syndication surface. They also had no create-contract or read-contract counterpart, so
accepting them only on update would have been write-without-read-back. Their stored columns are not
dropped and an ordinary update leaves them unchanged. The now-unused client-only
`MODULE_ALIGNMENT` vocabulary is removed with the controls that consumed it.

### Module listing pages the placement rows it returns

**Earlier target behaviour.** The service paged modules and then expanded each module into one row
per page placement. A page could therefore return more rows than its requested size, while
`totalCount` and `pageSize` were fabricated with `Math.Max` to make the metadata appear large
enough for the expanded result.

**Target behaviour.** Filtering and ordering still identify the eligible modules, but paging is
applied after expansion to the placement-row projection — the same unit carried in `items`.
`totalCount` is the exact number of placement rows, `pageSize` is the requested size, and adjacent
pages neither overlap nor skip a placement.

**Operational consequence.** A module placed on several pages consumes several positions in the
result set. That is a contract correction: the paging metadata now describes the rows the caller
actually receives rather than a different upstream entity.

### Portal settings are a writable projection of the portal row

**Earlier target behaviour.** `GET /api/v1/portals/{portalId}/settings` existed, but the matching
update operation did not. The Angular settings screen could display the legacy Site Settings field
set without having an AAP-authorized endpoint that persisted it.

**Target behaviour.** `PUT /api/v1/portals/{portalId}/settings` accepts the twenty-six editable
settings fields and returns the updated `PortalSettingsDto`. The portal identifier remains
route-owned and the immutable portal GUID is absent from the body. The operation uses the same
measured Site Settings validation rules, host-only-field guard, administrator-retention invariant,
mapping and cache invalidation as the full portal update.

There is still no `PortalSettings` table. Both verbs project columns on the `Portals` row; the
legacy `PortalSettings` class was a request-lifetime composite rather than a persisted aggregate.

**Processor credential semantics.** The processor credential is accepted only inbound and is never
returned, so the settings resource carries `ProcessorCredentialReference` rather than a password box.
Because no response echoes it, the request carries the three states explicitly: `null` keeps the
stored reference, the empty string clears it, and a non-empty value must be a `secret://`
managed-secret reference. The Angular screen represents the clear operation as its own control, so
two visually identical submissions cannot act differently.

### Each operation has one canonical public route

**Target route families.** Modules and accounts are flat:
`/api/v1/modules` and `/api/v1/users`; membership policy is
`/api/v1/users/settings`. Roles, role groups and profile definitions are likewise exposed only at
`/api/v1/roles`, `/api/v1/role-groups` and `/api/v1/profile-definitions`. These controllers
resolve the tenant from the request host through the scoped portal context rather than accepting a
second portal identity in the path.

Portal aliases are the deliberate exception because an alias is owned by a portal:
`/api/v1/portals/{portalId}/aliases`. Page listing remains portal-owned for the same reason. The
permission API is only the read-only catalogue at `/api/v1/permissions` and
`/api/v1/permissions/{permissionId}`.

**Withdrawn duplicate identities.** The following public families no longer exist:
portal-nested modules, users, roles, role groups and profile definitions; host-wide
`/api/v1/portal-aliases`; and the module- and page-scoped permission child reads. They were not
compatibility aliases: OpenAPI exposed each as a separate operation, so generated clients had to
guess which resource identity was authoritative.

**Unresolved tenant behaviour.** A flat tenant-dependent route addressed through a host with no
matching alias is refused with `403 portal.tenant_unresolved`; it is never defaulted to portal zero
or minus one. Runtime validation pins every canonical collection at `200` for a resolved tenant and
every withdrawn family at `404`.

### Page updates preserve stored skin and container tokens

**Earlier target behaviour.** `UpdateTabRequest` exposed `skinSrc` and `containerSrc`. Because the
mapping was a whole-row replacement, simply omitting either optional JSON member deserialised it as
null and erased the stored token during an unrelated page edit.

**Target behaviour.** Neither member is writable. The mapper never assigns the two columns, so their
stored values survive a page update byte-for-byte. Both remain on the read-only page-detail
projection, which keeps existing configuration observable without reintroducing the excluded
skinning and container-management surface.

### Login verification-code absence has one type on both clients

The optional verification code is `string | null` in the Angular request and `string?` in the
.NET request. Omission, null and the empty string all mean that no code was supplied, matching the
legacy empty-string sentinel and the hidden-by-default verification control. This is a type
alignment only; the verification-required and invalid-code outcomes are unchanged.

### Request completion logs no longer receive exception objects

**Earlier target behaviour.** The request-logging middleware passed the caught exception to
Serilog after rethrowing it, while the global exception handler also logged the failure. That wrote
the same failure twice and allowed exception messages — including provider or parser text quoting
submitted values — through the generic completion log.

**Target behaviour.** The completion event records a bounded chain of exception **type names** in
`FailureType`, with no messages, stack or exception object. The global exception handler remains
the single owner of detailed failure diagnostics, joined to the request by the correlation
identifier. Successful requests record the fixed value `none`.

### Profile-definition host scope follows the legacy `-1`-to-`NULL` translation on every read

**Legacy behaviour.** Profile-property definitions expose the sharpest collision in the old null
table. `Portals.PortalID` starts at `-1`, but the core provider also passed that value through
`GetNull` on `AddPropertyDefinition`, `GetPropertyDefinitionByName` and
`GetPropertyDefinitionsByPortal`
(`Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb:L1021,L1039,L1042`).
`Null.GetNull` converted it to `DBNull`, so those operations wrote or matched only rows whose
`ProfilePropertyDefinition.PortalID` was SQL `NULL`. The controller's single-item method first
searched that scoped catalogue, then fell back to an unscoped identifier lookup on a miss
(`Library/Components/Users/Profile/ProfileController.vb:L425-L439`), which could reveal another
tenant's declaration.

**Target behaviour.** `UserProfileRepository.DefinitionsInPortalScope` is the single predicate for
collection, name and identifier reads. A caller naming `-1` reaches only SQL-null host
declarations; it does not also reach a row physically storing `-1`, and every other identifier
matches exactly. `UserMappings.ToNewDefinition` applies the same translation before a create is
staged, while `UserMappings.ToDto` restores `-1` at the response boundary. The old unscoped
single-item fallback is deliberately not reproduced because it violates the migration's tenant
isolation requirement. Integration coverage stores both encodings simultaneously and proves that
all three read shapes return the same scope.

### Blocking credential and profile remediation is durable, server-enforced and fails closed

**Legacy behaviour.** The post-credential status values for an administrator-forced password
change, an expired password and an incomplete required profile were blocking UI states:
`Website/admin/Authentication/Login.ascx.vb` sent the caller to an interstitial or profile step and
did not expose the ordinary application surface. The two shipped-default-credential outcomes were
different: they promoted an already-successful sign-in status, so forcing a change was an intent
carried only by that one response and was not persisted as account state.

**Target behaviour.** `MustChangePassword` and `MustUpdateProfile` are no longer client-only
advisories. Login and refresh responses retain both values. The ACCESS TOKEN CARRIES NEITHER: it carries
identity only, per the minimal-claims correction recorded above, so no remediation flag can outlive
the request that read it. Enforcement is therefore entirely server-side and always current - a
restricted-session stage in the pipeline and an authorization handler both re-evaluate the account
and tenant from authoritative storage on every protected request. While
either requirement remains active, only authentication lifecycle operations and the account
owner's matching password or profile remediation route are admitted; every ordinary protected
route is forbidden.

**Shipped-default credentials become durable remediation.** A successful login with one of the
credentials distributed with the product now sets `Users.UpdatePassword` before the token pair is
issued. The password-change workflow already clears that flag, so the requirement survives access
token expiry, refresh rotation and a new process until the credential is actually replaced. This is
a deliberate strengthening over the ephemeral legacy success-with-caveat status and is necessary
because the raw submitted credential is unavailable on later requests.

**Profile-state failure now fails closed.** An earlier target revision treated a failed
required-profile evaluation as "no advisory" and issued an unrestricted session. That behaviour is
not preserved: once profile completion is an authorization input, an unreadable store provides no
evidence that the requirement is satisfied. Login is therefore refused, refresh rotation is ended,
and a protected request is forbidden when the current state cannot be evaluated. The outward
failure names no profile definition or stored value.

**Why no claim carries the decision, and why none carries the state either.** A claim would let the
client choose its remediation experience without a further call, but it becomes stale the moment an
administrator imposes a requirement or the caller completes one, and a token lives for minutes. The
state is therefore reported on the login and refresh RESPONSES - which the client reads once, at the
moment it must choose a screen - and re-read from storage on every protected request. A response
value cannot preserve a cleared block or bypass a newly imposed one, because nothing consults it
after the redirect it caused.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Dtos/Auth/AuthenticationRemediationState.cs`,
`backend/src/DnnMigration.Application/Dtos/Auth/LoginResponse.cs`,
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Application/Abstractions/IAuthService.cs`,
`backend/src/DnnMigration.Api/Authorization/RemediationAuthorizationHandler.cs`,
`backend/src/DnnMigration.Api/Authorization/AllowDuringRemediationAttribute.cs`,
`backend/src/DnnMigration.Api/Controllers/AuthController.cs`,
`backend/src/DnnMigration.Api/Controllers/UsersController.cs`,
`frontend/src/app/core/models/auth.model.ts`.


### Liveness and readiness are separate contracts

**Legacy behaviour.** `Website/KeepAlive.aspx` proved only that the IIS worker could answer a
request. It did not test SQL Server or gate another process.

**Target behaviour.** `/health` is retained as the backward-compatible liveness address, and
`/health/live` is the stricter view that executes no registered check whatsoever. `/health` keeps
every probe that is NOT tagged as a readiness signal - today the audit-delivery probe - so a monitor
pinned to it still learns that the audit transport has failed, while neither view can be held down
by a database outage. `/health/ready` selects only checks tagged `ready`;
it names the custom database connection probe and the SQL Server package probe and returns an
unhealthy result when either cannot serve. All three publish the same non-secret JSON shape and
remain anonymous, uncached, unthrottled and exempt from HTTPS redirection.

**Operational consequence.** The image's own health check and the compose condition stay on
`/health`, because the validation topology declares no database service and the store it reaches is
external: gating the front end on readiness there would hold it back for a reason unrelated to the
API's ability to serve it. An orchestrator that provisions the store alongside the API points its
readiness probe at `/health/ready` instead, and a process supervisor may use either liveness path
without restarting a healthy process merely because SQL Server is temporarily unavailable.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/DependencyInjection.cs`,
`backend/src/DnnMigration.Infrastructure/HealthChecks/DatabaseHealthCheck.cs`,
`backend/src/DnnMigration.Api/Extensions/ApplicationBuilderExtensions.cs`,
`backend/tests/DnnMigration.IntegrationTests/Api/HealthCheckTests.cs`,
`docker/api.Dockerfile`,
`docker/docker-compose.yml`.

### Module content transfer is explicitly bounded at one mebibyte

**Legacy behaviour.** The Web Forms import page read an uploaded XML document without a
feature-specific byte ceiling in the application code.

**Target behaviour.** Both module export and import actions declare a one-mebibyte request-size
limit explicitly, matching Kestrel's global administration-API ceiling. Export requests are small,
but naming the same bound on both content-transfer operations prevents a later global-limit change
from silently widening one or narrowing the other.

**Operational consequence.** An import document whose JSON request body exceeds one mebibyte is
rejected while the server is reading it, before model binding or XML parsing allocates further
state. Increasing the limit is an explicit endpoint contract change rather than an incidental
server configuration edit.

**Annotated in code at.**
`backend/src/DnnMigration.Api/Controllers/ModulesController.cs`,
`backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`,
`backend/tests/DnnMigration.IntegrationTests/Api/ModuleApiTests.cs`.
### Frontend API classification and wire-contract corrections

**Bearer tokens are attached only after URL parsing, exact-origin comparison and a
segment-bounded path match.** The production API base is the relative `/api/v1`,
resolved against `document.baseURI`; the development base is the configured
absolute API origin. A request is now classified as API traffic only when its
resolved origin exactly equals the configured origin and its path is either
`/api/v1` or begins `/api/v1/`. The former substring test accepted any hostile
absolute URL containing that text and also confused `/api/v10` with version 1,
which could disclose both the current bearer token and a rotated token after a
401. Query strings and fragments do not influence the decision.

**The Angular module-update request now mirrors the C# request exactly.** It has
the same sixteen required members, in the same order after camel-casing.
`tabId` is a required member and the three
administrator-intent members — `isDeleted`, `setAsDefaultSettings` and
`applyToAllModules` — are declared optional, because the server supplies a
documented default for each when it is absent; the module-settings component
nevertheless sends all four, taking the first two from its loaded state and
mapping the two intent controls to the server spellings through a shared
projection helper rather than composing the body inline. Six
legacy presentation-only placement values — pane name, alignment, colour,
border, print and syndication flags — remain local screen state and are absent
from the wire request, rather than being advertised as fields the server
silently ignores.

**The frontend permission model publishes only shapes the API produces.** The
persisted four-key vocabulary and the five-member catalogue definition remain.
The former `ModulePermission` and `TabPermission` interfaces are removed because
no endpoint returns those grant rows and no production consumer used them.
Legacy module/page grant entities and their role/user discrimination remain
authoritative server-side concerns; a client grant type will be introduced only
with a matching endpoint, DTO and consumer rather than in anticipation of one.

**Annotated and verified in.**
`frontend/src/app/core/config/api-endpoints.ts`,
`frontend/src/app/core/config/api-endpoints.spec.ts`,
`frontend/src/app/core/models/module.model.ts`,
`frontend/src/app/core/models/permission.model.ts`,
`frontend/src/app/core/models/tab.model.ts`,
`frontend/src/app/features/module/module-settings/module-settings.component.ts`,
`frontend/src/app/features/module/module-settings/module-settings.component.spec.ts`.

### FINAL credential-migration status: first-login re-hash is active behind a bounded secret-backed window

This final entry supersedes the older administrative-reset-only clarification and the later
work-factor-only clarification above. The complete delivered behavior is documented in the section
“CORRECTION: the bounded first-login legacy credential migration is implemented”: legacy membership
rows can be verified during an explicitly enabled absolute UTC window, are replaced immediately with
BCrypt on success, and emit a credential-migration audit event containing no secret material.
Administrative reset remains the fallback after the deadline, for unsupported or damaged rows, and
for owners who no longer know the credential. Password retrieval remains absent everywhere.


### Where two independent corrections disagreed, and what survives

Several corrections were produced independently against the same code, and in a handful of places
they reached opposite conclusions. Each is settled here so the tree and its notes agree.

**The submitted page on a module update SELECTS the placement; it does not move it.** One correction
read `UpdateModuleRequest.TabId` as the legacy page picker and assigned it to the placement, gating the
change on portal administration and validating the destination page. That reading is withdrawn. The
service resolves the placement BY the submitted page and refuses a request naming a page the module
does not occupy, so a page identifier cannot also be a destination: one member cannot be both. What is
kept from that correction is its authority gate — changing the all-pages flag, naming the module as the
portal default, or rewriting every module's appearance now requires portal administration rather than
a page-scoped grant — and its requirement that the caller hold the edit grant on the page whose
placement is being edited. Moving a module between pages is consequently not available through this
contract and would need a second member naming the destination.

**One bounded legacy-credential verifier, with an absolute deadline.** Two corrections each introduced
a verifier, an options section and a test suite for the same first-login bridge. The verifier that
survives is the one whose options are internal to the infrastructure project beside the code that
consumes the deployment key, extended with the other's absolute UTC deadline, its clock-driven window
test and its Triple-DES weak-key refusal. Start-up now refuses an enabled window with no deadline, a
deadline carrying a non-zero offset, a malformed key, or a degenerate key, and the window closes at the
configured instant whatever the process does afterwards. The configuration section is
`LegacyCredentials`; the parallel `LegacyCredentialMigration` section is withdrawn, and compose passes
the surviving names.

**The tenant name and alias stay out of the audit properties.** One correction narrowed the
portal-installation record to remove the administrator's personal data and the caller's free text while
keeping the tenant name and alias; the other removed those as well. The narrower set survives, because
the record's envelope already carries the tenant key and both values are caller-supplied text bounded
in length and not in content. The presence-only flags for the two free-text members and the
administrator's numeric key are kept from the wider set, and the sink's key allowlist admits exactly
the properties the services now record.

**Audit values are refused, not repaired.** One correction rendered every property into text and
replaced control characters with a placeholder. The sink that survives never renders properties into a
line at all: it admits only allowlisted keys, bounds each value, and replaces a value containing a
control character or a separator with a fixed scalar, counting any key outside the vocabulary as
withheld. The property being protected is the same — no submitted text can forge a second record — and
the assertions that proved it were re-pointed rather than removed.

**Membership-settings bounds are declared on the write shape.** A validator declared on the read
projection could never run, because no endpoint binds that shape. Its bounds — the closed display,
visibility and control vocabularies, the page-size range, the display-name width and the compilable
email expression — are enforced on the update request and asserted against the validator the pipeline
actually resolves.

**The composed topology terminates TLS at an overlay, not from compose secrets.** Two corrections
described incompatible transports for the front-end container. One mounted a certificate pair as
read-only compose secrets, terminated TLS inside the container and published it on 4443; the other
kept the base topology on plain HTTP for the two loopback probes the acceptance gates issue and moved
TLS into an overlay that mounts a server block and a certificate directory. The overlay survives,
because it is what the shipped `docker/nginx.conf` actually implements: that file declares one
plain-HTTP server plus two wildcard includes that match no file in the image, and it reads no
`/run/secrets` path anywhere. Two fragments of the withdrawn design had nonetheless survived in
`docker/docker-compose.yml` — a service-level `secrets:` reference and a `4443:443` publish — and
because no top-level `secrets:` key declared them, **the compose project would not parse at all**:
both `docker compose build` and `docker compose up` refused it, with and without the overlay, so the
container gates could not run. Both fragments are withdrawn rather than completed: declaring the
secrets would have delivered files nothing reads and, since the declaration named them with the
required-variable form, would have made the base topology refuse to start unless a deployment supplied
certificates it does not need. TLS is now activated one way — `docker compose -f
docker/docker-compose.yml -f docker/docker-compose.tls.yml` — and the header comments, the README
verification commands and the mount path named in `nginx.conf` were each corrected to the measured
behaviour: `http://localhost:4200` and `http://localhost:8080/health` both answer 200, not a redirect,
because both address a loopback authority that every transport stage here exempts.

**The two transport edges return different redirect codes, deliberately.** The proxy answers a
plain-HTTP request for a non-loopback host with 307 and the API's own transport stage answers with 308;
a comment in the proxy configuration claimed the two agreed, which was never true. Both codes preserve
the method and the body, so the difference is cacheability alone, and the two edges do not face the same
caller: the proxy's answer is cached by real browsers and by every intermediary on the path, where
reversibility during a certificate replacement is worth having, while the API's stage is a
defence-in-depth backstop on the private container network that no browser addresses, where permanence
is the honest description of a perimeter that terminates TLS and carries no cache blast radius. The
claim of agreement is removed and the reason for the split is stated at both edges.

**The TLS overlay was activated and measured, and three further disagreements only appear when it is.**
The overlay is the one supported way to terminate TLS in the front-end container, so it was brought up
against a generated certificate rather than reasoned about. Three defects surfaced, each the product of
two revisions naming the same thing differently, and each invisible to every build, unit, integration
and browser suite:

- *The mounted server block named a variable that does not exist, and nginx refused to start.*
  `docker/nginx.tls.conf` carried `$csp`, from a revision whose policy variable is now
  `$content_security_policy`, so nginx exited with `unknown "csp" variable` and the container restarted
  in a loop. The same file wrote its own strict-transport-security value — two years, with `preload` —
  where the main configuration resolves `$hsts_policy` to one year without preload and states why:
  preload is not reversible on any useful timescale and binds every subdomain. Both values now come from
  the declared variables, which is the principle the main configuration states for itself.
  `docker/nginx.tls.conf.example` named a `$strict_transport_security` that likewise does not exist and
  is corrected the same way.
- *Mounting a TLS block silently made the container report unhealthy for its whole life.* The
  http-level include that loads a mounted block sits above the main server, and nginx makes the first
  server listening on an address the default for it — so the mounted block's own port-80 redirect
  server became the catch-all for port 80 and answered the container's own health probe with a redirect
  to HTTPS, which the probe could not verify. The main plain-HTTP server is now the explicit
  `default_server`, which makes the probe independent of include order and of whatever a deployment
  mounts. Measured: the front-end container reports healthy in six seconds with TLS active, and the
  plain-HTTP base topology behaves exactly as before.
- *The overlay's stated security property did not hold.* It withdrew the API's published port with
  `ports: []`, on the stated ground that a list-valued key is "last one wins" in an overlay. It is not:
  list-valued service keys merge by union, so the empty sequence contributed nothing and the API stayed
  published on the host. The compose-spec `!reset` tag removes an inherited key and `!override` replaces
  an inherited list; with those, the overlaid project publishes nothing for the API and 80 and 443 for
  the front end, and the base project is unchanged. Verified by reading the resolved configuration and
  by failing to reach the API on the host port while the SPA answered over TLS.

The redirect in the mounted block is also aligned to 307. Its own note argued only against 301, on the
ground that 308 preserves the method and body — which 307 does equally — so the cacheability argument
decides it, and the two files are the same browser-facing edge answering the same class of request.

### The local launch profile binds 8080, not the 5000 the prior guide documents, and no IIS profile is carried forward

**Legacy behaviour.** There was no launch profile, because there was nothing to launch. The legacy
application was an IIS application, not a process: `Website/release.config:L253` declares
`<probing privatePath="bin;bin\HttpModules;bin\Providers;bin\Modules;bin\Support;" />`, and the port,
the host header and the application root were properties of an IIS site defined outside the
repository. A developer attached to that site; they did not choose a port.

**Target behaviour.** `backend/src/DnnMigration.Api/Properties/launchSettings.json` declares one
profile, `DnnMigration.Api`, which binds `http://localhost:8080`, sets
`ASPNETCORE_ENVIRONMENT=Development`, and opens `/swagger`. It declares no `iisSettings` block and no
`IIS Express` profile: the probing path above has no successor, and the target is Kestrel behind
nginx.

**Why 8080 and not 5000.** `docs/project-guide.md:L171-L172` documents the prior run as starting on
`http://localhost:5000`, the framework default, and that value is deliberately not adopted. The
decisive reason is `frontend/src/environments/environment.development.ts:L25`, which sets
`apiBaseUrl: 'http://localhost:8080/api/v1'` as an absolute URL because `ng serve` has no proxy in
front of it. A local API on any other port answers the development front end with a refused
connection, and no build step, unit test or linter reports it. The same number is fixed everywhere
else it appears: `docker/api.Dockerfile:L69` sets `ASPNETCORE_URLS=http://+:8080` and `:L73` exposes
8080, `docker/nginx.conf:L145` proxies `/api/` to `http://api:8080/api/`, and
`docker-compose.yml:L22-L23` exposes 8080 only to the private compose network. The image and compose
health checks probe `http://127.0.0.1:8080/health` from inside that container rather than
publishing Kestrel on the host. Keeping the local port equal to the container port also keeps the
profile honest about a constraint the
container enforces: `docker/api.Dockerfile:L39` creates `appuser` with `adduser -D -u 1000 appuser`
and `:L63` switches to it, and an unprivileged process cannot bind a port below 1024, so no profile
here may nominate one. `docs/project-guide.md` is a prior-run planning artefact and is reference
only; its own parenthetical, "or configured port", concedes the point.

**Two removals.** The file previously carried a top-level `$schema` key pointing at a remote schema
store; it is dropped, because a tracked file should not make a developer's tooling depend on a
network fetch to describe six well-known keys. A second profile that existed only to bind the
container port is dropped as redundant once the primary profile binds 8080.

**What the file deliberately does not contain.** No secret of any kind - no `Jwt__Secret`, no
`ConnectionStrings__Default`, not even a placeholder. This file is tracked, and a tracked sample is
how a real value eventually arrives; the legacy repository is the proof, since
`Website/development.config:L89-L90` commits both a `validationKey` and the 3DES `decryptionKey`
that, with `passwordFormat="Encrypted"` and `enablePasswordRetrieval="true"`
(`Website/release.config:L239-L245`), decrypts every stored password - and it is the *development*
file that leaks the most. Both values reach the host from the developer's own environment instead.
Nothing functional depends on this file existing: every validation gate builds, tests or runs the
container, `WebApplicationFactory<Program>` never reads it, and `dotnet run --no-launch-profile`
with the two variables exported is the supported equivalent.

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

**Corrected under code review: the separation is now enforced by the route, not only by a
field.** The two operations were distinguished by a value in the body while sharing one
endpoint and one authorisation gate, so a caller who could reach the endpoint at all could
select `reset` and obtain a credential change that proves nothing. They are now separate
actions on separate routes: the self-service change requires ownership of the account and
always verifies the current credential, and the administrative reset requires administration
of the portal the account belongs to. A reset is therefore unreachable without administrator
authorisation rather than merely undesirable, and a plain member is refused it **even on
their own account** — self-service is the change operation, which is what proof-of-possession
is for.

**The shipped `enablePasswordReset` default is left enabled**, matching
`Website/release.config:L240`. The authorisation gate is the fix; disabling the capability
would have removed a legacy feature rather than securing it.

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

**Target behaviour.** `CreatePortalRequest.HomeDirectory` is validated by
`Application/Validation/CreatePortalRequestValidator`, which declares the rule set
directly. It refuses a whitespace-only value, any control character, any of
`\ : * ? " < > |`, a leading `/` - which covers both a rooted path and a
`//server/share` form - an empty or blank segment, and any `.` or `..` segment. It then
confirms lexically that the combined path remains under a probe root. A single trailing
`/` is tolerated.

`UpdatePortalRequest.HomeDirectory` deliberately carries **no** lexical shape rule. An
earlier revision applied the same rule to both contracts from one shared file, but code
review established that the legacy update screen declared no validator on its
home-directory box at all, so the rule was an unauthorised addition on the update path
and was removed. The create path keeps it because its own contract states that no mapped
or absolute path is accepted from a caller. With one contract left applying the rules,
the shared file had no second caller to justify it and its members were moved into the
validator that applies them. Should a second contract ever carry this field, extracting
them again - rather than copying them - is the correct answer, for the reason given
below.

**Why.** The value names a directory the application will read and write, so a
rooted or traversing form is a path-traversal vector. Wherever more than one contract
maintains this column, those contracts must not be allowed to disagree about what it may
hold: two copies of a security predicate are two things to keep in step, and the weaker
copy would define the application's actual behaviour. That is why the rules are declared
once and would be extracted again rather than duplicated. The trailing separator is
tolerated because the legacy code appended one itself, so refusing it would reject values
the legacy application produced.

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

### Role-group writes bind their own contracts, and the response projection is no longer a request

**Legacy behaviour.** `Website/admin/Security/EditGroups.ascx.vb` held the group in a
single object across both operations and chose between them by testing the identifier
against `-1` at L42, L68 and L113, saving through the branch at L107-L111. One shape
therefore served as request and response because the screen never left the server.

**Target behaviour.** `POST /api/v1/role-groups` binds
`CreateRoleGroupRequest` and `PUT /api/v1/role-groups/{roleGroupId}` binds
`UpdateRoleGroupRequest`. Each declares exactly two members - `RoleGroupName` and
`Description` - and `RoleGroupDto` is now returned and never bound. Two validators
replace the single one that governed the projection, both reading the shared widths
and wording on `Application/Validation/RoleGroupTermsRules`.

**Why.** Both verbs previously bound `RoleGroupDto`, which also carries
`RoleGroupId` and `PortalId`. Neither is writable on either path: the store issues
the key on a create and the route names it on an update, and the owning portal is the
resolved tenant. A caller could therefore submit a group key or a foreign tenant,
receive `201` or `200`, and find that neither had been read - the request schema
promised two fields the service was structurally incapable of honouring. That is the
same authorisation-adjacent hazard recorded above for the portal identifier, and it is
resolved the same way: a contract with no such member has nothing to disagree with the
route about, on any path, including paths nobody has written yet.

**What the store actually writes.** Measured, not inferred. The terminal procedures
`AddRoleGroup` and `UpdateRoleGroup` write the name and the description; the group key
is an `IDENTITY` column and the portal key is set on insert from the procedure's own
tenant argument. The two request contracts are exactly that member set.

**Consequence for callers.** A caller still sending the wider projection is now
refused with a field-keyed `400`. The API globally disallows unmapped JSON members, so
a surplus identifier cannot be silently discarded while the response reports success.
The change removes the false promise from the schema and makes drift visible at runtime.

### Profile-definition writes bind two different contracts, because the two procedures write different columns

**Legacy behaviour.** `Website/admin/Users/EditProfileDefinition.ascx.vb` edited one
object through a reflective property editor and chose between adding and updating by
testing the identifier against the null-integer sentinel at L449, calling the add path
at L451 and the update path at L459.

**Target behaviour.** `POST /api/v1/profile-definitions` binds
`CreateProfilePropertyDefinitionRequest`, carrying ten members;
`PUT /api/v1/profile-definitions/{propertyDefinitionId}` binds
`UpdateProfilePropertyDefinitionRequest`, carrying nine.
`ProfilePropertyDefinitionDto` is returned by every read and by both successful
writes, and is bound by nothing. Two validators replace the single one that governed
the projection, both reading the shared widths, pattern and wording on
`Application/Validation/ProfileDefinitionTermsRules` so that no rule can hold on one
verb and not the other.

**Why the two contracts differ from each other.** This is not symmetry for its own
sake - the procedures genuinely disagree. `AddPropertyDefinition`
(`04.06.00.SqlDataProvider:L1101`) declares eleven parameters, one of which is
`@ModuleDefId`. `UpdatePropertyDefinition` (`04.05.00.SqlDataProvider:L1685`) declares
ten, and `@ModuleDefId` is not among them; its `UPDATE ... SET` list does not name the
column either. A module association can therefore be established when a property is
declared and never afterwards, so a single shared shape would have had to advertise the
member on a verb that discards it. It did, and a caller reassigning a definition to
another module through `PUT` was answered `200` having changed nothing.

**Three members withdrawn from the write surface.** `PropertyDefinitionId` and
`PortalId` arrive from the route or are issued by the store, for the reasons already
recorded for the portal and role-group identifiers. `Visibility` is a third and
different case: it is not a column on `ProfilePropertyDefinition` at any point in the
88-script chain - the stored per-account counterpart lives on `UserProfile` - so the
value a definition reports is a default hint the API derives from the "User Accounts"
module setting `Profile_DefaultVisibility`. It is retained on the response, because a
client needs the resolved hint, and removed from both requests, because no write could
persist it. An earlier attempt to bound that member to its three documented modes was
withdrawn as an invented rule; removing it from the request surface is what that
correction should have been.

**One divergence corrected while splitting.** The response contract previously
documented the property name as immutable once a definition exists, on the strength of
the legacy class marking it read-only. That documentation was wrong.
`IsReadOnly(True)` on `ProfilePropertyDefinition.vb:L228` is a rendering hint to the
reflective editor, and the terminal update procedure assigns
`PropertyName = @PropertyName`. The name is therefore writable on the update verb, the
member is required there because the column is `NOT NULL`, and a rename onto a name
another definition of the same portal and module already holds is reported as `409`
with the definition being edited excluded from the comparison - otherwise every
ordinary edit, which resubmits the name it read, would collide with itself.

**Consequence for callers.** As with the role groups, nothing that breaks an existing
caller: surplus members on a submitted body are still ignored rather than refused. What
changes is that the published schema now describes what each verb writes.

### The application layer refuses an undefined enumeration value itself, and where that value can actually come from

**Legacy behaviour.** Not applicable in the strict sense - the legacy screens used bare integers with
sentinel bands rather than enumerations, and the value came from a server-side drop-down the caller could
not author. `Roles.ascx.vb:L129` assigns `-2` for "all roles" and the role-group selector supplies `-1` for
"global roles"; a value outside those bands could not be sent.

**Target behaviour.** Two application members now test membership and refuse an undefined value:

- `IRoleService.ListRolesAsync` answers `role_group.scope_invalid` for a `RoleGroupScope` outside its two
  members.
- `IPermissionService.GetPermissionKeysAsync` answers `permission.key_invalid` for a `PermissionKey` filter
  outside its four.

Both codes map to `400` through the shared translator, neither token appearing in any of its special sets.

**What was verified, and what the verification changed.** The review that prompted this work described the
gap as an HTTP-reachable input-validation exposure - `?scope=999` answered `200` with the full role list,
and `?permissionKey=99` answered `200` with `["99"]`. That was checked against the running API rather than
accepted, and **it is not reachable over HTTP**:

- `?scope=999` and `?permissionKey=99` are refused by MVC model binding with `400` and
  `errors["scope"] = ["The value '999' is invalid."]` - the action body never runs. MVC's enumeration binder
  tests DEFINED membership for a non-flags enumeration.
- `?scope=0`, `?scope=1`, `?permissionKey=0` and `?permissionKey=3` all bind and answer `200`, which proves
  numeric binding works and that the refusal is specifically the membership check rather than a failure to
  parse a number.

The finding's *service-layer* observation was nonetheless correct: neither member tested membership, and a
CLR enumeration is an integer at run time, so `(RoleGroupScope)999` is a constructible value. The guards are
therefore implemented, and the reason recorded for them is the accurate one - the Application layer is a
public API reachable by callers that never touch MVC - rather than the HTTP exposure the finding described.

**Why the guards are not redundant.** Without them the two members answer wrongly for any non-HTTP caller,
and the two failure modes differ in severity. The role listing's narrowing tests only for equality with
`Ungrouped`, so an undefined scope matched no branch and the member returned **every role in the portal**
reporting success - a wider set than was asked for. The permission-key catalogue answers a lone key filter
from the closed enumeration by returning the filter itself, so it returned `["99"]`: it **fabricated** a key
that names no member, no row and no grant. On the scoped branches the same value became a comparison operand
and produced an empty set, which reads as "declared nowhere" rather than "does not exist". An invariant
belongs to the layer that owns it, which is why `PermissionService` already carried the identical
`Enum.IsDefined` test on the two non-nullable key parameters of its evaluation members - and those *are*
reached from the authorization path.

**Query-bound and body-bound enumerations are not the same problem, and this is the load-bearing
distinction.** The binder check above applies to query and route values. `System.Text.Json` performs no such
check for a request body, which was also measured: posting `billingFrequency` as the number `99` is refused
by this solution's own converter and validator, not by MVC. So body-bound enumerations must carry explicit
rules and query-bound ones are already covered - the codebase's existing split between converter rules and
validator rules follows exactly that line, and a future reader must not "simplify" either side on the
assumption that the other's protection applies.

**Documentation corrected in both directions.** Four public documentation sites claimed that "an
unrecognised spelling is refused by model binding", which was true but incomplete and was being used as the
reason no service-side test existed. They now state that the binder refuses an undefined *number* too, that
this was measured, that the same claim is false for a JSON body, and that the service-side test exists for
non-MVC callers. An interim revision of these notes asserted the opposite - that the binder does not check
membership - and that assertion was wrong; it is recorded here so that nobody reinstates it.

### The role-membership listing is orderable by three account fields the boundary used to refuse

**Legacy behaviour.** `SecurityRoles.ascx.vb:L246` bound `GetUserRolesByRoleName` to a grid that rendered
the account and sorted on what it rendered, with no page-size or field bound of any kind.

**Target behaviour.** `GET /api/v1/roles/{roleId}/users` binds its own
`RoleUserPagedRequest`, whose validator applies the ten-name role-membership sortable set. `CreatedDate`,
`LastLoginDate` and `IsApproved` are accepted where they were previously refused.

**Why.** The action bound `UserPagedRequest`, the ACCOUNT collection's contract, so
`UserPagedRequestValidator` resolved for it and applied that collection's seven names - while
`RoleService.ListRoleUsersAsync` enforces the role-membership set of ten and
`RoleService.OrderRoleMemberships` has an ordering arm for each of the ten. Three orderings the
application was fully able to perform were unreachable over HTTP, refused at the boundary as unknown
fields. The endpoint advertised less than it implemented.

**This is the inverse of the defect that introduced the per-collection contracts, not a relaxation of
them.** Sharing one request type across collections made a listing *accept* a field name it would silently
discard - too permissive. Borrowing another collection's type made this listing *refuse* a field name it
would have honoured - too restrictive. Both are the same underlying mistake: the validator's vocabulary and
the service's vocabulary must be the same vocabulary, and one request type per collection, each closed over
the one set that collection honours, is what guarantees it. `UserPagedRequest` and its validator are
untouched.

**The two vocabularies legitimately differ, and both verdicts are correct.** The account listing pages in
the STORE, so it cannot order by the three columns the external `aspnet_*` membership objects supply - they
are filled after the page has been skipped and taken, and ordering by one of them would order a single
arbitrary page rather than the collection. The role-membership listing materialises the role's assignment
rows composed with their accounts and pages IN MEMORY, so those same three values are present on every row
before any page is cut. The unit and integration suites assert the same three names as refused by one
listing and accepted by the other, in the same tests, so the asymmetry is deliberate and cannot drift.

**What is still not orderable.** The two assignment dates the membership projection carries. The legacy
grid offered no ordering by them, so admitting them would be a new feature rather than preserved
behaviour, and the role identifier is fixed by the route for every record on the page, so ordering by it
could not change any order.

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

### Correction: the delivered sign-in evaluates the lock and approval BEFORE the credential, and answers every refusal uniformly

**What this entry corrects.** The entry immediately above describes a sign-in that
verifies the password first and then reports five distinguishable outcomes. Four of
its claims do not match the delivered service, and this file is append-only, so they
are left in place and superseded here. Where the two disagree, this entry is the one
that matches `backend/src/DnnMigration.Application/Services/AuthService.cs`.

**Corrected claim one — the gate order.** The delivered order is the provider's own:
resolve the account within the tenant, evaluate the lock, evaluate approval, and only
then compare the credential — reproducing the guard at
`AspNetMembershipProvider.vb:L1481` rather than reordering it. Verifying the
credential first was considered and **rejected**, because it defeats the very control
the lock is: a locked account exists precisely so that its credential is no longer
compared, and comparing it anyway both lets a guess be confirmed against a locked
account and spends an adaptive hash verification on every attempt against one, which
is a denial-of-service lever rather than a hardening measure. The same reasoning
applies to the approval gate: comparing the credential of an unapproved registration
would report credential failure separately from approval failure, which anyone able
to register could use as a credential oracle.

**Corrected claim two — one answer for every refusal.** The delivered service does
**not** distinguish "enter your code" from "that code is wrong" to the caller. An
unknown account name, a wrong credential, an account belonging to another tenant, an
account with no credential on file, a tenant that does not exist and an unapproved
registration all receive the identical failure `auth.invalid_credentials` with
identical wording. The approval gate closes *before* any credential is compared, so
answering the caller specifically would confirm to an unauthenticated party that a
named account exists and is merely awaiting verification — without that party having
proved anything at all. The three legacy message keys are nevertheless preserved and
still determined on every such refusal, as
`auth.verification_required` (`EnterCode`), `auth.verification_code_invalid`
(`InvalidCode`) and `auth.account_not_approved` (`UserNotAuthorized`), because the
mapping of those outcomes is owned by the application layer; the determination is
made and then deliberately withheld from the response. The one refusal that *is*
reported specifically is the lock, and only to a caller already entitled to it — a
host account, or the tenant's administrator identified by identifier rather than by
role name.

**Corrected claim three — the outcome vocabulary.** The delivered reason codes are
the lower-dotted `auth.` family, not the upper-snake names the entry above lists:
`auth.request_invalid`, `auth.invalid_credentials`, `auth.locked_out`,
`auth.invalid_refresh_token`, `auth.user_not_found`, the three approval codes above,
and the two advisory codes `auth.insecure_admin_password` and
`auth.insecure_host_password`. `TOKEN_STORE_UNAVAILABLE` keeps the token service's
own upper-snake form because it is propagated unchanged, which is what lets a caller
tell "your credential was refused" from "your credential was accepted and the session
could not be recorded". The Api edge maps the first five to `401`, `auth.user_not_found`
to `404` and `auth.request_invalid` to `400`.

**Corrected claim four — the automatic-unlock window is NOT preserved.** The entry
above states that an account whose lockout has elapsed is unlocked and the sign-in
continues, as `AspNetMembershipProvider.vb:L1454-L1461` did. **That is not the
delivered behaviour.** The window depended on a lockout-duration setting the
preserved credential policy does not carry, and honouring it would perform a
security-relevant write on an anonymous request path. A locked account is therefore
cleared by the administrative unlock member on `IUserService`.
**Operational consequence:** an account locked by repeated wrong credentials stays
locked until an administrator unlocks it, however long the holder waits. For an
installation that relied on the window this is a support obligation to plan for, not
an incident to discover.

**The residual weakness the entry above identified correctly, and its compensating
control.** A verification code is still compared, and a matching one still approves
the account and persists it, *before* the credential is compared — so a party who can
guess a tenant identifier and an account identifier can still flip somebody else's
pending registration to approved without holding their credential, exactly as the
legacy did. That is preserved rather than repaired because repairing it means
reordering the gates, whose cost is set out above and is higher. It confers no
sign-in: the credential gate still stands, and the outcome is still the uniform
denial. The compensating control is rate limiting on the credential endpoints,
recorded in its own entry in this file. This is stated so the trade-off is on record
rather than lost between two entries that each describe half of it.

### The sign-in status enumeration is computed in full, and a locked-out account is mapped to a refusal

**Legacy behaviour.** `Login.ascx.vb:L163` declared a status variable initialised to
the failure member and passed it by reference into `UserController.ValidateUser` at
`:L164`, which passed it on to the provider to be mutated while separately returning
an object it set to `Nothing` on refusal. The page then derived its own authenticated
flag at `:L187` from `loginStatus <> UserLoginStatus.LOGIN_FAILURE`, in the `Else` arm
of the `:L168` test that special-cases the not-approved member alone.

**The defect that produces, stated exactly.** Every member other than not-approved
reaches that inequality, so `LOGIN_USERLOCKEDOUT` — which is 3, not 0 — evaluated as
authenticated. The provider compounds it: a locked account never has its credential
compared (`:L1481`) and is returned as `Nothing` (`:L1505-L1508`), so the legacy
raised an authenticated event carrying no account at all.

**Target behaviour, and the mapping decision this service owns.** The by-reference
argument is eliminated: a local status occupies the same place in the flow, is moved
by the same three gates in the same order, and is then mapped **once** onto the
returned result. The complete mapping is:

| Status | Value | Legacy `authenticated` | Delivered outcome |
| --- | --- | --- | --- |
| `Failure` | 0 | false | Refusal. The attempt is recorded against the account, which is what produces the lock. |
| `Success` | 1 | true | Success. Token pair issued. |
| `SuperUser` | 2 | true | Success. Token pair issued, and the post-credential advisories are skipped. |
| `UserLockedOut` | 3 | **true** | **Refusal** — a deliberate, documented divergence. |
| `UserNotApproved` | 4 | false | Refusal, answered uniformly; the approval ladder is determined and withheld. |
| `InsecureAdminPassword` | 5 | true | Success **with an advisory reason**, and the must-change flag set. |
| `InsecureHostPassword` | 6 | true | Success **with an advisory reason**, and the must-change flag set. |

**Why the locked-out divergence is taken.** `AAP 0.9.1` requires a discovered defect
to be annotated and not fixed *unless it blocks delivery*. A lock-out that does not
lock out is the failure of the only control standing between an attacker and unlimited
credential guessing, and preserving it would make the failed-attempt bookkeeping the
same method performs pointless. It is therefore treated as blocking. The legacy tree
is not touched and stays byte-identical; the divergence is recorded here and annotated
in place.

**Why the two insecure-password members are successes.** They are promotions of an
already-successful authentication — `UserController.vb:L1144-L1152` *replaced* a
successful status when a shipped default credential was presented, and the caller was
signed in regardless. Refusing them would lock an installation out of the two accounts
every installation begins with. They travel two ways at once: as an informational
reason on a successful result, which keeps the two cases distinguishable at the Api
edge, and as the must-change advisory on the response body, because forcing a
credential change is the legacy remediation intent for both. No legacy status ordinal
reaches the wire.

**One widening.** The legacy compared the shipped account name with VB's `=` operator
under the default binary comparison, making it case-**sensitive**, so an account
signing in as `Admin` escaped the advisory while being just as exposed. The delivered
comparison is case-insensitive, which is consistent with the case-insensitive
resolution that admitted the account in the first place. The credential itself is
still compared exactly, and neither the matched name nor the matched credential ever
appears in a reported message.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Application/Dtos/Auth/LoginResponse.cs`,
`backend/src/DnnMigration.Domain/Enums/UserLoginStatus.cs`.

### The credential-expiry advisories are read from the installation settings, including a legacy key with a trailing space

**Legacy behaviour.** `UserController.vb:L1171-L1197` returned the post-credential
validation enumeration: an administrator-forced update took precedence, otherwise —
and only when `PasswordConfig.PasswordExpiry` exceeded zero — the stored change date
plus that window was compared against `Today`, reporting expired when it had passed
and expiring when it fell inside the reminder window. `PasswordConfig.vb:L52-L64`
reads the window from the installation-wide setting `PasswordExpiry`, defaulting to
zero, and `:L77-L88` reads the reminder from `PasswordExpiryReminder`, defaulting to
seven.

**Target behaviour.** The same rule, in the same precedence, evaluated through
`IHostSettingsService` over the real `HostSettings` table, and surfaced as the
`MustChangePassword` and `PasswordExpiring` flags on the sign-in response. The
forced-update case still suppresses the expiry evaluation entirely, so the two are
never reported together — the legacy enumeration was single-valued and could not
report both. The evaluation is skipped in full for a host account, which is measured:
`Website/admin/Authentication/Login.ascx.vb:L511` wraps the call in
`If Not objUser.IsSuperUser Then`.

**A legacy key with a trailing space is preserved verbatim.**
`PasswordConfig.vb` reads the reminder setting at `:L81-L82` and writes it at `:L88`
under the name `"PasswordExpiryReminder "` — **with a trailing space**. The getter and
the setter agree with each other, which is why the defect was never visible, and the
consequence is that the row an existing installation holds carries the space in its
key. The delivered code reads what the legacy wrote rather than what it meant to
write. Trimming the name would silently stop honouring a configured reminder window
on every existing installation, so the quirk is recorded here rather than repaired,
per `AAP 0.9.1`.

**A calendar comparison that can differ by one day.** The legacy compared against
VB's `Today`, the server's **local** calendar date. The delivered comparison is
against the coordinated universal date, because the injected clock is
universal-time-only. For an installation east or west of the meridian a credential can
therefore be reported expired, or reminded about, up to **one calendar day** earlier or
later than the legacy would have. Universal time is nevertheless correct for the
target: a container has no meaningful local zone, and two replicas in two zones would
otherwise disagree about the same account.

**An absent change date yields no advisory rather than an expiry.** The legacy read a
non-nullable date that the null-sentinel helper had already collapsed to the minimum
date when the column held no value, so an account with no recorded change date
computed the minimum plus the window and was reported **expired**. The target models
the column as nullable and treats an absent date as "no expiry can be computed". This
is a deliberate divergence in the safe direction: the alternative forces a credential
change on every account whose date was never recorded, on the strength of a sentinel
rather than of a fact. The case is unreachable against a real installation, because
the externally installed membership objects populate the column when a credential is
created.

**An unusable setting is tolerated rather than fatal.** The legacy read was a late
conversion compiled with Option Strict off and threw on any non-numeric value. The
delivered read is explicit, total and culture-invariant: an absent, blank or
unparseable setting applies the default rather than faulting a sign-in over a mistyped
configuration row.

**Target behaviour.** The legacy condition at `UserController.vb:L1189-L1193`
combined the tenant's `Security_RequireValidProfileAtLogin` membership setting with
the completeness check at `ProfileController.vb:L305-L319`. That rule now has one
authoritative implementation:
`IUserService.RequiresProfileCompletionAsync` reads the setting and the portal-scoped
required profile-property definitions, then compares them with the account's values.
The sign-in and refresh paths ask that service for the result after credentials have
been accepted rather than duplicating profile policy inside authentication.

**Blocking consequence.** `MustUpdateProfile` is emitted in the response and access
token, re-evaluated on refresh, and enforced by the remediation authorization handler.
While it remains true, only the account owner's profile read/update endpoints and the
explicit authentication/remediation routes are available; unrelated protected API
operations are refused. The Angular session model carries the same flag, so the
client-side state no longer contradicts the server-enforced contract.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Application/Dtos/Auth/LoginResponse.cs`.

### Clarification: a stored credential IS replaced on sign-in when its work factor is superseded

**What this clarifies.** The correction earlier in this file states that no credential
is upgraded on sign-in. That is exactly right about the **legacy reversible format**,
and it is worth stating precisely what it does not cover, because the delivered
sign-in does perform one replacement and the two statements would otherwise appear to
contradict each other.

**The distinction.** A pre-migration value cannot be verified at all: verifying it
would need the symmetric material committed at `Website/release.config:L89-L93`, which
is out of scope and is reproduced nowhere, and the delivered hasher holds exactly one
algorithm with no legacy branch. Such an account regains access through an
administrative reset, and nothing on the sign-in path changes that. What the sign-in
path *does* do is ask the hasher whether an **already one-way** representation was
produced at a cost the current configuration considers superseded, and replace it when
it was. The hasher's own implementation records that this is the member's only
reachable use, because the method is consulted only after a verification has already
succeeded and a legacy value can never accompany a successful verification.

**Why it is contained.** The replacement is transparent: no extra round trip, no
change to the response and no new failure mode. A persistence failure must not fail a
sign-in whose credential was correct, so it is caught and the account simply keeps a
still-valid representation at the superseded cost until the next successful sign-in
tries again. Cancellation is deliberately excluded from that containment and continues
to propagate, because a cancelled request is not a failed write.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`.

### CORRECTION: the sign-in audit record IS emitted, through an abstraction the application layer owns

**This supersedes the gap reported below, which was closed under code review.** The
reasoning that follows was sound about `ILogger<T>` and wrong about the conclusion it
drew from that. `ILogger<T>` genuinely cannot be named in the application layer, and
the manifest is genuinely not the thing to change — but neither fact requires the
application layer to stay silent. It requires the application layer to declare *its own*
contract and let a project that already has logging implement it, which is the same
ruling the layer's manifest records for `IOptions<T>`, applied properly rather than read
as a prohibition.

**What is delivered now.** `Application/Abstractions/IAuditSink.cs` declares the audit
contract in terms of the base class library only, so it names nothing the layer cannot
reference. `Infrastructure/Services/LoggingAuditSink.cs` implements it over `ILogger`, where
**What is delivered now.** `Application/Abstractions/IAuditTrail.cs` declares the audit
contract in terms of the base class library only, so it names nothing the layer cannot
reference. `Infrastructure/Services/AuditTrail.cs` implements it over `ILogger`, where
logging is already available transitively, and is registered as a singleton in
`Infrastructure/DependencyInjection.cs`. `AuthService` takes the contract and records the
outcome of **every** sign-in at the one point after which the outcome can no longer
change — immediately after the shipped-credential promotion and before the status is
translated — under a stable event name drawn from `AuditEventNames`.

An earlier revision of this note described the contract as `IAuditTrail`/`AuditTrail` with
three members. Two abstractions for this one concern were authored independently and the
delivered tree carries exactly one, `IAuditSink`; the other was removed rather than kept
beside it, because two audit streams that can disagree about the same operation are worse
than either alone. The reasoning below is unaffected — it was never about the type's name —
and the safety property is now stated in the shape the delivered contract actually has.
translated — under stable event `1001 SignInOutcome`.

**The per-gate loss described below is therefore recovered.** The specific gate that
closed a sign-in *is* now recorded, because the emitted event carries the computed status
member name itself: an unknown tenant, an unknown account name, an account with no
credential record, and every outcome the status enumeration can express, are separate
values of the `SignInOutcome` property rather than a single "refused" observation.

**Two deliberate divergences from the legacy trail, both supersets.** The legacy entry
was raised for the locked-out and failure outcomes only; every outcome is now audited,
including success. And where the legacy call always passed `Null.NullInteger` as the
account identifier — so the legacy trail never recorded *which* account failed — the
resolved identifier is now carried when one exists and is `null` when the account name
matched nothing.

**The closed event shape is the safety property, not a convenience.** The contract exposes
exactly one member, `void Record(AuditEvent)`, and `AuditEvent` declares a closed set of
init-only members: an outcome, a tenant, an actor and its name, a subject, a resource type
and identifier, a failure code, and a string-keyed property bag. A credential is not merely
omitted from the call sites; there is no member that could carry one, so no future call site
can introduce one. The member returns `void` rather than `Task`, so an audit entry can never
fail, delay or cancel the operation it describes, and the implementation is documented never
to throw — including for a null event, which is discarded rather than escalated.
**The closed method set is the safety property, not a convenience.** The contract exposes
exactly three members and each takes only the fields its event needs. A credential is not
merely omitted from the call sites; it is *unexpressible* through the contract, so no
future call site can introduce one. The members return `void` rather than `Task`, so an
audit entry can never fail, delay or cancel the operation it describes.

**The network-address note below still stands unchanged.** The address remains absent from
the layer, and remains recorded by the Api request log.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Abstractions/IAuditSink.cs`,
`backend/src/DnnMigration.Application/Abstractions/AuditEvent.cs`,
`backend/src/DnnMigration.Infrastructure/Services/LoggingAuditSink.cs`,
`backend/src/DnnMigration.Application/Abstractions/IAuditTrail.cs`,
`backend/src/DnnMigration.Infrastructure/Services/AuditTrail.cs`,
`backend/src/DnnMigration.Application/Services/AuthService.cs`.

### Superseded gap report: the sign-in audit record cannot be emitted from the application layer

**Legacy behaviour.** `UserController.vb:L66-L82` wrote an audit entry whose log type
key was `loginStatus.ToString` at `:L80`, carrying the properties `IP`, the tenant
identifier, the tenant name, the account name — passed through
`PortalSecurity.InputFilter` with `NoScripting`, `NoAngleBrackets` and `NoMarkup` at
`:L77` — and the account identifier. It was raised for the locked-out and failure
outcomes only (`:L1138-L1141`), and it was always passed `Null.NullInteger`, minus
one, as the account identifier, so **the legacy trail never recorded which account
failed.**

**What is delivered, and what is not.** The mapping of these outcomes onto stable
event names — which the enumeration entry earlier in this file assigns to the
application layer — is discharged: the sign-in service computes the status for every
outcome and its member name *is* that stable name. **Emission is not possible in that
project.** `ILogger<T>` does not resolve there: the application layer declares
FluentValidation and nothing else, per `AAP 0.6.1`, and the logging abstractions are
absent from the reference pack a class library targets. This was established by
compiling a probe, which fails with `CS0234` on the namespace and `CS0246` on the
generic, rather than by inspection. The layer's manifest already records an identical
earlier attempt — made for `IOptions<T>` — that was reverted with the ruling that the
consumer changes rather than the manifest, and the same ruling is applied here.

**The compensating control.** `Api/Middleware/RequestLoggingMiddleware.cs` records
every request with its method, path, status code and elapsed time, raising a refused
sign-in to warning level, and `Api/Middleware/CorrelationIdMiddleware.cs` binds the
correlation identifier those events are read against. The log-forging concern that
`PortalSecurity.InputFilter` addressed is answered structurally rather than by a
filter: the Api layer emits structured properties, so a submitted value is a property
of an event and can never become part of its message template.

**What is lost, stated plainly.** The specific gate that closed a sign-in is not
recorded anywhere — the request log records that the attempt was refused, not which of
the lock, the approval gate or the credential comparison refused it. An installation
that needs per-gate audit must supply a logging abstraction to the application layer,
which is a manifest decision rather than a code change.

**The caller's network address is absent from the layer altogether.** It was the
seventh argument at `Login.ascx.vb:L164` and the legacy service did nothing with it
but record it. The sign-in request contract carries no address property, and reaching
for the ambient request context is confined to the tenant-resolution middleware, so
the address is recorded by the Api request log instead. Absence is a stronger
guarantee than acceptance here: an address that could influence the outcome would be
an input the caller controls.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Application/DnnMigration.Application.csproj`.

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
exports exactly one public type: `DependencyInjection`, whose only declared public method is
`AddInfrastructure`. Nothing outside the assembly can name an implementation, let alone
construct one. The context, unit of work and nine aggregate repositories are scoped behind
Domain-layer contracts; the password hasher, clock, cache, refresh-token store and token
service are singletons. `JwtTokenService` reaches scoped entitlement data through
`IServiceScopeFactory` for each refresh instead of capturing a repository in its constructor.
The fourteen provider declarations do not become fourteen replacement switches: each legacy
family becomes either one explicit container registration or a typed option value bound by
the API layer, so provider indirection is removed rather than reproduced. The 269-member
provider contract is decomposed by aggregate boundary, with only the in-scope subset
implemented; portal creation then stages its five-table write and commits it through the
single unit-of-work boundary described in “Portal persistence contract” above.

**Why.** A caller that cannot name the password hasher cannot come to depend on which
algorithm is in use, and a caller that cannot name the token store cannot come to depend on
its storage medium, locking strategy or retention policy; swapping either then costs one
line. An unresolvable dependency is also reported at startup with the name of what is
missing, which is strictly better than a placeholder that defers the failure to a request.

**Note on test visibility.** Exactly **one** `InternalsVisibleTo` grant is declared in this
solution — `DnnMigration.Infrastructure` names `DnnMigration.UnitTests`, and no other
assembly — and it is deliberately narrow. An earlier revision of this note recorded that no
such grant existed anywhere and argued that resolving contracts from a container was always
sufficient. Most of that argument still holds and still governs: a test that can construct a
concrete type directly can pass while the registration is broken, and anything a *caller*
needs to observe belongs on the Domain contract.

Where the argument did not survive contact with the code is the two security components in
`Infrastructure/Security` whose decisions are **algorithmic rather than relational**. The
work factor a hash is produced at, the pre-hash that removes BCrypt's input-length limit,
the refusal of a malformed stored value, and the pseudo-principal rules in the permission
evaluator are all decided in code, with no database and no observable difference through the
`IPasswordHasher` contract. An endpoint test proves that *some* implementation is wired up
and returns the right status code; it cannot observe **which cost** a hash was produced at.
The consequence of having no seam was not an absence of tests but something worse: both
suites had drifted into asserting a *private re-implementation* of the algorithm they
claimed to protect, so they passed whatever production did and read as coverage while
protecting nothing. AAP 0.5.1.5 sources those suites from those files, so the seam realises
the plan rather than working around it.

Two bounds are load-bearing and are recorded here because they are the reason this grant is
safe. **No type was made public** to enable it — every concrete type in that assembly is
still internal, and widening one would have permanently enlarged the assembly's surface
instead. And **no production layering changed**: this is a change to the *test* project
graph only. Do not add a second assembly to the grant, and do not make a type public in
order to reach it from a test.


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

**And the exception handler is the ONLY owner of exception diagnostics.** The
request-logging stage sits outside the handler, so a failure passes through it first. It
records the failure's **type name and nothing else** - no exception object is handed to
the logger there. Passing one would have the framework render the message, every inner
message and the whole stack trace into the request log, ahead of and in addition to the
allowlisted description above: the redaction would be defeated in the one log that is
highest-volume, longest-retained and most widely readable, and the same failure would be
recorded twice. The two entries share a correlation identifier, which is how a reader
moves from the type name in the request log to the bounded description in the handler's
entry. Verified by driving a failure whose message named a server, a database and a
login: the request-log entry carried the exception type, a null exception object, and
neither the message nor any stack frame.

**Annotated in code at.** `backend/src/DnnMigration.Api/ErrorHandling/GlobalExceptionHandler.cs`,
`backend/src/DnnMigration.Api/Middleware/RequestLoggingMiddleware.cs`.

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
no token yet, and the health views are probed by the container's own health check and by any
orchestrator in front of it with no credential at all.
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

**Target behaviour, as corrected under code review.** The policy is registered with
`AddRequirements` and a `PortalAdministratorRequirement`, and decided by a scoped
`PortalAdministratorAuthorizationHandler` that is **anchored to the portal the route names**.
It resolves the target portal from the route's `portalId` first and falls back to the
resolved tenant only when the route names none, then loads *that* portal to read *its*
administrator role and verifies time-bounded membership of it.

**Why the correction was needed.** The handler previously read the administrator role from
`IPortalContext` alone — the tenant the caller arrived through — while the action went on to
act on the portal named in the route. Those are not the same portal. An administrator of one
tenant, arriving through their own host name, therefore satisfied the policy for a request
addressing **another** tenant's portal, and the strongest reach was over portal aliases: an
alias addressed by its own global identifier carries no portal binding at all, so retargeting
one moves which tenant a host name serves. The gate must be evaluated against the resource
being acted on, not against the door the caller came through.

**Operations with no portal binding are host-scoped instead.** Listing every portal, creating
a portal, and the identifier-addressed alias operations cannot be anchored to a route portal
because there is none, so they require `HostAdministrator` — verified against authoritative
super-user state — rather than falling back to whichever tenant the caller resolved to. Every
`{portalId}`-nested action keeps the route-anchored portal policy.

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

**Note on transport security.** TLS terminates at the browser-facing edge, and the container
health check probes the API over plain HTTP on the internal network, so the API listens on plain
HTTP by design. `UseForwardedHeaders` is installed so the application sees the original scheme and
caller address from the headers the proxy sends, and from trusted hops only. HTTPS redirection is
then enforced on top of that in the production overlay, branched so that the plain-HTTP health
probe and any loopback-addressed request are never answered with a redirect - the two cases in
which a redirect would break the deployment rather than protect it.

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

### Tenant resolution refuses a request that can only get its tenant from the host name, and lets every other request continue

**Legacy behaviour.** The page pipeline resolved the tenant before anything else and
had nowhere to go if it could not.

**Target behaviour, as corrected under code review.** An unresolved tenant no longer
lets every request through. The middleware now asks whether the *matched endpoint* can
obtain its tenant by any means other than the host name, and refuses the request with
`403` and the shared problem-details vocabulary — type
`urn:dnnmigration:error:portal.tenant_unresolved` — when it cannot. Continuing was the
original behaviour and it was wrong: an endpoint whose only source of tenant is the host
name was being served with no tenant at all, which is the condition tenant isolation
exists to prevent.

**Three cases are deliberately still served.** A request that matched **no endpoint** is
a routing 404 and that answer belongs to routing; refusing it here would replace a
truthful "no such address" with a misleading one and would tell a caller probing for
addresses that the host is unconfigured. An endpoint marked `TenantOptionalAttribute`
carries a written justification for why it does not need one — sign-in, which may be
told its tenant explicitly; the permission catalogue, which is host-wide; the
identifier-addressed alias operations, which are host-scoped; and the pages addressed by
identifier alone, which take their tenant from the caller's signed portal claim. And an
endpoint whose **route carries a `portalId`** is served, because a route value is set by
route matching alone — it cannot be supplied through the query string or a header — so
its presence means the tenant arrives by a means other than the host name. That is not
treated as an authorisation decision: the portal-administrator policy separately verifies
administration of *that exact portal* against stored role membership, and every service
verifies that the record it acts on belongs to it.

**The original concern that motivated continuing is preserved.** The finding that
provoked it — that a propagating lookup failure turned *every* `/api` request into a 500,
including requests needing no tenant — still holds, and is still avoided. The refusal is
scoped to endpoints that genuinely depend on the host name, so the health endpoint
continues to report the state of the database rather than the state of the alias table,
and no request that never needed a tenant is affected. A failed lookup is still logged.

**Verified by running it.** With a host name that matches no alias,
`GET /api/v1/module-definitions` — no `portalId` in its route and not tenant-optional —
answers `403` with the problem document, while the same route on a configured alias
answers `200`. A `portalId`-bearing route answers `200` on the unknown host, as intended.

**Operational consequence.** A misconfigured alias now produces an explicit, uniform
refusal from the endpoints that depend on it, and still not a total outage.

## Request pipeline

### The eight managed modules and seven handlers become ten explicit pipeline stages

**Legacy behaviour.** `Website/release.config` declared eight managed modules at L67-L74 —
`ScriptModule`, `Compression`, `RequestFilter`, `UrlRewrite`, `Exception`, `UsersOnline`,
`DNNMembership` and `Personalization` — and seven handlers at L77-L83, for `ScriptResource.axd`,
`*_AppService.axd`, `*.asmx`, `Logoff.aspx`, `RSS.aspx`, `LinkClick.aspx` and `*.captcha.aspx`. All of
them were located by assembly probing, `privatePath="bin;bin\HttpModules;bin\Providers;bin\Modules;bin\Support;"`,
and `Website/Default.aspx.vb` then ran a 700-line Web Forms page lifecycle on top of them.

**Target behaviour.** One explicit pipeline, composed in
`backend/src/DnnMigration.Api/Extensions/ApplicationBuilderExtensions.cs` and mapped as stages 1-10 in
`backend/src/DnnMigration.Api/Program.cs`: exception handler, correlation id, request logging, routing,
CORS, authentication, authorization, portal-alias resolution, controllers, health checks. Five further
stages are interleaved without displacing any of the ten — HSTS and HTTPS redirection under guards, a
tenant path-base stage before routing, the credential rate limiter after CORS, and the documentation
console between authentication and authorisation. There is no assembly probing, because .NET 8 has no
equivalent and needs none.

**Why the difference is deliberate.** Only three of the fifteen legacy entries describe a responsibility
this API still has, and each is met natively rather than ported: exception handling by
`IExceptionHandler` with problem details, membership by bearer authentication, and compression by the
reverse proxy. The rest belong to subsystems this migration excludes — users-online tracking,
personalisation, URL rewriting, the AJAX script handlers, RSS syndication, file-server link clicks and
the CAPTCHA handler. A configured module chain resolved by reflection also defeats every compile-time
check: an entry naming a type that no longer exists fails at first request rather than at build, which
is the specific property that made the legacy pipeline expensive to reason about.

### Affiliate tracking and site logging are not ported

**Legacy behaviour.** `Website/Default.aspx.vb:L304` ran both on every request.
`ManageRequest` read an `AffiliateId` query parameter, called
`AffiliateController.UpdateAffiliateStats(AffiliateId, 1, 0)` and, when no such cookie existed yet,
wrote an `AffiliateId` cookie that persisted for one year (L306-L322). It then wrote a visit record
through `SiteLogController.AddSiteLog` whenever `PortalSettings.SiteLogHistory` was non-zero, carrying
the referrer, URL, user agent, host address, host name, active tab and affiliate id, buffered by
`HostSettings("SiteLogBuffer")` and stored according to `HostSettings("SiteLogStorage")`, which
defaulted to `"D"` (L324-L351).

**Target behaviour.** Neither exists. No affiliate parameter is read, no affiliate cookie is written, no
visit record is stored, and no `SiteLog` table is mapped.

**Why the difference is deliberate.** Both belong to excluded subsystems — affiliate statistics to the
vendors and affiliates administration, site logging to the log provider model — and both were
per-request writes performed by the page lifecycle, which this migration replaces rather than reproduces.
Reproducing either would mean mapping a table and re-creating an administration surface that is out of
scope, and doing so on the request path of an API whose clients are a single-page application rather
than crawled pages. Request-level observability is met instead by structured logging with a correlation
identifier, which is retained.

### The health documents publish exactly four fixed members; probe detail stays in logs

**Legacy behaviour.** `Website/KeepAlive.aspx` was the nearest analogue: a page that returned
successfully if the application could serve a request. It ran no dependency check and reported nothing
about the database.

**Target behaviour.** `GET /health` is anonymous, uncached, and answers a JSON document whose first four
and only four members are the published contract — `status`, `timestamp`, `version`, `serviceName`.
`serviceName` and `version` are read from the running assembly rather than written as literals, so they
cannot drift from the assembly the container image starts. Measured:

```json
{"status":"Healthy","timestamp":"2026-08-04T12:34:44.08+00:00","version":"1.0.0.0",
 "serviceName":"DnnMigration.Api"}
```

**Where the diagnostic detail went.** Each completed health evaluation writes one structured log entry
containing the total duration and a bounded summary of every registered probe. That preserves the
operator-facing distinction the aggregate status word cannot draw — the database probe reports "not
configured" and "unavailable" differently, and those send an operator to the container's environment and
to the instance respectively — without expanding an anonymous wire contract. No probe exception, data
dictionary or connection string is logged.

**One database probe, not two.** An earlier revision registered two — this solution's own connection open
plus the health-check package's `AddSqlServer` — and justified the pair by claiming they answered different
questions. They did not: both read `ConnectionStrings:Default` and both opened a connection to the instance
it names, so the second could disagree with the first only by being flaky, and neither exercises the entity
model. Whether the model agrees with the schema it maps is settled by the integration suite. Because the
readiness view runs every readiness-tagged check on every request, and that view is re-polled for the life
of the container, the duplicate cost a second connection on every poll while distinguishing nothing. It is
withdrawn. The `AspNetCore.HealthChecks.SqlServer` package reference is **kept**: it is the only route by
which the health-check abstractions reach a class library that references no web framework, so removing it
would break the build rather than drop a dead dependency.

**Why the difference is deliberate.** `docs/project-guide.md`, the container probe and the acceptance
gate define the four-member body. Publishing probe names and timings in an anonymous endpoint widened
that contract and disclosed deployment detail to every caller. The endpoint must also stay anonymous and
unthrottled — the image's `HEALTHCHECK` probes it with `wget --spider` before any credential exists, and
the front-end service is held back by `condition: service_healthy` until it answers.

### A cancelled probe is not a database outage

**Legacy behaviour.** Not applicable; the legacy analogue ran no dependency check, so it had
nothing to cancel.

**Target behaviour.** The database probe reports `Unhealthy` when the connection string is
absent and when opening a connection fails, and it **propagates** an
`OperationCanceledException` raised while the supplied cancellation token is cancelled instead
of converting it into a verdict.

**Why the difference is deliberate.** That token is cancelled by the probe's own deadline
elapsing, by the caller disconnecting, or by the host shutting down — none of which says
anything whatever about whether the instance is reachable. Absorbing it would answer a rolling
restart with "the database is unavailable" and put a dependency outage that never happened into
the record of every deployment, which is precisely the diagnosis an on-call engineer would then
chase. The `when (cancellationToken.IsCancellationRequested)` guard is what makes this correct
rather than merely well intentioned: the SQL provider raises the *same* exception type for an
internal command timeout while the token is still live, and that is a genuine connectivity
failure which must still be reported `Unhealthy`. Testing the token distinguishes the two;
testing the type alone cannot. Rethrowing is safe for the endpoint, because the health
infrastructure treats a cancelled check as cancellation of the whole report rather than as a
fault — and by definition nobody is still waiting for the answer. Verified across all four
paths: reachable reports healthy, an absent connection string and an unreachable instance both
report unhealthy with their fixed descriptions, and a pre-cancelled token propagates.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/HealthChecks/DatabaseHealthCheck.cs`.

### HTTPS redirection is enforced in-process, exempting only the health endpoint and loopback addresses

**Legacy behaviour.** Transport security was configured at the web server.

**Target behaviour.** The host performs in-process HTTPS redirection wherever
`Https:RedirectEnabled` is set — which the production overlay does — for every request
except the anonymous `/health` endpoint and any request addressed to a loopback host.

**Why the difference is deliberate.** TLS terminates at the browser-facing edge in the
delivered topology, so the API receives plain HTTP on its container port by design, and
that is exactly why the API cannot simply trust its own connection: the scheme the
browser used arrives as a forwarded header. Enforcing here as well as at the edge is
what closes the plain-HTTP path to a directly reachable API port, so a caller that
bypasses the proxy cannot send a credential or a bearer token in clear text. The two
exemptions are what make enforcement safe rather than destructive. Redirecting the
health endpoint would answer the container's own probe — which calls
`http://127.0.0.1:8080/health` — with a redirect to a port nothing listens on, making
the container permanently unhealthy and, through the compose dependency condition,
preventing the front end from ever starting; no compiler, analyser or unit test reports
that. Redirecting a loopback-addressed request would protect nothing, because such a
request never leaves the machine that issued it, and it is the same carve-out the
framework's own strict transport security makes by default. The redirect target port is
configured rather than discovered, because the stage otherwise logs once and forwards
the request unchanged — a transport control that silently does nothing.

### The forwarded-for header IS honoured, from named hops only, and the forwarded host is not

`GET /health/live` answers the same four-member document and is the one view that executes no
registered check at all, so it reports the process itself and nothing else. `GET /health/ready`
answers the same shape from the readiness-tagged probes. All three are anonymous, uncached and
exempt from transport redirection.


**Legacy behaviour.** Not applicable; there was no rate limiting to partition, and IIS
terminated TLS in the same process that ran the pages, so the caller's address and the
request's scheme were already properties of the connection.

**Target behaviour.** `UseForwardedHeaders()` processes the forwarded address and the
forwarded scheme, with a hop limit of one, from hops the deployment has explicitly named
in `Proxy:KnownProxies` or `Proxy:KnownNetworks` — and from nowhere else. The forwarded
**host** is excluded. The authentication rate limiter therefore partitions by the
caller's real address as reported by the trusted proxy, rather than collapsing every
caller behind that proxy into one shared budget.

**Why the difference is deliberate, and why the trust boundary is where it is.** A
forwarded-for header is caller-controlled, so honouring one from a hop the deployment
does not operate would let an attacker change a header per request and escape the limit
entirely. The framework's default is to trust the loopback address alone, which inside a
container means nothing is trusted, so a deployment must name its own proxies — the
narrower the entry, the less a directly reachable caller can claim. Refusing to honour
them at all was the alternative and is worse: every caller behind the proxy then shares
one credential budget, so a single caller can exhaust the sign-in allowance for
everybody. Measured on the running host: 30 sign-in attempts claiming one forwarded
address were admitted and the 31st refused, an attempt claiming a different forwarded
address was admitted with a fresh budget, and an untrusted caller's forwarded scheme was
ignored rather than believed. The forwarded host stays excluded because tenant
resolution reads the request host, so accepting a forwarded one would let a caller
behind a trusted proxy choose which tenant serves it — a cross-tenant escalation
reachable from a header. `docker/nginx.conf` sets that host itself from `$http_host`.

**Where the trust is named, and why it is one address rather than a subnet.**
`UseForwardedHeaders` runs immediately inside the exception handler and before correlation,
request logging and rate limiting, so every later stage sees one settled client address. The
shipped Compose topology gives nginx the fixed address `172.28.0.10` and names exactly that
address through `Proxy__KnownProxies__0`. The surrounding subnet is deliberately not trusted:
the API service publishes its own port, so a caller reaching that port directly is
source-translated to the network gateway, which is a same-subnet address. Trusting the subnet
would therefore trust a directly reachable caller as if it were the proxy.


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

### Module-admin content crosses the boundary as verbatim inner XML, not portal-template escaped text

**Legacy authority.** The module administration screens, not the portal-template helper, define
this contract. `Website/admin/Modules/Export.ascx.vb:L157-L165` concatenated an XML declaration,
a `<content type="..." version="...">` start tag, the module controller's payload **verbatim**,
and the closing tag. `Website/admin/Modules/Import.ascx.vb:L196-L200` checked the `type`
attribute and passed `xmlDoc.DocumentElement.InnerXml` to the module controller with no HTML
decoding.

**What an earlier target revision got wrong.** It copied the portal-template path from
`Library/Components/Modules/ModuleController.vb:L226-L254,L410-L441`, where the writer
`HtmlEncode`d content and the reader later `HtmlDecode`d it after a fixed-offset CDATA strip.
That is a different document format. Applying the pair to the module-admin route corrupted
entity references, removed CDATA delimiters and made two wrong transformations appear to cancel
when the target exported and re-imported its own document.

**Target behaviour.** Export now composes the module-admin document exactly as the legacy screen
did, placing the payload between the tags without escaping it. Import preserves whitespace,
checks the root and the sanitised `type` attribute against the module package and friendly names,
and reconstructs the root's inner XML from its child nodes. Markup remains markup, escaped entity
references remain escaped, and a CDATA section reaches the business controller **with its
delimiters intact**, matching `InnerXml`.

**One explicit strengthening.** The composed export is parsed before it is returned. If a module
returns content that cannot form a well-formed XML document — including a C0 control character
that XML cannot represent — the operation reports `module.export_failed` instead of returning a
file the matching importer cannot read. The legacy screen could write such a file and discover
the defect only on import; the target refuses it at the boundary that produced it, without
publishing the module content or the parser's quoting message.

**Preserved and deliberate details.** Whitespace-only content is retained. XML line-ending
normalisation still changes a carriage return to a line feed, as the legacy XML reader did.
Imports are attributed to the actual caller rather than unconditionally to the portal
administrator, and an unidentified caller uses the legacy `-1` sentinel rather than risking a
real low-seeded account identifier. The excluded deferred-import queue is not recreated; an
undetermined or non-portable module is refused explicitly.

**Operational consequence.** Correct legacy module-admin export files remain importable. Files
produced by the withdrawn escaped revision may need to be regenerated because their payload was
not the legacy module-admin representation. A document whose `type` names another module is
refused before its content reaches the controller.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/ModuleService.cs` and
`backend/src/DnnMigration.Application/Abstractions/IModuleBusinessControllerFactory.cs`.
Guarded by `ExportModule_WrapsTheExportedContentInTheLegacyDocument`,
`ExportThenImportModule_ReturnsThePayloadUnchanged`,
`ExportModule_WhenTheModuleReturnsContentThatIsNotXml_IsRefused`,
`ImportModule_HandsOnACdataSectionWithItsDelimitersIntact`,
`ImportModule_ChecksTheDocumentTypeAgainstTheModulesOwnNames` and the surrounding export/import
service tests.

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

### The module page picker SELECTS the placement being edited; it no longer moves one, and the container selector is absent

**Legacy behaviour.** The module settings screen offered a page-move affordance and a
container selector. `ModuleSettings.ascx.vb:L398-L408` compared the selected page with the
current one and called `ModuleController.MoveModule`, which copied the placement and its
placement-scoped settings to the destination page and then deleted the source.

**Target behaviour.** The page picker is present and still sends `tabId` on the module update
request, but the value SELECTS which placement the update addresses. The service resolves the
placement on the named page and refuses with `module.placement_not_found` — answered as 404 —
when the module does not occupy it. A module placed on several pages is therefore edited one
placement at a time, by naming its page. No placement is copied, and none is deleted. The
container selector remains absent.

**Why the shape differs.** The update contract carries ONE page identifier and a move needs
two: the placement being edited and the page it should end up on. A single member cannot be
both without the service guessing which the caller meant, and guessing is what produced the
defect this shape exists to remove — an earlier revision resolved the placement with the
lowest key and ignored the submitted page entirely, so a caller editing the instance on page
four silently rewrote page one. Selecting on the submitted page fixes that; deriving a move
from a delta cannot be layered on top of it, because after the selection the two values are
equal by construction. Reinstating the affordance means adding a destination member, which is
a contract change rather than a frontend one. The container selector addresses a skin object,
and skinning is out of scope by the system boundaries.

**Operational consequence.** An operator who picks a page the module does not occupy receives
a 404 naming the reason rather than an apparently successful save that amended a different
placement. Moving an instance between pages is not available through this API; the legacy
wording on the picker is preserved for recognisability, and the reduction is recorded here.
The stored container value is preserved untouched on every update rather than being cleared.

## Deployment

### The API Alpine image installs ICU and can open SQL Server connections

**What was found.** Running the composed topology end to end, the API container answers
`503` on `/health/ready` for its entire life when the stock Alpine runtime is used unchanged,
and every request that touches the store fails. The container's own probe reads the liveness
view, so docker reports it healthy and the front-end service starts against an API that cannot
serve a single store-backed request — a silent failure rather than a blocked start-up.
The cause is not the connection string, the network or the database:
`Microsoft.Data.SqlClient` throws
`System.NotSupportedException: Globalization Invariant Mode is not supported` inside
`SqlConnection.TryOpen`, **before any socket is opened**.

**Why it happens.** The mandated runtime base image
`mcr.microsoft.com/dotnet/aspnet:8.0-alpine` sets
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true`, because Alpine's musl-based userland ships no
ICU. The 5.x client library deliberately refuses to open a connection in that mode rather
than risk locale-dependent behaviour.

**The applied remedy.** The runtime stage of `docker/api.Dockerfile` installs:
`RUN apk add --no-cache icu-libs icu-data-full` and
`ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`. The pair is inseparable: disabling
invariant mode without installing ICU makes the runtime fail fast at startup, while
installing ICU and leaving invariant mode enabled leaves SqlClient refusing the connection.

**Why the preserved example is extended.** The example's Alpine base and the required
SqlClient version cannot operate together without ICU. Applying the two production
prerequisites is therefore necessary to satisfy the container validation gates; leaving the
image verbatim would deliver a topology that starts, reports healthy and then fails every
store-backed request.

**Verified outcome.** Both images build, the API reaches SQL Server, `/health/ready` reports
healthy, compose starts the frontend, the HTTP ingress redirects to TLS and the HTTPS SPA
loads successfully.

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
`POST /api/v1/roles`.

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

`RoleId` and `PortalId` are both absent. The role arrives in the route
`PUT /api/v1/roles/{roleId}`, while the portal is resolved from the addressed alias; a value that
does not exist cannot contradict either source, so no reconciliation check is needed. The legacy screen carried
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

## Portal lifecycle orchestration — `PortalService`

Every entry in this section concerns
`backend/src/DnnMigration.Application/Services/PortalService.cs`, which absorbs
`Library/Components/Portal/PortalController.vb` (1,632 lines, 21 public members) and
the alias surface of `Library/Components/Portal/PortalAliasController.vb` (9
members). Each difference below carries a matching `// MIGRATION:` annotation in
that file.

### The portal-creation audit entry is not emitted by the Application layer

**Legacy behaviour.** `CreatePortal` closed by writing an event-log record at
`PortalController.vb:L1137-L1160`. It set `BypassBuffering = True`, so the entry was
written through immediately rather than batched — a failed installation had to leave
a trace — typed the entry as `EventLogType.HOST_ALERT`, and attached **fourteen**
properties at L1142-L1155: `Install Portal:` carrying the portal name, then
`FirstName`, `LastName`, `Username`, `Email`, `Description`, `Keywords:` (the label
is spelt with a lower-case `w` although the argument it read was `KeyWords`),
`TemplatePath`, `TemplateFile`, `HomeDirectory`, `PortalAlias`, `ServerPath`,
`ChildPath` and `IsChildPortal`. The password argument was **not** among them, so the
legacy code already declined to record the credential.

**Target behaviour, as corrected under code review.** The record becomes a structured
log event, and it **is** emitted from this service — through `IAuditSink`, an
log event, and it **is** emitted from this service — through `IAuditTrail`, an
abstraction the application layer declares itself in terms of the base class library
only. The earlier reasoning, preserved below because it remains correct on its own
terms, established that `ILogger<T>` cannot be named here: `DnnMigration.Application`
declares exactly two packages, `FluentValidation` and its dependency-injection
extensions, and `Microsoft.Extensions.Logging.Abstractions` is neither among them nor
present in the reference pack a class library targets. Naming `ILogger<T>` in this
project fails to compile with `CS0234` and `CS0246`; that was **verified by compiling
it**, not assumed, exactly as the project file already records for an earlier attempt
to import the options package. What did not follow from any of that is silence. The
layer names its own contract and `Infrastructure/Services/LoggingAuditSink.cs` implements it
layer names its own contract and `Infrastructure/Services/AuditTrail.cs` implements it
over `ILogger`, which is the same ruling the manifest records for the options package —
the consumer changes, not the manifest.

**What the entry carries.** `PortalService` emits stable event `1002 PortalInstalled`
on a successful installation with **eleven** of the legacy fourteen properties, plus the
created tenant's identifier, which the legacy entry did not have: `PortalId`,
`PortalName`, `PortalAlias`, `HomeDirectory`, `TemplateFile`, `IsChildPortal`,
`PortalDescription`, `PortalKeywords`, `AdministratorUsername`,
`AdministratorFirstName`, `AdministratorLastName` and `AdministratorEmail`.
`TemplatePath`, `ServerPath` and `ChildPath` are **omitted rather than guessed**: they
are file-system paths belonging to a subsystem this migration excludes, and inventing
values for them would put fabricated data into an audit trail. The credential remains
absent, as it was in the legacy call — and now cannot be supplied, because the contract
does not admit it.

**The legacy `BypassBuffering = True` has no counterpart and needs none.** It forced an
unbatched write so a failed installation left a trace; the structured event is written
by the configured sink on the same synchronous path, and the entry is emitted only on
success, so there is no batching window to defeat. No password, hash or token is logged
anywhere.

### `RemoveCache("GetRoles")` has no counterpart

**Legacy behaviour.** `PortalController.vb:L1131` evicted a single cache entry keyed
by the bare literal `GetRoles`, because creating a portal had just inserted its three
stock roles and the role cache would otherwise have served a list that predated them.

**Target behaviour.** The eviction is **omitted**, and the omission is bounded rather
than open-ended. `ICacheService` exposes twelve members and none of them evicts
roles; the generic `Remove(key)` member cannot be used correctly from the Application
layer because the key is composed inside the Infrastructure layer and this layer
holds no constant for it, so passing the bare legacy string would be a guess that
fails silently if the two spellings ever diverge — worse than the omission, because
it would look as though the concern had been handled. The roles being created belong
to a **brand-new** portal, so no reader can hold a cached role list for a tenant that
did not exist a moment earlier, and the stale-read window the legacy eviction guarded
is empty at that point in the lifecycle. The host-wide and portal-scoped
invalidations that surround it are both reproduced.

### Four legacy defects are annotated and deliberately not fixed

**Legacy behaviour.** Four faults were measured in the source of this service and are
recorded rather than corrected, because a defect discovered during a migration is
annotated in place and not fixed unless it blocks delivery.

- `PortalController.vb:L1140` and `:L1141` are **byte-identical consecutive
  statements**, both assigning `LogTypeKey` the same `HOST_ALERT` value. The second
  is pure redundancy, but it is almost certainly a copy-and-paste survivor of a line
  meant to set a different property.
- `ParseTemplate` at `:L1372-L1376` wraps the template load in a `Try` followed by a
  `Catch` whose body is **empty**, so a missing or malformed template was swallowed
  and parsing continued against an empty document: a tenant was created with none of
  the pages, modules or settings the template described, and the caller was told the
  creation had succeeded. The same member seeds three role identifiers to `-1` at
  `:L1364-L1366`, a value that is also a legitimate role key in this schema.
- The audit block is wrapped in an **empty** `Catch` at `:L1158-L1160`, so a logging
  failure was discarded and the one record proving a portal had been installed could
  go missing while the caller was told it had succeeded.
- The private two-argument `CreatePortal` ends with an **empty** `Catch` at
  `:L371-L373` and returns `-1`. That signal is unusable here, because
  `Portals.PortalID` is declared `IDENTITY (-1, 1)`, so a successful creation of the
  first portal and a swallowed failure returned the **same value**.

**Target behaviour.** None of the three empty handlers is reproduced, because
reproducing one would violate the engineering baseline this migration is held to.
Failures surface as a `Result` failure or propagate; none is discarded. That is a
**documented divergence** from the legacy behaviour rather than a silent correction of
it, and the redundant assignment is recorded rather than quietly tidied away.

### The last-portal refusal keeps the legacy wording

**Legacy behaviour.** `DeletePortal` at `PortalController.vb:L162` read how many
portals existed, proceeded only when more than one did, and otherwise returned the
shared resource keyed `LastPortal.Text`, whose wording at
`Website/App_GlobalResources/SharedResources.resx` is "You Can Not Delete The Last
Portal In Your Database". The outcome was reported by **returning that string**, with
an empty string meaning success, so a caller had to compare text to learn whether the
delete had happened.

**Target behaviour.** The rule survives as the failure code `portal.last_remaining`,
and the message **leads with that legacy sentence verbatim** so existing operators
still recognise it, followed by a sentence explaining the rule to a caller that has
never seen the legacy screen. The tally is read and judged inside the service; no
member reports a portal count, so the decision cannot migrate into a controller.
Asserted by `DeletePortal_RefusesToRemoveTheLastRemainingTenant`.

### Portal deletion removes final-member accounts but never deletes a host account

**Legacy behaviour.** `PortalController.DeletePortalInfo` called
`UserController.DeleteUsers(portalId, notify:=False, deleteAdmin:=True)` before removing the
portal. For each returned row, `AspNetMembershipProvider.DeleteUser` removed the portal's role
assignments and then read `vw_Users` by username with a null portal filter. Because that view
returns one row per portal membership, the second `Read()` decided the branch: one row meant the
current portal was the account's final membership, so the global `Users` row and the
`aspnet_Membership` credential were deleted; a second row meant only the current
`UserPortals` row was deleted. The bulk listing did not exclude super users, so a host account
whose only membership was the deleted portal could be removed globally as an unintended side
effect.

**Target behaviour.** `PortalService.DeletePortalAsync` performs the same membership-count
decision inside its existing serialisable transaction. Multi-portal accounts lose only the
expiring membership. Ordinary final-membership accounts have their direct module and page grants,
credential, global account row and refresh-token families removed before the portal transaction
commits; an unavailable credential or token store refuses the whole database removal rather than
publishing a tenant with unreachable identity rows. The account enumeration deliberately does not
populate approval or lockout snapshots from the external membership store, because those facts are
irrelevant to removal and must not make the relational cleanup unreadable.

Host accounts are the deliberate divergence: portal deletion removes their membership row but
never their global account, credential or sessions, even when that row is their only tenancy.
Deleting the installation's host operator would lock administrators out of every remaining portal,
so the unsafe legacy side effect is not reproduced. A portal's ordinary administrator is **not**
protected by this rule: the legacy caller passed `deleteAdmin:=True`, the tenant is disappearing,
and an administrator with no other membership is removed like every other final member.

### The seven installation defaults invert their guard

**Legacy behaviour.** The private two-argument `CreatePortal` at
`PortalController.vb:L326-L377` read seven host settings, each guarded by
`If Convert.ToString(HostSettings(key)) <> ""` — a comparison against the **empty
string**, which worked because `Null.NullString` is the empty string rather than
`Nothing` and `Convert.ToString` of a database null also yields the empty string, so
one comparison covered a missing key, a null value and a blank value alike. The
defaults were: `DemoPeriod` yielding no expiry; `HostFee`, `HostSpace`, `PageQuota`
and `UserQuota` each yielding zero; `SiteLogHistory` yielding **`-1`**, a sentinel
meaning "keep for ever" rather than a count; and `HostCurrency` yielding `"USD"`.

**Target behaviour.** `IHostSettingsService.GetSettingsAsync` deliberately diverges
from the legacy null contract: a key that is not present is simply **absent from the
dictionary**. Testing for the empty string alone would therefore let a missing key
through as though it were configured, so each read tests presence **and**
non-blankness **and** parsability, which restores the legacy outcome across all three
cases. A present but unparsable value is treated as absent, matching the legacy
conversion. Every legacy default is preserved, including the distinction that matters
most: retention stays **absent** rather than collapsing to zero, because zero would
silently turn unlimited retention into none, while a configured `-1` round-trips as
`-1`. The currency is a string setting, so only blankness makes it absent — a value
that is not a number is still a valid currency code, exactly as the legacy guard
implied.

### Expiry dates are computed from a UTC clock, not server-local time

**Legacy behaviour.** The trial expiry added a day interval to `Now()` using the VB
runtime's date-arithmetic intrinsic, reached without an `Imports` statement because
the project imported the runtime namespace globally. `Now()` is **server-local**. The
result was then round-tripped through a formatted medium-date string and re-parsed, a
lossy artefact of a formatting helper in the excluded globals module, which truncated
the time component.

**Target behaviour.** The instant comes from the injected clock, which is **UTC
only**. Because the offset between the two can cross midnight, a computed expiry can
land on a different **calendar day** from the one the legacy code would have produced
for the same real instant — a trial can appear to end a day early or a day late
relative to a legacy installation. That is accepted deliberately: a local-zone
timestamp is not comparable across hosts and cannot be interpreted without knowing
the machine that wrote it, and the injected clock is also what makes time-dependent
behaviour testable. The value is kept strongly typed rather than formatted and
re-parsed, so no precision is lost. The same UTC-versus-local difference applies to
the administrator membership's creation timestamp.

### The file-system stages of creation and deletion are omitted

**Legacy behaviour.** Creation performed five file-system stages, each with its own
failure resource: deleting a pre-existing upload folder at
`PortalController.vb:L1030-L1034` (`DeleteUploadFolder.Error`, "Error deleting
previous upload folder"); configuring a child portal on disc at `:L1038-L1053`
(`ChildPortal.Error`, "Error configuring Child Portal"); creating the home directory
and copying the tenant's resource file at `:L1058-L1067`; parsing the portal and
administration templates at `:L1075` and `:L1082` (`PortalTemplate.Error`, "Error
parsing Portal Template", and `AdminTemplate.Error`, "Error parsing Admin
Template"); and copying the default page template and synchronising the folder tree
at `:L1087-L1102`. A sixth resource, `CreatePortal.Error`, "Error creating Portal",
reported an outright creation failure, and `CreateAdminUser.Error`, "Error creating
Administrator user account.", reported a failed administrator. Deletion performed
four more stages: removing the tenant's `.Portal-<id>.resx` overrides, deleting the
child-portal directory derived from each alias, deleting `Portals\<id>`, and deleting
the mapped home directory.

**Target behaviour.** All nine stages are **omitted**, because the file-system
subsystem, the skinning subsystem and the module installer are outside this
migration, and none of those five stage-failure codes is reachable. The consequences
are stated plainly rather than implied: a portal created here has its **database rows
complete but no directories on disc**, and the pages, modules and folder permissions
the template would have supplied are absent — except the three stock roles, which are
lifted out of the template path and created unconditionally, because a tenant without
them cannot be administered. A portal deleted here has **all of its rows removed**,
so no tenant remains addressable, but orphaned directories and resource files are
**left on disc** for an operator to reclaim. `DeletePortalInfo` at `:L1191` also began
with four skin resets, which have no counterpart because skinning is out of scope.

The load-bearing literal `"admin.template"` — the name of the template that
provisioned a tenant's administration pages, where a misspelling would have produced
a portal with no administration surface and no error — is preserved as the named
constant `PortalOptions.AdminTemplateFileName`, whose default is that exact string,
so the value survives in configuration even though nothing currently reads it.

The legacy delete reached its paths through the VB runtime's `InStr`, `Mid` and
`InStrRev` intrinsics. None is transliterated, and the difference matters if the path
is ever restored: those intrinsics are **one-based** and return `0` for "not found",
whereas `IndexOf`, `Substring` and `LastIndexOf` are **zero-based** and return `-1`.

### The two scheduler-driven members are not ported

**Legacy behaviour.** `DeleteExpiredPortals` at `PortalController.vb:L156` swept
expired tenants and `UpdatePortalExpiry` at `:L1495` advanced the expiry date. Both
were driven by the scheduling subsystem.

**Target behaviour.** Both are **omitted**, because scheduling is out of scope. The
`ExpiryDate` **column survives** and is still read and written, so no data is lost and
an operator can still see and set it — only the unattended sweep is gone. Restoring it
would mean a hosted background service rather than a ported scheduler client.

### The administrator's password is hashed, and creation status becomes failure codes

**Legacy behaviour.** `PortalController.vb:L1005` assigned the administrator's
password in **cleartext**, and the membership provider that received it was
registered with `passwordFormat="Encrypted"` and `enablePasswordRetrieval="true"`
(`Website/release.config:L236-L246`) — a **reversible** scheme whose 3DES decryption
key was itself committed to source control at `Website/release.config:L89-L93`.
Anyone holding the repository and the database could recover every password. The
outcome of creating the account was reported by an enum returned by value at `:L1013`
and tested at `:L1015`.

**Target behaviour.** The password is hashed one-way through `IPasswordHasher`, so the
stored value cannot be reversed even by this application. Two consequences are
accepted: password **retrieval** is not carried forward to any endpoint or screen,
because a one-way hash cannot support it, and a forgotten password is answered by
reset rather than by recovery. The cleartext value is never logged, returned or
stored. The creation-status enum does not cross the service boundary; its eighteen
members become distinct, stable failure codes, which also removes a trap worth
recording — that enum's `Success` member is **13, not 0**, and member `0` is a
failure, so the usual "zero means success" reflex would have reported every failed
creation as a success. No numeric status comparison is carried forward at all.

### Host names are matched exactly, closing a cross-tenant hazard

**Legacy behaviour.** Tenant resolution was created as
`where PortalAlias like '%' + @PortalAlias + '%'` at
`Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L4569-L4600`
and then took `min(PortalID)` among the matches, so a request for `example.com` also
matched a stored `test.example.com.au`.

**Target behaviour.** Matching is **exact**, on the write side in this service and at
request time in `Api/Middleware/PortalAliasResolutionMiddleware`, so the duplicate
check and the later lookup cannot disagree. Two legacy consequences are closed: an
alias could be accepted as free while colliding with an existing tenant under the
legacy predicate, and a live request could be resolved to the **wrong tenant** purely
because one host name was a substring of another — a cross-tenant data-exposure
hazard in a multi-tenant product. An installation that relied on substring
resolution will resolve differently, which is why this is recorded as a deliberate
behavioural difference rather than an optimisation.

### The two lazy tallies become explicit awaited reads

**Legacy behaviour.** `PortalInfo.Users` at `:L309` and `PortalInfo.Pages` at `:L320`
were **lazy property getters**. Each issued a synchronous database read the first time
it was touched, memoised the answer in a backing field, and used `-1` in that field to
mean "not loaded yet".

**Target behaviour.** The domain entity carries neither property, and both tallies are
awaited explicitly where the projection is assembled. Three faults are removed rather
than carried: reading a property performed hidden I/O, so a caller could not tell a
field access from a query and a template that touched it in a loop issued one query
per iteration; the I/O was synchronous, which the async-throughout rule forbids in the
request path; and the not-loaded marker was `-1`, which in this schema is also a
legitimate identifier, so the sentinel was ambiguous.

### The unpaged sentinel is replaced by a named factory

**Legacy behaviour.** `GetPortalsByName` at `PortalController.vb:L262` declared
"return everything" by passing a page index of `-1`, then rewrote its own arguments to
page 0 with a page size of `Integer.MaxValue`, and reported the grand total through a
by-reference argument.

**Target behaviour.** The unpaged case has its own named factory on the paged
envelope, and a **negative page index is refused outright** with
`portal.paging_invalid` rather than reinterpreted. The legacy sentinel was
indistinguishable from a caller's arithmetic error, so a page index that had
underflowed to `-1` silently returned the whole table instead of failing, and it
collided with this schema's use of `-1` as a real identifier. The records and the
total travel together on one immutable value, so no by-reference argument appears.

### Role fee clamping replaces an intrinsic that evaluates both branches

**Legacy behaviour.** The private `CreateRole` helper clamped a negative service fee
and a negative trial fee to zero using the VB runtime's inline-conditional intrinsic,
wrapped in a narrowing conversion, at `PortalController.vb:L395` and `:L398`. It also
looked the role up **by name** first and, when one already existed, returned the
existing identifier **unchanged**, and it set `RoleGroupID` to the absent-integer
sentinel `-1` as its "belongs to no group" convention. Its three call sites passed the
literals `Administrators`, `Registered Users` and `Subscribers` with the billing code
`"M"` and the trial code `"N"`.

**Target behaviour.** The clamp becomes a maximum against zero. That substitution is
**not valid in general** and the reasoning is recorded because of it: the intrinsic is
an ordinary **function**, so both of its value arguments are evaluated before it is
called, whereas the C# conditional operator **short-circuits** and evaluates only the
branch it selects. The rewrite is faithful here only because each branch is a literal
zero or a parameter already in hand, so nothing is observable in the difference.
Where the same intrinsic appears with arguments that call a method, index a collection
or read a side-effecting property, the rewrite would silently change behaviour.
Create-or-reuse is preserved as a genuine rule, and on the creation path it holds by
construction because the portal is created in the same transaction, so the tenant
provably carries no role of any name and a lookup could only miss; a caller adding a
role to an existing tenant goes through the role service, which performs the name
check. The billing and trial codes are **load-bearing data** stored in
`Roles.BillingFrequency`, a `char(1)` column, so they are carried across as the enum
members whose stored values are those exact characters and are never renamed — and
that enum admits **six** codes, not the four the calendar-interval letters alone
suggest. No `-1` group sentinel is stored; the grouping navigation is simply left
unset, so absence is a null column rather than a number that is also a legitimate
group identifier.

### The whole tenant graph commits once

**Legacy behaviour.** Creation wrote across the `Portals`, `PortalAlias`, `Roles`,
`Tabs` and `Modules` tables as a sequence of independent statements, each committed on
its own, so a failure part-way through left a half-built tenant behind and the
clean-up branch at `:L1164-L1167` had to delete it explicitly.

**Target behaviour.** Every insert is **staged** and the whole graph is committed
inside **one database transaction**, so a partially built tenant cannot be left
behind and no store-assigned identifier is needed beforehand — every foreign key
inside the graph is resolved by the object graph itself. Three portal columns and the
credential record still cannot be written in the *first* save, because each needs an
identifier the database assigns during it, and the legacy path had the same shape,
stamping those identifiers only after the rows existed. The difference is that both
saves now happen **inside the same transaction**, so neither is durable on its own.

**Correction — the compensation routine described here previously is gone.** An
earlier revision of this note recorded that a failure in the second step was
"compensated by removing everything the first commit created". That design was
replaced, because the compensation could only run *in process*: it could not survive
termination of the process performing it, and it deliberately excluded cancellation,
so a caller who disconnected mid-creation left the half-built tenant it was written to
remove. Compensation is therefore deleted rather than retained as a fallback — there
is no second durable commit left to compensate for, and a failure or a cancellation
anywhere in the sequence is reversed by the store when the scope is disposed without
committing. One consequence is worth stating plainly: **a cancellation now rolls
back**, where previously it was the one case compensation ignored.

Retry-on-failure is retained for the connection but is **suspended inside a
caller-opened transaction**, because the SQL Server retrying execution strategy
refuses a user-initiated transaction outright: retrying a partially applied
multi-statement sequence is not safe, and the strategy is right to refuse. The
suspension is expressed by deferring the decision to execution time rather than by
disabling retries globally, so every read and write *outside* an explicit transaction
keeps its resilience.

### Portal configuration is columns, not a settings table

**Legacy behaviour.** The name `PortalSettings` suggests a key-value store, and there
is none. No such member appears among the abstract data provider's methods, no such
procedure among the ones the provider invoked, and no such table in any of the schema
scripts — which define `ModuleSettings`, `HostSettings`, `TabModuleSettings` and
`ScheduleItemSettings`, but nothing portal-scoped. The legacy `PortalSettings` class
was not persisted at all: `PortalController.vb:L1209-L1210` returns it from the
ambient per-request store, so it was a request-lifetime composite assembled from the
portal row and discarded at the end of the request.

**Target behaviour.** Portal configuration lives as **columns** on the portal, the
settings projection reads those columns, and the request-lifetime half of the legacy
class is served by the scoped portal context. Nothing reads or writes a portal
settings table. That projection is also deliberately **not cached**, unlike the
listing and detail reads: it backs an editing screen whose purpose is to show an
administrator what is stored immediately after they changed it, and serving that from
a sixty-minute cache would show a stale form and invite them to save the old values
back over their own edit.
### Correction: the seven convention-asserted foreign-key indexes are now suppressed at the context

**What changed.** The entry above, on
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/`, recorded that Entity
Framework Core's `ForeignKeyIndexConvention` asserts seven indexes the DotNetNuke 4.9 schema
does not have, established that no per-configuration suppression exists, and named the single
correct suppression point as a `ConfigureConventions` override on a context that did not exist
at the time. **That context now exists and the override is implemented.**
`DnnDbContext.ConfigureConventions` removes the convention once for the whole model.

**Consequence.** The model now declares exactly the thirty-three non-primary-key indexes the
terminal schema declares, each explicitly through `HasIndex` and named with
`HasDatabaseName`. The eight convention-derived extras are gone. Nothing real is suppressed:
the convention only ever adds an index where no declared index already leads with the
foreign-key column, so every index the schema actually has was declared explicitly in the
first place and is untouched by the removal.

**Why this is a correction rather than a new difference.** The divergence itself was already
documented and already had no runtime consequence, because Entity Framework Core consults
index metadata only when generating migrations or scaffolding. What the divergence did affect
was model comparison: a future `dotnet ef` diff started from a topology the database does not
have. Removing the convention removes that.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Persistence/DnnDbContext.cs`.

### Correction: primary-key clustering and unfiltered unique constraints are now asserted

**Legacy behaviour.** Seven of the twenty-one tables in scope declare their primary key
`PRIMARY KEY NONCLUSTERED`, against SQL Server's clustered default: `Modules`
(`01.00.00.SqlDataProvider` line 515), `ModuleDefinitions` (line 464), `Tabs` (line 498),
`Portals` (`01.00.05.SqlDataProvider` line 1457), `Roles` (line 2784), `RoleGroups`
(`04.00.04.SqlDataProvider` line 58) and `UserProfile` (line 1423). The remaining fourteen are
clustered, `Users` among them — its terminal declaration is
`PRIMARY KEY CLUSTERED` (`03.00.13.SqlDataProvider` line 27), so it is deliberately not in
this set. Separately, five unique constraints are declared over columns that permit null and
carry **no** filter: `IX_ModuleControls` (`02.00.00.SqlDataProvider` line 5017),
`IX_ModulePermission` (`04.05.02.SqlDataProvider` line 135), `IX_TabPermission` (line 215),
`IX_PortalAlias` (`03.00.07.SqlDataProvider`) and `IX_RoleName`
(`03.00.09.SqlDataProvider` line 323).

**Target behaviour.** The seven keys declare `.IsClustered(false)`. The five unique indexes
declare `.HasFilter(null)`. The fourteen clustered keys are left alone, which is how the
model states that they are clustered.

**Why the differences matter.** Neither is cosmetic, and neither has a runtime effect on
query translation — both are model metadata. What they affect is the model's account of the
database it maps. Left unstated, the model claimed seven clustered keys the database does not
have. Left unstated, the unique filters were worse than merely wrong: the SQL Server provider
adds `WHERE [column] IS NOT NULL` to a unique index over a nullable column, and on the two
permission grant tables every row has a null in one of the constrained columns by
construction — a grant names either a role or an account, never both — so the generated
filter would have excluded essentially every row from the uniqueness the database actually
enforces over all of them.

**Annotated in code at.** `ModuleConfiguration.cs`, `ModuleDefinitionConfiguration.cs`,
`PortalConfiguration.cs`, `RoleConfiguration.cs`, `RoleGroupConfiguration.cs`,
`TabConfiguration.cs`, `UserProfileValueConfiguration.cs`, `ModuleControlConfiguration.cs`,
`ModulePermissionConfiguration.cs`, `TabPermissionConfiguration.cs`,
`PortalAliasConfiguration.cs`.

### Correction: the permission catalogue's module-definition reference is a scalar, not a relationship

**Legacy behaviour.** `dbo.Permission.ModuleDefID` is `int NOT NULL`, and the value `-1` is
real, seeded data: the product-wide catalogue entries — those carrying the
`SYSTEM_MODULE_DEFINITION` and `SYSTEM_TAB` scope codes — use it precisely because they belong
to no single module definition. There is no `ModuleDefinitions` row with that key.

**Target behaviour.** The conceptual relationship between the catalogue entry and the module
definition is **removed** at both ends: the reference navigation, the inverse collection and
the `HasOne`/`WithMany` configuration. The scalar `ModuleDefinitionId` remains exactly as it
was, `int` and non-nullable, and `-1` continues to travel through it untouched. Code that
needs the definition behind a real identifier resolves it explicitly.

**Why the alternative is not merely undesirable but impossible.** A relationship over a
non-nullable foreign key is **required** by definition in Entity Framework Core; the model
cannot be built with an optional relationship over one, so "keep the relationship and make the
navigation nullable" is not an available option. A required relationship asserts that every
`ModuleDefID` names an existing row, which the `-1` entries falsify. Removing the concept is
the only way to keep the data truthful, and it costs nothing: no code read either navigation.
Both ends had to go, because leaving the inverse collection in place would let convention
rediscover the relationship.

**Annotated in code at.** `backend/src/DnnMigration.Domain/Entities/Permission.cs`,
`backend/src/DnnMigration.Domain/Entities/ModuleDefinition.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/PermissionConfiguration.cs`.

### Correction: role billing and trial frequency use the shared converter again

**What changed.** `dbo.Roles.BillingFrequency` and `dbo.Roles.TrialFrequency` are
`char(1) NULL` holding the codes `D`, `W`, `M` and `Y`, and the mapping between those codes and
the domain enumeration belongs to one place —
`Persistence/ValueConverters/BillingFrequencyToStringConverter`, which normalises through
`Enum.IsDefined` so an unrecognised stored character cannot become a member that does not
exist. A revision had replaced both bindings with inline conversion lambdas that duplicated the
mapping and dropped the normalisation, leaving the shared converter unreferenced. Both
properties bind to `BillingFrequencyToStringConverter.Instance` again and the duplicated
lambdas are deleted.

**Why it matters.** Two copies of a code mapping is one copy too many, and the copy that was
in use was the weaker of the two. The codes are load-bearing stored data rather than an
implementation detail, so they may not be renamed or reinterpreted.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/RoleConfiguration.cs`.

### Correction: the baseline migration snapshot is regenerated from the corrected model

**What changed.** The baseline migration's designer and the model snapshot are regenerated
from the model as corrected by the four entries above, so
`dotnet ef migrations has-pending-model-changes` reports no pending changes. The drift being
closed was real: the snapshot carried unnamed indexes where the model declares `IX_UserProfile`,
`IX_UserRoles`, `IX_UserRoles_1` and the four named page-permission indexes.

**What deliberately did not change.** `InitialCreate.Up` and `InitialCreate.Down` remain
**empty**, so no schema statement of any kind can reach a database from this work. The
migration exists to seed migration history as a baseline, never to create or alter a table —
the schema depends on externally installed `aspnet_*` membership objects that the eighty-eight
scripts only ever `ALTER`, so a generated create-migration could not reproduce the terminal
schema even in principle.

**What the identifier is.** The baseline is stamped `20260730120000_InitialCreate`, and that
identifier is byte-identical across the migration filename, the designer filename and the
`[Migration]` attribute, so exactly one designer carrying exactly one attribute exists. The
earlier `20260802072256` stamp is gone, and it had to go: two files declaring the same
`partial class InitialCreate` with `Up` and `Down` in one namespace is a duplicate-member
compile error, so the superseded migration was removed and the companion designer renamed with
its attribute retargeted — a single-line change that preserves `BuildTargetModel` verbatim and,
**as a re-stamp specifically**, leaves `DnnDbContextModelSnapshot` untouched; the snapshot is
regenerated by the separate correction described above, not by this one. Re-stamping is safe
precisely because the bodies
are empty and nothing applies migrations on its own: no `Migrate`, `MigrateAsync` or
`EnsureCreated` call exists anywhere in the source, so a database that already carries the
earlier history row is unaffected until an operator runs `dotnet ef database update`
deliberately, and when they do the only statement issued is the guarded history insert.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Persistence/Migrations/20260730120000_InitialCreate.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Migrations/20260730120000_InitialCreate.Designer.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Migrations/DnnDbContextModelSnapshot.cs`.
Those three files are the whole of the migration tree; the superseded `20260802072256` designer
named by an earlier revision of this entry no longer exists and must not be looked for.

### Sign-in proves the credential before it approves anything

**Legacy behaviour.** The legacy membership provider evaluated a sign-in in the order
lock, then approval, then password (`AspNetMembershipProvider.vb` lines 1445-1480). The
approval branch **wrote** — it set the account approved when the submitted verification code
matched — and the code it compared against was derived from the portal and account
identifiers, so it was guessable by anyone who could enumerate two integers. The credential
had not been checked at the point that write happened.

**Target behaviour.** The order is lock, then **credential**, then approval. The approval
write is unreachable unless the credential comparison has already succeeded. Lockout
bookkeeping is unchanged and still short-circuits ahead of the credential comparison, so a
locked account never becomes a credential oracle.

**A second, visible consequence, adopted deliberately.** Because the credential is now proved
first, the reply to an unapproved account can safely say which step remains, and it does:
verification required, verification code invalid, or account not approved. These are reported
as `400 Bad Request` rather than `401 Unauthorized`, because the caller **has** authenticated
and the submission is incomplete rather than unauthorised. The legacy stack discarded this
distinction.

**Why the difference is deliberate.** Reproducing the legacy order would reproduce a
credential-free account approval, which is a privilege escalation reachable by an
unauthenticated caller. The migration discipline requires behavioural equivalence, and equally
requires that a discovered defect not be carried into new code silently; this one is carried
into neither.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/AuthService.cs`,
`backend/src/DnnMigration.Application/Abstractions/IAuthService.cs`.

### A lockout expires on its own, as it did in the legacy stack

**Legacy behaviour.** `AspNetMembershipProvider.AutoUnlockUser` (lines 64-86) read the host
setting `AutoAccountUnlockDuration`, defaulted it to ten minutes when the setting was absent,
treated a configured zero as "never unlock automatically", and unlocked the account when its
recorded lockout instant was further in the past than that window.

**Target behaviour.** Reproduced. The same host setting is read, the same ten-minute default
applies when it is absent, a configured zero still disables automatic unlocking, and the
window is compared against the recorded lockout instant using the injected clock rather than
the server's local time.

**One narrowing, stated.** A **negative** configured duration is treated as disabling
automatic unlocking, exactly as zero is. Read literally, a negative window would describe an
interval that has always already elapsed, which would unlock every locked account on its next
sign-in attempt and make the lockout mechanism inert. Treating it as a disabling value is the
safe reading of a configuration mistake.

**An account with no recorded lockout instant is not unlocked**, because there is no window to
measure and inventing one would unlock an account on the strength of missing data.

**No lockout detail is disclosed.** Whether an unlock happened is never reported to the
caller; the reply is the reply the resulting sign-in earns.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/AuthService.cs`.

### A token-store outage is reported as an outage, not as a server fault

**What changed.** The refresh-token store reports its own unavailability with a distinct
reason code. A revision converted that code into a thrown `InvalidOperationException` on the
refresh, sign-out and issuance paths, which the API edge could only render as a generic server
fault — so an availability problem reached the caller looking like a defect, and the typed code
the contract promises to propagate unchanged was discarded on the way. The code now propagates
unchanged, and token issuance reports it as a typed failed outcome rather than throwing.

**Consequence.** The condition renders as `503 Service Unavailable` at the API edge, through
the existing reason-code mapping and with no special case anywhere. That is the honest status
for a dependency that is temporarily unreachable, and it is the status a client can retry
against.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/AuthService.cs`.

### The sign-in reply no longer advertises a profile-completion flag

**Legacy behaviour.** The legacy stack could require a valid profile at sign-in, gated by a
**module setting** on the login control (`UserController.vb` lines 1189-1193 with
`ProfileController.vb` lines 305-319). The gate was a property of a Web Forms control instance,
not of any schema-backed configuration.

**Target behaviour, as finally delivered.** `IUserService.RequiresProfileCompletionAsync`
reads the tenant's `Security_RequireValidProfileAtLogin` membership setting and walks the
tenant's required profile-property definitions against the caller's stored values, exactly as
`ProfileController.vb` lines 305-319 did. Sign-in and refresh both re-evaluate the result,
`JwtTokenService` emits it as the `must_update_profile` boolean claim and the response carries
`mustUpdateProfile`.

**Blocking enforcement.** The API's remediation authorization handler reads the authoritative
stored state on every protected request. While profile remediation is required it permits only
the account owner's profile read/update endpoints and authentication/remediation routes; every
unrelated protected operation is refused. Refresh does not preserve a stale decision: it
recomputes the state before issuing the next access token.

**Client contract.** The Angular authentication response and stored session both carry
`mustUpdateProfile`, so route guards and future remediation presentation consume the same state
the server enforces. The signal is no longer client-only and no contradictory removal decision
remains.

**Annotated in code at.** `backend/src/DnnMigration.Application/Dtos/Auth/LoginResponse.cs`,
`backend/src/DnnMigration.Application/Services/AuthService.cs`,
`frontend/src/app/core/models/auth.model.ts`.

### The page listing includes pages in the recycle bin

**Legacy behaviour.** The terminal page read returns soft-deleted pages and projects the
deletion flag as one of its columns. `GetTabs` was rewritten at `04.04.00.SqlDataProvider`
lines 440-448 to select every column of `vw_Tabs` under a portal predicate alone, and that
view's terminal definition (`04.05.04.SqlDataProvider`) carries no deletion predicate either.
The flag is projected precisely so that the **reader** decides, and the legacy readers decided
differently: the page-management grid hid recycled pages while the recycle-bin screen listed
nothing but them.

**Target behaviour.** The single page listing this migration exposes returns the complete
read, and every row carries its own `isDeleted` flag. Filtering recycled pages out is a
client-side projection over a complete answer.

**Why not reproduce the page-management grid's filter.** Because both legacy filters were
call-site policy over one complete read rather than properties of the read, and this migration
has one listing rather than two screens. Hard-coding either policy would contradict the
completeness this endpoint's contract promises, would put the recycle-bin view of the data out
of reach entirely, and would do so **silently** — a caller cannot distinguish a portal with no
recycled pages from a portal whose recycled pages were withheld from it. No boolean filter
argument is added either: the surface is deliberately narrow, and a filter over a field every
row already carries earns nothing.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/TabService.cs`,
`backend/src/DnnMigration.Application/Abstractions/ITabService.cs`.

### Correction: the sortable allowlist is enforced per collection, and two collections offer no ordering

**What changed.** The entry "Paging, filtering and sorting are bounded, and caller-chosen
sorting is a new capability" above states that sorting is restricted to an explicitly
enumerated set **per resource**. Enforcement did not match that statement: the shared request
validator applied the **union** of every collection's set, and no listing consulted its own.
A field meaningful only for roles was therefore accepted for a portal listing and then answered
in that listing's default order — a page the caller could neither account for nor detect.

**Target behaviour.** Every listing now checks the caller's sort field against **its own** set
before it dispatches a read, and refuses a field it cannot honour. The union remains as the
outer bound, and it has to: FluentValidation resolves one validator per request **type**, and
one paging contract serves every collection endpoint, so the union is the narrowest vocabulary
that boundary can possibly apply. Neither half is redundant — without the boundary an entirely
unknown name reaches a store, and without the per-collection check a known-but-foreign name is
silently ignored. Enforcing in the service also binds callers that never pass through request
validation at all.

**Each allowlist is now exactly what its ordering expression honours**, in both directions.
The portal set is the five arms the portal read honours; the role and role-membership sets are
the eleven and ten arms their orderings honour. An allowlist entry with no arm behind it is
worse than an omission, for the same reason the union alone was worse than nothing.

**Two collections deliberately offer no ordering at all, and refuse rather than ignore.**

- **The account listing.** Its page is assembled by the database under a fixed order, and
  three of the ten columns the legacy account grid bound — the creation instant, the last
  sign-in instant and the approval flag — are not columns of this database at all. They are
  read from the external `aspnet_*` membership store **after** the page has been selected, and
  `dbo.UserPortals` carries no last-sign-in column at any schema version. Offering the seven
  that are account columns while refusing the three that are not would give one endpoint a
  partial ordering surface whose membership a caller could not predict from the payload.
- **The module listing.** Its projected row is not its paged unit: the page window is taken over
  modules while each module then contributes one row per placement, so a page of ten modules
  routinely returns more than ten rows. Any ordering could only ever apply to the modules
  behind the rows, never to the rows the caller receives, and the payload gives the caller no
  way to see that distinction.

Reordering either page is a client-side projection over the answer, which is the mechanism the
shared data table and its feature store already provide.

**Operational consequence.** A caller that named a sort field a listing cannot honour now
receives a refusal where it previously received a differently ordered page. Because the legacy
grids offered no sorting at all, no legacy caller can be affected.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/SortableFields.cs`,
`backend/src/DnnMigration.Application/Validation/PagedRequestValidator.cs`,
`backend/src/DnnMigration.Application/Services/PortalService.cs`,
`backend/src/DnnMigration.Application/Services/RoleService.cs`,
`backend/src/DnnMigration.Application/Services/UserService.cs`,
`backend/src/DnnMigration.Application/Services/ModuleService.cs`,
`backend/src/DnnMigration.Infrastructure/Repositories/PortalRepository.cs`.

### Correction: the module listing could not return the page the entry above describes

**What changed.** The entry immediately above states that the module listing's projected row is
not its paged unit — "the page window is taken over modules while each module then contributes
one row per placement, so a page of ten modules routinely returns more than ten rows". The code
could not actually produce that page. It handed the **module** total and the **module** window
size to the paging envelope alongside **placement** rows, and the envelope rejects arguments in
disagreement: a grand total may not be smaller than the page it describes, and a page may not
carry more records than the size it declares. Both guards fired and the read raised
`ArgumentOutOfRangeException`, which the API edge publishes as a server error.

**When it fired.** Whenever a paged request was answered without a page filter — `tabId` absent,
which is the default listing of a tenant's modules — and any module in the window sat on more
than one page. The service's own remarks reconciled the two figures by observing that a module
has at most one placement on any one page. That is true, but only while a page is **named**; with
no page filter a module contributes every placement it has. A module placed on two pages is
ordinary, and a module marked to appear on all pages triggers it always.

**Target behaviour.** The unpaged answer is unchanged. For a paged answer both figures are raised
to the number of rows the page actually carries. Neither becomes less truthful by it: the module
total was already a **lower bound** on the placement total — the trade the service documents,
taken because the exact figure needs an unbounded read of every placement in the tenant — and the
row count is a better lower bound drawn from the same information. The declared window widens only
when the placement expansion overflowed it, so a page whose rows fit reports the size that was
asked for, unchanged.

**Why this is a correction and not a divergence.** The legacy listing returned an untyped
`ArrayList` and reported no total, page index or page size at all, so there was no envelope and
nothing to reconcile. The fault belongs entirely to code written for this migration, which is why
it is repaired rather than annotated and left in place — the rule that a discovered **legacy**
defect is recorded rather than quietly improved does not extend to a defect this migration
introduced.

**Operational consequence.** A default module listing with paging enabled now answers instead of
failing. No caller sees a narrower or differently ordered page: the rows returned are the same
rows the read always selected, and only the two count fields describing them changed, each in the
direction that can only ever grow. No repository read was added, so the read profile is unchanged.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/ModuleService.cs`.
Guarded by
`ListModules_WhenAModuleOutnumbersItsWindow_ReconcilesTheEnvelopeInsteadOfThrowing` and
`ListModules_WhenTheRowsFitTheWindow_ReportsTheRequestedGeometryUnchanged` in
`backend/tests/DnnMigration.UnitTests/Application/ModuleServiceTests.cs`, the first reproducing
the failure and the second holding the repair to widening only when it must.

### Correction: the module cache period carries no lower bound, and is no longer clamped

**Legacy behaviour.** The module settings screen validated the cache period for
**integrality and nothing else** — `valCacheTime` at `modulesettings.ascx` line 172 declares
`Operator="DataTypeCheck" Type="Integer"` with no companion comparison — and the code-behind
stored whatever parsed, `Int32.Parse(txtCacheTime.Text)` at lines 349-350 with no comparison of
any kind. A negative period was an accepted submission. The column is a plain `int NOT NULL`
with no check constraint anywhere in the schema chain, and minus one is meaningful in this
domain rather than nonsensical: line 138 tests a module definition's default cache period
against exactly that value.

**Target behaviour.** No lower bound is imposed, in **four** places where a revision had
imposed one: both module request validators refused a negative period, and both projections
onto the placement row silently clamped it to zero. All four are removed. Integrality is still
enforced, by the type — the member is an `int`, so a non-integer is refused by model binding
before any validator runs, which is the same check the legacy validator performed, relocated to
the type rather than dropped. A blank legacy field stored literally zero and an omitted JSON
property arrives as zero, so that submission behaves identically and zero still means "do not
cache".

**Why the clamp was the worse half.** Removing only the validator rules would have left a
negative period accepted and then **silently rewritten**, so the record read back would not be
the record submitted and the caller would have no way to detect the substitution. The clamp's
own justification — that the store would refuse to interpret a negative — is contradicted by
the schema. This is the same correction already recorded for the portal contracts, where five
comparable floors described in their own comments as "a deliberate, documented strengthening"
were likewise removed; the deliberate contrast is that `editroles.ascx` **does** declare
`Operator="GreaterThanEqual" ValueToCompare="0"` for its fee fields, so the role fee floors are
ported rather than invented and remain in place.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Validation/CreateModuleRequestValidator.cs`,
`backend/src/DnnMigration.Application/Validation/UpdateModuleRequestValidator.cs`,
`backend/src/DnnMigration.Application/Mapping/ModuleMappings.cs`.

### A module's own failure explanation is logged, not returned

**Legacy behaviour.** Three of the legacy module lifecycle call sites **swallowed** the
failure outright — a bare handler around the export site, one commented "ignore errors" around
the import site, and one wrapping the queued import — so a failed export wrote an incomplete
portal template and a failed import lost content while the caller was told nothing.

**Target behaviour.** The failure is reported, and its explanation is **logged rather than
returned**. Each lifecycle operation reports a stable reason code with a fixed message naming
the operation that failed; the exception itself, with its type, message and stack, is written to
the log at error severity inside the request's correlation scope. Not swallowing the fault and
not disclosing its text are two separate obligations, and both are met rather than one being
traded for the other.

**Why the explanation may not be returned.** A business controller is third-party code, and the
fault classification is deliberately wide — anything it raises is a module fault, including an
exception raised by dependency resolution on its behalf — so the message can hold a connection
string, a file system path, an internal type name, or personal data quoted out of the very
payload being parsed. A failed outcome from this boundary is published by the API edge as the
problem-details explanation, which is to say to the HTTP client. Nothing actionable is lost: the
correlation identifier already in the logging scope joins a log entry to the exact response the
caller saw.

**Nothing else travels on the message either** — not the supplied controller key, not the
payload, not a stack trace. The payload is never logged either; its size alone makes it
unsuitable, and it is the one argument crossing that boundary expected to hold tenant data.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Services/ModuleBusinessControllerFactory.cs`,
`backend/src/DnnMigration.Application/Abstractions/IModuleBusinessControllerFactory.cs`.

### Permission evaluation reads every grant once, and the catalogue in one request

**What changed.** Evaluating what a caller holds on a module or a page issued one grant read
per applicable catalogue entry, and resolving the catalogue behind a set of grants issued one
read per distinct permission. Both are now single reads. The number of round trips no longer
varies with how many permissions a module definition declares or how many distinct permissions
a tenant uses.

**How, and why it is not a trick.** The legacy grant procedures accept `-1` in their permission
argument as a wildcard meaning "every permission" — measured from the terminal bodies'
`(PermissionID = @PermissionID OR @PermissionID = -1)` guard — and that wildcard is part of the
procedure contract the repository interface inherits. Asking it once and completing the
narrowing against the catalogue already in memory is the contract's own way of expressing the
question. A set-wise catalogue reader is **net-new**: the legacy provider's catalogue reader
took one identifier, so there was nothing to port, and the batching belongs where the query is
composed rather than being simulated in a loop.

**Nothing about the decision changed.** The in-memory join is exactly as strict as the
per-permission reads it replaced: a grant is judged only when its own permission identifier is
one of the catalogue entries that passed the key filter and the scope test, so a row the
wildcard read now returns but the narrowed read could never have fetched is discarded rather
than admitted. Deny precedence, the pseudo-principal sentinels, the superuser short-circuit,
the current-role refresh and the fail-closed reading of a grant naming a catalogue entry that
does not exist are all untouched. The order of the intermediate matches changes and cannot
matter, because precedence is computed as a set operation and the resulting key names are
sorted before they are returned.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Security/PermissionEvaluator.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IPermissionRepository.cs`,
`backend/src/DnnMigration.Infrastructure/Repositories/PermissionRepository.cs`.

## Code-review remediation — differences introduced while closing sixteen findings

Everything in this section was introduced while resolving a code review, not while
writing the original migration. Each entry records a difference a consumer or an
operator can observe, so that no correction made under review is absorbed silently.
Three sections earlier in this document were also **corrected rather than extended**,
because the fixes made their claims untrue: the sign-in audit gap report, the
portal-creation audit note, and the tenant-resolution note.

### The wire contract: every payload-bearing response is now enveloped

**What changed.** A response that carries a payload now always carries it inside one of
two envelopes. A single record is `{ "data": ..., "meta": null }`; a page is
`{ "items": [...], "meta": { "pageIndex", "pageSize", "totalCount", "totalPages" } }`.
Both shapes were declared by the application layer from the outset and **neither had a
single consumer**: collection endpoints were serialising the domain paging type
directly, so a domain type *was* the public contract and a change to the domain model
would have been a breaking API change.

**The scalar `meta` member is written, not omitted.** The API's serializer deliberately
writes default values, and `ApiResponse<T>.Meta` carries no per-member null-omission
condition. The client contract therefore declares `meta: ApiMeta | null`, required and
nullable, rather than an optional non-null member. Problem-details members follow a
different rule: the framework type carries its own per-member null-omission attributes,
so an absent `instance` is not written and remains optional on the client.

**A page is not double-wrapped.** The paging projection is returned as itself, not placed
inside the single-record envelope. Two levels of wrapping would make a client unwrap twice
for a page and once for a record, which is the inconsistency the envelope exists to remove.

**A bodyless command still answers `204 No Content` with no envelope**, because there is
no payload to wrap and inventing one would mean a client parsing a body to learn nothing.
A consequence worth stating: the non-generic `ApiResponse` therefore has no consumer and
**cannot** have one. It is retained and documented as such rather than deleted, because it
is the arity the generic form is understood against.

**Migration impact for a client.** The paging numbers used to be siblings of the records
and are now one level down under `meta`. Binding the old flat shape against the new body
still yields the records while reading every paging number as zero — a pager showing one
page of everything instead of failing where a reader would notice — so this is a change a
client must make deliberately.

**An unpaged collection reports `pageSize` equal to `totalCount`**, which is the honest
reading of "one page containing everything" rather than a fabricated page size.

**On the front end, `PagedResult<T>` is now a client-side view rather than the wire
contract.** `PagedResponse<T>` is the wire shape and `flattenPagedResponse` lifts it into
the flat view that shared components already render.

### Sorting is validated against the collection being sorted, not against every collection

**What changed.** The sort allowlist was the union of every sortable field across every
collection, so a field belonging to one collection was accepted on another and then
silently ignored — the caller received a differently-ordered page with a `200` and no
indication that the ordering they asked for had not been applied. Each collection now
validates against its own set and answers `400` naming the fields it accepts.

**Observable by contrast.** `sortBy=hostFee` is accepted on the portal collection and
refused on the role collection; `sortBy=roleName` is accepted on roles and refused on
users. Both refusals name the accepted set.

**Every advertised field is now genuinely ordered by.** Two arms of the portal ordering
switch — `Description` and `Currency` — were unreachable and are removed, and `HostFee`
and `HostSpace` arms were added because both were advertised and neither was implemented.
Role, module and user ordering are honoured end to end, which for users meant carrying the
sort into the repository so that ordering is applied **before** the page is taken rather
than to the page after it.

**Two collections deliberately advertise less than their grid could.** Users cannot be
sorted by created date, last sign-in or approval state, and modules cannot be sorted by
order or display title, because no measured legacy grid offered those orderings.

### Statuses and body shapes are now declared only where they can occur

**What changed.** Thirteen actions took nothing but route values constrained to integers,
and each advertised a `400` carrying a validation document naming the offending parameter.
Twelve of those responses were unreachable: a route value that is not an integer fails the
route constraint, so the request matches no endpoint and the router answers **`404`**
before any action, filter or model binder runs. Removal of a profile definition also
advertised a `409` for a state conflict the service has no way to report. All of these
declarations are removed, along with the prose that described them.

**A validation document is now advertised exactly where the request carries content.**
The validation shape differs from the plain problem document by carrying a map of the
request members that were refused, so it belongs only where the request has members to
refuse. A route-only action may still refuse a well-formed request on **state** grounds —
an account that is not locked, an installation that must retain one portal — and such a
refusal is declared as the plain problem document it actually is.

**The published contract gained the statuses the pipeline really produces**: `401` and
`403` wherever authentication or a policy or the tenant gate can refuse, `429` on the
seven credential-bearing actions, and `500`/`503` on the four operations whose failure
codes deliberately classify as server faults.

**The universal `500` is deliberately not declared anywhere.** Any endpoint can meet an
unexpected exception, so declaring it on all of them carries no information; it is
declared only where a designed outcome maps there.

**A module's exported content is now published under `application/xml`.** The response
itself is unchanged — it was always served as XML — but the document advertised
`application/json`, because the description is built from the media types declared for the
whole action and then narrowed to those a registered output formatter can write. The
payload is written as a content result and never passes through a formatter, so the
formatter set was the wrong authority. This is corrected in the description only:
declaring the media type on the action would have forced every **failure** from that one
action to be negotiated as XML and answered `406` with an empty body.

### Refusing to delete the last portal is a conflict, not a malformed request

**What changed.** The refusal now answers `409 Conflict` where it previously answered
`400 Bad Request`. The legacy member reported it by *returning* the shared resource string
keyed `LastPortal.Text`, with an empty string meaning success, so it had no HTTP status of
its own to preserve — and the endpoint's own published description already declared the
refusal as a `409`, arguing the case in the same words this API uses for a removal blocked
by a still-referenced resource. The declared `409` was therefore unreachable and the `400`
the caller actually received was undeclared: one defect with two visible halves. The
legacy wording of the message is unchanged.

**Consistency, not preference.** A well-formed request that current state declines is a
conflict. Answering `400` told the caller to edit a request that had nothing wrong with it.

### A refusal never carries a third party's exception text, and no failure is silently a bad request

**Plugin text is not surfaced.** A module business controller is third-party code. Its
exception message used to travel into the problem document, which discloses internal
detail to a caller and hands an extension author a channel into this API's error surface.
The message is logged internally with correlation context and the caller receives fixed,
caller-safe text.

**An operation that had already passed validation and authorisation before failing is a
server fault.** Such codes previously fell to a `400` default, telling the caller to
correct a request that nothing they could change would fix. They now classify as `500`,
with the narrower classifications still winning where both could match.

**A `404` produced from a successful read that found nothing is a problem document**, not
a bare status with an empty body, and it carries a stable type so a client branches on the
same value whichever endpoint produced it.

**A refusal by the authorisation pipeline carries a body.** A token or policy refusal used
to answer `401` or `403` with nothing in it, so a client could not tell an authentication
failure from an authorisation failure without inferring it from the status alone. Both now
carry the shared vocabulary with the correlation identifier.

### Credential work is bounded wherever it happens, not only where the path says so

**What changed.** The credential window and the process-wide concurrency ceiling were
applied by matching whole segments of the request path against a word list, so operations
that hash a credential without saying so in their path — creating a user, creating a
portal, changing and resetting a password — were completely unbounded. Hashing is
deliberately expensive, so an unbounded hashing endpoint lets the caller decide how much of
this process's time is spent. Those actions now carry an explicit mark that the limiter's
classifier reads from endpoint metadata, which is a statement about what the action *does*
rather than about how its path is spelt. The path matcher is retained as a fall-back.

**Requiring a password change is deliberately not bounded**: it sets a flag and hashes
nothing.

### An unrecognised stored frequency character reads as "none" instead of failing the row

**What changed.** The role billing and trial frequency columns are a single legacy
character, and both readers cast whatever was stored straight to the enumeration. A value
outside the six defined codes produced an undefined enumeration value that later threw
while being written to the wire, turning a perfectly readable row into a `500`. Both
columns now go through the shared converter, which resolves an unrecognised character to
`None`.

**The comparison is case-sensitive, and that is deliberate.** A stored `'m'` reads as
`None`, not as `Month`. The legacy comparison was VB's binary comparison against a
case-insensitive database collation, and preserving the stricter reading is what keeps a
lower-case value from being silently promoted to a meaning nobody stored. The wire
converter takes the opposite position and upper-cases inbound caller text; that asymmetry
is intentional — caller input is being interpreted, stored data is being reported.

### A module's "append" instruction is resolved before the write, never persisted

**What changed.** A position of `-1` is a command meaning "put this at the end", and it was
being persisted literally, so a newly appended module sorted *before* every normal
non-negative position — the opposite of what was asked. The sentinel is now resolved in the
application layer to the next valid position and the row never holds it.

**The legacy arithmetic is reproduced exactly.** The greatest position in the target pane is
read and two is added. Two, not one, because the renumbering pass assigns odd positions, so
appended positions stay on the same sequence. An empty pane yields position `1`, which is
what the legacy expression produced from `-1 + 2`.

**One legacy consequence is preserved and one legacy omission is not.** Updating a module
that is alone in its pane, with no position supplied, leaves it at `1` — the legacy answer,
because the legacy wrote the row carrying `-1` and only then resolved, so its own read
included the row it had just written. But the legacy *copy* path passed `-1` under a comment
saying "add to the bottom" and then never resolved it, leaving the sentinel in the row; the
intent is honoured and the omission is not reproduced.

**Placing a module on every page appends to each page's own pane**, resolved per target,
rather than reusing one position across pages.

### Validation runs once, on one path, for every write

**What changed.** Four request contracts had no validator at all, so six write actions
reached their service with unvalidated bodies. The validators now exist, reproducing the
measured legacy field rules including the icon-path containment rule that the update verb
was missing while the create verb had it. The shared rules are extracted so the two verbs
cannot disagree, which is what made choosing the other verb a way around the rule.

**Manual validation is gone.** Ten hand-written validation calls across five controllers
invoked the same rules a globally registered filter already ran before the action. Two
invocation paths for one rule set is how the paging contract came to be judged against the
wrong vocabulary; there is now exactly one.

**Three bounds are net additions.** A profile definition's data type admits zero, because
zero is a real type identifier in the legacy list; a length lower bound of zero is new; and
the role-assignment account identifier must be positive, which is justified only by that
column's identity seed and must not be generalised to identifiers that seed at zero or minus
one.

**A latent legacy truncation defect is annotated, not inherited.** The terminal update
procedure still declares narrower parameter widths than the columns it writes.

### Diff hygiene

Four files ended with a trailing blank line and no longer do. Three were reported; the
fourth was found by sweeping every tracked text file, because leaving a known instance of a
reported defect would be a partial fix. The twelve files that lack a final newline are all
in the read-only legacy trees and are untouched by design.


## Review remediation: deliberate behavioural differences

Every entry below was introduced while resolving a code-review finding. Each is a
behavioural difference from the legacy application, from an earlier revision of this
migration, or both, and each is recorded here rather than absorbed silently.

### Portal-scoped authority is bound to the addressed tenant, and installation-wide operations are a separate policy

**Legacy behaviour.** A caller-supplied tenant was honoured only in narrow
circumstances. `SiteSettings.ascx.vb:L235` reads a portal identifier from the query
string **only** when the request sits under the host navigation tree or the caller is a
host account, and forces every other caller onto the ambient tenant.
`EditPortalAlias.ascx.vb:L65-L70` states the same rule as a refusal, rejecting with
"You do not have access to view this Portal Alias" when the addressed record belongs to
a different portal than the ambient one and the caller is not a host account.

**Target behaviour.** A portal-scoped action now requires that the `portalId` in the
route **equal the tenant resolved from the address the caller used**, in addition to the
administrator role it already required. The three genuinely installation-wide portal
lifecycle operations — listing portals, creating one, and deleting one — move to a
separate `HostAdministrator` policy keyed on the super-user claim, so installation-wide
authority is named explicitly instead of being an implicit consequence of holding an
administrator role in one tenant.

**The divergence.** The **host-account arm of the legacy rule is deliberately not
reproduced.** A host credential cannot address another tenant's records through any
tenant's host name; it administers a tenant by addressing that tenant's own alias and
holding its administrator role, and retains the three installation-wide operations
through the host policy. This is a **capability reduction** — narrower than legacy,
never wider — and it is deliberate: honouring the legacy arm would reintroduce exactly
the unbounded cross-tenant authority that separating the two policies exists to prevent,
and AAP 0.2.2.4 additionally places host-level administration outside this migration.
The legacy `DemoSignup` host-setting exception is likewise not carried forward.

Two mechanical consequences are worth recording. A request whose route carries **no**
`portalId` is unaffected, so by-key routes still behave as before. And a request for
which no tenant can be resolved at all is refused rather than defaulted, because there
is nothing to compare the route value against.

### A refresh no longer outlives the account's eligibility to sign in

**Legacy behaviour.** Forms authentication issued a ticket whose lifetime was
independent of the account behind it; disabling or locking an account did not invalidate
a ticket already held.

**Target behaviour.** Rotation now **re-reads credential and membership eligibility**
before it commits, applying the same rules the sign-in path applies — the account must
still exist, must not be locked out, and must be approved, with the measured super-user
exemption preserved. When any of those fails, the presented token is refused **and the
entire refresh family is revoked**, so a disabled account cannot continue trading one
refresh token for the next until the family's own deadline arrives. Every outcome is
recorded as a session audit event.

### The absolute refresh-family ceiling is bounded at thirty days

**Legacy behaviour.** No absolute session ceiling existed.

**Target behaviour.** An individual refresh token was already bounded at thirty days,
but the *family* deadline was accepted anywhere in a thirty-one to three-hundred-and-
sixty-five day range, so a deployment could configure a session family that outlived the
individual token limit by more than a factor of ten. The configurable maximum is now
**thirty days**, validated at startup, and a value above it stops the host with a message
naming the permitted bound rather than being accepted quietly. The store's own
three-hundred-and-sixty-five day guard is retained as a defensive backstop, deliberately
unreachable through configuration.

### A failed credential-cost upgrade is now reported

**Legacy behaviour.** Passwords were stored reversibly, so no cost existed to become
obsolete.

**Target behaviour.** A successful sign-in still succeeds when re-hashing the credential
at the current cost fails to persist — refusing the sign-in would deny service for a
condition the caller cannot influence. What changed is that the failure is no longer
**silently discarded**: it emits a `PASSWORD_REHASH_FAILURE` audit event carrying the
account identifier and the failure reason and no credential material, so a credential
that is quietly stuck at an obsolete cost becomes visible to an operator instead of
persisting indefinitely with no signal.

### The business audit trail is restored, with the real account identifier

**Legacy behaviour.** `EventLogController.vb:L38-L77` defines a vocabulary of stable
business event names, and the legacy application wrote them for portal, user, role and
page changes. For sign-in it recorded only two outcomes, and it recorded them with the
`-1` identifier sentinel rather than the account that attempted the sign-in.

**Target behaviour.** Business events are emitted again through an audit abstraction
declared in the Application layer, with a nineteen-name vocabulary covering portal
creation and removal, user creation and removal, role and role-membership changes, page
updates, every sign-in outcome, and session renewal, refusal and end. The abstraction
carries **no logging dependency**, because the Application project references only the
Domain project and FluentValidation; the implementation that writes the events lives in
Infrastructure. Events are emitted as structured properties, so an operator filters on
the event name rather than matching text, and failures are emitted at warning level so
they can be alerted on separately.

Two differences from legacy are deliberate. Every sign-in outcome is recorded, not two.
And each carries the **real account identifier**, not the `-1` sentinel. No credential,
hash, or token material appears in any event.

### The legacy absent-date marker is read as absence at the boundaries that own it

**Legacy behaviour.** `Null.vb:L66-L70` defines the absent-date marker as
`Date.MinValue`, and `Null.vb:L183-L186` shows the write boundary substituting SQL
`NULL` for a value whose **date part** matches it. A role-membership row therefore
carried absence as SQL `NULL` in the store, while in-memory objects carried the marker.

**Target behaviour.** The Domain model expresses absence as **null and only null**. The
marker is translated at the two boundaries that own it: inbound writes normalise it to
null before the row is staged, and the repository normalises it on the way through, both
mirroring the legacy substitution. Role-active evaluation therefore no longer special-
cases a magic date, which matters because that evaluation feeds authorization.

This is also the one sentinel in the set that is **physically unstorable**: the columns
are SQL Server `datetime`, whose minimum is 1753-01-01, so the marker cannot round-trip
even if something tried. Removing the normalization makes the store raise a range error
rather than silently accepting the value — which is how the normalization is proved to be
load-bearing.

### A child portal's alias is composed beneath its parent, and resolved by longest prefix

**Legacy behaviour.** `Signup.ascx.vb:L175-L290` composes a child portal's alias as the
**current request's authority followed by the child path** — `GetDomainName(Request) &
"/" & ChildPath` — so a child tenant is addressed at a path *beneath* an existing host
name rather than at a host name of its own.

**Target behaviour.** The `IsChildPortal` flag is now **read** by the creation path and
determines how the alias is built: for a child portal the service composes the alias from
the **resolved parent authority** plus the validated path segment, instead of storing the
submitted segment raw. A child portal whose parent authority cannot be resolved is refused
with `portal.parent_alias_unresolved` rather than being created with an alias that could
never match a request. Validation applies the parent/child character-set distinction, so a
host name and a path segment are not judged by the same rule.

Request-time resolution matches an authority-plus-path alias by **longest prefix**, most
specific first, and a new middleware stage rebases the request path before routing so the
application sees a route it can match. That stage is deliberately un-named in the plan's
pipeline order and sits before routing; every stage the plan does name keeps its relative
position.

### Page updates are validated at the edge, and one legacy omission is preserved

**Legacy behaviour.** `managetabs.ascx` declares a required-field validator on the page
name and column widths that the store enforces. A `RequiredFieldValidator` fails on an
empty **and** on a whitespace-only value.

**Target behaviour.** A validator now enforces the one presence rule the legacy screen
declared and the **nine terminal column widths** measured from the schema, so an overlong
value is refused with a validation problem at the edge instead of reaching SQL Server and
surfacing as a server fault. An empty or whitespace-only page name is now refused; an
earlier revision accepted it on a **misreading** of the legacy screen, and that reading is
corrected here rather than left in place.

**Not corrected.** The legacy screen declares **no comparison** between the start and end
dates, so an end date before a start date was storable. That omission is reproduced
deliberately and pinned by a test: correcting it would be an unrequested behavioural
change to a rule the legacy application did not have.

### Page links use a positive allowlist, and special pages cannot disable their link

**Legacy behaviour.** The page editor's URL control produced three useful stored forms: a
numeric page identifier, a `fileid=NNN` reference, or an external URI. The helper
`Globals.AddHTTP` prefixed an unrecognised value with HTTP unless it already contained
`mailto:`, `://`, `~` or a UNC marker. A string such as `javascript:alert(1)` therefore did
not remain an active JavaScript URI; it became an inert HTTP-prefixed value. Separately,
`ManageTabs.ascx.vb` disabled the `DisableLink` checkbox for the portal's administration,
splash, home, login and user pages, and the save path consequently left the stored flag at
`False` for each of those five roles.

**Target behaviour.** The request validator accepts only an ASCII-decimal page identifier, a
case-insensitive `fileid=NNN` token, or an absolute HTTP, HTTPS or mailto URI. Active schemes
such as `javascript:`, `data:` and `vbscript:`, unknown schemes and malformed tokens are
refused rather than rewritten, so no active value can be persisted and returned to a
navigation consumer. `TabService` loads the owning portal and forces `DisableLink` to false
when the edited page occupies any of the five special-page roles. The mapper still copies the
request value mechanically; the stateful portal rule belongs to the service.

### Page header text is raw markup and requires an explicit rendering policy

`Tabs.PageHeadText` remains a verbatim, administrator-authored value for functional parity
with the legacy "Page Header Tags" field. Neither the request contract, mapper nor response
projection parses, sanitises or escapes it. That does **not** make it safe to inject into a
document head: a consumer may render it as markup only behind an explicit, narrowly-scoped
sanitisation and element/attribute allowlist policy at the rendering boundary. Without such a
policy it must be treated as untrusted text. Authorisation controls who may store the value;
it is not a substitute for output sanitisation.

### Paged responses carry the wire envelope, and three derived members are no longer sent

**Legacy behaviour.** Paging was a control-level concern with no wire contract.

**Target behaviour.** Every paged endpoint now returns the mandated `{ items, meta }`
envelope, and the Domain paging type no longer crosses the API boundary. `meta` carries
the total count, page index, page size and total pages.

**The divergence.** Three members an earlier revision exposed — `isUnpaged`,
`hasPreviousPage` and `hasNextPage` — are **not sent**. They were derived, not stored, so
a client that read them off the wire received `undefined` at run time; the client model is
reshaped to match what is actually transmitted, and a consumer that needs them derives
them from the three numbers in `meta`.

### The last-portal invariant is decided and acted on inside one serializable transaction

**Legacy behaviour.** The refusal message is
`SharedResources.resx` keyed `LastPortal.Text`, and the check was a count followed later
by a delete, with nothing between them.

**Target behaviour.** The count and the delete now occur inside a **single serializable
transaction**, so two concurrent removals cannot both observe that two portals remain and
leave the installation with none. Serializable is used here and *not* on the creation path:
creation is guarded by unique indexes on the alias and account-name columns, so the store
already arbitrates a duplicate, and a stricter level would only widen the lock footprint of
the installation's busiest write.

### The shared table always has an accessible name

**Legacy behaviour.** The legacy grids rendered no caption element at all.

**Target behaviour.** The shared table renders a visually-hidden caption and now falls back
to a **generic accessible name** when a consumer projects no caption of its own, so a
consumer cannot produce a table that is unnamed — previously an omitted caption produced an
*empty* caption element, which is worse than none. The fallback uses the framework's default
projection content, so **no input is added** to the component's fixed input surface. It is a
floor, not a licence: projecting a real, screen-specific caption remains the consumer's
obligation and overrides the fallback completely.

### The visual vocabulary gains fourteen tokens, and one measured width is unified

**Legacy behaviour.** The legacy stylesheets contain no spacing, radius or elevation scale,
and no media queries at all.

**Target behaviour.** Fourteen semantic layout, sizing, geometry and measure tokens are
added so that fill values, viewport heights, grid tracks, flex ratios, a clip shape, a
quarter turn and a table minimum resolve to named tokens rather than to literals, and the
four breakpoint widths are named at their single authorised declaration site. The
breakpoint steps remain Sass variables because a media query cannot evaluate a CSS custom
property; the four widths themselves are a **net addition**, since the legacy design had
none.

**One intentional value change.** The form label column is now the canonical measured
**9.375rem** everywhere. One screen previously composed its own **9rem** approximation of
the same measured legacy width, so a single measured value had two definitions that had
already drifted apart; the second is removed. Everything else in this change is
effect-equivalent, and that was verified rather than asserted: ten screenshots across five
routes at two viewport widths are **byte-identical** before and after.

Three inline comments that argued a geometric literal needed no token have been corrected
rather than left to contradict the vocabulary rule they predate.

### Two security suites now bind to the code they claim to protect

**Legacy behaviour.** The legacy tree contains no automated tests of any kind.

**Target behaviour.** The password-hashing and permission-evaluation suites now exercise the
**production** implementations instead of private re-implementations declared inside the test
files. The mechanism and its bounds are recorded under "Note on test visibility" above.

Two deletions are deliberate and are recorded because they reduce the test count. The
duplicate permission evaluator **disagreed with production in three places**, each baking an
installation-wide bypass into the evaluator layer; the shipped design refuses that
pseudo-principal in the evaluator and grants host authority in the service instead, so the
evaluator never holds a flag whose only possible implementation would be "admit everyone".
And four tests describing a grant-**replacement** path were removed rather than ported: the
target publishes no grant-write member, the repository members they described have **no
production caller**, and AAP 0.5.1.4 specifies that surface as a read-only catalogue.

## Correction: the delivered sign-in proves the credential FIRST, performs exactly one comparison per attempt, and reports the approval outcome

**What this entry corrects, and why it exists rather than an edit.** Two earlier
entries in this file describe the sign-in path, and the second of them — *"Correction:
the delivered sign-in evaluates the lock and approval BEFORE the credential, and
answers every refusal uniformly"* — accurately described the service as it then stood.
It no longer does. A security review of this checkpoint found that the order it
recorded, and defended, carried two defects that a migration must not ship, and the
service was changed. This file is append-only, so that entry is left in place and is
superseded here. Where the three disagree, **this entry** is the one that matches
`backend/src/DnnMigration.Application/Services/AuthService.cs`.

**Supersedes corrected claim one — the gate order is now credential-first.** The
delivered order is: resolve the tenant, resolve the account within it, read the stored
credential state, **compare the credential**, and only then evaluate the lock and the
approval. The earlier entry rejected this order on two grounds, and both were
re-examined rather than overruled:

- *"Comparing a locked account's credential lets a guess be confirmed against it."* It
  does not, in the delivered code, because the comparison's **result is discarded
  unread** when the account is locked. The lock is still evaluated ahead of everything
  else and a locked account is still refused without regard to what was submitted. What
  changed is only that the comparison is *performed* rather than *skipped*, which is
  what stops a locked account from answering measurably sooner than an unlocked one.
- *"Spending an adaptive hash verification on every attempt is a denial-of-service
  lever."* That cost is now paid on **every** structurally valid attempt by design —
  see the next paragraph — so it is no longer a property of locked accounts in
  particular. The lever it describes is answered by rate limiting on the credential
  endpoints, which has its own entry in this file, and not by making the response time
  depend on which account was named.

**Supersedes the "residual weakness" paragraph entirely — the defect is repaired, not
preserved.** That paragraph recorded, correctly, that a matching verification code
approved and persisted an account *before* any credential was compared, so a party who
could guess a tenant identifier and an account identifier could flip somebody else's
pending registration to approved without holding its credential. It concluded that
repairing this cost more than preserving it. **That trade-off is reversed.** The
approval gate now sits inside the branch guarded by an accepted credential, so a wrong
password approves nothing and persists nothing. The guessable composition
`portalId & "-" & userId` is preserved exactly, for the parity reason given in the
earlier entries — every account awaiting verification at cut-over still holds a code
this service accepts — and what changed is that the code is no longer sufficient on
its own. **Operational consequence:** an account holder who follows a verification
link must now also present their password to complete verification, which is what the
link's covering message already asked them to do.

**Supersedes corrected claim two — the three approval outcomes are now reported.** The
earlier entry recorded that "enter your code", "that code is wrong" and "not
authorised" were determined and then withheld, because answering specifically would
have confirmed to a caller who had proved nothing that a named account existed and was
awaiting verification. That reasoning was sound *for that gate order* and does not
survive the reordering: the approval gate is now reachable only by a caller who has
already presented the account's correct credential, so `auth.verification_required`
(`EnterCode`), `auth.verification_code_invalid` (`InvalidCode`) and
`auth.account_not_approved` (`UserNotAuthorized`) are returned with authored wording.
This restores the parity Minimal Change Clause item 4 asks for: the legacy screen told
its user which of the three had happened, and so does this.

The uniform denial `auth.invalid_credentials` still answers **every** gate that closes
without credential proof — an unknown tenant, an unknown account name, an account
belonging to another tenant, an account with no credential row, and a wrong credential
— so the enumeration surface is unchanged. The lock remains reported specifically only
to a caller already entitled to it. None of the three approval sentences quotes any
value the caller submitted, so a rejected verification code is never echoed back.

**Extends corrected claim three — one code is added to the vocabulary.**
`auth.approval_store_unavailable` is reported when a correct code accompanied a
correct credential and the resulting approval could not be written. It is deliberately
not one of the three: those describe something the caller did and are actionable by
the caller, whereas this describes a dependency that did not answer while the caller
did everything correctly. Reporting an invalid-code outcome there would tell an
account's own owner that a code it copied correctly was wrong. Its reason token ends
in `store_unavailable`, so the Api edge answers `503`. The three approval codes are
added to that edge's unauthorised list and answer `401`, alongside `auth.locked_out`.

**New: exactly one credential comparison per attempt, whatever was found.** Every
structurally valid submission performs one comparison at the configured work factor.
Where there is no stored value to compare against — an unknown tenant, an unknown
account, or an account with no credential row — a decoy stored value published by the
hashing abstraction stands in for one. The decoy is produced once, at the abstraction's
*current* work factor, from random input the implementation does not retain, so it
costs what a genuine comparison costs and cannot succeed. It belongs to that
abstraction rather than to the sign-in service because the algorithm, the pre-hash
pairing and the current factor are that abstraction's knowledge; a constant embedded in
the caller would become measurably cheaper than a real comparison the first time the
factor was raised. **One residual difference is recorded rather than hidden:** an
unknown tenant performs one database read where a known account performs three. Those
are indexed lookups whose combined cost is a small fraction of one comparison at this
factor. Equalising them would mean issuing reads whose results are discarded, which
buys a small improvement at a permanent cost on every legitimate sign-in.

**New: a wrong credential against an unapproved account now counts toward the lock.**
This follows from the reordering and is an improvement rather than a side effect. Under
the previous order such an attempt produced the not-approved outcome and never reached
the failed-attempt counter, so a pending registration could be guessed against without
limit and could never lock. Pending accounts now count like every other account.

**New: a membership-store outage on the sign-in path is a server fault, not a silent
success.** The failed-attempt increment and the successful-sign-in reset are the
lock-out control itself, not bookkeeping around it, and their outcome is now read. The
store reports four distinguishable results — recorded, recorded-and-locked, no such
record, and store unavailable — where it previously returned a boolean that could not
tell "counted, not yet locked" from "not counted at all". An unavailable store now
fails the request, which the Api edge answers `500` with its fixed generic text; a
missing record is recorded as a diagnostic and the sign-in proceeds. **Operational
consequence:** a membership store that stops answering mid-request now produces visible
`500` responses on sign-in instead of ordinary-looking refusals with a lock-out control
that has silently stopped counting. The outage is escalated on the *success* path too,
because the store answered a credential read moments earlier in the same request, so a
failure at that point means it went down mid-request and issuing tokens would mean
issuing them while the lock-out control is known to be down.

**New: three security-control failures are recorded instead of being absorbed.** A
work-factor upgrade that cannot be written, an effective-permission lookup that fails
unexpectedly during sign-in, and the same lookup failing during a refresh were each
absorbed silently — the first by an empty catch, the other two by substituting an empty
permission set that is indistinguishable from an account that legitimately holds no
permissions. Each is now recorded through a Domain-level diagnostics abstraction whose
signature admits only a closed set of occurrence identifiers, optional tenant and
account identifiers, and a short character-restricted reason code. It accepts no
message, no exception and no format argument, so no credential, hash or caller-supplied
value can be written through it even by accident. The sign-in still succeeds when an
upgrade fails, because the credential was correct and the stored value remains valid;
the permission set still fails closed, because minting a token that claims permissions
the store did not confirm would be worse than minting one that claims none.

## Session revocation is now reached, the refresh exchange re-decides membership, and the session store is bounded per family

**Why this is one entry.** Three findings from the same security review turned out to share a single
theme: an obligation that was documented in full and then not carried out, or a bound that was
argued for and then not enforced on every path. Each is recorded here with what changed and what it
costs, because in two of the three the *documentation* was already correct and only the code was
not - which is the kind of gap that a reader of the contracts alone would never suspect.

### The refresh exchange re-reads approval and lock-out

**What it did.** `RefreshAsync` re-read the tenant, the account, its roles, its permission keys and
its credential advisories, and rebuilt the caller snapshot from all of them - so a role change or a
forced credential change took effect at the next exchange rather than at the next sign-in, exactly
as intended. **Approval and lock-out were not among them**, because they live in the external
membership store rather than on the account row, and nothing on that path consulted it.

**The consequence.** A session whose account had since been locked by repeated wrong credentials, or
whose approval an administrator had withdrawn, went on minting fresh access tokens for as long as the
client kept exchanging - indefinitely, up to the family's absolute ceiling. The whole purpose of a
short access token is that authority is re-decided at the exchange, and these two facts were the ones
it never re-decided. The lock-out control in particular could be waited out by simply never signing
in again.

**What it does now.** The exchange reads the membership state and applies the sign-in gates to it: a
locked account, an unapproved account that is not a host account, and an account whose credential
record has gone are all refused. A state that refuses a sign-in must refuse a renewal, or the renewal
becomes a way to hold a session the account could not otherwise obtain. The host-account exemption
from approval is carried across unchanged, because a host account is created by the installer and is
approved into no tenant.

**And the refusal ends every session, not just the exchange.** Rotation has already consumed the
presented value and minted its successor by the time the gate is reached, so refusing alone would
leave that successor live in the store belonging to nobody, and would leave an account that may no
longer sign in still holding the means to keep trying. The refusal therefore revokes every refresh
token the account holds. **Operational consequence:** locking an account now ends its sessions at the
next exchange rather than only preventing new sign-ins, and the account's holder must sign in again
once unlocked.

### The three operations that must end an account's sessions now do

**What it did.** `ITokenService` states in terms that "every service operation that ends an account's
right to sign in, or that changes the credential by which it does so, must call this member as part of
the same request", and names four: a self-service credential change, an administrative reset, the
withdrawal of an approval and the deletion of an account. `IUserService` restates that obligation on
each of the three members concerned. **The implementing service did not take a dependency on the token
contract at all**, so every one of those statements was unhonoured - the obligation was documented
three times and discharged nowhere.

**The consequence.** An administrative reset performed because a credential was believed compromised
left every session opened under the old credential exchangeable. Withdrawing an approval took effect
for new sign-ins only. Deleting an account left refresh tokens that were still exchanged for access
tokens asserting an identity that no longer existed - and, the account row being gone, nothing
remained for an administrator to find or disable.

**What it does now.** The service takes the session-revocation dependency and calls it on the
credential change (both operations), on the withdrawal of an approval, and on deletion. Granting an
approval revokes nothing, because it takes nothing away, and requiring a future credential change
revokes nothing either - it replaces no credential and the standing requirement travels on every
subsequent exchange, so the client is told to act on it without being signed out first.

**The ordering is the substance, not the call.** The revocation is attempted BEFORE the state change
in all three cases, and its failure abandons the operation. Revoking first and then failing costs a
holder an inconvenience it can undo by signing in again; changing the state first and then failing to
revoke would report the operation as failed while the credential had in fact been replaced, or the
approval withdrawn, with every session still live - a falsehood to the caller as well as the exposure
the revocation exists to close. On deletion the revocation is the first destructive step for the same
reason: nothing has been removed yet, so a refusal leaves the account whole rather than half
dismantled.

**One documented claim was corrected rather than implemented.** `IUserService` said deletion revokes
"within the same unit of work as the rest of the cascade". The session store is not a relational
participant, so no transaction spans it and the cascade together, and an implementation claiming
otherwise would be describing a guarantee it does not have. The contract now promises what is
achievable and required - ordering - and says why the stronger promise is not available.

**Operational consequence.** These three operations can now fail with a dependency error when the
session store cannot be written, where previously they always succeeded. That is the intended trade:
the alternative is an administrator being told an account has been reset, unapproved or deleted while
its sessions continue.

### The credential removal during cleanup is now verified

Two cleanup paths issued the external credential deletion and discarded its answer, so a membership
store that refused looked exactly like one that complied. On account deletion the account row was then
removed anyway, leaving a credential no administrative screen can reach and no later deletion will
revisit, while the operation reported success. On the compensating reversal of a failed portal
creation the same discard could commit a partial reversal.

Both now read the answer and abandon rather than commit. In both cases nothing has been committed at
the point of refusal, so the account - or the newly created portal, its alias, its roles and its
administrator - is left whole for an operator to inspect, which is exactly what the compensation
already promised in its own documentation and did not deliver.

### The session store is bounded per family, not only in total

**What it did.** The store refused to issue beyond a hundred thousand entries, and rotation
deliberately never refused on capacity - correctly, because refusing to rotate destroys a live
session where refusing to issue merely declines a new one. It was argued from there that rotation
could not grow the store without limit, because rotation creates no family and cannot be driven
without a valid token.

**Why that did not hold, in numbers.** Rotation retains the entry it consumed, for replay detection,
and adds a replacement beside it - so a family grows by one entry per exchange, and pruning only
removes a family once its absolute ceiling has passed. Under the shipped configuration a client
rotates when its sixty-minute access token lapses and the ceiling is thirty days, giving about **720**
entries for one continuously rotating session. A hundred thousand divided by 720 is about **139**:
fewer than a hundred and forty long-lived sessions filled the store, after which **every new sign-in
in the installation was refused**. A single authenticated account rotating in a loop reached the same
state far faster. The earlier claim that a hundred thousand entries is "far beyond what any single
process serves within one absolute ceiling" was simply wrong for the configuration this solution
ships.

**What it does now.** A family retains at most **eight** generations - its live one plus its retained
history. Older history is forgotten as rotation moves past it, oldest first, so a family costs at most
that many entries whatever its cadence and however long it lives, and the live generation is never a
candidate for removal because the test is whether an entry can still be redeemed, not where it sits.

Two bounds for this one defect were authored independently: a retention window of eight generations and
a looser cap of sixty-four **spent** generations. The delivered tree carries the eight-generation window
alone. The two used the identical removability test and the tighter one always bound first, so the
looser could never remove anything - and two retention rules that can disagree about the same family are
a liability rather than redundancy. The arithmetic above is what justified either of them and is
unchanged; the figure the store enforces is eight.

**Rotation may refuse on the store-wide cap, and the ordering is what makes that safe.** The per-family
trim runs BEFORE capacity is judged and before the presented token is consumed, so a session is never
refused for space it was about to release, and a refusal leaves the presented token unconsumed and the
family untouched - a retryable condition rather than a lost session. With the window in force rotation
reaches a steady state in which it adds nothing at all, so the cap can effectively only be met through
genuine exhaustion by new families, which issuing already governs.

**The looser sixty-four-generation cap has been removed from the code as well as from the description.**
An earlier paragraph here described it as current: "a family retains at most sixty-four spent
generations". It never did, and could not. The eight-generation trim runs first on every rotation and a
rotation then adds exactly one generation, so a family holds at most nine entries of which at most eight
are spent - a surplus over sixty-four was unreachable by construction. The second pass therefore walked
the family on every rotation, counted its spent generations, and returned having removed nothing. Both
the constant and the pass are gone, so the eight-generation window is the sole retention bound in the
code and in this document alike. Two retention rules that can disagree about the same family are a
liability even when one of them is unreachable, because a later change to either figure silently decides
which one governs.

**The cost, stated plainly.** A replay of a forgotten generation classifies as unrecognised rather
than as already-used, so it is still refused but no longer escalates to revoking the account's
sessions. That escalation is a leak signal rather than a gate: the forgotten value is spent and
unusable either way. Eight generations is several times the window in which a replay can matter at all -
once the legitimate client has rotated past a copied value, every path refuses it whether history
remembers it or not - so the signal is kept for the case that matters, a copied token replayed while it
is still recent, and Trimming oldest-first is what preserves that. The accepted trade is that this
reduction is far smaller than the alternative, which was an installation that could be talked out of
accepting any new sign-in at all.

## Correction: the lazy credential upgrade was claimed in nine places, and one of them was the code

**What this entry corrects.** An entry earlier in this file already establishes that the credential
migration path is an administrative reset and that no legacy credential is upgraded on sign-in. That
correction was right, and it was incomplete: it corrected this file while **nine other places went on
saying the opposite**. A security review of the authorisation checkpoint found them, and this entry
records the sweep so that the claim's full reach is on record rather than only its conclusion.

**Why a documentation defect is worth an entry of its own.** Nothing about this claim breaks a build,
fails a test or throws at runtime. Its entire cost falls on whoever reads it: an operator who believes
pre-existing accounts migrate themselves will not schedule the resets that every one of them requires,
and will discover the gap when their users cannot sign in - after cut-over, with no diagnostic pointing
at the cause, because nothing malfunctioned. That is precisely the class of defect the migration
discipline exists to prevent, which is why an untrue sentence about behaviour is treated here as a
finding rather than as a wording preference.

**The eight prose sites, all corrected.** The repository contract's header note and its
credential-write member; the hashing abstraction's header note and the return documentation of its
upgrade predicate; the account entity's credential-migration paragraph; the authentication contract's
"credential-migration obligation" paragraph and its own inline annotation; the account-administration
contract's statement that a "re-hash-on-first-sign-in half" of the migration lives elsewhere; the
sign-in response contract; the account mapping boundary note; and the account service's header note.
Each now states the same two facts: an administrative reset is the whole path by which a pre-existing
credential becomes usable, and the re-hash a successful sign-in performs is a **work-factor upgrade**
on a value the current scheme itself produced.

Three files were found already stating it correctly and were left alone - the hasher implementation,
the request contract for a credential change, and its validator - and their wording was used as the
reference so that ten files now say one thing rather than nine saying one and three another. Where a
correction replaces a claim, it says so explicitly instead of quietly reading as though the false
version had never been there: a future author who re-derives the plausible-sounding intention will find
it named and refuted rather than absent.

**The ninth place was executable, and it was the substantive fix.** The hasher's upgrade predicate
answered **"yes, replace this"** for any value it could not parse - which is every value held under the
legacy reversible scheme. The earlier defence was a reachability argument: the predicate's one caller
consults it only after a successful verification, verification rejects exactly the values that reach
those handlers, so the answer could never actually be acted upon for a legacy row. The argument is
correct. The answer was still wrong, for two reasons.

- It made the member assert something unperformable. "Replace this" is an instruction to pair the
  answer with a successful verification and re-hash the plaintext that verification yields; for a
  legacy value that verification can never occur, so the instruction can never be carried out.
  Depending on nobody consulting a public member on a domain abstraction outside its one intended
  pairing is a property of future authors' discipline, not of the code.
- It left the false claim asserted in the one place a reader trusts above any comment. Correcting
  eight prose sites while the code went on answering "upgrade this legacy value" would have fixed the
  description and kept the defect.

The predicate now answers **false** for any value it cannot parse, so it reports one thing only: this
scheme produced this value and could now produce a stronger one. Its handled exception set already
mirrored the verifier's; the two now agree on the *answer* as well, which is a stronger and simpler
guarantee - a value this scheme did not produce is neither verifiable nor upgradable.

**Operational consequence: none.** An account whose stored value cannot be parsed could not sign in
before this change and cannot now, because verification rejects it either way, so it required an
administrative reset before and requires one still. What changed is that the system no longer describes
that account as one it is about to upgrade.

**A test was corrected rather than preserved.** A unit test asserted that a legacy value reports as
needing regeneration, and its conformance double was written to match. Both encoded the defect rather
than a guarantee, so both were changed; the test now carries the reasoning, so the assertion cannot be
flipped back without confronting it. A new integration suite exercises the real registered hasher
through the public domain contract and pins the whole boundary in executable form: a legacy value
neither verifies nor reports as upgradable; a value this scheme produced at a superseded cost both
verifies and reports as upgradable; a current value verifies and reports as needing nothing; and the
enumeration decoy is a current-cost value that matches nothing and is never reported as upgradable.
Prose could not have prevented this claim's return. Those five assertions can.

## A third-party module's exception message no longer reaches an HTTP response

**Legacy behaviour.** The legacy platform bound a module's companion class late, from a free-text name
held in a database column, and let whatever that code threw propagate into the page pipeline. What a
visitor saw depended on the deployment's error configuration rather than on any decision this code
made.

**What the target did, and why it looked reasonable.** The module-lifecycle factory catches a module
fault and reports it as a failed outcome, so that one module's failure cannot end an operation
spanning several. A private helper produced the failure's message, and it returned the module's own
`Exception.Message`. Its documentation gave the reason plainly: the underlying explanation must
survive so that the caller can log something actionable. The premise is sound. The destination was
not: the API edge publishes a failed outcome's message verbatim as the RFC 7807 `detail`, so the
"caller" that received the module's text was an HTTP client rather than a log.

**Why that is a disclosure and not merely untidy.** A module is third-party code and its message may
quote anything it had in hand - a connection string, a file-system path, the SQL it was executing, a
configuration value, or the content it was asked to import. None of that can be recognised from the
exception's type, so no filter applied to the message could be trusted, and the same text bypassed
the deliberate redaction that the global exception handler applies to every exception that escapes the
pipeline. One code path was publishing what the rest of the application takes care never to publish.

**Target behaviour.** Each of the four module operations now returns fixed authored text alongside the
stable failure code it already carried, so a client still learns which operation failed and can still
branch on the code, and learns nothing about the module's internals. The diagnosis is written to the
log instead, which is where the original premise is actually satisfied - and it is written there in
the same redacted form the API edge uses for its own diagnostics: the exception's type chain and stack
traces, with **every message dropped and the exception object deliberately not handed to the logger**,
because passing it would reinstate the messages through the logging framework's own formatting. The
log entry additionally carries the operation, the registered controller name and the module instance,
which is more than the returned message ever conveyed. The chain walk is depth-bounded so that a
cyclic or deeply nested failure cannot write an unbounded entry.

**And a backstop at the edge, because fixing the instance does not close the class.** Any future
author may reach for the same premise, and nothing about a `string` announces where it came from. The
edge therefore refuses to publish a failed outcome's message when it does not have the shape of an
authored explanation: multi-line, which is what a stack trace or an aggregated failure looks like, or
longer than any explanation this solution authors. Neither test can be satisfied by a legitimate
message - every authored detail here is one short single-line sentence - so the guard cannot mask a
correct message, and an overlong one is replaced rather than truncated, because half an explanation
with no indication that anything was removed is worse than a stand-in that names the correlation
identifier.

It is deliberately **not** a general redactor: it cannot tell whether a short single-line message
quotes something it should not, and presenting it as though it could would invite exactly the
complacency that produced the finding. The rule remains that a failed outcome's message must be
authored text.

**What is pinned by tests.** That an authored explanation is published unchanged, so the guard has not
become a redactor of legitimate messages; that a multi-line message and an overlong one are both
replaced; and - the invariant that matters most - that **no member of the factory which turns an
exception into text reproduces that exception's message**, inner messages included. That last
assertion was first written as "no member with the shape `Exception -> string` may exist", which was
the wrong invariant: it failed against the redacting describer that legitimately replaced the leaking
one, and would have forbidden the fix. Forbidding the behaviour rather than the shape permits any
number of redacting describers, under any name, and admits no leaking one.

## The write surface is bounded by what the store can hold, and four bound requests gained the validator they never had

Every request contract treated its CLR type as a sufficient bound, although the domains underneath are
narrower in three separate ways: a page offset is multiplied into a 32-bit integer, a fee lands in a
currency column whose range is far smaller than `decimal`, and an instant lands in a calendar that begins
in 1753 rather than in the year one. The consequence was uniform and wrong in the same way each time - a
submission the type accepted reached the provider or the arithmetic, failed there, and surfaced as a
server fault naming no field, when the caller needed to be told which value it could not use.

**Four requests were bound by an endpoint while carrying no validator whatsoever**, so every field on them
was unbounded end to end: `UpdateRoleRequest`, `RoleAssignmentRequest`, `UpdateTabRequest` and the module
settings payload. Their validators now exist and are picked up by the assembly scan with no registration
edit, which the architectural test asserting the exact registered set now records by name.

**Two kinds of bound, and they are not interchangeable.** A *representability* bound says what the column
can physically hold; a *business* bound says what the application permits. The two are stated separately
throughout, and where a business bound was measured absent from the legacy screen it stays absent. The
role fees gained an upper bound that is the currency column's own and emphatically not a price ceiling -
the legacy screen declared none, and inventing one would refuse a fee some installation legitimately
charges. The portal fee already carried a nineteen-digit precision rule, which turns out to leave a narrow
band open: fifteen leading digits of any magnitude satisfy the digit count, so an amount between the
currency ceiling and a sixteen-digit integer part passed the rule and still overflowed the column.

**The upper date bound sits at the last instant of 9999-12-31, not at that day's midnight, and that detail
is load-bearing.** The literal `9999-12-31` is an ordinary stored value meaning "no expiry" that a legacy
reader expects verbatim, distinct from the sentinel minimum date which means absence. A bound stated at
midnight would have refused the very value the bound exists to protect - the rule would have broken the
contract it was added to defend. A test asserts the perpetual value is admitted for exactly this reason.

**Which date pairs are ordered is decided by MEASUREMENT, and an earlier revision of this entry had two
of the three the wrong way round.** The rule is uniform: a pair is compared here if and only if the legacy
screen compared it.

- **Role membership dates ARE compared.** `Website/admin/Security/securityroles.ascx` line 47 declares
  `valDates` with `operator="GreaterThan"`, `controltovalidate="txtExpiryDate"` and
  `controltocompare="txtEffectiveDate"`, message "Expiry Date must be Greater than Effective Date". The
  comparison is strictly greater, so an equal pair is refused too, and it fires only when both dates are
  present, which is the ASP.NET comparison validator's own behaviour. An earlier revision recorded that
  this pair is *not* ordered, on the stated ground that the screen declared no ordering validator. That
  ground was false, and the reason it was believed is worth recording so it is not believed again: **the
  markup writes its tags in lower case**, so a case-sensitive search for `CompareValidator` finds nothing
  while `comparevalidator` finds three.
- **Module term dates are NOT compared.** The module screen's validators on both boxes were format checks
  alone, so an ordering rule there would be a new restriction on callers.
- **Page term dates are NOT compared either.** `Website/admin/Tabs/managetabs.ascx` declares the two date
  boxes with a rendered width and no `ControlToCompare` anywhere, so a reversed pair was a submission the
  legacy screen accepted and stored. An earlier revision ordered them, reasoning that a page whose window
  never opens is hidden permanently. That consequence is real and is a **legacy defect**, which the
  migration discipline requires be recorded rather than corrected: identical inputs must produce identical
  outcomes. Removing the rule also removes an asymmetry that was never justified — the same kind of value
  is now judged the same way on every surface.

**A net-new ceiling on the page refresh interval was likewise withdrawn.** It was proposed at one day and
was self-described as net-new rather than measured; the column is a plain integer and the page screen
declares no validator over the field, so no legacy submission was ever refused on it. The tests that
pinned both withdrawn rules were retargeted rather than deleted, so each still asserts the measured half
of its finding.

**The user reference on a membership assignment IS bounded, to strictly positive values.** `dbo.Users.UserID`
is `IDENTITY (1, 1)`, so no legitimate identifier is zero or negative, and the legacy screen expressed the
same requirement through a drop-down that could only offer real users - an affordance a JSON caller does
not have. The bound is specific to that member and must not be generalised: `dbo.Portals.PortalID` is
`IDENTITY (-1, 1)` and `dbo.Roles.RoleID` is `IDENTITY (0, 1)`, so "at or below zero means absent" is wrong
for a sibling identifier on the same route. Whether the nominated user exists remains the service's
question.
**Deliberate asymmetries, recorded so they are not mistaken for oversights.** The page term dates ARE
compared to one another and the module term dates are NOT. The module screen's validators on both boxes
were format checks alone, so an ordering rule there would be a new restriction on callers; a page's window
decides whether the page is reachable at all, so a window that never opens hides it permanently. The role
membership window is likewise not ordered, for the module's reason: a degenerate window is storable, the
legacy screen declared no ordering validator, and the window is evaluated in SQL on every read. The user
reference on a membership assignment is not bounded either, even though the identity column is seeded at
one - that is schema knowledge rather than a rule the legacy screen applied, and existence is a question a
stateless validator cannot answer.

**The date arithmetic clamps instead of failing, and the weekly case was the worst of the four.** The
offsets that derive a membership expiry called the framework's date arithmetic directly with a stored
period nothing had ever bounded. Three frequencies raised an out-of-range fault. The weekly one multiplied
by seven in unchecked 32-bit arithmetic, so a large period **wrapped to a negative day count and moved the
expiry silently into the past** - an assignment that lapsed the instant it was granted, with no error
anywhere. All four now clamp to the perpetual expiry, which is coherent rather than arbitrary: that value
is already this domain's encoding of "no expiry" and is what a one-off term stores, so a term outrunning
the calendar is recorded as the perpetual term it effectively is, in a form a legacy reader recognises.
The yearly offset is applied as twelve months rather than through year addition so both calendar
frequencies share one range check, and a test pins the two as equivalent.

**A blank page name is now refused, and that RESTORES the legacy behaviour rather than narrowing it.** An
earlier revision accepted an empty name, reasoning that it was "the legacy no-text value arriving
explicitly" and citing the screen's required-field validator as authority. That inverted what the cited
validator does: it declares no initial value, so its initial value is the empty string and it fails
precisely when the trimmed control value equals that. The empty string was the one value the screen
refused, and because the comparison follows a trim, a name of spaces was refused with it. Accepting a
blank name was also actively harmful, which is why it is corrected rather than annotated: a page's stored
path is composed from its name, so every blank-named page under one parent composes the same path and
becomes indistinguishable to anything addressing a page by path.

**The page hierarchy walk is iterative, and the recursion it replaces was a denial-of-service vector.** It
descended once per level of a hierarchy whose depth is set by stored data, and a portal administrator
lengthens that chain one ordinary create call at a time. A stack overflow cannot be caught: the process
terminates, taking every other tenant's in-flight request with it. The visitation order is preserved
exactly, because it is not cosmetic - two running counters assign every page's order in visitation
sequence, so any reordering silently renumbers the whole portal. Children are pushed in reverse so they
pop in sorted order, which reproduces the identical depth-first pre-order the nested calls produced, and a
test asserts the full path sequence over a *branched* hierarchy, since a chain cannot tell depth-first and
breadth-first apart. The depth limit that remains is read from the column rather than chosen: the path
column holds 255 characters and every level contributes at least the two-character separator, so 127
levels is the deepest hierarchy whose path is storable at all. A limit set there refuses nothing that
could ever have been written.

**The settings payload gained the dimension it was missing.** Every setting name and value was already
bounded individually, but nothing bounded the *number* of entries, and the two limits multiply: a body
well inside the request size limit could carry tens of thousands of short settings, each becoming a
tracked entity and a row in one transaction. A per-scope bound of 250 is more than three times the
seventy-two distinct setting names that exist across *every* bundled module in the whole legacy
application, and a lower aggregate bound of 400 is what actually bounds one transaction, since the
per-scope bounds alone do not. An explicitly null map is now refused rather than thrown on - both members
are non-nullable reference types carrying an initialiser, which makes null look unreachable, but an
initialiser only runs when the deserialiser does not assign, and a body carrying `null` assigns over it.

**The shared icon rule exists because three paths disagreed.** The containment check was private to the
role create validator, so the role update and the page update accepted rooted and parent-traversing
references that the create path refused, and the weakest path defined the application's actual behaviour.
It now lives in one place and is applied by all three. It is not validation *of* the reference: both
contracts state that an icon value is opaque and may be a raw `fileid=NNN` token stored verbatim, and a
test pins that the token is admitted unchanged. A link target is deliberately left unconstrained in shape
for the same reason - it legitimately takes three unrelated forms, and a containment rule would refuse
every absolute URL, since all of them carry a scheme separator. What a link target does refuse is a
control character, which no shape of the value can contain and which is the vehicle for splitting a
response header should the stored value ever be emitted into one.

**A regression this work introduced and then removed.** The range rules were first written over the
unwrapped optional value - `request.ServiceFee!.Value` - which made the framework report the failing field
as `ServiceFee.Value`, so the key in the `errors` payload stopped matching the key the caller had sent.
The existing wording tests caught it. The rules are now stated over the optional member itself, against
overloads that treat absence as storable, and every range test additionally asserts the reported field
name so the regression cannot return quietly.

## Two named advisories are now fixed rather than accepted, and the acceptance that remains is bounded by what the pinned framework majors allow

This supersedes the .NET half of *Dependency feed trust, package source mapping, and why
advisories are not build gates* above. That section argued both ecosystems the same way -
that promoting an advisory to a build error would stop delivery on a transitive package
that may not even be reachable. The argument still holds for the packages listed at the end
of this section, but it was applied too widely: two advisories had a real remedy inside the
pinned major versions, and reachability is not the only question worth asking. Where a fix
exists and costs nothing, the fix is taken. Advisory review remains a human activity and
neither `npm audit` nor the NuGet audit is wired into a build; that part is unchanged.

**The native SQLite library is pinned, and the pin contradicts a warning this repository
wrote itself.** CVE-2025-6965 covers `SQLitePCLRaw.lib.e_sqlite3` at every version up to
and including 2.1.11. `Microsoft.EntityFrameworkCore.Sqlite` 8.0.29 selects 2.1.6, so the
version the ORM asks for is vulnerable and there is no patched release anywhere on the line
it selects - 2.1.12 is the first unaffected 2.1.x. The integration test project therefore
pins `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12. The bundle is pinned rather than
`lib.e_sqlite3` alone so that core, provider and lib all move together and the native family
cannot skew across versions. This is the same manoeuvre the approved dependency set already
performs for `Microsoft.Data.SqlClient` 6.1.6, and for the same reason: a transitive package
resolves to a version with a known problem, and a direct reference is the only mechanism
that moves it.

That project's own comment previously forbade exactly this pin, on the grounds that a direct
`PackageReference` wins version resolution outright and so moves the native provider off the
line the EF Core 8.0.29 band was tested against. **That objection is correct and the cost is
accepted deliberately**, because the risk it describes has no path to materialise here: no
`UseSqlite` call exists anywhere in the repository, so the native `e_sqlite3` asset is never
loaded and a load or behaviour regression cannot reach a test. The comment has been rewritten
to say this rather than to forbid it, and it carries the condition attached - if a fixture
ever does adopt SQLite, the pin must be re-validated against that fixture, and aligning the
EF Core version is still preferred over deepening the pin. The precedent does not generalise;
it rests entirely on the provider being provably unexercised.

**A stale claim in the same comment is corrected.** It described the in-memory and SQLite
providers as the default test stores, chosen so the suite runs without a container runtime.
That is no longer true and has not been for some time: every run uses a real SQL Server,
either the instance named by the server-connection environment variable or a container the
fixture starts, and the fixture documents three paths under test that a non-relational
provider cannot execute - a set-based delete, an interpolated-SQL query root, and the
membership availability probe that fails closed unless the provider is SQL Server and the
external `aspnet_*` objects exist. Account creation answering 201 is an acceptance criterion
and cannot pass without them. Both provider references are retained because the approved
dependency set includes them, not because anything uses them, and the comment now says so.

**The critical archive advisory is resolved by five overrides, chosen against a rule rather
than by taking the newest of everything.** An override is only taken where the candidate
version satisfies every *live range* declared against that package - an exact pin being
precisely what an override exists to displace, but a range being a real constraint that a
second consumer will enforce at runtime. On that test `postcss`, `piscina`, `vite` and
`@babel/core` are all safe: every range declared against them admits the patched version,
and only the build tooling's exact pins had to give way.

`tar` is the one deliberate exception, and it is the advisory the review actually named.
Version 6.x is abandoned at 6.2.1 with no patched 6.x in existence, so nothing inside the
declared `^6.1.11` range can clear the finding; only 7.5.21 and later are unaffected. Three
facts make the major move safe rather than hopeful: the upstream registry client has itself
moved to `tar` 7, two other consumers in this very dependency tree were already resolving
7.5.22 successfully alongside the vulnerable 6.2.1 copy, and the consumer holding the old
range is the package-fetching client used by scaffolding commands, not by the production
build or the test run. Both of those were then executed to confirm it.

**Two further candidates were rejected, and the reason is worth recording because the
temptation to take them is obvious.** `esbuild` 0.28.1 clears its advisory, but the bundler
declares `^0.25.0`, which for a `0.x` version admits nothing at or above 0.26. Likewise
`http-proxy-middleware` 3.0.7 clears its advisory, but the development server declares
`^2.0.9`, and two majors of that package legitimately coexist in the tree today. A blanket
override of either would force an incompatible major onto a second consumer's live range -
trading a build-chain advisory for a broken build, which is a worse outcome than the advisory.
They are left alone.

**What remains, and the bound on accepting it.** The critical finding is gone and the total
falls from 29 advisories to 24 - twelve high, eleven moderate, one low. Every one of the
remainder is a build-time or scaffolding dependency that never reaches a browser bundle, and
every one of them reports its only remedy as a framework major bump that the pinned Angular
and Node versions forbid, or has no published remedy at all. The framework's own advisories
keep the unreachability argument recorded in the earlier section: this workspace is built
without server-side rendering, the server platform package is not installed, and no hydration
provider appears anywhere in the source. The standing conditions on that acceptance are
unchanged and now explicit - it must be re-decided whenever the framework major moves, and
whenever a same-major patched version appears for any package listed above, because the rule
that admitted these five overrides will admit those too. Until then the mitigations are the
ones the review asked for: build on isolated runners, and never let an untrusted archive or
untrusted input reach a build step.

**Annotated in code at.** `backend/tests/DnnMigration.IntegrationTests/DnnMigration.IntegrationTests.csproj`,
`frontend/package.json`.


## Completion: opening the view policies was only half of reaching an anonymous grant - the requirement also had to be able to name the tenant

An earlier section recorded that the two view policies deliberately stop demanding an authenticated
caller, so that the migrated grants to the "All Users" and "Unauthenticated Users" pseudo-roles could
be evaluated instead of being refused before the permission handler was consulted. That change was
necessary and correct, and it was not sufficient. Running the finished API against a real database
found an anonymous caller still refused on a page carrying an explicit grant to the unauthenticated
pseudo-role, which is precisely the case the change existed to enable.

**Why it still failed.** The permission handler has to know which tenant it is deciding before it can
ask whether a grant reaches the caller. It took that tenant from the route, falling back to the
caller's token. The page resource is addressed by page identifier alone - `tabs/{tabId}` - so it names
no tenant, and a caller with no token carries none either. Both sources were therefore empty, the
handler abandoned the requirement unevaluated, and the framework turned the unmet requirement into a
challenge. The response was a 401 that looked exactly like a considered denial and was nothing of the
kind: no grant was ever consulted. A grant that cannot be evaluated is not a grant, which is the same
observation that motivated opening the policies in the first place - it had simply moved one step
along, from the policy to the tenant lookup.

**The fix.** The tenant now falls back to the host name the caller requested, resolved through the
same portal-context holder the alias middleware and the administration policies already use. This
restores the legacy behaviour, where the requested alias identified the tenant for every visitor
including the ones with no account. The fallback is reachable only when neither the route nor the
token names a tenant, so it cannot widen a request that already carries one, and it decides nothing by
itself - the evaluator still has to find a grant that reaches the caller.

**How the behaviour is pinned, and why one test would not have been enough.** Three cases are now
asserted against the real pipeline, because any one of them alone is passable by a wrong
implementation. With a grant to the unauthenticated pseudo-role an anonymous caller is served; with
that same single grant an authenticated member is still refused; and with no grant at all the
anonymous caller is refused again. The middle case is the one that matters most - without it, a
fallback that had simply opened the route to everybody would have satisfied the first test while
destroying the distinction the pseudo-role exists to draw. The third confirms the fallback grants
nothing on its own.

**Worth recording about how this was found.** It was not found by the compiler, the analysers, or the
1,986 tests that were green at the time; it was found by driving the running API against a seeded
database and checking a claim the test suite asserted only in its negative form. The suite covered
"anonymous with no grant is refused" and never covered "anonymous with a grant is served", so the
half of the property that the fix existed to deliver had no test at all. The gap is closed above.

**Annotated in code at.** `backend/src/DnnMigration.Api/Authorization/PermissionAuthorizationHandler.cs`,
`backend/tests/DnnMigration.IntegrationTests/Api/TabApiTests.cs`.


## Correction: `NeedsRehash` answers "no" for a stored value it cannot parse

**What this entry corrects.** The credential contract's replacement signal previously answered "yes,
replace it" for a stored value it could not parse, and defended the answer on reachability: a caller
arrives there only after verification has succeeded, verification rejects exactly the values that land
in the malformed-value handlers, so no legacy row could ever be accompanied by an accepted credential
and the answer was unreachable in practice. The reachability argument is correct and was never the
problem. The **answer** was.

**Why the answer was wrong.** It made the member say something untrue about a legacy row. "Replace it"
promises an upgrade that cannot be performed: replacing a stored value requires the plaintext, the
plaintext arrives only with a successful verification, and a value held under the legacy reversible
scheme can never be verified here. Depending on nobody consulting a public member outside its one
intended pairing is a property of future authors' discipline rather than of the code, and the member is
declared on a domain abstraction any caller may reach. It was also the same conflation that had spread
through eight contracts in prose — that a legacy credential is upgraded lazily rather than reset
administratively — so correcting the prose while leaving the code asserting it would have left the
misleading claim in the one place a reader trusts most.

**What it does now, and what that costs.** An unparseable stored value answers "no". It costs nothing
operationally: such an account cannot sign in either way, because verification rejects it, so it
requires an administrative reset whatever this member says. The handled set deliberately mirrors the
verifier's and now **agrees with it on the answer**, so the two members can never disagree about which
stored values this implementation understands.

**Annotated in code at.** `backend/src/DnnMigration.Domain/Abstractions/Services/IPasswordHasher.cs`,
`backend/src/DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs`.

## The icon-containment rule has exactly one implementation, reached by three names

**What this records.** The check that keeps a submitted icon reference inside the portal's own folder
was hoisted out of a private method twice, independently — once for the two role write paths and once
for the page update. Both hoists carried byte-identical character tests and identical message text, and
the delivered tree keeps **one** implementation, `Validation/IconReferenceRules.cs`. The role-path name
survives as a delegating member and its message is aliased rather than restated.

**Why one and not two.** The property the shared rule exists to provide is that every path judges a
reference the *same* way — that is the whole finding, since while the check was private to the role
create validator the role update and the page update accepted references the create path refused, and
the weakest path defined the application's actual behaviour. Two copies would have made that property
hold by coincidence rather than by construction, and would have let the rule be tightened on one
surface and missed on another. A test asserts the identity across all three paths.

**Annotated in code at.** `backend/src/DnnMigration.Application/Validation/IconReferenceRules.cs`,
`backend/src/DnnMigration.Application/Validation/RoleTermsRules.cs`.

## A single alias is addressed only through its owning portal

**Legacy behaviour.** `PortalAliasController.vb:L63` read an alias by its surrogate key alone, and the
administration screen that used it was reachable only by a host account.

**What an earlier target revision exposed.** `dbo.PortalAlias.PortalAliasID` is
`IDENTITY (1, 1)`, so it is unique across the installation and therefore **guessable across
tenants**. The first correction added portal-nested routes but retained a second host-wide
`/portal-aliases` family, making the tenant optional in the public contract. That produced two public
identities for the same read, update and delete and made generated clients choose which one was
authoritative.

**What it does now.** The public API exposes only
`/api/v1/portals/{portalId}/aliases` and
`/api/v1/portals/{portalId}/aliases/{portalAliasId}`. The route always supplies the owning tenant,
and the service compares it against the stored row before anything is reported or written. A mismatch
reads as not-found rather than as a refusal so the operation does not become an oracle for which
surrogate keys exist in another tenant. There is no host-wide list or by-key route.

**Operational consequence.** A host administrator uses the same portal-owned resource identity as
every other caller; installation-wide authority does not create a second alias address. Portal zero
and portal minus one remain valid route values and are never treated as absence.

**Annotated in code at.** `backend/src/DnnMigration.Application/Abstractions/IPortalService.cs`,
`backend/src/DnnMigration.Application/Services/PortalService.cs`,
`backend/src/DnnMigration.Api/Controllers/PortalAliasesController.cs`.

## The response contract now describes what the code emits: four declaration defects closed

**What this records.** Four findings that were all the same defect wearing different clothes — a public
description that a client could act on, contradicted by the behaviour it described. None of the four
changed a business rule; all four changed what the API *says about itself*, which for a generated client
is the only thing it has. Each was settled by measurement against a running instance rather than by
reading the service code, and in two cases the measurement contradicted the explanation that had been
written down.

### A refusal raised inside an action is a problem document, not an empty body

**The defect.** Twenty-one unresolved-tenant guards across the role, role-group, module-definition and
profile-definition controllers answered with the framework's bare `Forbid()`. A controller's `Forbid()`
does **not** pass through the authorisation middleware's problem-details result handler, so it wrote a
`403` with **no body at all**, while every one of those actions declares a problem document for `403`.
A client branching on the declared schema would have parsed nothing.

**What justified it, and why that justification was wrong.** Each controller carried a comment saying
the class-level policy had already refused an unresolved request, so the guard was unreachable and "the
caller sees the same bare `403` either way". The first half is true in spirit and the second half was
false in two ways: the body was not the same as the middleware's, and the *policy* is not what refuses.
Measured both ways — a portal administrator addressing a host name with no alias row is refused by the
policy with `auth.not_permitted`, but a **superuser passes that policy from any host name whatsoever**,
because the policy is anchored to the portal named in the route and an unscoped route names none. That
request is refused by the tenant-resolution **middleware**, with `portal.tenant_unresolved`.

**What it does now.** All twenty-one guards answer through the shared problem-details helper carrying
`portal.tenant_unresolved` — the same failure code the middleware uses — so a client keying on that code
cannot tell the two refusals apart. The guards remain, and remain expected to be unreachable: the
alternative to an unreachable refusal here is the tenant holder throwing, and a `500` is a worse answer
than a `403` for a condition that is not the caller's fault. The human-readable detail is deliberately
**not** unified with the middleware's wording, because the helper's sentence is shared with every other
`403` in this API and bending it to this one cause would make it wrong everywhere else; both sentences
are fixed and caller-independent, naming no host, no alias and no tenant, so neither discloses which
layer refused.

**Annotated in code at.** `backend/src/DnnMigration.Api/Controllers/RolesController.cs`,
`RoleGroupsController.cs`, `ModuleDefinitionsController.cs`, `ProfileDefinitionsController.cs`.

### Three operations advertise the common problem supertype, because both shapes are genuinely reachable

**The defect.** The portal creation, the role listing and the permission catalogue each declared
`ValidationProblemDetails` for `400`, but each can refuse a request whose every member is individually
valid. Such a refusal travels through the shared result translator, which emits a **plain**
`ProblemDetails` — it has a failure code and a sentence and no member to key an error map to. The
declared schema was therefore wrong for a reachable branch of each operation.

**What it does now.** All three declare the base `ProblemDetails`, which is the only schema that honestly
describes an operation emitting both shapes. Each was measured emitting both: the portal creation answers
a plain document for `portal.parent_alias_unresolved` when a child portal is asked for from a request
that resolves to no parent, and a member-named document for an incomplete body; the role listing answers
a plain document for `role_group.scope_invalid` when a group identifier is combined with the ungrouped
scope, and a member-named document for an unorderable `sortBy`; the permission catalogue answers a plain
document for `permission.filter_invalid` on `?moduleDefinitionId=0`, and a member-named document on
`?permissionKey=99`.

**A justification that did not survive measurement.** The permission catalogue also refuses a supplied
but blank `permissionCode` with the same failure code, and that branch was cited as part of the
justification. It is **unreachable over HTTP**: the simple-type binder converts a whitespace-only query
value to null, so `?permissionCode=`, `?permissionCode=%20%20` and `?permissionCode=%09` all answer `200`
with the whole catalogue. The guard is kept for direct Application-layer callers, which can pass what a
query string cannot, and the identifier branch is what actually earns the exemption. Recorded because the
same reasoning — "an unreachable refusal must not earn a declaration" — is what keeps every other listing
held to the validation document: their service-side paging refusals are unreachable, since the
per-collection request validator answers first and names `sortBy`.

**Annotated in code at.** `backend/src/DnnMigration.Api/Controllers/PortalsController.cs`,
`RolesController.cs`, `PermissionsController.cs`,
`backend/src/DnnMigration.Application/Services/PermissionService.cs`.

### A deletion that can report a conflict now says so

**The defect.** The profile-definition deletion declared `204`, `401`, `403` and `404`. Its service
reports a persistence conflict when a concurrent request changes or removes the definition between this
request's read and its write, and the shared status table answers that code with `409` — so the status
was reachable and undocumented. A comment in the service asserted that the endpoint already declared it,
which made the omission read as intentional.

**Why this shape of defect is the worst of the four.** Every other finding here is a status the client
meets with the wrong parser. An undeclared status is one the generated client has no branch for at all,
and it surfaces as an unhandled response. The rules that check declared statuses cannot catch it either,
because a status the operation never mentions gives them nothing to inspect — which is why it is now
pinned by its own test rather than by the general schema rule.

**Annotated in code at.** `backend/src/DnnMigration.Api/Controllers/ProfileDefinitionsController.cs`,
`backend/src/DnnMigration.Application/Services/UserService.cs`.

### An absent assignment date is sent as a written null, not omitted from the object

**Correction, measured.** An earlier revision of this entry stated the opposite — that serialisation is
configured with `DefaultIgnoreCondition = WhenWritingNull` and that a null date is dropped from the
object — and it was wrong on both counts. The configuration is
`DefaultIgnoreCondition = JsonIgnoreCondition.Never`, stated explicitly on both the minimal-API and the
controller serialiser surfaces in `backend/src/DnnMigration.Api/Extensions/ServiceCollectionExtensions.cs`.
Measured against a live response from the delivered build, an open-ended membership returns:

```json
{"userRoleId":1,"userId":1,"username":"admin","displayName":"Baseline Administrator",
 "roleId":0,"roleName":"Administrators","effectiveDate":null,"expiryDate":null}
```

**What it does now.** Both members are `DateTime?`, both are always written, and a null value means the
membership has no bound — effective immediately, or never expiring. The published schema marks them
`nullable` with no `required` entry, which agrees with that behaviour. A client reads the member's
**value** for null; it must not test whether the key is present, because the key is always present.

**Why the setting is `Never` rather than `WhenWritingNull`.** It is a whole-API decision, not a
preference expressed at this endpoint. The legacy null contract is a sentinel table rather than SQL
`NULL` — `Null.vb:L36-L85` encodes an absent integer as `-1`, an absent boolean as `False` and absent
text as the empty string — while this schema seeds `Portals.PortalID` at `IDENTITY(-1,1)` and
`Roles.RoleID`, `Tabs.TabID` and `Modules.ModuleID` at `IDENTITY(0,1)`, and `Globals.vb:L95-L98`
reserves `"-1"` through `"-4"` as role identifiers. Every one of `-1`, `0`, `""` and `false` is therefore
a legitimate value that must reach the wire. An ignore condition that dropped defaults would erase three
identity seeds and the empty-string contract outright; one that dropped only nulls would leave the API
expressing absence two different ways depending on whether a member happens to be nullable. Writing
every member costs a few bytes and removes both hazards.

**Annotated in code at.** `backend/src/DnnMigration.Api/Controllers/RolesController.cs`,
`backend/src/DnnMigration.Application/Dtos/Role/RoleMembershipDto.cs`,
`backend/src/DnnMigration.Application/Dtos/User/UserListItemDto.cs`,
`backend/src/DnnMigration.Application/Dtos/Tab/TabDetailDto.cs`,
`backend/src/DnnMigration.Api/Program.cs`.

**Annotated in code at.** `backend/src/DnnMigration.Api/Controllers/RolesController.cs`,
`backend/src/DnnMigration.Application/Dtos/Role/RoleMembershipDto.cs`.

## Every verb on a profile declaration now agrees on which declarations exist

**Legacy behaviour.** `ProfilePropertyDefinition` carries a `Deleted bit NOT NULL` column, added at
`03.02.03:L1066`, and withdrawal is **logical rather than physical** because stored answers reference the
declaration. The legacy read paths filter on that column.

**The defect.** The two READ paths honoured withdrawal — the single read tested it directly and the tenant
listing filtered on it in the store — while the two MUTATION paths tested only existence and tenancy. A
withdrawn declaration was therefore `404` on `GET`, absent from the listing, and yet freely editable and
deletable through `PUT` and `DELETE`. Three verbs on one address disagreed about whether the resource
existed.

**Why absence, and not a recycle bin.** Both remedies were available and only one is honest here. A recycle
bin is a *contract*: it lets a caller see what it holds, and restore from it. This contract has no such
member — no read, no restore, no `includeDeleted`, nothing on the service interface and nothing on the
controller, which publishes exactly five operations and none of them acknowledges a withdrawn declaration.
The asymmetry therefore had no contract behind it to preserve, and the honest answer for a resource this API
will not show is that it does not exist. Adding a recycle-bin surface instead would have been new
functionality, which the Minimal Change Clause does not license.

**Why this mattered most on the deletion.** Removal on this path is physical — the repository stages a
`Remove` and the recorded answers cascade with it — so the single reachable operation on a declaration the
API refused to show was the destructive one, and it destroyed per-account data that no caller could have
inspected first.

**Reachable against the schema this migration binds to.** This application does not itself produce withdrawn
rows, because its own deletion is physical. An existing DotNetNuke database does, which is the entire premise
of mapping to an unaltered schema: rows carrying `Deleted` are legacy data these members meet on day one.

**One read deliberately still sees withdrawn declarations.** The duplicate-name check is not subject to the
absence rule and must not be "made consistent" with it. The terminal index
`IX_ProfilePropertyDefinition ON (PortalID, ModuleDefID, PropertyName)` is declared UNIQUE at
`03.02.03:L1082` and again at `04.00.04:L1127`, and it does **not** include `Deleted` — so a withdrawn row
still occupies its name in the store. A duplicate check that skipped withdrawn rows would accept a rename the
database then rejects, converting a clear duplicate-name result into a constraint violation surfacing as a
server fault. Absence is the right answer for *addressing* a withdrawn declaration; presence is the right
answer for asking whether its *name is free*. The two rules are different questions, and both are now stated
where they are enforced.

**Indistinguishability.** The withdrawn case reports the same failure code as the unknown and the foreign
cases, so a caller cannot tell them apart. Distinguishing them would confirm that a declaration exists in a
tenant the caller may not read.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`,
`backend/src/DnnMigration.Application/Abstractions/IUserService.cs`.

## The permission handler delegates, and three legacy encodings stop at its boundary

**Artefact:** `backend/src/DnnMigration.Api/Authorization/PermissionAuthorizationHandler.cs`

The thirty-nine imperative permission checks scattered through the administration pages become one
declarative policy answered in one place. The sections above already record why the two view policies
stop demanding an authenticated caller and why the tenant falls back to the requested host. Three
further legacy behaviours end at this boundary, and one latent contradiction is settled rather than
reproduced.

**The legacy evaluation path ignored the grant-or-deny flag; the legacy string builders did not.** This
is a contradiction inside the legacy source, not a difference introduced by the migration.
`TabPermissionController.HasTabPermission` tests only the permission key before consulting the role
predicate (`TabPermissionController.vb:L41`), and `ModulePermissionController.HasModulePermission` does
the same (`ModulePermissionController.vb:L36`) — so the legacy *evaluation* returned true on the first
row whose key matched, **even when that row's `AllowAccess` column was `False`**. Yet the
permission-*string* builders in the very same two classes filter on exactly that column:
`GetTabPermissions` at `TabPermissionController.vb:L218` and `GetModulePermissions` at
`ModulePermissionController.vb:L243` both require `AllowAccess = True`. A deny row was therefore
honoured when a permission string was built and silently disregarded when a permission was evaluated.
The target resolves the contradiction in favour of the builders: a denial wins over an allowance for
the same principal, applied beneath this handler in the single evaluator, so a deny row means the same
thing on every path. The handler does not re-implement, second-guess or post-filter that answer, and it
carries no notion of the flag at all. The defect is annotated in place rather than repaired in the
legacy tree, which stays byte-identical.

**A per-user grant is no longer smuggled through the role channel as bracketed text.** The legacy code
had no way to ask about an account directly, so it manufactured a role name from the account key —
`GetTabPermissions` appended `"[" + UserID.ToString + "];"` (`TabPermissionController.vb:L222`),
`GetModulePermissions` did the same (`ModulePermissionController.vb:L247`), and the evaluation path
probed a per-account grant by passing `"[" & UserID.ToString & "]"` into the role predicate
(`TabPermissionController.vb:L47`). The delimited string it produced looked like `;Administrators;[42];`.
None of that survives: the application service takes a first-class nullable account key, so the
caller's key is handed over unchanged. No bracketed identifier is constructed, no delimited role or
permission string appears on any migrated contract, and nothing is split on a delimiter. The absence of
that key is now load-bearing in its own right — it is what reports that the caller holds no account, and
it is why the value is never coalesced to a stand-in. Substituting `0` or `-1` for a missing account
key would silently convert an anonymous caller into a real one and destroy the "Unauthenticated Users"
distinction; `0` is also a legitimate key for a role, a page and a module in this schema, and `-1` is
simultaneously the legacy integer sentinel, the seed of the portal key and the "All Users" role
identifier.

**A refusal is an HTTP status, not a page.** The legacy screens denied access by sending the browser to
a rendered access-denied page — `Response.Redirect(NavigateURL("Access Denied"), True)` at
`Website/admin/Security/SecurityRoles.ascx.vb:L323`, with `Website/admin/Security/AccessDenied.ascx.vb`
rendering the message. The handler produces no response of any kind. It declines simply by not granting
the requirement, and the framework turns an unmet requirement into a challenge when the caller was
never identified and a refusal once it was; the problem-details body is written by the dedicated
authorisation result handler. The handler never writes to the reply, sets a status code, adds a header,
or throws to signal a denial.

**It also never vetoes, and it never short-circuits on the super-user flag.** Declining to grant is the
correct denial. The framework's hard veto cannot be overridden by any other handler, so using it would
permanently foreclose composing this requirement with an alternative — a "may edit this module, or else
administers the portal" policy could never be expressed afterwards. The super-user flag is deliberately
neither read nor forwarded here: the legacy predicate short-circuited on it, but that test sat *inside*
the row loop (`PortalSecurity.vb:L123`), so a super user matched an assignment row that already existed
and conjured none where the loop never ran. Granting on the flag at this layer would admit a super user
to an item carrying no matching assignment at all, which is a privilege escalation relative to the
behaviour being preserved as well as a duplicate of a decision that belongs one layer down.

**Every ambiguity is a refusal.** Five conditions leave the requirement ungranted: the authorisation
resource is not the current request; no tenant can be named; the scope is not a declared member; the
route does not name the addressed item, or names it unparseably; or the service reports an unsuccessful
outcome. None of them throws, and none is resolved by inventing an identifier — an endpoint carrying an
item-scoped policy but exposing no item key is a registration mistake, and refusing is the safe reading
of it. A parsed key is passed through exactly as it parsed, including zero and negative values, because
this schema makes both legitimate.

**One shared constant replaced a backwards dependency.** `PortalAdministrationEvaluator` previously took
its portal route-segment name from a public constant on this handler, which pointed an evaluator at a
handler for a value that belongs to neither. Both now read `AuthorizationClaims.PortalRouteKey`, which
already held the identical value, so the segment name has one home and the handler's own route-key
constants are private. The route segment names are accepted exactly as the controllers declare them —
`moduleId` and `tabId` — with no generic single-segment fallback, because on a nested route such a
fallback could hand the handler some other entity's key and decide a module question from a page's or an
account's identifier.

**Annotated in code at.** `backend/src/DnnMigration.Api/Authorization/PermissionAuthorizationHandler.cs`,
`backend/src/DnnMigration.Api/Authorization/PortalAdministrationEvaluator.cs`.

## Role orchestration: eight obsolete wrappers, three switches, one sentinel and one uninitialised variable

These six differences all belong to `RoleController.vb` and are recorded together because they were
measured together, in one reading of that file. Each is annotated in place in
`backend/src/DnnMigration.Application/Services/RoleService.cs`.

### The obsoleted region carries eight wrappers, not nine, and none is ported

**Legacy behaviour.** The file closes with `#Region "Obsoleted Methods, retained for Binary
Compatability"`, spanning `RoleController.vb:L846-L888`. It carries **eight** `<Obsolete>` wrappers, at
L848, L853, L858, L863, L868, L873, L878 and L883 — the count is stated as measured because the planning
prose says nine while its own table lists eight. Every one of the eight is a single line of delegation to
a member that IS ported: role creation, the portal role listing, the account's role names, the two
paid-services listings, the role's members, and the two paid-services updates.

**Target behaviour.** None is carried across; the standing rule is that no obsolete member appears in the
target, and delegating wrappers lose nothing when the members they delegate to survive. Two of the eight
additionally carried faults of their own, which is a second reason not to reproduce them. The role-creation
wrapper at L849 is declared `As Integer` and has **no `Return` statement at all**, so it discarded the
identifier it had just delegated for and answered `0` on every call — and `0` is a legitimate role
identifier, because `dbo.Roles.RoleID` is `IDENTITY (0, 1)`, so a caller could not even recognise the
answer as wrong. The paid-services wrapper at L864 passed the literal `-1` described below.

### Three legacy switches disappear with that region, because it was their only carrier

**Legacy behaviour.** `SynchronizationMode` (L849) and `SynchronizeRoles` (L854) toggled the legacy
membership-and-role provider synchronisation model. Both wrappers **ignored the argument entirely** and
delegated to the single-argument member regardless, so neither flag had any effect even before it was
obsolete. `includePrivate` (L408) was a real parameter of the assignment listing, and its bands were
measured: every non-obsolete caller passed `True` (L393), while `False` was passed only by the two
obsolete paid-services wrappers (L865, L870).

**Target behaviour.** All three are dropped. The provider synchronisation model is replaced wholesale
rather than reproduced, so the first two have nothing left to switch. The third reproduces the surviving
behaviour — the `True` band — and offers no switch, because retaining a parameter whose only remaining
caller would be a member this migration does not have is how a dead argument survives a rewrite.

### The `-1` all-users sentinel becomes two members that name what they return

**Legacy behaviour.** `GetServices(PortalId)` at L864 reached the assignment listing as
`GetUserRoles(PortalId, -1, False)`, and the single-argument listing at L376-L377 did the same as
`GetUserRoles(PortalId, -1)`. The `-1` was in the account-identifier position and meant "every account in
the portal". It is also `Null.NullInteger`, the absence marker.

**Target behaviour.** No magic number reaches a parameter. The two answers are separate members that name
what they return — one account's roles, and one role's members — and neither treats a negative identifier
as a wildcard. An identifier that names nothing is reported as not found. Keeping absence and identity
distinguishable is a migration-wide requirement, and an identifier position that also encodes "all" is
precisely where the two collapse into each other.

### Assignment rows are portal-scoped by the service, because the table has no portal column

**Legacy behaviour.** The assignment members took the portal identifier as their first argument
(`RoleController.vb:L277`, L295, L330), and so did the removals.

**Target behaviour.** The repository members take none — `AddUserRoleAsync(UserRole)` and
`DeleteUserRoleAsync(userId, roleId)` — because `dbo.UserRoles` has **no `PortalID` column**: an
assignment is scoped only through the role it names, whose own `PortalID` carries the tenancy. The legacy
argument was therefore never stored; it existed to reach the ambient per-request composite and to look the
account up. Tenant scope is consequently proved in the service, above the repository: the portal, the
role's ownership of that portal and the account's membership of it are all resolved before anything is
staged, and a cross-tenant identifier reads as not found.

### No role read is cached, and the one missing eviction is bounded

**Legacy behaviour.** `RoleController.vb` contains **zero** cache sites of its own; every role read went
to the provider on each call. Separately, `PortalController.vb:L1131` evicted an entry keyed by the bare
literal `GetRoles` after creating a portal's stock roles.

**Target behaviour.** Nothing this service reads is cached, and that is the faithful outcome rather than
an omission — adding a cache where the legacy had none would be the divergence. The cache abstraction is
injected for the opposite duty: a role write invalidates the portal entries and a membership write
invalidates the account entry, so no other subsystem's cached projection outlives a role change. The
`GetRoles` eviction has no counterpart, for the reasons already recorded under *`RemoveCache("GetRoles")`
has no counterpart*, and the window it guarded is empty here too: nothing in the target ever **writes**
that entry, so there is no stale role list for the missing eviction to leave behind. The performance
multiplier that would scale a cache lifetime is consequently not read by this service, because a service
that stores nothing has no lifetime to scale.

### A latent legacy defect in the billing engine, annotated and NOT fixed

**Legacy behaviour.** `Dim Period As Integer` at `RoleController.vb:L508` carries **no initialiser**, so
the runtime seeded it with `0` rather than with the absence sentinel `-1`. Whenever the role lookup at
L518 came back `Nothing`, the sentinel guard at L537 — `If Period = Null.NullInteger` — could not fire,
the frequency stayed the empty string its L509 declaration gave it, the six-case selection at L540 matched
nothing, and the assignment was written with the expiry L534 had just set to `Now`. The result was an
assignment that expired the instant it was created, where the evident intent was no expiry at all.

**Target behaviour.** Annotated in place and **not corrected**, as the Minimal Change Clause requires of a
defect discovered during a migration. It is also unreachable in the migrated shape, and that is a
structural consequence rather than a quiet fix: the assignment operation resolves the role first and
answers `role.not_found` before the derivation is entered, so the role is never absent on any path that
reaches the guard. Nothing depends on that, and nothing re-creates the zero — an absent period is `null`
and is tested as `null`, never against `-1` and never against `0`, so a role that legitimately declares a
period of zero is refused by the shape check rather than mistaken for one that declares none.

### An absent trial frequency now lets the billing terms govern, because the legacy guard did not short-circuit and its right-hand side could not be null

**Legacy behaviour.** `RoleController.vb:L521` reads
`If IsTrialUsed = False And role.TrialFrequency.ToString <> "N" Then`, and the operator is `And`, **not**
`AndAlso` — the non-short-circuiting form. The right-hand side was therefore evaluated even when the
left-hand side had already decided the answer. That never faulted, for two reasons that both stop holding
at the migration boundary: `RoleInfo.vb:L188` declared the property as a `String`, and the legacy reader
coerced a null column to `Null.NullString`, which is the **empty string** and not `Nothing`. The
consequence was reachable and wrong: a role whose `TrialFrequency` column was `NULL` presented as `""`,
`"" <> "N"` evaluated **true**, and so the **trial** terms governed the expiry of a role that declared no
trial at all — taking `TrialPeriod` and an empty frequency into a six-case selection that matched none of
them.

**Target behaviour.** `dbo.Roles.TrialFrequency` is genuinely `char(1) NULL`, so the migrated property is
a nullable enumeration and an absent value is a `null` rather than an empty string. The migrated guard
requires a **present** frequency before the trial can govern, and an absent one falls through to the
**billing** terms. This is a different answer from the legacy one; it is the answer the column's own
nullability implies; and it is deliberate rather than incidental. The guard is written as a pattern match
on the nullable value rather than as a dereference, so neither operand order can fault and the legacy
question of *when* the property is touched stops mattering. The explicit `.ToString` the legacy line
performed on an already-`String` property — a coercion the administration screens' `strict="false"`
compilation permitted — has no migrated counterpart, and neither does the `Convert.ToDateTime` applied to
an already-`Date` local at L543-L546: both are Option-Strict-off coercions that the migrated types make
unnecessary rather than merely tidier.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/RoleService.cs`, and pinned by
`AssignUserToRole_AbsentTrialFrequency_LetsTheBillingTermsGovern` and
`AssignUserToRole_AbsentTrialFrequencyAndConsumedTrial_DoesNotFault` in
`backend/tests/DnnMigration.UnitTests/Application/RoleServiceTests.cs`.

### Cancelling a membership the member does not hold is now reported, where the legacy path deleted nothing and said nothing

**Legacy behaviour.** The cancellation guard at `RoleController.vb:L493-L501` tests the membership for
`Nothing` as the first of its three conditions and, when it is absent, falls to the **else** arm — which
calls `DeleteUserRole` for a row that does not exist. That member returned a `Boolean` the caller did not
read, and the enclosing `Sub` returned nothing at all, so cancelling a membership nobody held was
indistinguishable from cancelling one that existed: both were silent successes.

**Target behaviour.** The removal operation resolves the membership before it decides anything and reports
`role_assignment.not_found` when the member does not hold the role, committing nothing and recording no
audit entry. The externally visible consequence is stated plainly because it changes an HTTP answer: the
route responds `404` where the legacy screen would have completed, and a caller can now distinguish "there
was nothing to withdraw" from "the membership was withdrawn". The same resolution order gives the two
remaining outcomes their own reasons — `role_assignment.protected` for the two assignments the rule
refuses, and the advisory `role_assignment.expired_not_removed` on a **successful** outcome when the paid
trial had already been consumed and the row was back-dated instead of deleted, which is the only way a
caller learns which of the two effects occurred.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/RoleService.cs`, and pinned by
`RemoveUserFromRole_MembershipAbsent_IsReportedAsAReasonAndCommitsNothing`,
`RemoveUserFromRole_ExpiringArm_CarriesAnAdvisoryReasonOnSuccess` and the two protected-rule tests in
`backend/tests/DnnMigration.UnitTests/Application/RoleServiceTests.cs`.

### An unrecognised frequency character still does not throw, and the value that survives it differs

**Legacy behaviour.** The six-case selection at `RoleController.vb:L540-L547` has **no `Case Else`** — L547
closes the `Select` immediately after the year case. A character outside the six therefore matched nothing
and the expiry simply kept whatever the normalisation above had left it, which was never an error. Because
the local was seeded from the ambient clock at L505 and advanced to `Now` at L534 whenever it lay in the
past, an unrecognised character in practice stored an expiry equal to **now** — an assignment that lapsed
the moment it was written.

**Target behaviour.** The non-throwing shape is **preserved**, as the Minimal Change Clause requires of a
legacy shape discovered during a migration: the migrated switch carries a discard arm that yields the same
local, so an unrecognised character is still not a failure. Preserving it matters because the terminal
columns carry neither a foreign key nor a check constraint — `03.00.01.SqlDataProvider` drops the
`CodeFrequency` lookup and its constraint and nothing recreates them — so the store accepts any single
character and an unrecognised one is genuinely reachable from live data. A throw would have turned one bad
character into a failed operation on a row that is otherwise readable.

What **differs** is the value that survives, and the difference follows from the bounds being primed from
the request rather than from the ambient clock: where the legacy engine stored an already-lapsed expiry,
the migrated engine stores **no expiry** when the caller submitted none, and preserves the caller's bound
unchanged when one was submitted. That is the sane reading of the same fall-through, and it is recorded
here rather than absorbed.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/RoleService.cs`, and pinned by
`AssignUserToRole_UnrecognisedFrequencyCharacter_DoesNotThrowAndLeavesTheBoundAlone` and
`AssignUserToRole_UnrecognisedFrequencyCharacter_PreservesASubmittedFutureBound` in
`backend/tests/DnnMigration.UnitTests/Application/RoleServiceTests.cs`.

### The renewal bounds are primed from the request, where one of the two legacy members primed them from the row

**Legacy behaviour.** Two members wrote an assignment and they disagreed with each other. L295 stored the
caller's effective and expiry dates **verbatim** and ran no derivation at all. L489 ran the whole
derivation and had no date parameters to ignore, priming both bounds from the row it had just read
(L513-L514) and the trial-used fact with them (L515).

**Target behaviour.** One member serves both, so one of the two readings had to give way. The derivation
is kept, because it carries the paid-membership rules the migration must preserve, and the caller's dates
are honoured as its input. The consequence is confined to a single case and is stated rather than hidden:
renewing an assignment whose stored expiry is still in the **future** while submitting no expiry of one's
own offsets from the present instant rather than from that stored expiry, so the unexpired remainder of
the term is not carried forward. A caller wanting the legacy behaviour submits the stored expiry — which
is exactly what the screen this member serves did, `SecurityRoles.ascx.vb:L273-L303` having read the
existing assignment solely to pre-fill the two inputs it then posted back. The trial-used fact is still
primed from the stored row, because nothing a caller submits may reset it.

### Both clock readings are UTC where the legacy readings were server-local

**Legacy behaviour.** The billing engine read the ambient clock four times — the expiry seed at L505, the
effective-date comparison at L530, and the expiry comparison and assignment at L533-L534 — and the
cancellation path read `Date.Today()` at L496 through the Visual Basic runtime's day-offset intrinsic.
Both `Now` and `Date.Today()` are **server-local**.

**Target behaviour.** The derivation takes **one** reading from the injected clock instead of four, which
is not a liberty: four readings of a moving clock can disagree, so a request that crossed a tick between
L530 and L533 could clear an effective date against one instant and seed an expiry from another. One
reading makes the derivation internally consistent. The clock is **UTC only**, so a derived expiry — and
the back-dated expiry on the cancellation path, which is a date-only value — can fall on a different
**calendar day** from the one a legacy installation would have computed for the same real instant, by up
to the host's offset from Greenwich. Accepted deliberately, for the reasons already recorded under
*Expiry dates are computed from a UTC clock, not server-local time*: a local-zone stamp is not comparable
between hosts and cannot be read without knowing the machine that wrote it, and the injected clock is
what makes this engine testable at all. The `.Date` truncation on the cancellation path is preserved,
because the legacy value carried no time either and because the row must read as expired for the whole of
the current day rather than only after the current hour.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/RoleService.cs`.

## The permission cleanup commits both grant tables as one unit, evicts what it invalidated, and caches only the catalogue

Four deliberate differences in `PermissionService`, all in the same area and all measured against the
three legacy controllers it replaces — `PermissionController.vb` (71 lines, 9 public members, **0**
`DataCache` references), `ModulePermissionController.vb` (389 lines, 18 members, 11 references) and
`TabPermissionController.vb` (349 lines, 15 members, 10 references).

**1. Removing an account's grants is now one transaction, where the legacy pair was two independently
durable statements.** The legacy cleanup issued `DeleteModulePermissionsByUserID` and
`DeleteTabPermissionsByUserID` back to back — `ModulePermissionController.vb:L218` and
`TabPermissionController.vb:L209` — and each committed on its own. The provider *declared* transaction
members at `Library/Components/Providers/Data/DataProvider.vb:L70-L74` and neither cleanup invoked them,
so a failure between the two left an account's module grants removed and its page grants intact: grants
that outlive the account which held them, inheritable by a later account reusing the identifier. Both
target repository members issue a set-based delete that reaches the store when it is called rather than
when changes are flushed, so an enclosing transaction is the only construct that makes the pair atomic.
The removal now opens one transaction, issues both deletes, saves and commits, and rolls back on
disposal if no commit was taken — including on a cancellation observed between the two deletes. The
legacy behaviour is **annotated rather than reproduced**: reproducing it would mean writing a known
half-failure into new code, and the unit-of-work boundary is what this layer is required to own.

**2. The eviction the legacy cleanup performed is restored, and the tab-keyed family is reached page by
page.** `ModulePermissionController.vb:L220` cleared the module-permission entries of every tab in the
portal and `TabPermissionController.vb:L211` cleared the portal's page-permission entry, both
immediately after those same two deletes. An earlier revision of the service dropped both silently,
which left a deleted account's grants being served from a warm entry — stale *authorisation*, not a
stale listing. The page-permission entry is portal-keyed and is evicted directly; the module-permission
entry is **tab-keyed**, so the portal-wide clear is expressed by naming each of the portal's pages in
turn. That is the breadth `ICacheService` documents its narrow members as replacing, and it is what the
legacy private `ClearPermissionCache(moduleId)` did internally at `ModulePermissionController.vb:L62-L66`
— resolve the module, then clear by its owning `TabID`. No new cache member was invented for it. The
eviction is ordered **after** the commit, so a concurrent reader cannot repopulate the entry from rows
the transaction is about to remove, and a rolled-back transaction does not discard a valid entry.

**3. The catalogue reads are cached; the decision reads deliberately are not.** The two legacy cache
entries held **grant row sets** — a tab-keyed dictionary under `ModulePermissions{0}` and a portal-keyed
list under `TabPermissions{0}` — and the service contract exposes no grant-set read at all, because the
grant-management surface is deliberately absent. Those two entries therefore have no counterpart read to
attach to and belong to the evaluator that consumes grant rows. What is cached instead is the permission
**catalogue**, which is a net addition rather than a translation: the catalogue controller carried no
cache site whatsoever. It is nevertheless the one thing in the file that is safe to cache — host-wide
reference data seeded by the upgrade scripts, with no write member anywhere in the solution, consumed by
no access decision. The legacy lifetime arithmetic is reproduced exactly, twenty minutes times the
installation-wide performance multiplier, as is the guard that went with it at
`ModulePermissionController.vb:L183` and `TabPermissionController.vb:L290`: a multiplier of zero
**bypasses the cache entirely** rather than writing an entry that expires at once. A new key family is
used rather than either legacy name, because writing a catalogue projection under a grant key would both
misdescribe the entry and collide with the eviction targeted at that exact name. Only **projections** are
cached, never entities: the permission repository issues no no-tracking query, so caching what it returns
would hand a later request an entity attached to a disposed change tracker. The effective-key and
decision members are **not** cached, and that omission is a security judgement rather than an oversight —
their answers are caller-dimensioned and no grant mutation exists on this contract from which such an
entry could be invalidated, so a warm entry would outlive a grant change with no hook able to clear it.

**4. Two contradictory ways of addressing a module placement are refused for every key.** The contract
states without qualification that naming a placement by its own key and also naming a page that is not
the one that placement sits on is a contradiction, and that a contradiction is refused rather than
resolved in favour of either. The rule was enforced only inside the inherited-view branch, so a
contradictory pair asking about any other key never reached it and was answered from the module's own
grants as though no placement had been named — which is the wider answer, and therefore the exploitable
one. The check now runs before the key is considered. The refusal is a **denial** rather than a failure,
because the member publishes exactly two failure codes — an absent module and an undefined key — and a
contradiction has none; a denial is also the closed default this whole area falls back to.

**No log and no audit event is emitted, and the omission is measured.** All three legacy controllers
contain **zero** `AddLog` call sites; their only six logging references are `LogException` inside the
row-hydration helpers that the object-relational materialiser deletes outright, so there is no audit
trail to preserve. The account deletion that reaches the cleanup member is already audited as
`USER_DELETED` by the account service, so a second event here would double-count one action.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/PermissionService.cs`, with
regression coverage in `backend/tests/DnnMigration.UnitTests/Security/PermissionEvaluatorTests.cs`.

## The account service: the permission cascade reaches its owner, and five omissions are named

**Why this is one entry.** Every difference below was found while completing
`backend/src/DnnMigration.Application/Services/UserService.cs`, the port of
`Library/Components/Users/UserController.vb` — 1,372 lines, 64 public members, every one of them
`Shared`. They share a theme: in each case the legacy behaviour was *measured*, and the target either
routes it somewhere better or declines to carry it forward. None of them is a silent absorption.

### The user-deletion permission cascade is orchestrated through the permission contract

**Legacy behaviour.** `UserController.vb:L200` cleared the account's direct grants by calling two
separate provider members before removing the account — `DeleteModulePermissionsByUserID`, reached
from `ModulePermissionController.vb:L218`, and `DeleteTabPermissionsByUserID`, reached from
`TabPermissionController.vb:L209`. Each took the account **object** and read the portal off it.

**What it did.** The delivered account service issued those two table deletes itself, against the
grant repository. That worked, and it was committed and tested — but it put a second copy of a
permission rule inside a service whose subject is accounts. The rule in question is not incidental:
only grants made **directly** to the account may be removed, because a grant the account receives
**through a role** belongs to the role, and removing it would strip every other holder of that role.
Two copies of that rule are free to drift, and the drift would be invisible until the day one of them
was updated.

**What it does now.** The cascade is one call to `IPermissionService.DeleteUserPermissionsAsync`,
which is the member that owns both grant tables and states that rule once. The account identifier
crosses the boundary as an `int`, never as an entity — which is what makes it impossible to repeat the
legacy mistake of reading the portal off the object and widening the removal to every portal the
account belongs to. The call **stages** rather than commits, so it still joins the single
`SaveChangesAsync` at the foot of the delete and an abandoned deletion leaves the grants in place.

**The divergence, and it is a fix.** The refusal is now **propagated**. The two repository calls
returned `Task` and reported nothing, so a cascade that failed could not be detected; the consolidated
member returns `Result`, and a failure abandons the deletion with the account wholly intact. The
alternative — committing an account row as deleted while its grants remained — is the single outcome
the cascade exists to prevent, because a later account reusing the identifier would inherit them.

**A documentation defect closed with it.** `Api/Controllers/PermissionsController.cs:62` already
asserted this collaboration in prose — *"DeleteUserPermissionsAsync by UserService as part of the
user-deletion cascade. Nothing is orphaned by their absence here"* — while no such call existed. The
contract documentation and the code now agree, and `DeleteUserPermissionsAsync` has the production
caller its own documentation claims for it.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`,
`backend/tests/DnnMigration.UnitTests/Services/UserServiceTests.cs`.

### The deletion guard takes no caller-supplied override, and one boolean becomes three named refusals

**Legacy behaviour.** The signature was
`DeleteUser(ByRef objUser As UserInfo, ByVal notify As Boolean, ByVal deleteAdmin As Boolean) As Boolean`
(`UserController.vb:L200`). The tenant's designated administrator was read off the tenant row with
`Convert.ToInt32(dr("AdministratorId"))` at `L209` and, when the account being removed *was* that
administrator, `L210` set the verdict to whatever the **caller** had passed as `deleteAdmin`. The
answer was a single `Boolean`, set false from three unrelated places: the administrator guard, a
refusal from the membership provider at `L231`, and the `Catch` at the foot of the method, which
swallowed every fault into the same value.

**Target behaviour, and the two divergences.** `DeleteUserAsync(portalId, userId, cancellationToken)`
takes the tenant, the account and cancellation, and nothing else.

- **`deleteAdmin` is not carried forward.** Whether a tenant's designated administrator may be deleted
  is an authorisation rule, and the legacy signature delegated it to whichever screen happened to be
  calling — so the protection was only as strong as the least careful call site. The administrator is
  now refused unconditionally, and reassigning the designation on the tenant is the supported way to
  make that account removable. A second guard is added ahead of it: an installation-wide account is
  refused outright, because host accounts are beyond a single tenant's administration. That guard has
  no legacy counterpart at all on this path.
- **`notify` is not carried forward.** It selected a mail notification, and the mail subsystem is out
  of scope.

**One boolean becomes three reasons.** A caller previously learned only that the deletion had not
happened. The three refusals now carry `user.not-found`, `user.delete.superuser-protected` and
`user.delete.administrator-protected`, so an account holder can be told which of them applied. The
refusals stand ahead of every destructive step, and nothing — sessions, grants, enrolments, membership
or credential — is touched once one of them fires.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`,
`backend/tests/DnnMigration.UnitTests/Application/UserServiceTests.cs`.

### The membership-settings read is not cached, and the legacy expiry it drops was three minutes, not sixty

**Legacy behaviour.** `GetUserSettings` at `UserController.vb:L656-L670` cached its answer under the
key `SettingsKey(portalId)` — `"UserSettings|" + portalId`, composed at `L924` — and the expiry is the
reason this entry exists. It is `TimeSpan.FromMinutes(Globals.PerformanceSetting)`: the performance
setting is **the whole timeout**, not a multiplier over a 20-minute base as at every other caching
site in the legacy tree. `Globals.PerformanceSetting` resolves an absent host setting to **3**, so the
real legacy lifetime of this entry was **three minutes** against the sixty the usual convention would
imply.

**Target behaviour.** The read is not cached. It resolves the tenant's "User Accounts" module by
definition name and reads ordinary module settings against it, exactly as the legacy did, and returns
a **successful result whose value is `null`** when the tenant has no such module — the legacy returned
`Nothing` in that case and its callers fell back to their own defaults, so reporting a failure would
change behaviour those screens depended upon. As in the legacy, an absence is never cached.

**Why the cache is not reproduced, and it is a correctness reason rather than a performance one.** The
caching abstraction classifies a key by **declared prefix family** in order to scope its invalidation,
and `"UserSettings|"` is not one of the declared families, so `InvalidatePortal` could not evict this
entry. The rows behind it are *ordinary module settings* on the account module instance, writable
through the module service as well as through the settings-write member beside the read, so an entry
only the account service knew how to evict would go stale on a write the account service never saw.
The sibling module service records the same decision, for the same reason, for the analogous
`GetModuleSettings<id>` and `GetTabModuleSettings<id>` keys. What is cached instead is the
profile-definition catalogue, which is installation-time reference data with both a declared family
and a declared eviction member — and which correctly uses the **conventional** `20 ×` multiplier.

**The measurement is recorded so it is not "corrected".** Anyone reintroducing this cache must
reproduce **three minutes** and must not normalise it to `20 × multiplier`, which would multiply the
staleness window twentyfold.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### Defect 1: the legacy credential predicate overwrote its verdict, and the defect is annotated rather than fixed

**Legacy behaviour.** `ValidatePassword` at `UserController.vb:L1067` evaluated three rules, but its
third rule **assigned** its verdict rather than accumulating it. A credential that failed the minimum
length rule and then matched the configured strength expression was therefore reported **valid** — the
earlier failure was overwritten.

**Why nothing in `Library/` changes.** Per AAP §0.9.1 a discovered defect is annotated in place and not
fixed. The legacy tree is a read-only reference input and remains byte-identical.

**Why the defect is dormant, and what the target does.** No strength expression is configured in this
installation, so the third rule never runs and the legacy and target verdicts agree on every input this
configuration can produce. The target evaluates the rules in order and returns on the first refusal,
which is evidently what the legacy code was reaching for. Were a strength expression ever configured,
the target would refuse a short credential that the legacy predicate accepted — recorded here rather
than left to be discovered in production.

**Where enforcement lives.** The legacy predicate was **public**, and therefore a second policy free to
diverge from the one the screens declared; it is not exposed on the target contract at all. Declarative
request validation in `Application/Validation/` owns enforcement. The service retains a **private**
guard that reads the **same bound `PasswordPolicyOptions` instance** those validators read, so there is
one policy with one source: it hardcodes no threshold, adds no rule the options do not declare, and
skips the non-alphanumeric and strength rules unless the configuration asks for them. Its purpose is
that a credential write is never performed merely because a validator happened to be wired at the HTTP
boundary — the service is also reachable from a test, a seeder, or a future host with no such pipeline.
The legacy policy itself is preserved verbatim from `Website/release.config:L242-L245`: minimum length
seven, zero required non-alphanumeric characters, no question-and-answer requirement, and electronic
mail uniqueness not enforced. Tightening a policy during a migration locks out existing account
holders, so any hardening is a separate and explicit decision.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The ambient caller and tenant accessors are gone, and the never-null account sentinel with them

**Legacy behaviour.** `GetCurrentUserInfo` at `UserController.vb:L381` read the acting account out of
per-request state — `HttpContext.Current.Items("UserInfo")`, falling back to
`Thread.CurrentPrincipal.Identity` when there was no request at all — and on every miss returned a
**newly constructed empty account** rather than nothing. The delete at `L237` separately reached
`PortalController.GetCurrentPortalSettings`, purely to label its audit entry; that accessor read
`HttpContext.Current.Items("PortalSettings")`, a mutable per-request composite whose `ActiveTab`
callers could reassign mid-request.

**Target behaviour.** Neither accessor is reproduced. The acting caller arrives through the injected
`ICurrentUser` abstraction and every member takes the identifiers it acts on as explicit arguments,
`portalId` first. No `PortalSettings` type appears anywhere on the service — and could not, because the
layer graph gives the Application project a reference to Domain only.

**The divergence.** The **never-null contract is deliberately not carried forward.** Returning a hollow
account made "nobody is signed in" indistinguishable from "an account with no identifier", so a caller
that forgot to test the identifier silently operated as an anonymous ghost. Absence is now reported as
absence.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The profile-definition surface is definition management, not a read-only lookup

**The ambiguity.** The plan describes this surface two ways: AAP §0.5.1.4 calls the API surface a
*"Lookup surface"*, while AAP §0.5.1.8 calls the client feature *"Definition management"*. The
reconciliation is recorded here so it is not re-litigated downstream.

**The reading, and what settles it.** **Definition management.** `IUserService` declares a create, an
update and a delete for profile property definitions alongside the two reads, so read-only is not an
available interpretation of the contract. The legacy source agrees:
`Website/admin/Users/ProfileDefinitions.ascx.vb` is a management grid with add, edit, reorder and
delete commands and `EditProfileDefinition.ascx.vb` is its editor, so a read-only surface would have
**lost a workflow the legacy application offered** — which UI functional parity forbids. The narrower
phrase is best read as describing how the definitions are *consumed* by the profile screens, which do
only read them.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### Nine further legacy members are omitted, and the omissions are named

**What is not ported, and why.** `DeleteUsers` (`L273`) and `DeleteUnauthorizedUsers` (`L293`), the
bulk sweeps — a reachable "remove every account in this tenant" operation is a defect rather than a
feature, and neither has an endpoint in the target API. `SetAuthCookie` (`L919`), **whose body is empty
in this checkout**, wrote a forms-authentication cookie that has no counterpart under bearer
authentication. `GeneratePassword` (`L314`, `L330`) — a generated credential has to be transmitted to
be useful, which reintroduces exactly the disclosure that dropping retrieval removed.
`GetUserCreateStatus` (`L598`) mapped a status to a message drawn from the excluded localisation
mechanism; the status becomes a stable failure code and the wording is authored in the client.
`GetUserMembership` (`L638`) was a `Sub` that mutated the account it was handed and reported nothing at
all; the membership facts it fetched are materialised on the account by the repository read path.
`GetOnlineUsers` (`L415`) is excluded with the users-online subsystem and `GetSuperUsers` (`L1331`)
with host-level administration. `UpdateDisplayNames` (`L1259`) is a bulk maintenance sweep with no
endpoint. `GetUserCountByPortal` (`L582`) is omitted as **redundant rather than excluded**: the paged
envelope the listing member returns already carries the tenant-wide total, so a second member
answering the same question from a different query would be a second source of truth for one number.
The hydration and provider-synchronisation switches go with them — `isHydrated`, `hydrateRoles`,
`ProgressiveHydration`, `SynchronizeUsers` and `AddToMembershipProvider` — because the shape of a
response is settled by its data transfer object, never by a caller-supplied switch. The caching
*members* (`GetCachedUser` `L350`, `SettingsKey` `L924`, `GetCacheKey` `L1310`, `CacheKey` `L1315`) and
the two stateful controller properties that callers configured before invoking a method
(`DisplayFormat` `L1210`, `PortalId` `L1219`) are gone; the service holds no mutable state of any kind.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The Option Strict asymmetry is resolved towards the stricter side

**Legacy behaviour.** The class library was compiled with Option Strict **on**
(`Library/DotNetNuke.Library.vbproj:L24` — note that `L23` is `OptionExplicit`, so a citation of `L23`
for Option Strict is off by one, and the correction is recorded here). The admin code-behinds this
service absorbs its rules from were compiled with `strict="false"`
(`Website/release.config:L125`) — Option Strict **off** — and could therefore rely on late binding and
implicit narrowing that C# rejects outright.

**Target behaviour.** Every such conversion is made explicit. Text arriving from a stored module
setting is parsed with an invariant culture and an explicit fallback rather than coerced, so a value
the legacy screen would have silently collapsed to `0` or `""` is either parsed or refused. This is
also why the twelve `Website/admin/Users/*.ascx.vb` code-behinds are treated as reference inputs for
endpoint and screen semantics rather than as candidates for line-by-line translation.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

## The account service: the permission cascade reaches its owner, and five omissions are named

**Why this is one entry.** Every difference below was found while completing
`backend/src/DnnMigration.Application/Services/UserService.cs`, the port of
`Library/Components/Users/UserController.vb` — 1,372 lines, 64 public members, every one of them
`Shared`. They share a theme: in each case the legacy behaviour was *measured*, and the target either
routes it somewhere better or declines to carry it forward. None of them is a silent absorption.

### The user-deletion permission cascade is orchestrated through the permission contract

**Legacy behaviour.** `UserController.vb:L200` cleared the account's direct grants by calling two
separate provider members before removing the account — `DeleteModulePermissionsByUserID`, reached
from `ModulePermissionController.vb:L218`, and `DeleteTabPermissionsByUserID`, reached from
`TabPermissionController.vb:L209`. Each took the account **object** and read the portal off it.

**What it did.** The delivered account service issued those two table deletes itself, against the
grant repository. That worked, and it was committed and tested — but it put a second copy of a
permission rule inside a service whose subject is accounts. The rule in question is not incidental:
only grants made **directly** to the account may be removed, because a grant the account receives
**through a role** belongs to the role, and removing it would strip every other holder of that role.
Two copies of that rule are free to drift, and the drift would be invisible until the day one of them
was updated.

**What it does now.** The cascade is one call to `IPermissionService.DeleteUserPermissionsAsync`,
which is the member that owns both grant tables and states that rule once. The account identifier
crosses the boundary as an `int`, never as an entity — which is what makes it impossible to repeat the
legacy mistake of reading the portal off the object and widening the removal to every portal the
account belongs to. The call **stages** rather than commits, so it still joins the single
`SaveChangesAsync` at the foot of the delete and an abandoned deletion leaves the grants in place.

**The divergence, and it is a fix.** The refusal is now **propagated**. The two repository calls
returned `Task` and reported nothing, so a cascade that failed could not be detected; the consolidated
member returns `Result`, and a failure abandons the deletion with the account wholly intact. The
alternative — committing an account row as deleted while its grants remained — is the single outcome
the cascade exists to prevent, because a later account reusing the identifier would inherit them.

**A documentation defect closed with it.** `Api/Controllers/PermissionsController.cs:62` already
asserted this collaboration in prose — *"DeleteUserPermissionsAsync by UserService as part of the
user-deletion cascade. Nothing is orphaned by their absence here"* — while no such call existed. The
contract documentation and the code now agree, and `DeleteUserPermissionsAsync` has the production
caller its own documentation claims for it.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`,
`backend/tests/DnnMigration.UnitTests/Services/UserServiceTests.cs`.

### The membership-settings read is not cached, and the legacy expiry it drops was three minutes, not sixty

**Legacy behaviour.** `GetUserSettings` at `UserController.vb:L656-L670` cached its answer under the
key `SettingsKey(portalId)` — `"UserSettings|" + portalId`, composed at `L924` — and the expiry is the
reason this entry exists. It is `TimeSpan.FromMinutes(Globals.PerformanceSetting)`: the performance
setting is **the whole timeout**, not a multiplier over a 20-minute base as at every other caching
site in the legacy tree. `Globals.PerformanceSetting` resolves an absent host setting to **3**, so the
real legacy lifetime of this entry was **three minutes** against the sixty the usual convention would
imply.

**Target behaviour.** The read is not cached. It resolves the tenant's "User Accounts" module by
definition name and reads ordinary module settings against it, exactly as the legacy did, and returns
a **successful result whose value is `null`** when the tenant has no such module — the legacy returned
`Nothing` in that case and its callers fell back to their own defaults, so reporting a failure would
change behaviour those screens depended upon. As in the legacy, an absence is never cached.

**Why the cache is not reproduced, and it is a correctness reason rather than a performance one.** The
caching abstraction classifies a key by **declared prefix family** in order to scope its invalidation,
and `"UserSettings|"` is not one of the declared families, so `InvalidatePortal` could not evict this
entry. The rows behind it are *ordinary module settings* on the account module instance, writable
through the module service as well as through the settings-write member beside the read, so an entry
only the account service knew how to evict would go stale on a write the account service never saw.
The sibling module service records the same decision, for the same reason, for the analogous
`GetModuleSettings<id>` and `GetTabModuleSettings<id>` keys. What is cached instead is the
profile-definition catalogue, which is installation-time reference data with both a declared family
and a declared eviction member — and which correctly uses the **conventional** `20 ×` multiplier.

**The measurement is recorded so it is not "corrected".** Anyone reintroducing this cache must
reproduce **three minutes** and must not normalise it to `20 × multiplier`, which would multiply the
staleness window twentyfold.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### Defect 1: the legacy credential predicate overwrote its verdict, and the defect is annotated rather than fixed

**Legacy behaviour.** `ValidatePassword` at `UserController.vb:L1067` evaluated three rules, but its
third rule **assigned** its verdict rather than accumulating it. A credential that failed the minimum
length rule and then matched the configured strength expression was therefore reported **valid** — the
earlier failure was overwritten.

**Why nothing in `Library/` changes.** Per AAP §0.9.1 a discovered defect is annotated in place and not
fixed. The legacy tree is a read-only reference input and remains byte-identical.

**Why the defect is dormant, and what the target does.** No strength expression is configured in this
installation, so the third rule never runs and the legacy and target verdicts agree on every input this
configuration can produce. The target evaluates the rules in order and returns on the first refusal,
which is evidently what the legacy code was reaching for. Were a strength expression ever configured,
the target would refuse a short credential that the legacy predicate accepted — recorded here rather
than left to be discovered in production.

**Where enforcement lives.** The legacy predicate was **public**, and therefore a second policy free to
diverge from the one the screens declared; it is not exposed on the target contract at all. Declarative
request validation in `Application/Validation/` owns enforcement. The service retains a **private**
guard that reads the **same bound `PasswordPolicyOptions` instance** those validators read, so there is
one policy with one source: it hardcodes no threshold, adds no rule the options do not declare, and
skips the non-alphanumeric and strength rules unless the configuration asks for them. Its purpose is
that a credential write is never performed merely because a validator happened to be wired at the HTTP
boundary — the service is also reachable from a test, a seeder, or a future host with no such pipeline.
The legacy policy itself is preserved verbatim from `Website/release.config:L242-L245`: minimum length
seven, zero required non-alphanumeric characters, no question-and-answer requirement, and electronic
mail uniqueness not enforced. Tightening a policy during a migration locks out existing account
holders, so any hardening is a separate and explicit decision.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The ambient caller and tenant accessors are gone, and the never-null account sentinel with them

**Legacy behaviour.** `GetCurrentUserInfo` at `UserController.vb:L381` read the acting account out of
per-request state — `HttpContext.Current.Items("UserInfo")`, falling back to
`Thread.CurrentPrincipal.Identity` when there was no request at all — and on every miss returned a
**newly constructed empty account** rather than nothing. The delete at `L237` separately reached
`PortalController.GetCurrentPortalSettings`, purely to label its audit entry; that accessor read
`HttpContext.Current.Items("PortalSettings")`, a mutable per-request composite whose `ActiveTab`
callers could reassign mid-request.

**Target behaviour.** Neither accessor is reproduced. The acting caller arrives through the injected
`ICurrentUser` abstraction and every member takes the identifiers it acts on as explicit arguments,
`portalId` first. No `PortalSettings` type appears anywhere on the service — and could not, because the
layer graph gives the Application project a reference to Domain only.

**The divergence.** The **never-null contract is deliberately not carried forward.** Returning a hollow
account made "nobody is signed in" indistinguishable from "an account with no identifier", so a caller
that forgot to test the identifier silently operated as an anonymous ghost. Absence is now reported as
absence.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The profile-definition surface is definition management, not a read-only lookup

**The ambiguity.** The plan describes this surface two ways: AAP §0.5.1.4 calls the API surface a
*"Lookup surface"*, while AAP §0.5.1.8 calls the client feature *"Definition management"*. The
reconciliation is recorded here so it is not re-litigated downstream.

**The reading, and what settles it.** **Definition management.** `IUserService` declares a create, an
update and a delete for profile property definitions alongside the two reads, so read-only is not an
available interpretation of the contract. The legacy source agrees:
`Website/admin/Users/ProfileDefinitions.ascx.vb` is a management grid with add, edit, reorder and
delete commands and `EditProfileDefinition.ascx.vb` is its editor, so a read-only surface would have
**lost a workflow the legacy application offered** — which UI functional parity forbids. The narrower
phrase is best read as describing how the definitions are *consumed* by the profile screens, which do
only read them.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### Nine further legacy members are omitted, and the omissions are named

**What is not ported, and why.** `DeleteUsers` (`L273`) and `DeleteUnauthorizedUsers` (`L293`), the
bulk sweeps — a reachable "remove every account in this tenant" operation is a defect rather than a
feature, and neither has an endpoint in the target API. `SetAuthCookie` (`L919`), **whose body is empty
in this checkout**, wrote a forms-authentication cookie that has no counterpart under bearer
authentication. `GeneratePassword` (`L314`, `L330`) — a generated credential has to be transmitted to
be useful, which reintroduces exactly the disclosure that dropping retrieval removed.
`GetUserCreateStatus` (`L598`) mapped a status to a message drawn from the excluded localisation
mechanism; the status becomes a stable failure code and the wording is authored in the client.
`GetUserMembership` (`L638`) was a `Sub` that mutated the account it was handed and reported nothing at
all; the membership facts it fetched are materialised on the account by the repository read path.
`GetOnlineUsers` (`L415`) is excluded with the users-online subsystem and `GetSuperUsers` (`L1331`)
with host-level administration. `UpdateDisplayNames` (`L1259`) is a bulk maintenance sweep with no
endpoint. `GetUserCountByPortal` (`L582`) is omitted as **redundant rather than excluded**: the paged
envelope the listing member returns already carries the tenant-wide total, so a second member
answering the same question from a different query would be a second source of truth for one number.
The hydration and provider-synchronisation switches go with them — `isHydrated`, `hydrateRoles`,
`ProgressiveHydration`, `SynchronizeUsers` and `AddToMembershipProvider` — because the shape of a
response is settled by its data transfer object, never by a caller-supplied switch. The caching
*members* (`GetCachedUser` `L350`, `SettingsKey` `L924`, `GetCacheKey` `L1310`, `CacheKey` `L1315`) and
the two stateful controller properties that callers configured before invoking a method
(`DisplayFormat` `L1210`, `PortalId` `L1219`) are gone; the service holds no mutable state of any kind.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The Option Strict asymmetry is resolved towards the stricter side

**Legacy behaviour.** The class library was compiled with Option Strict **on**
(`Library/DotNetNuke.Library.vbproj:L24` — note that `L23` is `OptionExplicit`, so a citation of `L23`
for Option Strict is off by one, and the correction is recorded here). The admin code-behinds this
service absorbs its rules from were compiled with `strict="false"`
(`Website/release.config:L125`) — Option Strict **off** — and could therefore rely on late binding and
implicit narrowing that C# rejects outright.

**Target behaviour.** Every such conversion is made explicit. Text arriving from a stored module
setting is parsed with an invariant culture and an explicit fallback rather than coerced, so a value
the legacy screen would have silently collapsed to `0` or `""` is either parsed or refused. This is
also why the twelve `Website/admin/Users/*.ascx.vb` code-behinds are treated as reference inputs for
endpoint and screen semantics rather than as candidates for line-by-line translation.

**Annotated in code at.** `backend/src/DnnMigration.Application/Services/UserService.cs`.

### The development overlay carries three log levels and nothing else, and the legacy development file's committed validation key is not among them

**Legacy behaviour.** `Website/release.config` and `Website/development.config` are the same
444-line and 442-line file differing in only six places, and exactly two of those six carry
meaning. `Website/development.config:L89` commits validation material identified only by the
non-secret fingerprint
`sha256:64b7990f63e537b28824e35321b5339db436427ff46755f7de36e484169519f4`; the release
setting at `:L90` has fingerprint
`sha256:da4916cea319caa4c6fddecf69a434cdfbceb5ea6fc280922a7eca7b3293afe4`. Both files
reference the same 3DES decryption material, identified only by fingerprint
`sha256:4886c93e723fe42dc604e4cca065708c304f05308a17cc6aa2af9d40204a64ac`
(`development.config:L90-L91`), which combined with `passwordFormat="Encrypted"` and
`enablePasswordRetrieval="true"` (`release.config:L239-L245`) decrypts every stored password. The
development file is therefore the *more* exposed of the two: it commits the signing key as well as
the decryption key. The other four differences are `objectQualifier="dnn_"` against `""`
(`development.config:L352` against `release.config:L354`), `<trust level="Medium" originUrl=".*" />`
active at `development.config:L121` but commented out at `release.config:L122`,
`<compilation debug="true" strict="false">` against `debug="false" strict="false"`
(`development.config:L123`, `release.config:L125`), and two whitespace-only hunks.

**Target behaviour.** `backend/src/DnnMigration.Api/appsettings.Development.json` is eleven lines
and declares one section:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Information",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  }
}
```

No signing key, no connection string, no `Jwt` section of any kind. `ConnectionStrings__Default`
and `Jwt__Secret` arrive from the environment in development exactly as they do in production, and
both are startup-validated, so a developer who supplies neither is told which key is missing rather
than being handed a working default that hides the requirement. The section that is declared exists
in the base file, which keeps the overlay to genuine deltas: everything else - the two empty
secrets, the password policy, the single permitted cross-origin caller, the credential rate limit,
the console sink and its compact-JSON formatter - is inherited unchanged.

**Why each of the six legacy differences produces no key here.** The committed keys are the whole
point of not carrying them forward, and "it is only development" is precisely the reasoning that put
a 3DES key into source control in the first place. `objectQualifier` is answered by Fluent mapping
rather than configuration, for the reasons set out two sections above. `<trust level="Medium">` has
no counterpart at all: code access security was removed from .NET with the move off the .NET
Framework, partial trust is not a concept the modern runtime implements, and a `Trust` or
`TrustLevel` key would describe a sandbox that cannot exist - isolation is now the container
boundary and the unprivileged user the API image runs as. The `strict="false"` half of the
compilation element is not a setting to port but a warning about the *source* it governed: the
thirty-nine admin code-behinds compiled with Option Strict off while
`Library/DotNetNuke.Library.vbproj:L23-L24` compiled the class library with `OptionExplicit` and
`OptionStrict` on, so those code-behinds may legally contain late binding and implicit narrowing
that C# rejects outright, and every such conversion had to be made explicit during translation. The
`debug="true"` half maps to the build configuration and to the developer exception page the
framework installs from the environment name alone; it is deliberately **not** expressed as a
`DetailedErrors` key, because `GlobalExceptionHandler` takes `IProblemDetailsService` and
`ProblemDetailsFactory` and no environment abstraction, so the RFC 7807 response shape is identical
in every environment by construction, and a key nothing reads is worse than no key.

**Why the SQL-command channel stays closed even in development.** At `Information` the
`Microsoft.EntityFrameworkCore.Database.Command` category writes the command text together with its
parameter list, and on the authentication and user-management paths that list is where credentials
and tokens would be. `Warning` is therefore restated in the overlay rather than merely inherited,
so that the level appears in the same block that relaxes the two levels around it - the place a
reviewer looks to check it was not relaxed as well. The consequence is accepted deliberately:
generated SQL does not appear in a development log, and a developer who needs it enables
sensitive-data logging locally rather than lowering a level in a tracked file. Verified by running
the host in development against a live SQL Server: with the shipped value the login path produced
twenty `Database.Connection`, twelve `Query` and one `ChangeTracking` event at `Debug` and **zero**
`Database.Command` events, and with the level lowered to `Information` the same request produced
four `Executed DbCommand` entries carrying `Parameters=[...]` and the full statement. The two
levels that *are* relaxed are bounded: `Default` at `Debug` buys the value-free Entity Framework
Core diagnostics that make a mapping against the legacy schema inspectable, and the
`Microsoft.AspNetCore` override at `Information` restores the framework's per-request entry while
simultaneously holding the whole ASP.NET Core namespace above `Debug`, since a Serilog override
applies to the longest matching prefix. Neither appears in the production overlay, and neither may
be copied into it.

### `TrustServerCertificate=True` belongs to a development connection string only

**Legacy behaviour.** Transport security did not arise. `Website/release.config:L25` and `:L36`
both point at `Data Source=.\SQLExpress;Integrated Security=True;User Instance=True;`
`AttachDBFilename=|DataDirectory|Database.mdf;` - a local file-attached SQL Server Express 2005
instance - and the commented alternative at `:L30` is `Server=(local);Database=DotNetNuke;uid=;pwd=;`.
The .NET Framework 2.0 SQL client did not encrypt by default, so no certificate was validated and
no setting existed to relax.

**Target behaviour.** `Microsoft.Data.SqlClient` 6.1.6, which `DnnMigration.Infrastructure` pins,
defaults `Encrypt` to `True`, so the client validates the server certificate unless told otherwise.
A developer running SQL Server locally with a self-signed certificate may add
`TrustServerCertificate=True` to the `ConnectionStrings__Default` value they export, and that is the
only place it is acceptable. It must never appear in a production connection string, where it
silently disables the validation that makes encryption worth having, and it appears in no tracked
file in this repository: `appsettings.json` declares the connection string as an empty value,
`appsettings.Development.json` declares no connection string at all, and
`appsettings.Production.json` declares none either. Neither `User Instance` nor `AttachDBFilename`
is carried forward in any form; both are SQL Server Express 2005 features with no counterpart in a
containerised deployment.

**Why the development overlay ships no connection string, credential-free or otherwise.** A sample
value in a tracked file is how a real one eventually arrives there, which is the reasoning already
recorded for the base file. A credential-free
`Server=localhost;Database=DotNetNuke;Trusted_Connection=True` template was considered and rejected
for a second, concrete reason: integrated authentication is not how this project's own development
database is reached - that is a SQL Server container addressed over TCP with a password supplied
from an untracked file - so the template would fail on the very machine it was meant to help, and
the only edit that would make it work is the one edit that must never be made. Leaving the key out
means the startup guard in `AddInfrastructure` names both `ConnectionStrings:Default` and
`ConnectionStrings__Default` and stops, which is a better first experience than a driver-level login
failure.

## Observability remediation: the audit vocabulary, the five events nothing can raise, and three values withdrawn from the trail

### The tenant installation records both legacy intents, not the tidier one

`PortalController.vb:L1140-L1141` assigns `objEventLogInfo.LogTypeKey =
...EventLogType.HOST_ALERT.ToString` — twice, byte-identically, which is a legacy defect recorded
elsewhere in these notes — and the record is written at `:L1157`. `HOST_ALERT` is therefore the only
type the legacy installation ever raised.

The first implementation emitted `PORTAL_CREATED` alone, reasoning that the legacy typing was the
coarser of the two members available in the same enumeration and that the accurate one should win.
**That reasoning is replaced rather than softened.** It is a judgement about legacy intent, and
acting on it silently detached every operator search and alert already written against `HOST_ALERT`
— precisely the breakage this vocabulary is preserved verbatim to prevent. It also, in passing,
introduced the false claim into `AuditEventNames.cs` that `HOST_ALERT` belonged to the excluded
host-administration surface, which is how a legacy audit site came to be counted as out of scope
when its one in-scope producer sits in `PortalService.CreatePortalAsync`.

`CreatePortalAsync` now records **both** names for one installation, built from the same facts by
changing only the name, so the two records cannot drift apart or describe different events. Both
share the `PortalInstalled` event identifier, so an alert rule addressing the family by number sees
every installation rather than whichever half of the pair it happened to match.

### Five legacy events have no producer, and their absence is a decision

`TAB_CREATED` (`ManageTabs.ascx.vb:L315`), `TAB_DELETED` (`RecycleBin.ascx.vb:L205`),
`TAB_SENT_TO_RECYCLE_BIN` (`TabController.vb:L840` and `:L952`), `TAB_RESTORED`
(`RecycleBin.ascx.vb:L280`) and `MODULE_RESTORED` (`RecycleBin.ascx.vb:L392`) are legacy audit sites
that this migration cannot raise, because none of the operations they describe exists here to raise
them from. The page surface is *deliberately* narrow — the migration plan specifies `GET
/api/v1/portals/{id}/tabs` and `GET`/`PUT /api/v1/tabs/{id}` and nothing else, so a page can be read
and updated but never created or removed — and the recycle-bin surface the other four belong to is
excluded along with the rest of the Web Forms administration pages.

Publishing a name that nothing can raise would be a placeholder, so none of the five is published in
`AuditEventNames`. Each is named in that file's header instead, with its legacy site, so the absence
reads as a recorded decision rather than as an oversight, and each becomes raisable in the same
change that adds the operation it describes.

### A module removal is audited; an export is no longer audited as a change

`RecycleBin.ascx.vb:L156` audited a module removal as `MODULE_DELETED`. The page is excluded; the
deletion it audited is not, and `ModuleService.DeleteModuleAsync` is where the deletion now happens —
so that boundary, previously the only write in the service with no trail at all, records
`MODULE_DELETED` after its commit. The `Operation` fact distinguishes recycling the whole module from
withdrawing one placement, and the legacy soft-delete-then-purge distinction is not reproduced
because there is one removal operation.

Three further corrections travel with it. The module service no longer declares its own event-name
constant citing `EventMessageProcessor.vb:L69` — an excluded file — as the provenance of a name that
was always a member of the in-scope enumeration; the name comes from `AuditEventNames` like every
other. The caller now names the operation instead of the helper assuming it, because one name for
four operations meant a settings change, a content import, an export and a deletion were all
recorded as `MODULE_UPDATED`. And an **export** is recorded as `MODULE_EXPORTED`, a net-new name with
no legacy counterpart because `Website/admin/Modules/Export.ascx.vb` contains no `AddLog` call at
all: an export changes nothing, so recording it as an update asserted something untrue in a trail
whose entire value is that it is believed. Reading a module's content out of the system is worth
auditing on its own terms, so the operation keeps its record and gets an honest name.

### The import trail withdraws the caller's file description and bounds the document's version

`ModuleImportRequest` documents `Folder` and `FileName` as accepted-and-deliberately-unused parity
metadata, with **no length bound preserved** and no interpretation as a path. Both were nevertheless
copied verbatim into the import audit record, which meant nothing validated them, nothing read them,
and a caller could place a secret, a personal identifier, control text or an unbounded
high-cardinality value into the audit trail simply by naming a file that way. Neither is recorded any
more. The trail loses nothing an operator can act on: the module, the declared version, the payload
size and the placement count describe what actually happened, whereas the caller's own description of
where the document came from describes only what the caller said.

The declared version *is* still recorded, because it is the one provenance fact that identifies the
content — but it is read from an attribute of a caller-supplied document, so it is bounded to
thirty-two characters and allowlisted to digits, dots and hyphens first. A value that fails is
replaced by `(unusable)` rather than dropped, so the record still says the document declared
something and that what it declared was not usable; dropping it would make a hostile document
indistinguishable from one that declared no version at all. It is replaced rather than stripped of
its unwelcome characters, because stripping reshapes a hostile value into something that reads as
authentic provenance.

### Audit facts are properties now, and a lost record is no longer silent

The legacy record held its descriptive facts in one `nvarchar` column as a rendered list, and the
first implementation reproduced that shape — which made every fact unqueryable: an operator could not
ask for `Operation = "Import"`, and any value containing the separator or the assignment character
made the list ambiguous to parse. Each admitted fact is now attached to the event as a first-class
property under an `Audit` prefix, so it is addressable by name. The configured console sink formats
every property as a JSON field, so nothing an operator reads is lost by removing the rendered list.

Admission is bounded, because these facts originate in caller and document input: at most
thirty-two per record, keys must be short plain identifiers or they are withheld rather than
reshaped, values are truncated at two hundred and fifty-six characters with a visible marker,
a value carrying control characters is replaced wholesale, and a key that would collide with a name
the record's own template occupies is withheld — because a logging pipeline resolves that collision
by keeping whichever value arrived first, so the fact would vanish while the record still claimed to
carry it. Every record therefore reports two counts: how many facts were attached, and how many were
withheld.

The sink still never throws — the operation it describes has already been committed, so failing the
request to complain about the note taken of it would be worse than the note being missing — but the
failure is no longer discarded. An audit trail whose losses are invisible is worse than one that is
absent, because it is believed: a gap reads as "the operation did not happen". The loss is reported
on a channel that cannot depend on the one that failed — a diagnostic event source and a monotonic
counter, both from the base class library. Serilog's own self-diagnostic channel would have been the
obvious choice and is unavailable: the persistence project references no logging library at all, by
design, and adding one to report a logging failure would be the wrong trade.

## Observability remediation: the health endpoint answers two questions, bounds each probe, and stops attributing a cancellation to the database

There is no legacy predecessor for any of this. The nearest analogue in the legacy tree is
`Website/KeepAlive.aspx`, which exists to stop an idle worker process being recycled rather than to
report a dependency's condition, so nothing below is a preserved behaviour — all of it is net-new
behaviour being corrected against its own stated contract.

### The `ready` tag was load-bearing in the design and inert in the code

`DatabaseHealthCheck` was authored against an explicit instruction: the anonymous `/health` endpoint
is the **liveness** view and must exclude `ready`-tagged checks so that it stays healthy while no
database is guaranteed to exist, and readiness must be surfaced separately. Both probes were duly
registered with the `ready` tag — and nothing ever read it. A single unpredicated endpoint ran every
registered check, which made the tag decorative and made the delivered behaviour the exact opposite
of the documented one.

That is not a cosmetic gap. `docker/api.Dockerfile` probes `/health` from inside the image, and
`docker/docker-compose.yml` holds the front-end service back with `condition: service_healthy` until
that probe succeeds — while the same compose file declares **no database service at all**. The store
is external by design. A liveness view that opened a database connection therefore reported the
container unhealthy for a reason that has nothing to do with whether it can answer, and held the
front end back behind it, while every individual probe was reporting the truth.

`/health` now selects only registrations that do **not** carry the `ready` tag, and a companion
`/health/ready` selects only those that do. Both are anonymous, both are written by the same writer,
and both therefore emit one document shape rather than two — an orchestrator's readiness probe
carries no credential either, so a readiness path behind authentication would report the instance
permanently unready.

**The container artefacts are deliberately unchanged.** They already probe `/health`, and that is
precisely the alignment: the address depended upon from outside this codebase is the one that no
longer requires a reachable store. Point an orchestrator's readiness probe at `/health/ready`.

The liveness view runs only probes that reach nothing external, and the assertion that guards it is
written as the absence of the named database probe rather than as an empty list — so registering a
further genuine liveness probe later, one that depends on nothing external, does not fail a test that
has no business forbidding it.

### Each probe is bounded, and the bound is derived from the container's own budget

The registration carried no timeout, so a probe blocked on an unreachable store was bounded only by
whatever the client library's own connect timeout happened to be — fifteen seconds by default, against
a container health check that allows five.

The readiness probe now carries a two-second timeout. Two seconds is chosen so the bound holds on the
pessimistic reading as well as the observed one: even when a second probe was registered beside it and
the two ran strictly one after the other, four seconds still landed inside the container's five-second
budget — and that duplicate has since been withdrawn, so one timeout is the whole of the budget spent.
Measured against an unreachable store, the readiness aggregate answered 503 in **2.03 seconds** with
the probe reporting 2.00. Either way an unreachable store now produces a reported 503 instead of a
probe that does not return.

### A cancellation is no longer reported as a database outage

The check caught every exception and answered "Database connectivity is unavailable." That sentence
was also what a caller reported when the health infrastructure's own timeout fired, and when the
request was abandoned by whoever made it — three distinct conditions collapsed into one message
naming the wrong cause, which sends an operator to inspect a database that was never the problem.

`OperationCanceledException` is now rethrown when the supplied token is the cancelled one. This is
deliberate and it is the only correct choice available: the health infrastructure holds the timeout
it imposed, so it is the only party that can tell its own timeout from a caller's abandonment, and it
can only do that if the cancellation reaches it. Every other failure still answers rather than
raising, because a dependency outage must stay readable as a 503 with a report rather than surfacing
as an unhandled fault.

The correction is visible in the answer. Against an unreachable store the readiness document now
reports the database entry as `"A timeout occurred while running check."` — which is the health
infrastructure's own wording, produced only because the cancellation reached it. The same condition
previously read "Database connectivity is unavailable", naming a cause that had not been established.

### The failure's identity is reported; the provider's exception is not

The unhealthy result attached the raw provider exception. Nothing in the delivered response writer
serialises an exception, so this leaked nothing today — but the value was one configuration change
away from being rendered, and a SQL client exception's message routinely carries the server name, the
database name, the login name and the shape of the failing statement. Publishing that on an endpoint
whose entire purpose is to be reachable without a credential is not a risk worth holding open on the
strength of the current writer's behaviour.

No exception is attached now. In its place the result carries a single bounded data entry naming the
failure's **type** — `SqlException`, `InvalidOperationException`, `TimeoutException` — which is the
part an operator actually triages on and which cannot carry a value from the connection string or the
statement. The response writer still emits no data dictionary, so this is available to a diagnostic
sink and to a test without widening the anonymous contract.

## Review remediation: eleven findings, and the six behavioural differences that closing them introduced

**Why this is one entry.** Every difference below was introduced while closing a single code review
that returned eleven findings across the backend closure milestone — one critical, four major, three
minor and three informational. They are recorded together because they were decided together, and
because five of the six are cases where a *delivered* behaviour was measured against its legacy
authority and found to disagree with it. None is a silent absorption, and each one is annotated at the
call site as well as here.

### Removing an account is one transaction, so a partial removal is no longer reachable

**Legacy behaviour.** `UserController.vb:L200` removed an account by issuing its writes one after
another with no transaction around them at all — two permission-table deletes reached through
`ModulePermissionController.vb:L218` and `TabPermissionController.vb:L209`, then the role assignments,
the portal membership, the credential and the account row. A failure part-way through left the
database in whatever state it had reached.

**What it did.** The delivered account service opened no transaction either, and the permission
cleanup it called opened and committed one **of its own**. The two facts combined into a defect
neither had alone: a failure after the grant cleanup returned — most concretely a refusal from the
external credential store — left the grants committed and gone while the account they belonged to
remained. `IUnitOfWork.BeginTransactionAsync` refuses to nest, so the account service could not
simply wrap the sequence; the inner commit had to be removed first.

**Target behaviour.** The permission contract now declares the removal in two halves: a **stage-only**
member that writes nothing and is contractually forbidden from committing, flushing, opening a
transaction or evicting a cache entry, and a separate post-commit member that performs the eviction.
The account service opens exactly one transaction spanning the grant staging, the role assignments,
the portal membership, the credential-store delete and the account row, commits once, and only then
touches the cache and the audit trail. The credential store borrows the same connection and enlists
in that transaction, so the external write is inside the boundary rather than beside it. The
standalone top-level permission-removal operation keeps its own single commit and is unaffected.

**Why it matters.** The behavioural difference is that a failure now leaves the account exactly as it
was. That is a divergence from the legacy, which had no such guarantee, and it is the one the
migration exists to make.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/UserService.cs`,
`backend/src/DnnMigration.Application/Services/PermissionService.cs`,
`backend/src/DnnMigration.Application/Abstractions/IPermissionService.cs`.

### The role subscription engine exists once, in the Application layer, and the store now refuses the legacy absent-date marker

**Legacy behaviour.** `RoleController.vb` computed a subscription's effective and expiry dates from
the role's billing frequency and trial terms, using `Microsoft.VisualBasic.DateAdd` over the stored
`char(1)` codes `D`, `W`, `M` and `Y` (`RoleController.vb:L494-L547`), and decided whether cancelling a
paid subscription expired the assignment or deleted it.

**What it did.** Those rules were implemented **twice** — once in `RoleService`, where they belong, and
once again inside `RoleRepository`, which had been given an `IClock` in order to run them. The two
copies agreed, which is exactly the condition under which duplication is invisible: they would have
continued to agree until one was amended, and then disagreed on precisely the case that prompted the
amendment. AAP Rule T2 places no business logic above the Application layer, so the repository copy
was the one to go.

**Target behaviour.** `RoleRepository` is scoped queries and staging only. It persists whatever final
values it is handed, records the six frequency codes as data it does not interpret, and no longer
takes a clock. `RoleService` owns marker normalisation, the clearing of a past effective date, expiry
derivation and the expire-versus-delete decision, on every write path.

**Why it matters, and what it changed for a caller.** The removed repository copy had also been
quietly translating the legacy absent-date marker — `Date.MinValue`, which SQL Server's `datetime`
cannot hold — into a null. With the duplicate gone, a marker that reaches the store is refused by the
store, loudly, instead of being reinterpreted. That is strictly better and it is asserted rather than
assumed: the integration suite pins the refusal, so the Application-layer translation is demonstrably
necessary rather than merely tidy. A real past expiry still persists verbatim, so back-dated
cancellation remains reachable.

**A superseded instruction.** The per-file specification for `RoleRepository.cs` had *mandated* the
repository-side engine. It is superseded by AAP Rule T2 and by this review; the note is recorded so
that a later reader comparing the two does not restore the duplicate on the strength of the narrower
document.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Repositories/RoleRepository.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IRoleRepository.cs`,
`backend/src/DnnMigration.Domain/Entities/UserRole.cs`,
`backend/src/DnnMigration.Infrastructure/Persistence/Configurations/UserRoleConfiguration.cs`.

### A portal's page tally is the terminal `GetTabCount`, which counts recycled pages and can answer minus one

**Legacy behaviour.** The page figure the portal grid displayed came from `GetTabCount`
(`04.04.00.SqlDataProvider:L511-L527`), reached through `PortalInfo.Pages`. Its terminal form is
`SELECT COUNT(*) - 1 ... WHERE PortalID = @PortalID AND TabID <> @AdminTabId AND (ParentId <>
@AdminTabId OR ParentId IS NULL)`. Three of its properties are counter-intuitive and all three are
load-bearing: it carries **no** deleted-row condition, so a page in the recycle bin is counted; it
excludes only the administration page and its **direct** children, so an administration
*grandchild* is counted; and it subtracts one unconditionally, so a portal with no administration
page recorded answers **minus one**.

**What it did.** Both portal-repository members counted every page that was not deleted, which
disagreed with the procedure on all three points at once — and the batched member was compared in
test only against the equally wrong single member, so the two agreed with each other and with neither
authority.

**Target behaviour.** Both members reproduce the procedure. The exclusion predicate is stated **once**
and shared by the single and batched paths, so they cannot drift apart again, and the minus-one tally
is a named constant documented as arithmetic rather than as the legacy integer sentinel — the two are
easy to confuse here, because `-1` is simultaneously `Null.NullInteger` and a real `PortalID` under
`IDENTITY(-1,1)`.

**Why it matters.** A recycled page occupying a portal's page allowance is what the legacy displayed,
so an operator reading the new grid sees the number they have always seen. The response contract's
former claim that the figure is never negative on the wire was false and is corrected.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/Repositories/PortalRepository.cs`,
`backend/src/DnnMigration.Domain/Abstractions/Repositories/IPortalRepository.cs`,
`backend/src/DnnMigration.Application/Dtos/Portal/PortalListItemDto.cs`.

### Module portability uses the standalone admin format, so no escaping layer sits over the payload

**Legacy behaviour.** Two different legacy paths write a `<content>` document and they do **not** agree.
`Website/admin/Modules/Export.ascx.vb:L157-L164` — the standalone admin screen — concatenated what
the module returned **directly** between the tags, with `type` holding `CleanName(ModuleName)`, and its
counterpart `Import.ascx.vb:L200` handed `DocumentElement.InnerXml` straight back with no decoding
step of any kind. `ModuleController.vb:L244` and `L428` HTML-encoded and later decoded the payload,
because that path was embedding module content inside a **portal template** — a different document
with a different reader.

**What it did.** The delivered service applied the portal-template pair to the standalone endpoints,
and additionally let the XML writer escape the already-escaped text a second time, so `<` left as
`&amp;lt;`. Nothing legacy either produced or consumed that shape: a legacy export file failed to
round-trip through it, and a document it wrote was unreadable by the legacy importer. It also wrote
the **raw** module name into `type` where the legacy wrote the cleaned name, and never checked the
attribute at all.

**Target behaviour.** Four differences, each reproducing the standalone pair.

- The payload is spliced as inner XML on export and read back as the root's inner XML on import, with
  no encode and no decode. An element child arrives as markup, a text child arrives with the three
  XML-significant characters escaped, and a **CDATA section arrives as a CDATA section** rather than
  being resolved to text — which is what `InnerXml` did, and what makes a module's own export
  round-trip byte for byte.
- `type` carries the cleaned module name, using the character set measured from `Export.ascx.vb:L204-L218`
  — the period, the space, and `` ~`!@#$%^&*()-_+={[}]|\:;<,>?/ `` followed by `Chr(34)` and `Chr(39)`,
  the two quotation marks the legacy literal appended by character code.
- The declared type is **enforced**, accepting the cleaned module name or the cleaned friendly name
  exactly as `Import.ascx.vb:L195-L205` accepted either, and refusing anything else. Without it a
  document exported from one module type could be loaded into a module of another, whose portability
  behaviour would then be handed content it cannot interpret. The legacy sourced this refusal from the
  file **name** as well as the attribute; the target has no shared file system, so the name-based half
  has nothing to test and the attribute-based half is what survives. The comparison is widened to
  case-insensitive: every document either exporter writes carries the exact cleaned name, so no correct
  document is affected, while a hand-edited file differing only in case is admitted rather than refused
  with a message an operator cannot act on.
- A payload the module hands back that is **not well-formed XML content** is refused, where the legacy
  exporter concatenated it regardless and wrote a file its own importer then rejected as invalid. The
  portability contract is an XML fragment, so this is a defect in the module; the refusal is reported
  as a server fault, because the request was correct and nothing the caller changes makes it succeed.
  The module's own text is never echoed in the message.

**One preserved legacy loss.** A carriage return inside a payload becomes a line feed. The XML
specification requires a reader to normalise every line ending, inside a CDATA section too, so the
legacy path lost it at exactly the same point. Preserving it would need a character reference that
would make documents this service writes unreadable by the legacy importer, so the behaviour is
annotated and left alone.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/ModuleService.cs`,
`backend/src/DnnMigration.Application/Abstractions/IModuleService.cs`.

### Import provenance on the audit trail is bounded and normalised, never verbatim

**What it did.** The import audit event recorded the document's declared version and the caller's
submitted folder and file names **raw**. `ModuleImportRequest` deliberately carries no validator and
therefore no length bound of any kind — a label the caller merely names is not something the request
should refuse over — so those three values were unbounded caller-controlled text entering a retained
structured log. That is two hazards at once (CWE-532 and CWE-400): a metadata field becomes a channel
for moving text into an operator-readable store, and one bounded request becomes an arbitrarily large
log event.

**Target behaviour.** The bound lives at the **audit boundary**, where the hazard is, and not on the
wire contract, so no request is ever refused for the length of a label nothing resolves. Every fact
that can be server-derived is taken from the server instead of the caller — the package's own name and
installed version, the cleaned content type the import was accepted against, the payload length and
the placement count. The three request-derived values are capped, have their control characters
replaced by single spaces so a submitted newline cannot forge what reads as a separate entry, and are
named so a reader cannot mistake them for facts the server vouched for. Truncation is **announced**
with a trailing marker: a shortened value that read as complete would let an operator draw a
conclusion the record does not support. Whitespace-only input is recorded as absent, because the
legacy absent-string sentinel was itself the empty string. The payload is still never recorded — only
its length.

**Annotated in code at.**
`backend/src/DnnMigration.Application/Services/ModuleService.cs`,
`backend/src/DnnMigration.Application/Dtos/Module/ModuleImportRequest.cs`.

### The readiness probe propagates the caller's own cancellation instead of calling it an outage

**What it did.** The database probe caught every exception and answered `Unhealthy`, cancellation
included. Answering "unhealthy" to a probe that was **abandoned** states a fact about the database
that no attempt established: the attempt did not fail, it stopped being wanted. Both the container
`HEALTHCHECK` and the compose dependency read this signal, so a probe deadline or a host shutdown
could be recorded by whatever was watching as a database failure — and the cancellation contract was
broken, because the infrastructure that supplies the token distinguishes "we stopped asking" from "we
asked and the answer was no" by whether cancellation surfaces.

**Target behaviour.** An exception filter rethrows when the supplied token has been cancelled, ahead
of the verdict. The filter reads nothing but the token, which is the whole of the discrimination: a
driver-level connect timeout is cancellation-**shaped** yet leaves the token unsignalled, so it is
still an unreachable instance and still reported as unhealthy. Cancellation is now the only way this
method faults, and that is deliberate — an escaping fault of any other kind would break the endpoint
and take the frontend that waits on it down with it.

**Annotated in code at.**
`backend/src/DnnMigration.Infrastructure/HealthChecks/DatabaseHealthCheck.cs`.
## Review remediation: two accessibility corrections in the design vocabulary

Both entries below record a deliberate divergence from the values this project's own design plan
specifies, and both were measured in a real browser before and after the change rather than argued
from the token table. The plan's stated precedence is design-system compliance first, visual
continuity with the legacy portal second, accessibility third — and neither change disturbs that
order, because in both cases the plan's own accessibility requirement is what is being satisfied and
the token vocabulary is where the plan says a remedy belongs.

### An eleventh colour token, because danger TEXT and a danger BORDER are not one role

**Legacy behaviour.** The measured legacy error colour is pure red. It occurs five times in
`Website/Portals/_default/default.css`, and the legacy stylesheet's own comment describes the class
that carries it as the text style used for error messages, so the legacy application really did
render error text in it. The design plan's colour table maps that value to a single token and
characterises it as "validation and error text".

**Target behaviour.** The value is unchanged and the token keeping it is unchanged; what changes is
that it no longer carries text. Pure red measures **4.00:1** on the page background. That clears the
3:1 threshold that governs a non-text UI component — a border, a rule, an indicator — and misses the
4.5:1 minimum that governs normal text, and bold weight is no mitigation below 18.66px. The three
places that rendered danger text in it each carried a standing accessibility flag stating, verbatim,
that the remedy belonged in the token rather than at the point of use. The token layer now supplies
it:

- `--color-danger` keeps the measured legacy red and is reserved for borders and indicators, where
  4.00:1 is sufficient. Its five remaining consumers are all boundaries.
- `--color-danger-text` is a new eleventh member of a vocabulary the token file declares closed. It
  is the same hue at a lower lightness, and it is admitted on accessibility grounds rather than on
  legacy-fidelity grounds — the only such admission, and the token file's closed-count annotation
  records that the ground is deliberately not extensible.

Measured in Chrome with the effective painted backdrop resolved by walking the ancestor chain rather
than reading the element's own transparent background: **7.20:1** on the page background, **6.21:1**
on the neutral surface, **6.86:1** on the informational hint surface and **4.70:1** on the selected
tint. All four clear 4.5:1, so the token is safe on any surface the vocabulary can put behind danger
text rather than only on white. A darker-but-less-dark candidate was measured first and rejected: it
reads 4.23:1 on the selected tint, which would have relocated the defect onto selected rows instead
of removing it.

**What a reader sees.** The same red family in the same places. The banner keeps a pure-red border
around darker-red text — measured as exactly 1px of pure red with the darker ink inside it — and the
required marker and validation message read as red at a legible weight. The compensating non-colour
cues are retained rather than retired: the banner still names its severity in words, and the required
marker still carries its own visually hidden text.

**One consequence for the test suite.** A component spec asserted that the danger band's text colour
WAS the legacy red. That assertion encoded the defect rather than the contract, so it now pins the
split: the border is the legacy red and the text is the compliant token. Two neighbouring specs
justified their non-colour cue by citing the old contrast figure; the cue is still required and the
justification now rests on colour-independence, which does not depend on any ratio.

### A two-tone focus ring, because the ring colour is also a painted surface

**Legacy behaviour.** None to preserve. The legacy stylesheets declare no focus rule of any kind, so
focus was whatever the 2005-era browser drew by default, and the target's focus indicator is a
net-new addition rather than a translation.

**Target behaviour.** The shared focus-ring mixin emitted a single-toned outline resolving to the
brand colour. The brand colour is also a painted surface in this system — the shell fills the skip
link with it — so any focusable element on a brand-filled surface painted brand-on-brand. The record
grid carried a standing flag for exactly that collision, measured at **1.00:1** with a pixel census of
the ring annulus finding zero ring-distinct pixels, and it established by exhaustive search that **no
single flat ring colour can resolve it**: clearing the 3:1 non-text floor on a brand band needs a
luminance at or above roughly 0.20 while clearing it on the selected tint needs one at or below
roughly 0.18, an empty window whose best achievable worst-case is about 2.87:1. Setting the ring to
the page background moves the failure rather than removing it, reading 1:1 against every unselected
row.

The mixin now emits an outline PLUS an outset shadow of a new `--focus-ring-contrast-color`, which
resolves to the page background. Every geometric term is composed from the existing focus tokens —
the spread is offset plus two ring widths — so retuning the width or the offset keeps all three bands
consistent. Because a shadow is painted beneath the element's background while an outline is painted
above it, the result is three concentric bands: contrast, ring, contrast.

Measured in Chrome by scanning rendered pixels outward from the control edge on three axes, on a
control sitting on the brand surface: white 2px, navy 2px, white 2px, then the brand surface. The
indicator reads **12.61:1** against its own band and the band reads 12.61:1 against the brand
surface, where the outline alone would have been 1.00:1. On the light surfaces that make up almost
the whole application the bands are indistinguishable from the backdrop they replace — the inner band
occupies the gap the offset already left transparent and the outer band lands on a surface it matches
to within 1.13:1 — so there is **no visible change** anywhere the ring already worked. Confirmed on
the running application across every focusable element on two routes, with the layout unchanged
before and after focus across 56 measured rectangle fields, and confirmed still gated to keyboard
focus: a real mouse click yields no ring at all.

**In forced-colours mode** the user agent drops the shadow and keeps the outline, which is the
correct degradation — the backdrop is forced there too, so the collision the second tone answers
cannot arise. The shadow is additive and the outline remains the load-bearing indicator.

**The one suppression, and a defect it exposed.** A record-grid row turns its ring inward, because
rows sit in a region that scrolls and a region that scrolls in one axis clips in both, so an outward
band would be cut off exactly when it matters and would paint over the neighbouring rows besides. The
row rule had always been authored as a nested `&:focus-visible`, which compiles to a two-token
selector — and browser measurement showed it had **never applied**: the base focus rule's
`[tabindex]:not([tabindex='-1']):focus-visible` arm and the global table convention's
`tbody > tr[aria-selected]:focus-visible` both out-specify it, and every row matches all three. The
inward offset was therefore silently inert long before a second tone existed, and adding one would
have painted a band that the container clipped away on the left and right edges of every row and on
the bottom edge of the last. The correction is now a compound top-level rule naming the clipping
ancestor, which out-specifies both aggressors on the type-selector count and therefore wins from any
position in the sheet — proven by re-running the cascade with the correction placed first and every
competitor after it. Measured after the fix: a focused row changes **7548 pixels inside its own
border box and exactly zero outside it**, the inward band is 100% continuous along the top and
99.68% along the bottom with the shortfall being antialiasing of the container's own corner radius,
and the three controls that must keep the outward ring still have it.


## Review remediation: the profile family's two request bodies, and the affordance one of them made impossible

**Targets:** `frontend/src/app/core/models/profile.model.ts`,
`frontend/src/app/features/user/user-profile/user-profile.component.ts`,
`frontend/src/app/features/user/profile-definition-list/profile-definition-list.component.{ts,html}`

A wire-seam review found the client's profile write shapes disagreeing with the request
contracts the API binds, in two ways that no compiler on either side can see. Both are
recorded here because closing the second one removed a control an operator could see.

### The profile replace payload names its collection `properties`, not `values`

`PUT /api/v1/users/{userId}/profile` binds `UserProfileDto` — the same type the matching
read returns, because a replace and a read of the whole profile are one contract on the
server rather than two. That type declares `UserId` and `Properties`. The client declared
`userId` and `values`, and the profile editor assembled `values` to match it.

That is not a tolerated near-miss. The API's request deserialiser is configured to
**refuse** an undeclared member rather than to discard it, so the payload would have been
answered `400` naming `values` while the required `properties` was absent — every profile
save on the screen, not an edge case. The member is renamed on the contract and at the one
site that builds it. The editor's own grouping still calls its local member `values`,
which is correct: that is a view model of the component's and never crosses the wire.

### One client shape cannot serve both declaration verbs

The client declared a single `ProfilePropertyDefinitionSubmission` used for both the
create and the replace of a profile declaration. The API binds two distinct contracts
there, and the reason is in the terminal procedures rather than in a style preference:
`AddPropertyDefinition` (`04.06.00.SqlDataProvider:L1101`) declares `@ModuleDefId` and
`UpdatePropertyDefinition` (`04.05.00.SqlDataProvider:L1685`) neither declares it nor
names the column in its `SET` list. A module association can therefore be established
when a declaration is made and never afterwards — which is already recorded above, where
the two server contracts were split for exactly this reason.

The single shared shape is replaced by three declarations mirroring the server's own
split: the nine members both verbs carry, a create shape adding the module association,
and a replace shape that is the shared set exactly. The consequence worth stating is the
one a naive fix gets wrong: adding `moduleDefId` to a *shared* shape would have made every
replace fail, because the replace contract does not declare it. The create arm of the
editor adds the member; nothing else does, and it sends `null` because the screen offers
no module picker — as the legacy editor did not either. `0` would have been wrong in any
case, since `dbo.ModuleDefinitions.ModuleDefID` is `IDENTITY(1, 1)`.

### A visibility control was removed from the declaration editor

This is the behavioural difference, and it is a **reduction in a visible affordance**, so
it is stated plainly rather than folded into the paragraph above.

The declaration editor offered a "Visible to" dropdown bound to a `visibility` member of
the shared submission. No write contract declares that member, for the reason already
recorded above: `ProfilePropertyDefinition` has no visibility column at any point in the
88-script chain, the stored per-account counterpart lives on `UserProfile`, and the value
a declaration *reports* is a default hint the API derives from the "User Accounts" module
setting `Profile_DefaultVisibility`. Because the deserialiser refuses an undeclared
member, the control did not merely have its value ignored — it made **every** create and
every edit from that screen fail with `400`.

The control is therefore removed rather than silently emptied, because a control whose
value is collected and then discarded is worse than no control: it tells an operator a
choice was recorded when nothing was. Two facts make the removal parity rather than loss.
The legacy editor, `Website/admin/Users/EditProfileDefinition.ascx`, offered no visibility
control at all — its steps are an introduction, a list step and a localisation step, and
the only dropdown in the markup selects a locale. And the value remains **visible**: the
grid's "Visible to" column still renders each declaration's resolved hint, which is the
read a maintainer of the catalogue actually needs.

Setting the per-tenant default remains a module-setting decision, exactly where the API
derives it from. Nothing about this change alters what any account's own profile answer
can be set to; per-answer visibility is a member of the profile write and is untouched.

### What now prevents both defects from returning

Neither defect is visible to a compiler, so both are pinned by key rather than by type.
The profile editor's suite asserts the submitted object's key set is exactly
`['properties', 'userId']`; the declaration editor's suite asserts the create payload's
ten keys and the replace payload's nine, asserts `moduleDefId` is absent from the replace
shape and `visibility` from both, and asserts the editor renders no visibility control.


## Review remediation: the client's failure-code vocabularies were spelled in a language the API never speaks

**Target:** `frontend/src/app/core/utils/form-errors.util.ts`
**Legacy sources:** `Library/Components/Users/UserController.vb` (`GetUserCreateStatus`, L598),
`Library/Components/Users/Membership/PasswordUpdateStatus.vb`,
`Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb` L168-L185,
`Website/App_GlobalResources/SharedResources.resx`

The error-presentation module carries four vocabularies mapping a failure outcome to the
wording the legacy application showed for it. Every one of them was keyed on the **legacy
enumeration member name** — `DuplicateRole`, `EnterCode`, `PasswordMissing`,
`NotValidXml`, `Portal.LastPortal` and about twenty-five more.

The API publishes a failure code in exactly one place, and it is not a member of its own:
`GlobalExceptionHandler.BuildProblemType` writes it into the problem document's `type` as
`urn:dnnmigration:error:<code>`, lower-cased with a hyphen folded onto an underscore.
There is no `code` extension member, so `type` is the whole channel. The consequence was
absolute rather than partial: **not one of the declared values could ever match a value
taken off the wire**, so every lookup returned null, and the module had no parser for the
namespace either. Five of the declared values named nothing that exists in the API at all.

### The wording is preserved; the key becomes what the server sends

The migration has to preserve the *wording*, and it is preserved verbatim, Title Case and
legacy punctuation included. What changes is the key. Each entry now names the legacy
member it descends from in a comment, so the parity claim stays checkable, and a new
`failureCode(problem)` reads the code out of the document — returning null for an absent
`type`, for the specification URL the framework writes when it maps a status without
reaching an action, and for a prefix with nothing after it.

| Vocabulary | Was | Now |
| --- | --- | --- |
| Verification ladder | `EnterCode`, `InvalidCode`, `UserNotAuthorized` | `auth.verification_required`, `auth.verification_code_invalid`, `auth.account_not_approved` |
| Password change | eight legacy member names | five `user.password.*` codes |
| Account creation | eighteen legacy member names | ten `user.create.*` codes |
| State refusals | eleven legacy resource keys | nine codes across `portal.*`, `role.*`, `role_group.*`, `role_assignment.*`, `tab.*` and `module.*` |

The two arrays that existed only to restate a legacy enumeration are removed, following
the precedent this module already set when it removed the legacy login-status array: an
exported vocabulary with no reader is a second definition of something that already had
one, and the two spellings drift apart with nothing to detect it. The legacy ordinals
those arrays recorded are still pinned — the creation enumeration by its own model's
specification, the password enumeration in the commentary that cites its source lines.

### Nine legacy outcomes have no code, and each absence is a decision

- **`Success` (password) and `AddUser`/`Success` (creation)** are not failures. No code is
  emitted for a write that worked.
- **`InvalidPasswordAnswer`, `InvalidPasswordQuestion`, `InvalidAnswer`, `InvalidQuestion`**
  have no counterpart because the password question-and-answer requirement is not carried
  forward. The legacy store held credentials reversibly so a password could be recovered by
  answering a question; the target hashes them one way, so there is no question to be wrong
  about. Their wording is reproduced under no key rather than parked under one nothing can
  select.
- **`UserRejected`** described an approval state, which the target reports at sign-in as
  `auth.account_not_approved` rather than on the creation path.
- **`DuplicateAlias`** ("The Portal Alias already exists.") collapses onto the same
  `portal.alias_duplicate` code as `DuplicatePortalAlias`, because the server reports one
  code for the collision however it was reached. The more actionable of the two legacy
  wordings survives — it names the field and says what to do — and the terser variant is
  not kept under a second key.
- **`TabExists`** ("The Page Name you chose is already being used…") has no code because no
  request can elicit it. The legacy guard sat behind `If String.IsNullOrEmpty(strAction)`
  at `ManageTabs.ascx.vb:L279` while the edit branch was entered at `:L304` under
  `If strAction = "edit"`, so it belonged to the create path — and this API deliberately
  exposes no page create. The page service declares no conflict reason code and the update
  action declares no `409`, so wording it would describe a refusal that cannot happen. The
  reserved-device-name refusal, which *is* reachable on the update verb, keeps its wording
  under `tab.name_reserved`.

### Four legacy members become one code, without losing anything

`ProviderError`, `UnexpectedError`, `DuplicateProviderUserKey` and `InvalidProviderUserKey`
all become `user.create.provider-error`. That is not a fidelity loss: `GetUserCreateStatus`
already collapsed all four into a single `Case` arm resolving to one message, so they were
indistinguishable to a person before this migration too. `AddUserToPortal` becomes
`user.create.portal-assignment-failed` and takes the same registration-fault wording — the
legacy value reached the throwing `Case Else` and so had no wording of its own, and
authoring a new sentence would introduce text no operator has seen.

### The vocabulary is no longer one status

"Conflict" is retained as the historical name of the fourth vocabulary, but the set is
explicitly documented as spanning three statuses, because the server derives a status from
a reason token: the duplicate and last-remaining codes arrive as `409`, the
protected-assignment code as `403` (its token is `protected`), and the page-name and
module-content codes as `400`. What the nine share is a shape — a refusal the person can
act on — not a status code.

### What now prevents the defect from returning

One assertion would have caught the whole class: every key of every table must match
`/^[a-z0-9_]+(\.[a-z0-9_]+)+/` and must round-trip through `failureCode` from a document
spelled as the server writes it. Both are asserted over all twenty-seven codes at once.
Each vocabulary additionally asserts that every legacy member name it descends from
resolves to **null**, so a reversion to the legacy spelling fails loudly rather than
silently returning nothing. Beyond the suite, the twenty-seven codes were checked
mechanically against every dotted string literal in `backend/src`: all twenty-seven are
emitted, and none is speculative.


## Review remediation: the route surface was correct and its justification was not

**Target:** `frontend/src/app/core/config/api-endpoints.ts`

A wire-seam review raised the resource-shape question as needing human adjudication:
the client's route module asserted that the flat resource families were what the frozen
action plan authorises, and the reviewer's record of the plan said tenant scoping was
nested. Both sides of the wire agreed 39 endpoints to 39, so no build, test or request
could ever have settled it — agreement between two sides is not evidence of conformance
to a specification, which is exactly why the reviewer declined to close it.

**Adjudicated against the specification, and the code was already right.** The action
plan's API-layer file mapping (§0.5.1.4) names each family explicitly, and it names them
flat: "Read-only lookup surface at `/api/v1/module-definitions`", "Lookup surface at
`/api/v1/profile-definitions`", "Read-only `/api/v1/permissions` catalogue", "Role
membership assignment becomes `POST`/`DELETE` on `/api/v1/roles/{id}/users`",
"Attribute-routed to `/api/v1/portals`", "`POST /api/v1/modules/{id}/export` and
`POST /api/v1/modules/import`", and "`POST /api/v1/auth/login`, `refresh`, `logout` and
`GET /api/v1/auth/me`". It names exactly two operations as children of a portal —
"Nested under `/api/v1/portals/{id}/aliases`" and "`GET /api/v1/portals/{id}/tabs` and
`GET`/`PUT /api/v1/tabs/{id}` only". **No route changed**, because changing one would
have moved the code away from the frozen surface rather than towards it.

**What did change is the justification.** The claim was asserted rather than sourced, so
a future reader had no way to check it without re-doing the adjudication. Each family now
carries the plan's own wording beside it, in a table, with the section cited. The note
also records that both directions of "tidying" are wrong: nesting a flat family invents a
second public identity for one operation, and flattening a nested one addresses a resource
that has no standalone identity.

**One genuine documentation defect was found while adjudicating.** The note said the
exceptions were "portal aliases and page listings" — two — while the implementation nests
**three**: aliases, the page listing, and the settings projection. Enumerating the
controllers mechanically confirmed the count. Settings is nested because the projection is
a representation of the portal itself rather than a separate resource, which is why it sits
on the portal controller and carries no family of its own. The corrected note names all
three and gives each its reason, and additionally records that the page family is
deliberately split across both shapes — the listing belongs to a portal, an individual page
is addressed on its own — which the plan's own sentence fixes and which a reader would
otherwise be likely to read as an inconsistency.

The 39-to-39 correspondence was re-verified mechanically after the edit, by resolving every
`apiUrl` template in the module against every `[Http*]` route on the eleven controllers:
zero client templates without a controller route, and zero controller routes without a
client template.


## Review remediation: three contract declarations that described themselves inaccurately

**Targets:** `frontend/src/app/core/models/paged-result.model.ts`,
`frontend/src/app/core/models/role.model.ts`,
`frontend/src/app/core/models/permission.model.ts`

Three low-severity findings from the wire-seam review, grouped because they share a
failure mode: each declaration said something about the wire that was not true, and in
none of the three could a compiler have noticed.

### The payload-free envelope's metadata member described itself two ways at once

`EmptyApiResponse` carried an interface-level paragraph saying its metadata member was
"left OPTIONAL", a member-level paragraph saying "Present and nullable", and a
declaration reading `meta: ApiMeta | null`. Two of the three agreed; the interface-level
paragraph was the stale one, left behind when the declaration was corrected.

It is now rewritten to say present-and-nullable, and — more usefully — to say WHY the
absence of a producer is not a reason to declare it differently from its
payload-bearing twin. The serializer policy is a property of the server rather than of
any endpoint, so if this arity is ever produced it will carry the member with the value
`null` rather than omit it; and declaring the two arities of one contract differently
would let them disagree about the member they share, so a consumer narrowing this one
with `=== undefined` would take the wrong branch the moment a producer appeared. That
is the precise defect already corrected on the payload-bearing form, and repeating it
here on the strength of there being nothing to measure yet would have reintroduced it
in the one place nobody would look.

### The role classification claimed to travel by name, and nothing made it do so

`RoleStatus` is a three-member string union, and the note justifying that said the API
"states that the value travels by name". The sub-claim it rested on is true — the Domain
enumeration really does declare its members with no explicit numeric values — but the
inference is not. Declaring an enumeration without numeric values does not make it
serialise by name: the platform's default is the integer, and this API registers no
blanket string-enum converter. It registers exactly **two** per-type converters, for the
two values that genuinely cross the wire — the billing frequency, pinned to its
one-character code, and the permission key, pinned to its member name. Published today,
without a third converter, the classification would arrive as `0`, `1` or `2`.

Nothing is broken, because nothing publishes it: the role listing documents the
classification as absent by design, the enumeration is consumed only server-side by the
portal-administration evaluator, and no contract member carries the type. So the union
stays as the client's own vocabulary for a classification it derives, and the note now
records the obligation rather than an untrue claim — whoever publishes the value must
register a per-type string converter at the same time, exactly as was done for the other
two, or the names will not be what arrives.

### The permission key was narrowed on the client while the producer stayed open

The permission projection's key member was typed as the closed four-member union
`'VIEW' | 'EDIT' | 'READ' | 'WRITE'`, with no runtime guard. The producer is open: the
server declares the member as a plain string and the catalogue listing publishes bare
strings, because the column stores whatever key was seeded and an installation may hold
one this codebase has never seen — a third-party module's own key, for instance.

The member is therefore widened to `string`, which is the same decision already taken
and documented for its sibling scope-code member on the same type, for the same reason:
narrowing it would statically type an unrecognised key as a recognised one, and a check
against the four would look exhaustive to both a reader and the compiler while failing
at run time. That is strict typing producing exactly the unsafety it exists to prevent.

Constraining the PRODUCER to the Domain enumeration was considered and rejected: the
stored column is genuinely wider than the enumeration, so the effect would be to make a
legitimate installation-specific key unreadable rather than merely untyped.

The union is kept and re-documented as what it actually is — the client's vocabulary for
a value the application decides, such as the key a screen requires or a route guard
tests — and the supported route from the wire to it is named: `normaliseRequiredKeys` in
the permission directive, which takes an unknown value and returns only recognised keys,
discarding the rest. Comparing the member directly against a key literal still needs no
guard, because both sides are strings; what needs the guard is treating the value AS the
narrower type. No runtime code was added to the model file, which is type-only by
construction.

### One finding closed by verification rather than by a change

The review also observed that the payload-free `ApiResponse` arity is referenced by no
production code, and recorded that no action was required. That was confirmed rather than
taken on trust: the type has no production reference outside its own file, its absence is
already documented at length on the type and in the result translator, and it is pinned by
a contract test asserting that the schema appears nowhere in the generated OpenAPI
document and that no `204` carries a body. The client's mirroring declaration is
documented as producerless in the same terms. Both sides are consistent and the
declaration is retained as documentation of an arity the server declares, so no code
changed.
