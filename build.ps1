param(
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$FrameworkDependent,
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    [xml]$properties = Get-Content -LiteralPath Directory.Build.props -Raw
    $buildVersion = if ($Version) { $Version } else { $properties.Project.PropertyGroup.Version }
    # CD 传入四段版本号；本地构建仍使用项目中的基础版本。
    dotnet build CodexQuotaTray.slnx -c Release "-p:Version=$buildVersion" --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    dotnet run --project tests/CodexQuotaTray.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
    if ($Publish) {
        # 默认发布两个版本；保留单独选择某个版本的参数。
        $publishBoth = !$SelfContained -and !$FrameworkDependent
        $variants = @()
        if ($publishBoth -or $SelfContained) {
            $variants += @{ Folder = 'CodexQuotaTray-self-contained'; BundleRuntime = 'true'; Suffix = 'win-x64-self-contained' }
        }
        if ($publishBoth -or $FrameworkDependent) {
            $variants += @{ Folder = 'CodexQuotaTray-no-runtime'; BundleRuntime = 'false'; Suffix = 'win-x64-no-runtime' }
        }
        foreach ($variant in $variants) {
            $output = Join-Path $PSScriptRoot ('dist/' + $variant.Folder)
            dotnet publish src/CodexQuotaTray -c Release -r win-x64 --self-contained $variant.BundleRuntime "-p:Version=$buildVersion" -p:PublishSingleFile=true -p:DebugType=None -o $output --nologo
            if ($LASTEXITCODE -ne 0) { throw ('发布失败：' + $variant.Suffix) }
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force
            # 只打包本次发布的应用和说明，避免将输出目录中遗留的本机文件打进包。
            $archive = Join-Path $PSScriptRoot ('dist/CodexQuotaTray-' + $buildVersion + '-' + $variant.Suffix + '.zip')
            Compress-Archive -LiteralPath @((Join-Path $output 'CodexQuotaTray.exe'), (Join-Path $output 'README.md')) -DestinationPath $archive -Force
            Write-Output ('已生成：' + $archive)
        }
    }
} finally { Pop-Location }
