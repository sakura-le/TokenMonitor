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
$CBX = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$appPid = (Get-Process TokenMonitor.App).Id
$SETTINGS = [char]0x8BBE + [char]0x7F6E
$PRICING  = [char]0x8BA1 + [char]0x4EF7 + [char]0x914D + [char]0x7F6E
$ADDRULE  = [char]0x6DFB + [char]0x52A0 + [char]0x89C4 + [char]0x5219 + [char]0x5361
$CANCEL   = [char]0x53D6 + [char]0x6D88

function Get-Win($name) { $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name))) }
function ClickAt($x, $y) {
  [W.U]::SetCursorPos($x, $y); Start-Sleep -m 320
  [W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 140; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -m 900
}

# 0) 确保面板显示：无面板则双击球
$p = Get-Win 'Token Monitor'
if (-not $p) {
  $ball = Get-Win 'TokenMonitor Ball'
  if ($ball) {
    $r = $ball.Current.BoundingRectangle
    ClickAt ([int]($r.X+34)) ([int]($r.Y+34))
    Start-Sleep -m 200
    [W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 60; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -m 1400
    $p = Get-Win 'Token Monitor'
  }
}
if (-not $p) { Write-Output 'PANEL_UNAVAILABLE'; exit }
Write-Output 'panel visible'

# 1) 真实点击卡片「设置」
$sbtn = $null
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -eq $SETTINGS) { $sbtn = $b; break } }
if (-not $sbtn) { Write-Output 'NO_SETTINGS_BTN'; exit }
$sr = $sbtn.Current.BoundingRectangle
ClickAt ([int]($sr.X+$sr.Width/2)) ([int]($sr.Y+$sr.Height/2))
$items = $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)
Write-Output ("menu items: " + $items.Count)
if ($items.Count -eq 0) { Write-Output 'MENU_NOT_OPEN'; exit }

# 2) 真实点击「计价配置…」
$cfg = $null
foreach ($it in $items) { if ($it.Current.Name -like ($PRICING + '*')) { $cfg = $it; break } }
if (-not $cfg) { Write-Output 'PRICING_ITEM_MISSING'; exit }
$cr = $cfg.Current.BoundingRectangle
ClickAt ([int]($cr.X+$cr.Width/2)) ([int]($cr.Y+$cr.Height/2))

# 3) 找对话框
$dlg = $null
foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
  if ($wd.Current.ProcessId -eq $appPid) {
    $n = $wd.Current.Name; $w = [int]$wd.Current.BoundingRectangle.Width
    if ($n -ne 'Token Monitor' -and $n -ne 'TokenMonitor Ball' -and $w -gt 80) { $dlg = $wd; break }
  }
}
if (-not $dlg) { Write-Output 'RESULT: DIALOG_NOT_SHOWN'; exit }
Write-Output ("dialog: [" + $dlg.Current.Name + "] " + [int]$dlg.Current.BoundingRectangle.Width + "x" + [int]$dlg.Current.BoundingRectangle.Height)

# 4) 真实点击「添加规则卡」
$add = $null
foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($d.Current.Name -like ('*' + $ADDRULE + '*')) { $add = $d; break } }
if (-not $add) { Write-Output 'RESULT: DIALOG_OPEN_BUT_NO_ADD_BTN'; exit }
$c0 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CBX).Count
$ar = $add.Current.BoundingRectangle
ClickAt ([int]($ar.X+$ar.Width/2)) ([int]($ar.Y+$ar.Height/2))
$c1 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CBX).Count
Write-Output ("add-rule-card: combos " + $c0 + " -> " + $c1)
if ($c1 -gt $c0) { Write-Output 'RESULT: PASS' } else { Write-Output 'RESULT: FAIL_BUTTON_DEAD' }

foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($d.Current.Name -like ('*' + $CANCEL + '*')) { $d.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break } }
Write-Output 'FLOW_DONE'
