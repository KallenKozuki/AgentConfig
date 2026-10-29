<#
RuntimeInspector client. Talks to the Editor-only HTTP service in Assets/Editor/RuntimeInspector.
Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less scripts with the ANSI code page.

Examples:
  ri.ps1 ping
  ri.ps1 models -Filter WestOcean
  ri.ps1 get -Name WestOceanDataModel -Path 'UserData.Event' -Depth 3
  ri.ps1 search -Name WestOceanDataModel -Key energy
  ri.ps1 search -Value 1001
  ri.ps1 set -Name WestOceanDataModel -Path 'UserData.Energy' -Value 100
  ri.ps1 col -Name BagDataModel -Path 'itemDic' -Op add -Key 1001 -Value '{"Id":1001,"Num":5}'
  ri.ps1 ui -Filter Main
  ri.ps1 get -UI MainView -Path 'someField'
  ri.ps1 watch add -Name AssistantDataModel -Path 'ServerSettings' [-Depth 3] [-Interval 1]
  ri.ps1 watch list | watch history -Id 1 [-Full] | watch remove -Id 1 | watch clear
  ri.ps1 event list -Filter Assistant
  ri.ps1 event schema -Event PlayerAssistantDataEventArgs
  ri.ps1 event dispatch -Event PlayerAssistantDataEventArgs -ValueFile args.json [-Wait 30] [-Watch 'BagDataModel,ui:MainView'] [-Depth 4]
#>
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('ping', 'models', 'get', 'search', 'set', 'col', 'ui', 'watch', 'event')]
    [string]$Cmd,
    [Parameter(Position = 1)]
    [string]$Sub,
    [string]$Name,
    [string]$UI,
    [Alias('Event')]
    [string]$EventName,
    [int]$Id = 0,
    [int]$Interval = -1,
    [int]$Wait = -1,
    [string]$Watch,
    [switch]$Full,
    [string]$ValueFile,
    [string]$Path,
    [int]$Depth = -1,
    [int]$Limit = -1,
    [int]$Max = -1,
    [switch]$Props,
    [string]$Filter,
    [string]$Key,
    [string]$Value,
    [string]$Op,
    [int]$Index = -1,
    [switch]$Force,
    [switch]$Merge,
    [switch]$Setter,
    [switch]$Pretty,
    [string]$Out,
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Get-InspectorPort {
    if ($Port -gt 0) { return $Port }
    $projectDir = Get-Item -LiteralPath (Get-Location).Path
while ($projectDir -and -not (Test-Path (Join-Path $projectDir.FullName 'ProjectSettings/ProjectVersion.txt'))) {
    $projectDir = $projectDir.Parent
}
if (-not $projectDir) { throw 'Run this skill from a Unity project directory or one of its subdirectories.' }
$root = $projectDir.FullName
    $portFile = Join-Path $root 'Library\RuntimeInspector\port.txt'
    if (Test-Path $portFile) { return [int](Get-Content $portFile -Raw).Trim() }
    return 17890
}

function Add-Query([System.Collections.Generic.List[string]]$list, [string]$k, $v) {
    if ($null -eq $v -or "$v" -eq '' -or "$v" -eq '-1') { return }
    $list.Add($k + '=' + [Uri]::EscapeDataString("$v"))
}

function ConvertTo-JsonString([string]$s) {
    if ($null -eq $s) { return 'null' }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($c in $s.ToCharArray()) {
        switch ($c) {
            '"' { [void]$sb.Append('\"') }
            '\' { [void]$sb.Append('\\') }
            "`n" { [void]$sb.Append('\n') }
            "`r" { [void]$sb.Append('\r') }
            "`t" { [void]$sb.Append('\t') }
            default { [void]$sb.Append($c) }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# Value / Key are JSON text; anything that is not valid JSON is sent as a JSON string.
function ConvertTo-JsonValue([string]$text) {
    if ($null -eq $text) { return 'null' }
    $t = $text.Trim()
    if ($t -match '^(-?\d+(\.\d+)?([eE][+-]?\d+)?|true|false|null)$' -or $t.StartsWith('{') -or $t.StartsWith('[') -or $t.StartsWith('"')) {
        return $t
    }
    return ConvertTo-JsonString $text
}

function Invoke-Inspector([string]$method, [string]$url, [string]$body) {
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = $method
    $req.Proxy = $null
    $req.Timeout = 30000
    $req.ReadWriteTimeout = 30000
    if ($body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $req.ContentType = 'application/json; charset=utf-8'
        $req.ContentLength = $bytes.Length
        $stream = $req.GetRequestStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Close()
    }
    try {
        $resp = $req.GetResponse()
    }
    catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($null -eq $resp) {
            return '{"ok":false,"error":"Cannot connect to RuntimeInspector at ' + $url + '. Is Unity Editor open with Assets/Editor/RuntimeInspector compiled? Try menu Tools/RuntimeInspector/Restart. Detail: ' + ($_.Exception.Message -replace '"', "'") + '"}'
        }
    }
    $reader = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd()
    $reader.Close()
    $resp.Close()
    return $text
}

function Get-TargetParts {
    $parts = @()
    if ($Name) { $parts += '"name":' + (ConvertTo-JsonString $Name) }
    if ($UI) { $parts += '"ui":' + (ConvertTo-JsonString $UI) }
    return $parts
}

$hasValue = $PSBoundParameters.ContainsKey('Value')
if ($ValueFile) {
    $Value = [IO.File]::ReadAllText([IO.Path]::GetFullPath((Join-Path (Get-Location) $ValueFile)), [Text.Encoding]::UTF8)
    $hasValue = $true
}

$base = "http://127.0.0.1:$(Get-InspectorPort)"
$query = New-Object 'System.Collections.Generic.List[string]'
if ($Pretty) { $query.Add('pretty=1') }
$method = 'GET'
$body = $null

switch ($Cmd) {
    'ping' { $route = '/ping' }
    'models' {
        $route = '/models'
        Add-Query $query 'filter' $Filter
    }
    'ui' {
        $route = '/ui'
        Add-Query $query 'filter' $Filter
    }
    'watch' {
        switch ($Sub) {
            'add' {
                $route = '/watch/add'
                $method = 'POST'
                $parts = @(Get-TargetParts) + @('"path":' + (ConvertTo-JsonString $Path))
                if ($Depth -ge 0) { $parts += '"depth":' + $Depth }
                if ($Interval -ge 0) { $parts += '"interval":' + $Interval }
                $body = '{' + ($parts -join ',') + '}'
            }
            'list' { $route = '/watch/list' }
            'history' {
                $route = '/watch/history'
                Add-Query $query 'id' $Id
                if ($Full) { $query.Add('full=1') }
            }
            'remove' {
                $route = '/watch/remove'
                $method = 'POST'
                $body = '{"id":' + $Id + '}'
            }
            'clear' {
                $route = '/watch/clear'
                $method = 'POST'
                $body = '{}'
            }
            default { throw 'watch requires a sub command: add / list / history / remove / clear' }
        }
    }
    'event' {
        switch ($Sub) {
            'list' {
                $route = '/event/list'
                Add-Query $query 'filter' $Filter
                Add-Query $query 'max' $Max
            }
            'schema' {
                $route = '/event/schema'
                Add-Query $query 'event' $EventName
            }
            'dispatch' {
                $route = '/event/dispatch'
                $method = 'POST'
                $parts = @('"event":' + (ConvertTo-JsonString $EventName))
                if ($hasValue) { $parts += '"value":' + (ConvertTo-JsonValue $Value) }
                if ($Wait -ge 0) { $parts += '"wait":' + $Wait }
                if ($Watch) { $parts += '"watch":' + (ConvertTo-JsonString $Watch) }
                if ($Depth -ge 0) { $parts += '"depth":' + $Depth }
                $body = '{' + ($parts -join ',') + '}'
            }
            default { throw 'event requires a sub command: list / schema / dispatch' }
        }
    }
    'get' {
        $route = '/model'
        Add-Query $query 'name' $Name
        Add-Query $query 'ui' $UI
        Add-Query $query 'path' $Path
        Add-Query $query 'depth' $Depth
        Add-Query $query 'limit' $Limit
        if ($Props) { $query.Add('props=1') }
    }
    'search' {
        $route = '/search'
        Add-Query $query 'name' $Name
        Add-Query $query 'ui' $UI
        Add-Query $query 'path' $Path
        Add-Query $query 'key' $Key
        Add-Query $query 'value' $Value
        Add-Query $query 'depth' $Depth
        Add-Query $query 'limit' $Limit
        Add-Query $query 'max' $Max
        if ($Props) { $query.Add('props=1') }
    }
    'set' {
        $route = '/set'
        $method = 'POST'
        if (-not $hasValue) { throw 'set requires -Value or -ValueFile (JSON text)' }
        $parts = @(Get-TargetParts) + @(
            ('"path":' + (ConvertTo-JsonString $Path)),
            ('"value":' + (ConvertTo-JsonValue $Value))
        )
        if ($Force) { $parts += '"force":true' }
        if ($Merge) { $parts += '"merge":true' }
        if ($Setter) { $parts += '"setter":true' }
        $body = '{' + ($parts -join ',') + '}'
    }
    'col' {
        $route = '/collection'
        $method = 'POST'
        $parts = @(Get-TargetParts) + @(
            ('"path":' + (ConvertTo-JsonString $Path)),
            ('"op":' + (ConvertTo-JsonString $Op))
        )
        if ($PSBoundParameters.ContainsKey('Key')) { $parts += '"key":' + (ConvertTo-JsonValue $Key) }
        if ($Index -ge 0) { $parts += '"index":' + $Index }
        if ($hasValue) { $parts += '"value":' + (ConvertTo-JsonValue $Value) }
        $body = '{' + ($parts -join ',') + '}'
    }
}

$url = $base + $route
if ($query.Count -gt 0) { $url += '?' + ($query -join '&') }
$result = Invoke-Inspector $method $url $body

if ($Out) {
    $full = [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
    [IO.File]::WriteAllText($full, $result, (New-Object Text.UTF8Encoding($false)))
    Write-Output "saved $($result.Length) chars to $full"
}
else {
    Write-Output $result
}
