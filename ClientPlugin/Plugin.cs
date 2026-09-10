using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using ClientPlugin.Aurora;
using ClientPlugin.Settings;
using ClientPlugin.Tools;
using HarmonyLib;
using Keen.VRage.Core.Plugins;
using Keen.VRage.Library.Diagnostics;

namespace ClientPlugin;

public class Plugin : IPlugin, IDisposable
{
    public const string Name = "Aurora";
    public static Plugin Instance;

    // The data directory will be provided by a proper SDK in the future.
    // This static function is currently injected by Pulsar, which will
    // remain compatible, even after the SDK's release.
#pragma warning disable CS0649 // This field is assigned by Pulsar
    private static Func<string, string, string> GetConfigPath;
#pragma warning restore CS0649
    public string DataDir { get; private set; } = GetConfigPath(Name, null);

    public Plugin()
    {
        Instance = this;

        // Force-load Config.Current now that DataDir is available.
        _ = Config.Current;

        // IDE/msbuild builds embed the shader into the assembly; Pulsar builds do not, they
        // copy the asset folder and call LoadAssets with it afterwards, which wins.
        ExtractEmbeddedShader();

        Config.Current.PropertyChanged += OnConfigPropertyChanged;

        Log.Default.WriteLine($"[{Name}] Loaded plugin.");
#if DEBUG
        Harmony.DEBUG = true;
#endif
        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Log.Default.WriteLine($"[{Name}] Applied patches");
    }

    public void Dispose()
    {
        // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        AuroraRenderer.Shutdown();
        PlanetOrientationSampler.Clear();
        Instance = null;
    }

    // Called by Pulsar with the folder the plugin's asset files were copied into.
    // The game's shader compiler loads shaders from files, so the .hlsl file has to be there.
    // ReSharper disable once UnusedMember.Global
    public void LoadAssets(string folder)
    {
        try
        {
            var path = Path.Combine(folder, AuroraShaderFiles.ShaderFileName);
            if (File.Exists(path))
                AuroraShaderFiles.Folder = folder;
            else
                Log.Default.WriteLine(LogSeverity.Warning, $"[{Name}] Shader not found in the asset folder: {path}");
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Name}] Failed to load assets from {folder}: {e}");
        }
    }

    // Fallback for msbuild/IDE builds: extract the embedded shader into the plugin's data
    // folder and use that. Pulsar builds have no embedded resource, so this does nothing there.
    private void ExtractEmbeddedShader()
    {
        try
        {
            using var resource = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("ClientPlugin.Shaders." + AuroraShaderFiles.ShaderFileName);
            if (resource == null)
                return;

            var directory = Path.Combine(DataDir, "Shaders");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, AuroraShaderFiles.ShaderFileName);

            using (var file = File.Create(path))
                resource.CopyTo(file);

            AuroraShaderFiles.Folder = directory;
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Name}] Failed to extract the embedded shader: {e}");
        }
    }

    private static void OnConfigPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Config.ColorPreset):
            case nameof(Config.BottomColor):
            case nameof(Config.TopColor):
                AuroraRenderer.MarkRampDirty();
                break;
        }
    }

    // Invoked by Pulsar via reflection when the user clicks the plugin's config button.
    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        try
        {
            var sharedUi = GameAccess.GetSharedUI();
            if (sharedUi == null)
            {
                Log.Default.WriteLine(LogSeverity.Warning, $"[{Name}] SharedUIComponent not available");
                return;
            }

            var generator = new SettingsGenerator();
            var viewModel = new SettingsScreenViewModel(
                generator.Title,
                panel => generator.PopulateContent(panel),
                () => ConfigStorage.Save(Config.Current));

            sharedUi.CreateScreen<SettingsScreen>(viewModel, showCursor: true);
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Name}] OpenConfigDialog failed: {e}");
        }
    }
}
