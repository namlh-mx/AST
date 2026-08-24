# Applies migrations/V*.sql in filename order, which is the order they must run in.
#
# The application NEVER migrates its own database. It verifies the schema version at
# startup and blocks with a readable message on a mismatch, so this script is not
# optional -- it is how the database reaches a version the application will accept.
#
# Defaults match docker-compose.yml.
#
#   .\scripts\apply-migrations.ps1 [-DbHost x] [-Port n] [-User u] [-Password p] [-Database d]
#
param(
  [string]$DbHost   = "127.0.0.1",
  [int]   $Port     = 3306,
  [string]$User     = "ast",
  [string]$Password = "ast-dev-only",
  [string]$Database = "ast_db"
)

$ErrorActionPreference = "Stop"
$migrations = Join-Path $PSScriptRoot ".." "migrations"

$files = Get-ChildItem -Path $migrations -Filter "V*.sql" | Sort-Object Name
if ($files.Count -eq 0) {
  throw "No migration scripts found in $migrations"
}

foreach ($f in $files) {
  Write-Output "Applying $($f.Name) ..."
  Get-Content -Raw -Encoding UTF8 $f.FullName |
    & mysql --host=$DbHost --port=$Port --user=$User --password=$Password `
            --default-character-set=utf8mb4 $Database
  if ($LASTEXITCODE -ne 0) {
    throw "Migration $($f.Name) failed with exit code $LASTEXITCODE."
  }
}

Write-Output ""
Write-Output "Schema version is now:"
& mysql --host=$DbHost --port=$Port --user=$User --password=$Password `
        -N -B $Database -e "SELECT MAX(version) FROM schema_version;"
