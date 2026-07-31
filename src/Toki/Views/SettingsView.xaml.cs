using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Toki.Llm;
using Toki.State;
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

    Ellipse _statusDot = null!, _statusHalo = null!;
    Storyboard? _pulse;

    public SettingsView()
    {
        InitializeComponent();

        ProviderHost.Content = _providerDrop;
        ModelHost.Content = _modelDrop;
        _providerDrop.Changed += (_, _) => OnProviderChanged();
        _modelDrop.Changed += (_, _) => OnModelChanged();
        BuildStatusDot();

        // Populate NOW, not just on Loaded, so the view is fully filled in before its first frame —
        // no blank flash. Settings uses a fixed size + internal scroll (see PillWindow.AutoHeight),
        // so sizing no longer depends on this timing. Loaded re-runs it in case settings changed.
        Load();
        Loaded += (_, _) => Load();
    }

    void Load()
    {
        _loading = true;
        var s = AppSettings.Current;

        NotesModeSeg.Children.Clear();
        NotesModeSeg.Children.Add(SegButton("On-device", NotesMode.Local, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("Heuristic", NotesMode.HeuristicOnly, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("My API key", NotesMode.ApiKey, s.NotesMode));

        GpuToggle.IsChecked = s.GpuLayers >= 0; // on = auto (uses GPU when it fits safely)
        ShowInRecordingsToggle.IsChecked = s.ShowInRecordings; // on = visible to recorders/screen-share (ghost off)

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

    void OnGpuToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        // On = auto (0): detect VRAM and offload a safe number of layers. Off = force
        // CPU (-1). Takes effect next launch (the model is loaded once at startup).
        s.GpuLayers = GpuToggle.IsChecked == true ? 0 : -1;
        s.Save();
    }

    void OnShowInRecordingsToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        // On = show Toki in captures (ghost/capture-exclusion off); off (default) = hidden from
        // recorders and screen-share. Applies live so the user can flip it and record immediately.
        s.ShowInRecordings = ShowInRecordingsToggle.IsChecked == true;
        s.Save();
        App.SetShowInRecordings(s.ShowInRecordings);
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
            ApiTestStatus.Visibility = Visibility.Collapsed;   // the green status dot is the single confirmation
            UpdateStateText();
        }
        else
        {
            ShowApiStatus(message, ok: false);
        }
        TestApiButton.IsEnabled = true;
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

    /// <summary>The single connection line + its status dot. Three steps: no key → key saved but
    /// unconfirmed → confirmed working (green dot + pulse). This is the ONLY "connected" message.</summary>
    void UpdateStateText()
    {
        var s = AppSettings.Current;
        var hasKey = !string.IsNullOrWhiteSpace(s.ApiKey);
        var connected = hasKey && s.ApiVerified;
        ApiStateText.Text = connected
            ? "Connected — notes now come from the cloud, in seconds."
            : hasKey
                ? "Key saved. Tap Test connection to confirm notes will come from the cloud."
                : "Pick your provider, paste your API key, then choose a model. Your key stays encrypted on this PC.";
        SetConnected(connected);
    }

    // ── connection status dot: a small green dot with a soft outward pulse when connected ──

    void BuildStatusDot()
    {
        _statusHalo = new Ellipse
        {
            Width = 16, Height = 16, Fill = (Brush)FindResource("Green"), Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.6, 0.6),
            IsHitTestVisible = false,
        };
        _statusDot = new Ellipse
        {
            Width = 8, Height = 8, Fill = (Brush)FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        StatusDotHost.Children.Add(_statusHalo);
        StatusDotHost.Children.Add(_statusDot);
    }

    void SetConnected(bool on)
    {
        _statusDot.Fill = (Brush)FindResource(on ? "Green" : "TextDim");
        if (on) StartPulse(); else StopPulse();
    }

    void StartPulse()
    {
        if (_pulse is not null || App.Headless) return;   // no looping animation in the render harness
        var dur = new Duration(TimeSpan.FromMilliseconds(1500));
        // A separate timeline per animated property — Storyboard target/property are attached to
        // the timeline instance, so ScaleX and ScaleY each need their own.
        var pairs = new (AnimationTimeline Anim, string Path)[]
        {
            (new DoubleAnimation(0.6, 2.4, dur) { RepeatBehavior = RepeatBehavior.Forever }, "RenderTransform.ScaleX"),
            (new DoubleAnimation(0.6, 2.4, dur) { RepeatBehavior = RepeatBehavior.Forever }, "RenderTransform.ScaleY"),
            (new DoubleAnimation(0.5, 0.0, dur) { RepeatBehavior = RepeatBehavior.Forever }, "Opacity"),
        };
        _pulse = new Storyboard();
        foreach (var (anim, path) in pairs)
        {
            Storyboard.SetTarget(anim, _statusHalo);
            Storyboard.SetTargetProperty(anim, new PropertyPath(path));
            _pulse.Children.Add(anim);
        }
        _pulse.Begin();
    }

    void StopPulse()
    {
        _pulse?.Stop();
        _pulse = null;
        _statusHalo.Opacity = 0;
        ((ScaleTransform)_statusHalo.RenderTransform).ScaleX = 0.6;
        ((ScaleTransform)_statusHalo.RenderTransform).ScaleY = 0.6;
    }

    /// <summary>Inline test/validation result. Success is plain white text (no green, no tick);
    /// errors are coral; in-progress is dim. Never shows the key.</summary>
    void ShowApiStatus(string message, bool? ok)
    {
        ApiTestStatus.Text = message;
        ApiTestStatus.Foreground = (Brush)FindResource(ok switch
        {
            true => "Text",
            false => "Coral",
            _ => "TextDim",
        });
        ApiTestStatus.Visibility = Visibility.Visible;
    }

    void OnBack(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Home);
}
