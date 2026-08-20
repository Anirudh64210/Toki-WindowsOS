using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toki.Llm;

namespace Toki.Views;

/// <summary>
/// First-run onboarding, rendered inside the notch. Three steps (name, privacy, done), each a height
/// morph via <see cref="App.GrowNotch"/>. Finishing persists the name + OnboardingComplete and lands in Home.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
        // Rounded 1px rim, snapped to whole device pixels (see GlassSurface.Hairline).
        Theme.GlassSurface.CrispRim(NameFieldRim);
        // Prefill with the name Toki currently holds so a stale one can be corrected here.
        NameBox.Text = AppSettings.Current.UserName ?? "";
        Loaded += (_, _) =>
        {
            if (!App.Headless) Toki.DropIn();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>
    /// Back to step one, for a replay: the notch caches one instance of this view, whose flow position
    /// lives only in the three steps' Visibility. Visibility only - no React, no GrowNotch: this runs
    /// from OnStateChanged BEFORE the notch measures the view; morphing here fights the starting transition.
    /// </summary>
    public void ResetToStart()
    {
        StepName.Visibility = Visibility.Visible;
        StepPrivacy.Visibility = Visibility.Collapsed;
        StepDone.Visibility = Visibility.Collapsed;
        // Re-read the name: it may have been changed in Settings since the last run.
        NameBox.Text = AppSettings.Current.UserName ?? "";
    }

    void Show(StackPanel step)
    {
        StepName.Visibility = ReferenceEquals(step, StepName) ? Visibility.Visible : Visibility.Collapsed;
        StepPrivacy.Visibility = ReferenceEquals(step, StepPrivacy) ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = ReferenceEquals(step, StepDone) ? Visibility.Visible : Visibility.Collapsed;
        if (!App.Headless) Toki.React();
        App.GrowNotch();   // morph the notch to the new step's height
    }

    void OnNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnNameNext(sender, e); }
    }

    void OnNameNext(object sender, RoutedEventArgs e) => Show(StepPrivacy);

    void OnPrivacyBack(object sender, RoutedEventArgs e) => Show(StepName);

    void OnPrivacyNext(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        DoneTitle.Text = name.Length > 0 ? $"You're all set, {name}." : "You're all set.";
        Show(StepDone);
    }

    void OnFinish(object sender, RoutedEventArgs e) => App.CompleteOnboarding(NameBox.Text?.Trim() ?? "");

    /// <summary>Dev-only: which step is showing (0 name, 1 privacy, 2 done).</summary>
    public int AuditOnStep() =>
        StepName.Visibility == Visibility.Visible ? 0
        : StepPrivacy.Visibility == Visibility.Visible ? 1 : 2;

    /// <summary>Dev-only: jump straight to a step (0 name, 1 privacy, 2 done) for the --measuretest layout check.</summary>
    public void DebugShowStep(int step)
    {
        if (step <= 0) { Show(StepName); return; }
        if (step == 1) { Show(StepPrivacy); return; }
        DoneTitle.Text = "You're all set, Anirudh.";
        Show(StepDone);
    }
}
