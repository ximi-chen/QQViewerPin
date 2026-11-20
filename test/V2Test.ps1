$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$imgTitle = [string]([char]0x56FE) + [char]0x7247 + [char]0x67E5 + [char]0x770B + [char]0x5668
$wxTitle = [string]([char]0x56FE) + [char]0x7247 + [char]0x548C + [char]0x89C6 + [char]0x9891

Get-Process QQViewerPin -ErrorAction SilentlyContinue | Stop-Process -Force
$qqFake = Join-Path $root 'test\QQ.exe'
$wxFake = Join-Path $root 'test\Weixin.exe'
Get-Process QQ -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $qqFake } | Stop-Process -Force
Get-Process Weixin -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $wxFake } | Stop-Process -Force
Start-Sleep -Milliseconds 300
if (-not (Test-Path $wxFake)) { Copy-Item $qqFake $wxFake }

$testData = Join-Path $root 'testdata'
New-Item -ItemType Directory -Force $testData | Out-Null
Remove-Item (Join-Path $testData 'log.txt') -ErrorAction SilentlyContinue
$json = '{"image":{"x":300,"y":200,"w":700,"h":900},"video":null,"alpha":60,"hideFromTaskbar":true,"autoStart":false,"hotkeyEnabled":true,"hotkeyModifiers":3,"hotkeyKey":122,"closeHotkeyEnabled":true,"closeHotkeyModifiers":3,"closeHotkeyKey":123,"weChatMedia":null,"qqEnabled":true,"weChatEnabled":true,"weChatAlpha":70}'
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
  [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
  [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr h, out uint color, out byte alpha, out uint flags);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr FindHandle(string want) {
    IntPtr result = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
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
    return string.Format("hwnd=0x{0:X}|pid={1}|rect={2},{3},{4},{5}|ex=0x{6:X}|layered={7}|alpha={8}|vis={9}|iconic={10}",
      h.ToInt64(), pid, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, ex, lay, lay?a:-1, IsWindowVisible(h), IsIconic(h));
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

function Start-Fake($path, $title, $tag) {
  return Start-Process $path -ArgumentList @($title) -PassThru `
    -RedirectStandardOutput (Join-Path $root "test\$tag-out.txt") `
    -RedirectStandardError (Join-Path $root "test\$tag-err.txt")
}

$env:QQVIEWERPIN_DATA = $testData
$app = Start-Process (Join-Path $root 'QQViewerPin.exe') -ArgumentList '--test' -PassThru `
  -RedirectStandardOutput (Join-Path $root 'test\v2-app-out.txt') `
  -RedirectStandardError (Join-Path $root 'test\v2-app-err.txt')
Start-Sleep -Milliseconds 900
"app pid=$($app.Id) alive=$(-not $app.HasExited)"

$q = Start-Fake $qqFake $imgTitle 'v2q'
Start-Sleep -Milliseconds 1600
"qq: " + [WinInspect]::Find($imgTitle)

$w = Start-Fake $wxFake $wxTitle 'v2w'
Start-Sleep -Milliseconds 1600
"wx: " + [WinInspect]::Find($wxTitle)

"hide-all post: " + [WinInspect]::PostHotkey([uint32]$app.Id, 1)
Start-Sleep -Milliseconds 600
"qq after hide: " + [WinInspect]::Find($imgTitle)
"wx after hide: " + [WinInspect]::Find($wxTitle)

"restore-all post: " + [WinInspect]::PostHotkey([uint32]$app.Id, 1)
Start-Sleep -Milliseconds 600
"qq after restore: " + [WinInspect]::Find($imgTitle)
"wx after restore: " + [WinInspect]::Find($wxTitle)

"close-all post: " + [WinInspect]::PostHotkey([uint32]$app.Id, 2)
Start-Sleep -Milliseconds 1000
"qq exited=" + $q.HasExited + " wx exited=" + $w.HasExited
"config: " + (Get-Content (Join-Path $testData 'config.json') -Raw)

Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
"--- log ---"
if (Test-Path (Join-Path $testData 'log.txt')) { Get-Content (Join-Path $testData 'log.txt') -Encoding UTF8 }
