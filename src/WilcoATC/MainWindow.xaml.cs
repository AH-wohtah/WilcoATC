using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FreqWatch.ViewModels;

namespace FreqWatch;

public partial class MainWindow : Window
{
    // Raccourci GLOBAL de test (fonctionne même quand MSFS a le focus) : Ctrl + Alt + A.
    private const int HotkeyId = 0xB001;
    private const uint ModAlt = 0x0001, ModControl = 0x0002;
    private const uint VkA = 0x41;
    private const int WmHotkey = 0x0312;

    private HwndSource? _source;

    public MainWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        try { RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModAlt, VkA); }
        catch { /* raccourci global indisponible -> le bouton et F1 restent utilisables */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            (DataContext as MainViewModel)?.TestTransmissionCommand.Execute(null);
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HotkeyId);
        }
        catch { }
        _source?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
