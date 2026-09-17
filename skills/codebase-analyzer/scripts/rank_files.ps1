<#
.SYNOPSIS
    Ranqueia os arquivos da worktree por relevância para um conjunto de termos (BM25 sobre o corpus).
.DESCRIPTION
    Para "onde vive a lógica de X" quando X são várias palavras e o find_usages devolveria centenas de arquivos
    sem ordem. Cada termo é palavra inteira, literal e sensível a caixa (passe as variantes que quiser:
    "voucher Voucher"); o arquivo que tem mais termos, mais vezes e é mais curto vem primeiro. Cobre todo texto
    da worktree (.cs, .config, .resx, .xaml, .sql, .md, .js...), como o find_usages, e aceita os mesmos -Path e
    -Include. Só funciona dentro de um checkout do Code (ou com $env:MC_CODE_ROOT); a primeira chamada de cada
    checkout materializa índice e corpus (40 a 70 s + ~12 s, uma vez por máquina).
.EXAMPLE
    .\rank_files.ps1 Voucher Cancel Reschedule
    .\rank_files.ps1 Voucher Cancel -Top 10 -Include *.cs
    .\rank_files.ps1 CardToken Cielo -Path Applications\Cloud
.NOTES
    Saída: score, caminho relativo à raiz, termos casados e as primeiras linhas de cada acerto.
    -NoRefresh consulta corpus e manifesto já materializados sem passar pelo git (pode estar defasado).
    Códigos de saída: 0 achou, 1 erro de uso ou de ferramenta, 3 nada encontrado.
#>
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Terms,

    [string]$Path = (Get-Location),

    [string[]]$Include = @("*"),

    [int]$Top = 20,

    [switch]$NoRefresh
)

. "$PSScriptRoot\CodeRoot.ps1"

$resolvedPath = Resolve-Path $Path -ErrorAction SilentlyContinue
if (-not $resolvedPath) {
    Write-Host "Erro: caminho não encontrado: $Path"
    exit 1
}

$root = Find-CodeAncestor $resolvedPath.Path
if (-not $root -and -not $PSBoundParameters.ContainsKey('Path')) { $root = Find-CodeRoot }
if (-not $root) {
    Write-Host "Erro: fora de um checkout do Code (Applications\ e Components\ lado a lado) e sem MC_CODE_ROOT."
    exit 1
}

$exe = Join-Path $PSScriptRoot "DeclIndex\DeclIndex\bin\Release\net10.0\DeclIndex.exe"
if (-not (Test-Path $exe)) {
    dotnet build (Join-Path $PSScriptRoot "DeclIndex\DeclIndex.slnx") -c Release --nologo -v q | Out-Null
    if (-not (Test-Path $exe)) {
        Write-Host "Erro: não foi possível compilar o DeclIndex (dotnet build em $PSScriptRoot\DeclIndex)."
        exit 1
    }
}

$arguments = @("search", "--worktree", $root, "--store", (Get-CodeIndexStore), "--top", $Top)
foreach ($term in $Terms) { $arguments += @("--term", $term) }
$scope = if ($resolvedPath.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $resolvedPath.Path.Substring($root.Length).Trim('\', '/') } else { "" }
if ($scope) { $arguments += @("--scope", $scope) }
foreach ($glob in $Include) { if ($glob -ne "*") { $arguments += @("--include", $glob) } }
if ($NoRefresh) { $arguments += "--no-refresh" }

$lines = @(& $exe @arguments 2>$null)
if ($LASTEXITCODE -ne 0) {
    Write-Host "Erro: DeclIndex search falhou para $root (a raiz é uma worktree git?)."
    exit 1
}

Write-Host "Raiz: $root"
if ($lines.Count -eq 0) {
    Write-Host "Nenhum arquivo com os termos: $($Terms -join ', ')"
    exit 3
}

foreach ($line in $lines) {
    $columns = $line -split "`t"
    if ($columns.Count -lt 4) { continue }
    $hits = $columns[3] -split ','
    $shown = ($hits | Select-Object -First 8) -join ', '
    if ($hits.Count -gt 8) { $shown += " (+$($hits.Count - 8))" }
    Write-Host ("  {0,7}  {1}  [{2}]  linhas {3}" -f $columns[1], $columns[0], $columns[2], $shown)
}
