using System.Windows;

namespace PPObjectSearch.Views;

public partial class EntityPickerWindow : Window
{
    public EntityPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
