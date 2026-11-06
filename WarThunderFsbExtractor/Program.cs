using System.Text;
using System.Text.Json;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;

internal static class Program
{
    private static readonly string[] PreferredTracks =
    {
        "main_menu",
        "menu_final_10_12",
        "china_main_theme",
        "Air - Track 1",
        "Air_Track_1",
        "Ground - Track 1"
    };

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: WarThunderFsbExtractor <WarThunder folder> <output folder>");
            return 2;
        }

        try
        {
            var installFolder = Path.GetFullPath(args[0]);
            var outputFolder = Path.GetFullPath(args[1]);
            var soundFolder = Path.Combine(installFolder, "sound");
            if (!Directory.Exists(soundFolder))
            {
                Console.Error.WriteLine("War Thunder sound folder was not found.");
                return 3;
            }

            Directory.CreateDirectory(outputFolder);
            var bankFiles = Directory.GetFiles(soundFolder, "aircraft_music*.assets.bank")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (bankFiles.Length == 0)
            {
                Console.Error.WriteLine("No aircraft music banks were found.");
                return 4;
            }

            var signature = string.Join("|", bankFiles.Select(path =>
            {
                var info = new FileInfo(path);
                return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }));
            var manifestPath = Path.Combine(outputFolder, ".warthunder-music.json");
            var extracted = 0;

            foreach (var desiredName in PreferredTracks)
            {
                var outputPath = Path.Combine(
                    outputFolder,
                    "War Thunder - " + SafeFileName(desiredName) + ".ogg");
                if (File.Exists(outputPath)) continue;

                FmodSample match = null;
                string sourceBank = null;
                foreach (var bankPath in bankFiles)
                {
                    var bank = LoadBank(bankPath);
                    match = bank.Samples.FirstOrDefault(sample =>
                        string.Equals(sample.Name, desiredName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        sourceBank = bankPath;
                        break;
                    }
                }

                if (match == null)
                {
                    Console.WriteLine($"Skipped missing track: {desiredName}");
                    continue;
                }

                if (!match.RebuildAsStandardFileFormat(out var data, out var extension) ||
                    data == null ||
                    !string.Equals(extension, "ogg", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Skipped unsupported track: {desiredName}");
                    continue;
                }

                File.WriteAllBytes(outputPath, data);
                extracted++;
                Console.WriteLine($"Extracted {desiredName} from {Path.GetFileName(sourceBank)}");
            }

            var manifest = new
            {
                version = 1,
                signature,
                created_at = DateTimeOffset.UtcNow,
                tracks = PreferredTracks
            };
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
            Console.WriteLine($"Done. New tracks: {extracted}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static FmodSoundBank LoadBank(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var marker = Encoding.ASCII.GetBytes("FSB5");
        var offset = bytes.AsSpan().IndexOf(marker);
        if (offset < 0) throw new InvalidDataException($"FSB5 marker was not found in {path}.");
        return FsbLoader.LoadFsbFromByteArray(bytes.AsSpan(offset).ToArray());
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).Trim();
    }
}
