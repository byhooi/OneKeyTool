// GenerateIcon.cs — 自动生成 OneKeyTool 专属多分辨率 .ico 图标
// 编译：csc /r:System.Drawing.dll tools\GenerateIcon\GenerateIcon.cs
// 运行：.\GenerateIcon.exe [输出路径]  (默认输出到 src\app.ico)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace OneKeyTool.Tools
{
    internal static class GenerateIcon
    {
        private static void Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : Path.Combine("src", "app.ico");
            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Console.WriteLine("正在生成 OneKeyTool 应用图标...");

            // 生成 16, 24, 32, 48, 64, 128, 256 尺寸的 Bitmap
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            List<Bitmap> bitmaps = new List<Bitmap>();

            foreach (int size in sizes)
            {
                bitmaps.Add(RenderKeycapIcon(size));
            }

            // 保存多尺寸 ICO 文件
            SaveMultiSizeIcon(bitmaps, outPath);

            foreach (var b in bitmaps) b.Dispose();

            Console.WriteLine("成功生成图标: " + Path.GetFullPath(outPath));
        }

        /// <summary>
        /// 绘制单个尺寸的立体科技感键帽图标
        /// </summary>
        private static Bitmap RenderKeycapIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                float scale = size / 256.0f;

                // 键帽参数（基于 256x256 基准画布）
                // 1. 底座与阴影 (Key Skirt / Shadow)
                // 2. 键帽外壳 (Keycap Body)
                // 3. 键帽顶面 (Keycap Top Face，带微凹陷与渐变)
                // 4. 核心符号 (发光的定制闪电 / 1 键标识)

                if (size <= 16)
                {
                    // 16x16 专门做像素对齐优化，避免次像素模糊
                    RenderPixelAligned16(g);
                    return bmp;
                }

                // 256基准尺寸绘制
                DrawVectorKeycap(g, scale);
            }
            return bmp;
        }

        /// <summary>
        /// 针对 16x16（系统托盘区）做高清晰度像素对齐绘制
        /// </summary>
        private static void RenderPixelAligned16(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 外底座阴影/底边
            using (Brush shadow = new SolidBrush(Color.FromArgb(200, 10, 14, 22)))
            {
                FillRoundedRectangle(g, shadow, 1, 1, 14, 14, 3.5f);
            }

            // 键帽顶面
            using (LinearGradientBrush topBrush = new LinearGradientBrush(
                new Point(2, 2), new Point(2, 13),
                Color.FromArgb(255, 42, 48, 62),
                Color.FromArgb(255, 26, 30, 40)))
            {
                FillRoundedRectangle(g, topBrush, 1.5f, 1.5f, 13f, 12f, 3.0f);
            }

            // 顶面内边高光
            using (Pen highPen = new Pen(Color.FromArgb(120, 80, 110, 150), 1f))
            {
                DrawRoundedRectangle(g, highPen, 2f, 2f, 12f, 11f, 2.5f);
            }

            // 中间闪电/定制符号（高对比度青蓝 + 亮光）
            PointF[] bolt = new PointF[]
            {
                new PointF(9.5f, 2.5f),
                new PointF(5.0f, 8.0f),
                new PointF(8.0f, 8.0f),
                new PointF(6.5f, 13.5f),
                new PointF(11.5f, 7.0f),
                new PointF(8.5f, 7.0f),
                new PointF(10.5f, 2.5f)
            };

            using (Brush boltBrush = new SolidBrush(Color.FromArgb(255, 0, 230, 255)))
            {
                g.FillPolygon(boltBrush, bolt);
            }

            // 16x16 核心微白光
            using (Pen corePen = new Pen(Color.FromArgb(230, 255, 255, 255), 1f))
            {
                g.DrawLine(corePen, 8.5f, 4.0f, 7.0f, 8.0f);
            }
        }

        /// <summary>
        /// 矢量级立体键帽绘制（适用于 24 到 256 尺寸）
        /// </summary>
        private static void DrawVectorKeycap(Graphics g, float s)
        {
            // 投影/光晕
            using (GraphicsPath shadowPath = GetRoundedRectPath(18 * s, 26 * s, 220 * s, 218 * s, 42 * s))
            {
                using (PathGradientBrush pgb = new PathGradientBrush(shadowPath))
                {
                    pgb.CenterColor = Color.FromArgb(140, 0, 180, 220); // 外圈微蓝弱光晕
                    pgb.SurroundColors = new Color[] { Color.Transparent };
                    g.FillPath(pgb, shadowPath);
                }
            }

            // 1. 底座/底面（深空灰蓝）
            using (LinearGradientBrush baseBrush = new LinearGradientBrush(
                new PointF(0, 30 * s), new PointF(0, 245 * s),
                Color.FromArgb(255, 20, 24, 33),
                Color.FromArgb(255, 10, 12, 18)))
            {
                FillRoundedRectangle(g, baseBrush, 22 * s, 30 * s, 212 * s, 206 * s, 44 * s);
            }

            // 底座微边框
            using (Pen borderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1.5f * s))
            {
                DrawRoundedRectangle(g, borderPen, 22 * s, 30 * s, 212 * s, 206 * s, 44 * s);
            }

            // 2. 键帽斜面与顶面基座
            using (LinearGradientBrush skirtBrush = new LinearGradientBrush(
                new PointF(0, 32 * s), new PointF(0, 220 * s),
                Color.FromArgb(255, 38, 45, 60),
                Color.FromArgb(255, 18, 22, 30)))
            {
                FillRoundedRectangle(g, skirtBrush, 26 * s, 34 * s, 204 * s, 194 * s, 40 * s);
            }

            // 3. 键帽顶面 (Keycap Top Surface)
            // 顶部凹陷质感与精致微边
            float topX = 36 * s;
            float topY = 42 * s;
            float topW = 184 * s;
            float topH = 170 * s;
            float topR = 32 * s;

            using (LinearGradientBrush topBrush = new LinearGradientBrush(
                new PointF(topX, topY), new PointF(topX, topY + topH),
                Color.FromArgb(255, 52, 60, 78),
                Color.FromArgb(255, 28, 33, 45)))
            {
                FillRoundedRectangle(g, topBrush, topX, topY, topW, topH, topR);
            }

            // 顶面上边缘高光（模拟光源从上方打下）
            using (GraphicsPath topRim = GetRoundedRectPath(topX + 2 * s, topY + 2 * s, topW - 4 * s, topH - 4 * s, topR - 2 * s))
            {
                using (Pen rimPen = new Pen(Color.FromArgb(90, 140, 180, 230), 2f * s))
                {
                    g.DrawPath(rimPen, topRim);
                }
            }

            // 4. 键帽中心：主视觉符号
            // 左上角小标："1" 键
            // 正中央：极速响应闪电 + 环形科技电路感
            DrawSymbols(g, s, topX, topY, topW, topH);
        }

        private static void DrawSymbols(Graphics g, float s, float tx, float ty, float tw, float th)
        {
            // 在较大尺寸（>=48）下绘制小角标 "KEY 1" 或 "[1]"
            if (s >= 0.18f)
            {
                using (Font subFont = new Font("Segoe UI", 20f * s, FontStyle.Bold))
                using (Brush subBrush = new SolidBrush(Color.FromArgb(170, 110, 145, 185)))
                {
                    // 左上角标 "1"
                    g.DrawString("1", subFont, subBrush, tx + 14 * s, ty + 10 * s);
                }
            }

            // 中央发光外环
            float cx = tx + tw / 2.0f;
            float cy = ty + th / 2.0f + 2 * s;
            float ringR = 48 * s;

            using (Pen ringPen = new Pen(Color.FromArgb(50, 0, 229, 255), 3f * s))
            {
                g.DrawEllipse(ringPen, cx - ringR, cy - ringR, ringR * 2, ringR * 2);
            }

            // 核心符号：闪电 (Lightning Bolt)
            // 坐标基于 (cx, cy)
            PointF[] bolt = new PointF[]
            {
                new PointF(cx + 4 * s,  cy - 48 * s),
                new PointF(cx - 30 * s, cy + 2 * s),
                new PointF(cx - 2 * s,  cy + 2 * s),
                new PointF(cx - 14 * s, cy + 46 * s),
                new PointF(cx + 30 * s, cy - 8 * s),
                new PointF(cx + 6 * s,  cy - 8 * s),
                new PointF(cx + 20 * s, cy - 48 * s)
            };

            // 闪电外发光
            using (GraphicsPath boltPath = new GraphicsPath())
            {
                boltPath.AddPolygon(bolt);

                using (Pen glowPen = new Pen(Color.FromArgb(80, 0, 229, 255), 8f * s))
                {
                    glowPen.LineJoin = LineJoin.Round;
                    g.DrawPath(glowPen, boltPath);
                }

                // 闪电主体填充（青蓝渐变）
                using (LinearGradientBrush boltGrad = new LinearGradientBrush(
                    new PointF(cx, cy - 48 * s), new PointF(cx, cy + 46 * s),
                    Color.FromArgb(255, 120, 255, 255),
                    Color.FromArgb(255, 0, 210, 255)))
                {
                    g.FillPath(boltGrad, boltPath);
                }

                // 闪电核心高光线（纯白光心）
                PointF[] corePoints = new PointF[]
                {
                    new PointF(cx + 7 * s,  cy - 42 * s),
                    new PointF(cx - 20 * s, cy + 0 * s),
                    new PointF(cx + 1 * s,  cy + 0 * s),
                    new PointF(cx - 8 * s, cy + 36 * s)
                };
                using (Pen corePen = new Pen(Color.FromArgb(200, 255, 255, 255), 2.2f * s))
                {
                    corePen.StartCap = LineCap.Round;
                    corePen.EndCap = LineCap.Round;
                    corePen.LineJoin = LineJoin.Round;
                    g.DrawLines(corePen, corePoints);
                }
            }
        }

        // ==================== 几何路径工具 ====================

        private static GraphicsPath GetRoundedRectPath(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            float d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void FillRoundedRectangle(Graphics g, Brush brush, float x, float y, float w, float h, float r)
        {
            using (GraphicsPath p = GetRoundedRectPath(x, y, w, h, r))
            {
                g.FillPath(brush, p);
            }
        }

        private static void DrawRoundedRectangle(Graphics g, Pen pen, float x, float y, float w, float h, float r)
        {
            using (GraphicsPath p = GetRoundedRectPath(x, y, w, h, r))
            {
                g.DrawPath(pen, p);
            }
        }

        // ==================== 多分辨率 ICO 编码器 ====================

        /// <summary>
        /// 将多张尺寸不同的 Bitmap 合成为标准 Windows .ico 文件。
        /// 所有尺寸均以 PNG 封装（Vista 及以上原生支持；本工具不支持 XP）。
        /// </summary>
        private static void SaveMultiSizeIcon(List<Bitmap> bitmaps, string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                // ICONDIR: 6 bytes
                bw.Write((ushort)0);              // Reserved
                bw.Write((ushort)1);              // Type: 1 = ICO
                bw.Write((ushort)bitmaps.Count);  // Image count

                // 准备每张图像的二进制流 (全部以 PNG 编码，Windows Vista/7/8/10/11 完全原生支持)
                List<byte[]> pngBuffers = new List<byte[]>();
                foreach (Bitmap b in bitmaps)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        b.Save(ms, ImageFormat.Png);
                        pngBuffers.Add(ms.ToArray());
                    }
                }

                // 计算头部总大小：6 + 16 * count
                int offset = 6 + 16 * bitmaps.Count;

                // 写入 ICONDIRENTRY 列表
                for (int i = 0; i < bitmaps.Count; i++)
                {
                    Bitmap b = bitmaps[i];
                    byte[] data = pngBuffers[i];

                    byte w = (byte)(b.Width >= 256 ? 0 : b.Width);
                    byte h = (byte)(b.Height >= 256 ? 0 : b.Height);

                    bw.Write(w);                      // Width
                    bw.Write(h);                      // Height
                    bw.Write((byte)0);                // Color count
                    bw.Write((byte)0);                // Reserved
                    bw.Write((ushort)1);              // Planes
                    bw.Write((ushort)32);             // Bit count (32bpp)
                    bw.Write((uint)data.Length);      // Bytes in resource
                    bw.Write((uint)offset);           // Image offset

                    offset += data.Length;
                }

                // 写入所有图像的二进制数据
                for (int i = 0; i < pngBuffers.Count; i++)
                {
                    bw.Write(pngBuffers[i]);
                }
            }
        }
    }
}
