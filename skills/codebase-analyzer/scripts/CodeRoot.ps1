# Resolução da raiz do checkout do Code, compartilhada por find_declarations.ps1, summarize_file.ps1 e
# hooks\session-start.ps1 (dot-source: . "$PSScriptRoot\CodeRoot.ps1"). Nada aqui conhece caminho de máquina:
# o checkout é reconhecido pela forma (Applications\ e Components\ lado a lado), e quem abre sessão fora dele
# aponta a raiz por variável de ambiente.

# O ancestral mais próximo de $From (inclusive) que tem Applications\ e Components\. $null se não há.
# Worktrees do Code também têm os dois diretórios, então cada worktree resolve para si mesma.
function Find-CodeAncestor([string]$From = (Get-Location).Path) {
    $dir = $From
    while ($dir) {
        if ((Test-Path (Join-Path $dir 'Applications') -PathType Container) -and
            (Test-Path (Join-Path $dir 'Components') -PathType Container)) {
            return $dir
        }
        $dir = Split-Path $dir -Parent
    }
    return $null
}

# A raiz para consultas: o ancestral acima ou, fora de um checkout, MC_CODE_ROOT. $null quando não há nenhum
# dos dois — o chamador decide a mensagem.
function Find-CodeRoot([string]$From = (Get-Location).Path) {
    $ancestor = Find-CodeAncestor $From
    if ($ancestor) { return $ancestor }
    if ($env:MC_CODE_ROOT -and (Test-Path $env:MC_CODE_ROOT -PathType Container)) {
        return (Resolve-Path $env:MC_CODE_ROOT).Path
    }
    return $null
}

# Onde o plugin age (roteamento no início da sessão e, depois, o hook PreToolUse): dentro de um checkout do Code
# ou sob um dos diretórios de MC_HOOK_ROOTS (separados por ';'). MC_CODE_ROOT não conta: ele diz onde consultar,
# não onde interferir — o plugin fica instalado no nível de usuário e não pode atrapalhar outros projetos.
function Test-CodeScope([string]$Path = (Get-Location).Path) {
    if (Find-CodeAncestor $Path) { return $true }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    foreach ($root in ($env:MC_HOOK_ROOTS -split ';' | Where-Object { $_ })) {
        $rootFull = [IO.Path]::GetFullPath($root.Trim()).TrimEnd('\', '/')
        if ($full.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

# Armazém do índice: MC_CODEINDEX ou %LOCALAPPDATA%\mc-code-intelligence\index — o mesmo padrão do DeclIndex
# (Program.cs). Fora da pasta do plugin porque a atualização do plugin troca a pasta versionada.
function Get-CodeIndexStore {
    if ($env:MC_CODEINDEX) { return $env:MC_CODEINDEX }
    return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'mc-code-intelligence\index'
}
