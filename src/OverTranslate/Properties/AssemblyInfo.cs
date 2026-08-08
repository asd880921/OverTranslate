using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("OverTranslate.Tests")]
// The offline OCR harness, so a measurement can ask the real RealtimeDetectorSize what the app
// would pick rather than keeping a second copy of that rule that could drift out of step with it.
[assembly: InternalsVisibleTo("OcrHarness")]
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
