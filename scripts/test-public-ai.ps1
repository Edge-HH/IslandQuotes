[CmdletBinding()]
param(
    # OpenAI-compatible chat-completions URL, e.g. https://api.openai.com/v1/chat/completions
    [Parameter(Mandatory = $true)]
    [string]$Endpoint,
    [Parameter(Mandatory = $true)]
    [string]$Model,
    # Defaults to the OPENAI_API_KEY environment variable; leave empty for keyless local endpoints.
    [string]$ApiKey = $env:OPENAI_API_KEY,
    [int]$TimeoutSeconds = 45
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Fail([string]$Message) {
    Write-Host ''
    Write-Host "PUBLIC AI TEST FAILED: $Message" -ForegroundColor Red
    exit 1
}

function Get-ErrorMessage([System.Exception]$Exception) {
    $parts = New-Object System.Collections.Generic.List[string]
    $current = $Exception
    while ($null -ne $current) {
        if (-not [string]::IsNullOrWhiteSpace($current.Message)) {
            $parts.Add($current.Message.Trim())
        }
        $current = $current.InnerException
    }
    return ($parts -join ' -> ')
}

function Read-HttpErrorBody($Response) {
    if ($null -eq $Response) { return '' }
    try {
        if ($null -ne $Response.PSObject.Methods['GetResponseStream']) {
            $stream = $Response.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }
        if ($null -ne $Response.Content -and $null -ne $Response.Content.PSObject.Methods['ReadAsStringAsync']) {
            return $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
    }
    catch {
        return ''
    }
    return ''
}

function Get-ResponseStatusCode($Response) {
    if ($null -eq $Response) { return 0 }
    try {
        if ($null -ne $Response.PSObject.Properties['StatusCode']) {
            return [int]$Response.StatusCode
        }
    }
    catch { }
    return 0
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
catch {
    # Newer PowerShell versions negotiate TLS automatically.
}

if ([string]::IsNullOrWhiteSpace($Endpoint)) {
    Fail 'Endpoint is empty.'
}
$uri = $null
if (-not [Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri)) {
    Fail "Endpoint is not an absolute URI: $Endpoint"
}
if ($TimeoutSeconds -lt 1) {
    Fail 'TimeoutSeconds must be at least 1.'
}

# This is deliberately identical in shape to the plugin's OpenAI-compatible
# chat-completions request. No credential is required for the public preset.
$payload = [ordered]@{
    model = $Model
    messages = @(
        [ordered]@{
            role = 'system'
            content = 'You are a connectivity probe. Follow the requested output format exactly.'
        },
        [ordered]@{
            role = 'user'
            content = 'Reply with a short plain-text sentence: connectivity ok'
        }
    )
    temperature = 0
    max_tokens = 64
    stream = $false
}
$jsonBody = $payload | ConvertTo-Json -Depth 8 -Compress

Write-Host 'Live AI connectivity test (OpenAI-compatible)' -ForegroundColor Cyan
Write-Host "Endpoint : $Endpoint"
Write-Host "Model    : $Model"
Write-Host ('Auth     : ' + $(if ([string]::IsNullOrWhiteSpace($ApiKey)) { 'none' } else { 'Bearer API key' }))
Write-Host 'Request  : POST application/json (OpenAI chat-completions shape)'
Write-Host ''

$headers = @{ Accept = 'application/json' }
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
    $headers['Authorization'] = "Bearer $ApiKey"
}

$statusCode = 0
$responseBody = ''
$transportError = ''
try {
    $response = Invoke-WebRequest -Uri $Endpoint -Method Post -ContentType 'application/json; charset=utf-8' `
        -Headers $headers -UserAgent 'IslandQuotes-Plugin-QA/1.0' `
        -Body $jsonBody -TimeoutSec $TimeoutSeconds -UseBasicParsing
    $statusCode = Get-ResponseStatusCode $response
    $responseBody = [string]$response.Content
}
catch {
    $transportError = Get-ErrorMessage $_.Exception
    $webResponse = $_.Exception.Response
    if ($null -ne $webResponse) {
        $statusCode = Get-ResponseStatusCode $webResponse
        $responseBody = Read-HttpErrorBody $webResponse
    }
}

Write-Host "HTTP status: $statusCode"
if (-not [string]::IsNullOrWhiteSpace($responseBody)) {
    $preview = ($responseBody -replace '\s+', ' ').Trim()
    if ($preview.Length -gt 700) {
        $preview = $preview.Substring(0, 700) + '… [truncated]'
    }
    Write-Host "Body prefix: $preview"
}
else {
    Write-Host 'Body prefix: <empty>'
}

if (-not [string]::IsNullOrWhiteSpace($transportError)) {
    Write-Host "Transport error: $transportError" -ForegroundColor Yellow
}
if ($statusCode -lt 200 -or $statusCode -ge 300) {
    if ($statusCode -eq 0) {
        Fail "No HTTP response was received. $transportError"
    }
    Fail "The endpoint returned HTTP $statusCode."
}
if ([string]::IsNullOrWhiteSpace($responseBody)) {
    Fail 'The endpoint returned a successful HTTP status with an empty body.'
}

try {
    $parsed = $responseBody | ConvertFrom-Json
}
catch {
    Fail 'The endpoint returned a non-JSON response despite HTTP success.'
}
if ($null -eq $parsed.PSObject.Properties['choices']) {
    Fail 'The JSON response is missing the OpenAI-compatible "choices" field.'
}
$choices = @($parsed.choices)
if ($choices.Count -eq 0 -or $null -eq $choices[0]) {
    Fail 'The JSON response contains no choices.'
}
$content = ''
if ($null -ne $choices[0].PSObject.Properties['message'] -and
    $null -ne $choices[0].message.PSObject.Properties['content']) {
    $content = [string]$choices[0].message.content
}
if ([string]::IsNullOrWhiteSpace($content)) {
    Fail 'The first choice has no assistant message content.'
}

Write-Host 'Response shape: OpenAI-compatible choices[0].message.content present.' -ForegroundColor Green
Write-Host 'PUBLIC AI TEST PASSED' -ForegroundColor Green
exit 0