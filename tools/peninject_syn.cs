// peninject_syn.cs — InjectSyntheticPointerInputでペン入力を注入（筆圧つき）
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class PenInject {
    [DllImport("user32.dll")] static extern IntPtr CreateSyntheticPointerDevice(uint type, uint maxCount, uint mode);
    [DllImport("user32.dll")] static extern bool InjectSyntheticPointerInput(IntPtr dev, ref POINTER_TYPE_INFO info, uint count);
    [DllImport("user32.dll")] static extern void DestroySyntheticPointerDevice(IntPtr dev);

    const uint PT_PEN = 3;
    const uint POINTER_FLAG_DOWN = 0x00010000;
    const uint POINTER_FLAG_UPDATE = 0x00020000;
    const uint POINTER_FLAG_UP = 0x00040000;
    const uint POINTER_FLAG_INRANGE = 0x00000002;
    const uint POINTER_FLAG_INCONTACT = 0x00000004;
    const uint POINTER_FLAG_PRIMARY = 0x00000200;
    const uint PEN_MASK_PRESSURE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_PEN_INFO {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TYPE_INFO {
        public uint type;
        public POINTER_PEN_INFO penInfo;
    }

    static uint frameId = 0;

    static void Main(string[] args) {
        int sx = int.Parse(args[0]);
        int sy = int.Parse(args[1]);
        uint pressure = args.Length > 2 ? uint.Parse(args[2]) : 512u;

        IntPtr dev = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
        if (dev == IntPtr.Zero) {
            Console.WriteLine("ERR: Create failed err=" + Marshal.GetLastWin32Error());
            return;
        }

        try {
            POINTER_TYPE_INFO info = Make(sx, sy, POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_PRIMARY, pressure);
            if (!Inject(dev, ref info, "DOWN")) return;
            Thread.Sleep(80);

            for (int i = 1; i <= 3; i++) {
                int dx = sx + i * 20, dy = sy + i * 10;
                info = Make(dx, dy, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, pressure);
                if (!Inject(dev, ref info, "UPDATE" + i)) return;
                Thread.Sleep(30);
            }

            info = Make(sx + 60, sy + 30, POINTER_FLAG_UP | POINTER_FLAG_INRANGE, 0);
            if (!Inject(dev, ref info, "UP")) return;

            Console.WriteLine("OK: pen injected (" + sx + "," + sy + ") pressure=" + pressure);
        } finally {
            DestroySyntheticPointerDevice(dev);
        }
    }

    static POINTER_TYPE_INFO Make(int sx, int sy, uint flags, uint pressure) {
        frameId++;
        POINTER_TYPE_INFO t = new POINTER_TYPE_INFO();
        t.type = PT_PEN;
        t.penInfo.pointerInfo.pointerType = PT_PEN;
        t.penInfo.pointerInfo.pointerId = 1;
        t.penInfo.pointerInfo.frameId = frameId;
        t.penInfo.pointerInfo.pointerFlags = flags;
        t.penInfo.pointerInfo.ptPixelLocation.X = sx;
        t.penInfo.pointerInfo.ptPixelLocation.Y = sy;
        t.penInfo.pointerInfo.historyCount = 1;
        t.penInfo.penMask = PEN_MASK_PRESSURE;
        t.penInfo.pressure = pressure;
        return t;
    }

    static bool Inject(IntPtr dev, ref POINTER_TYPE_INFO info, string stage) {
        if (InjectSyntheticPointerInput(dev, ref info, 1)) return true;
        Console.WriteLine("ERR: inject at " + stage + " err=" + Marshal.GetLastWin32Error());
        return false;
    }
}
