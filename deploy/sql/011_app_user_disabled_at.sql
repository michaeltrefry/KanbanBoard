-- Adds the time a service identity was disabled. NULL means enabled: every existing user, person or service identity,
-- stays enabled. A disabled identity gets no new token and none of its tokens is accepted; it is never deleted.
-- The API sets and clears it from the admin page (in the transaction that revokes the identity's tokens); do not
-- set it by hand without revoking the identity's tokens too.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the column and the history row are only added when missing (MariaDB 10.0.2+).
START TRANSACTION;
ALTER TABLE `AppUsers` ADD COLUMN IF NOT EXISTS `DisabledAtUtc` datetime(6) NULL;

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261014110144_AddAppUserDisabledAt', '10.0.6');

COMMIT;
