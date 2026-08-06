using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toki.Llm;

namespace Toki.Views;

/// <summary>
/// First-run onboarding, rendered inside the notch (no separate wizard window). Three short steps -
/// name → privacy-as-pitch → all set - each a height morph via <see cref="App.GrowNotch"/>. Finishing
/// persists the name + the OnboardingComplete flag and lands in Home, where the greeting immediately
/// uses the name they just gave (proof the answer mattered). See <see cref="AppSettings.OnboardingComplete"/>.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
        // Prefill with the name Toki currently holds (so a stale/wrong one - e.g. "Sid" - is shown and
        // corrected here); empty on a fresh install, where the field's prompt asks for it.
        NameBox.Text = AppSettings.Current.UserName ?? "";
        Loaded += (_, _) =>
        {
            if (!App.Headless) Toki.DropIn();
            NameBox.Focus();
            NameBox.SelectAll();
        };
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

    /// <summary>Dev-only: jump straight to a step (0 name, 1 privacy, 2 done) for the --measuretest layout check.</summary>
    public void DebugShowStep(int step)
    {
        if (step <= 0) { Show(StepName); return; }
        if (step == 1) { Show(StepPrivacy); return; }
        DoneTitle.Text = "You're all set, Anirudh.";
        Show(StepDone);
    }
}
