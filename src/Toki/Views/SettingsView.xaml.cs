using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Toki.Llm;
using Toki.State;
using Toki.Theme;
using Toki.Transcription;

namespace Toki.Views;

public partial class SettingsView : UserControl
{
    bool _loading;

    // The BYO-key flow is provider-first: choose a provider (which fixes the address), paste the
    // key, then pick a model. Provider + model are compact search-dropdowns. "Custom" reveals a
    // free-text address for any other OpenAI-compatible service or a local model.
    static readonly (string Name, string? Base)[] KnownProviders =
    {
        ("Claude (Anthropic)", "https://api.anthropic.com"),
        ("OpenAI",             "https://api.openai.com"),
        ("Groq",               "https://api.groq.com/openai/v1"),
        ("OpenRouter",         "https://openrouter.ai/api/v1"),
        ("Custom",             null),
    };
    const string CustomName = "Custom";

    readonly SearchableDropdown _providerDrop = new();
    readonly SearchableDropdown _modelDrop = new();
    List<string> _models = new();          // last fetched model list for the current provider
    CancellationTokenSource? _modelsCts;   // cancels an in-flight model fetch when things change

    public SettingsView()
    {
        InitializeComponent();

        ProviderHost.Content = _providerDrop;
        ModelHost.Content = _modelDrop;
        _providerDrop.Changed += (_, _) => OnProviderChanged();
        _modelDrop.Changed += (_, _) => OnModelChanged();

        // Populate NOW, not just on Loaded, so the view is fully filled in before its first frame —
        // no blank flash. Settings uses a fixed size + internal scroll (see PillWindow.AutoHeight),
        // so sizing no longer depends on this timing. Loaded re-runs it in case settings changed.
        Load();
        Loaded += (_, _) => Load();
        UpdateThemeIcon();
        UpdateRecordingIcon();
    }

    void Load()
    {
        _loading = true;
        var s = AppSettings.Current;

        RefreshGoogle();

        NotesModeSeg.Children.Clear();
        NotesModeSeg.Children.Add(SegButton("On-device", NotesMode.Local, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("Heuristic", NotesMode.HeuristicOnly, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("My API key", NotesMode.ApiKey, s.NotesMode));

        GpuToggle.IsChecked = s.GpuLayers >= 0; // on = auto (uses GPU when it fits safely)
        GlassToggle.IsChecked = s.GlassyNotch;

        LocalOptions.Visibility = s.NotesMode == NotesMode.Local ? Visibility.Visible : Visibility.Collapsed;
        ApiOptions.Visibility = s.NotesMode == NotesMode.ApiKey ? Visibility.Visible : Visibility.Collapsed;

        if (s.NotesMode == NotesMode.ApiKey)
        {
            var providerName = ProviderNameFor(s.ApiBaseUrl);
            _providerDrop.SetItems(KnownProviders.Select(p => p.Name), providerName);
            CustomEndpointRow.Visibility = providerName == CustomName ? Visibility.Visible : Visibility.Collapsed;
            ApiBaseUrlBox.Text = s.ApiBaseUrl;
            ApiKeyBox.Password = s.ApiKey ?? "";

            var seed = _models.Count > 0 ? _models
                     : string.IsNullOrWhiteSpace(s.ApiModel) ? new List<string>()
                     : new List<string> { s.ApiModel };
            _modelDrop.SetItems(seed, s.ApiModel, placeholder: "—");
            ApiTestStatus.Visibility = Visibility.Collapsed;   // stale test result shouldn't linger across opens
            UpdateStateText();
        }

        // Model status is informational for the on-device / heuristic paths; in API-key mode the
        // status dot + model dropdown already cover it, so hide it (no duplicate connection lines).
        var showModelStatus = s.NotesMode != NotesMode.ApiKey;
        ModelStatusTitle.Visibility = ModelStatus.Visibility = showModelStatus ? Visibility.Visible : Visibility.Collapsed;
        ModelStatus.Text = s.NotesMode switch
        {
            NotesMode.HeuristicOnly => "Using fast built-in extraction (no AI model).",
            NotesMode.ApiKey => "",
            _ => ModelManager.QwenInstalled
                ? "On-device model installed and ready (Qwen3-4B)."
                : "On-device model will download (~2.5 GB) the first time notes are written.",
        };
        _loading = false;

        // Once the view is set up, load this provider's live model list if we have a key.
        if (s.NotesMode == NotesMode.ApiKey) RefreshModels();
    }

    // ── provider / model wheels ──

    static string ProviderNameFor(string? baseUrl)
    {
        var target = ApiLlm.TryBuildEndpoint(baseUrl)?.ToString();
        if (target is not null)
            foreach (var (name, b) in KnownProviders)
                if (b is not null && ApiLlm.TryBuildEndpoint(b)?.ToString() == target)
                    return name;
        return CustomName;
    }

    static string? BaseFor(string name) => KnownProviders.FirstOrDefault(p => p.Name == name).Base;

    void OnProviderChanged()
    {
        if (_loading) return;
        var s = AppSettings.Current;
        var name = _providerDrop.Selected ?? CustomName;
        var isCustom = name == CustomName;
        CustomEndpointRow.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;

        if (!isCustom)
        {
            var b = BaseFor(name);
            if (b is not null) { s.ApiBaseUrl = b; ApiBaseUrlBox.Text = b; }
        }

        // A new provider means the old model no longer applies — clear it (so it's never "stuck"
        // on the previous provider's pick) and reload the list from the new provider. RefreshModels
        // fills in a default from the fetched list, or the wheel stays empty until the right key
        // is pasted. No provider is favoured.
        s.ApiModel = "";
        s.ApiVerified = false;
        s.Save();
        _models = new();
        _modelDrop.SetItems(Array.Empty<string>(), null, placeholder: "—");
        UpdateStateText();
        RefreshModels();
    }

    void OnModelChanged()
    {
        if (_loading) return;
        var s = AppSettings.Current;
        var sel = _modelDrop.Selected;
        if (string.IsNullOrWhiteSpace(sel) || sel == s.ApiModel) return;
        s.ApiModel = sel;
        s.ApiVerified = false;   // a different model is unverified until re-tested
        s.Save();
        UpdateStateText();
    }

    /// <summary>Fetch the provider's live model list and fill the wheel. Cheap, cancellable,
    /// never blocks. Doubles as a key check — if it loads, the key + address reach the provider.</summary>
    async void RefreshModels()
    {
        if (_loading || App.Headless) return;   // never fetch or save from the offscreen render harness
        var s = AppSettings.Current;
        if (s.NotesMode != NotesMode.ApiKey) return;

        var key = s.ApiKey;
        var baseUrl = s.ApiBaseUrl;
        if (string.IsNullOrWhiteSpace(key)) { SetModelHint("Paste your API key above to load this provider’s models."); return; }
        if (ApiLlm.TryBuildEndpoint(baseUrl) is null) { SetModelHint("Enter a valid address above first."); return; }

        _modelsCts?.Cancel();
        var cts = _modelsCts = new CancellationTokenSource();
        SetModelHint("Loading models…");

        var (ok, message, models) = await ApiLlm.ListModelsAsync(key, baseUrl, cts.Token);
        if (cts.IsCancellationRequested) return;

        if (ok)
        {
            _models = models.ToList();
            var current = s.ApiModel;
            if (!_models.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)))
            {
                // Saved model belongs to a different provider (or none) — pick a sensible default.
                current = PickDefault(_models);
                s.ApiModel = current;
                s.ApiVerified = false;
                s.Save();
            }
            _modelDrop.SetItems(_models, current);
            SetModelHint($"{_models.Count} model{(_models.Count == 1 ? "" : "s")} — flick to choose.");
        }
        else
        {
            SetModelHint(message);   // leave the current model selectable so it still works
        }
    }

    /// <summary>Pick a reasonable default from a fetched list — a provider-neutral lean toward the
    /// cheaper/smaller tier (markers common across providers), else the first. No provider favoured.</summary>
    static string PickDefault(IReadOnlyList<string> models)
    {
        string[] cheap = { "mini", "instant", "flash", "small", "lite", "nano", "haiku", "8b" };
        foreach (var p in cheap)
            if (models.FirstOrDefault(m => m.Contains(p, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit;
        return models[0];
    }

    void SetModelHint(string text)
    {
        ModelHint.Text = text;
        ModelHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── segmented mode control ──

    Border SegButton(string label, NotesMode mode, NotesMode current)
    {
        var on = mode == current;
        var text = new TextBlock
        {
            Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11,
            FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(on ? "Text" : "TextDim"),
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        var b = new Border
        {
            Background = on ? (Brush)FindResource("Chip") : Brushes.Transparent,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 6, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand, Child = text,
        };
        b.MouseLeftButtonUp += (_, _) => SetMode(mode);
        return b;
    }

    void SetMode(NotesMode mode)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.NotesMode = mode;
        s.Save();
        Load(); // refresh the segmented control + conditional sections
    }

    // ── appearance (light/dark): one quiet header toggle ──
    // Deliberately NOT coupled to Load() — flipping the theme must never re-run the heavy settings
    // reload (model fetches etc.), which is what could leave it stuck. It just recolours + re-icons.

    void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        var s = AppSettings.Current;
        s.Theme = next == AppTheme.Light ? "light" : "dark";
        s.Save();
        ThemeManager.Apply(next);   // recolours the whole app live (DynamicResource re-resolves)
        Load();                     // rebuild this view's code-built bits (segmented control) in the new palette
        UpdateThemeIcon();
    }

    // Show the glyph for the mode you'd switch TO: a sun in dark mode (→ light), a moon in light (→ dark).
    void UpdateThemeIcon()
    {
        var dark = ThemeManager.Current == AppTheme.Dark;
        ThemeToggle.Content = dark ? "" : "";   // Fluent: Brightness (sun, go light) / QuietHours (moon, go dark)
        ThemeToggle.ToolTip = dark ? "Switch to light" : "Switch to dark";
    }

    void OnGlassToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.SetGlassyNotch(GlassToggle.IsChecked == true);
    }

    void OnGpuToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        // On = auto (0): detect VRAM and offload a safe number of layers. Off = force
        // CPU (-1). Takes effect next launch (the model is loaded once at startup).
        s.GpuLayers = GpuToggle.IsChecked == true ? 0 : -1;
        s.Save();
    }

    // ── screen-recording visibility (ghost): one header icon, shown once for the whole Settings ──
    // (Previously a section that appeared under every notes-mode page — the owner's "three times" bug.)

    void OnRecordingToggle(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        s.ShowInRecordings = !s.ShowInRecordings;   // on = visible to recorders (ghost off); off = hidden (default)
        s.Save();
        App.SetShowInRecordings(s.ShowInRecordings);   // applies live, no restart
        UpdateRecordingIcon();
    }

    // A video camera, CROSSED OUT when Toki is hidden from recordings (ghost on, the default) and
    // plain when it's visible — an immediate, obvious on/off, no colour change needed.
    void UpdateRecordingIcon()
    {
        var hidden = !AppSettings.Current.ShowInRecordings;   // ghost on = not shown in recordings

        var glyph = new TextBlock
        {
            Text = ((char)0xE714).ToString(),   // Fluent: Video
            FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var strike = new System.Windows.Shapes.Line
        {
            X1 = 4, Y1 = 19, X2 = 20, Y2 = 5, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Visibility = hidden ? Visibility.Visible : Visibility.Collapsed,
        };
        strike.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text");
        var box = new Grid { Width = 24, Height = 24 };
        box.Children.Add(glyph);
        box.Children.Add(strike);
        RecordToggle.Content = box;
        RecordToggle.ToolTip = hidden
            ? "Hidden from screen recordings & sharing — tap to show Toki (to record a demo)"
            : "VISIBLE in screen recordings & sharing — tap to hide Toki again";
    }

    // ── BYO API key ──
    // The key is saved on focus-lost (not per keystroke, which would DPAPI-encrypt and rewrite
    // settings.json on every pasted character). The address is only editable under "Custom".

    void OnApiFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (SaveApiFields()) RefreshModels();
    }

    /// <summary>Persist the key (and the custom address, when shown). Any change clears the
    /// verified flag so the state card drops back to "confirm it's working". Returns false with
    /// an inline message when a custom address isn't https.</summary>
    bool SaveApiFields()
    {
        var s = AppSettings.Current;
        var key = ApiKeyBox.Password.Trim();
        var baseUrl = CustomEndpointRow.Visibility == Visibility.Visible ? ApiBaseUrlBox.Text.Trim() : s.ApiBaseUrl;

        if (baseUrl.Length > 0 && ApiLlm.TryBuildEndpoint(baseUrl) is null)
        {
            ShowApiStatus("The address must be https:// (or http:// for a local proxy).", ok: false);
            return false;
        }

        var changed = key != (s.ApiKey ?? "") || (baseUrl.Length > 0 && baseUrl != s.ApiBaseUrl);
        s.ApiKey = key;                                    // setter trims + DPAPI-encrypts
        if (baseUrl.Length > 0) s.ApiBaseUrl = baseUrl;
        if (changed) s.ApiVerified = false;
        s.Save();
        if (changed) UpdateStateText();
        return true;
    }

    async void OnTestApi(object sender, RoutedEventArgs e)
    {
        if (_loading || !SaveApiFields()) return;
        var s = AppSettings.Current;
        var llm = new ApiLlm(s.ApiKey, s.ApiModel, s.ApiBaseUrl);

        TestApiButton.IsEnabled = false;
        ShowApiStatus("Testing…", ok: null);
        // Off the UI thread (never block it — the async stack can deadlock if we do).
        var (ok, message) = await Task.Run(() => llm.TestAsync());
        if (ok)
        {
            s.ApiVerified = true;
            s.Save();
            // Immediate feedback on the button you pressed, plus the green status line just below it.
            FlashTestButton();
            UpdateStateText();
        }
        else
        {
            ShowApiStatus(message, ok: false);
        }
        TestApiButton.IsEnabled = true;
    }

    // Briefly turn the Test button into a green "Connected ✓", then restore it — immediate,
    // unmissable feedback at the point of the click.
    void FlashTestButton()
    {
        TestApiButton.Content = "Connected";
        TestApiButton.Foreground = (Brush)FindResource("Green");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            TestApiButton.Content = "Test connection";
            TestApiButton.ClearValue(ForegroundProperty);   // back to the PillButton default
        };
        t.Start();
    }

    // One-tap opt-out: wipe the key from disk and go back to the free on-device model.
    void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.ApiKey = null;                    // clears the DPAPI blob (ApiKeyProtected → null) from settings.json
        s.ApiVerified = false;              // nothing to verify once the key is gone
        s.NotesMode = NotesMode.Local;      // back to the on-device model
        s.Save();
        ApiKeyBox.Password = "";
        Load();                             // refresh: On-device selected, the API section collapses
    }

    /// <summary>One quiet status line under the buttons (no dot, no card). Green when connected, dim
    /// otherwise; hidden entirely before a key is entered (the field placeholders guide instead).</summary>
    void UpdateStateText()
    {
        var s = AppSettings.Current;
        var hasKey = !string.IsNullOrWhiteSpace(s.ApiKey);
        var connected = hasKey && s.ApiVerified;
        if (connected) ShowApiStatus("Connected — notes now come from the cloud.", ok: true, connected: true);
        else if (hasKey) ShowApiStatus("Key saved — tap Test connection to confirm.", ok: null);
        else { ApiTestStatus.Text = ""; ApiTestStatus.Visibility = Visibility.Collapsed; }
    }

    /// <summary>The single status line under the buttons. Connected = green; errors = coral;
    /// in-progress/neutral = dim; a plain ok = normal text. Never shows the key.</summary>
    void ShowApiStatus(string message, bool? ok, bool connected = false)
    {
        ApiTestStatus.Text = message;
        ApiTestStatus.Foreground = (Brush)FindResource(connected ? "Green" : ok switch
        {
            true => "Text",
            false => "Coral",
            _ => "TextDim",
        });
        ApiTestStatus.Visibility = Visibility.Visible;
    }

    // ── Google Calendar ──

    void RefreshGoogle()
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        if (svc.IsConnected)
        {
            GoogleStatus.Text = svc.AccountEmail is { Length: > 0 } m
                ? $"Connected as {m}." : "Connected to Google Calendar.";
            GoogleConnectBtn.Content = "Reconnect";
            GoogleDisconnectBtn.Visibility = Visibility.Visible;
        }
        else
        {
            GoogleStatus.Text = svc.IsConfigured
                ? "Not connected. Sign in to see upcoming meetings and let Toki prep you."
                : "Google sign-in isn’t set up in this build yet.";
            GoogleConnectBtn.Content = "Connect Google";
            GoogleConnectBtn.IsEnabled = svc.IsConfigured;
            GoogleDisconnectBtn.Visibility = Visibility.Collapsed;
        }
    }

    async void OnGoogleConnect(object sender, RoutedEventArgs e)
    {
        var svc = Toki.Calendar.CalendarService.Instance;
        if (!svc.IsConfigured) { GoogleStatus.Text = "Google sign-in isn’t set up in this build yet."; return; }
        GoogleConnectBtn.IsEnabled = false;
        GoogleStatus.Text = "Opening your browser — sign in and click Allow…";
        try
        {
            await svc.ConnectAsync();
        }
        catch (Toki.Calendar.GoogleAuthException ex) { GoogleStatus.Text = ex.Message; }
        catch { GoogleStatus.Text = "Couldn’t connect to Google."; }
        finally { GoogleConnectBtn.IsEnabled = true; RefreshGoogle(); }
    }

    async void OnGoogleDisconnect(object sender, RoutedEventArgs e)
    {
        GoogleDisconnectBtn.IsEnabled = false;
        try { await Toki.Calendar.CalendarService.Instance.DisconnectAsync(); }
        finally { GoogleDisconnectBtn.IsEnabled = true; RefreshGoogle(); }
    }

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);

    void OnOpenSource(object sender, MouseButtonEventArgs e) => App.State.TryGo(PillState.About);
}
