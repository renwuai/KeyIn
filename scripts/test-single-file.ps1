#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BundleFile,
    [Parameter(Mandatory)] [string] $Rid,
    [Parameter(Mandatory)] [int] $Machine,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $HostModel,
    [Parameter(Mandatory)] [string] $NoticeFile,
    [Parameter(Mandatory)] [string] $LicenseFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$iconFile = Join-Path $repo 'KeyLend.ico'

function Assert-HostResources([string] $Exe) {
    # 直接读取目标架构的 PE，不加载程序；核对图标组和每一帧实际像素数据。
    $pe = [Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead($Exe))
    try {
        if ([int]$pe.PEHeaders.CoffHeader.Machine -ne $Machine -or [int]$pe.PEHeaders.PEHeader.Subsystem -ne 2) {
            throw "GUI 子系统或 PE 架构不符：$Rid"
        }
        $resources = $pe.PEHeaders.PEHeader.ResourceTableDirectory
        $reader = $pe.GetSectionData($resources.RelativeVirtualAddress).GetReader(0, $resources.Size)
        $data = $reader.ReadBytes($resources.Size)
        $typeCount = [BitConverter]::ToUInt16($data, 12) + [BitConverter]::ToUInt16($data, 14)
        $types = for ($i = 0; $i -lt $typeCount; $i++) { [BitConverter]::ToUInt32($data, 16 + 8 * $i) }
        if (3 -notin $types -or 14 -notin $types) { throw 'EXE 缺少 Windows 图标资源' }
        $native = [Text.Encoding]::Latin1.GetString($data)
        $icon = [IO.File]::ReadAllBytes($iconFile)
        for ($i = 0; $i -lt [BitConverter]::ToUInt16($icon, 4); $i++) {
            $size = [BitConverter]::ToInt32($icon, 6 + 16 * $i + 8)
            $offset = [BitConverter]::ToInt32($icon, 6 + 16 * $i + 12)
            if (-not $native.Contains([Text.Encoding]::Latin1.GetString($icon, $offset, $size), [StringComparison]::Ordinal)) {
                throw "EXE 未找到品牌图标第 $i 帧"
            }
        }
    } finally { $pe.Dispose() }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).ProductVersion -ne $Version) { throw "EXE 版本不符：$Exe" }
}

function Read-BundlePayload($Reader, $Entry) {
    $Reader.BaseStream.Position = $Entry.Offset
    $bytes = $Reader.ReadBytes($Entry.Stored)
    if ($Entry.Compressed) {
        $inputStream = [IO.MemoryStream]::new($bytes)
        $inflate = [IO.Compression.DeflateStream]::new($inputStream, [IO.Compression.CompressionMode]::Decompress)
        $outputStream = [IO.MemoryStream]::new()
        try { $inflate.CopyTo($outputStream); $bytes = $outputStream.ToArray() }
        finally { $inflate.Dispose(); $outputStream.Dispose(); $inputStream.Dispose() }
    }
    if ($bytes.Length -ne $Entry.Size) { throw "文件长度不符：$($Entry.Name)" }
    return ,$bytes
}

function Assert-ManagedResources([byte[]] $AppBytes) {
    $expected = @{
        'KeyLend.KeyLend.ico' = $iconFile
        'KeyIn.LICENSE' = $LicenseFile
        'KeyIn.THIRD-PARTY-NOTICES.txt' = $NoticeFile
    }
    $managed = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new($AppBytes))
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($managed)
        $block = $managed.GetSectionData($managed.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress)
        foreach ($handle in $metadata.ManifestResources) {
            $resource = $metadata.GetManifestResource($handle)
            $name = $metadata.GetString($resource.Name)
            if (-not $expected.ContainsKey($name)) { continue }
            $reader = $block.GetReader([int]$resource.Offset, $block.Length - [int]$resource.Offset)
            $bytes = $reader.ReadBytes($reader.ReadInt32())
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            if (-not $resource.Implementation.IsNil -or $digest -ne (Get-FileHash -LiteralPath $expected[$name]).Hash) {
                throw "程序集内资源校验失败：$name"
            }
            $expected.Remove($name)
        }
        if ($expected.Count) { throw "程序集缺少嵌入资源：$($expected.Keys -join ', ')" }
    } finally { $managed.Dispose() }
}

Assert-HostResources $BundleFile
# 使用构建 SDK 的 HostModel 定位清单，再读取 .NET 6–11 使用的 v6 bundle 文件表。
[void][Reflection.Assembly]::LoadFrom($HostModel)
[long]$header = 0
if (-not [Microsoft.NET.HostModel.Bundle.Bundler]::IsBundle($BundleFile, [ref]$header)) { throw '文件不是 .NET 单文件程序' }
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($BundleFile))
try {
    $reader.BaseStream.Position = $header
    if ($reader.ReadUInt32() -ne 6 -or $reader.ReadUInt32() -ne 0) { throw '未识别的 bundle 清单版本' }
    $count = $reader.ReadInt32()
    [void]$reader.ReadString()
    $reader.BaseStream.Position += 40  # deps、runtimeconfig 的位置与长度，以及标志位。
    $entries = @{}
    for ($i = 0; $i -lt $count; $i++) {
        $offset = $reader.ReadInt64()
        $size = $reader.ReadInt64()
        $compressed = $reader.ReadInt64()
        $kind = $reader.ReadByte()
        $name = $reader.ReadString()
        $stored = if ($compressed) { $compressed } else { $size }
        if ($offset -lt 0 -or $size -lt 0 -or $compressed -lt 0 -or $offset + $stored -gt $header -or $entries.ContainsKey($name)) {
            throw "无效的 bundle 条目：$name"
        }
        $entries[$name] = @{ Name=$name; Offset=$offset; Size=$size; Stored=$stored; Compressed=$compressed; Kind=$kind }
    }
    foreach ($name in 'KeyLend.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll','PresentationFramework.dll','WinRT.Runtime.dll','Microsoft.Windows.SDK.NET.dll') {
        if (-not $entries.ContainsKey($name) -or $entries[$name].Kind -ne 1) { throw "缺少托管程序集：$name" }
    }
    foreach ($name in 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll') {
        if (-not $entries.ContainsKey($name) -or $entries[$name].Kind -ne 2) { throw "缺少原生桌面运行库：$name" }
        $native = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new((Read-BundlePayload $reader $entries[$name])))
        try { if ([int]$native.PEHeaders.CoffHeader.Machine -ne $Machine) { throw "原生桌面运行库架构不符：$name" } }
        finally { $native.Dispose() }
    }
    $runtime = [Text.Encoding]::UTF8.GetString((Read-BundlePayload $reader $entries['KeyLend.runtimeconfig.json'])) | ConvertFrom-Json -AsHashtable
    $deps = [Text.Encoding]::UTF8.GetString((Read-BundlePayload $reader $entries['KeyLend.deps.json'])) | ConvertFrom-Json -AsHashtable
    $included = @($runtime.runtimeOptions.includedFrameworks.name)
    if ('Microsoft.NETCore.App' -notin $included -or 'Microsoft.WindowsDesktop.App' -notin $included -or -not $deps.runtimeTarget.name.EndsWith("/$Rid")) {
        throw "自包含框架或 RID 不符：$Rid"
    }
    Assert-ManagedResources (Read-BundlePayload $reader $entries['KeyLend.dll'])
    Write-Host "已验证 $Rid 的 PE、$count 个 bundle 条目、WindowsDesktop 原生库、品牌图标与许可。"
} finally { $reader.Dispose() }
