using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using YoutubeDownloader.Framework;
using YoutubeDownloader.Utils.Extensions;
using YoutubeDownloader.ViewModels.Dialogs;

namespace YoutubeDownloader.Views.Dialogs;

public partial class AuthSetupView : UserControl<AuthSetupViewModel>
{
    private static readonly Uri HomePageUri = new("https://www.youtube.com");
    private static readonly Uri LoginPageUri = new(
        $"https://accounts.google.com/ServiceLogin?continue={Uri.EscapeDataString(HomePageUri.AbsoluteUri)}"
    );

    public AuthSetupView() => InitializeComponent();

    private void NavigateToLoginPage() => WebBrowser.Source = LoginPageUri;

    private static bool HasLoginCookies(IEnumerable<Cookie> cookies) =>
        cookies.Any(c => c.Name.StartsWith("__SECURE", StringComparison.OrdinalIgnoreCase));

    private async Task TryCaptureCookiesAsync()
    {
        if (WebBrowser.TryGetCookieManager() is not { } cookieManager)
            return;

        var cookies = (await cookieManager.GetCookiesAsync())
            .Where(c => c.IsApplicableFor(HomePageUri))
            .ToArray();

        if (HasLoginCookies(cookies))
            DataContext.Cookies = cookies;
    }

    private async void LogOutButton_OnClick(object sender, RoutedEventArgs args)
    {
        DataContext.Cookies = null;

        if (WebBrowser.TryGetCookieManager() is { } cookieManager)
        {
            foreach (var cookie in await cookieManager.GetCookiesAsync())
                cookieManager.DeleteCookie(cookie);
        }

        NavigateToLoginPage();
    }

    private void WebBrowser_OnLoaded(object sender, RoutedEventArgs args) => NavigateToLoginPage();

    private async void WebBrowser_OnUnloaded(object sender, RoutedEventArgs args)
    {
        try
        {
            await TryCaptureCookiesAsync();
        }
        catch { }
    }

    private void WebBrowser_OnEnvironmentRequested(
        object? sender,
        WebViewEnvironmentRequestedEventArgs args
    )
    {
        args.EnableDevTools = false;

        var isEphemeral = !DataContext.IsAuthPersisted;

        switch (args)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windowsArgs:
                windowsArgs.IsInPrivateModeEnabled = isEphemeral;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs appleArgs:
                appleArgs.NonPersistentDataStore = isEphemeral;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtkArgs:
                gtkArgs.EphemeralDataManager = isEphemeral;
                break;
        }
    }

    private async void WebBrowser_OnNavigationStarted(
        object? sender,
        WebViewNavigationStartingEventArgs args
    )
    {
        // Extract the cookies after being redirected to the home page (i.e., after logging in)
        if (
            args.Request is { } url
            && string.Equals(url.Scheme, HomePageUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(url.Host, HomePageUri.Host, StringComparison.OrdinalIgnoreCase)
        )
        {
            await TryCaptureCookiesAsync();
        }
    }
}
