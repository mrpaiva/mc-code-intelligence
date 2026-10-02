# Testes do find_usages.ps1 (Pester 5): Invoke-Pester -Path .\find_usages.Tests.ps1
# Mesma árvore sintética do find_declarations.Tests.ps1: o símbolo só é usado em Applications\, e a chamada parte de
# Components\, onde ele não aparece — é o que separa "checkout inteiro" de "só a pasta atual".

BeforeAll {
    $script:findUsages = Join-Path $PSScriptRoot "find_usages.ps1"

    function New-TempDirectory([string]$Prefix) {
        $path = Join-Path ([IO.Path]::GetTempPath()) ($Prefix + [guid]::NewGuid().ToString("N").Substring(0, 8))
        New-Item -ItemType Directory $path | Out-Null
        return $path
    }

    function New-SyntheticCode {
        $root = New-TempDirectory "mcci-code-"
        New-Item -ItemType Directory (Join-Path $root "Applications\App\Src") | Out-Null
        New-Item -ItemType Directory (Join-Path $root "Components\Comp") | Out-Null
        Set-Content (Join-Path $root "Applications\App\Src\GuardService.cs") "namespace App.Services;`npublic class GuardService { public void Check() { } }"
        Set-Content (Join-Path $root "Applications\App\Src\Consumer.cs") "namespace App.Services;`npublic class Consumer { public void Run() => new GuardService().Check(); }"
        Set-Content (Join-Path $root "Components\Comp\Helper.cs") "namespace Comp;`npublic static class Helper { public static int Sum(int a, int b) => a + b; }"
        git -C $root init -q
        git -C $root add -A
        git -C $root -c user.name=pester -c user.email=pester@local commit -q -m init
        return $root
    }

    function Invoke-FindUsages([string]$From, [hashtable]$Arguments) {
        Push-Location $From
        try {
            $output = & $script:findUsages @Arguments 6>&1 | Out-String
            return @{ Output = $output; ExitCode = $LASTEXITCODE }
        }
        finally { Pop-Location }
    }

    $script:code = New-SyntheticCode
    $script:store = New-TempDirectory "mcci-store-"
    $script:savedStore = $env:MC_CODEINDEX
    $script:savedRoot = $env:MC_CODE_ROOT
    $env:MC_CODEINDEX = $script:store
    $env:MC_CODE_ROOT = $null
}

AfterAll {
    $env:MC_CODEINDEX = $script:savedStore
    $env:MC_CODE_ROOT = $script:savedRoot
    foreach ($dir in @($script:code, $script:store)) {
        if ($dir -and (Test-Path $dir)) { Remove-Item $dir -Recurse -Force }
    }
}

Describe "find_usages — raiz e recorte" {
    It "sem -Path, de uma subpasta, cobre o checkout inteiro e diz a raiz" {
        $result = Invoke-FindUsages (Join-Path $script:code "Components\Comp") @{ Symbol = "GuardService" }

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match ("Raiz: " + [regex]::Escape($script:code))
        $result.Output | Should -Not -Match "Recorte"
        $result.Output | Should -Match "Applications/App/Src/Consumer.cs"
    }

    It "-Path recorta, diz o recorte e, sem resultado, aponta como cobrir o checkout inteiro" {
        $result = Invoke-FindUsages $script:code @{ Symbol = "GuardService"; Path = "Components" }

        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match "Recorte: Components"
        $result.Output | Should -Match "Nenhuma referência"
        $result.Output | Should -Match ("-Path " + [regex]::Escape($script:code))
    }
}
