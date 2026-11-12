using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;

namespace DynamicNotch.Views;

/// <summary>
/// Small Settings window styled like the notch. Opens and closes with a scale + fade spring, closes on
/// Esc or the close button, and keeps a data-driven tab list with a highlight pill that springs
/// between tabs. Only one instance exists (see <see cref="ShowSingle"/>).
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly SettingsViewModel _vm;
    private readonly Spring _open = new(Motion.WindowOpen, 0);
    private readonly Spring _tab = new(Motion.TabHighlight, 0) { Precision = 50 };
    private readonly Spring _check = new(Motion.Pop, 0);
    private PasswordBox? _keyBox;
    private TextBox? _keyPlain;
    private FrameworkElement? _savedCheck;
    private bool _closing, _revealed;

    /// <summary>Raised with the window handle when it opens / null when it closes (for the keyboard hook).</summary>
    public static event Action<IntPtr>? HandleChanged;

    private SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        _open.ValueChanged += v =>
        {
            Card.Opacity = Math.Clamp(v, 0, 1);
            if (Card.Child is UIElement content) BlurFx.Reveal(content, v, Motion.WindowBlur); // the card keeps its shadow
            double s = Motion.WindowStartScale + (1 - Motion.WindowStartScale) * v;
            CardScale.ScaleX = CardScale.ScaleY = s;
            if (_closing && v <= 0.02) Close();
        };
        _tab.ValueChanged += v => TabHighlightMove.Y = v;
        _check.ValueChanged += v =>
        {
            if (_savedCheck == null) return;
            _savedCheck.Opacity = Math.Clamp(v, 0, 1);
            _savedCheck.RenderTransform = new ScaleTransform(0.4 + 0.6 * v, 0.4 + 0.6 * v);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedIndex))
                _tab.SetTarget(vm.SelectedIndex * Theme.SettingsTabHeight, Motion.TabHighlight);
        };

        Loaded += (_, _) => { _open.Snap(0); _open.SetTarget(1, Motion.WindowOpen); };
        SourceInitialized += (_, _) => HandleChanged?.Invoke(new WindowInteropHelper(this).Handle);
        Closed += (_, _) => { _instance = null; HandleChanged?.Invoke(IntPtr.Zero); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { AnimateClose(); e.Handled = true; } };
    }

    /// <summary>Opens the window, or brings the existing one to the front.</summary>
    public static void ShowSingle(SettingsViewModel vm)
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(vm);
        _instance.Show();
        _instance.Activate();
    }

    private void AnimateClose()
    {
        if (_closing) return;
        _closing = true;
        if (!SpringAnimator.AnimationsEnabled) { Close(); return; }
        _open.SetTarget(0, Motion.FadeOut);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => AnimateClose();

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Drag the window by any empty part of the card.
        if (e.OriginalSource is Border or Grid && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SettingsTab tab) _vm.SelectedTab = tab;
    }

    // ---- API key tab -----------------------------------------------------------------------------
    private void KeyBox_Loaded(object sender, RoutedEventArgs e)
    {
        _keyBox = (PasswordBox)sender;
        _keyBox.Focus();
        _keyBox.PasswordChanged += (_, _) =>
        {
            if (!_revealed && _keyPlain != null && _keyPlain.Text != _keyBox.Password) _keyPlain.Text = _keyBox.Password;
        };
    }

    private void KeyPlain_Loaded(object sender, RoutedEventArgs e)
    {
        _keyPlain = (TextBox)sender;
        _keyPlain.TextChanged += (_, _) =>
        {
            if (_revealed && _keyBox != null && _keyBox.Password != _keyPlain.Text) _keyBox.Password = _keyPlain.Text;
        };
    }

    private void SavedCheck_Loaded(object sender, RoutedEventArgs e) => _savedCheck = (FrameworkElement)sender;

    private string TypedKey => (_revealed ? _keyPlain?.Text : _keyBox?.Password)?.Trim() ?? "";

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        if (_keyBox == null || _keyPlain == null) return;
        _revealed = !_revealed;
        if (_revealed) _keyPlain.Text = _keyBox.Password;
        else _keyBox.Password = _keyPlain.Text;
        _keyPlain.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
        _keyBox.Visibility = _revealed ? Visibility.Collapsed : Visibility.Visible;
        ((Button)sender).Content = _revealed ? Icons.EyeOff : Icons.Eye;
        (_revealed ? (Control)_keyPlain : _keyBox).Focus();
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch { return; }
        if (text.Length == 0) return;
        if (_keyBox != null) _keyBox.Password = text;
        if (_keyPlain != null) _keyPlain.Text = text;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.ApiKey.Save(TypedKey)) return;
        // Never show the full key after saving.
        if (_keyBox != null) _keyBox.Password = "";
        if (_keyPlain != null) _keyPlain.Text = "";
        _check.Snap(0);
        _check.SetTarget(1, Motion.Pop);
    }

    private async void Test_Click(object sender, RoutedEventArgs e) => await _vm.ApiKey.TestAsync(TypedKey);
}
