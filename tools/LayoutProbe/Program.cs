using System.Text;
using System.Windows;

namespace LayoutProbe;

/// <summary>
/// Prints where the capture overlay and the capture toolbar actually put things, on this machine.
/// </summary>
/// <remarks>
/// <para>A console tool rather than a test because neither window can be loaded without an
/// <see cref="Application"/>: both resolve <c>StaticResource</c> at parse time, and the unit test
/// project deliberately keeps process-level state out of itself. So the layout of the two things
/// the user actually looks at has no automated coverage at all, and every question about it has
/// been answered by writing a throwaway WPF executable — twice in one workstream before this was
/// checked in.</para>
///
/// <para>It reports numbers and takes no position on them. Nothing here prints "pass": what a
/// correct figure looks like belongs in the report that quotes it, not in the instrument.</para>
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                LayoutProbe — measures the capture overlay's and toolbar's layout on this machine.

                  LayoutProbe overlay
                      Drives a real OverlayWindow over three fixtures and prints, for each bubble,
                      the box it drew and where the text sits inside it:

                        intent    two boxes of identical geometry and identical text, one drawn
                                  Default and one GroupReflow, so that any difference between the
                                  two lines of output can only have come from the intent.
                        room      the same pair, given empty canvas to their right, which is the
                                  only condition under which "does not expand rightwards" is
                                  visible at all.
                        vertical  a vertical capture, which goes down a different renderer and is
                                  here so that "the horizontal change did not touch it" can be a
                                  measurement rather than a reading of the diff.

                  LayoutProbe toolbar
                      Opens a real ToolbarWindow and prints its size, which mode it opened on, the
                      two segmented controls' halves and pill travel, and the resolved tooltips.

                Both read the application's own settings file, so the toolbar opens on whatever mode
                is stored on this machine.
                """);
            return args.Length == 0 ? 1 : 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // What App.xaml merges at startup. Without them every StaticResource in the two windows
        // fails at parse time, which is the whole reason this is an executable and not a test.
        foreach (var uri in new[]
                 {
                     "pack://application:,,,/OverTranslate;component/Themes/LightTheme.xaml",
                     "pack://application:,,,/OverTranslate;component/Themes/SharedStyles.xaml",
                     "pack://application:,,,/OverTranslate;component/Resources/Strings.zh-Hant.xaml",
                 })
        {
            app.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) });
        }

        try
        {
            switch (args[0])
            {
                case "overlay":
                    OverlayLayout.Report();
                    break;
                case "toolbar":
                    ToolbarLayout.Report();
                    break;
                default:
                    Console.Error.WriteLine($"unknown command '{args[0]}'");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            // Loudly, and with the type: the commonest failure here is a missing resource
            // dictionary, and "object reference not set" three frames deep does not say so.
            Console.Error.WriteLine("probe failed: " + ex);
            return 1;
        }
        finally
        {
            app.Shutdown();
        }

        return 0;
    }
}
