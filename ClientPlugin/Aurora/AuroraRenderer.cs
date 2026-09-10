using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.LightingStage;
using Keen.VRage.Render12.Resources.Views;
using Keen.VRage.Render12.SceneSystem.Components;
using Keen.VRage.Render12.Utils;

namespace ClientPlugin.Aurora;

/// <summary>
/// Render-thread side of the effect. Called from the Harmony postfix on the atmosphere
/// pass with the command list and the additive atmosphere buffer that pass draws into;
/// the volume rendering composite then adds that buffer to the light buffer, so the aurora
/// is lit, occluded by clouds and fogged exactly like the atmosphere glow.
///
/// Unlike the SE1 plugin, everything but the planet orientation comes from the renderer
/// itself: it keeps its own list of planets with their atmospheres, camera and sun.
/// </summary>
public static class AuroraRenderer
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AuroraConstants
    {
        public Vector4 CenterInner;     // xyz = planet center in view space, w = inner radius
        public Vector4 PoleOuter;       // xyz = pole axis in view space, w = outer radius
        public Vector4 Tangent1;
        public Vector4 Tangent2;
        public Vector4 BandParams;      // sin(latLo), sin(latHi), feather, unused
        public Vector4 NoiseParams;     // tiling1, tiling2, threshold, push
        public Vector4 ScrollOffsets;   // layer1.xy, layer2.xy
        public Vector4 ColumnScroll;    // column layer offset.xy, tiling.z, unused.w
        public Vector4 ColorIntensity;  // rgb tint, w intensity
        public Vector4 StepParams;      // steps, dither, fade factor (night x distance), height variation
        public Vector4 PatchScroll;     // patch layer1 offset.xy, patch layer2 offset.zw
        public Vector4 PatchParams;     // patch tiling1, patch tiling2, threshold, feather
        public Vector4 GroundParams;    // ground light intensity, curtain contrast exponent, unused x2
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RampConstants
    {
        public Vector4 BottomColor;
        public Vector4 TopColor;
    }

    // The scroll animation runs on wall clock time like in SE1, where it followed the render
    // frame time; nothing about the aurora depends on the simulation.
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static AuroraResources resources;
    private static bool failed;
    private static volatile bool shutdown;
    private static int rampVersion;

    /// <summary>Called (from any thread) when a color setting changes; the ramp is re-baked on next draw.</summary>
    public static void MarkRampDirty()
    {
        Interlocked.Increment(ref rampVersion);
    }

    /// <summary>Stops drawing for good; GPU objects are left to the renderer's teardown.</summary>
    public static void Shutdown()
    {
        shutdown = true;
    }

    /// <summary>Entry point from the render patch. Never throws: on the first error the effect disables itself.</summary>
    public static void Draw(DirectCommandList commandList, IRenderTargetView rtView)
    {
        if (failed || shutdown)
            return;
        try
        {
            DrawInternal(commandList, rtView);
        }
        catch (Exception e)
        {
            failed = true;
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] Aurora renderer failed, disabling for this session: {e}");
        }
    }

    private static void DrawInternal(DirectCommandList commandList, IRenderTargetView rtView)
    {
        var config = Config.Current;
        if (!config.Enabled)
            return;

        if (!TryFindPlanet(out var planet, out float groundRadius, out float atmosphereRadius))
            return;

        var view = CoreSystems.Settings.RenderView;
        Vector3D cameraPosition = view.CameraPosition;
        Vector3D planetCenter = planet.WorldPosition;

        // Distance fade: full brightness out to the start factor, then a linear fade to zero
        // at the end factor. The end factor is clamped so it never sits below the start.
        double fadeStartFactor = config.FadeStartFactor;
        double fadeEndFactor = Math.Max(config.FadeEndFactor, fadeStartFactor);
        float fadeStart = (float)(atmosphereRadius * fadeStartFactor);
        float fadeEnd = (float)(atmosphereRadius * fadeEndFactor);
        float distance = (float)(cameraPosition - planetCenter).Length();
        float fadeFactor = distance <= fadeStart
            ? 1f
            : Clamp((fadeEnd - distance) / Math.Max(fadeEnd - fadeStart, 1f), 0f, 1f);
        if (fadeFactor <= 0f)
            return;

        // The light settings hold the direction the sunlight travels; flip it to point at the sun.
        var dirToSun = Vector3.Normalize(-CoreSystems.Settings.Light.Sun.Normal);
        if (config.NightOnly)
        {
            var up = (Vector3)Vector3D.Normalize(cameraPosition - planetCenter);
            float elevation = Vector3.Dot(up, dirToSun);
            // Fully visible once the sun is 0.15 below the local horizon, gone at 0.05 above it.
            fadeFactor *= Clamp((0.05f - elevation) / 0.20f, 0f, 1f);
            if (fadeFactor <= 0f)
                return;
        }

        // The shader manager and the pipeline states come up asynchronously; draw nothing
        // until they exist. All of this only happens on the first frames of a session.
        if (!AuroraShaderFiles.EnsureRegistered())
            return;
        resources ??= new AuroraResources();
        if (!resources.Poll())
            return;

        BakeTextures(commandList, config);

        // Fit the shell between the ground and the top of the atmosphere.
        float inner = Lerp(groundRadius, atmosphereRadius, config.AltitudeMin);
        float outer = Lerp(groundRadius, atmosphereRadius, config.AltitudeMax);
        if (outer < inner + 100f)
            outer = inner + 100f;

        // World space pole axis (tilted toward the night side) and the tangent basis the noise
        // is projected with. The basis has to be fixed in the world: built from a view space
        // reference it would turn with the camera and drag the whole curtain pattern around
        // the pole on every look. Only then is everything transformed into view space, the
        // frame the shader reconstructs positions in.
        var poleWorld = PlanetOrientationSampler.FindUp(planetCenter);
        var axisWorld = ComputeMagneticAxis(poleWorld, dirToSun, config.MagneticAxisTilt);
        var referenceWorld = Math.Abs(axisWorld.X) < 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f);
        var tangent1World = Vector3.Normalize(Vector3.Cross(axisWorld, referenceWorld));
        var tangent2World = Vector3.Cross(axisWorld, tangent1World);

        MatrixD viewMatrix = view.ViewD;
        var centerView = (Vector3)Vector3D.Transform(planetCenter, in viewMatrix);
        var axisView = Vector3.Normalize((Vector3)Vector3D.TransformNormal(axisWorld, in viewMatrix));
        var tangent1View = Vector3.Normalize((Vector3)Vector3D.TransformNormal(tangent1World, in viewMatrix));
        var tangent2View = Vector3.Normalize((Vector3)Vector3D.TransformNormal(tangent2World, in viewMatrix));

        var constants = FillConstants(config, centerView, axisView, tangent1View, tangent2View, inner, outer, fadeFactor);
        using var constantBuffer = CoreSystems.BindableBuffers.CreateTransientConstantBuffer("auroraConstants", in constants);

        var screenBuffers = CoreSystems.ScreenBuffers;
        commandList.ClearBindings();
        var rootParameters = new RootParameterBuilder(commandList);
        rootParameters.AddCBV(CoreSystems.CommonResources.FrameSettings);
        rootParameters.AddCBV(CoreSystems.CommonResources.JitteredCameraSettings);
        rootParameters.AddCBV(constantBuffer);
        rootParameters.AddSRV(screenBuffers.DepthStencilBuffer.DepthTexture);
        rootParameters.AddSRV(screenBuffers.GBuffer[0]);
        rootParameters.AddSRV(screenBuffers.GBuffer[1]);
        rootParameters.AddSRV(screenBuffers.GBuffer[2]);
        rootParameters.AddSRV(screenBuffers.GBuffer[3]);
        rootParameters.AddSRV(screenBuffers.GBuffer[4]);
        rootParameters.AddSRV(resources.Noise);
        rootParameters.AddSRV(resources.Ramp);
        commandList.SetRTV(rtView);
        resources.Main.Draw(commandList);
        commandList.ClearBindings();
    }

    // Nearest planet WITH an atmosphere: airless planets and moons never get an aurora, so
    // they must not be able to steal the selection either. The renderer's list is already
    // sorted by camera distance.
    private static bool TryFindPlanet(out PlanetEnvironmentEntityComponent planet, out float groundRadius, out float atmosphereRadius)
    {
        planet = null;
        groundRadius = 0f;
        atmosphereRadius = 0f;

        var planets = CoreSystems.PlanetEnvironments.SortedPlanets;
        for (int i = 0; i < planets.Length; i++)
        {
            var candidate = planets[i];
            if (candidate == null || candidate.AtmDefinition == null)
                continue;

            Atmosphere atmosphere;
            try
            {
                atmosphere = CoreSystems.Atmospheres.GetAtmosphere(candidate);
            }
            catch (KeyNotFoundException)
            {
                continue;
            }

            var constants = atmosphere.Constants;
            if (!constants.HasValue)
                continue;

            float ground = constants.Value.RadiusGround;
            float top = constants.Value.MaxWorldSpaceRadius(ground);
            if (top <= ground)
                continue;

            planet = candidate;
            groundRadius = ground;
            atmosphereRadius = top;
            return true;
        }

        return false;
    }

    // The noise is baked once, the ramp whenever the colors change.
    private static void BakeTextures(DirectCommandList commandList, Config config)
    {
        int wantedVersion = Volatile.Read(ref rampVersion);
        if (resources.NoiseBaked && resources.BakedRampVersion == wantedVersion)
            return;

        config.GetGradientColors(out var bottom, out var top);
        var rampConstants = new RampConstants
        {
            BottomColor = new Vector4(bottom, 0f),
            TopColor = new Vector4(top, 0f),
        };
        using var constantBuffer = CoreSystems.BindableBuffers.CreateTransientConstantBuffer("auroraRamp", in rampConstants);

        commandList.ClearBindings();
        var rootParameters = new RootParameterBuilder(commandList);
        rootParameters.AddCBV(constantBuffer);

        if (!resources.NoiseBaked)
        {
            commandList.SetRTV(resources.Noise);
            resources.NoiseBake.Draw(commandList);
            resources.NoiseBaked = true;
        }

        commandList.SetRTV(resources.Ramp);
        resources.RampBake.Draw(commandList);
        resources.BakedRampVersion = wantedVersion;

        commandList.ClearBindings();
    }

    // The magnetic axis is the rotation axis tilted away from the sun, so the aurora band
    // shifts toward the night side where it is actually visible. Tied to the sun rather
    // than the planet frame because a fixed tilt direction would favor the day side just
    // as often; the sun moves slowly enough that the drift is imperceptible.
    private static Vector3 ComputeMagneticAxis(Vector3 pole, Vector3 dirToSun, float tiltDegrees)
    {
        if (tiltDegrees <= 0f)
            return pole;

        var sunPerp = dirToSun - pole * Vector3.Dot(pole, dirToSun);
        float length = sunPerp.Length();
        if (length < 1e-3f)
            return pole;
        sunPerp /= length;

        float tilt = ToRadians(tiltDegrees);
        return pole * (float)Math.Cos(tilt) - sunPerp * (float)Math.Sin(tilt);
    }

    private static AuroraConstants FillConstants(Config config, Vector3 centerView, Vector3 pole, Vector3 tangent1, Vector3 tangent2, float innerRadius, float outerRadius, float fadeFactor)
    {
        float halfWidth = config.LatitudeWidth * 0.5f;
        float latLo = ToRadians(Clamp(config.LatitudeCenter - halfWidth, 0f, 89.5f));
        float latHi = ToRadians(Clamp(config.LatitudeCenter + halfWidth, 1f, 89.9f));
        float sinLo = (float)Math.Sin(latLo);
        float sinHi = (float)Math.Sin(latHi);
        float feather = Math.Max((sinHi - sinLo) * 0.25f, 1e-4f);

        // Noise tiling over the azimuthal projection of the polar cap: how many curtains
        // fit across it. The two layers are close in scale so their difference forms thin
        // veins rather than blobs, and their ratio sets the vein shape, so the density
        // setting scales both together.
        float density = Math.Max(config.PatternDensity, 0.01f);
        float tiling1 = 9f * density;
        float tiling2 = 10.5f * density;

        // The per-column height and offset layer is the same noise at a lower frequency,
        // moving with layer 1. It gets its own tiling and offset rather than the shader
        // rescaling layer 1's: an offset wrapped into [0, 1) is only invisible to a wrapped
        // sampler while it is added at the scale it was wrapped for, and rescaling it makes
        // every wrap teleport the whole curtain structure at once.
        const float columnScale = 0.37f;

        // Scroll rates are in texture units per second, so the speed the pattern moves over
        // the ground is rate / tiling. Scaling them by the same density keeps that ratio
        // fixed, so the density setting changes the feature size without also changing how
        // fast the curtains drift.
        double rate1X = 0.005 * density;
        double rate1Y = 0.002 * density;
        double rate2X = -0.0035 * density;
        double rate2Y = 0.0025 * density;

        double t = Clock.Elapsed.TotalSeconds * config.AnimationSpeed;
        var scroll = new Vector4(
            Frac(t * rate1X), Frac(t * rate1Y),
            Frac(t * rate2X), Frac(t * rate2Y));
        var columnScroll = new Vector4(
            Frac(t * rate1X * columnScale), Frac(t * rate1Y * columnScale),
            tiling1 * columnScale, 0f);

        // Structural visibility patches: macro-scale noise gates which parts of the band
        // are lit at any moment. The tiling is fixed rather than following the curtain
        // density, because these are the largest structures: a few patches across the
        // whole polar cap. The layers counter-scroll so the lit areas morph in place.
        const float patchTiling1 = 1.1f;
        const float patchTiling2 = 0.9f;
        const float patchFeather = 0.12f;
        float coverage = Clamp(config.Coverage, 0f, 1f);
        // Maps coverage 1 to a threshold below virtually all noise values (fully lit)
        // and low coverage to one only the highest peaks exceed (sparse patches).
        float patchThreshold = 0.9f - 0.8f * coverage;
        var patchScroll = new Vector4(
            Frac(t * 0.0016), Frac(t * -0.0007),
            Frac(t * -0.0011), Frac(t * 0.0009));

        return new AuroraConstants
        {
            CenterInner = new Vector4(centerView, innerRadius),
            PoleOuter = new Vector4(pole, outerRadius),
            Tangent1 = new Vector4(tangent1, 0f),
            Tangent2 = new Vector4(tangent2, 0f),
            BandParams = new Vector4(sinLo, sinHi, feather, 0f),
            NoiseParams = new Vector4(tiling1, tiling2, 0.25f, 4f),
            ScrollOffsets = scroll,
            ColumnScroll = columnScroll,
            // The shader saturates its emission to this value, so it sets how far the
            // brightest curtains reach into the game's bloom.
            ColorIntensity = new Vector4(1f, 1f, 1f, config.Intensity),
            StepParams = new Vector4(config.StepCount, 1f, fadeFactor, 0.6f),
            PatchScroll = patchScroll,
            PatchParams = new Vector4(patchTiling1, patchTiling2, patchThreshold, patchFeather),
            GroundParams = new Vector4(config.GroundLight, Math.Max(config.Contrast, 1f), 0f, 0f),
        };
    }

    private static float Frac(double v) => (float)(v - Math.Floor(v));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    private static float ToRadians(float degrees) => degrees * ((float)Math.PI / 180f);
}
