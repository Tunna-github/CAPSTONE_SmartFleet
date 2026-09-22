# SmartFleet RESTful Authentication & RBAC

## REST endpoints

| Method | Endpoint | Access | Purpose |
|---|---|---|---|
| POST | `/api/v1/sessions` | Anonymous | Create an authenticated session / issue JWT |
| GET | `/api/v1/users/me` | Authenticated | Get current user |
| GET | `/api/v1/users` | ADMIN | List user accounts |
| GET | `/api/v1/transport-tasks` | ADMIN, WAREHOUSE_OPERATOR | Read transport tasks |
| GET | `/api/v1/maintenance-records` | ADMIN, MAINTENANCE_TECHNICIAN | Read maintenance records |

The previous RPC-style `/api/v1/auth/login` and `/api/v1/access-test/...` endpoints were removed.
Routes now use resource nouns and HTTP methods to express actions.

## Login

```http
POST /api/v1/sessions
Content-Type: application/json
```

```json
{
  "usernameOrEmail": "admin",
  "password": "SmartFleet@123"
}
```

A successful response returns a Bearer access token. Send it on protected APIs:

```http
Authorization: Bearer <access-token>
```

## Role boundaries currently enforced

- `ADMIN`: can list users; can also access transport-task and maintenance-record reads.
- `WAREHOUSE_OPERATOR`: can access transport-task reads but cannot list users or access maintenance records.
- `MAINTENANCE_TECHNICIAN`: can access maintenance-record reads but cannot list users or access transport tasks.
- All three authenticated roles can call `/api/v1/users/me`.

## HTTP status behavior

- `200 OK`: request succeeded.
- `400 Bad Request`: validation failed.
- `401 Unauthorized`: token missing/invalid/expired, or login failed.
- `403 Forbidden`: token is valid but the user's role is not allowed.
- `404 Not Found`: requested resource does not exist.

## Development users

Run `Database/SeedAuthUsers.sql` once against `SmartFleetDB`.

- `admin` / `SmartFleet@123`
- `operator` / `SmartFleet@123`
- `maintenance` / `SmartFleet@123`

Change these passwords/secrets outside development.
