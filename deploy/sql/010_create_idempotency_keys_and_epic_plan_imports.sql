-- Makes the creation of epics, epic documents and work items repeatable, and adds the record of an epic plan import.
-- IdempotencyKeyHash is the SHA-256, in lowercase hex, of the caller's identity and idempotency key. The unique keys
-- let one key create one epic or one work item per project and one document per epic, and any number of them be
-- created without a key (NULL): existing rows keep NULL.
-- EpicPlanImports holds one row per import_epic_plan call that created an epic: the key hash, unique per project, and
-- the ids the import answered with (Result, JSON), so that a repeat of the key answers with the same ids. The row
-- goes with its epic. The API writes it in the transaction of the import; do not insert rows by hand.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the columns, the table, the indexes and the history row are only added when missing (MariaDB 10.5.2+).
START TRANSACTION;
ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash` varchar(64) NULL;
ALTER TABLE `Epics` ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash` varchar(64) NULL;
ALTER TABLE `EpicDocuments` ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash` varchar(64) NULL;

CREATE TABLE IF NOT EXISTS `EpicPlanImports` (
    `EpicId` varchar(36) NOT NULL,
    `ProjectId` varchar(36) NOT NULL,
    `IdempotencyKeyHash` varchar(64) NOT NULL,
    `Result` longtext NOT NULL,
    PRIMARY KEY (`EpicId`),
    CONSTRAINT `FK_EpicPlanImports_Epics_EpicId` FOREIGN KEY (`EpicId`) REFERENCES `Epics` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

CREATE UNIQUE INDEX IF NOT EXISTS `IX_WorkItems_ProjectId_IdempotencyKeyHash`
    ON `WorkItems` (`ProjectId`, `IdempotencyKeyHash`);
CREATE UNIQUE INDEX IF NOT EXISTS `IX_Epics_ProjectId_IdempotencyKeyHash`
    ON `Epics` (`ProjectId`, `IdempotencyKeyHash`);
CREATE UNIQUE INDEX IF NOT EXISTS `IX_EpicDocuments_EpicId_IdempotencyKeyHash`
    ON `EpicDocuments` (`EpicId`, `IdempotencyKeyHash`);
CREATE UNIQUE INDEX IF NOT EXISTS `IX_EpicPlanImports_ProjectId_IdempotencyKeyHash`
    ON `EpicPlanImports` (`ProjectId`, `IdempotencyKeyHash`);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261013132811_AddCreateIdempotencyKeysAndEpicPlanImports', '10.0.6');

COMMIT;
