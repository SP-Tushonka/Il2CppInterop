using System.Text.RegularExpressions;
using CppAst;
using Il2CppInterop.StructGenerator.CodeGen;
using Il2CppInterop.StructGenerator.Resources;
using Il2CppInterop.StructGenerator.Utilities;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.StructGenerator;

public record Il2CppStructWrapperGeneratorOptions(
    string HeadersDirectory,
    string OutputDirectory,
    ILogger? Logger,
    string? ExistingDirectory = null
);

// TODO: Instead expose as source generator (might not be viable since clang is platform-dependent)
public static class Il2CppStructWrapperGenerator
{
    private static readonly Dictionary<int, List<VersionSpecificGenerator>> SGenerators = new();
    internal static ILogger? Logger { get; set; }

    /// <summary>
    ///     Write stand-ins for the standard library headers Unity 6.5 includes. The bundled libclang cannot parse the
    ///     ones a newer MSVC ships.
    /// </summary>
    /// <returns>Directory to add to the include path</returns>
    private static string WriteStlStubs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Il2CppInterop.StructGenerator", "stl");
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in Config.StlStubs)
            File.WriteAllText(Path.Combine(directory, name), content);
        return directory;
    }

    private static int GetMetadataVersion(string libil2CppPath)
    {
        var metadataVersion = -1;
        foreach (var versionContainer in Config.MetadataVersionContainers)
        {
            var fullPath = Path.Combine(libil2CppPath, versionContainer);
            if (File.Exists(fullPath))
            {
                var metadataMatch = Regex.Match(File.ReadAllText(fullPath),
                    @"\(s_GlobalMetadataHeader->version == ([0-9]+)\);");

                if (metadataMatch.Success)
                {
                    metadataVersion = int.Parse(metadataMatch.Groups[1].Value);
                    break;
                }
            }
        }

        return metadataVersion;
    }

    private static VersionSpecificGenerator? VisitClass(CppClass @class, int metadataVersion,
        UnityVersion unityVersion, CppClass[] classes)
    {
        if (Config.ClassForcedIgnores.Contains(@class.Name)) return null;
        if (Config.ClassRenames.TryGetValue(@class.Name, out var rename)) @class.Name = rename;
        if (!Config.ClassToGenerator.TryGetValue(@class.Name, out var generatorType)) return null;
        if (!typeof(VersionSpecificGenerator).IsAssignableFrom(generatorType))
            throw new Exception($"{@class.Name} has an invalid generator");

        var existingVersionGeneratorCount =
            SGenerators[metadataVersion].Count(x => x.GetType() == generatorType);
        var existingGenerators =
            SGenerators.Values.SelectMany(x => x).Where(x => x.GetType() == generatorType).ToList();
        var generator = (VersionSpecificGenerator)Activator.CreateInstance(generatorType,
            $"{metadataVersion}_{existingVersionGeneratorCount}", @class,
            new Func<string, CppClass>(dependencyName => { return classes.Single(x => x.Name == dependencyName); }))!;

        foreach (var field in generator.NativeStructGenerator.FieldsToImport.ToList())
        {
            var cppField = generator.NativeStructGenerator.CppClass.Fields.Single(x => x.Name == field.Name);

            CppClass? typeClass = null;
            if (cppField.Type is CppClass)
                typeClass = (CppClass)cppField.Type;
            if (cppField.Type is CppTypedef typeDef && typeDef.ElementType is CppClass)
                typeClass = (CppClass)typeDef.ElementType;
            if (typeClass != null)
            {
                var gen = VisitClass(typeClass, metadataVersion, unityVersion, classes);
                if (gen == null) continue;
                field.FieldType =
                    $"{gen.HandlerGenerator.HandlerClass.Name}.{gen.NativeStructGenerator.NativeStruct.Name}";
                generator.NativeStructGenerator.FieldsToImport.Remove(field);
                if (Config.ClassToGenerator.ContainsKey(gen.NativeStructGenerator.CppClass.Name))
                    generator.AddExtraUsing(
                        $"Il2CppInterop.Runtime.Runtime.VersionSpecific.{gen.NativeStructGenerator.CppClass.Name.Replace("Il2Cpp", string.Empty)}");
            }
        }

        generator.SetupElements();
        foreach (var existingGenerator in existingGenerators)
            if (existingGenerator.NativeStructGenerator.NativeStruct == generator.NativeStructGenerator.NativeStruct)
            {
                existingGenerator.ApplicableVersions.Add(unityVersion);
                return existingGenerator;
            }

        generator.ApplicableVersions.Add(unityVersion);
        SGenerators[metadataVersion].Add(generator);
        return generator;
    }

    public static void Generate(Il2CppStructWrapperGeneratorOptions options)
    {
        Logger = options.Logger;
        if (Directory.Exists(options.OutputDirectory))
            Directory.Delete(options.OutputDirectory, true);
        Directory.CreateDirectory(options.OutputDirectory);
        foreach (var (libil2CppDir, version) in Directory.GetDirectories(options.HeadersDirectory)
                     .Select(x => (x, new UnityVersion(Path.GetFileName(x)))).OrderBy(x => x.Item2))
        {
            var classInternalsPath = Path.Combine(libil2CppDir, "il2cpp-class-internals.h");
            if (!File.Exists(classInternalsPath))
            {
                Logger?.LogWarning(
                    "{} doesn't have il2cpp-class-internals.h - falling back to class-internals.h", version);
                classInternalsPath = Path.Combine(libil2CppDir, "class-internals.h");
                if (!File.Exists(classInternalsPath))
                {
                    Logger?.LogWarning("{} doesn't have class-internals.h", version);
                    continue;
                }
            }

            var objectInternalsPath = Path.Combine(libil2CppDir, "il2cpp-object-internals.h");
            if (!File.Exists(objectInternalsPath))
            {
                Logger?.LogWarning(
                    "{} doesn't have il2cpp-object-internals.h - falling back to object-internals.h", version);
                objectInternalsPath = Path.Combine(libil2CppDir, "object-internals.h");
                if (!File.Exists(objectInternalsPath))
                {
                    Logger?.LogWarning("{} doesn't have object-internals.h", version);
                    continue;
                }
            }

            var metadataVersion = GetMetadataVersion(libil2CppDir);
            if (metadataVersion == -1)
            {
                Logger?.LogWarning("{} has an invalid metadata version", version);
                continue;
            }

            var classInternalsIsTmp = true;
            // Graduated top of my class by the way
            {
                if (!File.Exists($"{classInternalsPath}_backup"))
                {
                    var classInternalsData = File.ReadAllText(classInternalsPath);
                    // From 6000.3 the class shares rgctx_data with genericParameterFlags. Both are pointers.
                    classInternalsData = Regex.Replace(classInternalsData,
                        @"union\s*\{\s*(const Il2CppRGCTXData\* rgctx_data;)[^}]*genericParameterFlags;[^}]*\};", "$1");
                    // I have to do this because the lib I use doesn't recognize these unions, so I have to name them in the most disgusting way imaginable
                    classInternalsData = Regex.Replace(classInternalsData,
                        @"(union.{0,60}?rgctx_data;.*?method(?:Definition|MetadataHandle);.*?});", "$1 runtime_data;",
                        RegexOptions.Singleline);
                    classInternalsData = Regex.Replace(classInternalsData,
                        @"(union.{0,60}?genericMethod;.*?genericContainer(?:Handle)?;.*?});", "$1 generic_data;",
                        RegexOptions.Singleline);

                    File.Move(classInternalsPath, $"{classInternalsPath}_backup");
                    File.WriteAllText(classInternalsPath, classInternalsData);
                }
            }
            if (!SGenerators.ContainsKey(metadataVersion))
                SGenerators[metadataVersion] = new List<VersionSpecificGenerator>();
            var parserOptions = new CppParserOptions
            {
                ParseAsCpp = true,
                AutoSquashTypedef = false,
                ParseMacros = true
            };
            parserOptions.IncludeFolders.Add(WriteStlStubs());
            var compilation = CppParser.ParseFiles(new List<string> { objectInternalsPath, classInternalsPath }, parserOptions);
            foreach (var error in compilation.Diagnostics.Messages.Where(it => it.Type == CppLogMessageType.Error))
                Logger?.LogWarning("{} {}", version, error);
            Logger?.LogInformation("Parsing {}", version);
            var classes = compilation.Classes.ToArray();
            foreach (var @class in classes) VisitClass(@class, metadataVersion, version, classes);
            if (classInternalsIsTmp)
            {
                File.Delete(classInternalsPath);
                File.Move($"{classInternalsPath}_backup", classInternalsPath);
            }
        }

        Logger?.LogInformation("Building version specific classes");
        // In the eyes of god - I am a disappointment
        Dictionary<Type, Dictionary<UnityVersion, VersionSpecificGenerator>> versionToGeneratorLookup = new();
        foreach (var generator in SGenerators.Values.SelectMany(x => x))
        {
            if (!versionToGeneratorLookup.ContainsKey(generator.GetType()))
                versionToGeneratorLookup[generator.GetType()] =
                    new Dictionary<UnityVersion, VersionSpecificGenerator>();

            foreach (var version in generator.ApplicableVersions)
                versionToGeneratorLookup[generator.GetType()][version] = generator;
        }

        foreach (var kvp in versionToGeneratorLookup)
        {
            VersionSpecificGenerator? last = null;
            foreach (var kvp2 in kvp.Value.Where(kvp2 => last is null || last != kvp2.Value))
            {
                kvp2.Value.HandlerGenerator.HandlerClass.Attributes.Add(
                    $"ApplicableToUnityVersionsSince(\"{kvp2.Key.ToStringShort()}\")");
                last = kvp2.Value;
            }
        }

        var generators = SGenerators.Values.SelectMany(x => x).ToList();
        var renames = new Dictionary<string, string>();
        if (options.ExistingDirectory != null)
            ReuseExisting(options.ExistingDirectory, generators, renames);

        foreach (var generator in generators)
        {
            var generatorOutputDir = Path.Combine(options.OutputDirectory, KindOf(generator));
            if (!Directory.Exists(generatorOutputDir))
                Directory.CreateDirectory(generatorOutputDir);
            File.WriteAllText(Path.Combine(generatorOutputDir,
                    $"{generator.NativeStructGenerator.NativeStruct.Name.Replace("Il2Cpp", string.Empty)}.cs"),
                ApplyRenames(BuildFile(generator), renames));
        }

        Logger = null;
    }

    /// <summary>
    ///     Drop every generated layout a checked-in one already matches and point references at the checked-in name.
    ///     A run over a few versions then only adds what is new to them.
    /// </summary>
    /// <param name="existingDirectory">The checked-in VersionSpecific directory</param>
    /// <param name="generators">Generated layouts, matched ones are removed</param>
    /// <param name="renames">Qualified struct names to replace in the remaining output</param>
    private static void ReuseExisting(string existingDirectory, List<VersionSpecificGenerator> generators,
        Dictionary<string, string> renames)
    {
        var existing = Directory.GetDirectories(existingDirectory)
            .ToDictionary(Path.GetFileName, directory => Directory.GetFiles(directory, "*.cs")
                .Select(path => ReadLayout(File.ReadAllText(path)))
                .Where(layout => layout != null)
                .Select(layout => layout!.Value)
                .ToList());

        // A layout only compares equal once the layouts it embeds carry their checked-in names
        var matched = true;
        while (matched)
        {
            matched = false;
            foreach (var generator in generators.ToList())
            {
                var layout = ReadLayout(ApplyRenames(BuildFile(generator), renames));
                if (layout == null || !existing.TryGetValue(KindOf(generator), out var candidates))
                    continue;

                var match = candidates.FirstOrDefault(candidate => candidate.Body == layout.Value.Body);
                if (match.Name == null)
                    continue;

                Logger?.LogInformation("{} matches the checked-in {}", layout.Value.Name, match.Name);
                renames[layout.Value.Name] = match.Name;
                generators.Remove(generator);
                matched = true;
            }
        }
    }

    /// <summary>
    ///     Read the qualified struct name and the field and bitfield lines of a struct handler source
    /// </summary>
    /// <param name="source">Handler source</param>
    /// <returns>Name as Handler.Struct and the trimmed body, or null for a file without a native struct</returns>
    private static (string Name, string Body)? ReadLayout(string source)
    {
        var lines = source.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var handler = lines.Select(line => Regex.Match(line, @"public unsafe class (\w+)")).FirstOrDefault(match => match.Success);
        var start = lines.FindIndex(line => line.TrimStart().StartsWith("internal unsafe struct "));
        if (handler == null || start < 0)
            return null;

        var closing = new string(' ', lines[start].Length - lines[start].TrimStart().Length) + "}";
        var end = lines.FindIndex(start + 1, line => line == closing);
        var body = lines.Skip(start + 2).Take(end - start - 2)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//"));
        return ($"{handler.Groups[1].Value}.{lines[start].Trim()["internal unsafe struct ".Length..].Trim()}", string.Join("\n", body));
    }

    /// <summary>
    ///     Build the source file of a struct handler
    /// </summary>
    /// <param name="generator">Generated layout</param>
    /// <returns>File contents</returns>
    private static string BuildFile(VersionSpecificGenerator generator)
    {
        CodeGenFile file = new()
        {
            Namespace = $"Il2CppInterop.Runtime.Runtime.VersionSpecific.{KindOf(generator)}",
            Usings =
            {
                "System",
                "System.Runtime.InteropServices"
            },
            Elements =
            {
                generator.HandlerGenerator.HandlerClass
            }
        };
        foreach (var extraUsing in generator.ExtraUsings)
            file.Usings.Add(extraUsing);
        return file.Build();
    }

    private static string KindOf(VersionSpecificGenerator generator) =>
        generator.NativeStructGenerator.CppClass.Name.Replace("Il2Cpp", string.Empty);

    private static string ApplyRenames(string source, Dictionary<string, string> renames) =>
        renames.Aggregate(source, (text, rename) => text.Replace(rename.Key, rename.Value));
}
