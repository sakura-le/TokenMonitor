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
$SETTINGS = [char]0x8BBE + [char]0x7F6E                                   # 设置
$PRICING  = [char]0x8BA1 + [char]0x4EF7 + [char]0x914D + [char]0x7F6E     # 计价配置
$ADDSEC   = [char]0x6DFB + [char]0x52A0 + [char]0x65F6 + [char]0x6BB5 + [char]0x884C  # 添加时段行
$CANCEL   = [char]0x53D6 + [char]0x6D88                                   # 取消

function List-Win($tag) {
  Write-Output ("-- " + $tag)
  foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
    if ($wd.Current.ProcessId -eq $appPid) {
      $r = $wd.Current.BoundingRectangle
      Write-Output ("   [{0}] {1},{2} {3}x{4}" -f $wd.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    }
  }
}
function ClickAt($x, $y) {
  [W.U]::SetCursorPos($x, $y); Start-Sleep -m 320
  [W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 140; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -m 800
}

$p = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Token Monitor')))
$sbtn = $null
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -eq $SETTINGS) { $sbtn = $b; break } }
$sr = $sbtn.Current.BoundingRectangle
ClickAt ([int]($sr.X+$sr.Width/2)) ([int]($sr.Y+$sr.Height/2))

$items = $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)
Write-Output ("menu items: " + $items.Count)
$cfg = $null
foreach ($it in $items) { if ($it.Current.Name -eq ($PRICING + [char]0x2026)) { $cfg = $it; break } }
if (-not $cfg) { foreach ($it in $items) { if ($it.Current.Name -like ($PRICING + '*')) { $cfg = $it; break } } }
if (-not $cfg) { Write-Output 'PRICING_ITEM_MISSING'; exit }
$cr = $cfg.Current.BoundingRectangle
Write-Output ("pricing item at " + [int]($cr.X+$cr.Width/2) + "," + [int]($cr.Y+$cr.Height/2))
ClickAt ([int]($cr.X+$cr.Width/2)) ([int]($cr.Y+$cr.Height/2))
List-Win 'after pricing click'

$dlg = $null
foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
  if ($wd.Current.ProcessId -eq $appPid) {
    $n = $wd.Current.Name; $w = [int]$wd.Current.BoundingRectangle.Width
    if ($n -ne 'Token Monitor' -and $n -ne 'TokenMonitor Ball' -and $w -gt 80) { $dlg = $wd; break }
  }
}
if (-not $dlg) { Write-Output 'NO_DIALOG'; exit }
Write-Output ("dialog: [" + $dlg.Current.Name + "] " + [int]$dlg.Current.BoundingRectangle.Width + "x" + [int]$dlg.Current.BoundingRectangle.Height)

# 真实点击「添加规则卡」（计价对话框里的按钮）
$ADDRULE = [char]0x6DFB + [char]0x52A0 + [char]0x89C4 + [char]0x5219 + [char]0x5361   # 添加规则卡
$add = $null
foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($d.Current.Name -like ('*' + $ADDRULE + '*')) { $add = $d; break } }
if ($add) {
  $cards0 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CBX).Count
  $ar = $add.Current.BoundingRectangle
  ClickAt ([int]($ar.X+$ar.Width/2)) ([int]($ar.Y+$ar.Height/2))
  $cards1 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CBX).Count
  Write-Output ("add-rule-card real click: combos " + $cards0 + " -> " + $cards1)
  if ($cards1 -gt $cards0) { Write-Output 'RESULT: PASS_DIALOG_INTERACTIVE' } else { Write-Output 'RESULT: FAIL_DIALOG_DEAD' }
} else { Write-Output 'ADD_RULE_BTN_MISSING' }

foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($d.Current.Name -like ('*' + $CANCEL + '*')) { $d.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break } }
Write-Output 'DIAG6_DONE'
