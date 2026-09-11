using DubStudio.App.ViewModels;
namespace DubStudio.App.Pages;
public partial class DirectorPage : ContentPage
{
    public DirectorPage(DirectorViewModel vm) { InitializeComponent(); BindingContext = vm; }
}
