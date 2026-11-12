// Softbody smoke (0.27.0): how the renderer fork draws the GPU gas's puffs (and the liquid's spray, foam and bubbles) in place of
// Resonite's Unlit. The SoftbodyShaders Unity project (2019.4) builds it into an AssetBundle with its noise and two materials, and
// SoftbodyFluidHost loads that bundle; without it the fork draws the old discs with the plugin's Unlit material.
//
// Each puff is a quad of 4 vertices that all hold its centre:
//   POSITION   its centre (the gas's space: the plugin's placeholder slot)
//   NORMAL     x its radius, y how much of the world's main light reaches it through the smoke, z how much of the sky reaches it
//              (both worked out on the GPU from the smoke's own density: FluidSim.hlsl, GasDrawQuads)
//   COLOR      its colour (linear) and opacity
//   TEXCOORD0  xy the corner (-1 or 1), z a random number of its own, w its age as a share of its life
// The vertex shader turns the quad to face each eye (both eyes, mirrors and cameras see it right) and turns it by its own angle. The
// pixel shader makes it a soft round puff broken into wisps by a 3D noise that changes over its life, shades it as a soft ball (the
// light wraps round it; seen against the light its thin parts glow), darkens it by the smoke between it and the light and the sky,
// and fades it where it meets a surface (when the camera has a depth texture: _SoftbodyDepthOK, set per camera by the fork) and right
// next to the eye. ForwardBase: the main directional light, the sky (spherical harmonics) and up to 4 vertex lights; ForwardAdd: the
// other per-pixel lights (point, spot, more directional ones), added on.
Shader "Softbody/Smoke"
{
    Properties
    {
        [NoScaleOffset] _NoiseTex ("Wisp noise (3D)", 3D) = "" {}
        _NoiseAmount ("Wisps", Range(0, 1)) = 0.65
        _NoiseScale ("Wisp size", Range(0.2, 3)) = 1
        _NoiseEvolve ("Wisps change over a life", Range(0, 4)) = 1.5
        _Spin ("Turns over a life", Range(0, 1)) = 0.1
        _Edge ("Edge falloff", Range(0.5, 4)) = 1.5
        _Density ("Opacity", Range(0.2, 4)) = 1.3
        _Roundness ("Roundness", Range(0, 1)) = 0.6
        _Wrap ("Light wrap", Range(0, 1)) = 0.5
        _Forward ("Glow against the light", Range(0, 0.95)) = 0.6
        _ForwardMix ("Glow share", Range(0, 1)) = 0.35
        _Shadow ("Self-shadow", Range(0, 1)) = 1
        _Ambient ("Sky light", Range(0, 2)) = 1
        _SoftEdge ("Soft edge at surfaces (radii)", Range(0.05, 2)) = 0.6
        _NearFade ("Fade next to the eye (m)", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "Lighting.cginc"
        #include "AutoLight.cginc"

        sampler3D _NoiseTex;
        float _NoiseAmount, _NoiseScale, _NoiseEvolve, _Spin, _Edge, _Density, _Roundness, _Wrap, _Forward, _ForwardMix;
        float _Shadow, _Ambient, _SoftEdge, _NearFade;
        float _SoftbodyDepthOK;   // 1 while the camera rendering renders a depth texture (set by the fork before each camera)
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 color : COLOR;
            float4 texcoord : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            float4 color : COLOR;        // rgb its colour, a its opacity (after the fade next to the eye)
            float4 uv : TEXCOORD0;       // xy the corner, z its random number, w its age (share of its life)
            float4 shade : TEXCOORD1;    // x the main light reaching it, y the sky reaching it, z its radius (world), w unused
            float3 worldPos : TEXCOORD2;
            float3 right : TEXCOORD3;    // the quad's axes in world space (turned by its angle) and its normal (towards this eye),
            float3 up : TEXCOORD4;       // for the round normal
            float3 toEye : TEXCOORD5;
            float4 projPos : TEXCOORD6;  // screen position, z the eye depth (for the soft edge)
            float3 vlight : TEXCOORD7;   // ForwardBase: the vertex lights
            UNITY_FOG_COORDS(8)
            UNITY_VERTEX_OUTPUT_STEREO
        };

        // smoke scatters light every way: the vertex lights count by their falloff alone (half: the wrap's mean)
        float3 SmokeVertexLights(float3 p)
        {
            float4 toX = unity_4LightPosX0 - p.x, toY = unity_4LightPosY0 - p.y, toZ = unity_4LightPosZ0 - p.z;
            float4 d2 = max(toX * toX + toY * toY + toZ * toZ, 1e-6);
            float4 a = 1.0 / (1.0 + d2 * unity_4LightAtten0);
            return 0.5 * (unity_LightColor[0].rgb * a.x + unity_LightColor[1].rgb * a.y + unity_LightColor[2].rgb * a.z + unity_LightColor[3].rgb * a.w);
        }

        v2f vert(appdata v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_OUTPUT(v2f, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 centre = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;
            float scale = length(float3(unity_ObjectToWorld[0].x, unity_ObjectToWorld[1].x, unity_ObjectToWorld[2].x));
            float radius = max(v.normal.x, 1e-4) * scale;
            // facing this eye (_WorldSpaceCameraPos and UNITY_MATRIX_V are each eye's in single-pass stereo), upright to its view
            float3 toEye = _WorldSpaceCameraPos - centre;
            float dist = length(toEye);
            toEye = dist > 1e-5 ? toEye / dist : -UNITY_MATRIX_V[2].xyz;
            float3 right = cross(UNITY_MATRIX_V[1].xyz, toEye);
            float rl = length(right);
            right = rl > 1e-4 ? right / rl : UNITY_MATRIX_V[0].xyz;
            float3 up = cross(toEye, right);
            // turned by its own angle, and on over its life (either way)
            float ang = 6.2831853 * (v.texcoord.z + v.texcoord.w * _Spin * (frac(v.texcoord.z * 7.31) * 2 - 1));
            float s, c;
            sincos(ang, s, c);
            float3 r2 = right * c + up * s, u2 = up * c - right * s;
            float3 wp = centre + (r2 * v.texcoord.x + u2 * v.texcoord.y) * radius;
            o.pos = UnityWorldToClipPos(wp);
            o.worldPos = wp;
            o.right = r2; o.up = u2; o.toEye = toEye;
            o.uv = v.texcoord;
            // fades out as the eye comes into it (walking through smoke: no wall of puffs at the near plane)
            float nearFade = saturate((dist - 0.25 * radius) / max(_NearFade + 0.5 * radius, 1e-3));
            o.color = float4(v.color.rgb, v.color.a * nearFade);
            o.shade = float4(lerp(1, saturate(v.normal.y), _Shadow), saturate(v.normal.z), radius, 0);
            o.projPos = ComputeScreenPos(o.pos);
            o.projPos.z = -mul(UNITY_MATRIX_V, float4(wp, 1)).z;
            o.vlight = 0;
            #ifdef VERTEXLIGHT_ON
            o.vlight = SmokeVertexLights(centre);
            #endif
            UNITY_TRANSFER_FOG(o, o.pos);
            return o;
        }

        // the puff's opacity at this pixel (round, wispy, soft at surfaces) and its round normal; false: nothing to draw here
        bool Puff(v2f i, out float alpha, out float dens, out float3 n)
        {
            float2 uv = i.uv.xy;
            float r2 = dot(uv, uv);
            float profile = pow(saturate(1 - r2), _Edge);
            // two scales of 3D noise; the third axis is its life, so the wisps change as it ages
            float3 nc = float3(uv * (0.35 * _NoiseScale) + i.uv.z * float2(17.31, 5.13), i.uv.z * 3.7 + i.uv.w * _NoiseEvolve * 0.5);
            float4 nz = tex3D(_NoiseTex, nc);
            float wisp = saturate(nz.r * 0.7 + nz.g * 0.45 - 0.05);
            dens = profile * lerp(1, wisp * 1.6, _NoiseAmount);
            alpha = saturate(i.color.a * dens * _Density);
            // (read whether or not this camera has a depth texture: no texture read inside a branch)
            float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.projPos)));
            float soft = saturate((scene - i.projPos.z) / max(i.shade.z * _SoftEdge, 1e-3));
            alpha *= _SoftbodyDepthOK > 0.5 ? soft : 1;   // (a select: whatever an unrendered depth texture holds never gets in)
            float z = sqrt(saturate(1 - r2));
            n = normalize((i.right * uv.x + i.up * uv.y) * _Roundness + i.toEye * (z * _Roundness + 1 - _Roundness));
            return r2 < 1 && alpha >= 1.0 / 255.0;
        }

        // how one light shades the smoke here: wrapped round the soft ball, and (seen against the light) a glow through its thin parts
        float SmokeLight(float3 n, float3 toLight, float3 toEye, float dens)
        {
            float diff = saturate((dot(n, toLight) + _Wrap) / (1 + _Wrap));
            float g = _Forward;
            float cosT = dot(-toLight, toEye);
            float hg = (1 - g * g) / pow(max(1 + g * g - 2 * g * cosT, 1e-4), 1.5);
            return lerp(diff, min(hg, 4) * (1 - 0.5 * saturate(dens)), _ForwardMix);
        }
        ENDCG

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode"="ForwardBase" }
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBase
            #pragma target 3.5
            #pragma multi_compile _ VERTEXLIGHT_ON
            #pragma multi_compile_fog

            float4 fragBase(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float alpha, dens; float3 n;
                if (!Puff(i, alpha, dens, n)) discard;
                float3 toEye = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 toLight = _WorldSpaceLightPos0.xyz;   // the main directional light (none: its colour is black)
                float3 light = _LightColor0.rgb * (i.shade.x * SmokeLight(n, toLight, toEye, dens));
                float3 sky = max(ShadeSH9(float4(n, 1)), 0) * (i.shade.y * _Ambient);
                float3 col = i.color.rgb * (light + sky + i.vlight * lerp(1, i.shade.y, 0.5));
                UNITY_APPLY_FOG(i.fogCoord, col);
                return float4(col * alpha, alpha);
            }
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode"="ForwardAdd" }
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAdd
            #pragma target 3.5
            #pragma multi_compile_fwdadd
            #pragma multi_compile_fog

            float4 fragAdd(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float alpha, dens; float3 n;
                if (!Puff(i, alpha, dens, n)) discard;
                float3 toEye = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 toLight = normalize(UnityWorldSpaceLightDir(i.worldPos));
                UNITY_LIGHT_ATTENUATION(atten, 0, i.worldPos);
                // (no shadow march towards this light: the smoke around it dims it by its sky and main-light shading, half way)
                float occl = lerp(1, min(i.shade.x, i.shade.y), 0.5);
                float3 col = i.color.rgb * _LightColor0.rgb * (atten * occl * SmokeLight(n, toLight, toEye, dens));
                UNITY_APPLY_FOG_COLOR(i.fogCoord, col, float4(0, 0, 0, 0));
                return float4(col * alpha, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
