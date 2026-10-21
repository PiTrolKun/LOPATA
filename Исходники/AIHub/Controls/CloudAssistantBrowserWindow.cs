using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using AIHub.Services;

namespace AIHub.Controls;

internal sealed record CloudPageAnswer(string Id, string Text, bool Copy, string[] Links);
internal sealed record CloudPageState(string Auth, bool Ready, bool Generating, CloudPageAnswer[] Answers,
    string Url, string InputText = "", string SendLabel = "", string Problem = "", bool Interrupted = false);

/// <summary>A single persistent browser session, including the user-visible sign-in surface.</summary>
internal sealed class CloudAssistantBrowserWindow : Window, IDisposable
{
    private readonly MusicCloudProvider _provider;
    private string Bridge => _provider.Id == MusicCloudProviders.AliceId ? "window.__lopataAlice" : "window.__lopataCloud";
    private bool _userClosed;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly WebView2 _web = new();
    private readonly Func<string, string> _l;
    private bool _disposed, _failed, _navigating;
    private CoreWebView2WebErrorStatus? _navigationError;
    private string _script = "";
    public event Action<bool>? ConnectivityChanged;
    internal bool RequiresRestart => _disposed || _failed;

    public CloudAssistantBrowserWindow(Func<string, string> l, MusicCloudProvider provider)
    {
        _l = l; _provider = provider; Title = string.Format(l("Search.BrowserTitle"), provider.Id == MusicCloudProviders.AliceId ? l("Cloud.Alice") : provider.Name); Width = 1100; Height = 800; MinWidth = 600; MinHeight = 400;
        Content = _web; ShowActivated = false; ShowInTaskbar = false;
        Closing += HideOnClose;
    }

    public async Task EnsureAsync(CancellationToken token)
    {
        if (_disposed || _failed) throw new IOException("Music.Poetry.Search.BrowserFailed");
        if (_web.CoreWebView2 is not null) return;
        await ComponentLicenseGate.EnsureAsync(new[] { "viewer.webview2", _provider.LicenseId, "nuget.Microsoft.Web.WebView2" }, token);
        try { _ = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { throw new IOException("Music.Poetry.Search.RuntimeMissing"); }
        using var source = typeof(CloudAssistantBrowserWindow).Assembly.GetManifestResourceStream(_provider.ScriptResource)!;
        using var reader = new StreamReader(source); _script = await reader.ReadToEndAsync(token);
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppDataPaths.BaseDirectory, "Music", _provider.ProfileDirectory));
        token.ThrowIfCancellationRequested();
        Left = -10000; Top = -10000; Opacity = 0; Show();
        try { await _web.EnsureCoreWebView2Async(environment); }
        finally { Hide(); Opacity = 1; WindowStartupLocation = WindowStartupLocation.CenterScreen; Left = 100; Top = 100; }
        if (_web.CoreWebView2 is null) throw new IOException("Music.Poetry.Search.BrowserFailed");
        _web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        _web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme == "https") _web.CoreWebView2.Navigate(uri.AbsoluteUri); };
        _web.CoreWebView2.ProcessFailed += (_, _) => _failed = true;
        _web.CoreWebView2.NavigationStarting += (_, _) => { _navigationError = null; _navigating = true; };
        _web.CoreWebView2.NavigationCompleted += (_, e) => { _navigating = false; if (!e.IsSuccess) _navigationError = e.WebErrorStatus; };
        await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_script);
        _navigating = true; _web.CoreWebView2.Navigate(_provider.HomeUrl);
        await WaitReadyAsync(token, allowLogin: true);
    }

    public async Task ShowBrowserAsync(CancellationToken token)
    {
        await EnsureAsync(token); ShowInTaskbar = true; ShowActivated = true;
        _userClosed = false; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
    }

    public async Task NavigateChatAsync(string url, CancellationToken token)
    {
        await EnsureAsync(token);
        if (IsVisible && _web.Source?.Host is { } host && host != _provider.Host)
            throw new IOException("Music.Poetry.Search.ActionRequired");
        var current = await SnapshotAsync();
        if (current.InputText.Length > 0 || current.Generating)
            throw new IOException("Music.Poetry.Search.Busy");
        var destination = _provider.ValidChatUrl(url) ? url : _provider.HomeUrl;
        if (_web.Source?.AbsoluteUri != destination) { _navigating = true; _web.CoreWebView2.Navigate(destination); }
        await WaitReadyAsync(token, allowLogin: true);
        await RequireSignInAsync(token);
        // A sign-in redirect may land on Home. Restore the saved chat only after manual sign-in.
        if (_provider.ValidChatUrl(url) && _web.Source?.AbsoluteUri != destination)
        {
            _navigating = true; _web.CoreWebView2.Navigate(destination);
            await WaitReadyAsync(token);
        }
    }

    public async Task RequireSignInAsync(CancellationToken token)
    {
        if (!_provider.RequiresSignIn || (await SnapshotAsync()).Ready) return;
        await ShowBrowserAsync(token);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (_userClosed || _disposed) throw new OperationCanceledException(token);
            var state = await SnapshotAsync();
            if (state.Ready && state.Auth == "signed-in") return;
            await Task.Delay(500, token);
        }
        throw new IOException("Music.Poetry.Search.SignInRequired");
    }

    public async Task<CloudPageState> SnapshotAsync()
    {
        if (_failed || _disposed) throw new IOException("Music.Poetry.Search.BrowserFailed");
        if (!NetworkInterface.GetIsNetworkAvailable() || _navigationError == CoreWebView2WebErrorStatus.Disconnected)
        { ConnectivityChanged?.Invoke(false); throw new IOException("Music.Poetry.Search.Offline"); }
        if (_navigating) return new("unknown", false, false, [], "");
        var result = await _web.CoreWebView2.ExecuteScriptAsync($"{Bridge}?.snapshot() ?? null");
        if (result == "null") return new("unknown", false, false, [], "");
        var snapshot = JsonSerializer.Deserialize<CloudPageState>(result, Json) ?? throw new IOException("Music.Poetry.Search.PageChanged");
        if (snapshot.Ready) ConnectivityChanged?.Invoke(true);
        return snapshot;
    }

    private async Task WaitReadyAsync(CancellationToken token, bool allowLogin = false)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var state = await SnapshotAsync();
            if (allowLogin && _provider.RequiresSignIn && state.Auth == "guest") return;
            if (state.Problem.Length > 0) throw new IOException("Music.Poetry.Search.ActionRequired");
            if (state.Ready) return;
            if (_navigationError is not null) throw new IOException("Music.Poetry.Search.SiteUnavailable");
            await Task.Delay(400, token);
        }
        throw new IOException("Music.Poetry.Search.PageChanged");
    }

    public async Task<CloudPageAnswer> AskAsync(string text, Action<string> checkpoint,
        Action<string> progress, CancellationToken token)
    {
        await RequireSignInAsync(token);
        var before = await SnapshotAsync();
        if (!before.Ready || before.Generating) throw new IOException("Music.Poetry.Search.Busy");
        if (before.InputText.Length > 0) throw new IOException("Music.Poetry.Search.ActionRequired");
        if (text.Length > 60000) throw new IOException("Music.Poetry.Search.InputTooLong");
        var encoded = JsonSerializer.Serialize(text);
        if (await _web.CoreWebView2.ExecuteScriptAsync($"{Bridge}.fill({encoded})") != "true")
            throw new IOException("Music.Poetry.Search.InputTooLong");
        var ready = false;
        for (var i = 0; i < 10; i++)
        {
            token.ThrowIfCancellationRequested(); var state = await SnapshotAsync();
            if (state.InputText == text && !System.Text.RegularExpressions.Regex.IsMatch(state.SendLabel, "слуш|listen", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { ready = true; break; }
            await Task.Delay(200, token);
        }
        if (!ready) throw new IOException("Music.Poetry.Search.PageChanged");
        // Persist before the side effect. An unknown outcome is never automatically resent.
        checkpoint("sent"); token.ThrowIfCancellationRequested();
        if (await _web.CoreWebView2.ExecuteScriptAsync($"{Bridge}.send({encoded})") != "true")
            throw new IOException("Music.Poetry.Search.PageChanged");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
        var oldIds = before.Answers.Select(a => a.Id).Where(id => id.Length > 0).ToHashSet();
        string lastText = ""; DateTimeOffset changed = DateTimeOffset.UtcNow; var completed = false;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, token); var state = await SnapshotAsync();
                if (state.Problem.Length > 0 || !state.Ready) throw new IOException("Music.Poetry.Search.ActionRequired");
                var answer = state.Answers.LastOrDefault(a => a.Id.Length > 0 ? !oldIds.Contains(a.Id) : state.Answers.Length > before.Answers.Length);
                if (answer is null || answer.Text.Length == 0)
                {
                    if (state.Interrupted) throw new IOException("Music.Poetry.Search.ResponseInterrupted");
                    continue;
                }
                if (answer.Text.Length > 32000) throw new IOException("Music.Poetry.Search.AnswerTooLong");
                if (answer.Text != lastText) { lastText = answer.Text; changed = DateTimeOffset.UtcNow; progress(lastText); }
                if (state.Interrupted) throw new IOException("Music.Poetry.Search.ResponseInterrupted");
                if (!state.Generating && answer.Copy && DateTimeOffset.UtcNow - changed > TimeSpan.FromSeconds(2)
                    && state.InputText.Length == 0) { completed = true; return answer; }
            }
            throw new IOException("Music.Poetry.Search.Timeout");
        }
        finally { if (!completed) { try { await _web.CoreWebView2.ExecuteScriptAsync($"{Bridge}?.stop()"); } catch { } } }
    }

    public string CurrentChatUrl => _web.Source?.AbsoluteUri is { } url && _provider.ValidChatUrl(url) ? url : "";
    private void HideOnClose(object? sender, CancelEventArgs e) { if (!_disposed) { e.Cancel = true; _userClosed = true; Hide(); } }
    public void Dispose() { if (_disposed) return; _disposed = true; _web.Dispose(); Close(); }
}
