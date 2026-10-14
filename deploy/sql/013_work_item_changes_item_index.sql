-- Adds the index behind the history of one work item: GET /api/items/changes?itemId=… (the Story page) and
-- list_changes with item_id read the entries of one item in feed order. Without it they scan the whole feed, which is
-- never pruned. Existing rows are not changed.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the index and the history row are only added when missing (MariaDB 10.0.2+).
START TRANSACTION;
CREATE INDEX IF NOT EXISTS `IX_WorkItemChanges_WorkItemId_Sequence`
    ON `WorkItemChanges` (`WorkItemId`, `Sequence`);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261014185513_AddWorkItemChangesItemIndex', '10.0.6');

COMMIT;
