$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Test-Path (Join-Path $root '.env'))) {
    throw 'Нет .env. Скопируй .env.example в .env и укажи POSTGRES_PASSWORD.'
}
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) { throw 'Podman CLI не найден в PATH.' }
Push-Location $root
try {
    podman compose --env-file .env -f infrastructure/compose.yaml up -d postgres
    if ($LASTEXITCODE -ne 0) { throw 'podman compose up завершился ошибкой.' }
    Write-Host 'PostgreSQL запрошен к запуску. Проверь состояние и готовность: .\scripts\podman-status.ps1'
} finally { Pop-Location }
