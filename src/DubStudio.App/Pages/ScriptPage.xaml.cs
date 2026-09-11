using DubStudio.App.ViewModels;

namespace DubStudio.App.Pages;

public partial class ScriptPage : ContentPage
{
    public ScriptPage(ScriptViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
