using DubStudio.App.ViewModels;
namespace DubStudio.App.Pages;
public partial class ToolsPage : ContentPage
{
    public ToolsPage(ToolsViewModel vm) { InitializeComponent(); BindingContext = vm; }
}
