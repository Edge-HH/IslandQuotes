[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$TestPublicAI
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:Passed = 0
$script:Failed = 0
$script:Warned = 0
$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-Pass([string]$Name) {
    $script:Passed++
    Write-Host "  [PASS] $Name" -ForegroundColor Green
}

function Add-Fail([string]$Name, [string]$Detail) {
    $script:Failed++
    $script:Failures.Add("$Name -- $Detail")
    Write-Host "  [FAIL] $Name" -ForegroundColor Red
    if (-not [string]::IsNullOrWhiteSpace($Detail)) {
        Write-Host "         $Detail" -ForegroundColor Red
    }
}

function Add-Warn([string]$Name, [string]$Detail) {
    $script:Warned++
    Write-Host "  [WARN] $Name" -ForegroundColor Yellow
    if (-not [string]::IsNullOrWhiteSpace($Detail)) {
        Write-Host "         $Detail" -ForegroundColor Yellow
    }
}

function Add-Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { Add-Pass $Name } else { Add-Fail $Name $Detail }
}

function Read-Utf8([string]$Path) {
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Assert-File([string]$RelativePath, [long]$MinimumBytes = 1) {
    $full = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Add-Fail "required file: $RelativePath" "missing"
        return $null
    }
    $item = Get-Item -LiteralPath $full
    if ($item.Length -lt $MinimumBytes) {
        Add-Fail "required file: $RelativePath" "file is only $($item.Length) bytes"
        return $null
    }
    Add-Pass "required file: $RelativePath"
    return $full
}

function Assert-TextContains([string]$Name, [string]$Text, [string]$Needle) {
    $ok = $Text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0
    Add-Check $Name $ok "missing expected token: $Needle"
}

function Assert-TextMatches([string]$Name, [string]$Text, [string]$Pattern) {
    $ok = [regex]::IsMatch($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Add-Check $Name $ok "text did not match required pattern: $Pattern"
}

function Get-ManifestField([string]$Path, [string]$Key) {
    $text = Read-Utf8 $Path
    $pattern = '(?m)^\s*' + [regex]::Escape($Key) + '\s*:\s*(?<value>[^\r\n]*)\s*$'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) { return $null }
    return $match.Groups['value'].Value.Trim().Trim('"').Trim("'")
}

function Get-BigEndianUInt32([byte[]]$Bytes, [int]$Offset) {
    [uint32]$value = 0
    for ($i = 0; $i -lt 4; $i++) {
        $value = [uint32](($value -shl 8) -bor [uint32]$Bytes[$Offset + $i])
    }
    return $value
}

Write-Host ''
Write-Host 'Motivation acceptance verification' -ForegroundColor Cyan
Write-Host "Repository : $repoRoot"
Write-Host "Config     : $Configuration"
Write-Host "Build      : $(-not $SkipBuild)"
Write-Host "Live AI    : $TestPublicAI"
Write-Host ''

Write-Host '[1/7] Required files and manifest' -ForegroundColor Cyan
$manifestPath = Assert-File 'manifest.yml' 50
$projectPath = Assert-File 'Motivation.csproj' 200
$pluginPath = Assert-File 'Plugin.cs' 100
$readmePath = Assert-File 'README.md' 500
$userGuidePath = Assert-File 'docs\USER_GUIDE.md' 500
$privacyPath = Assert-File 'docs\PRIVACY_SECURITY.md' 500
$iconPath = Assert-File 'icon.png' 1000
$assemblySourcePath = Assert-File 'Services\AiReviewService.cs' 100
$queueSourcePath = Assert-File 'Services\ReviewQueueService.cs' 100

if ($null -ne $manifestPath) {
    $manifestText = Read-Utf8 $manifestPath
    $manifestRequired = @('id', 'name', 'description', 'entranceAssembly', 'url', 'version', 'apiVersion', 'author', 'repoOwner', 'repoName', 'assetsRoot')
    $missingFields = @()
    foreach ($field in $manifestRequired) {
        $value = Get-ManifestField $manifestPath $field
        if ([string]::IsNullOrWhiteSpace($value)) { $missingFields += $field }
    }
    Add-Check 'manifest has required fields' ($missingFields.Count -eq 0) ("missing/empty: " + ($missingFields -join ', '))

    $manifestChecks = @(
        @{ Name = "manifest id is ink.motivation.quoteai"; Actual = (Get-ManifestField $manifestPath 'id'); Expected = 'ink.motivation.quoteai' },
        @{ Name = 'manifest entranceAssembly is Motivation.dll'; Actual = (Get-ManifestField $manifestPath 'entranceAssembly'); Expected = 'Motivation.dll' }
    )
    foreach ($check in $manifestChecks) {
        Add-Check $check.Name ([string]::Equals([string]$check.Actual, [string]$check.Expected, [StringComparison]::Ordinal)) "expected '$($check.Expected)', got '$($check.Actual)'"
    }
    $nameValue = Get-ManifestField $manifestPath 'name'
    Add-Check 'manifest name contains Motivation' ($nameValue -match 'Motivation') "name='$nameValue'"
    $versionValue = Get-ManifestField $manifestPath 'version'
    Add-Check 'manifest version is four-part numeric' ($versionValue -match '^\d+\.\d+\.\d+\.\d+$') "version='$versionValue'"
    $apiVersionValue = Get-ManifestField $manifestPath 'apiVersion'
    Add-Check 'manifest apiVersion is v2' ($apiVersionValue -match '^2\.') "apiVersion='$apiVersionValue'"
    $urlValue = Get-ManifestField $manifestPath 'url'
    Add-Check 'manifest url is http(s)' ($urlValue -match '^https?://') "url='$urlValue'"
    $manifestText | Out-Null
}

if ($null -ne $projectPath) {
    $projectText = Read-Utf8 $projectPath
    try {
        [xml]$projectXml = $projectText
        Add-Pass 'Motivation.csproj is well-formed XML'
    }
    catch {
        Add-Fail 'Motivation.csproj is well-formed XML' $_.Exception.Message
    }
    Assert-TextContains 'project targets net10.0-windows' $projectText '<TargetFramework>net10.0-windows</TargetFramework>'
    Assert-TextContains 'project assembly name is Motivation' $projectText '<AssemblyName>Motivation</AssemblyName>'
    Assert-TextContains 'project references ClassIsland.PluginSdk' $projectText 'ClassIsland.PluginSdk'
    Assert-TextContains 'project copies manifest.yml' $projectText 'manifest.yml'
    Assert-TextContains 'project copies icon.png' $projectText 'icon.png'
}

if ($null -ne $pluginPath) {
    $pluginText = Read-Utf8 $pluginPath
    Assert-TextContains 'plugin has PluginEntrance attribute' $pluginText '[PluginEntrance]'
    Assert-TextContains 'plugin derives from PluginBase' $pluginText ': PluginBase'
    Assert-TextContains 'plugin initializes PluginRuntime' $pluginText 'PluginRuntime.Initialize(PluginConfigFolder)'
    Assert-TextContains 'plugin registers quote component' $pluginText 'AddComponent<QuoteAiComponent, QuoteAiSettings>'
    Assert-TextContains 'plugin registers settings pages' $pluginText 'AddSettingsPage<AiSettingsPage>'
}

if ($null -ne $iconPath) {
    [byte[]]$iconBytes = [System.IO.File]::ReadAllBytes($iconPath)
    $signature = ($iconBytes[0..7] | ForEach-Object { $_.ToString('X2') }) -join ' '
    $isPng = $signature -eq '89 50 4E 47 0D 0A 1A 0A'
    Add-Check 'icon.png has a real PNG signature' $isPng "signature=$signature"
    if ($iconBytes.Length -ge 24) {
        $width = Get-BigEndianUInt32 $iconBytes 16
        $height = Get-BigEndianUInt32 $iconBytes 20
        Add-Check 'icon.png is 256x256' (($width -eq 256) -and ($height -eq 256)) "dimensions=${width}x${height}"
    }
}
Write-Host '[2/7] Source feature contracts' -ForegroundColor Cyan
$sourceRoots = @('Core', 'Models', 'Services', 'Components', 'SettingsPages', 'ViewModels')
$sourceFiles = @()
foreach ($rootName in $sourceRoots) {
    $rootPath = Join-Path $repoRoot $rootName
    if (Test-Path -LiteralPath $rootPath -PathType Container) {
        $sourceFiles += @(Get-ChildItem -LiteralPath $rootPath -Recurse -File |
            Where-Object { $_.Extension -in '.cs', '.axaml', '.xaml' })
    }
}
$codeText = ''
if ($sourceFiles.Count -gt 0) {
    $codeText = (($sourceFiles | ForEach-Object { Read-Utf8 $_.FullName }) -join "`n")
}

$featureFiles = @(
    'Services\AiReviewService.cs',
    'Services\AiReviewPrompt.cs',
    'Services\AiProviderPresetCatalog.cs',
    'Services\AuditLogService.cs',
    'Services\PluginSettingsStore.cs',
    'Services\QuoteSourceService.cs',
    'Services\RhesisProviders.cs',
    'Services\ReviewQueueService.cs',
    'Core\PluginRuntime.cs',
    'Components\QuoteAi\QuoteAiComponent.axaml',
    'SettingsPages\AiSettingsPage.axaml',
    'SettingsPages\ReviewQueuePage.axaml'
)
$missingFeatureFiles = @()
foreach ($relative in $featureFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Leaf)) {
        $missingFeatureFiles += $relative
    }
}
Add-Check 'all feature source files exist' ($missingFeatureFiles.Count -eq 0) ("missing: " + ($missingFeatureFiles -join ', '))

$providerClasses = @('HitokotoProvider', 'JinrishiciProvider', 'DlystcProvider', 'SainticProvider')
$missingProviders = @($providerClasses | Where-Object { $codeText.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
Add-Check 'four extracted quote providers are implemented' ($missingProviders.Count -eq 0) ("missing: " + ($missingProviders -join ', '))

$presetIds = @(
    'pollinations-openai', 'openai', 'deepseek', 'moonshot', 'dashscope-qwen',
    'zhipu-glm', 'siliconflow', 'openrouter-free', 'groq', 'together',
    'google-gemini', 'anthropic', 'ollama-local', 'custom-openai-compatible'
)
$missingPresets = @($presetIds | Where-Object { $codeText.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -lt 0 })
Add-Check 'all required AI provider presets are present' ($missingPresets.Count -eq 0) ("missing: " + ($missingPresets -join ', '))
foreach ($category in @('AiProviderCategory.PublicFree', 'AiProviderCategory.FreeTier', 'AiProviderCategory.Paid', 'AiProviderCategory.Local', 'AiProviderCategory.Custom')) {
    Assert-TextContains "provider category: $category" $codeText $category
}
foreach ($protocol in @('AiProtocol.OpenAiCompatible', 'AiProtocol.AnthropicMessages', 'AiProtocol.GeminiGenerateContent')) {
    Assert-TextContains "protocol support: $protocol" $codeText $protocol
}
Assert-TextContains 'Pollinations anonymous endpoint' $codeText 'https://text.pollinations.ai/openai'
Assert-TextContains 'Pollinations preset is public/free' $codeText 'AiProviderCategory.PublicFree'
Assert-TextContains 'Pollinations preset does not require a key' $codeText 'requiresApiKey: false'
Assert-TextContains 'content-derived upstream IDs are supported' $codeText 'content-sha256'
Assert-TextContains 'upstream ID type is tracked' $codeText 'UpstreamIdKind'

$queueText = Read-Utf8 (Join-Path $repoRoot 'Services\ReviewQueueService.cs')
$aiText = Read-Utf8 (Join-Path $repoRoot 'Services\AiReviewService.cs')
$auditText = Read-Utf8 (Join-Path $repoRoot 'Services\AuditLogService.cs')
$sourceServiceText = Read-Utf8 (Join-Path $repoRoot 'Services\QuoteSourceService.cs')

Assert-TextContains 'queue has a manual review collection' $queueText 'PendingManualReview'
Assert-TextContains 'queue has a FIFO playback collection' $queueText 'PlaybackQueue'
Assert-TextContains 'queue supports individual approve' $queueText 'public void Approve('
Assert-TextContains 'queue supports individual reject' $queueText 'public void Reject('
Assert-TextContains 'queue supports approve selected' $queueText 'ApproveSelected('
Assert-TextContains 'queue supports reject selected' $queueText 'RejectSelected('
Assert-TextContains 'queue supports approve all' $queueText 'ApproveAllPending('
Assert-TextContains 'queue supports reject all' $queueText 'RejectAllPending('
Assert-TextContains 'queue supports batch refresh' $queueText 'RefreshBatchAsync('
Assert-TextContains 'queue bounds AI concurrency' $queueText 'MaxConcurrentReviews'
Assert-TextContains 'queue enforces max buffered items' $queueText 'MaxBufferedItems'
Assert-TextContains 'queue persists review-queue.json' $queueText 'review-queue.json'

$lowPattern = 'case RiskLevel\.Low:\s*item\.State = ReviewItemState\.Approved;\s*decision = "auto_pass";'
$mediumPattern = 'case RiskLevel\.Medium:\s*item\.State = ReviewItemState\.AwaitingManualReview;\s*decision = "manual_required";'
$highPattern = 'case RiskLevel\.High:\s*item\.State = ReviewItemState\.Blocked;\s*decision = "auto_block";'
$failurePattern = 'else if \(!aiSucceeded\).*?ReviewFailureAction\.Block.*?ReviewItemState\.Blocked.*?ReviewItemState\.AwaitingManualReview.*?decision = "ai_failure";'
Assert-TextMatches 'low risk maps to auto_pass' $queueText $lowPattern
Assert-TextMatches 'medium risk maps to manual review' $queueText $mediumPattern
Assert-TextMatches 'high risk maps to auto_block' $queueText $highPattern
Assert-TextMatches 'AI failure is fail-closed and logs ai_failure' $queueText $failurePattern
Assert-TextContains 'AI failures are never returned as success' $aiText 'IsSuccess = false'
Assert-TextContains 'AI parser falls back to RiskLevel.Unknown' $aiText 'Risk = RiskLevel.Unknown'
Assert-TextContains 'local ignore terms are enforced' $queueText 'local_ignore'
Assert-TextContains 'queue restores Pending/Reviewing safely' $queueText 'case ReviewItemState.Reviewing:'
Assert-TextContains 'restored unreviewed items become manual review' $queueText 'item.State = ReviewItemState.AwaitingManualReview;'

$fifoChecks = @(
    @{ Name = 'FIFO dequeues from index zero'; Needle = 'PlaybackQueue[0]' },
    @{ Name = 'FIFO removes from index zero'; Needle = 'PlaybackQueue.RemoveAt(0)' },
    @{ Name = 'approved items are appended'; Needle = 'PlaybackQueue.Add(item)' },
    @{ Name = 'no insertion at queue head'; Needle = 'PlaybackQueue.Insert(' }
)
for ($i = 0; $i -lt $fifoChecks.Count; $i++) {
    $check = $fifoChecks[$i]
    $contains = $queueText.IndexOf($check.Needle, [StringComparison]::Ordinal) -ge 0
    if ($i -lt 3) {
        Add-Check $check.Name $contains "missing: $($check.Needle)"
    }
    else {
        Add-Check $check.Name (-not $contains) "unexpected head insertion: $($check.Needle)"
    }
}
Write-Host '[3/7] Audit logging and key redaction contracts' -ForegroundColor Cyan
Assert-TextContains 'audit directory is under plugin config log' $auditText 'Path.Combine(pluginConfigFolder, "log")'
Assert-TextContains 'audit file prefix is audit-' $auditText '"audit-"'
Assert-TextContains 'audit files use .jsonl' $auditText '".jsonl"'
Assert-TextContains 'audit writes UTF-8 without BOM' $auditText 'Utf8NoBom'
Assert-TextContains 'audit redacts sensitive assignments' $auditText 'SensitiveAssignment'
Assert-TextContains 'audit redacts Bearer values' $auditText 'BearerValue'
Assert-TextContains 'audit has correlation IDs' $auditText 'CorrelationId'
Assert-TextContains 'quote requests are audited' $sourceServiceText 'Stage = "quote_request"'
Assert-TextContains 'AI requests are audited' $aiText 'Stage = "ai_request"'
Assert-TextContains 'AI reviews are audited' $aiText 'Stage = "ai_review"'
Assert-TextContains 'decisions are audited' $queueText 'Stage = "decision"'
Assert-TextContains 'manual reviews are audited' $queueText 'Stage = "manual_review"'
Assert-TextContains 'playback is audited' $queueText 'Stage = "playback"'
Assert-TextContains 'queue restoration is audited' $queueText 'Stage = "queue_restored"'
Assert-TextContains 'AI request IDs are captured' $aiText 'AiRequestId'
Assert-TextContains 'upstream IDs are captured in logs' $auditText 'UpstreamId'
Assert-TextContains 'AI request audit includes user prompt' $aiText '["userPrompt"]'
Assert-TextContains 'AI review audit includes response body' $aiText '["responseBody"]'
Assert-TextContains 'log full prompt setting is honored' $aiText 'FullPrompts'
Assert-TextContains 'log full response setting is honored' $aiText 'FullResponses'

$presetText = Read-Utf8 (Join-Path $repoRoot 'Services\AiProviderPresetCatalog.cs')
$hardcodedPresetKey = [regex]::IsMatch($presetText, 'ApiKey\s*=\s*"[^"\r\n]+"')
Add-Check 'built-in preset API key fields are blank' (-not $hardcodedPresetKey) 'preset catalog contains a non-empty ApiKey literal'

Write-Host '[4/7] Documentation acceptance tokens' -ForegroundColor Cyan
$readmeText = Read-Utf8 (Join-Path $repoRoot 'README.md')
$userGuideText = Read-Utf8 (Join-Path $repoRoot 'docs\USER_GUIDE.md')
$privacyText = Read-Utf8 (Join-Path $repoRoot 'docs\PRIVACY_SECURITY.md')
foreach ($token in @('ExtraIsland', '.NET 10', 'CreateCipx', 'Pollinations', 'auto_pass', 'manual_required', 'auto_block', 'audit-', 'review-queue.json', 'env:OPENAI_API_KEY', 'Ollama')) {
    Assert-TextContains "README covers: $token" $readmeText $token
}
foreach ($token in @('人工复核', '批准选中', 'FIFO', 'audit-', 'Ollama', '.NET 10')) {
    Assert-TextContains "user guide covers: $token" $userGuideText $token
}
foreach ($token in @('settings.json', 'env:', 'Pollinations', 'fail-closed', '高风险', 'review-queue.json')) {
    Assert-TextContains "privacy guide covers: $token" $privacyText $token
}
$sourcePathDiscrepancy = ($readmeText.IndexOf('Codes\extraisland', [StringComparison]::Ordinal) -ge 0) -and
                         ($readmeText.IndexOf('Code\ExtraIsland', [StringComparison]::Ordinal) -ge 0)
Add-Check 'README records the actual ExtraIsland source path discrepancy' $sourcePathDiscrepancy 'expected both Codes\extraisland (old) and Code\ExtraIsland (actual)'

Write-Host '[5/7] JSON defaults and hardcoded secret scan' -ForegroundColor Cyan
$excludedPattern = '\\(\.git|\.tools|bin|obj|cipx)\\|\\node_modules\\'
$jsonFiles = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $excludedPattern })
if ($jsonFiles.Count -eq 0) {
    Add-Warn 'settings-default JSON validation' 'no standalone *.json defaults found; defaults are defined by C# model initializers and verified by source checks'
}
else {
    foreach ($jsonFile in $jsonFiles) {
        $relative = $jsonFile.FullName.Substring($repoRoot.Length + 1)
        try {
            $raw = Read-Utf8 $jsonFile.FullName
            $null = $raw | ConvertFrom-Json
            Add-Pass "valid JSON: $relative"
        }
        catch {
            Add-Fail "valid JSON: $relative" $_.Exception.Message
        }
    }
}

$secretPatterns = @(
    @{ Name = 'OpenAI-style sk- key'; Pattern = '(?i)\bsk-[A-Za-z0-9_-]{20,}\b' },
    @{ Name = 'Google AIza key'; Pattern = '(?i)\bAIza[0-9A-Za-z_-]{30,}\b' },
    @{ Name = 'long Bearer value'; Pattern = '(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]{24,}\b' },
    @{ Name = 'GitHub token'; Pattern = '(?i)\bgh[pousr]_[A-Za-z0-9_]{20,}\b' },
    @{ Name = 'Slack token'; Pattern = '(?i)\bxox[baprs]-[A-Za-z0-9-]{20,}\b' }
)
$textExtensions = @('.cs', '.axaml', '.xaml', '.json', '.yml', '.yaml', '.ps1', '.md', '.csproj', '.props', '.targets', '.config')
$textFiles = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch $excludedPattern -and
        $_.Name -ne 'verify.ps1' -and
        $textExtensions -contains $_.Extension
    })
$secretFindings = New-Object System.Collections.Generic.List[string]
foreach ($secretPattern in $secretPatterns) {
    $regex = New-Object System.Text.RegularExpressions.Regex($secretPattern.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    foreach ($file in $textFiles) {
        $text = Read-Utf8 $file.FullName
        $lineNumber = 0
        foreach ($line in ($text -split "`r?`n")) {
            $lineNumber++
            if ($regex.IsMatch($line)) {
                $relative = $file.FullName.Substring($repoRoot.Length + 1)
                $secretFindings.Add("$relative`:$lineNumber [$($secretPattern.Name)]")
            }
        }
    }
}
Add-Check 'no hardcoded API keys or long Bearer values in text sources' ($secretFindings.Count -eq 0) (($secretFindings | Select-Object -First 10) -join '; ')
Write-Host '[6/7] Release build and CIPX packaging' -ForegroundColor Cyan
$buildScript = Join-Path $repoRoot 'scripts\build.ps1'
Add-Check 'build helper exists' (Test-Path -LiteralPath $buildScript -PathType Leaf) "missing: $buildScript"

$buildSucceeded = $false
if ($SkipBuild) {
    Add-Warn 'build execution' 'skipped by -SkipBuild; artifact checks use the existing output tree'
}
elseif (Test-Path -LiteralPath $buildScript -PathType Leaf) {
    $buildExitCode = -1
    try {
        $powerShellHost = (Get-Process -Id $PID).Path
        $buildArguments = @('-NoLogo', '-NoProfile')
        if ($PSVersionTable.PSEdition -ne 'Core') {
            $buildArguments += @('-ExecutionPolicy', 'Bypass')
        }
        $buildArguments += @('-File', $buildScript, '-Configuration', $Configuration, '-CreateCipx')
        Push-Location $repoRoot
        try {
            & $powerShellHost @buildArguments
            $buildExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
    }
    catch {
        Add-Fail 'build helper completed' $_.Exception.Message
    }
    $buildSucceeded = ($buildExitCode -eq 0)
    Add-Check "build succeeded ($Configuration + CIPX)" $buildSucceeded "build exit code: $buildExitCode"
}

$binDir = Join-Path $repoRoot ("bin\{0}\net10.0-windows" -f $Configuration)
$assemblyPath = Join-Path $binDir 'Motivation.dll'
$builtManifestPath = Join-Path $binDir 'manifest.yml'
$builtIconPath = Join-Path $binDir 'icon.png'
$cipxPath = Join-Path $repoRoot 'cipx\Motivation.cipx'

$canInspectBuild = $buildSucceeded -or $SkipBuild
if ($canInspectBuild) {
    foreach ($artifact in @($assemblyPath, $builtManifestPath, $builtIconPath)) {
        $relative = $artifact.Substring($repoRoot.Length + 1)
        if (Test-Path -LiteralPath $artifact -PathType Leaf) {
            $size = (Get-Item -LiteralPath $artifact).Length
            Add-Check "built artifact exists: $relative" ($size -gt 0) "size=$size"
        }
        else {
            Add-Fail "built artifact exists: $relative" 'missing'
        }
    }

    if (Test-Path -LiteralPath $builtManifestPath -PathType Leaf) {
        $builtEntrance = Get-ManifestField $builtManifestPath 'entranceAssembly'
        Add-Check 'built manifest entranceAssembly matches Motivation.dll' ($builtEntrance -eq 'Motivation.dll') "entranceAssembly='$builtEntrance'"
    }

    if (Test-Path -LiteralPath $cipxPath -PathType Leaf) {
        $cipxSize = (Get-Item -LiteralPath $cipxPath).Length
        Add-Check 'CIPX package exists and is non-empty' ($cipxSize -gt 0) "size=$cipxSize"
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
            $archive = [System.IO.Compression.ZipFile]::OpenRead($cipxPath)
            try {
                $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
                foreach ($requiredEntry in @('Motivation.dll', 'manifest.yml', 'icon.png')) {
                    Add-Check "CIPX contains $requiredEntry" ($entryNames -contains $requiredEntry) ("entries: " + ($entryNames -join ', '))
                }
                $entry = $archive.GetEntry('manifest.yml')
                if ($null -ne $entry) {
                    $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
                    try { $packagedManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    Add-Check 'packaged manifest has Motivation.dll entrance' ($packagedManifest -match '(?m)^\s*entranceAssembly\s*:\s*Motivation\.dll\s*$') 'packaged manifest entranceAssembly mismatch'
                }
            }
            finally {
                $archive.Dispose()
            }
        }
        catch {
            Add-Fail 'CIPX is a readable ZIP archive' $_.Exception.Message
        }
    }
    else {
        if ($buildSucceeded) {
            Add-Fail 'CIPX package exists' "expected $cipxPath after build with -CreateCipx"
        }
        else {
            Add-Warn 'CIPX package check' 'package is absent and current build did not succeed'
        }
    }
}
else {
    Add-Warn 'built artifact inspection' 'skipped because the build did not succeed; stale artifacts are not treated as verification evidence'
}

Write-Host '[7/7] Optional live connectivity and summary' -ForegroundColor Cyan
if ($TestPublicAI) {
    $publicScript = Join-Path $repoRoot 'scripts\test-public-ai.ps1'
    if (-not (Test-Path -LiteralPath $publicScript -PathType Leaf)) {
        Add-Fail 'public Pollinations test script exists' "missing: $publicScript"
    }
    else {
        $publicExitCode = -1
        try {
            $powerShellHost = (Get-Process -Id $PID).Path
            $publicArguments = @('-NoLogo', '-NoProfile')
            if ($PSVersionTable.PSEdition -ne 'Core') {
                $publicArguments += @('-ExecutionPolicy', 'Bypass')
            }
            $publicArguments += @('-File', $publicScript)
            & $powerShellHost @publicArguments
            $publicExitCode = $LASTEXITCODE
        }
        catch {
            Add-Fail 'public Pollinations connectivity test' $_.Exception.Message
        }
        Add-Check 'public Pollinations connectivity test' ($publicExitCode -eq 0) "test exit code: $publicExitCode"
    }
}
else {
    Add-Warn 'public Pollinations connectivity test' 'not run; pass -TestPublicAI to enable the optional anonymous live test'
}

Write-Host ''
Write-Host ('Verification summary: {0} passed, {1} failed, {2} warnings' -f $script:Passed, $script:Failed, $script:Warned) -ForegroundColor Cyan
if ($script:Failed -gt 0) {
    Write-Host 'FAILURES:' -ForegroundColor Red
    foreach ($failure in $script:Failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    Write-Host 'VERIFY FAILED' -ForegroundColor Red
    exit 1
}

Write-Host 'VERIFY OK' -ForegroundColor Green
exit 0