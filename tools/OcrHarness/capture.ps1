# Capture a screen region to PNG.
# Usage: capture.ps1 -Out file.png [-X 0 -Y 0 -W 0 -H 0]  (W/H = 0 -> full primary screen)
param(
  [Parameter(Mandatory=$true)][string]$Out,
  [int]$X = 0, [int]$Y = 0, [int]$W = 0, [int]$H = 0
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($W -le 0) { $W = $screen.Width }
if ($H -le 0) { $H = $screen.Height }
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out ($W x $H at $X,$Y)"
