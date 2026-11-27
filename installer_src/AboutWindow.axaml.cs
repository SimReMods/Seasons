using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SeasonsInstaller;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Opened += (_, _) => OkButton.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
