USE SmartFleetDB;
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

-- Development/demo password for all three accounts: SmartFleet@123
-- BCrypt cost 12. Change these passwords before any real deployment.
DECLARE @PasswordHash NVARCHAR(255) = N'$2y$12$67iSneGBAXEdmoCOtSbDGupSsD5G639p/7dC1d5VR8emFXnQCiiL6';

-- 1) Ensure the three roles exist.
IF NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = N'WAREHOUSE_OPERATOR')
    INSERT INTO Roles (RoleName, Description, CreatedAt)
    VALUES (N'WAREHOUSE_OPERATOR', N'Creates and manages warehouse transport operations.', @Now);

IF NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = N'ADMIN')
    INSERT INTO Roles (RoleName, Description, CreatedAt)
    VALUES (N'ADMIN', N'System administrator with management permissions.', @Now);

IF NOT EXISTS (SELECT 1 FROM Roles WHERE RoleName = N'MAINTENANCE_TECHNICIAN')
    INSERT INTO Roles (RoleName, Description, CreatedAt)
    VALUES (N'MAINTENANCE_TECHNICIAN', N'Monitors and performs robot maintenance activities.', @Now);

-- 2) Ensure the three demo users exist.
IF NOT EXISTS (SELECT 1 FROM Users WHERE Username = 'operator')
BEGIN
    INSERT INTO Users (Username, Email, PasswordHash, FullName, PhoneNumber, IsActive, CreatedAt, UpdatedAt)
    VALUES ('operator', 'operator@smartfleet.local', @PasswordHash, N'Warehouse Operator', NULL, 1, @Now, NULL);
END;

IF NOT EXISTS (SELECT 1 FROM Users WHERE Username = 'admin')
BEGIN
    INSERT INTO Users (Username, Email, PasswordHash, FullName, PhoneNumber, IsActive, CreatedAt, UpdatedAt)
    VALUES ('admin', 'admin@smartfleet.local', @PasswordHash, N'SmartFleet Administrator', NULL, 1, @Now, NULL);
END;

IF NOT EXISTS (SELECT 1 FROM Users WHERE Username = 'maintenance')
BEGIN
    INSERT INTO Users (Username, Email, PasswordHash, FullName, PhoneNumber, IsActive, CreatedAt, UpdatedAt)
    VALUES ('maintenance', 'maintenance@smartfleet.local', @PasswordHash, N'Maintenance Technician', NULL, 1, @Now, NULL);
END;

-- 3) Assign exactly the requested role to each demo account if missing.
DECLARE @OperatorUserId INT = (SELECT UserID FROM Users WHERE Username = 'operator');
DECLARE @AdminUserId INT = (SELECT UserID FROM Users WHERE Username = 'admin');
DECLARE @MaintenanceUserId INT = (SELECT UserID FROM Users WHERE Username = 'maintenance');

DECLARE @OperatorRoleId INT = (SELECT RoleID FROM Roles WHERE RoleName = N'WAREHOUSE_OPERATOR');
DECLARE @AdminRoleId INT = (SELECT RoleID FROM Roles WHERE RoleName = N'ADMIN');
DECLARE @MaintenanceRoleId INT = (SELECT RoleID FROM Roles WHERE RoleName = N'MAINTENANCE_TECHNICIAN');

IF NOT EXISTS (SELECT 1 FROM UserRoles WHERE UserID = @OperatorUserId AND RoleID = @OperatorRoleId)
    INSERT INTO UserRoles (UserID, RoleID, AssignedAt) VALUES (@OperatorUserId, @OperatorRoleId, @Now);

IF NOT EXISTS (SELECT 1 FROM UserRoles WHERE UserID = @AdminUserId AND RoleID = @AdminRoleId)
    INSERT INTO UserRoles (UserID, RoleID, AssignedAt) VALUES (@AdminUserId, @AdminRoleId, @Now);

IF NOT EXISTS (SELECT 1 FROM UserRoles WHERE UserID = @MaintenanceUserId AND RoleID = @MaintenanceRoleId)
    INSERT INTO UserRoles (UserID, RoleID, AssignedAt) VALUES (@MaintenanceUserId, @MaintenanceRoleId, @Now);

COMMIT TRANSACTION;
GO

SELECT
    u.UserID,
    u.Username,
    u.Email,
    u.FullName,
    u.IsActive,
    r.RoleName
FROM Users u
JOIN UserRoles ur ON ur.UserID = u.UserID
JOIN Roles r ON r.RoleID = ur.RoleID
WHERE u.Username IN ('operator', 'admin', 'maintenance')
ORDER BY u.UserID;
GO
