using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Text.RegularExpressions;

namespace mysimmap_client;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private static readonly Regex IcaoRegex = new("^[A-Z]{4}$");
    private static readonly Regex CallsignRegex = new("^[A-Z0-9]{2,8}$");

    public MainWindow()
    {
        InitializeComponent();
        _ = LoadConfigAsync();
    }
    
    private async Task LoadConfigAsync()
    {
        var config = await AppConfig.LoadAsync();

        EmailBox.Text = config.Email;
        ApiKeyBox.Text = config.ApiKey;
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        // Already running → stop
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        var email = EmailBox.Text?.Trim() ?? "";
        var host = IpBox.Text?.Trim() ?? "";
        var port = (int)(PortBox.Value ?? 8086);
        var apiKey = ApiKeyBox.Text?.Trim() ?? "";
        
        var callsign = CallsignBox.Text?.Trim().ToUpperInvariant() ?? "";
        var from = FromBox.Text?.Trim().ToUpperInvariant() ?? "";
        var to = ToBox.Text?.Trim().ToUpperInvariant() ?? "";

        CallsignBox.Text = callsign;
        FromBox.Text = from;
        ToBox.Text = to;
        
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText.Text = "Enter your API key from mysimmap.net.";
            return;
        }

        if (!email.Contains('@'))
        {
            StatusText.Text = "Enter a valid email address.";
            return;
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusText.Text = "Enter X-Plane's IP address.";
            return;
        }
        
        if (!CallsignRegex.IsMatch(callsign))
        {
            StatusText.Text = "Enter a callsign (2–8 letters/digits).";
            return;
        }
        if (!IcaoRegex.IsMatch(from) || !IcaoRegex.IsMatch(to))
        {
            StatusText.Text = "Departure and arrival must be 4-letter ICAO codes.";
            return;
        }
        
        await new AppConfig(email, apiKey).SaveAsync();

        _cts = new CancellationTokenSource();
        SetRunning(true);

        var tracker = new PositionTracker(host, port, email, apiKey, callsign, from, to);
        tracker.ShareUrlChanged += url =>
            Dispatcher.UIThread.Post(() => ShareUrlBox.Text = url);
        tracker.StatusChanged += s => Dispatcher.UIThread.Post(() => StatusText.Text = s);
        tracker.PositionUpdated += p => Dispatcher.UIThread.Post(() =>
            PositionText.Text =
                $"Lat  {p.Latitude,11:F5}\n" +
                $"Lon  {p.Longitude,11:F5}\n" +
                $"Alt  {p.AltitudeFt,8:F0} ft\n" +
                $"Hdg  {p.HeadingDeg,8:F0}°\n" +
                $"GS   {p.GroundSpeedKt,8:F0} kt");

        try
        {
            await tracker.RunAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        StartButton.Content = running ? "Stop" : "Start";
        EmailBox.IsEnabled = !running;
        IpBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        ApiKeyBox.IsEnabled = !running;
        CallsignBox.IsEnabled = !running;
        FromBox.IsEnabled = !running;
        ToBox.IsEnabled = !running;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
    
    private async void CopyShareUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ShareUrlBox.Text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(ShareUrlBox.Text);
    }
}