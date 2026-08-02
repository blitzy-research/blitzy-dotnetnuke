-- =====================================================================================
-- HostSettingsSchema.sql - the installation-wide settings table, which is read through
-- explicit statements rather than through a mapped entity type.
--
-- WHY IT IS NOT PART OF DnnSchema.sql
--   dbo.HostSettings is not an entity in the model. AAP 0.2.2 excludes the
--   DotNetNuke.Common.Globals and DotNetNuke.Entities.Host subsystems, and AAP 0.5.1.3
--   replaces the six members that in-scope code actually reached with a narrow
--   Infrastructure/Services/HostSettingsService that issues its own parameterised
--   statements against this table. It therefore has no entity configuration and cannot
--   appear in a script emitted from the model, yet portal creation reads it: the demo
--   period, host fee, host space, page quota, user quota, site-log history and currency
--   defaults all come from here, reproducing legacy PortalController.CreatePortal.
--
--   Without this table the very first POST to /api/v1/portals fails with an object-name
--   error, so it is a hard requirement of the suite.
--
-- TERMINAL SHAPE
--   Taken from 01.00.05.SqlDataProvider line 2259, which creates SettingName and
--   SettingValue with a unique constraint on the name, plus the later alteration that
--   adds SettingIsSecure as NOT NULL defaulting to 0. Row values are seeded by the test
--   fixture, not by this script, because which settings matter depends on the test.
--
-- BATCHES
--   Separated by a line containing only GO, matching the other schema scripts.
-- =====================================================================================

CREATE TABLE [dbo].[HostSettings] (
    [SettingName]     nvarchar(50)  NOT NULL,
    [SettingValue]    nvarchar(256) NOT NULL,
    [SettingIsSecure] bit           NOT NULL CONSTRAINT [DF_HostSettings_Secure] DEFAULT (0),
    CONSTRAINT [IX_HostSettings] UNIQUE NONCLUSTERED ([SettingName])
);
GO
