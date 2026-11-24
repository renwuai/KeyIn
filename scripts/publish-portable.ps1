#requires -Version 7.2
[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'KeyLend.csproj'
$version = [string]([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw "无效的项目版本 $version" }
$sdk = (Get-Command -Name $DotNetPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$sdkVersion = (& $sdk --version | Select-Object -Last 1).Trim()
$hostModel = Join-Path (Split-Path -Parent $sdk) "sdk/$sdkVersion/Sdks/Microsoft.NET.Sdk/tools/net472/Microsoft.NET.HostModel.dll"
$machines = [ordered]@{ 'win-x64' = 0x8664; 'win-arm64' = 0xAA64; 'win-x86' = 0x014C }
$tempRoot = Join-Path $repo '_temp'
$releaseRoot = Join-Path $tempRoot 'release'
$work = Join-Path $tempRoot ('portable-' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $work 'packages'
$destination = Join-Path $releaseRoot $version

function Copy-RuntimeLicenses([string] $AssetsFile, [string] $Rid, [string] $PublishDir) {
    $assets = Get-Content -Raw -LiteralPath $AssetsFile | ConvertFrom-Json -AsHashtable
    $framework = @($assets.project.frameworks.Values)[0]
    $licenseDir = Join-Path $PublishDir 'licenses'
    [IO.Directory]::CreateDirectory($licenseDir) | Out-Null
    foreach ($name in "Microsoft.NETCore.App.Runtime.$Rid", "Microsoft.WindowsDesktop.App.Runtime.$Rid", 'Microsoft.Windows.SDK.NET.Ref') {
        $dependency = $framework.downloadDependencies | Where-Object name -eq $name | Select-Object -First 1
        if (-not $dependency -or $dependency.version -notmatch '^\[([^,\]]+)') { throw "找不到还原的依赖版本：$name" }
        $packageVersion = $Matches[1]
        $packageDir = $null
        foreach ($folder in $assets.packageFolders.Keys) {
            $candidate = Join-Path $folder $name.ToLowerInvariant() $packageVersion
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDir = $candidate; break }
        }
        if (-not $packageDir) { throw "找不到 NuGet 包：$name $packageVersion" }
        $target = Join-Path $licenseDir "$name-$packageVersion"
        [IO.Directory]::CreateDirectory($target) | Out-Null
        $notices = @(Get-ChildItem -LiteralPath $packageDir -File | Where-Object Name -Match '^(LICENSE|LICENCE|NOTICE|THIRD[-_. ]?PARTY[-_. ]?NOTICES?)(\..*)?$')
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target }
        if ($notices.Count -eq 0) {
            # Windows SDK 投影包在 nuspec 中使用许可链接；随包保存原始来源与版本。
            $nuspec = Get-ChildItem -LiteralPath $packageDir -File -Filter '*.nuspec' | Select-Object -First 1
            $url = ([xml](Get-Content -Raw -LiteralPath $nuspec.FullName)).SelectSingleNode("//*[local-name()='licenseUrl']")
            if (-not $url -or $url.InnerText -notmatch '^https://') { throw "找不到许可文件或许可链接：$name" }
            [IO.File]::WriteAllText((Join-Path $target 'LICENSE-URL.txt'), "$name $packageVersion`n$($url.InnerText)`n")
        }
    }
}

[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$lock = [IO.File]::Open((Join-Path $tempRoot 'publish-portable.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    [IO.Directory]::CreateDirectory($packages) | Out-Null
    # 固定本批次的许可快照，逐个检查三个 EXE 内嵌的原文。
    $license = Join-Path $work 'LICENSE'
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $license
    $sums = foreach ($rid in $machines.Keys) {
        Write-Host "正在发布 KeyIn $version ($rid)"
        $publish = Join-Path $work $rid
        $obj = Join-Path $work "obj/$rid"
        $buildArgs = @('--runtime', $rid, '--disable-build-servers', "-p:BaseIntermediateOutputPath=$obj/",
            "-p:BaseOutputPath=$work/bin/", '-p:SelfContained=true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
            '-p:PublishTrimmed=false', '-p:DebugSymbols=false', '-p:DebugType=None')
        & $sdk restore $project @buildArgs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore $rid 失败，退出码 $LASTEXITCODE" }

        # 许可正文作为程序集资源嵌入，托管程序集可直接从单文件加载。
        $noticeDir = Join-Path $work "notices/$rid"
        Copy-RuntimeLicenses (Join-Path $obj 'project.assets.json') $rid $noticeDir
        $noticeFile = Join-Path $noticeDir 'THIRD-PARTY-NOTICES.txt'
        $notices = [Text.StringBuilder]::new()
        foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $noticeDir 'licenses') -Recurse -File | Sort-Object FullName) {
            [void]$notices.AppendLine([IO.Path]::GetRelativePath($noticeDir, $notice.FullName).Replace('\', '/'))
            [void]$notices.AppendLine([IO.File]::ReadAllText($notice.FullName)).AppendLine()
        }
        [IO.File]::WriteAllText($noticeFile, $notices.ToString())
        & $sdk publish $project @buildArgs --configuration Release --self-contained true --no-restore --output $publish --nologo "-p:PortableNoticeFile=$noticeFile" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $rid 失败，退出码 $LASTEXITCODE" }

        $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Force)
        if ($files.Count -ne 1 -or $files[0].FullName -ne (Join-Path $publish 'KeyLend.exe')) {
            throw "发布目录必须只包含单一 EXE：$publish"
        }
        $name = "KeyIn-$version-$rid.exe"
        $exe = Join-Path $packages $name
        [IO.File]::Move($files[0].FullName, $exe)
        & (Join-Path $PSScriptRoot 'test-single-file.ps1') -BundleFile $exe -Rid $rid -Machine $machines[$rid] -Version $version -HostModel $hostModel -NoticeFile $noticeFile -LicenseFile $license
        $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    if ((Get-FileHash -LiteralPath $license).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repo 'LICENSE')).Hash) {
        throw '构建期间 LICENSE 已更新，请重新运行以使用最终文本'
    }
    [IO.File]::WriteAllLines((Join-Path $packages 'SHA256SUMS.txt'), [string[]]$sums, [Text.UTF8Encoding]::new($false))
    # 三个单文件全部通过校验后再整组替换，其他版本保留。
    $previous = Join-Path $work 'previous'
    if ([IO.Directory]::Exists($destination)) {
        $resolved = (Resolve-Path -LiteralPath $destination).Path
        if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($releaseRoot)) { throw "发布目录不在版本输出根目录内：$resolved" }
        $ownedNames = @('SHA256SUMS.txt') + @($machines.Keys | ForEach-Object { "KeyIn-$version-$_.exe" })
        if (Get-ChildItem -LiteralPath $resolved -Force | Where-Object { $_.PSIsContainer -or $_.Name -notin $ownedNames }) {
            throw "版本目录内存在其他文件：$resolved"
        }
        [IO.Directory]::Move($resolved, $previous)
    }
    try { [IO.Directory]::Move($packages, $destination) }
    catch {
        if ([IO.Directory]::Exists($previous)) { [IO.Directory]::Move($previous, $destination) }
        throw
    }
    Write-Host "已验证三个自包含单文件 EXE，校验清单位于 $destination/SHA256SUMS.txt"
} finally {
    $lock.Dispose()
    # 只移除本次创建的随机工作目录；若上次产物未能回滚则保留，便于手动恢复。
    if ([IO.Directory]::Exists($work) -and ([IO.Directory]::Exists($destination) -or -not [IO.Directory]::Exists((Join-Path $work 'previous')))) {
        $resolved = (Resolve-Path -LiteralPath $work).Path
        if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($tempRoot) -and [IO.Path]::GetFileName($resolved) -match '^portable-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
