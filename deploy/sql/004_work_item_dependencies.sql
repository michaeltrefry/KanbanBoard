-- Adds work item dependencies: one row per "BlockedItemId is blocked by BlockerItemId" edge. Existing items start with none.
-- Deleting a work item (or its project) deletes the edges that name it.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the table and the history row are only added when missing.
START TRANSACTION;
CREATE TABLE IF NOT EXISTS `WorkItemDependencies` (
    `BlockedItemId` varchar(36) NOT NULL,
    `BlockerItemId` varchar(36) NOT NULL,
    PRIMARY KEY (`BlockedItemId`, `BlockerItemId`),
    KEY `IX_WorkItemDependencies_BlockerItemId` (`BlockerItemId`),
    CONSTRAINT `FK_WorkItemDependencies_WorkItems_BlockedItemId` FOREIGN KEY (`BlockedItemId`) REFERENCES `WorkItems` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_WorkItemDependencies_WorkItems_BlockerItemId` FOREIGN KEY (`BlockerItemId`) REFERENCES `WorkItems` (`Id`) ON DELETE CASCADE
);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261012214428_AddWorkItemDependencies', '10.0.6');

COMMIT;
