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
        SetOpBadgeStyle(req.Op);
        RepoText.Text = $"{req.Repo} · {req.Target}";
        ShaText.Text = $"指纹 {req.Sha8}";
        BodyText.Text = req.Body;
        if (!string.IsNullOrEmpty(req.Warning))
        {
            WarnText.Text = req.Warning;
            WarnBanner.IsVisible = true;
        }
        Title = $"Sluice · 批准请求 · {req.Op}";

        DenyBtn.Focus(); // 默认焦点拒绝（§7 误触安全侧）
        TimeBar.Maximum = Gate.TimeoutSeconds;
        EvalTimer(); // 立即按 _secondsLeft 渲染倒计时与三档配色（防首秒显示陈旧的"60s"）
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

    private void EvalTimer()
    {
        Countdown.Text = PauseReason() is { } p ? $"{_secondsLeft}s · {p}" : $"{_secondsLeft}s";
        UpdateTiers();
    }

    /// <summary>倒计时三档视觉（>30 常规 / 16–30 橙 / ≤15 红+呼吸脉冲）与顶部进度条——纯样式，不参与超时判定。</summary>
    private void UpdateTiers()
    {
        var tier = _secondsLeft > 30 ? "tier-normal" : _secondsLeft > 15 ? "tier-warn" : "tier-critical";
        CountPill.Classes.Set("tier-normal", tier == "tier-normal");
        CountPill.Classes.Set("tier-warn", tier == "tier-warn");
        CountPill.Classes.Set("tier-critical", tier == "tier-critical");
        TimeBar.Classes.Set("tier-normal", tier == "tier-normal");
        TimeBar.Classes.Set("tier-warn", tier == "tier-warn");
        TimeBar.Classes.Set("tier-critical", tier == "tier-critical");
        TimeBar.Value = Math.Max(0, _secondsLeft);
    }

    /// <summary>op 徽标配色（纯视觉，无安全语义）：仓库直改类=红、PR=蓝、issue=紫、workflow=绿、其余=中性。</summary>
    private void SetOpBadgeStyle(string op)
    {
        var cls = op switch
        {
            "push" or "branch_delete" => "op-danger",
            "pr_comment" or "pr_create" or "pr_merge" or "pr_reopen" => "op-pr",
            "issue_close" => "op-issue",
            "workflow_dispatch" => "op-wf",
            _ => "op-neutral",
        };
        OpBadge.Classes.Clear();
        OpBadge.Classes.Add("sluice-op");
        OpBadge.Classes.Add(cls);
    }

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

        // D1（2026-10-31，hello:false）：静默 vault（SLCV2）无需签名派生——批准点击即批准事件，
        // 不弹 Hello。注入点击仍被 InputFilter 拦（LLMHF_INJECTED）；此路径的降级前提见 Gate.HelloEnabled 注释。
        if (TokenStore.IsDpapiVault)
        {
            Gate.Result = new Gate.Outcome.Approved([]);
            CloseWindow();
            return;
        }

        // UI-2/3（2026-10-30 实测修订）：Hello 系统凭据 UI **不是 TOPMOST 且 Z 序在调用者
        // 窗口之下**——本窗在前必遮 Hello（置顶版实测：Hello 恰在批准窗下面）。正解是
        // 弹 Hello 前隐藏本窗；Activate 先行确保 sluice 是前台（credential UI 定位跟随
        // 前台进程，防落左上角/沉底）。Hello 完成/取消后按结果恢复。
        Activate();
        Hide();

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
            CloseWindow(); // 窗口已隐藏，直接关闭即可
            return;
        }
        if (sig is null)
        {
            // UI-4：Hello 取消/未通过 ≠ 拒绝整个请求——回窗让用户看清内容再决定。
            // 安全无损：取消不产生任何写动作；再次批准仍必须过 Hello（vault 解密同一事件）。
            _closing = false;
            StatusText.Text = "Windows Hello 已取消或未通过——可重新批准，或直接拒绝";
            StatusBanner.IsVisible = true;
            Show();
            Activate();
            UpdateApprove();
            DenyBtn.IsEnabled = true;
            _timer?.Start();
            EvalTimer();
            DenyBtn.Focus();
            return;
        }
        Gate.Result = new Gate.Outcome.Approved(sig);
        CloseWindow(); // 批准已成，无需再展示窗口
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
