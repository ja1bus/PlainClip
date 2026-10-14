#Requires -Version 7.0
<#
.SYNOPSIS
    PlainClip のアイコン元画像 icon.png（1024px）を描画する。

.DESCRIPTION
    クリップボード（板＋クリップ）に「書式なしの文字」を表す横線を描いた単純な図柄。
    生成後に tools\make-icon.ps1 で Resources\icon.ico を作る。

.EXAMPLE
    pwsh -File tools\draw-icon.ps1
    pwsh -File tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot 'icon.png'
$size = 1024

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # 板（青）
    $board = New-RoundRect 150 120 724 850 90
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 103, 192)), $board)
    # 紙（白）
    $paper = New-RoundRect 230 250 564 640 40
    $g.FillPath([System.Drawing.Brushes]::White, $paper)
    # クリップ（濃い灰）
    $clip = New-RoundRect 350 60 324 170 60
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 60, 60, 60)), $clip)
    # 文字を表す横線（書式なし = 同じ太さ・同じ色）
    $lineBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 70, 70, 70))
    foreach ($row in 0..3) {
        $w = if ($row -eq 3) { 280 } else { 424 }
        $line = New-RoundRect 300 (340 + $row * 125) $w 56 28
        $g.FillPath($lineBrush, $line)
    }
}
finally {
    $g.Dispose()
}
$bmp.Save($output, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "書き出し: $output"
