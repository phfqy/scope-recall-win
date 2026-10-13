# ScopeRecallWin MSIX 提权安装脚本（由 WorkBuddy 生成，用户 UAC 确认后执行）
$ErrorActionPreference = "Continue"
$log = "E:\WorkBuddy\2026-10-13-12-38-12\publish\install_result.txt"
$base = "E:\WorkBuddy\2026-10-13-12-38-12\src\scope-recall-win\scope-recall-win\AppPackages\scope-recall-win_1.0.0.0_x64_Test"
$msix = "$base\scope-recall-win_1.0.0.0_x64.msix"
$dep = "$base\Dependencies\x64\Microsoft.WindowsAppRuntime.1.8.msix"
$cer = "E:\WorkBuddy\2026-10-13-12-38-12\publish\test-cert.cer"

$out = @()
$out += "=== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
$out += "STEP1 import cert to LocalMachine\Root"
$out += (cmd /c "certutil -addstore -f Root `"$cer`"" 2>&1 | Out-String)
$out += "STEP2 Add-AppxPackage"
try {
    Add-AppxPackage -Path $msix -DependencyPath $dep -ForceUpdateFromAnyVersion -ErrorAction Stop
    $out += "INSTALL OK"
} catch {
    $out += "INSTALL FAILED: $($_.Exception.Message)"
}
$out += "STEP3 package status"
$pkg = Get-AppxPackage -Name "ScopeRecallWin.410979729" -ErrorAction SilentlyContinue
if ($pkg) {
    $out += "PKG: $($pkg.PackageFullName) | Status=$($pkg.Status)"
    $out += "STEP4 launch"
    try {
        Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App" -ErrorAction Stop
        Start-Sleep -Seconds 6
        $proc = Get-Process ScopeRecallWin -ErrorAction SilentlyContinue
        if ($proc) {
            $out += "LAUNCH OK pid=$($proc.Id) title='$($proc.MainWindowTitle)'"
            $out += "STEP5 capture screenshot"
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            $screen = [System.Windows.Forms.Screen]::Primary.Bounds
            $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
            $bmp.Save("E:\WorkBuddy\2026-10-13-12-38-12\publish\smoke_screenshot.png")
            $g.Dispose(); $bmp.Dispose()
            $out += "SCREENSHOT SAVED"
        } else {
            $out += "LAUNCH FAILED: process not found after start"
        }
    } catch {
        $out += "LAUNCH ERROR: $($_.Exception.Message)"
    }
} else {
    $out += "PKG NOT FOUND"
}
$out += "=== DONE ==="
$out | Set-Content $log -Encoding UTF8
