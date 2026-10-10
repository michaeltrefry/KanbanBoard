-- Adds a scope to personal access tokens. Existing tokens become 'full' (unchanged access).
-- Must be applied to production MariaDB BEFORE the code that reads `Scope` is deployed.
-- Safe to re-run: the column and the history row are only added when missing (MariaDB 10.0.2+).
START TRANSACTION;
ALTER TABLE `PersonalAccessTokens` ADD COLUMN IF NOT EXISTS `Scope` varchar(16) NOT NULL DEFAULT 'full';

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261010161942_AddPersonalAccessTokenScope', '10.0.6');

COMMIT;
