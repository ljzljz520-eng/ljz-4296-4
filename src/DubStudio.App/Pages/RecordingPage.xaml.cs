using DubStudio.App.ViewModels;

namespace DubStudio.App.Pages;

public partial class RecordingPage : ContentPage
{
    public RecordingPage(RecordingViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
