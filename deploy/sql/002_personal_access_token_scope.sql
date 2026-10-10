-- Adds a scope to personal access tokens. Existing tokens become 'full' (unchanged access).
START TRANSACTION;
ALTER TABLE `PersonalAccessTokens` ADD `Scope` varchar(16) NOT NULL DEFAULT 'full';

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261010161942_AddPersonalAccessTokenScope', '10.0.6');

COMMIT;
