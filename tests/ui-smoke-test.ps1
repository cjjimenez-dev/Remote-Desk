param(
    [string]$ViewerExe = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\RemoteDesk.Viewer\bin\Release\net10.0-windows10.0.19041.0\RemoteDesk.Viewer.exe")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ViewerExe)) {
    throw "Viewer executable not found: $ViewerExe"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutMs = 5000
    )
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $el = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $el) {
            return $el
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Wait-Until {
    param(
        [scriptblock]$Predicate,
        [int]$TimeoutMs = 8000,
        [string]$FailureMessage = "Condition timed out"
    )
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if (& $Predicate) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw $FailureMessage
}

function Invoke-Button {
    param([System.Windows.Automation.AutomationElement]$Button)
    $pattern = $Button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Set-TextValue {
    param(
        [System.Windows.Automation.AutomationElement]$TextBox,
        [string]$Value
    )
    $pattern = $TextBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Send-SignalRFrame {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Ws,
        [string]$Json
    )
    $payload = $Json + [char]0x1e
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $seg = New-Object System.ArraySegment[byte]($bytes, 0, $bytes.Length)
    $Ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Receive-SignalRText {
    param(
        [System.Net.WebSockets.ClientWebSocket]$Ws,
        [int]$TimeoutMs = 8000
    )
    $cts = New-Object System.Threading.CancellationTokenSource($TimeoutMs)
    $buf = New-Object byte[] 16384
    $seg = New-Object System.ArraySegment[byte]($buf, 0, $buf.Length)
    $sb = New-Object System.Text.StringBuilder
    while ($true) {
        $res = $Ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
        if ($res.Count -gt 0) {
            $chunk = [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
            [void]$sb.Append($chunk)
        }
        if ($res.EndOfMessage -or $res.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            break
        }
    }
    return $sb.ToString()
}

Write-Host "[1/5] Starting RemoteDesk.Viewer from $ViewerExe..."
$proc = Start-Process -FilePath $ViewerExe -PassThru
$ws = $null

try {
    $root = $null
    $winCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "RemoteDesk"))
    )
    Wait-Until -TimeoutMs 20000 -FailureMessage "RemoteDesk main WPF window did not appear." -Predicate {
        $script:foundWin = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $winCond)
        return ($null -ne $script:foundWin)
    }
    $root = $script:foundWin
    $partnerBox = Find-ByAutomationId -Root $root -AutomationId "PartnerIdTextBox" -TimeoutMs 10000
    $connectBtn = Find-ByAutomationId -Root $root -AutomationId "ConnectRemoteButton" -TimeoutMs 10000
    if ($null -eq $partnerBox -or $null -eq $connectBtn) {
        throw "Could not locate PartnerIdTextBox or ConnectRemoteButton in WPF visual tree."
    }

    # Wait briefly for built-in Signaling server (:5000) to come up
    Wait-Until -TimeoutMs 15000 -FailureMessage "Integrated signaling server (:5000) did not respond." -Predicate {
        try {
            $resp = Invoke-RestMethod -Uri "http://127.0.0.1:5000/" -TimeoutSec 2
            return $null -ne $resp
        } catch {
            return $false
        }
    }
    Write-Host "[2/5] Integrated signaling server is online and window is responsive."

    # Test Case 1: Offline partner shows Connecting -> Partner Not Reachable modal without hanging
    Set-TextValue -TextBox $partnerBox -Value "999-999"
    Invoke-Button -Button $connectBtn

    Wait-Until -TimeoutMs 8000 -FailureMessage "Offline test modal did not transition to Partner Not Reachable." -Predicate {
        $titleEl = Find-ByAutomationId -Root $root -AutomationId "ConnectionModalTitleText" -TimeoutMs 200
        return ($null -ne $titleEl -and $titleEl.Current.Name -eq "Partner Not Reachable")
    }
    $proc.Refresh()
    if (-not $proc.Responding) { throw "UI stopped responding on offline error modal!" }

    $closeModalBtn = Find-ByAutomationId -Root $root -AutomationId "CloseConnectionModalButton" -TimeoutMs 2000
    if ($null -eq $closeModalBtn) { throw "CloseConnectionModalButton not found." }
    Invoke-Button -Button $closeModalBtn

    Wait-Until -TimeoutMs 4000 -FailureMessage "Modal did not close after clicking Close." -Predicate {
        $titleEl = Find-ByAutomationId -Root $root -AutomationId "ConnectionModalTitleText" -TimeoutMs 150
        return ($null -eq $titleEl -or $titleEl.Current.IsOffscreen)
    }
    Write-Host "[3/5] Test 1 passed: Offline partner modal displayed and closed cleanly."

    # Test Case 2: Live dummy SignalR partner ("888-888") -> Connecting modal -> Partner Reject -> Connection Declined modal (Zero hang)
    $negResp = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5000/signaling/negotiate?negotiateVersion=1"
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $wsUri = [Uri]("ws://127.0.0.1:5000/signaling?id=$($negResp.connectionToken)")
    $ws.ConnectAsync($wsUri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Send-SignalRFrame -Ws $ws -Json '{"protocol":"json","version":1}'
    [void](Receive-SignalRText -Ws $ws)
    Send-SignalRFrame -Ws $ws -Json '{"type":1,"invocationId":"1","target":"RegisterHost","arguments":["888-888","SmokeHost",null]}'
    [void](Receive-SignalRText -Ws $ws)

    Set-TextValue -TextBox $partnerBox -Value "888-888"
    Invoke-Button -Button $connectBtn

    Wait-Until -TimeoutMs 5000 -FailureMessage "Connecting modal title did not appear." -Predicate {
        $titleEl = Find-ByAutomationId -Root $root -AutomationId "ConnectionModalTitleText" -TimeoutMs 150
        return ($null -ne $titleEl -and $titleEl.Current.Name -eq "Connecting to 888-888")
    }
    $cancelBtn = Find-ByAutomationId -Root $root -AutomationId "CancelConnectionButton" -TimeoutMs 2000
    if ($null -eq $cancelBtn) { throw "CancelConnectionButton not visible while connecting!" }

    # Wait for ConnectionRequested on dummy host WebSocket
    $viewerConnId = $null
    $deadline = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $deadline -and [string]::IsNullOrEmpty($viewerConnId)) {
        $raw = Receive-SignalRText -Ws $ws -TimeoutMs 4000
        foreach ($part in ($raw -split [char]0x1e)) {
            if ([string]::IsNullOrWhiteSpace($part)) { continue }
            $msg = $part | ConvertFrom-Json
            if ($msg.target -eq "ConnectionRequested") {
                $viewerConnId = $msg.arguments[1]
                break
            }
        }
    }
    if ([string]::IsNullOrEmpty($viewerConnId)) { throw "Dummy host never received ConnectionRequested." }

    Start-Sleep -Milliseconds 400
    $rejectPayload = @{
        type = 1
        target = "RejectConnection"
        arguments = @($viewerConnId, "Declined by SmokeHost")
    } | ConvertTo-Json -Compress
    Send-SignalRFrame -Ws $ws -Json $rejectPayload

    Wait-Until -TimeoutMs 6000 -FailureMessage "Modal did not transition to Connection Declined after host reject." -Predicate {
        $titleEl = Find-ByAutomationId -Root $root -AutomationId "ConnectionModalTitleText" -TimeoutMs 150
        return ($null -ne $titleEl -and $titleEl.Current.Name -eq "Connection Declined")
    }

    $errTextEl = Find-ByAutomationId -Root $root -AutomationId "ConnectionModalStageErrorText" -TimeoutMs 2000
    if ($null -eq $errTextEl -or -not $errTextEl.Current.Name.Contains("Declined by SmokeHost")) {
        throw "Declined reason text missing: $($errTextEl.Current.Name)"
    }

    # Verify UI remains completely responsive after decline (no deadlock)
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if (-not $proc.Responding) { throw "UI hung (Not Responding) after partner declined!" }

    $closeAfterReject = Find-ByAutomationId -Root $root -AutomationId "CloseConnectionModalButton" -TimeoutMs 2000
    Invoke-Button -Button $closeAfterReject
    Write-Host "[4/5] Test 2 passed: Live partner decline showed modal and UI stayed responsive!"

    # Test Case 3: Window closes cleanly with zero zombie process
    $winPattern = $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $winPattern.Close()
    if (-not $proc.WaitForExit(6000)) {
        throw "RemoteDesk.Viewer.exe did not exit cleanly after closing main window!"
    }
    Write-Host "[5/5] Test 3 passed: RemoteDesk.Viewer.exe exited cleanly (exit code $($proc.ExitCode))."
    Write-Host "ALL_UI_SMOKE_TESTS_PASSED"
}
finally {
    if ($null -ne $ws) {
        try { $ws.Dispose() } catch { }
    }
    if ($null -ne $proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force } catch { }
    }
}
