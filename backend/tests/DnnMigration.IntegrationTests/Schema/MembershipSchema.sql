-- =====================================================================================
-- MembershipSchema.sql - the EXTERNAL ASP.NET membership objects the credential store
-- reaches, which no mapped entity type describes and no EF migration can produce.
--
-- WHY THESE THREE TABLES ARE PROVISIONED SEPARATELY
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
