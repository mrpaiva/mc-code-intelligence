# SessionStart do plugin mc-code-intelligence. Faz duas coisas e sai:
#   1. dentro de um checkout do Code (ou de MC_HOOK_ROOTS), compila o DeclIndex se o executável ainda não
#      existe — uma vez por versão do plugin, porque a atualização troca a pasta versionada;
#   2. devolve o bloco de roteamento (hooks\routing.md com os caminhos absolutos resolvidos) como
#      additionalContext. Fora do escopo não devolve nada: o plugin é invisível nos outros projetos do dev.
# Nunca falha a sessão: qualquer erro vira uma linha no contexto.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$pluginRoot = if ($env:CLAUDE_PLUGIN_ROOT) { $env:CLAUDE_PLUGIN_ROOT } else { Split-Path $PSScriptRoot -Parent }
$pluginRoot = [IO.Path]::GetFullPath($pluginRoot).TrimEnd('\', '/')
$scripts = Join-Path $pluginRoot 'skills\codebase-analyzer\scripts'

. (Join-Path $scripts 'CodeRoot.ps1')

# O cwd vem do payload (JSON em stdin); sem payload, o diretório do processo.
$cwd = (Get-Location).Path
try {
    $payload = [Console]::In.ReadToEnd()
    if ($payload) {
        $parsed = $payload | ConvertFrom-Json
        if ($parsed.cwd) { $cwd = $parsed.cwd }
    }
}
catch { }

if (-not (Test-CodeScope $cwd)) { exit 0 }

$notes = @()

# ---------- Build ----------
$exe = Join-Path $scripts 'DeclIndex\DeclIndex\bin\Release\net10.0\DeclIndex.exe'
$hookExe = Join-Path $scripts 'DeclIndex\DeclIndex.Hook\bin\Release\net10.0\DeclIndex.Hook.exe'
if (-not (Test-Path $exe) -or -not (Test-Path $hookExe)) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $build = & dotnet build (Join-Path $scripts 'DeclIndex\DeclIndex.slnx') -c Release --nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe) -or -not (Test-Path $hookExe)) {
            $reason = ($build | Select-Object -Last 3) -join ' '
            $notes += "> ⚠️ DeclIndex não compilou (``dotnet build`` em ``$scripts\DeclIndex``): $reason"
        }
    }
    else {
        $notes += "> ⚠️ DeclIndex não compilou: ``dotnet`` (SDK .NET 10) não está no PATH."
    }
}
if (-not (Get-Command rg -ErrorAction SilentlyContinue)) {
    $notes += "> ⚠️ ``rg`` (ripgrep) não está no PATH; ``find_declarations`` e ``find_usages`` precisam dele."
}

# ---------- Roteamento ----------
$reference = Join-Path $pluginRoot 'skills\codebase-analyzer\references\code-intelligence.md'
$block = Get-Content (Join-Path $PSScriptRoot 'routing.md') -Raw -Encoding UTF8
$block = $block.Replace('{{SCRIPTS}}', $scripts).Replace('{{REFERENCE}}', $reference)
if ($notes) { $block = $block.TrimEnd() + "`n`n" + ($notes -join "`n") + "`n" }

@{
    hookSpecificOutput = @{
        hookEventName     = 'SessionStart'
        additionalContext = $block
    }
} | ConvertTo-Json -Depth 4 -Compress
exit 0
