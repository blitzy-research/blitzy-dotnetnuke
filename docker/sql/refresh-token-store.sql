/*
    DnnMigration - shared refresh-token store provisioning
    =====================================================

    Creates the one table the durable refresh-token store reads and writes, in a catalogue provisioned for
    session state. Run it ONCE per deployment, before starting the API with
    RefreshTokenStore:Provider set to 'SqlServer'.

    WHY THIS FILE EXISTS. The store used to create this table itself, on first use, under whatever identity
    the API runs as. That is schema authorship performed at runtime by the process that serves requests:
    AAP rule T4 makes the existing schema immutable and schema changes a deployment step, and a rule that
    said "except in the store's own catalogue" would be no rule at all - a login that may create one table
    may create any table it is permitted to. It also forced the API's principal to hold DDL rights it needs
    for nothing else, so a compromise of the API inherited the ability to reshape storage rather than only
    to read and write rows. The API now probes for this table and refuses session operations when it is
    absent; it never creates it.

    WHERE TO RUN IT. Against the catalogue named by RefreshTokenStore:ConnectionString, which must NOT be
    the DotNetNuke database and must not be a system database - the host refuses to start otherwise, and
    refuses a connection string that names no catalogue at all. Create the catalogue first, for example:

        CREATE DATABASE [DnnMigrationSessions];
        GO

    WHO SHOULD RUN IT, AND WHO SHOULD NOT. Run this as a principal that may create tables. Do NOT give
    that principal to the API. The API needs SELECT, INSERT, UPDATE and DELETE on this one table and
    nothing else:

        CREATE USER [dnnmigration_api] FOR LOGIN [dnnmigration_api];
        GRANT SELECT, INSERT, UPDATE, DELETE ON [dbo].[DnnMigrationRefreshTokens] TO [dnnmigration_api];

    IDEMPOTENT. Every object is guarded, so re-running this script against a provisioned catalogue changes
    nothing and reports success. It never drops anything: a DROP here would destroy live sessions.

    IF YOU RENAME THE TABLE OR SCHEMA. RefreshTokenStore:Schema and RefreshTokenStore:TableName must match
    what this script creates. Both are validated as plain identifiers, and the store composes the qualified
    name from them.

    THE SHAPE IS NOT ARBITRARY. Only DIGESTS are stored - a SHA-256 of the refresh token and a SHA-256 of
    the client binding that spent it - so a reader of this table cannot mint a session. The three indexes
    serve the three access paths the store actually has: revoking every family of one account, revoking one
    family, and reclaiming families whose absolute ceiling has passed.
*/

IF OBJECT_ID(N'[dbo].[DnnMigrationRefreshTokens]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[DnnMigrationRefreshTokens]
    (
        -- SHA-256 of the refresh token. The clustered key, because every read of one token is by digest.
        [TokenDigest]           varbinary(32)    NOT NULL,

        -- Shared by every generation of one session, and the unit both revocation and eviction act on.
        [FamilyId]              uniqueidentifier NOT NULL,

        -- How many rotations preceded this generation.
        [Generation]            int              NOT NULL,

        -- The account and tenant the session belongs to. No name, no role, no permission: authorisation is
        -- re-read from authoritative storage on every exchange rather than carried here.
        [UserId]                int              NOT NULL,
        [PortalId]              int              NOT NULL,

        -- When this generation stops being usable, and when the whole family does. The second is absolute:
        -- rotation slides the first and can never move the second.
        [ExpiresAtUtc]          datetime2(3)     NOT NULL,
        [FamilyExpiresAtUtc]    datetime2(3)     NOT NULL,

        -- Set when the generation is exchanged. The row is RETAINED afterwards, because a spent digest is
        -- what makes a replay recognisable; the client digest beside it distinguishes a same-client retry
        -- inside the configured grace from a presentation arriving from somewhere else.
        [ConsumedAtUtc]         datetime2(3)     NULL,
        [ConsumedClientDigest]  varbinary(32)    NULL,

        -- Set when the family is retired, by sign-out, by a credential change or by replay detection.
        [RevokedAtUtc]          datetime2(3)     NULL,

        CONSTRAINT [PK_DnnMigrationRefreshTokens] PRIMARY KEY CLUSTERED ([TokenDigest])
    );
END;
GO

-- Revoking every family an account holds, which a credential change, a deletion and an approval withdrawal
-- all perform.
IF NOT EXISTS
(
    SELECT 1
      FROM sys.indexes
     WHERE [name] = N'IX_DnnMigrationRefreshTokens_UserId'
       AND [object_id] = OBJECT_ID(N'[dbo].[DnnMigrationRefreshTokens]', N'U')
)
BEGIN
    CREATE INDEX [IX_DnnMigrationRefreshTokens_UserId]
        ON [dbo].[DnnMigrationRefreshTokens] ([UserId]);
END;
GO

-- Revoking one family, and the family grouping the capacity policy orders by.
IF NOT EXISTS
(
    SELECT 1
      FROM sys.indexes
     WHERE [name] = N'IX_DnnMigrationRefreshTokens_FamilyId'
       AND [object_id] = OBJECT_ID(N'[dbo].[DnnMigrationRefreshTokens]', N'U')
)
BEGIN
    CREATE INDEX [IX_DnnMigrationRefreshTokens_FamilyId]
        ON [dbo].[DnnMigrationRefreshTokens] ([FamilyId]);
END;
GO

-- Reclaiming families whose absolute ceiling has passed, which every issue and every rotation performs.
IF NOT EXISTS
(
    SELECT 1
      FROM sys.indexes
     WHERE [name] = N'IX_DnnMigrationRefreshTokens_FamilyExpiresAtUtc'
       AND [object_id] = OBJECT_ID(N'[dbo].[DnnMigrationRefreshTokens]', N'U')
)
BEGIN
    CREATE INDEX [IX_DnnMigrationRefreshTokens_FamilyExpiresAtUtc]
        ON [dbo].[DnnMigrationRefreshTokens] ([FamilyExpiresAtUtc]);
END;
GO
