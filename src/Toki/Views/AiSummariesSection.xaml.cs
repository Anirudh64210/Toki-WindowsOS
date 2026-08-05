using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Llm;
using Toki.Transcription;

namespace Toki.Views;

/// <summary>
/// The AI-summaries settings block — mode (On-device / Heuristic / My API key) plus the whole
/// BYO-API-key flow (provider wheel, key, model wheel, Test connection). Moved WHOLE from the notch
/// SettingsView (2026-08-04 consolidation) so it lives in exactly one place: the Library's Settings
/// page. All logic is the original SettingsView logic — same one-path persistence via AppSettings.
/// </summary>
public partial class AiSummariesSection : UserControl
{
    bool _loading;

    // Provider-first flow: choose a provider (which fixes the address), paste the key, pick a model.
    // "Custom" reveals a free-text address for any other OpenAI-compatible service or a local model.
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

    public AiSummariesSection()
    {
        InitializeComponent();
        ProviderHost.Content = _providerDrop;
        ModelHost.Content = _modelDrop;
        _providerDrop.Changed += (_, _) => OnProviderChanged();
        _modelDrop.Changed += (_, _) => OnModelChanged();
        // No model fetch at construction — the LibraryWindow is pre-warmed off-screen at startup, and
        // a hidden settings page must not fire network calls. The page's open (LoadLibrarySettings)
        // does the full Refresh.
        Refresh(refreshModels: false);
    }

    /// <summary>Re-read settings into the controls. <paramref name="refreshModels"/> false = a
    /// recolour-only pass (theme swap) — never fire a network fetch for that.</summary>
    public void Refresh(bool refreshModels = true)
    {
        _loading = true;
        var s = AppSettings.Current;

        NotesModeSeg.Children.Clear();
        NotesModeSeg.Children.Add(SegButton("On-device", NotesMode.Local, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("Heuristic", NotesMode.HeuristicOnly, s.NotesMode));
        NotesModeSeg.Children.Add(SegButton("My API key", NotesMode.ApiKey, s.NotesMode));

        GpuToggle.IsChecked = s.GpuLayers >= 0; // on = auto (uses GPU when it fits safely)

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
        // status line + model dropdown already cover it.
        ModelStatus.Visibility = s.NotesMode != NotesMode.ApiKey ? Visibility.Visible : Visibility.Collapsed;
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
        if (refreshModels && s.NotesMode == NotesMode.ApiKey) RefreshModels();
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

        // A new provider means the old model no longer applies — clear it and reload the list from
        // the new provider. No provider is favoured.
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
    /// cheaper/smaller tier, else the first. No provider favoured.</summary>
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
            Text = label, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11.5,
            FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(on ? "Text" : "TextDim"),
            FontFamily = (FontFamily)FindResource("UiFont"),
        };
        var b = new Border
        {
            Background = on ? (Brush)FindResource("Chip") : Brushes.Transparent,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 7, 0, 7),
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
        Refresh(); // refresh the segmented control + conditional sections
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

    // ── BYO API key ──
    // The key is saved on focus-lost (not per keystroke, which would DPAPI-encrypt and rewrite
    // settings.json on every pasted character). The address is only editable under "Custom".

    void OnApiFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (SaveApiFields()) RefreshModels();
    }

    /// <summary>Persist the key (and the custom address, when shown). Any change clears the
    /// verified flag. Returns false with an inline message when a custom address isn't https.</summary>
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
            FlashTestButton();
            UpdateStateText();
        }
        else
        {
            ShowApiStatus(message, ok: false);
        }
        TestApiButton.IsEnabled = true;
    }

    // Briefly turn the Test button into a green "Connected", then restore it — immediate,
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
        Refresh();                          // On-device selected, the API section collapses
    }

    /// <summary>One quiet status line under the buttons. Green when connected, dim otherwise;
    /// hidden entirely before a key is entered.</summary>
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
    /// in-progress/neutral = dim. Never shows the key.</summary>
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
}
