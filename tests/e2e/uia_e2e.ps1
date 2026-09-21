# 端到端 UIA 测试：新建任务全流程（须以管理员运行）
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$root = [System.Windows.Automation.AutomationElement]::RootElement
function FindWin([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
}
function FindChild([System.Windows.Automation.AutomationElement]$p, [string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $p.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function InvokeBtn([System.Windows.Automation.AutomationElement]$p, [string]$name) {
    $b = FindChild $p $name
    if (-not $b) { Write-Output "FAIL button-not-found: $name"; return $false }
    ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    return $true
}

# 0) 找主窗口（已由外部提权启动）
$main = FindWin 'FolderSync — 本地文件夹同步'
if (-not $main) { Write-Output 'FAIL main window not found'; exit 1 }

# 1) 点新建任务
(InvokeBtn $main '新建任务') | Out-Null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$dlg = $null
while ($sw.ElapsedMilliseconds -lt 15000) {
    Start-Sleep -Milliseconds 200
    $dlg = FindWin '任务设置'
    if ($dlg) { break }
}
if (-not $dlg) { Write-Output 'FAIL dialog not open'; exit 1 }
Write-Output ("PASS dialog-open ({0:F1}s)" -f $sw.Elapsed.TotalSeconds)

# 2) 测浏览速度
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
(InvokeBtn $dlg '浏览…') | Out-Null
$browseDlg = $null
while ($sw2.ElapsedMilliseconds -lt 30000) {
    Start-Sleep -Milliseconds 150
    $ws = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $ws) {
        $n = $w.Current.Name
        if (($n -match '浏览|选择|Select') -and $n -ne '任务设置' -and $n -notmatch 'FolderSync') { $browseDlg = $w; break }
    }
    if ($browseDlg) { break }
}
if ($browseDlg) {
    Write-Output ("PASS browse-dialog ({0:F1}s)" -f $sw2.Elapsed.TotalSeconds)
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 800
} else {
    Write-Output ("FAIL browse-dialog timeout ({0:F1}s)" -f $sw2.Elapsed.TotalSeconds)
}

# 3) 填表：Name/Left/Right/Exclude 顺序的 Edit 控件
$edits = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit))
Write-Output ("edits found: {0}" -f $edits.Count)
$names = @('E2E自动化测试任务', 'C:\Users\Yang\AppData\Local\Temp', 'C:\Users\Yang\AppData\Local\Temp\fstest_e2e_target', '')
for ($i = 0; $i -lt [Math]::Min($edits.Count, $names.Count); $i++) {
    ($edits[$i].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($names[$i])
}

# 4) 确定 → 回主窗口
(InvokeBtn $dlg '确定') | Out-Null
Start-Sleep -Seconds 2

# 5) 验证主窗口任务列表含新任务
$items = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem))
$found = $false
foreach ($it in $items) {
    if ($it.Current.Name -match 'E2E自动化测试任务') { $found = $true; break }
}
if ($found) { Write-Output ("PASS job-in-list (listItems={0})" -f $items.Count) }
else { Write-Output ("FAIL job-in-list (listItems={0})" -f $items.Count) }

# 6) 清理：选中并删除
if ($found) {
    foreach ($it in $items) {
        if ($it.Current.Name -match 'E2E自动化测试任务') {
            ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            break
        }
    }
    Start-Sleep -Milliseconds 500
    (InvokeBtn $main '删除') | Out-Null
    Start-Sleep -Seconds 1
    $mb = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $mb) {
        if ($w.Current.Name -match '确认') {
            $yes = FindChild $w '是'
            if ($yes) { ($yes.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
        }
    }
    Start-Sleep -Seconds 1
    # 复验已删除
    $items2 = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem))
    $still = $false
    foreach ($it in $items2) { if ($it.Current.Name -match 'E2E自动化测试任务') { $still = $true; break } }
    if ($still) { Write-Output 'FAIL delete' } else { Write-Output 'PASS delete' }
}
Write-Output 'E2E-DONE'
