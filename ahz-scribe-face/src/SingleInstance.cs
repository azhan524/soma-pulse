using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AhzScribe.Face
{
    // Private lifecycle guard: one Face per user session.
    public static class SingleInstance
    {
        const string BaseMutexName = "AhzScribe_Face_SingleInstance_v1";
        static Mutex _mutex;

        public static string InstanceSuffix
        {
            get
            {
                string id = Environment.GetEnvironmentVariable("AHZ_INSTANCE_ID");
                if (string.IsNullOrWhiteSpace(id)) return "";
                using (SHA256 sha = SHA256.Create())
                    return "_" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(id)))
                        .Substring(0, 16).ToLowerInvariant();
            }
        }

        static string MutexName { get { return BaseMutexName + InstanceSuffix; } }

        public static bool TryAcquire()
        {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            return created;
        }

        public static void ActivateExisting()
        {
            if (!string.IsNullOrEmpty(InstanceSuffix)) return;
            try
            {
                uint current = (uint)Process.GetCurrentProcess().Id;
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == 0 || pid == current) return true;
                    try
                    {
                        var p = Process.GetProcessById((int)pid);
                        if (!string.Equals(p.ProcessName, "AhzScribe.Face", StringComparison.OrdinalIgnoreCase)) return true;
                        var title = new StringBuilder(512);
                        GetWindowText(hWnd, title, title.Capacity);
                        if (!title.ToString().StartsWith("Ahz Scribe", StringComparison.OrdinalIgnoreCase)) return true;
                        ShowWindow(hWnd, 9);
                        SetForegroundWindow(hWnd);
                        return false;
                    }
                    catch { return true; }
                }, IntPtr.Zero);
            }
            catch { }
        }

        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
