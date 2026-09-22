using System;
using System.Drawing;
using System.Drawing.Imaging;
static class ScanBtn {
    static void Main() {
        using (var bmp = new Bitmap(800, 600)) {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(1920, 0, 0, 0, new Size(800, 600));
            bmp.Save("calc_current.png", ImageFormat.Png);
            // 電卓のボタン列・行の中心を特定するため色をスキャン
            // ボタン列のy座標を特定（暗い灰色のボタン色を検出）
            Console.WriteLine("=== y scan (x=50, y=250..550 step 10) ===");
            for (int y = 250; y <= 550; y += 10) {
                Color c = bmp.GetPixel(50, y);
                Console.WriteLine("y=" + y + " R=" + c.R + " G=" + c.G + " B=" + c.B);
            }
            Console.WriteLine("=== x scan (y=372, x=10..330 step 10) ===");
            for (int x = 10; x <= 330; x += 10) {
                Color c = bmp.GetPixel(x, 372);
                Console.WriteLine("x=" + x + " R=" + c.R + " G=" + c.G + " B=" + c.B);
            }
        }
    }
}
