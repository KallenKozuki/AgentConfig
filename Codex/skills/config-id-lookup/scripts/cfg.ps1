<#
Config ID -> name lookup. Keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).

Offline tables (reads Assets/GameRes/Config/*.bytes with Library/ScriptAssemblies/GamePlay.dll):
  cfg.ps1 get item 1001 1002            # aliases: partner item beauty mall skin system building word
  cfg.ps1 get Partner 840014,840010     # or any table name (TbXxx without "Tb")
  cfg.ps1 get item 1001 -Full           # include all fields
  cfg.ps1 find beauty <keyword> [max]   # keyword in any string field
  cfg.ps1 where skin ModelId 56003      # field equals value
  cfg.ps1 tables                        # list tables and aliases

Activity (needs Unity in Play mode, uses runtime-inspect):
  cfg.ps1 activity                      # all activities currently delivered by server
  cfg.ps1 activity 9010264,9010316
#>
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('get', 'find', 'where', 'tables', 'activity')]
    [string]$Cmd,
    [Parameter(Position = 1)]
    [string]$Table,
    [Parameter(Position = 2, ValueFromRemainingArguments = $true)]
    [string[]]$Rest,
    [switch]$Full
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$projectDir = Get-Item -LiteralPath (Get-Location).Path
while ($projectDir -and -not (Test-Path (Join-Path $projectDir.FullName 'ProjectSettings/ProjectVersion.txt'))) {
    $projectDir = $projectDir.Parent
}
if (-not $projectDir) { throw 'Run this skill from a Unity project directory or one of its subdirectories.' }
$root = $projectDir.FullName

function Get-UnityEditorDir {
    if ($env:UNITY_EDITOR_DIR -and (Test-Path $env:UNITY_EDITOR_DIR)) { return $env:UNITY_EDITOR_DIR }

    $versionLine = Get-Content (Join-Path $root 'ProjectSettings\ProjectVersion.txt') | Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1
    $version = ($versionLine -split ':', 2)[1].Trim()

    $candidates = New-Object System.Collections.Generic.List[string]
    $hubJson = Join-Path $env:APPDATA 'UnityHub\editors-v2.json'
    if (Test-Path $hubJson) {
        $hub = Get-Content $hubJson -Raw | ConvertFrom-Json
        foreach ($e in $hub.data) {
            if ($e.version -eq $version) { foreach ($loc in $e.location) { $candidates.Add((Split-Path $loc -Parent)) } }
        }
    }
    $candidates.Add("C:\Program Files\Unity\Hub\Editor\$version\Editor")
    $candidates.Add("C:\Program Files\Unity $version\Editor")

    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'Data\MonoBleedingEdge\bin\mono.exe')) { return $dir }
    }
    throw "Unity $version editor not found. Set env UNITY_EDITOR_DIR to the folder containing Unity.exe"
}

function Get-LookupExe([string]$data) {
    $src = Join-Path $PSScriptRoot 'ConfigLookup.cs'
    $outDir = Join-Path $root 'Library\ConfigLookup'
    $exe = Join-Path $outDir 'ConfigLookup.exe'
    if ((Test-Path $exe) -and ((Get-Item $exe).LastWriteTimeUtc -ge (Get-Item $src).LastWriteTimeUtc)) { return $exe }

    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $api = Join-Path $data 'MonoBleedingEdge\lib\mono\4.7.1-api'
    $cscArgs = @(
        (Join-Path $data 'DotNetSdkRoslyn\csc.dll'), '-nologo', '-noconfig', '-nostdlib+', '-target:exe', '-langversion:7.3', "-out:$exe",
        "-r:$api\mscorlib.dll", "-r:$api\System.dll", "-r:$api\System.Core.dll", $src
    )
    $output = & (Join-Path $data 'NetCoreRuntime\dotnet.exe') @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ConfigLookup build failed:`n$($output -join "`n")" }
    return $exe
}

function Invoke-Lookup([string[]]$lookupArgs) {
    $data = Join-Path (Get-UnityEditorDir) 'Data'
    $exe = Get-LookupExe $data
    $mono = Join-Path $data 'MonoBleedingEdge\bin\mono.exe'
    $allArgs = @($exe, $root, $data) + $lookupArgs
    if ($Full) { $allArgs += '--full' }
    $lines = & $mono @allArgs
    return ($lines -join "`n")
}

function Format-Ms([long]$ms) {
    if ($ms -le 0) { return $null }
    return [DateTimeOffset]::FromUnixTimeMilliseconds($ms).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss')
}

if ($Cmd -ne 'activity') {
    $lookupArgs = @($Cmd)
    if ($Table) { $lookupArgs += $Table }
    if ($Rest) { $lookupArgs += $Rest }
    Write-Output (Invoke-Lookup $lookupArgs)
    return
}

# activity: activityId -> runtime ActMainConfig (Type/ChildType) -> GameSystem name + LanguageWord Activity_Name
$ri = Join-Path $PSScriptRoot '..\..\runtime-inspect\scripts\ri.ps1'
$raw = & $ri get -Name ActivityDataModel -Path 'ActMainConfigs' -Depth 2 -Limit 5000
$resp = $raw | ConvertFrom-Json
if (-not $resp.ok) { Write-Output $raw; return }

$configs = @($resp.data.value)
if ($resp.data.value.PSObject.Properties.Name -contains '$items') { $configs = @($resp.data.value.'$items') }

$wanted = @()
foreach ($s in @($Table) + @($Rest)) { if ($s) { $wanted += ($s -split '[,\s]+') | Where-Object { $_ } | ForEach-Object { [long]$_ } } }
if ($wanted.Count -gt 0) { $configs = @($configs | Where-Object { $wanted -contains [long]$_.ActivityId }) }

$types = @($configs | ForEach-Object { $_.Type } | Sort-Object -Unique)
$systemNames = @{}
if ($types.Count -gt 0) {
    $sys = (Invoke-Lookup (@('get', 'GameSystem') + ($types -join ','))) | ConvertFrom-Json
    foreach ($it in $sys.items) { if ($it.found) { $systemNames[[string]$it.id] = $it.name } }
}

$wordKeys = @($configs | ForEach-Object { "Activity_Name-$($_.Type)-$($_.ChildType)" } | Sort-Object -Unique)
$wordNames = @{}
if ($wordKeys.Count -gt 0) {
    $words = (Invoke-Lookup (@('get', 'LanguageWord') + ($wordKeys -join ','))) | ConvertFrom-Json
    foreach ($it in $words.items) { if ($it.found) { $wordNames[[string]$it.id] = $it.name } }
}

$items = foreach ($c in $configs) {
    [ordered]@{
        activityId   = $c.ActivityId
        type         = $c.Type
        childType    = $c.ChildType
        systemName   = $systemNames[[string]$c.Type]
        activityName = $wordNames["Activity_Name-$($c.Type)-$($c.ChildType)"]
        begin        = Format-Ms $c.BeginTime
        end          = Format-Ms $c.EndTime
        showEnd      = Format-Ms $c.EndShowTime
    }
}
$missing = @($wanted | Where-Object { $id = $_; -not ($configs | Where-Object { [long]$_.ActivityId -eq $id }) })

$result = [ordered]@{ ok = $true; count = @($items).Count; items = @($items); notDelivered = $missing }
Write-Output ($result | ConvertTo-Json -Depth 5 -Compress)
