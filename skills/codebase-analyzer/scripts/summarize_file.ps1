<#
.SYNOPSIS
    Exibe as primeiras N e últimas M linhas de um arquivo para decisão rápida antes de ler tudo.
.DESCRIPTION
    Resolve caminhos absolutos, relativos ao diretório atual e relativos à raiz do Code (o ancestral do diretório
    atual com Applications\ e Components\, ou MC_CODE_ROOT; -RepoRoots substitui).
    Se não encontrar diretamente, busca pelo nome do arquivo em toda a raiz como último recurso.
.EXAMPLE
    .\summarize_file.ps1 CLAUDE.md
    .\summarize_file.ps1 "Applications\MultiVendasPos\Sources\MultiVendas.Pos\SellerExtensions.cs"
    .\summarize_file.ps1 "SellerExtensions.cs"  # busca pelo nome em todo o repo
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string[]]$RepoRoots = @(),

    [int]$HeadLines = 40,
    [int]$TailLines = 20
)

# ---------- Resolução de caminho ----------
if (-not $RepoRoots) {
    . (Join-Path $PSScriptRoot "CodeRoot.ps1")
    $codeRoot = Find-CodeRoot
    if ($codeRoot) { $RepoRoots = @($codeRoot) }
}

$resolved = $null

if ([System.IO.Path]::IsPathRooted($Path)) {
    $resolved = $Path
}
elseif (Test-Path $Path) {
    $resolved = (Resolve-Path $Path).Path
}
else {
    # Tenta prefixar com repo roots
    foreach ($root in $RepoRoots) {
        $candidate = Join-Path $root $Path
        if (Test-Path $candidate) {
            $resolved = $candidate
            break
        }
    }

    # Último recurso: buscar pelo nome do arquivo em toda a árvore do primeiro repo root
    if (-not $resolved -and $RepoRoots.Count -gt 0 -and (Test-Path $RepoRoots[0])) {
        $leaf = Split-Path $Path -Leaf
        $found = @(Get-ChildItem -Path $RepoRoots[0] -Recurse -Filter $leaf -ErrorAction SilentlyContinue)

        if ($found.Count -eq 1) {
            $resolved = $found[0].FullName
        }
        elseif ($found.Count -gt 1) {
            Write-Host "Múltiplas ocorrências de '$leaf' encontradas:"
            for ($i = 0; $i -lt $found.Count; $i++) {
                Write-Host "  [$($i+1)] $($found[$i].FullName)"
            }
            Write-Host ""
            Write-Host "Passe um trecho do caminho que torne a busca única."
            exit 2
        }
    }
}

if (-not $resolved -or -not (Test-Path $resolved)) {
    Write-Host "Erro: arquivo não encontrado: $Path"
    exit 1
}

# ---------- Leitura ----------
$lines = Get-Content $resolved -ErrorAction Stop
$total = $lines.Count

Write-Host "=== $resolved ($total linhas) ==="
Write-Host ""

if ($total -le ($HeadLines + $TailLines)) {
    $lines | ForEach-Object { Write-Host $_ }
}
else {
    $lines[0..($HeadLines - 1)] | ForEach-Object { Write-Host $_ }
    $omitted = $total - $HeadLines - $TailLines
    Write-Host ""
    Write-Host "--- [ $omitted linhas omitidas ] ---"
    Write-Host ""
    $lines[($total - $TailLines)..($total - 1)] | ForEach-Object { Write-Host $_ }
}

exit 0
