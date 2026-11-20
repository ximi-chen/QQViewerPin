$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$imgTitle = [string]([char]0x56FE) + [char]0x7247 + [char]0x67E5 + [char]0x770B + [char]0x5668
$vidTitle = [string]([char]0x89C6) + [char]0x9891 + [char]0x64AD + [char]0x653E + [char]0x5668

Get-Process QQViewerPin -ErrorAction SilentlyContinue | Stop-Process -Force
$fakePath = Join-Path $root 'test\QQ.exe'
Get-Process QQ -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $fakePath } | Stop-Process -Force
Start-Sleep -Milliseconds 300

$testData = Join-Path $root 'testdata'
New-Item -ItemType Directory -Force $testData | Out-Null
Remove-Item (Join-Path $testData 'log.txt') -ErrorAction SilentlyContinue
$json = '{"image":{"x":300,"y":200,"w":700,"h":900},"video":null,"alpha":60,"hideFromTaskbar":true,"autoStart":false}'
[System.IO.File]::WriteAllText((Join-Path $testData 'config.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

$code = @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinInspect {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  private delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
  [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h, out uint color, out byte alpha, out uint flags);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

  public static IntPtr FindHandle(string want) {
    IntPtr result = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      if (cls.ToString() != "Chrome_WidgetWin_1") return true;
      var t = new StringBuilder(512); GetWindowText(h, t, 512);
      if (t.ToString() != want) return true;
      result = h; return false;
    }, IntPtr.Zero);
    return result;
  }
  public static string Find(string want) {
    IntPtr h = FindHandle(want);
    if (h == IntPtr.Zero) return "";
    RECT r; GetWindowRect(h, out r);
    uint pid; GetWindowThreadProcessId(h, out pid);
    int ex = GetWindowLong(h, -20);
    uint col; byte a; uint fl; bool lay = GetLayeredWindowAttributes(h, out col, out a, out fl);
    return string.Format("hwnd=0x{0:X}|pid={1}|rect={2},{3},{4},{5}|ex=0x{6:X}|layered={7}|alpha={8}|vis={9}",
      h.ToInt64(), pid, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, ex, lay, lay?a:-1, IsWindowVisible(h));
  }
  public static bool Move(string want, int x, int y, int w, int h) {
    IntPtr hwnd = FindHandle(want);
    if (hwnd == IntPtr.Zero) return false;
    return SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010);
  }
  public static bool Close(string want) {
    IntPtr hwnd = FindHandle(want);
    if (hwnd == IntPtr.Zero) return false;
    return PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
  }
  public static bool Iconic(string want) {
    IntPtr hwnd = FindHandle(want);
    return hwnd != IntPtr.Zero && IsIconic(hwnd);
  }
  public static bool PostHotkey(uint pid, int id) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      var t = new StringBuilder(512); GetWindowText(h, t, 512);
      if (t.ToString() != "QQViewerPin.Message") return true;
      found = h; return false;
    }, IntPtr.Zero);
    if (found == IntPtr.Zero) return false;
    return PostMessage(found, 0x0312, (IntPtr)id, IntPtr.Zero);
  }
}
'@
Add-Type -TypeDefinition $code

function Start-Fake([string]$title, [string]$tag) {
  return Start-Process $fakePath -ArgumentList @($title) -PassThru `
    -RedirectStandardOutput (Join-Path $root "test\$tag-out.txt") `
    -RedirectStandardError (Join-Path $root "test\$tag-err.txt")
}
function ShowConfig() { return (Get-Content (Join-Path $testData 'config.json') -Raw) }

$env:QQVIEWERPIN_DATA = $testData
$app = Start-Process (Join-Path $root 'QQViewerPin.exe') -ArgumentList '--test' -PassThru `
  -RedirectStandardOutput (Join-Path $root 'test\app-out.txt') `
  -RedirectStandardError (Join-Path $root 'test\app-err.txt')
Start-Sleep -Milliseconds 900
"app alive=$(-not $app.HasExited) pid=$($app.Id)"

$f1 = Start-Fake $imgTitle 'q1'
Start-Sleep -Milliseconds 1800
"f1 pid=$($f1.Id) exited=$($f1.HasExited)"
"win1: " + [WinInspect]::Find($imgTitle)
"config: " + (ShowConfig)

"hotkey post: " + [WinInspect]::PostHotkey([uint32]$app.Id, 1)
Start-Sleep -Milliseconds 500
"iconic after hotkey=" + [WinInspect]::Iconic($imgTitle)
[void][WinInspect]::PostHotkey([uint32]$app.Id, 1)
Start-Sleep -Milliseconds 500
"iconic after second=" + [WinInspect]::Iconic($imgTitle)

$f2 = Start-Fake $imgTitle 'q2'
Start-Sleep -Milliseconds 1800
"f2 pid=$($f2.Id) exited=$($f2.HasExited)"
"f1 exited after second=" + $f1.HasExited
"win2: " + [WinInspect]::Find($imgTitle)
"config: " + (ShowConfig)

"move f2: " + [WinInspect]::Move($imgTitle, 500, 300, 900, 1000)
Start-Sleep -Milliseconds 1300
"win2 moved: " + [WinInspect]::Find($imgTitle)
"config after move: " + (ShowConfig)

"close f2: " + [WinInspect]::Close($imgTitle)
Start-Sleep -Milliseconds 900
"win2 after close: " + [WinInspect]::Find($imgTitle)
"config after close: " + (ShowConfig)

$f4 = Start-Fake $imgTitle 'q4'
Start-Sleep -Milliseconds 1800
"f4 pid=$($f4.Id) exited=$($f4.HasExited)"
"win4 (expect 500,300 900x1000): " + [WinInspect]::Find($imgTitle)
[void][WinInspect]::Close($imgTitle)
Start-Sleep -Milliseconds 900

$f3 = Start-Fake $vidTitle 'v1'
Start-Sleep -Milliseconds 1800
"f3 pid=$($f3.Id) exited=$($f3.HasExited)"
"winv: " + [WinInspect]::Find($vidTitle)
"config: " + (ShowConfig)

"close-all post: " + [WinInspect]::PostHotkey([uint32]$app.Id, 2)
Start-Sleep -Milliseconds 900
"winv after close-all: " + [WinInspect]::Find($vidTitle)
"f3 exited after close-all=" + $f3.HasExited
"config after close-all: " + (ShowConfig)

Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
"--- app log ---"
if (Test-Path (Join-Path $testData 'log.txt')) { Get-Content (Join-Path $testData 'log.txt') -Encoding UTF8 }
"--- err ---"
if (Test-Path (Join-Path $root 'test\app-err.txt')) { Get-Content (Join-Path $root 'test\app-err.txt') -Raw }
