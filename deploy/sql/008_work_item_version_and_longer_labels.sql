-- Gives a work item a version and room for more labels.
-- Version counts the writes to the item's own fields: every update and every move of the item adds one. Existing
-- items start at 1, like a new item. Labels stays one comma-separated string and grows from 240 to 4000 characters;
-- the labels already stored are kept as they are.
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the column and the history row are only added when missing, and MODIFY states the wanted type.
START TRANSACTION;
ALTER TABLE `WorkItems` MODIFY COLUMN `Labels` varchar(4000) NULL;
ALTER TABLE `WorkItems` ADD COLUMN IF NOT EXISTS `Version` bigint NOT NULL DEFAULT 1;

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261013004410_AddWorkItemVersionAndLongerLabels', '10.0.6');

COMMIT;
