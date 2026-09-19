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

$p = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Token Monitor')))
if (-not $p) { Write-Output 'NO_PANEL'; exit }
$sbtn = $null
foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) { if ($b.Current.Name -eq $SETTINGS) { $sbtn = $b; break } }
if (-not $sbtn) { Write-Output 'NO_SETTINGS_BTN'; exit }
$sr = $sbtn.Current.BoundingRectangle
[W.U]::SetCursorPos([int]($sr.X+$sr.Width/2), [int]($sr.Y+$sr.Height/2)); Start-Sleep -m 300
[W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 100; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -m 1000
$items = $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)
Write-Output ("menu items: " + $items.Count)
foreach ($it in $items) {
  $n = $it.Current.Name
  if ($n) {
    $auto = $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    Write-Output ("  enabled=" + $it.Current.IsEnabled + "  [" + $n + "]")
  }
}
Write-Output 'PROBE_DONE'
