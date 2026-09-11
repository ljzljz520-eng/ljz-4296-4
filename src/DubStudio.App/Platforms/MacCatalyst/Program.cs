using ObjCRuntime;
using UIKit;

namespace DubStudio.App.MacCatalyst;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--connection-mode")
            UIApplication.Main(args, null, typeof(AppDelegate));
        else
            UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
