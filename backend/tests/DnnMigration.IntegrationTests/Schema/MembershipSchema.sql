-- =====================================================================================
-- MembershipSchema.sql - the EXTERNAL ASP.NET membership objects the credential store
-- reaches, which no mapped entity type describes and no EF migration can produce.
--
-- WHY THESE TABLES ARE PROVISIONED SEPARATELY
--   AAP 0.7.1.3 records the finding that settles this: the 88 legacy DDL scripts only
--   ever ALTER the aspnet_* objects - 04.00.00.SqlDataProvider line 31 is
--   "ALTER PROCEDURE dbo.aspnet_Membership_UpdateUser" and line 119 is
--   "ALTER PROCEDURE dbo.aspnet_Membership_UpdateUserInfo" - because Microsoft's own
--   registration tool installs them. Replaying the legacy chain against an empty
--   database therefore cannot reproduce the terminal schema, and neither can any
--   migration generated from the target model. The membership store is an external
--   legacy dependency that the User entity maps alongside, never something this
--   solution owns.
--
--   An integration database consequently has to stand these objects up itself. Without
--   them Infrastructure/Persistence/MembershipStore.IsAvailableAsync returns false, the
--   user repository fails closed exactly as designed, and every credential-dependent
--   endpoint - account creation, sign-in, password change, unlock, approval - reports a
--   provider error instead of doing its work. Gate 5 requires account creation to answer
--   201, so these tables are a hard requirement of the suite rather than an optional
--   fidelity extra.
--
-- WHAT IS REPRODUCED
--   Only the columns MembershipStore actually reads or writes, with the types and
--   nullability of the stock aspnet_regsql schema. The lockout bookkeeping columns are
--   present because the failed-attempt window and lockout algorithm measured from
--   04.00.00.SqlDataProvider depends on them.
--
--   Stored procedures are NOT reproduced. This solution reaches the store through
--   explicit parameterised statements rather than through the legacy procedures, so a
--   procedure here would be dead weight that could drift away from the code under test.
--
-- BATCHES
--   Separated by a line containing only GO, matching DnnSchema.sql.
-- =====================================================================================

CREATE TABLE [dbo].[aspnet_Applications] (
    [ApplicationName]        nvarchar(256)    NOT NULL,
    [LoweredApplicationName] nvarchar(256)    NOT NULL,
    [ApplicationId]          uniqueidentifier NOT NULL,
    [Description]            nvarchar(256)    NULL,
    CONSTRAINT [PK_aspnet_Applications] PRIMARY KEY ([ApplicationId]),
    CONSTRAINT [UQ_aspnet_Applications_Name] UNIQUE ([ApplicationName]),
    CONSTRAINT [UQ_aspnet_Applications_LoweredName] UNIQUE ([LoweredApplicationName])
);
GO

CREATE TABLE [dbo].[aspnet_Users] (
    [ApplicationId]    uniqueidentifier NOT NULL,
    [UserId]           uniqueidentifier NOT NULL,
    [UserName]         nvarchar(256)    NOT NULL,
    [LoweredUserName]  nvarchar(256)    NOT NULL,
    [MobileAlias]      nvarchar(16)     NULL,
    [IsAnonymous]      bit              NOT NULL,
    [LastActivityDate] datetime         NOT NULL,
    CONSTRAINT [PK_aspnet_Users] PRIMARY KEY ([UserId]),
    CONSTRAINT [FK_aspnet_Users_Applications] FOREIGN KEY ([ApplicationId])
        REFERENCES [dbo].[aspnet_Applications] ([ApplicationId])
);
GO

CREATE UNIQUE INDEX [IX_aspnet_Users_LoweredUserName]
    ON [dbo].[aspnet_Users] ([ApplicationId], [LoweredUserName]);
GO

CREATE TABLE [dbo].[aspnet_Membership] (
    [ApplicationId]                          uniqueidentifier NOT NULL,
    [UserId]                                 uniqueidentifier NOT NULL,
    [Password]                               nvarchar(128)    NOT NULL,
    [PasswordFormat]                         int              NOT NULL,
    [PasswordSalt]                           nvarchar(128)    NOT NULL,
    [MobilePIN]                              nvarchar(16)     NULL,
    [Email]                                  nvarchar(256)    NULL,
    [LoweredEmail]                           nvarchar(256)    NULL,
    [PasswordQuestion]                       nvarchar(256)    NULL,
    [PasswordAnswer]                         nvarchar(128)    NULL,
    [IsApproved]                             bit              NOT NULL,
    [IsLockedOut]                            bit              NOT NULL,
    [CreateDate]                             datetime         NOT NULL,
    [LastLoginDate]                          datetime         NOT NULL,
    [LastPasswordChangedDate]                datetime         NOT NULL,
    [LastLockoutDate]                        datetime         NOT NULL,
    [FailedPasswordAttemptCount]             int              NOT NULL,
    [FailedPasswordAttemptWindowStart]       datetime         NOT NULL,
    [FailedPasswordAnswerAttemptCount]       int              NOT NULL,
    [FailedPasswordAnswerAttemptWindowStart] datetime         NOT NULL,
    [Comment]                                ntext            NULL,
    CONSTRAINT [PK_aspnet_Membership] PRIMARY KEY ([UserId]),
    CONSTRAINT [FK_aspnet_Membership_Applications] FOREIGN KEY ([ApplicationId])
        REFERENCES [dbo].[aspnet_Applications] ([ApplicationId]),
    CONSTRAINT [FK_aspnet_Membership_Users] FOREIGN KEY ([UserId])
        REFERENCES [dbo].[aspnet_Users] ([UserId])
);
GO

-- =====================================================================================
-- THE DEPENDANT MEMBERSHIP TABLES
--
-- WHY THEY ARE HERE, AND WHY THEIR ABSENCE WAS A DEFECT IN THIS FIXTURE
--   Four tables reference dbo.aspnet_Users through NON-CASCADING foreign keys, and the
--   stock aspnet_Users_DeleteUser (InstallCommon.sql lines 421-541) clears every one of
--   them before it removes the user row. A fixture that provisioned only Applications,
--   Users and Membership could not observe that: with no dependant row able to exist, a
--   deletion that skipped the dependants passed here and failed against any real
--   installation whose account had ever held a membership role, stored a profile or
--   personalised a page. The fixture masked the defect rather than catching it, so the
--   tables are provisioned and the foreign keys are declared exactly as the stock schema
--   declares them - WITHOUT ON DELETE CASCADE - because it is the absence of cascade that
--   makes the ordering load-bearing.
--
-- WHAT IS REPRODUCED
--   The stock columns, types and nullability of the aspnet_regsql schema for each table,
--   including aspnet_Paths, which aspnet_PersonalizationPerUser references. Nothing is
--   simplified away: a nullable column left NOT NULL here would let a seeded row exist
--   that the real schema forbids, and a cascade added for convenience would delete the
--   evidence the ordering test depends on.
-- =====================================================================================

CREATE TABLE [dbo].[aspnet_Roles] (
    [ApplicationId]    uniqueidentifier NOT NULL,
    [RoleId]           uniqueidentifier NOT NULL,
    [RoleName]         nvarchar(256)    NOT NULL,
    [LoweredRoleName]  nvarchar(256)    NOT NULL,
    [Description]      nvarchar(256)    NULL,
    CONSTRAINT [PK_aspnet_Roles] PRIMARY KEY ([RoleId]),
    CONSTRAINT [FK_aspnet_Roles_Applications] FOREIGN KEY ([ApplicationId])
        REFERENCES [dbo].[aspnet_Applications] ([ApplicationId])
);
GO

CREATE UNIQUE INDEX [IX_aspnet_Roles_LoweredRoleName]
    ON [dbo].[aspnet_Roles] ([ApplicationId], [LoweredRoleName]);
GO

CREATE TABLE [dbo].[aspnet_UsersInRoles] (
    [UserId] uniqueidentifier NOT NULL,
    [RoleId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_aspnet_UsersInRoles] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_aspnet_UsersInRoles_Users] FOREIGN KEY ([UserId])
        REFERENCES [dbo].[aspnet_Users] ([UserId]),
    CONSTRAINT [FK_aspnet_UsersInRoles_Roles] FOREIGN KEY ([RoleId])
        REFERENCES [dbo].[aspnet_Roles] ([RoleId])
);
GO

CREATE TABLE [dbo].[aspnet_Profile] (
    [UserId]               uniqueidentifier NOT NULL,
    [PropertyNames]        ntext            NOT NULL,
    [PropertyValuesString] ntext            NOT NULL,
    [PropertyValuesBinary] image            NOT NULL,
    [LastUpdatedDate]      datetime         NOT NULL,
    CONSTRAINT [PK_aspnet_Profile] PRIMARY KEY ([UserId]),
    CONSTRAINT [FK_aspnet_Profile_Users] FOREIGN KEY ([UserId])
        REFERENCES [dbo].[aspnet_Users] ([UserId])
);
GO

CREATE TABLE [dbo].[aspnet_Paths] (
    [ApplicationId] uniqueidentifier NOT NULL,
    [PathId]        uniqueidentifier NOT NULL,
    [Path]          nvarchar(256)    NOT NULL,
    [LoweredPath]   nvarchar(256)    NOT NULL,
    CONSTRAINT [PK_aspnet_Paths] PRIMARY KEY ([PathId]),
    CONSTRAINT [FK_aspnet_Paths_Applications] FOREIGN KEY ([ApplicationId])
        REFERENCES [dbo].[aspnet_Applications] ([ApplicationId])
);
GO

CREATE UNIQUE INDEX [IX_aspnet_Paths_LoweredPath]
    ON [dbo].[aspnet_Paths] ([ApplicationId], [LoweredPath]);
GO

CREATE TABLE [dbo].[aspnet_PersonalizationPerUser] (
    [Id]              uniqueidentifier NOT NULL,
    [PathId]          uniqueidentifier NULL,
    [UserId]          uniqueidentifier NULL,
    [PageSettings]    image            NOT NULL,
    [LastUpdatedDate] datetime         NOT NULL,
    CONSTRAINT [PK_aspnet_PersonalizationPerUser] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_aspnet_PersonalizationPerUser_Paths] FOREIGN KEY ([PathId])
        REFERENCES [dbo].[aspnet_Paths] ([PathId]),
    CONSTRAINT [FK_aspnet_PersonalizationPerUser_Users] FOREIGN KEY ([UserId])
        REFERENCES [dbo].[aspnet_Users] ([UserId])
);
GO

CREATE UNIQUE INDEX [IX_aspnet_PersonalizationPerUser_PathUser]
    ON [dbo].[aspnet_PersonalizationPerUser] ([PathId], [UserId]);
GO
