#!/usr/bin/env bash
#
# Applies migrations/V*.sql in filename order, which is the order they must run in.
#
# The application NEVER migrates its own database. It verifies the schema version at
# startup and blocks with a readable message on a mismatch, so this script is not
# optional -- it is how the database reaches a version the application will accept.
#
# Defaults match docker-compose.yml.
#
#   ./scripts/apply-migrations.sh [host] [port] [user] [password] [database]
#
set -euo pipefail

HOST="${1:-127.0.0.1}"
PORT="${2:-3306}"
USER="${3:-ast}"
PASS="${4:-ast-dev-only}"
DB="${5:-ast_db}"

if ! command -v mysql >/dev/null 2>&1; then
  cat >&2 <<'HELP'
The `mysql` client is not on PATH, and this script needs it.

Either install the MySQL client, or -- if you started the database with
docker compose -- apply the migrations through the container instead:

  for f in migrations/V*.sql; do
    docker exec -i ast-mysql mysql -uast -past-dev-only ast_db < "$f"
  done
HELP
  exit 1
fi

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../migrations" && pwd)"

shopt -s nullglob
FILES=("$DIR"/V*.sql)
if [ ${#FILES[@]} -eq 0 ]; then
  echo "No migration scripts found in $DIR" >&2
  exit 1
fi

# Sort by filename: V001, V002, ... The numbering IS the order.
IFS=$'\n' FILES=($(printf '%s\n' "${FILES[@]}" | sort)); unset IFS

for f in "${FILES[@]}"; do
  echo "Applying $(basename "$f") ..."
  mysql --host="$HOST" --port="$PORT" --user="$USER" --password="$PASS" \
        --default-character-set=utf8mb4 "$DB" < "$f"
done

echo
echo -n "Schema version is now: "
mysql --host="$HOST" --port="$PORT" --user="$USER" --password="$PASS" \
      -N -B "$DB" -e "SELECT MAX(version) FROM schema_version;"
