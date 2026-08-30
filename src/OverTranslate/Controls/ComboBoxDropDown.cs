using System.Windows;

namespace OverTranslate.Controls;

/// <summary>
/// Options for the list a <see cref="System.Windows.Controls.ComboBox"/> drops down.
/// </summary>
public static class ComboBoxDropDown
{
    /// <summary>
    /// Holds the open list to the width of the picker itself, and lets it scroll sideways for
    /// anything that does not fit.
    /// </summary>
    /// <remarks>
    /// A dropdown is free to be wider than its picker by default, and for a list of language names
    /// that is what you want: the picker is sized for the answer, the list has to be readable while
    /// it is being scanned, and the difference is a few characters.
    ///
    /// It is not what you want for a list of things the application did not choose the text of. The
    /// window picker in 即時翻譯 shows whatever titles happen to be open, and a browser tab called
    /// "GOOGLGGE翻譯 - Google 搜尋 和其他 6 個頁面 - 個人 - Microsoft Edge" drags the list out past the
    /// card it belongs to and off the side of the window — one open tab redrawing the page. Held to
    /// the picker's width the list stays where the user pointed at it, and a title too long to fit
    /// is reached by scrolling rather than by making everything else move.
    ///
    /// Nothing but a marker: the ComboBox template reads it in a trigger, because what it changes is
    /// three properties of parts inside that template.
    /// </remarks>
    public static readonly DependencyProperty FitsControlWidthProperty =
        DependencyProperty.RegisterAttached(
            "FitsControlWidth", typeof(bool), typeof(ComboBoxDropDown), new PropertyMetadata(false));

    public static void SetFitsControlWidth(DependencyObject element, bool value) =>
        element.SetValue(FitsControlWidthProperty, value);

    public static bool GetFitsControlWidth(DependencyObject element) =>
        (bool)element.GetValue(FitsControlWidthProperty);
}
