using System.Windows;
using System.Windows.Controls;
using OverTranslate.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Controls;

public partial class DictionaryResultView : UserControl
{
    public DictionaryResultView()
    {
        InitializeComponent();
    }

    public void Show(DictionaryLookupData? result)
    {
        DataContext = result;
        MessageText.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        Visibility = result?.HasContent == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowMessage(string resourceKey)
    {
        DataContext = null;
        MessageText.SetResourceReference(TextBlock.TextProperty, resourceKey);
        MessageText.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void Clear() => Show(null);
}
