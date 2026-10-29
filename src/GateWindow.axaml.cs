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
    }

    private void OnOpened()
    {
        var req = Gate.Req!;
        OpText.Text = req.Op;
        RepoText.Text = $"{req.Repo}  {req.Target}";
        ShaText.Text = req.Sha8;
        BodyText.Text = req.Body;
        Title = $"Sluice · 批准请求 · {req.Op}";

        DenyBtn.Focus(); // 默认焦点拒绝（§7 误触安全侧）
        EvaluateScrollEnd();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            Countdown.Text = $"{_secondsLeft}s";
            if (_secondsLeft <= 0)
            {
                Gate.Result ??= new Gate.Outcome.TimedOut();
                CloseWindow();
            }
        };
        _timer.Start();
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
            _scrolledToEnd = sv.Extent.Height - sv.Viewport.Height <= 2 ||
                             sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 2;
            ApproveBtn.IsEnabled = _scrolledToEnd;
        }
    }

    private void EvaluateScrollEnd()
    {
        var max = BodyScroll.Extent.Height - BodyScroll.Viewport.Height;
        _scrolledToEnd = max <= 2; // 内容不溢出：无需滚动
        ApproveBtn.IsEnabled = _scrolledToEnd;
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
