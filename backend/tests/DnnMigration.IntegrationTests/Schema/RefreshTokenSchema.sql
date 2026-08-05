/*
    Target-owned durable refresh-token store used by integration tests.

    The production application expects the same additive objects to be provisioned explicitly by
    backend/src/DnnMigration.Infrastructure/Persistence/Scripts/CreateRefreshTokenStore.sql. No
    application startup path creates or migrates these objects.
*/

IF SCHEMA_ID(N'DnnMigration') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [DnnMigration] AUTHORIZATION [dbo];');
END
GO

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
GO

CREATE NONCLUSTERED INDEX [IX_DnnMigration_RefreshTokens_Family]
    ON [DnnMigration].[RefreshTokens] ([FamilyId], [Generation]);
GO

CREATE NONCLUSTERED INDEX [IX_DnnMigration_RefreshTokens_User]
    ON [DnnMigration].[RefreshTokens] ([UserId], [FamilyExpiresAtUtc]);
GO