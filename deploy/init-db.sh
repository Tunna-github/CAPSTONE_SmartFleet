#!/bin/sh
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
until pg_isready -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB"; do sleep 2; done
# Fresh demo database only. A version marker makes subsequent `up` non-destructive.
if [ "$(psql -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT to_regclass('public.smartfleet_demo_bootstrap') IS NOT NULL")" = t ]; then
  echo 'SmartFleet demo database already initialized'; exit 0
fi
if [ "$(psql -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT to_regclass('public.\"Users\"') IS NOT NULL")" = t ]; then
  echo 'Refusing to initialize an existing non-demo database' >&2; exit 1
fi
psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<'SQL'
BEGIN;
\i /database/schema.sql
COMMIT;
\i /database/20261123_task_interventions.sql
\i /database/20261124_outbox_safety_commands.sql
\i /database/20261124_auth_uniqueness.sql
\i /database/20261124_mission_verification.sql
\i /deploy/seed-demo.sql
SQL
