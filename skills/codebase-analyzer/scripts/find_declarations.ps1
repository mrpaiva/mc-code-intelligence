<#
.SYNOPSIS
    Consulta o índice de declarações C#: quem declara, quem herda, quem tem atributo, membros de um tipo.
.DESCRIPTION
    Referência ("onde X é usado", "quem chama X") continua no find_usages.ps1. Este script responde só declaração.
    Resolve a raiz pelo diretório atual (ver .NOTES), atualiza o índice (DeclIndex refresh, incremental por
    SHA de blob) e consulta o TSV materializado com rg. O armazém é compartilhado por todas as worktrees em
    %LOCALAPPDATA%\mc-code-intelligence\index (ou $env:MC_CODEINDEX).
    Código gerado (GeneratedCode, *.Designer.cs, *.g.cs, Reference.cs) sai da resposta; -IncludeGenerated traz de volta.
    Atenção com tipo parcial de WinForms: a metade no *.Designer.cs (InitializeComponent, campos dos controles)
    só aparece com -IncludeGenerated.
    -NoRefresh consulta o TSV já materializado sem passar pelo git (mais rápido; pode estar defasado).
    -Name aceita curinga (* e ?): -Name *Controller lista quem termina em Controller; sem curinga é nome exato.
    Tipo genérico se pede sem a aridade, em -Name e em -Container: -Container OrderController acha os membros
    de OrderController`1 (o TSV guarda o nome com a aridade).
    Colunas do TSV: kind, name, container, modifiers, attributes, bases, signature, line, file, project.
    A linha é a do identificador (a de "class X"/"void M("), não a do atributo que o precede.
.EXAMPLE
    .\find_declarations.ps1 -Name Save                                  quem declara Save (não quem chama)
    .\find_declarations.ps1 -Name *Facility* -Kind class                tipos com Facility no nome
    .\find_declarations.ps1 -Base ServiceBase -Kind class               quem herda de ServiceBase
    .\find_declarations.ps1 -Attribute MessageContract -IncludeGenerated   tipos com o atributo
    .\find_declarations.ps1 -Container MultiVendas.Services.GuardService   membros do tipo, todos os arquivos (partial)
    .\find_declarations.ps1 -File Tef\Service.cs                        o que o arquivo declara e a que projeto pertence
    .\find_declarations.ps1 -Name Save -Project MultiVendas.Core -Raw   linhas TSV cruas, para encadear
    .\find_declarations.ps1 -Kind enummember -File Applications/MultiVendas/ -GroupBy container   enums por número de membros
    .\find_declarations.ps1 -Kind class -File Applications/MultiVendas/ -GroupBy project           classes por projeto
.NOTES
    -GroupBy container|file|project|kind|name devolve contagens (maior primeiro) em vez das declarações;
    com -Raw sai "contagem<TAB>chave".
    Códigos de saída: 0 achou, 1 erro de uso ou de ferramenta, 3 nada encontrado.
    A raiz é o ancestral mais próximo do diretório atual que tem Applications\ e Components\ (um checkout ou
    worktree do Code). Fora de um, usa $env:MC_CODE_ROOT; sem os dois, erro. -Root explícito prevalece. A primeira
    linha da saída (exceto com -Raw) diz qual raiz foi usada.
    Funciona por caminho absoluto quando o profile não carrega (-NoProfile).
#>
param(
    [string]$Name,
    # [string[]] porque no PowerShell "-Kind class,enum" sem aspas chega como array; com [string] vira "class enum".
    [string[]]$Kind,
    [string]$Container,
    [string]$Base,
    [string]$Attribute,
    [string]$File,
    [string]$Project,
    [switch]$IncludeGenerated,
    [switch]$Raw,
    [ValidateSet('container', 'file', 'project', 'kind', 'name')]
    [string]$GroupBy,
    [string]$Root,
    [switch]$NoRefresh,
    [int]$MaxResults = 200
)

$ErrorActionPreference = 'Stop'

# ---------- Validação ----------
if (-not ($Name -or $Kind -or $Container -or $Base -or $Attribute -or $File -or $Project)) {
    Write-Host "Erro: informe ao menos um filtro (-Name, -Kind, -Container, -Base, -Attribute, -File, -Project)."
    exit 1
}
if (-not (Get-Command rg -ErrorAction SilentlyContinue)) {
    Write-Host "Erro: ripgrep (rg) não encontrado no PATH."
    exit 1
}

# ---------- Worktree ----------
. (Join-Path $PSScriptRoot "CodeRoot.ps1")
if (-not $Root) {
    $Root = Find-CodeRoot
    if (-not $Root) {
        Write-Host "Erro: o diretório atual não está num checkout do Code (Applications\ e Components\). Informe -Root ou defina MC_CODE_ROOT."
        exit 1
    }
}
if (-not (Test-Path $Root)) {
    Write-Host "Erro: raiz não encontrada: $Root"
    exit 1
}
$Root = (Resolve-Path $Root).Path
if (-not $Raw) { Write-Host "Raiz: $Root" }

# ---------- Indexador ----------
$exe = Join-Path $PSScriptRoot "DeclIndex\DeclIndex\bin\Release\net10.0\DeclIndex.exe"
if (-not (Test-Path $exe)) {
    dotnet build (Join-Path $PSScriptRoot "DeclIndex\DeclIndex.slnx") -c Release --nologo -v q | Out-Null
    if (-not (Test-Path $exe)) {
        Write-Host "Erro: não foi possível compilar o DeclIndex (dotnet build em $PSScriptRoot\DeclIndex)."
        exit 1
    }
}
$store = Get-CodeIndexStore
# Mesma chave que WorktreeIndex.KeyFor: caminho completo com ':', '\' e '/' trocados por '_'.
$tsv = Join-Path $store ("worktrees\" + ($Root.TrimEnd('\', '/') -replace '[:\\/]', '_') + ".tsv")
if (-not $NoRefresh -or -not (Test-Path $tsv)) {
    $tsv = & $exe refresh --worktree $Root --store $store --quiet 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $tsv -or -not (Test-Path $tsv)) {
        Write-Host "Erro: DeclIndex refresh falhou para $Root (a raiz é uma worktree git?)."
        exit 1
    }
}

# ---------- Consulta ----------
# Um filtro por coluna, todos combinados num único regex (lookaheads ancorados) para uma só passada do rg.
function Column([int]$index, [string]$pattern) { "(?=^(?:[^\t]*\t){$index}(?:$pattern)(?:\t|$))" }

$filters = @()
if ($Name)      { $filters += Column 1 (([regex]::Escape($Name) -replace '\\\*', '[^\t]*' -replace '\\\?', '[^\t]') + '(?:`\d+)?') }
if ($Container) { $filters += Column 2 ('(?:[^\t]*\.)?' + [regex]::Escape($Container) + '(?:`\d+)?') }
if ($File)      { $filters += Column 8 ('[^\t]*' + [regex]::Escape(($File -replace '\\', '/')) + '[^\t]*') }
if ($Project)   { $filters += Column 9 ([regex]::Escape($Project)) }
if ($Kind)      { $filters += Column 0 (($Kind -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [regex]::Escape($_) }) -join '|') }
if ($Base)      { $filters += Column 5 ('(?:[^\t]*[,.])?' + [regex]::Escape($Base) + '(?:<[^\t]*)?(?:,[^\t]*)?') }
if ($Attribute) { $filters += Column 4 ('(?:[^\t]*[,.])?' + [regex]::Escape($Attribute) + '(?:Attribute)?(?:,[^\t]*)?') }

$lines = @(& rg -N -P ('^' + ($filters -join '')) $tsv 2>$null)
if (-not $IncludeGenerated) {
    $lines = @($lines | Where-Object {
        $_ -notmatch '(^|[\t,.])GeneratedCode(Attribute)?([,\t]|$)' -and
        $_ -notmatch '\.(?i:designer)\.cs\t' -and
        $_ -notmatch '\.g(\.i)?\.cs\t' -and
        $_ -notmatch '(^|/)Reference\.cs\t'
    })
}

# ---------- Saída ----------
if ($lines.Count -eq 0) {
    Write-Host "Nenhuma declaração encontrada."
    exit 3
}

# Contagem por grupo: "quais enums têm mais membros" vira uma chamada (-Kind enummember -GroupBy container).
if ($GroupBy) {
    $columns = @{ kind = 0; name = 1; container = 2; project = 9; file = 8 }
    $index = $columns[$GroupBy.ToLower()]
    # @(): com um só grupo o Sort-Object devolve o GroupInfo, e .Count viraria o tamanho do grupo, não 1.
    $groups = @($lines | ForEach-Object { ($_ -split "`t")[$index] } | Group-Object | Sort-Object Count, Name -Descending)
    if ($Raw) {
        $groups | ForEach-Object { Write-Output "$($_.Count)`t$($_.Name)" }
        exit 0
    }
    $groups | Select-Object -First $MaxResults | ForEach-Object { Write-Host ("  {0,6}  {1}" -f $_.Count, $_.Name) }
    Write-Host ""
    Write-Host "Total: $($lines.Count) declaração(ões) em $($groups.Count) $GroupBy(s)"
    if ($groups.Count -gt $MaxResults) { Write-Host "Mostrando $MaxResults grupos. Refine com -Kind, -Project, -Container ou -File." }
    exit 0
}

if ($Raw) {
    $lines | ForEach-Object { Write-Output $_ }
    exit 0
}

$rows = $lines | ForEach-Object {
    $c = $_ -split "`t"
    [PSCustomObject]@{ Kind = $c[0]; Name = $c[1]; Container = $c[2]; Modifiers = $c[3]; Attributes = $c[4]; Bases = $c[5]; Signature = $c[6]; Line = [int]$c[7]; File = $c[8]; Project = $c[9] }
}
# "[DBTable] FacilityRent : BaseIdentifiableObject<FacilityRent>, ICloneable": o TSV separa bases e atributos por
# vírgula sem espaço (a vírgula com espaço é de argumento genérico), então só essa vira ", ".
function Format-Declaration($row) {
    $declaration = $row.Name
    if ($row.Bases) { $declaration += " : " + ($row.Bases -replace ',(?! )', ', ') }
    if ($row.Attributes) { $declaration = "[" + ($row.Attributes -replace ',(?! )', ', ') + "] " + $declaration }
    return $declaration
}
$shown = 0
foreach ($group in ($rows | Group-Object File)) {
    if ($shown -ge $MaxResults) { break }
    Write-Host ""
    Write-Host "=== $($group.Name)  [$($group.Group[0].Project)]"
    foreach ($row in ($group.Group | Sort-Object Line)) {
        if ($shown -ge $MaxResults) { break }
        Write-Host ("  linha {0,-5} {1,-10} {2,-18} {3}  {4}   em {5}" -f $row.Line, $row.Kind, $row.Modifiers, (Format-Declaration $row), $row.Signature, $row.Container)
        $shown++
    }
}
Write-Host ""
Write-Host "Total: $($rows.Count) declaração(ões) em $(@($rows.File | Select-Object -Unique).Count) arquivo(s)"
if ($rows.Count -gt $MaxResults) { Write-Host "Mostrando $MaxResults. Refine com -Kind, -Project, -Container ou -File." }
exit 0
