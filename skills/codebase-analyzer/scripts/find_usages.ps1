<#
.SYNOPSIS
    Encontra referências a um símbolo (palavra inteira), saída compacta agrupada por arquivo.
.DESCRIPTION
    Dentro de um checkout do Code, consulta o corpus do DeclIndex (DeclIndex usages): o texto de todo arquivo
    rastreado ou não ignorado da worktree, até 1 MB, exceto binário — .cs, .config, .resx, .xaml, .sql, .yml,
    .md, .js, .csproj... O corpus é compartilhado entre worktrees e atualizado pelo mesmo refresh incremental
    do find_declarations; a resposta é filtrada pelo manifesto da worktree, então arquivo modificado sem commit
    é consultado no conteúdo novo. O símbolo é literal (não regex); para regex, contexto ou -i, use o Grep.
    Fora de um checkout, varre a árvore com rg (ou Get-ChildItem, lento) como sempre fez.
    Restrinja com -Include quando o ruído incomodar (ex.: -Include *.cs) e com -Path para uma subpasta.
    As pastas node_modules, bin, obj, dist, .git, packages, .vs, .vscode e publish ficam de fora nos dois modos.
    -NoRefresh consulta o corpus e o manifesto já materializados sem passar pelo git (pode estar defasado).
.EXAMPLE
    .\find_usages.ps1 GetTotal
    .\find_usages.ps1 GetTotal -Path "Applications\MultiVendasPos"
    .\find_usages.ps1 IMemberService -Path "Components"
    .\find_usages.ps1 DefaultConnectionString -Include *.config
.NOTES
    A raiz é o ancestral mais próximo de -Path que tem Applications\ e Components\; sem -Path e fora de um
    checkout, vale $env:MC_CODE_ROOT. Os caminhos saem relativos à raiz, com barra normal.
    Códigos de saída: 0 (mesmo sem referência), 1 erro de uso ou de ferramenta.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Symbol,

    [string]$Path = (Get-Location),

    [string[]]$Include = @("*"),

    [int]$Depth = 0,  # 0 = ilimitado (só o fallback Get-ChildItem)

    [switch]$NoRefresh
)

. "$PSScriptRoot\CodeRoot.ps1"

# ---------- Validação ----------
$resolvedPath = Resolve-Path $Path -ErrorAction SilentlyContinue
if (-not $resolvedPath) {
    Write-Host "Erro: caminho não encontrado: $Path"
    exit 1
}

# ---------- Configuração ----------
$excludeDirs = @("node_modules", "bin", "obj", "dist", ".git", "packages", ".vs", ".vscode", "publish")
$excludePattern = ($excludeDirs | ForEach-Object { [regex]::Escape($_) }) -join "|"

$results = [System.Collections.Generic.List[PSCustomObject]]::new()

# ---------- Raiz ----------
# -Path dentro de um checkout manda; sem -Path explícito e fora de um, MC_CODE_ROOT (como o find_declarations).
$root = Find-CodeAncestor $resolvedPath.Path
if (-not $root -and -not $PSBoundParameters.ContainsKey('Path')) { $root = Find-CodeRoot }

# ---------- Busca ----------
if ($root) {
    $exe = Join-Path $PSScriptRoot "DeclIndex\DeclIndex\bin\Release\net10.0\DeclIndex.exe"
    if (-not (Test-Path $exe)) {
        dotnet build (Join-Path $PSScriptRoot "DeclIndex\DeclIndex.slnx") -c Release --nologo -v q | Out-Null
        if (-not (Test-Path $exe)) {
            Write-Host "Erro: não foi possível compilar o DeclIndex (dotnet build em $PSScriptRoot\DeclIndex)."
            exit 1
        }
    }

    $arguments = @("usages", "--worktree", $root, "--symbol", $Symbol, "--store", (Get-CodeIndexStore))
    $scope = if ($resolvedPath.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $resolvedPath.Path.Substring($root.Length).Trim('\', '/') } else { "" }
    if ($scope) { $arguments += @("--scope", $scope) }
    foreach ($glob in $Include) { if ($glob -ne "*") { $arguments += @("--include", $glob) } }
    if ($NoRefresh) { $arguments += "--no-refresh" }

    $lines = @(& $exe @arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Erro: DeclIndex usages falhou para $root (a raiz é uma worktree git?)."
        exit 1
    }
    foreach ($line in $lines) {
        $tab = $line.IndexOf("`t")
        if ($tab -gt 0) { $results.Add([PSCustomObject]@{ File = $line.Substring(0, $tab); Line = [int]$line.Substring($tab + 1) }) }
    }
}
elseif (Get-Command rg -ErrorAction SilentlyContinue) {
    $globs = @()
    foreach ($ext in $Include) { $globs += "--glob"; $globs += $ext }
    foreach ($dir in $excludeDirs) { $globs += "--glob"; $globs += "!$dir/" }

    Push-Location $resolvedPath
    try {
        rg --word-regexp --fixed-strings --line-number --no-heading $Symbol @globs |
            ForEach-Object {
                if ($_ -match '^(.+):(\d+):') {
                    $results.Add([PSCustomObject]@{ File = $Matches[1]; Line = [int]$Matches[2] })
                }
            }
    } finally {
        Pop-Location
    }
}
else {
    # Fallback PowerShell — lento em árvores grandes. Use -Depth ou -Path específico.
    $gciParams = @{
        Path        = $resolvedPath
        Recurse     = $true
        Include     = $Include
        Force       = $true
        ErrorAction = 'SilentlyContinue'
    }
    if ($Depth -gt 0) { $gciParams['Depth'] = $Depth }

    Get-ChildItem @gciParams |
        Where-Object { $_.FullName -notmatch $excludePattern } |
        Select-String -Pattern "\b$([regex]::Escape($Symbol))\b" |
        ForEach-Object {
            $results.Add([PSCustomObject]@{ File = $_.Path; Line = $_.LineNumber })
        }
}

# ---------- Saída ----------
if ($results.Count -eq 0) {
    Write-Host "Nenhuma referência encontrada para: $Symbol"
    exit 0
}

$results | Group-Object File | ForEach-Object {
    Write-Host ""
    Write-Host "=== $($_.Name) ==="
    $_.Group | ForEach-Object { Write-Host "  linha $($_.Line)" }
}

$fileCount = ($results | Select-Object -ExpandProperty File -Unique | Measure-Object).Count
Write-Host ""
Write-Host "Total: $($results.Count) referência(s) em $fileCount arquivo(s)"
