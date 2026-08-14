using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so this name collides
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Controls;

/// <summary>
/// A single-line editor for secrets (API keys): masked by default, with an eye button that
/// reveals the value. WPF's PasswordBox cannot show its own text, so the control keeps a
/// PasswordBox and a TextBox in step and swaps which one is visible.
/// </summary>
public partial class SecretBox : UserControl
{
    // Copying the value into the twin control raises its change event; the flag stops that
    // echo from being reported as a user edit.
    private bool _syncing;
    private bool _revealed;

    public SecretBox()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user edits the value, in either display mode.</summary>
    public event EventHandler? SecretChanged;

    /// <summary>The value being edited. Setting it does not raise <see cref="SecretChanged"/>.</summary>
    public string Secret
    {
        get => MaskedBox.Password;
        set
        {
            var text = value ?? "";
            if (MaskedBox.Password == text && PlainBox.Text == text) return;

            _syncing = true;
            MaskedBox.Password = text;
            PlainBox.Text = text;
            _syncing = false;
        }
    }

    private void MaskedBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        PlainBox.Text = MaskedBox.Password;
        _syncing = false;
        SecretChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PlainBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        MaskedBox.Password = PlainBox.Text;
        _syncing = false;
        SecretChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RevealToggle_Click(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;

        PlainBox.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
        MaskedBox.Visibility = _revealed ? Visibility.Collapsed : Visibility.Visible;

        // Segoe Fluent Icons: Hide (crossed-out eye) while revealed, RedEye while masked.
        RevealIcon.Text = _revealed ? "" : "";
        RevealToggle.ToolTip = LocalizationService.Get(
            _revealed ? "S.Secret.Hide" : "S.Secret.Show");

        // Keep the caret where the user was typing instead of dropping focus on the button.
        if (_revealed)
        {
            PlainBox.Focus();
            PlainBox.CaretIndex = PlainBox.Text.Length;
        }
        else
        {
            MaskedBox.Focus();
        }
    }
}
