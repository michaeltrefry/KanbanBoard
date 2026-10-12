-- Adds the claim columns to work items (claimant, marker, expiry). Existing items start unclaimed.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the columns and the history row are only added when missing (MariaDB 10.0.2+).
START TRANSACTION;
ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `ClaimExpiresAtUtc` datetime(6) NULL;
ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `ClaimMarker` varchar(200) NULL;
-- Markers compare byte for byte, not under the database's case-insensitive default collation.
ALTER TABLE `WorkItems` MODIFY `ClaimMarker` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL;
ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `ClaimantName` varchar(320) NULL;
ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `ClaimantUserId` varchar(36) NULL;

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261012211402_AddWorkItemClaim', '10.0.6');

COMMIT;
