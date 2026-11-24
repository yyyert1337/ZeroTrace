using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

class IconGenerator
{
    static void Main(string[] args)
    {
        string outputPath = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");

        int[] sizes = new int[] { 256, 64, 48, 32, 16 };
        byte[][] entryData = new byte[sizes.Length][];
        bool[] isPng = new bool[] { true, false, false, false, false };

        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            using (Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float scale = s / 256f;

                if (s >= 32)
                {
                    using (GraphicsPath sp = new GraphicsPath())
                    {
                        sp.AddEllipse(64f * scale, 224f * scale, 128f * scale, 20f * scale);
                        using (PathGradientBrush pgb = new PathGradientBrush(sp))
                        {
                            pgb.CenterColor = Color.FromArgb(65, 12, 14, 28);
                            pgb.SurroundColors = new Color[] { Color.Transparent };
                            g.FillPath(pgb, sp);
                        }
                    }
                }

                PointF top = new PointF(128f * scale, 16f * scale);
                PointF left = new PointF(46f * scale, 128f * scale);
                PointF right = new PointF(210f * scale, 128f * scale);
                PointF bottom = new PointF(128f * scale, 240f * scale);
                PointF centerLeft = new PointF(110f * scale, 114f * scale);
                PointF centerRight = new PointF(146f * scale, 142f * scale);

                FillFacet(g, new PointF[] { top, left, centerLeft },
                    Color.FromArgb(255, 216, 180, 254), Color.FromArgb(255, 147, 51, 234),
                    left, top);

                FillFacet(g, new PointF[] { top, centerLeft, centerRight, right },
                    Color.FromArgb(255, 168, 85, 247), Color.FromArgb(255, 99, 102, 241),
                    top, right);

                FillFacet(g, new PointF[] { left, centerLeft, centerRight, bottom },
                    Color.FromArgb(255, 124, 58, 237), Color.FromArgb(255, 67, 24, 138),
                    centerLeft, bottom);

                FillFacet(g, new PointF[] { centerRight, right, bottom },
                    Color.FromArgb(255, 59, 7, 100), Color.FromArgb(255, 18, 10, 36),
                    centerRight, bottom);

                float wBright = Math.Max(0.8f, 1.5f * scale);
                float wSubtle = Math.Max(0.6f, 1.0f * scale);

                using (Pen bright = new Pen(Color.FromArgb(190, 255, 255, 255), wBright))
                using (Pen subtle = new Pen(Color.FromArgb(80, 255, 255, 255), wSubtle))
                {
                    bright.StartCap = LineCap.Round; bright.EndCap = LineCap.Round;
                    subtle.StartCap = LineCap.Round; subtle.EndCap = LineCap.Round;

                    g.DrawLine(bright, top, left);
                    g.DrawLine(bright, top, centerLeft);
                    g.DrawLine(bright, centerLeft, bottom);
                    g.DrawLine(subtle, centerLeft, centerRight);
                    g.DrawLine(subtle, centerRight, bottom);
                    g.DrawLine(subtle, centerRight, right);
                }

                g.Flush();

                if (isPng[i])
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        entryData[i] = ms.ToArray();
                    }
                }
                else
                {
                    entryData[i] = BitmapToIcoDib(bmp);
                }
            }
        }

        using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);

            int offset = 6 + (16 * sizes.Length);

            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                byte w = (byte)(s >= 256 ? 0 : s);
                byte h = (byte)(s >= 256 ? 0 : s);

                bw.Write(w);
                bw.Write(h);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)entryData[i].Length);
                bw.Write((uint)offset);

                offset += entryData[i].Length;
            }

            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write(entryData[i]);
            }
        }

        Console.WriteLine("Icon generated at: " + outputPath);
    }

    static byte[] BitmapToIcoDib(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int xorSize = w * h * 4;
        int andStride = ((w + 31) / 32) * 4;
        int andSize = andStride * h;
        int totalSize = 40 + xorSize + andSize;

        byte[] dib = new byte[totalSize];
        using (MemoryStream ms = new MemoryStream(dib))
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((uint)40);
            bw.Write((int)w);
            bw.Write((int)(h * 2));
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)0);
            bw.Write((uint)xorSize);
            bw.Write((int)0);
            bw.Write((int)0);
            bw.Write((uint)0);
            bw.Write((uint)0);

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixelRow = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                IntPtr src = new IntPtr(data.Scan0.ToInt64() + y * data.Stride);
                Marshal.Copy(src, pixelRow, 0, w * 4);
                bw.Write(pixelRow);
            }
            bmp.UnlockBits(data);

            byte[] andMask = new byte[andSize];
            bw.Write(andMask);
        }
        return dib;
    }

    static void FillFacet(Graphics g, PointF[] points, Color c1, Color c2, PointF p1, PointF p2)
    {
        using (GraphicsPath path = new GraphicsPath())
        {
            path.AddPolygon(points);
            using (LinearGradientBrush brush = new LinearGradientBrush(p1, p2, c1, c2))
            {
                g.FillPath(brush, path);
            }
        }
    }
}
