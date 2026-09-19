Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
'@ -Name U -Namespace W

$root = [System.Windows.Automation.AutomationElement]::RootElement
$BT = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$MI = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$WN = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$appPid = (Get-Process TokenMonitor.App).Id
$PRICING = [char]0x8BA1 + [char]0x4EF7 + [char]0x914D + [char]0x7F6E
$COLLAPSE = [char]0x6536 + [char]0x8D77 + [char]0x5230 + [char]0x7403   # 收起到球

function Get-Win($name) { $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name))) }

# 收起面板 -> 球
$p = Get-Win 'Token Monitor'
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -eq $COLLAPSE) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break } }
Start-Sleep -m 1300
$ball = Get-Win 'TokenMonitor Ball'
if (-not $ball) { Write-Output 'NO_BALL'; exit }
$r = $ball.Current.BoundingRectangle
[W.U]::SetCursorPos([int]($r.X+34), [int]($r.Y+34)); Start-Sleep -m 300
[W.U]::mouse_event(8,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 100; [W.U]::mouse_event(16,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 1000
$hit = $null
foreach ($it in $ball.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)) { if ($it.Current.Name -like ($PRICING + '*')) { $hit = $it; break } }
if (-not $hit) { Write-Output 'BALL_MENU_ITEM_MISSING'; exit }
$hr = $hit.Current.BoundingRectangle
[W.U]::SetCursorPos([int]($hr.X+$hr.Width/2), [int]($hr.Y+$hr.Height/2)); Start-Sleep -m 350
[W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 140; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -m 2000
$dlg = $null
foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
  if ($wd.Current.ProcessId -eq $appPid) {
    $n = $wd.Current.Name; $w = [int]$wd.Current.BoundingRectangle.Width
    if ($n -ne 'Token Monitor' -and $n -ne 'TokenMonitor Ball' -and $w -gt 80) { $dlg = $wd; break }
  }
}
if ($dlg) { Write-Output ("BALL_PATH_OK dialog=[" + $dlg.Current.Name + "]") } else { Write-Output 'BALL_PATH_ALSO_HANGS' }
Write-Output 'DIAG8_DONE'
