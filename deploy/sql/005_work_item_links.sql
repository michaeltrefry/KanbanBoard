-- Adds work item links: one row per external URL (a pull request, a branch, ...) attached to a work item.
-- UrlHash is the SHA-256 of Url in lowercase hex; uniqueness and lookups by URL go through it.
-- Deleting a work item (or its project) deletes its links. Existing items start with none.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the table and the history row are only added when missing.
START TRANSACTION;
CREATE TABLE IF NOT EXISTS `WorkItemLinks` (
    `Id` varchar(36) NOT NULL,
    `WorkItemId` varchar(36) NOT NULL,
    `Kind` varchar(16) NOT NULL,
    `Url` varchar(2048) NOT NULL,
    `UrlHash` varchar(64) NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_WorkItemLinks_WorkItemId_Kind_UrlHash` (`WorkItemId`, `Kind`, `UrlHash`),
    KEY `IX_WorkItemLinks_UrlHash` (`UrlHash`),
    CONSTRAINT `FK_WorkItemLinks_WorkItems_WorkItemId` FOREIGN KEY (`WorkItemId`) REFERENCES `WorkItems` (`Id`) ON DELETE CASCADE
);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261012225054_AddWorkItemLinks', '10.0.6');

COMMIT;
