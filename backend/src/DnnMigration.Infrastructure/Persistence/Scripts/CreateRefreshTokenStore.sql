/*
    Operator-run additive schema for durable refresh-token state.

    This script creates target-owned objects in the DnnMigration schema only. It does not alter,
    drop, rename or otherwise take ownership of any legacy DotNetNuke table, view, procedure,
    constraint or column. The application never executes this script automatically.
*/

IF SCHEMA_ID(N'DnnMigration') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [DnnMigration] AUTHORIZATION [dbo];');
END
GO

IF OBJECT_ID(N'[DnnMigration].[RefreshTokens]', N'U') IS NULL
BEGIN
    CREATE TABLE [DnnMigration].[RefreshTokens]
    (
        [TokenDigest] binary(32) NOT NULL,
        [FamilyId] uniqueidentifier NOT NULL,
        [Generation] int NOT NULL,
        [UserId] int NOT NULL,
        [PortalId] int NOT NULL,
        [CreatedAtUtc] datetime2(7) NOT NULL,
        [ExpiresAtUtc] datetime2(7) NOT NULL,
        [FamilyExpiresAtUtc] datetime2(7) NOT NULL,
        [ConsumedAtUtc] datetime2(7) NULL,
        [ConsumedClientDigest] binary(32) NULL,
        [RevokedAtUtc] datetime2(7) NULL,
        CONSTRAINT [PK_DnnMigration_RefreshTokens]
            PRIMARY KEY CLUSTERED ([TokenDigest]),
        CONSTRAINT [UQ_DnnMigration_RefreshTokens_FamilyGeneration]
            UNIQUE NONCLUSTERED ([FamilyId], [Generation]),
        CONSTRAINT [CK_DnnMigration_RefreshTokens_Generation]
            CHECK ([Generation] >= 0),
        CONSTRAINT [CK_DnnMigration_RefreshTokens_ExpiryOrder]
            CHECK ([CreatedAtUtc] <= [ExpiresAtUtc]
                AND [ExpiresAtUtc] <= [FamilyExpiresAtUtc]),
        CONSTRAINT [CK_DnnMigration_RefreshTokens_ConsumedClient]
            CHECK (([ConsumedAtUtc] IS NULL AND [ConsumedClientDigest] IS NULL)
                OR ([ConsumedAtUtc] IS NOT NULL AND [ConsumedClientDigest] IS NOT NULL))
    );

    CREATE NONCLUSTERED INDEX [IX_DnnMigration_RefreshTokens_Family]
        ON [DnnMigration].[RefreshTokens] ([FamilyId], [Generation]);

    CREATE NONCLUSTERED INDEX [IX_DnnMigration_RefreshTokens_User]
        ON [DnnMigration].[RefreshTokens] ([UserId], [FamilyExpiresAtUtc]);
END
GO