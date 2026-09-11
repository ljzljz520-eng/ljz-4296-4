using DubStudio.App.ViewModels;
namespace DubStudio.App.Pages;
public partial class RevisionPage : ContentPage
{
    public RevisionPage(RevisionViewModel vm) { InitializeComponent(); BindingContext = vm; }
}
