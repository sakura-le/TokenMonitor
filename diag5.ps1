Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
'@ -Name U -Namespace W

$root = [System.Windows.Automation.AutomationElement]::RootElement
$BT = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$MI = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$SETTINGS = [char]0x8BBE + [char]0x7F6E
$OPENCFG  = [char]0x6253 + [char]0x5F00 + [char]0x914D + [char]0x7F6E + [char]0x6587 + [char]0x4EF6  # 打开配置文件

$p = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Token Monitor')))
$sbtn = $null
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -eq $SETTINGS) { $sbtn = $b; break } }
$sr = $sbtn.Current.BoundingRectangle
[W.U]::SetCursorPos([int]($sr.X+$sr.Width/2), [int]($sr.Y+$sr.Height/2)); Start-Sleep -m 300
[W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 100; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -m 1000

$before = (Get-Process notepad -ErrorAction SilentlyContinue).Count
foreach ($it in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)) {
  if ($it.Current.Name -eq $OPENCFG) {
    $ir = $it.Current.BoundingRectangle
    Write-Output ("clicking menu item at " + [int]($ir.X+$ir.Width/2) + "," + [int]($ir.Y+$ir.Height/2))
    [W.U]::SetCursorPos([int]($ir.X+$ir.Width/2), [int]($ir.Y+$ir.Height/2)); Start-Sleep -m 350
    [W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 120; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    break
  }
}
Start-Sleep -m 2500
$after = (Get-Process notepad -ErrorAction SilentlyContinue).Count
Write-Output ("notepad before=$before after=$after")
if ($after -gt $before) { Write-Output 'RESULT: SETTINGS_MENU_COMMANDS_WORK (notepad opened)' }
else { Write-Output 'RESULT: SETTINGS_MENU_COMMAND_DEAD' }
foreach ($n in (Get-Process notepad -ErrorAction SilentlyContinue)) { $n.CloseMainWindow() | Out-Null }
Write-Output 'PROBE2_DONE'
