// Aurora Borealis: volumetric raymarch through a spherical shell segment over planet poles.
// Technique based on Roy Theunissen's "difference clouds" aurora, adapted from an axis-aligned
// box volume to a spherical shell around a planet. Ported from the Space Engineers 1 plugin.
//
// Compiled by the game's shader manager (entry point __PixelShader, DXC shader model 6.1) and
// drawn as a fullscreen quad right after the atmosphere pass, into the additive atmosphere
// buffer that the volume rendering composite adds to the light buffer. Everything is in view
// space: the camera sits at the origin and looks down -Z, matching the atmosphere shader.
//
// The same file bakes the two lookup textures the effect samples, selected by the macros below:
//   BAKE_NOISE  tileable RGBA fractal noise (curtain layers, column height and offset)
//   BAKE_RAMP   256x1 vertical color and alpha gradient
// @define SHADER_ASSERTS_ENABLED
// @define BAKE_NOISE
// @define BAKE_RAMP

#include <ScreenSpaceBase.hlsli>

#if defined(BAKE_NOISE)

// Tileable multi-octave gradient (Perlin) noise. The lattice gradients repeat with the
// given period, so the result tiles seamlessly. Same lattice hash and blending as the
// CPU generator of the SE1 plugin, so the baked texture matches it.

#define NOISE_SIZE 512.0

float NoiseFade(float t)
{
    return t * t * t * (t * (t * 6 - 15) + 10);
}

float GradDot(int ix, int iy, float dx, float dy, int period, uint seed)
{
    // Wrap the lattice so the noise tiles with the period.
    ix = ((ix % period) + period) % period;
    iy = ((iy % period) + period) % period;

    uint h = (uint)ix * 374761393u + (uint)iy * 668265263u + seed * 1274126177u;
    h = (h ^ (h >> 13)) * 1274126177u;
    h ^= h >> 16;

    float angle = (float)h * (6.28318530718 / 4294967295.0);
    return dx * cos(angle) + dy * sin(angle);
}

float Gradient2D(float x, float y, int period, uint seed)
{
    int x0 = (int)floor(x);
    int y0 = (int)floor(y);
    float tx = x - x0;
    float ty = y - y0;

    float d00 = GradDot(x0, y0, tx, ty, period, seed);
    float d10 = GradDot(x0 + 1, y0, tx - 1, ty, period, seed);
    float d01 = GradDot(x0, y0 + 1, tx, ty - 1, period, seed);
    float d11 = GradDot(x0 + 1, y0 + 1, tx - 1, ty - 1, period, seed);

    float sx = NoiseFade(tx);
    float sy = NoiseFade(ty);
    float a = d00 + sx * (d10 - d00);
    float b = d01 + sx * (d11 - d01);
    return a + sy * (b - a);
}

// Fractal sum normalized to 0..1 with the fixed min/max the sum reaches over the whole
// texture (measured on the CPU generator), which the SE1 plugin computed per texture.
float FractalNoise(float2 p, int basePeriod, int octaves, uint seed, float lo, float hi)
{
    float sum = 0;
    float amplitude = 1;
    int period = basePeriod;
    [loop]
    for (int octave = 0; octave < octaves; octave++)
    {
        sum += amplitude * Gradient2D(p.x * period, p.y * period, period, seed + (uint)octave * 7919u);
        amplitude *= 0.5;
        period *= 2;
    }
    return saturate((sum - lo) / (hi - lo));
}

float4 __PixelShader(VertexOut pin) : SV_Target
{
    float2 p = floor(pin.PosScreen.xy) / NOISE_SIZE;
    // R/G: the two difference-cloud layers; B/A: lower-frequency curtain height and vertical offset.
    return float4(
        FractalNoise(p, 8, 4, 12345u, -0.7577, 0.7518),
        FractalNoise(p, 8, 4, 54321u, -0.8024, 0.8772),
        FractalNoise(p, 4, 3, 98765u, -0.6851, 0.6742),
        FractalNoise(p, 4, 3, 56789u, -0.7970, 0.5782));
}

#elif defined(BAKE_RAMP)

#define RAMP_SIZE 256.0
// Peak of rise * exp(-3h) over the ramp, so the alpha normalizes to 1 at the brightest height.
#define RAMP_PEAK 0.743868

cbuffer AuroraRampConstants : register(b0)
{
    float4 BottomColor;   // rgb = color of the bright lower edge
    float4 TopColor;      // rgb = color of the fading upper tail
};

float4 __PixelShader(VertexOut pin) : SV_Target
{
    float h = floor(pin.PosScreen.x) / (RAMP_SIZE - 1);

    // Height falloff: sharp bright lower edge, long fading upper tail (normalized to peak 1).
    float rise = smoothstep(0, 1, saturate((h - 0.02) / 0.08));
    float alpha = rise * exp(-3.0 * h) / RAMP_PEAK;

    float blend = smoothstep(0, 1, saturate((h - 0.05) / 0.80));
    return float4(lerp(BottomColor.rgb, TopColor.rgb, blend), alpha);
}

#else

#define USES_DEPTH_BUFFER
#include <Common/Resources/SharedResources.hlsli>
#include <Common/Frame.hlsli>
#include <Common/AllSamplers.hlsli>
#include <Common/Math/Projection.hlsli>
#include <Common/GBufferPacking.hlsli>

// Same registers as the game's own G-buffer readers (Lighting/GBuffer.hlsli); the depth
// buffer comes from SharedResources at t0 of the same space.
Texture2D GBuffer0 : register(t1, SRV_SPACE_GBUFFER);
Texture2D GBuffer1 : register(t2, SRV_SPACE_GBUFFER);
Texture2D GBuffer2 : register(t3, SRV_SPACE_GBUFFER);
Texture2D GBuffer3 : register(t4, SRV_SPACE_GBUFFER);
Texture2D GBuffer4 : register(t5, SRV_SPACE_GBUFFER);

Texture2D<float4> AuroraNoise : register(t0);   // R/G: difference-cloud layers, B: curtain height, A: vertical offset
Texture2D<float4> AuroraRamp : register(t1);    // 256x1 vertical gradient LUT (rgb = color, a = height falloff)

cbuffer AuroraConstants : register(b0)
{
    float4 CenterInner;     // xyz = planet center in view space (meters), w = shell inner radius
    float4 PoleOuter;       // xyz = magnetic pole axis in view space (unit), w = shell outer radius
    float4 Tangent1;        // xyz = tangent basis vector 1 (unit, perpendicular to pole axis)
    float4 Tangent2;        // xyz = tangent basis vector 2 (unit, = pole x tangent1)
    float4 BandParams;      // x = sin(band lower lat), y = sin(band upper lat), z = feather (sin-space), w = unused
    float4 NoiseParams;     // x = layer1 UV tiling, y = layer2 UV tiling, z = threshold, w = contrast push
    float4 ScrollOffsets;   // xy = layer1 UV offset, zw = layer2 UV offset (precomputed from time)
    float4 ColumnScroll;    // xy = column layer UV offset, z = column layer UV tiling, w = unused
    float4 ColorIntensity;  // rgb = HDR tint, w = master intensity
    float4 StepParams;      // x = step count, y = dither strength, z = fade factor (night x distance), w = curtain height variation
    float4 PatchScroll;     // xy = patch layer1 UV offset, zw = patch layer2 UV offset
    float4 PatchParams;     // x = patch layer1 UV tiling, y = patch layer2 UV tiling, z = threshold, w = feather
    float4 GroundParams;    // x = ground light intensity, y = curtain contrast exponent, zw = unused
};

// Ray/sphere intersection around CenterInner.xyz; returns (tNear, tFar) or (-1, -1) on miss.
float2 RaySphere(float3 origin, float3 dir, float radius)
{
    float3 oc = origin - CenterInner.xyz;
    float b = dot(oc, dir);
    float c = dot(oc, oc) - radius * radius;
    float disc = b * b - c;
    if (disc < 0)
        return float2(-1, -1);
    float s = sqrt(disc);
    return float2(-b - s, -b + s);
}

// Difference-clouds curtain shape (verbatim math from the reference implementation).
float CurtainNoise(float2 uv1, float2 uv2)
{
    float a = AuroraNoise.SampleLevel(WrapNoAnisoSampler, uv1, 0).r;
    float b = AuroraNoise.SampleLevel(WrapNoAnisoSampler, uv2, 0).g;
    float noise = abs(a - b);
    noise = (noise - NoiseParams.z) * NoiseParams.w + NoiseParams.z;
    return 1 - saturate(noise);
}

// Latitude band mask (both hemispheres); takes abs(sin(latitude)).
float BandMask(float sinLat)
{
    float feather = BandParams.z;
    return smoothstep(BandParams.x - feather, BandParams.x + feather, sinLat)
         * (1 - smoothstep(BandParams.y - feather, BandParams.y + feather, sinLat));
}

// Structural visibility: only parts of the aurora are lit at any one time. Two slowly
// counter-scrolling macro-scale noise layers gate the emission, so lit patches grow,
// split and vanish in place instead of just drifting along with the curtains.
float PatchMask(float2 uvBase)
{
    float a = AuroraNoise.SampleLevel(WrapNoAnisoSampler, uvBase * PatchParams.x + PatchScroll.xy, 0).r;
    float b = AuroraNoise.SampleLevel(WrapNoAnisoSampler, uvBase * PatchParams.y + PatchScroll.zw, 0).g;
    return smoothstep(PatchParams.z - PatchParams.w, PatchParams.z + PatchParams.w, (a + b) * 0.5);
}

// Cheap screen-space hash for march-start dithering.
float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float3 __PixelShader(VertexOut pin) : SV_Target
{
    float intensity = ColorIntensity.w * StepParams.z;
    if (intensity <= 0)
    {
        discard;
        return 0;
    }

    // View-space ray of this pixel. The screen coordinate is the integer pixel like the
    // game's G-buffer readers use; ScreenToUV centers it and applies the jitter offset.
    uint2 pixel = (uint2)pin.PosScreen.xy;
    float2 uv = ScreenToUV(pixel);
    float3 screenRay = ComputeScreenRay(uv);
    float3 rayDir = normalize(screenRay);

    float innerR = CenterInner.w;
    float outerR = PoleOuter.w;

    // The ray's intersection with the shell is the outer sphere's interval minus the inner
    // sphere's. A ray that dips through the hollow under the shell and comes back out is
    // left with two disjoint segments; both have to be marched. Dropping the far one puts a
    // hard edge along the inner sphere's tangent, because a ray just missing that sphere
    // keeps its whole chord while its neighbour is cut at the entry point.
    float2 outerT = RaySphere(0, rayDir, outerR);
    if (outerT.y <= 0)
    {
        discard;
        return 0;
    }
    float tMin = max(outerT.x, 0);
    float tMax = outerT.y;

    // Scene depth occlusion: terrain and ships cut the march short.
    float hwDepth = DepthBuffer[pixel];
    bool foreground = IsForeground(hwDepth);
    float3 scenePos = 0;
    if (foreground)
    {
        scenePos = ComputeZDepth(hwDepth) * screenRay;
        tMax = min(tMax, length(scenePos));
    }

    // Ambient ground light: aurora-tinted light painted onto the terrain below the
    // shell, following the glow overhead. Approximated as hemispherical sky light
    // modulated by the G-buffer albedo, so bright surfaces (snow) pick up most of it.
    // Computed before the march early-outs because a ground pixel under the shell
    // usually leaves no shell segment to march and must still receive its light.
    float3 ground = 0;
    if (GroundParams.x > 0 && foreground)
    {
        float3 q = scenePos - CenterInner.xyz;
        float rq = length(q);
        if (rq < innerR)
        {
            float3 up = q / rq;
            float glow = BandMask(abs(dot(up, PoleOuter.xyz)));
            if (glow > 0)
            {
                float2 uvGround = float2(dot(up, Tangent1.xyz), dot(up, Tangent2.xyz));
                // The curtain pattern skips the contrast exponent: the ground sees the
                // whole sky dome, so its light is softer than the curtains themselves.
                glow *= PatchMask(uvGround);
                glow *= CurtainNoise(uvGround * NoiseParams.x + ScrollOffsets.xy,
                                     uvGround * NoiseParams.y + ScrollOffsets.zw);
            }
            if (glow > 0)
            {
                GBufferData gBuffer;
                UnpackGBuffer(GBuffer0[pixel], GBuffer1[pixel], GBuffer2[pixel], GBuffer3[pixel], GBuffer4[pixel], gBuffer);
                float3 albedo = gBuffer.Color * (1 - gBuffer.Metal);
                // Hemispherical sky visibility: level ground gets the full glow,
                // slopes get less, faces pointing away from the sky get none.
                float skyVisibility = saturate(dot(gBuffer.ViewNormal, up) * 0.5 + 0.5);
                float3 glowColor = AuroraRamp.SampleLevel(LinearSampler, float2(0.15, 0.5), 0).rgb;
                ground = albedo * glowColor * (glow * skyVisibility * GroundParams.x);
            }
        }
    }

    if (tMax <= tMin)
        return ground * intensity;

    float seg0Start = tMin, seg0End = tMax;
    float seg1Start = 0, seg1End = 0;

    float2 innerT = RaySphere(0, rayDir, innerR);
    if (innerT.y > tMin && innerT.x < tMax)
    {
        seg0End = clamp(innerT.x, tMin, tMax);
        seg1Start = clamp(innerT.y, tMin, tMax);
        seg1End = tMax;
    }

    float len0 = max(seg0End - seg0Start, 0);
    float len1 = max(seg1End - seg1Start, 0);
    float marchLength = len0 + len1;
    if (marchLength <= 0)
        return ground * intensity;

    int steps = (int)StepParams.x;
    float stepLen = marchLength / steps;

    // Jitter the march start to hide banding at low step counts.
    float jitter = Hash21(pin.PosScreen.xy) * StepParams.y;

    float shellThickness = outerR - innerR;
    float heightVariation = StepParams.w;
    float3 accum = 0;

    [loop]
    for (int i = 0; i < steps; i++)
    {
        // Step along the two segments as one continuous arc length, so the step count and
        // therefore the cost stay fixed no matter how the ray meets the shell.
        float s = (i + jitter) * stepLen;
        float t = (s < len0) ? (seg0Start + s) : (seg1Start + (s - len0));

        float3 p = rayDir * t - CenterInner.xyz;   // position relative to planet center
        float r = length(p);
        float3 dir = p / r;

        float bandMask = BandMask(abs(dot(dir, PoleOuter.xyz)));
        if (bandMask <= 0)
            continue;

        // Azimuthal (tangent plane) coordinates around the pole axis. A longitude/latitude
        // parameterisation would compress U as the meridians converge, smearing the noise
        // into radial spokes over the pole, and needs a seam at +-pi; this has neither.
        float2 uvBase = float2(dot(dir, Tangent1.xyz), dot(dir, Tangent2.xyz));
        float2 uv1 = uvBase * NoiseParams.x + ScrollOffsets.xy;
        float2 uv2 = uvBase * NoiseParams.y + ScrollOffsets.zw;

        float patchMask = PatchMask(uvBase);
        if (patchMask <= 0)
            continue;

        float curtain = CurtainNoise(uv1, uv2);
        if (curtain <= 0)
            continue;

        // Per-column curtain height and vertical offset from the extra noise channels. The
        // offset must come pre-scaled from the constant buffer: rescaling uv1 here would
        // rescale the [0, 1) wrap baked into its offset, and every wrap would then jump the
        // height of every column at once.
        float4 columnNoise = AuroraNoise.SampleLevel(WrapNoAnisoSampler, uvBase * ColumnScroll.z + ColumnScroll.xy, 0);
        float columnHeight = lerp(1 - heightVariation, 1, columnNoise.b);
        float columnOffset = columnNoise.a * heightVariation * 0.5;

        // Normalized altitude within the shell, remapped by the column shape.
        float h = (r - innerR) / shellThickness;
        float hRemapped = (h - columnOffset) / max(columnHeight, 1e-3);
        if (hRemapped < 0 || hRemapped > 1)
            continue;

        float4 ramp = AuroraRamp.SampleLevel(LinearSampler, float2(hRemapped, 0.5), 0);
        // The contrast exponent leaves a fully lit curtain at 1 and pushes everything
        // below it down, so raising it darkens the haze between the curtains without
        // dimming their cores. Brightness is then the intensity's job alone.
        float emission = pow(curtain, GroundParams.y);
        accum += ramp.rgb * (ramp.a * emission * bandMask * patchMask);
    }

    // Accumulated emission per unit of shell thickness. A ray crossing the shell near the
    // tangent travels many times its thickness, so the raw integral is unbounded; saturate
    // it exponentially to keep the brightest curtains inside the tint's HDR range instead
    // of flooding the sky when the camera sits under the shell.
    float3 optical = accum * (stepLen / shellThickness);
    float3 color = ColorIntensity.rgb * intensity * (1 - exp(-optical));
    return color + ground * intensity;   // additive blend into the atmosphere buffer
}

#endif
