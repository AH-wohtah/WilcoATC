using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WilcoATC.Input;

/// <summary>
/// Push-to-talk GLOBAL : détecte l'appui/relâchement d'UNE touche configurée, même quand
/// le simulateur a le focus (sinon le PTT serait inutilisable en vol).
///
/// Portée volontairement minimale : le hook clavier bas niveau ne fait que COMPARER le code
/// de touche à celui configuré pour lever <see cref="Pressed"/>/<see cref="Released"/>. Aucune
/// frappe n'est enregistrée, stockée ni transmise, et les touches ne sont jamais interceptées
/// (elles continuent d'aller au simulateur).
/// </summary>
public sealed class GlobalPushToTalk : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;

    private readonly LowLevelKeyboardProc _proc;   // gardé en champ : sinon le GC le collecte
    private readonly Func<int> _keyProvider;
    private IntPtr _hook = IntPtr.Zero;
    private bool _down;

    /// <summary>Touche PTT enfoncée (début de capture micro).</summary>
    public event Action? Pressed;

    /// <summary>Touche PTT relâchée (fin de capture -> transcription).</summary>
    public event Action? Released;

    /// <param name="keyProvider">Renvoie le code de touche virtuelle configuré (0 = désactivé).</param>
    public GlobalPushToTalk(Func<int> keyProvider)
    {
        _keyProvider = keyProvider;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        try
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WilcoATC/PTT] hook indisponible : " + ex);
        }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _down = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int configured = _keyProvider();
            if (configured != 0)
            {
                int vk = Marshal.ReadInt32(lParam);      // KBDLLHOOKSTRUCT.vkCode
                if (vk == configured)
                {
                    int msg = wParam.ToInt32();
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        if (!_down) { _down = true; SafeRaise(Pressed); }
                    }
                    else if (msg is WM_KEYUP or WM_SYSKEYUP)
                    {
                        if (_down) { _down = false; SafeRaise(Released); }
                    }
                }
            }
        }
        // On ne consomme JAMAIS la touche : elle continue vers le simulateur.
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SafeRaise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine("[WilcoATC/PTT] " + ex); }
    }

    /// <summary>Nom lisible d'une touche WPF (pour l'affichage dans les réglages).</summary>
    public static string DisplayName(Key key) => key.ToString();

    /// <summary>Convertit une touche WPF en code de touche virtuelle Windows.</summary>
    public static int ToVirtualKey(Key key) => KeyInterop.VirtualKeyFromKey(key);

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
