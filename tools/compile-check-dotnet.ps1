<#
.SYNOPSIS
  Compile MCP for Unity's C# (Runtime + Editor) with the .NET SDK against a local Unity
  installation's reference assemblies. Never launches the Editor.

.DESCRIPTION
  Windows/PowerShell counterpart to tools/compile-check.sh. The bash script needs Unity's
  bundled Roslyn (Data/DotNetSdkRoslyn/csc.dll), which Unity 6 no longer ships at that path;
  this script drives `dotnet build` with a generated csproj that references Unity's managed
  assemblies directly.

  Unity is used purely as a source of reference DLLs. No license is required and no Editor
  process is started, so this is a real semantic compile check that also runs on machines
  where the Editor cannot be opened.

.PARAMETER UnityData
  Unity Editor "Data" directory, e.g.
  "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Data".

.PARAMETER Platform
  Define a platform: win, osx or linux. Defaults to win.

.PARAMETER KeepArtifacts
  Leave the generated project and build output in place for inspection.

.EXAMPLE
  pwsh tools/compile-check-dotnet.ps1 -UnityData "C:\Program Files\Unity\Hub\Editor\6000.5.6f1\Editor\Data"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityData,

    [ValidateSet('win', 'osx', 'linux')]
    [string]$Platform = 'win',

    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $UnityData 'Managed'
$unityModules = Join-Path $managed 'UnityEngine'
$libcache = Join-Path $UnityData 'Resources/PackageManager/ProjectTemplates/libcache'

if (-not (Test-Path -LiteralPath $unityModules)) {
    throw "Unity reference assemblies not found at '$unityModules'."
}

$out = Join-Path ([System.IO.Path]::GetTempPath()) ("mcp-compile-check-dotnet-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $out -Force | Out-Null

$refs = New-Object System.Collections.Generic.List[string]

# Unity engine/editor module assemblies. The bare UnityEngine.dll / UnityEditor.dll facades
# are deliberately excluded: referencing both them and the modules duplicates every type.
foreach ($file in Get-ChildItem -LiteralPath $unityModules -File) {
    if ($file.Name -like 'UnityEngine*.dll' -or $file.Name -like 'UnityEditor*.dll') {
        $refs.Add($file.FullName)
    }
}

$refs.Add((Join-Path $managed 'Newtonsoft.Json.dll'))
$refs.Add((Join-Path $managed 'System.CodeDom.dll'))

# `dynamic` needs the real binder implementation, not the reference facade.
$microsoftCSharp = Join-Path $UnityData 'MonoBleedingEdge/lib/mono/4.5/Microsoft.CSharp.dll'
if (Test-Path -LiteralPath $microsoftCSharp) {
    $refs.Add($microsoftCSharp)
}

# Test-runner assemblies are only needed by the Editor assembly's test-runner service.
if (Test-Path -LiteralPath $libcache) {
    foreach ($name in @('UnityEditor.TestRunner.dll', 'UnityEngine.TestRunner.dll', 'nunit.framework.dll')) {
        $found = Get-ChildItem -LiteralPath $libcache -Recurse -File -Filter $name |
            Select-Object -First 1
        if ($found) { $refs.Add($found.FullName) }
    }
}

if ($Platform -eq 'win') {
    $platformDefines = 'UNITY_EDITOR_WIN;UNITY_STANDALONE_WIN;PLATFORM_STANDALONE_WIN'
}
elseif ($Platform -eq 'osx') {
    $platformDefines = 'UNITY_EDITOR_OSX;UNITY_STANDALONE_OSX;PLATFORM_STANDALONE_OSX'
}
else {
    $platformDefines = 'UNITY_EDITOR_LINUX;UNITY_STANDALONE_LINUX;PLATFORM_STANDALONE_LINUX'
}

# Version ladder: the installed Editor's own major.minor plus the rungs below it.
$unityVersion = (Get-Content -LiteralPath (Join-Path $UnityData '..\..\Unity\Data\unity-version.txt') -ErrorAction SilentlyContinue)
$defines = @(
    'UNITY_EDITOR'
    'DEBUG'
    'UNITY_INCLUDE_TESTS'
    'UNITY_2021_3_OR_NEWER', 'UNITY_2022_1_OR_NEWER', 'UNITY_2022_2_OR_NEWER'
    'UNITY_6000_0_OR_NEWER'
    # The MCP compat shims switch to the EntityId API on 6.5 and to the reflection path on
    # 6.6; both types exist in a 6.5+ installation, so selecting them keeps the probe on the
    # same code paths a licence-holding Editor would take.
    'UNITY_6000_5_OR_NEWER', 'UNITY_6000_6_OR_NEWER'
)

$csproj = New-Object System.Collections.Generic.List[string]
$csproj.Add('<Project Sdk="Microsoft.NET.Sdk">')
$csproj.Add('  <PropertyGroup>')
$csproj.Add('    <TargetFramework>netstandard2.1</TargetFramework>')
$csproj.Add('    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>')
$csproj.Add('    <LangVersion>9.0</LangVersion>')
$csproj.Add('    <NoWarn>CS1701;CS1702;CS0618;CS0612;CS0619;CS0649;CS0169;MSB3277;MSB3245</NoWarn>')
$csproj.Add('    <AssemblyName>MCPForUnity.Editor</AssemblyName>')
$csproj.Add("    <DefineConstants>$(($defines + $platformDefines.Split(';')) -join ';')</DefineConstants>")
$csproj.Add('  </PropertyGroup>')
$csproj.Add('  <ItemGroup>')
foreach ($reference in $refs) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($reference)
    $csproj.Add("    <Reference Include=""$name""><HintPath>$reference</HintPath><Private>false</Private></Reference>")
}
$csproj.Add('  </ItemGroup>')
$csproj.Add('  <ItemGroup>')
$csproj.Add("    <Compile Include=""$repo\MCPForUnity\Runtime\**\*.cs"" />")
$csproj.Add("    <Compile Include=""$repo\MCPForUnity\Editor\**\*.cs"" />")
$csproj.Add('  </ItemGroup>')
$csproj.Add('</Project>')

$csprojPath = Join-Path $out 'MCPForUnity.Editor.csproj'
$csproj -join "`n" | Set-Content -LiteralPath $csprojPath -Encoding UTF8

$sourceCount = (Get-ChildItem -LiteralPath (Join-Path $repo 'MCPForUnity/Editor') -Recurse -File -Filter *.cs).Count
Write-Host "Unity data : $UnityData"
Write-Host "Platform   : $Platform"
Write-Host "Sources    : $sourceCount Editor files, $($refs.Count) references"

Push-Location $out
try {
    & dotnet build $csprojPath -v q --nologo
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($exitCode -ne 0) {
    Write-Error "compile check FAILED (exit $exitCode). Artifacts: $out"
    exit $exitCode
}

Write-Host "compile check passed for: $Platform"
if (-not $KeepArtifacts) {
    Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
}
