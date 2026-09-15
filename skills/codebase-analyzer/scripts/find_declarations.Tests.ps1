# Testes do find_declarations.ps1 (Pester 5): Invoke-Pester -Path .\find_declarations.Tests.ps1
# Não dependem de um checkout do Code: cada execução cria uma árvore sintética (Applications\ + Components\,
# repositório git com dois .cs) e um armazém temporário, então servem tanto na máquina do dev quanto na CI.

BeforeAll {
    $script:findDeclarations = Join-Path $PSScriptRoot "find_declarations.ps1"

    function New-TempDirectory([string]$Prefix) {
        $path = Join-Path ([IO.Path]::GetTempPath()) ($Prefix + [guid]::NewGuid().ToString("N").Substring(0, 8))
        New-Item -ItemType Directory $path | Out-Null
        return $path
    }

    # Um checkout mínimo do Code: a forma (Applications\ e Components\) é o que a resolução da raiz reconhece.
    function New-SyntheticCode {
        $root = New-TempDirectory "mcci-code-"
        New-Item -ItemType Directory (Join-Path $root "Applications\App\Src") | Out-Null
        New-Item -ItemType Directory (Join-Path $root "Components\Comp") | Out-Null
        Set-Content (Join-Path $root "Applications\App\Src\App.csproj") '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
        Set-Content (Join-Path $root "Applications\App\Src\GuardService.cs") "namespace App.Services;`npublic class GuardService : ServiceBase { public void Check() { } }`npublic class ServiceBase { }"
        Set-Content (Join-Path $root "Components\Comp\Helper.cs") "namespace Comp;`npublic static class Helper { public static int Sum(int a, int b) => a + b; }"
        git -C $root init -q
        git -C $root add -A
        git -C $root -c user.name=pester -c user.email=pester@local commit -q -m init
        return $root
    }

    # Splat por hashtable: splat de array não vincula -Name/-Kind (o script devolve exit 3 em silêncio).
    function Invoke-FindDeclarations([string]$From, [hashtable]$Arguments) {
        Push-Location $From
        try {
            $output = & $script:findDeclarations @Arguments 6>&1 | Out-String
            return @{ Output = $output; ExitCode = $LASTEXITCODE }
        }
        finally { Pop-Location }
    }

    $script:code = New-SyntheticCode
    $script:outside = New-TempDirectory "mcci-outside-"
    $script:store = New-TempDirectory "mcci-store-"
    $script:savedStore = $env:MC_CODEINDEX
    $script:savedRoot = $env:MC_CODE_ROOT
    $env:MC_CODEINDEX = $script:store
    $env:MC_CODE_ROOT = $null
}

AfterAll {
    $env:MC_CODEINDEX = $script:savedStore
    $env:MC_CODE_ROOT = $script:savedRoot
    foreach ($dir in @($script:code, $script:outside, $script:store)) {
        if ($dir -and (Test-Path $dir)) { Remove-Item $dir -Recurse -Force }
    }
}

Describe "find_declarations — resolução da raiz" {
    It "de dentro do checkout (subpasta), usa o ancestral com Applications\ e Components\ e diz qual raiz usou" {
        $result = Invoke-FindDeclarations (Join-Path $script:code "Components\Comp") @{ Name = "GuardService"; Kind = "class" }

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match ("Raiz: " + [regex]::Escape($script:code))
        $result.Output | Should -Match "GuardService"
    }

    It "fora de um checkout, usa MC_CODE_ROOT" {
        $env:MC_CODE_ROOT = $script:code
        try { $result = Invoke-FindDeclarations $script:outside @{ Base = "ServiceBase"; Kind = "class" } }
        finally { $env:MC_CODE_ROOT = $null }

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match ("Raiz: " + [regex]::Escape($script:code))
        $result.Output | Should -Match "GuardService"
    }

    It "fora de um checkout e sem MC_CODE_ROOT, falha com instrução clara" {
        $result = Invoke-FindDeclarations $script:outside @{ Name = "GuardService" }

        $result.ExitCode | Should -Be 1
        $result.Output | Should -Match "MC_CODE_ROOT"
        $result.Output | Should -Match "-Root"
    }

    It "-Root explícito prevalece sobre a inferência" {
        $result = Invoke-FindDeclarations $script:outside @{ Name = "Helper"; Kind = "class"; Root = $script:code }

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match ("Raiz: " + [regex]::Escape($script:code))
        $result.Output | Should -Match "Helper"
    }

    It "o índice fica no armazém de MC_CODEINDEX" {
        Get-ChildItem (Join-Path $script:store "worktrees") -Filter *.tsv | Should -Not -BeNullOrEmpty
    }
}
