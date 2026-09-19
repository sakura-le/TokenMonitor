Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
'@ -Name U -Namespace W

$root = [System.Windows.Automation.AutomationElement]::RootElement
$BT = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$MI = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$CBX = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$WN = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$appPid = (Get-Process TokenMonitor.App).Id

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
'@ -Name KB -Namespace W
for ($i = 0; $i -lt 3; $i++) { [W.KB]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero); [W.KB]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero); Start-Sleep -m 300 }
$ADDSEC = [char]0x6DFB + [char]0x52A0 + [char]0x65F6 + [char]0x6BB5 + [char]0x884C   # 添加时段行
$MULCFG = [char]0x500D + [char]0x7387 + [char]0x914D + [char]0x7F6E                    # 倍率配置
$CANCEL = [char]0x53D6 + [char]0x6D88                                                  # 取消

function Find-Dlg {
  foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
    if ($wd.Current.ProcessId -eq $appPid) {
      $w = [int]$wd.Current.BoundingRectangle.Width
      if ($w -gt 100 -and $w -lt 750 -and $wd.Current.Name -ne 'Token Monitor' -and $wd.Current.Name -ne 'TokenMonitor Ball') { return $wd }
    }
  }
  return $null
}

$SETTINGS = [char]0x8BBE + [char]0x7F6E   # 设置
$p = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Token Monitor')))
if (-not $p) { Write-Output 'NO_PANEL'; exit }
$sbtn = $null
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) {
  if ($b.Current.Name -eq $SETTINGS) { $sbtn = $b; break }
}
if (-not $sbtn) { Write-Output 'NO_SETTINGS_BTN'; exit }
$sr = $sbtn.Current.BoundingRectangle
[W.U]::SetCursorPos([int]($sr.X+$sr.Width/2), [int]($sr.Y+$sr.Height/2)); Start-Sleep -m 300
[W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 90; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -m 900
$hit = $null
foreach ($it in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)) {
  if ($it.Current.Name -like ($MULCFG + '*')) { $hit = $it; break }
}
if (-not $hit) { Write-Output 'MENU_ITEM_MISSING'; exit }
$hit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -m 1800
$dlg = Find-Dlg
if (-not $dlg) { Write-Output 'DLG_NOT_OPEN'; exit }

$btns = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)
$bn = @(); foreach ($x in $btns) { if ($x.Current.Name) { $bn += $x.Current.Name } }
Write-Output ("dlg=[" + $dlg.Current.Name + "] size=" + [int]$dlg.Current.BoundingRectangle.Width + "x" + [int]$dlg.Current.BoundingRectangle.Height + " buttons=" + ($bn -join ','))
$c0 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CBX).Count
$add = $null
foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) {
  if ($d.Current.Name -like ('*' + $ADDSEC + '*')) { $add = $d; break }
}
if (-not $add) { Write-Output 'ADD_BTN_MISSING'; exit }
$ar = $add.Current.BoundingRectangle
Write-Output ("combo before: " + $c0 + "  add-btn at " + [int]($ar.X+$ar.Width/2) + "," + [int]($ar.Y+$ar.Height/2))
[W.U]::SetCursorPos([int]($ar.X+$ar.Width/2), [int]($ar.Y+$ar.Height/2)); Start-Sleep -m 400
[W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 120; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -m 1000
$dlg2 = Find-Dlg
$c1 = $dlg2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $CBX).Count
$verdict = if ($c1 -gt $c0) { 'PASS_realtime_mouse_click_works' } else { 'FAIL_still_dead' }
Write-Output ("combo after: " + $c1 + "  => " + $verdict)

foreach ($d in $dlg2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) {
  if ($d.Current.Name -like ('*' + $CANCEL + '*')) { $d.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break }
}
Write-Output 'DONE'
