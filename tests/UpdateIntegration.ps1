param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/update-integration-v2'),
    [ValidateSet('self-contained', 'no-runtime')]
    [string[]]$Variants = @('self-contained', 'no-runtime')
)
$ErrorActionPreference = 'Stop'
$outputDirectory = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
# 每次使用独立目录，仅操作此次创建的夹具；不启动真实圆环、不修改用户设置。
$runDirectory = Join-Path $outputDirectory ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
foreach ($variant in $Variants) {
    $bundleRuntime = if ($variant -eq 'self-contained') { 'true' } else { 'false' }
    foreach ($revision in @(1, 2, 3)) {
        $fixtureOutput = Join-Path $runDirectory "$variant-$revision"
        dotnet publish (Join-Path $PSScriptRoot 'UpdateFixture/UpdateFixture.csproj') -c Release -r win-x64 --self-contained $bundleRuntime "-p:Version=9.0.0.$revision" -p:PublishSingleFile=true -p:DebugType=None -o $fixtureOutput --nologo
        if ($LASTEXITCODE -ne 0) { throw "更新夹具构建失败：$variant-$revision" }
    }
    foreach ($revision in @(2, 3)) {
        $caseDirectory = Join-Path $runDirectory "$variant-case-$revision"
        New-Item -ItemType Directory -Path $caseDirectory | Out-Null
        $target = Join-Path $caseDirectory 'CodexQuotaTray.exe'
        $original = Join-Path $runDirectory "$variant-1/UpdateFixture.exe"
        $candidate = Join-Path $runDirectory "$variant-$revision/UpdateFixture.exe"
        Copy-Item -LiteralPath $original -Destination $target
        $start = [Diagnostics.ProcessStartInfo]::new($target)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.ArgumentList.Add('--stage')
        $start.ArgumentList.Add($candidate)
        $parent = [Diagnostics.Process]::Start($start)
        try {
            if (!$parent.WaitForExit(30000)) { $parent.Kill(); throw '更新交接超时。' }
            if ($parent.ExitCode -ne 0) { throw "更新交接失败：$($parent.ExitCode)" }
        } finally { $parent.Dispose() }
        $work = Get-Content -LiteralPath (Join-Path $caseDirectory 'work.txt') -Raw
        $resultPath = Join-Path $work 'result.json'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $resultPath)) {
            if ($watch.Elapsed.TotalSeconds -gt 45) { throw "更新结果超时：$variant-$revision" }
            Start-Sleep -Milliseconds 200
        }
        # 写入完成后再读取，避免观察到刚创建但尚未写完的文件。
        Start-Sleep -Milliseconds 300
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $expectedSuccess = $revision -eq 2
        if ($result.success -ne $expectedSuccess) { throw "更新结果不符：$variant-$revision" }
        $expectedFile = if ($expectedSuccess) { $candidate } else { $original }
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $expectedFile).Hash) { throw '安装/回滚后的文件不符。' }
        $restarted = Get-Content -LiteralPath (Join-Path $caseDirectory 'restarted.json') -Raw | ConvertFrom-Json
        $expectedVersion = if ($expectedSuccess) { '9.0.0.2' } else { '9.0.0.1' }
        if ($restarted.version -ne $expectedVersion -or $restarted.variant -ne $variant -or !$restarted.skipCheck -or !$restarted.follow) {
            throw '重启参数、版本或运行时类型丢失。'
        }
        if (@(Get-ChildItem -LiteralPath $caseDirectory -Filter '*.backup-*').Count -ne 0 -or
            @(Get-ChildItem -LiteralPath $caseDirectory -Filter '*.incoming-*').Count -ne 0) { throw '替换事务未完成清理。' }
        Write-Output "PASS updater integration: $variant / revision $revision"
    }
}
