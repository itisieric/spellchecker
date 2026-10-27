[CmdletBinding()]
param(
    [string]$OllamaExe = 'G:\Ollama\App\ollama.exe',
    [string]$Model = 'qwen3.5-spellchecker:4b',
    [int]$Repeats = 2,
    [ValidateSet('Memory', 'Generation')]
    [string]$Experiment = 'Memory',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = 'artifacts\ollama-' + $Experiment.ToLowerInvariant() + '-' + (Get-Date -Format yyyyMMdd-HHmmss)
}
$taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
if (-not $taskOutput.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Benchmark output must stay inside the workspace.'
}
if (Test-Path -LiteralPath $taskOutput) { throw 'Benchmark output already exists; choose a fresh directory to preserve earlier results.' }
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskAppExe = Join-Path $taskRoot 'artifacts\win-x64\LLMAutocorrect.exe'
$taskProduction = 'http://127.0.0.1:11434'
$taskTemporary = 'http://127.0.0.1:11435'
$taskRunnerExe = Join-Path (Split-Path -Parent $OllamaExe) 'lib\ollama\llama-server.exe'
$taskServer = $null
$taskResumeApp = $false
$taskProductionReleased = $false
$taskOriginalModel = $null
$taskOriginalContext = 4096
$taskRows = [Collections.Generic.List[object]]::new()
$taskProfiles = @(
    @{ Name = 'baseline-auto-f16-4096-b512'; Context = 4096; Batch = 512; Flash = $null; Cache = $null },
    @{ Name = 'auto-f16-2048-b512'; Context = 2048; Batch = 512; Flash = $null; Cache = $null },
    @{ Name = 'auto-f16-4096-b128'; Context = 4096; Batch = 128; Flash = $null; Cache = $null },
    @{ Name = 'auto-f16-4096-b1024'; Context = 4096; Batch = 1024; Flash = $null; Cache = $null },
    @{ Name = 'flash-on-f16-4096-b512'; Context = 4096; Batch = 512; Flash = '1'; Cache = 'f16' },
    @{ Name = 'flash-on-q8-4096-b512'; Context = 4096; Batch = 512; Flash = '1'; Cache = 'q8_0' },
    @{ Name = 'baseline-recheck'; Context = 4096; Batch = 512; Flash = $null; Cache = $null }
)
if ($Experiment -eq 'Generation') {
    $taskProfiles = @(
        @{ Name = 'generation-paired'; Context = 4096; Batch = 512; Spec = 'none'; PairReasons = $true },
        @{ Name = 'ngram-simple'; Context = 4096; Batch = 512; Spec = 'ngram-simple' },
        @{ Name = 'ngram-map-k4v'; Context = 4096; Batch = 512; Spec = 'ngram-map-k4v' },
        @{ Name = 'generation-recheck'; Context = 4096; Batch = 512; Spec = 'none' }
    )
}

# Read only the repository's fixed instructions, never personal memory,
# dictionary, credentials, live editor contents, or production log text.
$taskSource = Get-Content -LiteralPath (Join-Path $taskRoot 'src\LLMAutocorrect\Providers\GeminiCorrectionProvider.cs') -Raw
$taskRawInstruction = [regex]::Match($taskSource, '(?s)private const string Instruction = """\r?\n(.*?)\r?\n        """;')
if (-not $taskRawInstruction.Success) { throw 'Could not read the correction engine instruction.' }
$taskInstruction = [regex]::Replace($taskRawInstruction.Groups[1].Value, '(?m)^        ', '').Replace("`r`n", "`n")
$taskNoExplanationInstruction = 'When uncertain is false, reason must be an empty string, including successful corrections and confident no-change results. Only provide a short reason when uncertain is true; never quote target_text.'
$taskSchema = @{
    type = 'object'
    properties = [ordered]@{
        should_replace = @{ type = 'boolean' }; replacement = @{ type = 'string' }
        change_type = @{ type = 'string'; enum = @('none','spelling','grammar','spelling_grammar','clarity') }
        uncertain = @{ type = 'boolean' }; reason = @{ type = 'string' }
    }
    required = @('should_replace','replacement','change_type','uncertain','reason')
}
$taskCases = @(
    @{ Id = 'short'; Text = 'this needs corection'; Expected = 'correction'; Forbidden = 'corection'; Tokens = @() },
    @{ Id = 'sentence'; Text = 'This is a test to see how hfast it is'; Expected = 'fast'; Forbidden = 'hfast'; Tokens = @() },
    @{ Id = 'contraction'; Text = 'havent typped'; Expected = 'typed'; Forbidden = 'typped'; Tokens = @() },
    @{ Id = 'protected'; Text = 'the Modbus adress is 192.168.1.20 on port 502'; Expected = 'address'; Forbidden = 'adress'; Tokens = @('Modbus','192.168.1.20','502') },
    @{ Id = 'already-correct'; Text = 'This is a test.'; Expected = ''; Forbidden = ''; Tokens = @(); NoChange = $true },
    @{ Id = 'long-context'; Text = 'Please chekc the documment before we send it to the project team.'; Expected = 'document'; Forbidden = 'documment'; Tokens = @(); Context = ('The project team is preparing a short report. The notes describe progress, remaining work, and the next review. ' * 5) }
)
if ($Experiment -eq 'Generation') {
    $taskCases += @(
        @{ Id = 'multiple-typos'; Text = 'I recieved the mesage and will reply tommorow.'; Expected = 'received'; Forbidden = 'recieved|mesage|tommorow'; Tokens = @() },
        @{ Id = 'self-edit-word'; Text = 'lets try the back space'; Expected = 'backspace'; Forbidden = ''; Tokens = @() },
        @{ Id = 'sentence-after-enter'; Text = 'i just hit enter'; Expected = 'enter'; Forbidden = ''; Tokens = @() },
        @{ Id = 'address-bar'; Text = 'this is in the surch bar in my brav browser'; Expected = 'search'; Forbidden = 'surch|brav\b'; Tokens = @(); AddressBar = $true },
        @{ Id = 'file-path'; Text = 'the flie is saved at C:\Temp\notes.txt'; Expected = 'file'; Forbidden = '\bflie\b'; Tokens = @('C:\Temp\notes.txt') },
        @{ Id = 'web-address'; Text = 'pleese open https://example.com/project?id=17'; Expected = 'please'; Forbidden = 'pleese'; Tokens = @('https://example.com/project?id=17') },
        @{ Id = 'email'; Text = 'pleese send the mesage to notes@example.com'; Expected = 'please'; Forbidden = 'pleese|mesage'; Tokens = @('notes@example.com') },
        @{ Id = 'identifier'; Text = 'the setPoint varible is used by WindScreen'; Expected = 'variable'; Forbidden = 'varible'; Tokens = @('setPoint','WindScreen') },
        @{ Id = 'numbers'; Text = 'the motor speeed is 1750 rpm and the curent is 4.5 A'; Expected = 'speed'; Forbidden = 'speeed|curent'; Tokens = @('1750','4.5') },
        @{ Id = 'clean-technical'; Text = 'The Modbus device uses register 40001.'; Expected = ''; Forbidden = ''; Tokens = @('Modbus','40001'); NoChange = $true },
        @{ Id = 'ambiguous'; Text = 'flarn glorp zibble'; Expected = ''; Forbidden = ''; Tokens = @(); NoChange = $true },
        @{ Id = 'long-copy'; Text = 'The project team reviewed the installation notes and confirmed that the control panel, wiring diagram, motor connections, and operating instructions were ready for the next stage. Please chekc the final documment before sending the report to everyone on the team.'; Expected = 'document'; Forbidden = 'chekc|documment'; Tokens = @() }
    )
}

function Invoke-BenchmarkRequest($Profile, $Case, [string]$Variant = 'baseline') {
    $taskPayload = [ordered]@{
        context_before = $(if ($Case.Context) { $Case.Context } else { '' })
        target_text = $Case.Text; technical_terms = @(); protected_tokens = @($Case.Tokens)
        mode = 'normal'; custom_instructions = ''; preserve_capitalization_and_punctuation = [bool]$Case.AddressBar
        learned_spelling = @()
    }
    $taskBody = @{
        model = $Model; stream = $false; think = $false; format = $taskSchema; keep_alive = -1
        messages = @(@{ role = 'system'; content = $(if ($Variant -eq 'no-explanation') { $taskInstruction + "`n" + $taskNoExplanationInstruction } else { $taskInstruction }) },
            @{ role = 'user'; content = ($taskPayload | ConvertTo-Json -Depth 10 -Compress) })
        options = @{ temperature = 0; seed = 42; num_ctx = $Profile.Context; num_batch = $Profile.Batch; num_predict = 512 }
    } | ConvertTo-Json -Depth 15 -Compress
    $taskTimer = [Diagnostics.Stopwatch]::StartNew()
    $taskResponse = Invoke-RestMethod -Uri ($taskTemporary + '/api/chat') -Method Post -ContentType 'application/json' -Body $taskBody -TimeoutSec 60
    $taskTimer.Stop()
    $taskParsed = $taskResponse.message.content | ConvertFrom-Json
    $taskValid = $null -ne $taskParsed.should_replace -and $null -ne $taskParsed.replacement -and
        $null -ne $taskParsed.change_type -and $null -ne $taskParsed.uncertain -and $null -ne $taskParsed.reason
    $taskQuality = $taskValid
    if ($Case.NoChange) { $taskQuality = $taskQuality -and -not $taskParsed.should_replace }
    else {
        $taskQuality = $taskQuality -and $taskParsed.should_replace -and
            $taskParsed.replacement.Contains($Case.Expected, [StringComparison]::OrdinalIgnoreCase) -and
            ([string]::IsNullOrEmpty($Case.Forbidden) -or $taskParsed.replacement -notmatch $Case.Forbidden)
        foreach ($taskToken in $Case.Tokens) { $taskQuality = $taskQuality -and $taskParsed.replacement.Contains($taskToken) }
    }
    [pscustomobject]@{
        Profile = $Profile.Name; Variant = $Variant; Case = $Case.Id; Context = $Profile.Context; Batch = $Profile.Batch
        TestInput = $taskPayload; ExpectedNoChange = [bool]$Case.NoChange
        WallMs = [math]::Round($taskTimer.Elapsed.TotalMilliseconds, 1)
        TotalMs = [math]::Round($taskResponse.total_duration / 1e6, 1)
        LoadMs = [math]::Round($taskResponse.load_duration / 1e6, 1)
        PromptMs = [math]::Round($taskResponse.prompt_eval_duration / 1e6, 1)
        DecodeMs = [math]::Round($taskResponse.eval_duration / 1e6, 1)
        PromptTokens = $taskResponse.prompt_eval_count; OutputTokens = $taskResponse.eval_count
        TokensPerSecond = $(if ($taskResponse.eval_duration -gt 0) { [math]::Round($taskResponse.eval_count * 1e9 / $taskResponse.eval_duration, 1) } else { 0 })
        DoneReason = $taskResponse.done_reason; ValidJson = $taskValid; QualityPass = $taskQuality
        # Only generated results for the synthetic test cases are saved.
        Result = $taskParsed
    }
}

function Stop-BenchmarkServer {
    if ($script:taskServer) {
        # Ollama does not always reap a runner after forced termination. Target
        # only direct children of the exact temporary server we launched.
        $taskChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($script:taskServer.Id)")
        foreach ($taskChild in $taskChildren) {
            if ($taskChild.ExecutablePath -eq $taskRunnerExe) {
                Stop-Process -Id $taskChild.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }
        if (-not $script:taskServer.HasExited) {
            $script:taskServer.Kill()
            $script:taskServer.WaitForExit(5000) | Out-Null
        }
    }
    $script:taskServer = $null
}

try {
    if (Get-NetTCPConnection -LocalPort 11435 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Temporary benchmark port is already in use.'
    }
    $taskApps = @(Get-Process -Name LLMAutocorrect -ErrorAction SilentlyContinue)
    foreach ($taskApp in $taskApps) { if ($taskApp.Path -ne $taskAppExe) { throw 'Unexpected Spellchecker executable; benchmark stopped.' } }
    foreach ($taskApp in $taskApps) {
        Stop-Process -Id $taskApp.Id -Force
        $taskResumeApp = $true
        if (-not $taskApp.WaitForExit(5000)) { throw 'Spellchecker did not exit.' }
    }
    $taskLoaded = Invoke-RestMethod -Uri ($taskProduction + '/api/ps') -TimeoutSec 5
    if (@($taskLoaded.models | Where-Object name -ne $Model).Count -gt 0) {
        throw 'Another model is active; benchmark stopped to avoid disrupting unrelated work.'
    }
    $taskOriginalModel = @($taskLoaded.models | Where-Object name -eq $Model) | Select-Object -First 1
    if ($taskOriginalModel) { $taskOriginalContext = $taskOriginalModel.context_length }
    Invoke-RestMethod -Uri ($taskProduction + '/api/generate') -Method Post -ContentType 'application/json' -Body (@{ model = $Model; keep_alive = 0 } | ConvertTo-Json) -TimeoutSec 15 | Out-Null
    $taskProductionReleased = $true

    foreach ($taskProfile in $taskProfiles) {
        Stop-BenchmarkServer
        $taskSavedEnvironment = @{}
        foreach ($taskName in @('OLLAMA_HOST','OLLAMA_MODELS','OLLAMA_FLASH_ATTENTION','OLLAMA_KV_CACHE_TYPE','OLLAMA_NUM_PARALLEL','LLAMA_ARG_SPEC_TYPE')) {
            $taskSavedEnvironment[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
        }
        try {
            [Environment]::SetEnvironmentVariable('OLLAMA_HOST', '127.0.0.1:11435', 'Process')
            [Environment]::SetEnvironmentVariable('OLLAMA_MODELS', 'G:\Ollama\Models', 'Process')
            [Environment]::SetEnvironmentVariable('OLLAMA_FLASH_ATTENTION', $taskProfile.Flash, 'Process')
            [Environment]::SetEnvironmentVariable('OLLAMA_KV_CACHE_TYPE', $taskProfile.Cache, 'Process')
            [Environment]::SetEnvironmentVariable('OLLAMA_NUM_PARALLEL', '1', 'Process')
            [Environment]::SetEnvironmentVariable('LLAMA_ARG_SPEC_TYPE', $taskProfile.Spec, 'Process')
            $taskServer = Start-Process -FilePath $OllamaExe -ArgumentList 'serve' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutput ($taskProfile.Name + '.stdout.log')) -RedirectStandardError (Join-Path $taskOutput ($taskProfile.Name + '.stderr.log'))
        }
        finally {
            foreach ($taskName in $taskSavedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($taskName, $taskSavedEnvironment[$taskName], 'Process') }
        }
        $taskReady = $false
        for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
            try { Invoke-RestMethod -Uri ($taskTemporary + '/api/version') -TimeoutSec 1 | Out-Null; $taskReady = $true; break }
            catch { Start-Sleep -Milliseconds 250 }
        }
        if (-not $taskReady) { throw 'Temporary Ollama server did not become ready.' }
        Write-Output "PROFILE_START $($taskProfile.Name)"
        try {
            $taskWarmup = Invoke-BenchmarkRequest $taskProfile $taskCases[0]
            Write-Output "WARMUP load=$($taskWarmup.LoadMs)ms wall=$($taskWarmup.WallMs)ms"
            for ($taskRepeat = 1; $taskRepeat -le $Repeats; $taskRepeat++) {
                # Rotate case order so the longest prompt does not always occur last.
                for ($taskIndex = 0; $taskIndex -lt $taskCases.Count; $taskIndex++) {
                    $taskCase = $taskCases[($taskIndex + $taskRepeat - 1) % $taskCases.Count]
                    $taskVariants = if ($taskProfile.PairReasons) {
                        if ($taskRepeat % 2 -eq 1) { @('baseline','no-explanation') } else { @('no-explanation','baseline') }
                    } else { @('baseline') }
                    foreach ($taskVariant in $taskVariants) {
                        $taskRow = Invoke-BenchmarkRequest $taskProfile $taskCase $taskVariant
                        $taskRow | Add-Member -NotePropertyName Repeat -NotePropertyValue $taskRepeat
                        $taskRows.Add($taskRow)
                        Write-Output "SAMPLE $($taskProfile.Name) $taskVariant $($taskCase.Id) wall=$($taskRow.WallMs)ms prompt=$($taskRow.PromptMs)ms decode=$($taskRow.DecodeMs)ms tokens/s=$($taskRow.TokensPerSecond) quality=$($taskRow.QualityPass)"
                    }
                }
            }
            $taskPs = Invoke-RestMethod -Uri ($taskTemporary + '/api/ps') -TimeoutSec 5
            $taskRuntime = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($taskServer.Id)" | Select-Object Name,CommandLine)
            $taskGpu = & nvidia-smi --query-gpu=name,memory.total,memory.used,memory.free,temperature.gpu,utilization.gpu --format=csv,noheader
            @{ Profile = $taskProfile; LoadedModels = $taskPs; Runtime = $taskRuntime; Gpu = $taskGpu; Warmup = $taskWarmup } |
                ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput ($taskProfile.Name + '.json')) -Encoding utf8
        }
        catch { Write-Warning "Profile $($taskProfile.Name) failed: $($_.Exception.GetType().Name): $($_.Exception.Message)" }
        $taskRows | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskOutput 'samples.json') -Encoding utf8
    }
}
finally {
    Stop-BenchmarkServer
    try {
        if ($taskProductionReleased -and $taskOriginalModel) {
            $taskRemainingSeconds = ([DateTimeOffset]$taskOriginalModel.expires_at - [DateTimeOffset]::Now).TotalSeconds
            $taskRestoreKeepAlive = if ($taskRemainingSeconds -gt 31536000) { -1 } else { [math]::Max(0, [int][math]::Ceiling($taskRemainingSeconds)) }
            Invoke-RestMethod -Uri ($taskProduction + '/api/generate') -Method Post -ContentType 'application/json' -Body (@{ model = $Model; keep_alive = $taskRestoreKeepAlive; options = @{ num_ctx = $taskOriginalContext; num_batch = 512 } } | ConvertTo-Json -Depth 4) -TimeoutSec 60 | Out-Null
            Write-Output "Production model restored: context=$taskOriginalContext batch=512 keep_alive=$taskRestoreKeepAlive."
        }
    }
    finally {
        if ($taskResumeApp) { Start-Process -FilePath $taskAppExe -WindowStyle Hidden; Write-Output 'Spellchecker restarted.' }
    }
}

$taskRows | Group-Object Profile,Variant | ForEach-Object {
    $taskOrdered = @($_.Group.WallMs | Sort-Object)
    [pscustomobject]@{
        Profile = $_.Name; Samples = $_.Count
        MedianMs = [math]::Round(($taskOrdered[[int][math]::Floor(($taskOrdered.Count - 1) / 2)] + $taskOrdered[[int][math]::Ceiling(($taskOrdered.Count - 1) / 2)]) / 2, 1)
        AverageMs = [math]::Round(($_.Group.WallMs | Measure-Object -Average).Average, 1)
        AverageTokensPerSecond = [math]::Round(($_.Group.TokensPerSecond | Measure-Object -Average).Average, 1)
        QualityPasses = @($_.Group | Where-Object QualityPass).Count
    }
} | Format-Table -AutoSize
