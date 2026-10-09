using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Warden
{
    /// <summary>
    /// Sistem genelinde Ctrl+Shift+&lt;tuş&gt; kısayolu. Görünmez bir mesaj penceresine bağlıdır; UI thread'inde oluşturulmalı.
    /// </summary>
    public sealed class GlobalHotkey : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WmHotkey = 0x0312;
        private const uint ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
        private const int Id = 0x5752;   // 'WR'
        private static readonly IntPtr HwndMessage = new(-3);

        public event Action? Pressed;

        /// <summary>Tuş başka bir uygulamada kayıtlıysa false.</summary>
        public bool IsRegistered { get; }

        public GlobalHotkey(Keys key)
        {
            CreateHandle(new CreateParams { Parent = HwndMessage });
            IsRegistered = RegisterHotKey(Handle, Id, ModControl | ModShift | ModNoRepeat, (uint)key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == Id)
                Pressed?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                UnregisterHotKey(Handle, Id);
                DestroyHandle();
            }
        }

        /// <summary>Ayarlardaki seçenekler: F9–F12.</summary>
        public static readonly Keys[] Choices = { Keys.F9, Keys.F10, Keys.F11, Keys.F12 };

        public static Keys Parse(string? name)
            => Enum.TryParse<Keys>(name, out var k) && Array.IndexOf(Choices, k) >= 0 ? k : Keys.F10;
    }
}
