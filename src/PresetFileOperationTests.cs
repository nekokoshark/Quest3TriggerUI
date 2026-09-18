using System;
using System.IO;

internal static class PresetFileOperationTests
{
    private static string _gameRoot;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                Console.WriteLine(
                    "PRESET FILE OPS EXCEPTION=" + current.GetType().FullName +
                    "; Message=" + current.Message);
            }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        _gameRoot = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
        string relativeFixture =
            "Custom/Atom/Person/Appearance/.__q3_file_tools_test_v3";
        string fixture = Path.Combine(
            _gameRoot, relativeFixture.Replace('/', Path.DirectorySeparatorChar));
        string pluginRoot = Path.Combine(
            Path.Combine(Path.Combine(Path.Combine(_gameRoot, "Custom"), "Atom"), "Person"),
            "Appearance");
        if (!fixture.StartsWith(
                pluginRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new Exception("Fixture path escaped the plugin workspace.");
        if (Directory.Exists(fixture))
            throw new Exception("Preset file-operation fixture already exists.");

        bool newFolder = false;
        bool vapMoved = false;
        bool jpgMoved = false;
        bool hideMoved = false;
        bool restored = false;
        try
        {
            string source = Path.Combine(fixture, "Source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Sample.vap"), "{}");
            File.WriteAllText(Path.Combine(source, "Sample.jpg"), "thumbnail");
            File.WriteAllText(Path.Combine(source, "Sample.vap.hide"), "1");

            string destination = Path.Combine(Path.Combine(fixture, "Target"), "NewFolder");
            Directory.CreateDirectory(destination);
            newFolder = Directory.Exists(destination);

            string[] names = new string[] {
                "Sample.vap", "Sample.jpg", "Sample.vap.hide"
            };
            for (int i = 0; i < names.Length; i++)
            {
                File.Move(
                    Path.Combine(source, names[i]),
                    Path.Combine(destination, names[i]));
            }

            vapMoved = File.Exists(Path.Combine(destination, "Sample.vap"));
            jpgMoved = File.Exists(Path.Combine(destination, "Sample.jpg"));
            hideMoved = File.Exists(Path.Combine(destination, "Sample.vap.hide"));

            for (int i = names.Length - 1; i >= 0; i--)
            {
                File.Move(
                    Path.Combine(destination, names[i]),
                    Path.Combine(source, names[i]));
            }
            restored = File.Exists(Path.Combine(source, "Sample.vap")) &&
                       File.Exists(Path.Combine(source, "Sample.jpg")) &&
                       File.Exists(Path.Combine(source, "Sample.vap.hide"));

            if (!newFolder || !vapMoved || !jpgMoved || !hideMoved || !restored)
                throw new Exception("Preset file-operation verification failed.");
        }
        finally
        {
            if (Directory.Exists(fixture))
                Directory.Delete(fixture, true);
        }

        Console.WriteLine(
            "PRESET FILE OPS RESULT=PASS; NewFolder=" + newFolder +
            "; VapMoved=" + vapMoved + "; JpgMoved=" + jpgMoved +
            "; VapHideMoved=" + hideMoved + "; Restored=" + restored +
            "; FixtureCleaned=" + !Directory.Exists(fixture));
        return 0;
    }

}
