# Rebuild the vector-based multi-resolution icon using only the Windows .NET framework.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$drawingReferences = @('System.Drawing')
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $drawingReferences = @([System.Drawing.Bitmap].Assembly.Location) + @([System.Drawing.Bitmap].Assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
}
Add-Type -ReferencedAssemblies $drawingReferences -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class CaptionIconBuilder
{
    private static GraphicsPath Rounded(float x, float y, float w, float h, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    private static byte[] Render(int size)
    {
        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bitmap))
        using (var background = Rounded(2, 2, 60, 60, 14))
        using (var rear = Rounded(25, 23, 27, 30, 5))
        using (var bubble = Rounded(11, 12, 33, 29, 6))
        using (var gradient = new LinearGradientBrush(new Point(2, 2), new Point(62, 62), Color.FromArgb(54, 124, 245), Color.FromArgb(32, 84, 212)))
        using (var rearBrush = new SolidBrush(Color.FromArgb(102, 255, 255, 255)))
        using (var line = new Pen(Color.FromArgb(40, 104, 229), 3.5f))
        using (var stream = new MemoryStream())
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size / 64f, size / 64f);
            g.FillPath(gradient, background); g.FillPath(rearBrush, rear);
            g.FillPolygon(Brushes.White, new[] { new Point(18, 39), new Point(18, 48), new Point(29, 39) });
            g.FillPath(Brushes.White, bubble);
            line.StartCap = line.EndCap = LineCap.Round;
            g.DrawLine(line, 19, 22, 36, 22); g.DrawLine(line, 19, 30, 31, 30);
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }
    public static void Create(string iconPath, string previewPath)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new List<byte[]>();
        foreach (int size in sizes) frames.Add(Render(size));
        using (var writer = new BinaryWriter(File.Create(iconPath)))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
        File.WriteAllBytes(previewPath, Render(256));
    }
}
'@
[CaptionIconBuilder]::Create((Join-Path $projectRoot 'assets\App.ico'), (Join-Path $projectRoot 'assets\AppIcon.png'))
Write-Output 'Created assets/App.ico and assets/AppIcon.png (16-256px).'
