-- Runs once, on the container's first start (docker-entrypoint-initdb.d).
--
-- MYSQL_DATABASE creates only `ast_db`. The integration tests need a SECOND database,
-- because they drop every table on each run and must never touch the application's own.
CREATE DATABASE IF NOT EXISTS ast_test
  DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- One development account with access to both. Matches mysql.secrets.sample.json.
CREATE USER IF NOT EXISTS 'ast'@'%' IDENTIFIED BY 'ast-dev-only';
GRANT ALL PRIVILEGES ON ast_db.*   TO 'ast'@'%';
GRANT ALL PRIVILEGES ON ast_test.* TO 'ast'@'%';
FLUSH PRIVILEGES;
