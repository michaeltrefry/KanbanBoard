-- Makes work item comments records an orchestrator can find again.
-- AuthorUserId is the user behind the token that wrote the comment; it stays NULL for existing comments and for
-- comments written without a token, which keep the author name they have. Author grows to the length of a claimant
-- name. IdempotencyKeyHash is the SHA-256, in lowercase hex, of the caller's identity and idempotency key; the
-- unique key lets one item carry one comment per key, and any number of comments without one (NULL).
-- The API applies this migration on startup; run this script only for manual or bootstrap use.
-- Safe to re-run: the columns, the index and the history row are only added when missing (MariaDB 10.5.2+).
START TRANSACTION;
ALTER TABLE `WorkItemComments` MODIFY COLUMN `Author` varchar(320) NOT NULL;
ALTER TABLE `WorkItemComments` ADD COLUMN IF NOT EXISTS `AuthorUserId` varchar(36) NULL;
ALTER TABLE `WorkItemComments` ADD COLUMN IF NOT EXISTS `IdempotencyKeyHash` varchar(64) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS `IX_WorkItemComments_WorkItemId_IdempotencyKeyHash`
    ON `WorkItemComments` (`WorkItemId`, `IdempotencyKeyHash`);

INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261012233418_AddWorkItemCommentAuthorAndIdempotencyKey', '10.0.6');

COMMIT;
