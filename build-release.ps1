# build-release.ps1 — 一键构建绿色免安装版
#
# 用法：
#   .\build-release.ps1                   # 构建完整便携包
#   .\build-release.ps1 -SkipSelfTest     # 跳过自检（不推荐）
#
# 产物：
#   dist\StartupManager-<版本>-portable.zip   绿色免安装包（解压即用）
#   publish-dir\                              未压缩的发布目录
#
# 说明：
#   采用「自包含目录」形式而非单文件，原因参见 README 的「设计说明」：
#   单文件 exe 运行时需先解压自身到 %TEMP%，在受限环境下会因无法创建
#   解压目录而启动失败（错误码 5）。

param(
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

try {
    $version = (Select-String -Path 'StartupManager.csproj' -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
    Write-Host "构建 StartupManager v$version" -ForegroundColor Cyan
    Write-Host ''

    # ── 1. 清理 ────────────────────────────────────────
    Write-Host '[1/4] 清理旧产物…' -ForegroundColor Yellow
    Remove-Item 'publish-dir' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item 'dist' -Recurse -Force -ErrorAction SilentlyContinue

    # ── 2. 发布自包含目录版 ──────────────────────────
    Write-Host '[2/4] 发布自包含目录版…' -ForegroundColor Yellow
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:EnableCompressionInSingleFile=false `
        -p:DebugType=none `
        -o publish-dir | Out-Null

    if (-not (Test-Path 'publish-dir\StartupManager.exe')) {
        throw '发布失败：publish-dir\StartupManager.exe 不存在'
    }
    $sizeMB = [math]::Round((Get-ChildItem 'publish-dir' -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "      完成：$sizeMB MB" -ForegroundColor Green

    # ── 3. 自检 ──────────────────────────────────────
    if ($SkipSelfTest) {
        Write-Host '[3/4] 跳过自检（-SkipSelfTest）' -ForegroundColor DarkGray
    } else {
        Write-Host '[3/4] 运行自检…' -ForegroundColor Yellow
        # 结果文件写到项目目录，不用 %TEMP%：某些受限环境下 Temp 不可写
        $resultFile = Join-Path $root "selftest_result.txt"
        & '.\publish-dir\StartupManager.exe' --selftest $resultFile | Out-Null
        $code = $LASTEXITCODE

        if (Test-Path $resultFile) {
            $content = Get-Content $resultFile -Raw -Encoding UTF8
            $failCount = ([regex]::Matches($content, 'FAIL')).Count
            $passCount = ([regex]::Matches($content, 'PASS')).Count
            Remove-Item $resultFile -Force -ErrorAction SilentlyContinue

            Write-Host "      自检：通过 $passCount 项，失败 $failCount 项" -ForegroundColor $(if ($failCount -eq 0) { 'Green' } else { 'Red' })

            if ($failCount -gt 0 -or $code -ne 0) {
                throw "自检未通过（失败 $failCount 项），已中止打包。"
            }
        } else {
            throw '自检未产生结果文件，已中止打包。'
        }
    }

    # ── 4. 打包绿色版 zip ────────────────────────────
    # 结构：StartupManager\*.exe ... 解压后是独立文件夹，不会散落一地
    Write-Host '[4/4] 打包绿色免安装版…' -ForegroundColor Yellow
    New-Item -ItemType Directory -Force 'dist' | Out-Null
    $zipPath = "dist\StartupManager-$version-portable.zip"

    $staging = Join-Path $env:TEMP "sm_zip_$([guid]::NewGuid().ToString('N'))"
    try {
        $appDir = Join-Path $staging 'StartupManager'
        New-Item -ItemType Directory -Force $appDir | Out-Null
        Copy-Item 'publish-dir\*' $appDir -Recurse -Force

        # 排除运行时缓存目录（自检时可能生成），不应随包分发
        $cacheDir = Join-Path $appDir '.smcache'
        if (Test-Path $cacheDir) {
            Remove-Item $cacheDir -Recurse -Force
            Write-Host '      已排除运行时缓存目录 .smcache' -ForegroundColor DarkGray
        }

        # 附上使用说明，方便分发后直接查看
        if (Test-Path 'README.md') {
            Copy-Item 'README.md' $appDir -Force
        }

        Compress-Archive -Path $appDir -DestinationPath $zipPath -CompressionLevel Optimal
    }
    finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "      完成：$zipMB MB" -ForegroundColor Green

    # ── 汇总 ─────────────────────────────────────────
    Write-Host ''
    Write-Host '产物：' -ForegroundColor Cyan
    Get-ChildItem 'dist' -File | ForEach-Object {
        Write-Host ("      {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1))
    }
    Write-Host ''
    Write-Host '构建完成。' -ForegroundColor Cyan
}
finally {
    Pop-Location
}
