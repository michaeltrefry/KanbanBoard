-- Records which admin issued and which admin revoked a token from the Admin page: the user's id and name at that
-- moment, as a work item change keeps its actor, with no foreign key. IssuedBy* is set by an issue and on the
-- replacement of a rotate; RevokedBy* by a revoke, on the token a rotate replaces, and on each token a disable of its
-- identity revokes, and never overwritten once set. All four stay NULL for every existing token and for tokens
-- issued or revoked otherwise (by their owner on the Settings page, by create-service-identity).
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the columns and the history row are only added when missing (MariaDB 10.0.2+).
START TRANSACTION;
ALTER TABLE `PersonalAccessTokens` ADD COLUMN IF NOT EXISTS `IssuedByName` varchar(320) NULL;
ALTER TABLE `PersonalAccessTokens` ADD COLUMN IF NOT EXISTS `IssuedByUserId` varchar(36) NULL;
ALTER TABLE `PersonalAccessTokens` ADD COLUMN IF NOT EXISTS `RevokedByName` varchar(320) NULL;
ALTER TABLE `PersonalAccessTokens` ADD COLUMN IF NOT EXISTS `RevokedByUserId` varchar(36) NULL;

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261014120543_AddPersonalAccessTokenAdmins', '10.0.6');

COMMIT;
