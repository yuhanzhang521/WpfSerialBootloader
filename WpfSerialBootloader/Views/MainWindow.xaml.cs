using System.Windows;
using WpfSerialBootloader.ViewModels;

namespace WpfSerialBootloader.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Auto-scroll terminal output
            var vm = DataContext as MainViewModel;
            if (vm != null)
            {
                vm.TerminalOutput.CollectionChanged += (s, e) =>
                {
                    if (TerminalScrollViewer.VerticalOffset == TerminalScrollViewer.ScrollableHeight)
                    {
                        TerminalScrollViewer.ScrollToEnd();
                    }
                };
            }
        }
    }
}
