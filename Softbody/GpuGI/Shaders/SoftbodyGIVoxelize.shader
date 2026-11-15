// Live bounce light (GI) for the renderer fork: draws a slab of the world into voxels. The fork renders the world with this as a
// replacement shader (Camera.RenderWithShader, tag RenderType) from a camera around the slab; each renderer keeps its own
// material's colour, texture and glow. Every triangle is laid flat along the axis it faces most (geometry shader) and each pixel
// adds its colour, glow and normal into the voxel it falls in (the slab buffer, 10 uints a voxel: count, albedo rgb * 255,
// emission rgb * 256, normal xyz * 64 as int bits). SoftbodyGI.hlsl's Resolve averages them into the voxel grid.
//
// The target is a small R8 texture that is never written (ColorMask 0): _SoftbodyGISlabDim.w voxels across, drawn 2 pixels a voxel
// so thin parts are not missed. Clip distances keep only what is inside the slab.
Shader "Hidden/Softbody/GIVoxelize"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _Tex ("", 2D) = "white" {}
        _Color ("", Color) = (1, 1, 1, 1)
        _EmissionMap ("", 2D) = "white" {}
        _EmissionColor ("", Color) = (0, 0, 0, 0)
        _Cutoff ("", Float) = 0.5
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    // Resonite's lit shaders use _MainTex; its Unlit uses _Tex. A material has one of them; the other is the default white.
    sampler2D _MainTex; float4 _MainTex_ST;
    sampler2D _Tex; float4 _Tex_ST;
    sampler2D _EmissionMap; float4 _EmissionMap_ST;
    float4 _Color;
    float4 _EmissionColor;
    float _Cutoff;

    float4 _SoftbodyGISlabMin;   // xyz: the slab's world corner; w: 1 / voxel size
    float4 _SoftbodyGISlabDim;   // xyz: the slab's voxels; w: the target's side, in voxels
    RWStructuredBuffer<uint> _SoftbodyGIAccum : register(u1);

    struct appdata
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float2 uv : TEXCOORD0;
    };

    struct v2g
    {
        float3 v : TEXCOORD0;    // position in voxels from the slab's corner
        float3 n : TEXCOORD1;    // world normal
        float2 uv : TEXCOORD2;
    };

    struct g2f
    {
        float4 pos : SV_Position;
        float3 v : TEXCOORD0;
        float3 n : TEXCOORD1;
        float2 uv : TEXCOORD2;
        float4 clip0 : SV_ClipDistance0;
        float2 clip1 : SV_ClipDistance1;
    };

    v2g vert(appdata i)
    {
        v2g o;
        float3 w = mul(unity_ObjectToWorld, float4(i.vertex.xyz, 1.0)).xyz;
        o.v = (w - _SoftbodyGISlabMin.xyz) * _SoftbodyGISlabMin.w;
        o.n = UnityObjectToWorldNormal(i.normal);
        o.uv = i.uv;
        return o;
    }

    [maxvertexcount(3)]
    void geom(triangle v2g i[3], inout TriangleStream<g2f> s)
    {
        float3 c = cross(i[1].v - i[0].v, i[2].v - i[0].v);
        float3 fn = abs(c);
        float3 face = c / max(length(c), 1e-9);
        int axis = fn.x >= fn.y && fn.x >= fn.z ? 0 : (fn.y >= fn.z ? 1 : 2);
        [unroll] for (int k = 0; k < 3; k++)
        {
            g2f o;
            float3 v = i[k].v;
            float2 p = axis == 0 ? v.yz : (axis == 1 ? v.zx : v.xy);
            o.pos = float4(p / _SoftbodyGISlabDim.w * 2.0 - 1.0, 0.5, 1.0);
            o.v = v;
            // a mesh without normals: the face's own (its side is a guess)
            o.n = dot(i[k].n, i[k].n) > 1e-4 ? i[k].n : face;
            o.uv = i[k].uv;
            o.clip0 = float4(v.x, _SoftbodyGISlabDim.x - v.x, v.y, _SoftbodyGISlabDim.y - v.y);
            o.clip1 = float2(v.z, _SoftbodyGISlabDim.z - v.z);
            s.Append(o);
        }
    }

    // A replacement shader gets the drawn material's values. A texture that material's shader does not have is Unity's grey (0.21
    // in linear colour: white things came out at a fifth of their colour, editor test 2026-11-14), and its _ST is zero, while a
    // texture the shader has gets a scale (1, 1 unless set). So a texture only counts when its _ST says the material has it.
    bool Has(float4 st) { return any(st.xy != 0.0); }

    void Add(g2f i, float4 albedo)
    {
        float3 n = i.n / max(length(i.n), 1e-6);
        // A face right on a voxel boundary (a floor at y = 0, a wall at a round metre: most worlds) would land in the voxels on
        // both sides by rounding, mixing a thin wall's front and back at random; a hundredth of a voxel toward the side it faces
        // puts it in the voxel in front of it.
        int3 c = (int3)floor(i.v + n * 0.01);
        int3 d = (int3)_SoftbodyGISlabDim.xyz;
        if (any(c < 0) || any(c >= d)) return;
        float3 emission = _EmissionColor.rgb;
        if (Has(_EmissionMap_ST)) emission *= tex2D(_EmissionMap, TRANSFORM_TEX(i.uv, _EmissionMap)).rgb;
        uint a = (((uint)c.z * (uint)d.y + (uint)c.y) * (uint)d.x + (uint)c.x) * 10u;
        uint3 ai = (uint3)round(saturate(albedo.rgb) * 255.0);
        uint3 ei = (uint3)round(clamp(emission, 0.0, 255.0) * 256.0);
        int3 ni = (int3)round(n * 64.0);
        InterlockedAdd(_SoftbodyGIAccum[a], 1u);
        InterlockedAdd(_SoftbodyGIAccum[a + 1u], ai.r);
        InterlockedAdd(_SoftbodyGIAccum[a + 2u], ai.g);
        InterlockedAdd(_SoftbodyGIAccum[a + 3u], ai.b);
        InterlockedAdd(_SoftbodyGIAccum[a + 4u], ei.r);
        InterlockedAdd(_SoftbodyGIAccum[a + 5u], ei.g);
        InterlockedAdd(_SoftbodyGIAccum[a + 6u], ei.b);
        InterlockedAdd(_SoftbodyGIAccum[a + 7u], asuint(ni.x));
        InterlockedAdd(_SoftbodyGIAccum[a + 8u], asuint(ni.y));
        InterlockedAdd(_SoftbodyGIAccum[a + 9u], asuint(ni.z));
    }

    float4 Albedo(g2f i)
    {
        // a colour the material does not have is (0, 0, 0, 0): white then
        float4 a = any(_Color != 0.0) ? _Color : 1.0;
        if (Has(_MainTex_ST)) a *= tex2D(_MainTex, TRANSFORM_TEX(i.uv, _MainTex));
        if (Has(_Tex_ST)) a *= tex2D(_Tex, TRANSFORM_TEX(i.uv, _Tex));
        return a;
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always ColorMask 0
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            float4 frag(g2f i) : SV_Target
            {
                Add(i, Albedo(i));
                return 0;
            }
            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always ColorMask 0
            CGPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            float4 frag(g2f i) : SV_Target
            {
                float4 a = Albedo(i);
                clip(a.a - _Cutoff);
                Add(i, a);
                return 0;
            }
            ENDCG
        }
    }

    Fallback Off
}
