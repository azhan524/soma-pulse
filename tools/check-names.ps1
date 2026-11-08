$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$expected = @('ahz-scribe-brain', 'ahz-scribe-body', 'ahz-scribe-face')
$bad = 0

function Fail($m) {
    Write-Host "fail: $m" -ForegroundColor Red
    $script:bad++
}

Write-Host "root: $root"

# 1. 模块目录必须存在，名字必须完全一致
foreach ($name in $expected) {
    $p = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $p -PathType Container)) { Fail "missing directory: $name" }
    if ($name -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { Fail "not kebab-case: $name" }
}

# 2. 模块目录里不允许出现大写/下划线的子目录名
foreach ($name in $expected) {
    $p = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $p)) { continue }
    Get-ChildItem -LiteralPath $p -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)(\\|$)|\\(_body-data|_face-data|_brain-data|_sandbox[^\\]*)(\\|$)' } | ForEach-Object {
        if ($_.Name -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
            Fail "bad directory name: $($_.FullName)"
        }
    }
}

# 3. compose 的服务名/容器名必须复用同一套名字
$compose = Join-Path $root 'docker-compose.yml'
if (-not (Test-Path -LiteralPath $compose)) { Fail 'missing docker-compose.yml' }
else {
    $text = Get-Content -LiteralPath $compose -Raw
    $services = [regex]::Matches($text, '(?m)^  (ahz-scribe-[a-z0-9-]+):\s*$') |
        ForEach-Object { $_.Groups[1].Value }
    $containers = [regex]::Matches($text, '(?m)^\s+container_name:\s*(\S+)\s*$') |
        ForEach-Object { $_.Groups[1].Value }
    foreach ($name in $expected) {
        if ($services -notcontains $name) { Fail "compose service missing: $name" }
        if ($containers -notcontains $name) { Fail "container_name missing: $name" }
    }
    foreach ($s in $services) { if ($s -cnotmatch '^ahz-scribe-[a-z0-9-]+$') { Fail "bad service: $s" } }
    foreach ($c in $containers) { if ($c -cnotmatch '^ahz-scribe-[a-z0-9-]+$') { Fail "bad container: $c" } }
}

# 4. .gitmodules 的路径/名字必须一致
$gm = Join-Path $root '.gitmodules'
if (-not (Test-Path -LiteralPath $gm)) { Fail 'missing .gitmodules' }
else {
    $text = Get-Content -LiteralPath $gm -Raw
    foreach ($name in $expected) {
        if ($text -notmatch ('\[submodule "' + [regex]::Escape($name) + '"\]')) {
            Fail "submodule missing: $name"
        }
        if ($text -notmatch ('(?m)^\s*path\s*=\s*' + [regex]::Escape($name) + '\s*$')) {
            Fail "submodule path missing: $name"
        }
    }
}

if ($bad -gt 0) {
    Write-Host "check-names: FAIL ($bad)" -ForegroundColor Red
    exit 1
}
Write-Host "check-names: PASS" -ForegroundColor Green
exit 0
