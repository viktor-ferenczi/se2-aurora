using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using HarmonyLib;
using Keen.VRage.Core.Render;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Resources.Shaders;

namespace ClientPlugin.Aurora;

/// <summary>
/// Read-only view of the plugin's shader folder for the game's shader manager. The manager
/// looks shader files up through one file reader per project GUID; the handles it passes in
/// are lower-cased and normalized, so the lookup here ignores case. Only the top level of the
/// folder is exposed, includes of game headers fall through to the engine's own reader.
/// </summary>
internal sealed class AuroraShaderFileReader : IFileReader
{
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);

    public AuroraShaderFileReader(string folder)
    {
        foreach (var path in Directory.GetFiles(folder))
            files[Path.GetFileName(path)] = path;
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        path = path.Replace('\\', '/');
        while (path.StartsWith("./"))
            path = path.Substring(2);

        if (path.Contains('/'))
            return null;

        return files.TryGetValue(path, out var full) ? full : null;
    }

    public bool FileExists(string path) => Resolve(path) != null;

    public bool DirectoryExists(string path)
    {
        var p = (path ?? "").Replace('\\', '/').TrimEnd('/');
        return p == "" || p == ".";
    }

    public IEnumerable<string> EnumerateFiles(string path, bool includeHiddenEntries = false) =>
        DirectoryExists(path) ? files.Keys : Array.Empty<string>();

    public IEnumerable<string> EnumerateDirectories(string path, bool includeHiddenEntries = false) =>
        Array.Empty<string>();

    public FileSystemEntryInfo GetInfo(string path, PathType type)
    {
        var full = Resolve(path) ?? throw new FileNotFoundException("Shader file not found", path);
        return new FileSystemEntryInfo(new FileInfo(full));
    }

    public Stream OpenRead(string file, FileShare share = FileShare.Read, AdvancedFileOptions options = 0)
    {
        var full = Resolve(file) ?? throw new FileNotFoundException("Shader file not found", file);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public bool TryOpenReadSafeHandle(string file, [MaybeNullWhen(false)] out AccessHandle handle,
        FileShare share = FileShare.Read, AdvancedFileOptions options = 0)
    {
        handle = null;
        return false;
    }
}

/// <summary>
/// Registers the plugin's shader folder as a shader project of its own, so the game's shader
/// manager compiles the aurora shader from disk exactly like the engine's shaders, with the
/// shader cache, device reset handling and include resolution that come with it. Includes
/// that do not exist in this project resolve to the engine's shader tree, so the aurora shader
/// can use the game's frame, camera and G-buffer headers.
/// </summary>
public static class AuroraShaderFiles
{
    // The plugin's ID doubles as the shader project GUID; it only has to differ from the engine's.
    public static readonly Guid ProjectGuid = new Guid("FE4B5355-3CA3-41CD-B7DE-4DA5AB1B5228");
    public const string ShaderFileName = "AuroraBorealis.hlsl";

    /// <summary>Folder holding the shader file. Set at plugin init, read on the render thread.</summary>
    public static volatile string Folder;

    private static bool registered;

    public static ShaderFileHandle ShaderHandle => new ShaderFileHandle(ProjectGuid, ShaderFileName);

    /// <summary>
    /// Adds the plugin's file reader to the shader manager. Render thread only. Returns false
    /// while the render systems are not up yet, throws if the plugin has no shader folder.
    /// </summary>
    public static bool EnsureRegistered()
    {
        if (registered)
            return true;

        var folder = Folder;
        if (folder == null)
            throw new InvalidOperationException("No shader folder available, the aurora cannot render");

        var manager = CoreSystems.ShaderFileReaders;
        if (manager == null)
            return false;

        // Private dictionary of the manager, keyed by project GUID. Reached by reflection so the
        // same code works whether or not the reference assembly was publicized.
        var field = AccessTools.Field(typeof(ShaderFileReaderManager), "_fileReadersByGuid")
                    ?? throw new MissingFieldException(nameof(ShaderFileReaderManager), "_fileReadersByGuid");
        var readers = (Dictionary<Guid, IFileReader>)field.GetValue(manager)
                      ?? throw new InvalidOperationException("Shader file reader table is missing");

        readers[ProjectGuid] = new AuroraShaderFileReader(folder);
        registered = true;
        return true;
    }
}
