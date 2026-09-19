# Claude Code statusLine bridge. Only quota fields are persisted; no prompt/session data.
param([string]$OutputPath = (Join-Path $env:LOCALAPPDATA 'DeskMonitor\claude-subscription.json'))
$ErrorActionPreference = 'Stop'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $windows = @()
    foreach ($entry in @(@('five_hour', '5 小时'), @('seven_day', '每周'))) {
        $window = $payload.rate_limits.($entry[0])
        if ($null -eq $window -or $null -eq $window.used_percentage) { continue }
        $used = [double]$window.used_percentage
        if ([double]::IsNaN($used) -or [double]::IsInfinity($used) -or $used -lt 0 -or $used -gt 100) { continue }
        $reset = if ($null -ne $window.resets_at) { [DateTimeOffset]::FromUnixTimeSeconds([long]$window.resets_at).ToString('O') } else { $null }
        $windows += @{ label = $entry[1]; usedPercent = $used; resetsAt = $reset }
    }
    if ($windows.Count -gt 0) {
        $target = [IO.Path]::GetFullPath($OutputPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $temporary = $target + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        $json = @{ provider = 'Claude'; observedAt = [DateTimeOffset]::UtcNow.ToString('O'); windows = $windows } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $target -Force
        Write-Output (($windows | ForEach-Object { "$($_.label) $($_.usedPercent)% 已用" }) -join ' | ')
    } else { Write-Output 'Claude · 暂无订阅额度' }
} catch { Write-Output 'Claude · 额度快照暂不可用' }
