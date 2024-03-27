using System.Configuration;
using System.Data;
using System.Windows;

namespace WpfApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Use the dependency
        public string X = Dependency.Foo.Bar();
    }
}
