using System.Windows;
namespace PiGui;
public partial class AppearanceSettingsWindow : Window
{
 public bool Dark { get; private set; } public string Family { get; private set; } public double Size { get; private set; } public bool Bold { get; private set; }
 public AppearanceSettingsWindow(bool dark,string family,double size,bool bold){InitializeComponent();Dark=dark;Family=family;Size=size;Bold=bold;ThemeBox.SelectedIndex=dark?0:1;FontBox.SelectedIndex=family=="Cascadia Mono"?1:family=="Microsoft YaHei UI"?2:0;SizeSlider.Value=size;WeightBox.SelectedIndex=bold?1:0;}
 private void Apply_Click(object s,RoutedEventArgs e){Dark=ThemeBox.SelectedIndex==0;Family=((System.Windows.Controls.ComboBoxItem)FontBox.SelectedItem).Content.ToString()!;Size=SizeSlider.Value;Bold=WeightBox.SelectedIndex==1;DialogResult=true;}
}
