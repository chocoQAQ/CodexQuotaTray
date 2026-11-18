param([switch]$Publish, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build CodexQuotaTray.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    dotnet run --project tests/CodexQuotaTray.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
    if ($Publish) {
        $bundleRuntime = if ($SelfContained) { 'true' } else { 'false' }
        dotnet publish src/CodexQuotaTray -c Release -r win-x64 --self-contained $bundleRuntime -p:PublishSingleFile=true -p:DebugType=None -o dist/CodexQuotaTray --nologo
        if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    }
} finally { Pop-Location }
