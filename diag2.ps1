Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
'@ -Name U -Namespace W

$root = [System.Windows.Automation.AutomationElement]::RootElement
$MI = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$WN = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$appPid = (Get-Process TokenMonitor.App).Id

function List-Windows($tag) {
  Write-Output ("--- windows " + $tag + " ---")
  foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
    if ($wd.Current.ProcessId -eq $appPid) {
      $r = $wd.Current.BoundingRectangle
      Write-Output ("[{0}] {1},{2} {3}x{4} off={5}" -f $wd.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height, $wd.Current.IsOffscreen)
    }
  }
}

List-Windows 'before'
$ball = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'TokenMonitor Ball')))
if ($ball) {
  $r = $ball.Current.BoundingRectangle
  [W.U]::SetCursorPos([int]($r.X+34), [int]($r.Y+34)); Start-Sleep -m 300
  [W.U]::mouse_event(8,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 90; [W.U]::mouse_event(16,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 900
  $hits = $ball.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)
  Write-Output ("ball menu items: " + $hits.Count)
  foreach ($it in $hits) {
    $n = $it.Current.Name
    if ($n.Length -ge 2 -and $n[0] -eq [char]0x500D -and $n[1] -eq [char]0x7387) {
      Write-Output ("invoking: " + $n)
      $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      break
    }
  }
} else { Write-Output 'BALL_NOT_FOUND' }
Start-Sleep -m 2000
List-Windows 'after'
Write-Output 'LIST_DONE'
