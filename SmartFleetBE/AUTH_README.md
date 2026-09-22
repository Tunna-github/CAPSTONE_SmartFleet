# SmartFleet Authentication / RBAC

## Demo accounts

All three accounts use the development password `SmartFleet@123`.

| Username | Email | Role |
|---|---|---|
| operator | operator@smartfleet.local | WAREHOUSE_OPERATOR |
| admin | admin@smartfleet.local | ADMIN |
| maintenance | maintenance@smartfleet.local | MAINTENANCE_TECHNICIAN |

Run `Database/SeedAuthUsers.sql` after the existing SmartFleet database schema has been created.

## Login

`POST /api/v1/auth/login`

```json
{
  "usernameOrEmail": "admin",
  "password": "SmartFleet@123"
}
```

The API returns a JWT access token containing one `ClaimTypes.Role` claim for every role assigned to the user.

Send the token to protected endpoints as:

`Authorization: Bearer <access-token>`

## RBAC examples

- `GET /api/v1/access-test/authenticated` -> any authenticated user
- `GET /api/v1/access-test/admin` -> ADMIN only
- `GET /api/v1/access-test/operator` -> WAREHOUSE_OPERATOR only
- `GET /api/v1/access-test/maintenance` -> MAINTENANCE_TECHNICIAN only

Use the same pattern on real controllers:

```csharp
[Authorize(Roles = AppRoles.Admin)]
```

or allow multiple roles:

```csharp
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.WarehouseOperator)]
```

## Important

The JWT key and the demo password are development values. Move the JWT key to user-secrets/environment variables and replace demo passwords before deployment.
