-- Adds the change feed: one row per change to a work item, written in the transaction of the write it describes.
-- Sequence is the position in the feed. The API inserts and commits an entry while it holds the named lock
-- kanban_work_item_changes, so entries are committed in the order of their sequences; do not insert rows by hand.
-- There is no foreign key on purpose: an entry stays after its work item or its project is deleted.
-- Changes is a JSON array of {"field", "old", "new"}. Nothing is logged for writes made before this table existed.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the table and the history row are only added when missing.
START TRANSACTION;
CREATE TABLE IF NOT EXISTS `WorkItemChanges` (
    `Sequence` bigint NOT NULL AUTO_INCREMENT,
    `WorkItemId` varchar(36) NOT NULL,
    `ProjectId` varchar(36) NOT NULL,
    `Kind` varchar(32) NOT NULL,
    `ActorUserId` varchar(36) NULL,
    `ActorName` varchar(320) NULL,
    `Changes` longtext NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Sequence`),
    KEY `IX_WorkItemChanges_ProjectId_Sequence` (`ProjectId`, `Sequence`)
);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261013013744_AddWorkItemChanges', '10.0.6');

COMMIT;
