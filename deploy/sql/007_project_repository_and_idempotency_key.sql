-- Gives a project its target repository and makes its creation repeatable.
-- RepositoryUrl is the http, https or ssh URL of the repository the project's work is done in, and DefaultBranch the
-- branch of it that work starts from; both stay NULL for existing projects. IdempotencyKeyHash is the SHA-256, in
-- lowercase hex, of the caller's identity and idempotency key; the unique key lets one key create one project, and
-- any number of projects be created without one (NULL).
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the columns, the index and the history row are only added when missing (MariaDB 10.5.2+).
START TRANSACTION;
ALTER TABLE `Projects` ADD COLUMN IF NOT EXISTS `DefaultBranch` varchar(255) NULL;
ALTER TABLE `Projects` ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash` varchar(64) NULL;
ALTER TABLE `Projects` ADD COLUMN IF NOT EXISTS `RepositoryUrl` varchar(2048) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS `IX_Projects_IdempotencyKeyHash`
    ON `Projects` (`IdempotencyKeyHash`);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261013000421_AddProjectRepositoryAndIdempotencyKey', '10.0.6');

COMMIT;
