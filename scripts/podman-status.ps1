$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    podman compose -f infrastructure/compose.yaml ps
    if ($LASTEXITCODE -ne 0) { throw 'podman compose ps завершился ошибкой.' }
    podman compose -f infrastructure/compose.yaml logs --tail 40 postgres
    if ($LASTEXITCODE -ne 0) { throw 'podman compose logs завершился ошибкой.' }
    Write-Host 'Проверка готовности PostgreSQL:'
    podman compose -f infrastructure/compose.yaml exec -T postgres sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL пока не готов принимать подключения.' }
} finally { Pop-Location }
