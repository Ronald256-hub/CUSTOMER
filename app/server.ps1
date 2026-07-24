param(
    [int]$Port = 8765,
    [string]$DataDir = ""
)

$ErrorActionPreference = "Stop"
$AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$WebRoot = Join-Path $AppRoot "web"
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $env:LOCALAPPDATA "ROBO CASK TAP POS\Data"
}
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$BackupDir = Join-Path $DataDir "Backups"
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$StateFile = Join-Path $DataDir "state.json"
$LogFile = Join-Path $DataDir "server.log"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-Log([string]$Message) {
    try { [System.IO.File]::AppendAllText($LogFile, "$(Get-Date -Format s) $Message`r`n", $Utf8NoBom) } catch {}
}
function Bytes([string]$Text) { return $Utf8NoBom.GetBytes($Text) }
function Mime-Type([string]$Path) {
    switch ([System.IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        ".html" { "text/html; charset=utf-8" }
        ".css"  { "text/css; charset=utf-8" }
        ".js"   { "application/javascript; charset=utf-8" }
        ".json" { "application/json; charset=utf-8" }
        ".png"  { "image/png" }
        ".ico"  { "image/x-icon" }
        ".svg"  { "image/svg+xml" }
        default  { "application/octet-stream" }
    }
}
function Send-Response($Stream, [int]$Status, [string]$ContentType, [byte[]]$Body, [hashtable]$Headers = @{}) {
    $reason = switch ($Status) { 200 {"OK"} 201 {"Created"} 204 {"No Content"} 400 {"Bad Request"} 404 {"Not Found"} 405 {"Method Not Allowed"} 413 {"Payload Too Large"} 500 {"Internal Server Error"} default {"OK"} }
    $head = "HTTP/1.1 $Status $reason`r`nContent-Type: $ContentType`r`nContent-Length: $($Body.Length)`r`nConnection: close`r`nCache-Control: no-store`r`n"
    foreach ($key in $Headers.Keys) { $head += "$key`: $($Headers[$key])`r`n" }
    $head += "`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
    $Stream.Write($headBytes, 0, $headBytes.Length)
    if ($Body.Length -gt 0) { $Stream.Write($Body, 0, $Body.Length) }
    $Stream.Flush()
}
function Send-Json($Stream, [int]$Status, $Object) {
    $json = $Object | ConvertTo-Json -Depth 100 -Compress
    Send-Response $Stream $Status "application/json; charset=utf-8" (Bytes $json)
}
function Find-HeaderEnd([byte[]]$Data) {
    for ($i = 0; $i -le $Data.Length - 4; $i++) {
        if ($Data[$i] -eq 13 -and $Data[$i+1] -eq 10 -and $Data[$i+2] -eq 13 -and $Data[$i+3] -eq 10) { return $i }
    }
    return -1
}
function Read-Request($Client) {
    $stream = $Client.GetStream()
    $stream.ReadTimeout = 15000
    $buffer = New-Object byte[] 16384
    $memory = New-Object System.IO.MemoryStream
    $headerEnd = -1
    $contentLength = 0
    while ($true) {
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { break }
        $memory.Write($buffer, 0, $read)
        if ($memory.Length -gt 12582912) { throw "Request is too large." }
        $bytesNow = $memory.ToArray()
        if ($headerEnd -lt 0) {
            $headerEnd = Find-HeaderEnd $bytesNow
            if ($headerEnd -ge 0) {
                $headerText = [System.Text.Encoding]::ASCII.GetString($bytesNow, 0, $headerEnd)
                foreach ($line in ($headerText -split "`r`n")) {
                    if ($line -match '^Content-Length:\s*(\d+)') { $contentLength = [int]$Matches[1] }
                }
            }
        }
        if ($headerEnd -ge 0 -and $memory.Length -ge ($headerEnd + 4 + $contentLength)) { break }
    }
    $all = $memory.ToArray()
    if ($headerEnd -lt 0) { throw "Invalid HTTP request." }
    $headerText = [System.Text.Encoding]::ASCII.GetString($all, 0, $headerEnd)
    $lines = $headerText -split "`r`n"
    $requestLine = $lines[0] -split " "
    if ($requestLine.Length -lt 2) { throw "Invalid request line." }
    $headers = @{}
    foreach ($line in $lines[1..($lines.Length-1)]) {
        $colon = $line.IndexOf(":")
        if ($colon -gt 0) { $headers[$line.Substring(0,$colon).Trim().ToLowerInvariant()] = $line.Substring($colon+1).Trim() }
    }
    $body = ""
    if ($contentLength -gt 0) { $body = $Utf8NoBom.GetString($all, $headerEnd+4, $contentLength) }
    return @{ Stream=$stream; Method=$requestLine[0].ToUpperInvariant(); Target=$requestLine[1]; Headers=$headers; Body=$body }
}
function Save-StateObject($Object) {
    $json = $Object | ConvertTo-Json -Depth 100
    $temp = "$StateFile.tmp"
    [System.IO.File]::WriteAllText($temp, $json, $Utf8NoBom)
    if (Test-Path $StateFile) {
        $old = "$StateFile.old"
        Copy-Item $StateFile $old -Force
    }
    Move-Item $temp $StateFile -Force
}

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $Port)
try {
    $listener.Start()
    Write-Log "ROBO POS server started on port $Port. DataDir=$DataDir"
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $request = Read-Request $client
            $stream = $request.Stream
            $method = $request.Method
            $target = $request.Target
            $path = ($target -split '\?')[0]

            if ($path -eq "/api/health" -and $method -eq "GET") {
                Send-Json $stream 200 @{ ok=$true; app="ROBO CASK & TAP POS"; version="2.0.0"; port=$Port }
            }
            elseif ($path -eq "/api/state" -and $method -eq "GET") {
                if (Test-Path $StateFile) {
                    try {
                        $stateText = [System.IO.File]::ReadAllText($StateFile, $Utf8NoBom)
                        $null = $stateText | ConvertFrom-Json
                        $payload = "{`"state`":$stateText}"
                        Send-Response $stream 200 "application/json; charset=utf-8" (Bytes $payload)
                    } catch {
                        Write-Log "State read error: $($_.Exception.Message)"
                        Send-Json $stream 500 @{ error="The business data file could not be read. A previous copy may be available in the Data folder." }
                    }
                } else { Send-Json $stream 200 @{ state=$null } }
            }
            elseif ($path -eq "/api/state" -and $method -eq "POST") {
                try {
                    $wrapper = $request.Body | ConvertFrom-Json
                    if ($null -eq $wrapper.state -or $null -eq $wrapper.state.meta) { throw "Missing state data." }
                    Save-StateObject $wrapper.state
                    Send-Json $stream 200 @{ ok=$true; savedAt=(Get-Date).ToString("o") }
                } catch {
                    Write-Log "State save error: $($_.Exception.Message)"
                    Send-Json $stream 400 @{ error="The supplied data was invalid and was not saved." }
                }
            }
            elseif ($path -eq "/api/backup" -and $method -eq "POST") {
                try {
                    if (-not (Test-Path $StateFile)) { throw "No data file exists yet." }
                    $name = "ROBO_BACKUP_$(Get-Date -Format 'yyyyMMdd_HHmmss').json"
                    Copy-Item $StateFile (Join-Path $BackupDir $name) -Force
                    Send-Json $stream 200 @{ ok=$true; fileName=$name }
                } catch { Send-Json $stream 500 @{ error=$_.Exception.Message } }
            }
            elseif ($method -eq "GET") {
                $relative = if ($path -eq "/") { "index.html" } else { $path.TrimStart("/") }
                $relative = [Uri]::UnescapeDataString($relative).Replace("/", [IO.Path]::DirectorySeparatorChar)
                if ($relative.Contains("..")) { Send-Json $stream 404 @{ error="Not found" } }
                else {
                    $file = Join-Path $WebRoot $relative
                    $resolvedRoot = [IO.Path]::GetFullPath($WebRoot)
                    $resolvedFile = [IO.Path]::GetFullPath($file)
                    if (-not $resolvedFile.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path $resolvedFile -PathType Leaf)) {
                        Send-Json $stream 404 @{ error="Not found" }
                    } else {
                        $fileBytes = [IO.File]::ReadAllBytes($resolvedFile)
                        Send-Response $stream 200 (Mime-Type $resolvedFile) $fileBytes
                    }
                }
            }
            else { Send-Json $stream 405 @{ error="Method not allowed" } }
        } catch {
            Write-Log "Request error: $($_.Exception.Message)"
            try { Send-Json ($client.GetStream()) 500 @{ error="The local ROBO POS service encountered an error." } } catch {}
        } finally {
            try { $client.Close() } catch {}
        }
    }
} catch {
    Write-Log "Fatal server error: $($_.Exception.ToString())"
    throw
} finally {
    try { $listener.Stop() } catch {}
}
