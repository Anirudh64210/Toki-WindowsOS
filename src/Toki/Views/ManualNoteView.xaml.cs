using System.Windows.Controls;
using System.Windows.Threading;
using Toki.State;

namespace Toki.Views;

/// <summary>Editable manual note (title + freeform body), auto-saved to the store.</summary>
public partial class ManualNoteView : UserControl
{
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool _loading;

    public ManualNoteView()
    {
        InitializeComponent();
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Save(); };
        Loaded += (_, _) => LoadFromCurrent();
        Unloaded += (_, _) => { _saveDebounce.Stop(); Save(); };
    }

    void LoadFromCurrent()
    {
        _loading = true;
        var note = App.CurrentManualNote;
        TitleBox.Text = note?.Title ?? "";
        BodyBox.Text = note?.Body ?? "";
        _loading = false;
        if (string.IsNullOrWhiteSpace(TitleBox.Text) || TitleBox.Text == "Untitled note")
        {
            TitleBox.SelectAll();
            TitleBox.Focus();
        }
    }

    void OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    void Save()
    {
        var note = App.CurrentManualNote;
        if (note is null || note.Id <= 0) return;
        var title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Untitled note" : TitleBox.Text.Trim();
        note.Title = title;
        note.Body = BodyBox.Text;
        try { App.Store.UpdateTitleAndBody(note.Id, title, BodyBox.Text); Toki.LibraryWindow.MarkDirty(); }
        catch (Exception ex) { App.Log($"manual note save failed: {ex.Message}"); }
    }

    void OnBack(object sender, System.Windows.RoutedEventArgs e) { Save(); App.State.TryGo(PillState.Library); }

    void OnClose(object sender, System.Windows.RoutedEventArgs e) { Save(); App.State.TryGo(PillState.Hidden); }
}
