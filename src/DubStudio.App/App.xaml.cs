using DubStudio.App.Pages;

namespace DubStudio.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        MainPage = new AppShell();
    }
}
