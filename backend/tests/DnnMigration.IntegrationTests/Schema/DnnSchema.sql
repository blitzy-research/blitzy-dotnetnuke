-- =====================================================================================
-- DnnSchema.sql - the mapped DotNetNuke tables the Entity Framework model binds to.
--
-- WHY THIS FILE EXISTS RATHER THAN EnsureCreated()
--   AAP rule T4 states that the DotNetNuke schema is immutable and externally owned:
--   it is produced by the 88 legacy *.SqlDataProvider upgrade scripts, and the baseline
--   EF migration is deliberately empty so that nothing this solution ships can alter a
--   real installation. Provisioning an integration database from an explicit DDL script
--   therefore models reality far more closely than asking the model to create itself,
--   and it is the same mechanism that provisions the external membership objects in
--   MembershipSchema.sql - objects that no model-driven creation could ever produce
--   because they are not mapped entity types.
--
-- HOW IT IS KEPT HONEST
--   The statements below were emitted from the live model with:
--     dotnet ef dbcontext script --project src/DnnMigration.Infrastructure \
--                               --startup-project src/DnnMigration.Api
--   If an entity configuration later gains a column that this script lacks, the very
--   first repository query naming that column fails with an object-name error, so the
--   snapshot cannot drift silently: it fails loudly on the next test run.
--
--   Table and column names are the legacy names, upper/lower case included, because
--   that binding is exactly what the Fluent configurations exist to pin.
--
-- BATCHES
--   Statements are separated by a line containing only GO. TestDatabaseFactory splits
--   on that separator and executes each batch in order, because GO is a client-side
--   batch delimiter that the SQL Server protocol itself does not understand.
-- =====================================================================================

CREATE TABLE [dbo].[DesktopModules] (
    [DesktopModuleID] int NOT NULL IDENTITY,
    [FriendlyName] nvarchar(128) NOT NULL,
    [Description] nvarchar(2000) NULL,
    [Version] nvarchar(8) NULL,
    [IsPremium] bit NOT NULL,
    [IsAdmin] bit NOT NULL,
    [BusinessControllerClass] nvarchar(200) NULL,
    [FolderName] nvarchar(128) NOT NULL,
    [ModuleName] nvarchar(128) NOT NULL,
    [SupportedFeatures] int NOT NULL DEFAULT 0,
    [CompatibleVersions] nvarchar(500) NULL,
    [Dependencies] nvarchar(400) NULL,
    [Permissions] nvarchar(400) NULL,
    CONSTRAINT [PK_DesktopModules] PRIMARY KEY ([DesktopModuleID])
);
GO


CREATE TABLE [dbo].[Portals] (
    [PortalID] int NOT NULL IDENTITY(-1, 1),
    [PortalName] nvarchar(128) NOT NULL,
    [LogoFile] nvarchar(50) NULL,
    [FooterText] nvarchar(100) NULL,
    [ExpiryDate] datetime NULL,
    [UserRegistration] int NOT NULL DEFAULT 0,
    [BannerAdvertising] int NOT NULL DEFAULT 0,
    [AdministratorId] int NULL,
    [Currency] char(3) NULL,
    [HostFee] money NOT NULL DEFAULT 0.0,
    [HostSpace] int NOT NULL DEFAULT 0,
    [AdministratorRoleId] int NULL,
    [RegisteredRoleId] int NULL,
    [Description] nvarchar(500) NULL,
    [KeyWords] nvarchar(500) NULL,
    [BackgroundFile] nvarchar(50) NULL,
    [GUID] uniqueidentifier NOT NULL DEFAULT (newid()),
    [PaymentProcessor] nvarchar(50) NULL,
    [ProcessorUserId] nvarchar(50) NULL,
    [ProcessorPassword] nvarchar(50) NULL,
    [SiteLogHistory] int NULL,
    [HomeTabId] int NULL,
    [LoginTabId] int NULL,
    [UserTabId] int NULL,
    [DefaultLanguage] nvarchar(10) NOT NULL DEFAULT N'en-US',
    [TimezoneOffset] int NOT NULL DEFAULT -8,
    [AdminTabId] int NULL,
    [HomeDirectory] varchar(100) NOT NULL DEFAULT '',
    [SplashTabId] int NULL,
    [PageQuota] int NOT NULL DEFAULT 0,
    [UserQuota] int NOT NULL DEFAULT 0,
    CONSTRAINT [PK_Portals] PRIMARY KEY ([PortalID])
);
GO


CREATE TABLE [dbo].[Users] (
    [UserID] int NOT NULL IDENTITY,
    [Username] nvarchar(100) NOT NULL,
    [FirstName] nvarchar(50) NOT NULL,
    [LastName] nvarchar(50) NOT NULL,
    [DisplayName] nvarchar(128) NOT NULL DEFAULT N'',
    [Email] nvarchar(256) NULL,
    [IsSuperUser] bit NOT NULL DEFAULT CAST(0 AS bit),
    [AffiliateId] int NULL,
    [UpdatePassword] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [PK_Users] PRIMARY KEY ([UserID])
);
GO


CREATE TABLE [dbo].[ModuleDefinitions] (
    [ModuleDefID] int NOT NULL IDENTITY,
    [FriendlyName] nvarchar(128) NOT NULL,
    [DesktopModuleID] int NOT NULL,
    [DefaultCacheTime] int NOT NULL DEFAULT 0,
    CONSTRAINT [PK_ModuleDefinitions] PRIMARY KEY ([ModuleDefID]),
    CONSTRAINT [FK_ModuleDefinitions_DesktopModules] FOREIGN KEY ([DesktopModuleID]) REFERENCES [dbo].[DesktopModules] ([DesktopModuleID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[PortalAlias] (
    [PortalAliasID] int NOT NULL IDENTITY,
    [PortalID] int NOT NULL,
    [HTTPAlias] nvarchar(200) NOT NULL,
    CONSTRAINT [PK_PortalAlias] PRIMARY KEY ([PortalAliasID]),
    CONSTRAINT [FK_PortalAlias_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[PortalDesktopModules] (
    [PortalDesktopModuleID] int NOT NULL IDENTITY,
    [PortalID] int NOT NULL,
    [DesktopModuleID] int NOT NULL,
    CONSTRAINT [PK_PortalDesktopModules] PRIMARY KEY ([PortalDesktopModuleID]),
    CONSTRAINT [FK_PortalDesktopModules_DesktopModules] FOREIGN KEY ([DesktopModuleID]) REFERENCES [dbo].[DesktopModules] ([DesktopModuleID]) ON DELETE CASCADE,
    CONSTRAINT [FK_PortalDesktopModules_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[RoleGroups] (
    [RoleGroupID] int NOT NULL IDENTITY(0, 1),
    [PortalID] int NOT NULL,
    [RoleGroupName] nvarchar(50) NOT NULL,
    [Description] nvarchar(1000) NULL,
    CONSTRAINT [PK_RoleGroups] PRIMARY KEY ([RoleGroupID]),
    CONSTRAINT [FK_RoleGroups_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[Tabs] (
    [TabID] int NOT NULL IDENTITY(0, 1),
    [TabOrder] int NOT NULL DEFAULT 0,
    [PortalID] int NULL,
    [TabName] nvarchar(50) NOT NULL,
    [IsVisible] bit NOT NULL,
    [ParentId] int NULL,
    [Level] int NOT NULL DEFAULT 0,
    [IconFile] nvarchar(100) NULL,
    [DisableLink] bit NOT NULL DEFAULT CAST(0 AS bit),
    [Title] nvarchar(200) NULL,
    [Description] nvarchar(500) NULL,
    [KeyWords] nvarchar(500) NULL,
    [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
    [Url] nvarchar(255) NULL,
    [SkinSrc] nvarchar(200) NULL,
    [ContainerSrc] nvarchar(200) NULL,
    [TabPath] nvarchar(255) NULL,
    [StartDate] datetime NULL,
    [EndDate] datetime NULL,
    [RefreshInterval] int NULL,
    [PageHeadText] nvarchar(500) NULL,
    [IsSecure] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [PK_Tabs] PRIMARY KEY ([TabID]),
    CONSTRAINT [FK_Tabs_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE,
    CONSTRAINT [FK_Tabs_Tabs] FOREIGN KEY ([ParentId]) REFERENCES [dbo].[Tabs] ([TabID])
);
GO


CREATE TABLE [dbo].[UserPortals] (
    [UserId] int NOT NULL,
    [PortalId] int NOT NULL,
    [UserPortalId] int NOT NULL IDENTITY,
    [CreatedDate] datetime NOT NULL,
    [Authorised] bit NOT NULL,
    CONSTRAINT [PK_UserPortals] PRIMARY KEY ([UserId], [PortalId]),
    CONSTRAINT [FK_UserPortals_Portals] FOREIGN KEY ([PortalId]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserPortals_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[ModuleControls] (
    [ModuleControlID] int NOT NULL IDENTITY,
    [ModuleDefID] int NULL,
    [ControlKey] nvarchar(20) NULL,
    [ControlTitle] nvarchar(50) NULL,
    [ControlSrc] nvarchar(256) NULL,
    [IconFile] nvarchar(100) NULL,
    [ControlType] int NOT NULL,
    [ViewOrder] int NULL,
    [HelpUrl] nvarchar(200) NULL,
    [SupportsPartialRendering] bit NOT NULL DEFAULT CAST(0 AS bit),
    CONSTRAINT [PK_ModuleControls] PRIMARY KEY ([ModuleControlID]),
    CONSTRAINT [FK_ModuleControls_ModuleDefinitions] FOREIGN KEY ([ModuleDefID]) REFERENCES [dbo].[ModuleDefinitions] ([ModuleDefID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[Modules] (
    [ModuleID] int NOT NULL IDENTITY(0, 1),
    [ModuleDefID] int NOT NULL,
    [PortalID] int NULL,
    [ModuleTitle] nvarchar(256) NULL,
    [AllTabs] bit NOT NULL DEFAULT CAST(0 AS bit),
    [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit),
    [InheritViewPermissions] bit NULL,
    [Header] ntext NULL,
    [Footer] ntext NULL,
    [StartDate] datetime NULL,
    [EndDate] datetime NULL,
    CONSTRAINT [PK_Modules] PRIMARY KEY ([ModuleID]),
    CONSTRAINT [FK_Modules_ModuleDefinitions] FOREIGN KEY ([ModuleDefID]) REFERENCES [dbo].[ModuleDefinitions] ([ModuleDefID]) ON DELETE CASCADE,
    CONSTRAINT [FK_Modules_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID])
);
GO


CREATE TABLE [dbo].[Permission] (
    [PermissionID] int NOT NULL IDENTITY,
    [PermissionCode] varchar(50) NOT NULL,
    [ModuleDefID] int NOT NULL,
    [PermissionKey] varchar(20) NOT NULL,
    [PermissionName] varchar(50) NOT NULL,
    CONSTRAINT [PK_Permission] PRIMARY KEY ([PermissionID]),
    CONSTRAINT [FK_Permission_ModuleDefinitions_ModuleDefID] FOREIGN KEY ([ModuleDefID]) REFERENCES [dbo].[ModuleDefinitions] ([ModuleDefID])
);
GO


CREATE TABLE [dbo].[ProfilePropertyDefinition] (
    [PropertyDefinitionID] int NOT NULL IDENTITY,
    [PortalID] int NULL,
    [ModuleDefID] int NULL,
    [Deleted] bit NOT NULL,
    [DataType] int NOT NULL,
    [DefaultValue] ntext NULL,
    [PropertyCategory] nvarchar(50) NOT NULL,
    [PropertyName] nvarchar(50) NOT NULL,
    [Length] int NOT NULL DEFAULT 0,
    [Required] bit NOT NULL,
    [ValidationExpression] nvarchar(2000) NULL,
    [ViewOrder] int NOT NULL,
    [Visible] bit NOT NULL,
    CONSTRAINT [PK_ProfilePropertyDefinition] PRIMARY KEY ([PropertyDefinitionID]),
    CONSTRAINT [FK_ProfilePropertyDefinition_ModuleDefinitions_ModuleDefID] FOREIGN KEY ([ModuleDefID]) REFERENCES [dbo].[ModuleDefinitions] ([ModuleDefID]),
    CONSTRAINT [FK_ProfilePropertyDefinition_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[Roles] (
    [RoleID] int NOT NULL IDENTITY(0, 1),
    [PortalID] int NULL,
    [RoleName] nvarchar(50) NOT NULL,
    [Description] nvarchar(1000) NULL,
    [ServiceFee] money NULL,
    [BillingPeriod] int NULL,
    [BillingFrequency] char(1) NULL,
    [TrialFee] money NULL,
    [TrialPeriod] int NULL,
    [TrialFrequency] char(1) NULL,
    [IsPublic] bit NOT NULL DEFAULT CAST(0 AS bit),
    [AutoAssignment] bit NOT NULL DEFAULT CAST(0 AS bit),
    [RoleGroupID] int NULL,
    [RSVPCode] nvarchar(50) NULL,
    [IconFile] nvarchar(100) NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([RoleID]),
    CONSTRAINT [FK_Roles_Portals] FOREIGN KEY ([PortalID]) REFERENCES [dbo].[Portals] ([PortalID]) ON DELETE CASCADE,
    CONSTRAINT [FK_Roles_RoleGroups] FOREIGN KEY ([RoleGroupID]) REFERENCES [dbo].[RoleGroups] ([RoleGroupID])
);
GO


CREATE TABLE [dbo].[ModuleSettings] (
    [ModuleID] int NOT NULL,
    [SettingName] nvarchar(50) NOT NULL,
    [SettingValue] nvarchar(256) NOT NULL,
    CONSTRAINT [PK_ModuleSettings] PRIMARY KEY ([ModuleID], [SettingName]),
    CONSTRAINT [FK_ModuleSettings_Modules] FOREIGN KEY ([ModuleID]) REFERENCES [dbo].[Modules] ([ModuleID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[TabModules] (
    [TabModuleID] int NOT NULL IDENTITY,
    [TabID] int NOT NULL,
    [ModuleID] int NOT NULL,
    [PaneName] nvarchar(50) NOT NULL,
    [ModuleOrder] int NOT NULL,
    [CacheTime] int NOT NULL,
    [Alignment] nvarchar(10) NULL,
    [Color] nvarchar(20) NULL,
    [Border] nvarchar(1) NULL,
    [IconFile] nvarchar(100) NULL,
    [Visibility] int NOT NULL,
    [ContainerSrc] nvarchar(200) NULL,
    [DisplayTitle] bit NOT NULL,
    [DisplayPrint] bit NOT NULL,
    [DisplaySyndicate] bit NOT NULL,
    CONSTRAINT [PK_TabModules] PRIMARY KEY ([TabModuleID]),
    CONSTRAINT [FK_TabModules_Modules] FOREIGN KEY ([ModuleID]) REFERENCES [dbo].[Modules] ([ModuleID]) ON DELETE CASCADE,
    CONSTRAINT [FK_TabModules_Tabs] FOREIGN KEY ([TabID]) REFERENCES [dbo].[Tabs] ([TabID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[UserProfile] (
    [ProfileID] int NOT NULL IDENTITY,
    [UserID] int NOT NULL,
    [PropertyDefinitionID] int NOT NULL,
    [PropertyValue] nvarchar(3750) NULL,
    [PropertyText] ntext NULL,
    [Visibility] int NOT NULL DEFAULT 0,
    [LastUpdatedDate] datetime NOT NULL,
    CONSTRAINT [PK_UserProfile] PRIMARY KEY ([ProfileID]),
    CONSTRAINT [FK_UserProfile_ProfilePropertyDefinition] FOREIGN KEY ([PropertyDefinitionID]) REFERENCES [dbo].[ProfilePropertyDefinition] ([PropertyDefinitionID]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserProfile_Users] FOREIGN KEY ([UserID]) REFERENCES [dbo].[Users] ([UserID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[ModulePermission] (
    [ModulePermissionID] int NOT NULL IDENTITY,
    [ModuleID] int NOT NULL,
    [PermissionID] int NOT NULL,
    [RoleID] int NULL,
    [UserID] int NULL,
    [AllowAccess] bit NOT NULL,
    CONSTRAINT [PK_ModulePermission] PRIMARY KEY ([ModulePermissionID]),
    CONSTRAINT [FK_ModulePermissionUsers] FOREIGN KEY ([UserID]) REFERENCES [dbo].[Users] ([UserID]),
    CONSTRAINT [FK_ModulePermission_Modules] FOREIGN KEY ([ModuleID]) REFERENCES [dbo].[Modules] ([ModuleID]) ON DELETE CASCADE,
    CONSTRAINT [FK_ModulePermission_Permission] FOREIGN KEY ([PermissionID]) REFERENCES [dbo].[Permission] ([PermissionID]) ON DELETE CASCADE,
    CONSTRAINT [FK_ModulePermission_Roles_RoleID] FOREIGN KEY ([RoleID]) REFERENCES [dbo].[Roles] ([RoleID])
);
GO


CREATE TABLE [dbo].[TabPermission] (
    [TabPermissionID] int NOT NULL IDENTITY,
    [TabID] int NOT NULL,
    [PermissionID] int NOT NULL,
    [RoleID] int NULL,
    [UserID] int NULL,
    [AllowAccess] bit NOT NULL,
    CONSTRAINT [PK_TabPermission] PRIMARY KEY ([TabPermissionID]),
    CONSTRAINT [FK_TabPermission_Permission] FOREIGN KEY ([PermissionID]) REFERENCES [dbo].[Permission] ([PermissionID]) ON DELETE CASCADE,
    CONSTRAINT [FK_TabPermission_Roles_RoleID] FOREIGN KEY ([RoleID]) REFERENCES [dbo].[Roles] ([RoleID]),
    CONSTRAINT [FK_TabPermission_Tabs] FOREIGN KEY ([TabID]) REFERENCES [dbo].[Tabs] ([TabID]) ON DELETE CASCADE,
    CONSTRAINT [FK_TabPermission_Users] FOREIGN KEY ([UserID]) REFERENCES [dbo].[Users] ([UserID])
);
GO


CREATE TABLE [dbo].[UserRoles] (
    [UserRoleID] int NOT NULL IDENTITY,
    [UserID] int NOT NULL,
    [RoleID] int NOT NULL,
    [EffectiveDate] datetime NULL,
    [ExpiryDate] datetime NULL,
    [IsTrialUsed] bit NULL,
    CONSTRAINT [PK_UserRoles] PRIMARY KEY ([UserRoleID]),
    CONSTRAINT [FK_UserRoles_Roles] FOREIGN KEY ([RoleID]) REFERENCES [dbo].[Roles] ([RoleID]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserRoles_Users] FOREIGN KEY ([UserID]) REFERENCES [dbo].[Users] ([UserID]) ON DELETE CASCADE
);
GO


CREATE TABLE [dbo].[TabModuleSettings] (
    [TabModuleID] int NOT NULL,
    [SettingName] nvarchar(50) NOT NULL,
    [SettingValue] nvarchar(2000) NOT NULL,
    CONSTRAINT [PK_TabModuleSettings] PRIMARY KEY ([TabModuleID], [SettingName]),
    CONSTRAINT [FK_TabModuleSettings_TabModules] FOREIGN KEY ([TabModuleID]) REFERENCES [dbo].[TabModules] ([TabModuleID]) ON DELETE CASCADE
);
GO


CREATE INDEX [IX_DesktopModules_FriendlyName] ON [dbo].[DesktopModules] ([FriendlyName]);
GO


CREATE UNIQUE INDEX [IX_DesktopModules_ModuleName] ON [dbo].[DesktopModules] ([ModuleName]);
GO


CREATE UNIQUE INDEX [IX_ModuleControls] ON [dbo].[ModuleControls] ([ModuleDefID], [ControlKey], [ControlSrc]) WHERE [ModuleDefID] IS NOT NULL AND [ControlKey] IS NOT NULL AND [ControlSrc] IS NOT NULL;
GO


CREATE UNIQUE INDEX [IX_ModuleDefinitions] ON [dbo].[ModuleDefinitions] ([FriendlyName]);
GO


CREATE INDEX [IX_ModuleDefinitions_DesktopModuleID] ON [dbo].[ModuleDefinitions] ([DesktopModuleID]);
GO


CREATE UNIQUE INDEX [IX_ModulePermission] ON [dbo].[ModulePermission] ([ModuleID], [PermissionID], [RoleID], [UserID]) WHERE [RoleID] IS NOT NULL AND [UserID] IS NOT NULL;
GO


CREATE INDEX [IX_ModulePermission_PermissionID] ON [dbo].[ModulePermission] ([PermissionID]);
GO


CREATE INDEX [IX_ModulePermission_RoleID] ON [dbo].[ModulePermission] ([RoleID]);
GO


CREATE INDEX [IX_ModulePermission_UserID] ON [dbo].[ModulePermission] ([UserID]);
GO


CREATE INDEX [IX_Modules_ModuleDefID] ON [dbo].[Modules] ([ModuleDefID]);
GO


CREATE INDEX [IX_Modules_PortalID] ON [dbo].[Modules] ([PortalID]);
GO


CREATE UNIQUE INDEX [IX_Permission] ON [dbo].[Permission] ([PermissionCode], [ModuleDefID], [PermissionKey]);
GO


CREATE INDEX [IX_Permission_ModuleDefID] ON [dbo].[Permission] ([ModuleDefID]);
GO


CREATE UNIQUE INDEX [IX_PortalAlias] ON [dbo].[PortalAlias] ([HTTPAlias]);
GO


CREATE INDEX [IX_PortalAlias_PortalID] ON [dbo].[PortalAlias] ([PortalID]);
GO


CREATE UNIQUE INDEX [IX_PortalDesktopModules] ON [dbo].[PortalDesktopModules] ([PortalID], [DesktopModuleID]);
GO


CREATE INDEX [IX_PortalDesktopModules_DesktopModuleID] ON [dbo].[PortalDesktopModules] ([DesktopModuleID]);
GO


CREATE UNIQUE INDEX [IX_ProfilePropertyDefinition] ON [dbo].[ProfilePropertyDefinition] ([PortalID], [ModuleDefID], [PropertyName]);
GO


CREATE INDEX [IX_ProfilePropertyDefinition_ModuleDefID] ON [dbo].[ProfilePropertyDefinition] ([ModuleDefID]);
GO


CREATE INDEX [IX_ProfilePropertyDefinition_PropertyName] ON [dbo].[ProfilePropertyDefinition] ([PropertyName]);
GO


CREATE UNIQUE INDEX [IX_RoleGroupName] ON [dbo].[RoleGroups] ([PortalID], [RoleGroupName]);
GO


CREATE UNIQUE INDEX [IX_RoleName] ON [dbo].[Roles] ([PortalID], [RoleName]) WHERE [PortalID] IS NOT NULL;
GO


CREATE INDEX [IX_Roles_RoleGroupID] ON [dbo].[Roles] ([RoleGroupID]);
GO


CREATE UNIQUE INDEX [IX_TabModules] ON [dbo].[TabModules] ([TabID], [ModuleID]);
GO


CREATE INDEX [IX_TabModules_ModuleID] ON [dbo].[TabModules] ([ModuleID]);
GO


CREATE UNIQUE INDEX [IX_TabPermission] ON [dbo].[TabPermission] ([TabID], [PermissionID], [RoleID], [UserID]) WHERE [RoleID] IS NOT NULL AND [UserID] IS NOT NULL;
GO


CREATE INDEX [IX_TabPermission_PermissionID] ON [dbo].[TabPermission] ([PermissionID]);
GO


CREATE INDEX [IX_TabPermission_RoleID] ON [dbo].[TabPermission] ([RoleID]);
GO


CREATE INDEX [IX_TabPermission_UserID] ON [dbo].[TabPermission] ([UserID]);
GO


CREATE INDEX [IX_Tabs_ParentId] ON [dbo].[Tabs] ([ParentId]);
GO


CREATE INDEX [IX_Tabs_PortalID] ON [dbo].[Tabs] ([PortalID]);
GO


CREATE INDEX [IX_UserPortals_PortalId] ON [dbo].[UserPortals] ([PortalId]);
GO


CREATE INDEX [IX_UserProfile_PropertyDefinitionID] ON [dbo].[UserProfile] ([PropertyDefinitionID]);
GO


CREATE INDEX [IX_UserProfile_UserID] ON [dbo].[UserProfile] ([UserID]);
GO


CREATE INDEX [IX_UserRoles_RoleID] ON [dbo].[UserRoles] ([RoleID]);
GO


CREATE INDEX [IX_UserRoles_UserID] ON [dbo].[UserRoles] ([UserID]);
GO


CREATE UNIQUE INDEX [IX_Users] ON [dbo].[Users] ([Username]);
GO


