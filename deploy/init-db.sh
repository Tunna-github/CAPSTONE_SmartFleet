#!/bin/sh
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
until pg_isready -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB"; do sleep 2; done
finish_charging_schema() {
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /database/20261127_task_resume.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /database/20261127_cancelled_task_actions.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /database/20261127_charging_jobs.sql
  if [ "${COMMISSION_DUAL_LANE:-false}" = true ]; then
    psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /deploy/commission-dual-lane.sql
    psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /deploy/commission-transfer-areas.sql
  fi
}
# Fresh demo database only. A version marker makes subsequent `up` non-destructive.
if [ "$(psql -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT to_regclass('public.smartfleet_demo_bootstrap') IS NOT NULL")" = t ]; then
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /database/20261126_station_management.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /database/20261126_robot_registry.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /database/20261126_transfer_stations.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /deploy/commission-demo-qr-stations.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /deploy/rename-demo-transfer-points.sql
  psql -X -v ON_ERROR_STOP=1 -h database -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /deploy/unify-demo-verification-codes.sql
  finish_charging_schema
  echo 'SmartFleet demo database already initialized; additive catalog/registry migrations checked'; exit 0
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
\i /database/20261126_station_management.sql
\i /database/20261126_robot_registry.sql
\i /database/20261126_transfer_stations.sql
\i /deploy/seed-demo.sql
\i /deploy/commission-demo-qr-stations.sql
\i /deploy/rename-demo-transfer-points.sql
\i /deploy/unify-demo-verification-codes.sql
SQL
finish_charging_schema
