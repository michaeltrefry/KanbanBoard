CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) NOT NULL,
    `ProductVersion` varchar(32) NOT NULL,
    PRIMARY KEY (`MigrationId`)
);

START TRANSACTION;
CREATE TABLE `AppUsers` (
    `Id` varchar(36) NOT NULL,
    `Issuer` varchar(512) NOT NULL,
    `Subject` varchar(200) NOT NULL,
    `DisplayName` varchar(200) NULL,
    `Email` varchar(320) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    `LastSeenAtUtc` datetime(6) NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `Projects` (
    `Id` varchar(36) NOT NULL,
    `Name` varchar(160) NOT NULL,
    `Key` varchar(24) NOT NULL,
    `Description` longtext NULL,
    `IsArchived` tinyint(1) NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE `PersonalAccessTokens` (
    `Id` varchar(36) NOT NULL,
    `AppUserId` varchar(36) NOT NULL,
    `Name` varchar(160) NOT NULL,
    `TokenPrefix` varchar(32) NOT NULL,
    `TokenHash` varchar(128) NOT NULL,
    `EncryptedSecret` varchar(1024) NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `ExpiresAtUtc` datetime(6) NULL,
    `LastUsedAtUtc` datetime(6) NULL,
    `RevokedAtUtc` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_PersonalAccessTokens_AppUsers_AppUserId` FOREIGN KEY (`AppUserId`) REFERENCES `AppUsers` (`Id`) ON DELETE CASCADE
);

CREATE TABLE `Epics` (
    `Id` varchar(36) NOT NULL,
    `ProjectId` varchar(36) NOT NULL,
    `Name` varchar(160) NOT NULL,
    `Description` longtext NULL,
    `IsArchived` tinyint(1) NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Epics_Projects_ProjectId` FOREIGN KEY (`ProjectId`) REFERENCES `Projects` (`Id`) ON DELETE CASCADE
);

CREATE TABLE `EpicDocuments` (
    `Id` varchar(36) NOT NULL,
    `EpicId` varchar(36) NOT NULL,
    `Title` varchar(200) NOT NULL,
    `Body` longtext NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_EpicDocuments_Epics_EpicId` FOREIGN KEY (`EpicId`) REFERENCES `Epics` (`Id`) ON DELETE CASCADE
);

CREATE TABLE `WorkItems` (
    `Id` varchar(36) NOT NULL,
    `ProjectId` varchar(36) NOT NULL,
    `EpicId` varchar(36) NULL,
    `Title` varchar(240) NOT NULL,
    `Description` longtext NULL,
    `Type` int NOT NULL,
    `Status` int NOT NULL,
    `Priority` int NOT NULL,
    `Order` int NOT NULL,
    `Estimate` int NULL,
    `Labels` varchar(240) NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    `UpdatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_WorkItems_Epics_EpicId` FOREIGN KEY (`EpicId`) REFERENCES `Epics` (`Id`) ON DELETE SET NULL,
    CONSTRAINT `FK_WorkItems_Projects_ProjectId` FOREIGN KEY (`ProjectId`) REFERENCES `Projects` (`Id`) ON DELETE CASCADE
);

CREATE TABLE `WorkItemComments` (
    `Id` varchar(36) NOT NULL,
    `WorkItemId` varchar(36) NOT NULL,
    `Author` varchar(80) NOT NULL,
    `Body` longtext NOT NULL,
    `CreatedAtUtc` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    CONSTRAINT `FK_WorkItemComments_WorkItems_WorkItemId` FOREIGN KEY (`WorkItemId`) REFERENCES `WorkItems` (`Id`) ON DELETE CASCADE
);

CREATE UNIQUE INDEX `IX_AppUsers_Issuer_Subject` ON `AppUsers` (`Issuer`, `Subject`);

CREATE INDEX `IX_EpicDocuments_EpicId_CreatedAtUtc` ON `EpicDocuments` (`EpicId`, `CreatedAtUtc`);

CREATE INDEX `IX_Epics_ProjectId_Name` ON `Epics` (`ProjectId`, `Name`);

CREATE INDEX `IX_PersonalAccessTokens_AppUserId` ON `PersonalAccessTokens` (`AppUserId`);

CREATE UNIQUE INDEX `IX_PersonalAccessTokens_TokenHash` ON `PersonalAccessTokens` (`TokenHash`);

CREATE INDEX `IX_PersonalAccessTokens_TokenPrefix` ON `PersonalAccessTokens` (`TokenPrefix`);

CREATE UNIQUE INDEX `IX_Projects_Key` ON `Projects` (`Key`);

CREATE INDEX `IX_WorkItemComments_WorkItemId_CreatedAtUtc` ON `WorkItemComments` (`WorkItemId`, `CreatedAtUtc`);

CREATE INDEX `IX_WorkItems_EpicId` ON `WorkItems` (`EpicId`);

CREATE INDEX `IX_WorkItems_ProjectId_Status_Order` ON `WorkItems` (`ProjectId`, `Status`, `Order`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20260425232800_InitialMariaDbSchema', '10.0.6');

COMMIT;

