using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sluice;

public partial class GateWindow : Window
{
    private DispatcherTimer? _timer;
    private int _secondsLeft = 60;
    private bool _scrolledToEnd;
    private bool _closing;

    public GateWindow()
    {
        InitializeComponent();
        BodyScroll.ScrollChanged += OnScrollChanged;
        Closed += (_, _) => OnClosed();
        Opened += (_, _) => OnOpened();
        AddHandler(KeyDownEvent, OnGlobalKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 用户正在组织拒绝理由时暂停倒计时（输入中不该被 60s 超时打断）
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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            EvalTimer(); // 每秒刷新显示（保持"输入中"状态一致）
            if (_secondsLeft <= 0)
            {
                Gate.Result ??= new Gate.Outcome.TimedOut();
                CloseWindow();
            }
        };
        _timer.Start();
    }

    /// <summary>焦点在自由文本框或文本非空 → 暂停倒计时；否则运行。理由草稿在，超时不触发（误触安全侧）。</summary>
    private void EvalTimer()
    {
        var pause = FreeText.IsFocused || !string.IsNullOrEmpty(FreeText.Text);
        if (pause)
        {
            _timer?.Stop();
            Countdown.Text = $"{_secondsLeft}s · 输入中";
        }
        else if (!_closing)
        {
            _timer?.Start();
            Countdown.Text = $"{_secondsLeft}s";
        }
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
            var max = sv.Extent.Height - sv.Viewport.Height;
            _scrolledToEnd = max <= 2 || sv.Offset.Y >= max - 2;
            UpdateApprove();
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

        // 这一次 Hello 同时是批准事件与 vault 解密因子（§5：单次生物事件 = 解锁 + 授权）
        var sig = await new HelloConsent().SignAsync(Gate.Req!.VaultChallenge);
        Gate.Result = sig is null
            ? new Gate.Outcome.ConsentFailed()
            : new Gate.Outcome.Approved(sig);
        CloseWindow();
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
