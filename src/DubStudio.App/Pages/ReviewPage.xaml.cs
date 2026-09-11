using DubStudio.App.ViewModels;
namespace DubStudio.App.Pages;
public partial class ReviewPage : ContentPage
{
    public ReviewPage(ReviewViewModel vm) { InitializeComponent(); BindingContext = vm; }
}
