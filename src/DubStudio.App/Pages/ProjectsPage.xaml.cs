using DubStudio.App.ViewModels;
namespace DubStudio.App.Pages;
public partial class ProjectsPage : ContentPage
{
    public ProjectsPage(ProjectsViewModel vm) { InitializeComponent(); BindingContext = vm; }
}
