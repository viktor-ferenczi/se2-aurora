using Keen.VRage.Core.Render.Data;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Threading;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.PipelineStates;
using Keen.VRage.Render12.Resources.RootSignature;
using Keen.VRage.Render12.Resources.Shaders;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.Utils;
using Vortice.Direct3D12;
using Vortice.DXGI;
using RootParameter = Keen.VRage.Render12.Resources.RootSignature.RootParameter;

namespace ClientPlugin.Aurora;

/// <summary>
/// GPU-side objects of the effect: the three fullscreen quad jobs (aurora pass, noise bake,
/// ramp bake) and the two baked lookup textures. Created on first use from the render thread;
/// the pipeline states compile asynchronously and are polled until they exist.
/// </summary>
internal sealed class AuroraResources
{
    public const int NoiseSize = 512;
    public const int RampSize = 256;

    // Register space of the G-buffer textures (SRV_SPACE_GBUFFER in ResourceSpaces.hlsli).
    private const int GBufferSpace = 21;

    // Space of the frame and camera constant buffers (CBV_SPACE_GLOBALS).
    private const int GlobalsSpace = 1;

    private bool started;
    private Task<ScreenQuadJob>? mainTask;
    private Task<ScreenQuadJob>? noiseTask;
    private Task<ScreenQuadJob>? rampTask;

    public ScreenQuadJob Main { get; private set; }
    public ScreenQuadJob NoiseBake { get; private set; }
    public ScreenQuadJob RampBake { get; private set; }

    public RenderTargetTexture Noise { get; private set; }
    public RenderTargetTexture Ramp { get; private set; }

    public bool NoiseBaked;
    public int BakedRampVersion = -1;

    private void Start()
    {
        // Root parameter order here is the binding order in AuroraRenderer.Draw.
        var mainRootSignature = CoreSystems.RootSignatures.GetRootSignature(
            "AuroraBorealisRS", RootSignatureFlags.None,
            RootParameter.CreateCBV<IConstantBufferView>(0, GlobalsSpace, ShaderVisibility.Pixel),  // GlobalSettings
            RootParameter.CreateCBV<IConstantBufferView>(1, GlobalsSpace, ShaderVisibility.Pixel),  // TrackedCameraSettings
            RootParameter.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.Pixel),             // AuroraConstants
            RootParameter.CreateSRV<ITexture2DView>(0, GBufferSpace, ShaderVisibility.Pixel),       // DepthBuffer
            RootParameter.CreateSRV<ITexture2DView>(1, GBufferSpace, ShaderVisibility.Pixel),       // GBuffer0
            RootParameter.CreateSRV<ITexture2DView>(2, GBufferSpace, ShaderVisibility.Pixel),       // GBuffer1
            RootParameter.CreateSRV<ITexture2DView>(3, GBufferSpace, ShaderVisibility.Pixel),       // GBuffer2
            RootParameter.CreateSRV<ITexture2DView>(4, GBufferSpace, ShaderVisibility.Pixel),       // GBuffer3
            RootParameter.CreateSRV<ITexture2DView>(5, GBufferSpace, ShaderVisibility.Pixel),       // GBuffer4
            RootParameter.CreateSRV<ITexture2DView>(0, 0, ShaderVisibility.Pixel),                  // AuroraNoise
            RootParameter.CreateSRV<ITexture2DView>(1, 0, ShaderVisibility.Pixel));                 // AuroraRamp

        var bakeRootSignature = CoreSystems.RootSignatures.GetRootSignature(
            "AuroraBakeRS", RootSignatureFlags.None,
            RootParameter.CreateCBV<IConstantBufferView>(0, 0, ShaderVisibility.Pixel));            // AuroraRampConstants

        var handle = AuroraShaderFiles.ShaderHandle;

        // Same output format and blend as the atmosphere pass drawing into the same buffer.
        mainTask = ScreenQuadJob.CreateAsync("AuroraBorealis", mainRootSignature,
            new ShaderDescription(handle), new[] { Format.R11G11B10_Float }, BlendState.Additive);

        noiseTask = ScreenQuadJob.CreateAsync("AuroraBakeNoise", bakeRootSignature,
            new ShaderDescription(handle, new[] { new ShaderDefine("BAKE_NOISE") }), new[] { Format.R8G8B8A8_UNorm });

        rampTask = ScreenQuadJob.CreateAsync("AuroraBakeRamp", bakeRootSignature,
            new ShaderDescription(handle, new[] { new ShaderDefine("BAKE_RAMP") }), new[] { Format.R16G16B16A16_Float });

        // Baked by fullscreen draws instead of uploaded from the CPU: render targets are
        // state-tracked by the engine, so they can be bound as textures right after.
        Noise = CoreSystems.BindableTextures.CreateRenderTarget("AuroraNoise", Format.R8G8B8A8_UNorm, new Vector2I(NoiseSize, NoiseSize));
        Ramp = CoreSystems.BindableTextures.CreateRenderTarget("AuroraRamp", Format.R16G16B16A16_Float, new Vector2I(RampSize, 1));

        started = true;
    }

    /// <summary>
    /// Starts the creation on first call, then returns true once every job exists.
    /// Throws if any of the pipeline states failed to compile.
    /// </summary>
    public bool Poll()
    {
        if (!started)
            Start();

        Main ??= Take(ref mainTask);
        NoiseBake ??= Take(ref noiseTask);
        RampBake ??= Take(ref rampTask);

        return Main != null && NoiseBake != null && RampBake != null;
    }

    private static ScreenQuadJob Take(ref Task<ScreenQuadJob>? task)
    {
        if (!task.HasValue)
            return null;

        var awaiter = task.Value.GetAwaiter();
        if (!awaiter.IsCompleted)
            return null;

        task = null;
        return awaiter.GetResult();
    }
}
