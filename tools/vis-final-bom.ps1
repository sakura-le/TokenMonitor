Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
'@ -Name U -Namespace W

$root = [System.Windows.Automation.AutomationElement]::RootElement
$BT = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$MI = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$CK = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
$WN = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$TX = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$appPid = (Get-Process TokenMonitor.App).Id
$SETTINGS = [char]0x8BBE + [char]0x7F6E
$VISITEM  = [char]0x663E + [char]0x793A + [char]0x9690 + [char]0x85CF
$UP = [char]0x25B2
$DEL = [char]0x5220 + [char]0x9664
$CANCEL = [char]0x53D6 + [char]0x6D88

function Get-Win($name) { $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name))) }
function Find-Dlg {
  foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
    if ($wd.Current.ProcessId -eq $appPid) {
      $n = $wd.Current.Name; $w = [int]$wd.Current.BoundingRectangle.Width
      if ($n -ne 'Token Monitor' -and $n -ne 'TokenMonitor Ball' -and $w -gt 80) { return $wd }
    }
  }
  return $null
}
function ClickAt($x, $y) {
  [W.U]::SetCursorPos($x, $y); Start-Sleep -m 350
  [W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 150; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -m 800
}

$p = Get-Win 'Token Monitor'
if (-not $p) { Write-Output 'NO_PANEL'; exit }
$sbtn = $null
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -eq $SETTINGS) { $sbtn = $b; break } }
$sr = $sbtn.Current.BoundingRectangle
ClickAt ([int]($sr.X+$sr.Width/2)) ([int]($sr.Y+$sr.Height/2))
$vi = $null
foreach ($it in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)) { if ($it.Current.Name -like ($VISITEM + '*')) { $vi = $it; break } }
if (-not $vi) { Write-Output 'VIS_ITEM_MISSING'; exit }
$vr = $vi.Current.BoundingRectangle
ClickAt ([int]($vr.X+$vr.Width/2)) ([int]($vr.Y+$vr.Height/2))
Start-Sleep -m 1500
$dlg = Find-Dlg
if (-not $dlg) { Write-Output 'DLG_NOT_OPEN'; exit }
Write-Output ("vis dialog: " + [int]$dlg.Current.BoundingRectangle.Width + "x" + [int]$dlg.Current.BoundingRectangle.Height)

# 1) 复选框
$cbs = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CK)
$cb = $cbs[0]; $cr = $cb.Current.BoundingRectangle
$t0 = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
ClickAt ([int]($cr.X+$cr.Width/2)) ([int]($cr.Y+$cr.Height/2))
$t1 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CK)[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
if ($t0 -ne $t1) { Write-Output ("CHECKBOX: OK (" + $t0 + "->" + $t1 + ")") } else { Write-Output ("CHECKBOX: DEAD (" + $t0 + ")") }

# 2) ▲ 排序（第二行上移，比较前两行第一列文本）
$bs = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)
$ups = @(); $del = $null
foreach ($b in $bs) { if ($b.Current.Name -eq $UP) { $ups += $b }; if ($b.Current.Name -eq $DEL) { $del = $b } }
Write-Output ("up-buttons: " + $ups.Count + "  del-button: " + ($del -ne $null))
if ($ups.Count -ge 2) {
  $txts = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TX)
  $names = @(); foreach ($t in $txts) { if ($t.Current.Name.Length -gt 5) { $names += $t.Current.Name } }
  $b0 = $names[0]
  $u2 = $ups[1].Current.BoundingRectangle
  ClickAt ([int]($u2.X+$u2.Width/2)) ([int]($u2.Y+$u2.Height/2))
  $txts2 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $TX)
  $names2 = @(); foreach ($t in $txts2) { if ($t.Current.Name.Length -gt 5) { $names2 += $t.Current.Name } }
  $a0 = $names2[0]
  if ($b0 -ne $a0) { Write-Output ("SORT: OK (" + $b0 + " -> " + $a0 + ")") } else { Write-Output ("SORT: DEAD (still " + $b0 + ")") }
} else { Write-Output 'SORT: NO_UP_BTN' }

# 3) 删除 → 确认框
if ($del) {
  $dr = $del.Current.BoundingRectangle
  ClickAt ([int]($dr.X+$dr.Width/2)) ([int]($dr.Y+$dr.Height/2))
  Start-Sleep -m 1000
  $DELTITLE = [char]0x5220 + [char]0x9664 + [char]0x6A21 + [char]0x578B + [char]0x6570 + [char]0x636E
  $mb = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $DELTITLE)))
  if ($mb) { Write-Output 'DELETE: OK (confirm shown)'; foreach ($b2 in $mb.FindAll([System.Windows.Automation.TreeScope]::Children, $BT)) { if ($b2.Current.Name -match $CANCEL) { $b2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break } } } else { Write-Output 'DELETE: DEAD' }
} else { Write-Output 'DELETE: NO_BTN' }
foreach ($b in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -match $CANCEL) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break } }
Write-Output 'DONE'
