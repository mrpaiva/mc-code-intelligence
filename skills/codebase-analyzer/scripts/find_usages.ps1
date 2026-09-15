<#
.SYNOPSIS
    Encontra referências a um símbolo (word boundary), saída compacta agrupada por arquivo.
.DESCRIPTION
    Varre todo arquivo de texto (o rg pula binário sozinho): .cs, .config, .resx, .xaml, .sql de migration,
    .yml, .md. É o que torna seguro trocar um grep cru por este script — o que o grep acharia, ele acha.
    Restrinja com -Include quando o ruído incomodar (ex.: -Include *.cs).
    Quando executado sem 'rg' (ripgrep) instalado, limite o escopo com -Path apontando
    para a pasta mais específica possível (ex: Applications\MultiVendasPos) para evitar
    varreduras lentas.
.EXAMPLE
    .\find_usages.ps1 GetTotal
    .\find_usages.ps1 GetTotal -Path "Applications\MultiVendasPos"
    .\find_usages.ps1 IMemberService -Path "Components"
    .\find_usages.ps1 DefaultConnectionString -Include *.config
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Symbol,

    [string]$Path = (Get-Location),

    [string[]]$Include = @("*"),

    [int]$Depth = 0  # 0 = ilimitado
)

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

# ---------- Busca ----------
if (Get-Command rg -ErrorAction SilentlyContinue) {
    $globs = @()
    foreach ($ext in $Include) { $globs += "--glob"; $globs += $ext }
    foreach ($dir in $excludeDirs) { $globs += "--glob"; $globs += "!$dir/" }

    Push-Location $resolvedPath
    try {
        rg --word-regexp --line-number --no-heading $Symbol @globs |
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
