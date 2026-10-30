using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace Sluice;

public partial class GateWindow : Window
{
    private DispatcherTimer? _timer;
    private int _secondsLeft = Gate.TimeoutSeconds;
    private bool _scrolledToEnd;
    private bool _closing;
    private DateTime? _lastScrollAt; // UI-1：最后一次滚动时间——10s 内暂停倒计时
    private bool _alerted;            // C4：超时前召回提示只发一次

    public GateWindow()
    {
        InitializeComponent();
        BodyScroll.ScrollChanged += OnScrollChanged;
        Closed += (_, _) => OnClosed();
        Opened += (_, _) => OnOpened();
        AddHandler(KeyDownEvent, OnGlobalKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 用户正在组织拒绝理由时暂停倒计时（输入中不该被超时打断）
        FreeText.GotFocus += (_, _) => EvalTimer();
        FreeText.LostFocus += (_, _) => EvalTimer();
        FreeText.TextChanged += (_, _) => EvalTimer();
    }

    private void OnOpened()
    {
        var req = Gate.Req!;
        OpText.Text = req.Op;
        RepoText.Text = $"{req.Repo}  {req.Target}";
        ShaText.Text = req.Sha8;
        BodyText.Text = req.Body;
        if (!string.IsNullOrEmpty(req.Warning))
        {
            WarnText.Text = "⚠ " + req.Warning;
            WarnText.IsVisible = true;
        }
        Title = $"Sluice · 批准请求 · {req.Op}";

        DenyBtn.Focus(); // 默认焦点拒绝（§7 误触安全侧）
        EvaluateScrollEnd();

        // timer 恒跑，暂停条件（输入中/滚动中）在 tick 内判断——否则滚动暂停后无人重启。
        // UI-1：滚到底是批准的前置条件，惩罚阅读时间与 §7 自相矛盾；10s 无滚动自动恢复，
        // 误触滚轮不会无限阻塞（agent 弹窗等待、token 浪费）。LL 钩子丢弃 injected 鼠标，
        // agent 无法伪造滚动利用暂停。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        EvalTimer(); // 每秒刷新显示（保持暂停状态一致）
        if (PauseReason() is not null) return; // 输入中 / 10s 内滚动过：不递减
        _secondsLeft--;
        if (_secondsLeft <= 15 && !_alerted)
        {
            // C4：剩 15 秒召回（提示音 + 闪任务栏）——用户暂离时有一次被叫回来的机会
            _alerted = true;
            try { MessageBeep(MB_ICONEXCLAMATION); } catch { }
            FlashTaskbar();
        }
        if (_secondsLeft <= 0)
        {
            Gate.Result ??= new Gate.Outcome.TimedOut();
            CloseWindow();
        }
    }

    /// <summary>倒计时暂停原因（null = 运行中）：输入拒绝理由中 / 10 秒内滚动过。</summary>
    private string? PauseReason()
    {
        if (FreeText.IsFocused || !string.IsNullOrEmpty(FreeText.Text)) return "输入中";
        if (_lastScrollAt is { } t && (DateTime.Now - t).TotalSeconds < 10) return "阅读中";
        return null;
    }

    private void EvalTimer() => Countdown.Text = PauseReason() is { } p ? $"{_secondsLeft}s · {p}" : $"{_secondsLeft}s";

    private void OnGlobalKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape) // Enter/Esc = 拒绝（§7）
        {
            e.Handled = true;
            Deny();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            _lastScrollAt = DateTime.Now; // UI-1：滚动即在场证明
            var max = sv.Extent.Height - sv.Viewport.Height;
            _scrolledToEnd = max <= 2 || sv.Offset.Y >= max - 2;
            UpdateApprove();
            EvalTimer();
        }
    }

    private void EvaluateScrollEnd()
    {
        var max = BodyScroll.Extent.Height - BodyScroll.Viewport.Height;
        _scrolledToEnd = max <= 2; // 内容不溢出：无需滚动
        UpdateApprove();
    }

    private void UpdateApprove()
    {
        ApproveBtn.IsEnabled = _scrolledToEnd;
        ApproveBtn.Content = _scrolledToEnd ? "批准 → Hello" : "批准（先滚到底部）";
    }

    private void OnJumpToEnd(object? sender, RoutedEventArgs e)
    {
        // 显式动作直达：滚到底 + 强制解锁（不依赖 ScrollChanged 事件）
        BodyScroll.ScrollToEnd();
        _scrolledToEnd = true;
        UpdateApprove();
        ApproveBtn.Focus();
    }

    private void OnDeny(object? sender, RoutedEventArgs e) => Deny();

    private void Deny()
    {
        if (_closing) return;
        var preset = (PresetBox.SelectedItem as ComboBoxItem)?.Content as string ?? "不批准";
        Gate.Result = new Gate.Outcome.Denied(preset,
            string.IsNullOrWhiteSpace(FreeText.Text) ? null : FreeText.Text.Trim());
        CloseWindow();
    }

    private async void OnApprove(object? sender, RoutedEventArgs e)
    {
        if (_closing || !ApproveBtn.IsEnabled) return;
        _closing = true; // 冻结窗口交互，等待 Hello
        ApproveBtn.IsEnabled = false;
        DenyBtn.IsEnabled = false;
        _timer?.Stop();
        Countdown.Text = $"{_secondsLeft}s";

        // UI-2/3：Hello 的系统凭据 UI 跟随前台窗口定位——sluice 从 agent shell 启动时
        // 常拿不到前台激活权，Hello 窗会沉底/落左上角。弹 Hello 前确保自己前台+置顶。
        Topmost = true;
        Activate();
        StatusText.Text = "等待 Windows Hello 验证…（如未见提示窗，请注意任务栏闪烁）";
        StatusText.IsVisible = true;

        // 这一次 Hello 同时是批准事件与 vault 解密因子（§5：单次生物事件 = 解锁 + 授权）
        byte[]? sig;
        try
        {
            sig = await new HelloConsent().SignAsync(Gate.Req!.VaultChallenge);
        }
        catch (VaultKeyMissingException ex)
        {
            // 密钥缺失 ≠ 用户取消：签名尚未弹出 Hello 就已不可能，单独归因（重试无用，需 setup）
            Gate.Result = new Gate.Outcome.VaultKeyMissing(ex.Message);
            CloseWindow();
            return;
        }
        if (sig is null)
        {
            // UI-4：Hello 取消/未通过 ≠ 拒绝整个请求——回到未决态让用户看清内容再决定。
            // 安全无损：取消不产生任何写动作；再次批准仍必须过 Hello（vault 解密同一事件）。
            _closing = false;
            Topmost = false;
            StatusText.Text = "Windows Hello 已取消或未通过——可重新批准，或直接拒绝";
            UpdateApprove();
            DenyBtn.IsEnabled = true;
            _timer?.Start();
            EvalTimer();
            DenyBtn.Focus();
            return;
        }
        Gate.Result = new Gate.Outcome.Approved(sig);
        CloseWindow();
    }

    // ---------- C4：超时前召回（提示音 + 任务栏闪烁——user32 直调，不引 System.Windows.Extensions 包） ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint uType);
    private const uint MB_ICONEXCLAMATION = 0x30;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    private const uint FLASHW_ALL = 3; // FLASHW_CAPTION | FLASHW_TRAY
    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private void FlashTaskbar()
    {
        if (TryGetPlatformHandle() is not { } h) return;
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = h.Handle,
            dwFlags = FLASHW_ALL,
            uCount = 3,
            dwTimeout = 0,
        };
        try { FlashWindowEx(ref fi); } catch { }
    }

    private void CloseWindow()
    {
        _closing = true;
        _timer?.Stop();
        Close();
    }

    private void OnClosed()
    {
        _timer?.Stop();
        // 窗口被关（X / Alt+F4）= 拒绝（timeout 档，§7：无"关闭即同意"）
        Gate.Result ??= new Gate.Outcome.TimedOut();
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
    }
}
