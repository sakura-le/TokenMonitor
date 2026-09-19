Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
public struct RECT { public int L, T, R, B; }
'@ -Name U -Namespace W

$root = [System.Windows.Automation.AutomationElement]::RootElement
$BT = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$MI = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$ED = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
$WN = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$appPid = (Get-Process TokenMonitor.App).Id

function Find-Dlg {
  foreach ($wd in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $WN)) {
    if ($wd.Current.ProcessId -eq $appPid) {
      $w = [int]$wd.Current.BoundingRectangle.Width
      if ($w -gt 100 -and $w -lt 760) { return $wd }
    }
  }
  return $null
}
function Open-Multiplier {
  $ball = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'TokenMonitor Ball')))
  $p = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Token Monitor')))
  if ($ball) {
    $r = $ball.Current.BoundingRectangle
    [W.U]::SetCursorPos([int]($r.X+34), [int]($r.Y+34)); Start-Sleep -m 250
    [W.U]::mouse_event(8,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 80; [W.U]::mouse_event(16,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 900
    foreach ($it in $ball.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)) {
      if ($it.Current.Name -like [char]0x500D + [char]0x7387 + '*') { $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
    }
  } elseif ($p) {
    foreach ($b in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) {
      if ($b.Current.Name -eq ([char]0x8BBE + [char]0x7F6E)) {
        $br = $b.Current.BoundingRectangle
        [W.U]::SetCursorPos([int]($br.X+$br.Width/2), [int]($br.Y+$br.Height/2)); Start-Sleep -m 250
        [W.U]::mouse_event(2,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 80; [W.U]::mouse_event(4,0,0,0,[UIntPtr]::Zero); Start-Sleep -m 700
        foreach ($it in $p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $MI)) {
          if ($it.Current.Name -like [char]0x500D + [char]0x7387 + '*') { $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        }
        break
      }
    }
  }
  return $false
}

if (-not (Open-Multiplier)) { Write-Output 'MENU_PATH_FAILED'; exit }
Start-Sleep -m 1500
$dlg = Find-Dlg
if (-not $dlg) { Write-Output 'DLG_NOT_OPEN'; exit }

$uia = $dlg.Current.BoundingRectangle
$fg = [W.U]::GetForegroundWindow()
$wr = New-Object W.RECT
[W.U]::GetWindowRect($fg, [ref]$wr) | Out-Null
$sb = New-Object System.Text.StringBuilder 256
[W.U]::GetWindowTextW($fg, $sb, 256) | Out-Null
Write-Output ("UIA-dlg-rect: {0},{1} {2}x{3}" -f [int]$uia.X, [int]$uia.Y, [int]$uia.Width, [int]$uia.Height)
Write-Output ("FOREGROUND: [{0}] rect={1},{2} {3}x{4}" -f $sb.ToString(), $wr.L, $wr.T, ($wr.R-$wr.L), ($wr.B-$wr.T))

$rows0 = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ED).Count
$addPrev = [char]0x6DFB + [char]0x52A0 + [char]0x65F6 + [char]0x6BB5 + [char]0x884C
$add = $null
foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) {
  if ($d.Current.Name -like ('*' + $addPrev + '*')) { $add = $d; break }
}
if ($add) {
  $add.SetFocus()
  Start-Sleep -m 300
  $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -m 800
  $dlg2 = Find-Dlg
  $rows1 = $dlg2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ED).Count
  Write-Output ("focus+invoke: edits {0} -> {1}" -f $rows0, $rows1)
}
$cancelTxt = [char]0x53D6 + [char]0x6D88
foreach ($d in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $BT)) {
  if ($d.Current.Name -like ('*' + $cancelTxt + '*')) { $d.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break }
}
Write-Output 'DIAG_DONE'
