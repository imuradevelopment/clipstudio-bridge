using System;
using System.Drawing;
using System.Drawing.Imaging;
static class Probe2 {
    static void Main() {
        // 電卓のボタン位置を1px刻みで確認するため、ボタン中心周辺のピクセル色を確認
        // 電卓の「6」ボタンは第3列(y=423の行)、ワシが送った座標(170,425)が「5」に着弾した
        // 正確な列の境界を特定するため、y=423の行をx=100〜250で5px刻みでスキャン
        using (var bmp = new Bitmap(800, 600)) {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(1920, 0, 0, 0, new Size(800, 600));
            // y=423でx方向の色をスキャン
            for (int x = 100; x <= 300; x += 10) {
                Color c = bmp.GetPixel(x, 423);
                Console.WriteLine("x=" + x + " R=" + c.R + " G=" + c.G + " B=" + c.B);
            }
            // y=372でも確認（7〜×の行）
            Console.WriteLine("--- y=372 ---");
            for (int x = 20; x <= 320; x += 10) {
                Color c = bmp.GetPixel(x, 372);
                Console.WriteLine("x=" + x + " R=" + c.R + " G=" + c.G + " B=" + c.B);
            }
        }
    }
}
