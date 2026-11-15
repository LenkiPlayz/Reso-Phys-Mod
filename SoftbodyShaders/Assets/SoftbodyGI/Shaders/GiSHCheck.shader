// The scene test's ambient check (GiShaderBuild.Scene): pixel x of an 8 x 1 target holds the shader constant x as Unity uploads it
// for an object lit by the world's ambient (unity_SHAr, SHAg, SHAb, SHBr, SHBg, SHBb, SHC), to compare with GiUnity.PackAmbient.
Shader "Hidden/SoftbodyGI/SHCheck"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f { float4 pos : SV_Position; };
            v2f vert(float4 v : POSITION) { v2f o; o.pos = UnityObjectToClipPos(v); return o; }
            float4 frag(v2f i) : SV_Target
            {
                int k = (int)floor(i.pos.x);
                if (k == 0) return unity_SHAr;
                if (k == 1) return unity_SHAg;
                if (k == 2) return unity_SHAb;
                if (k == 3) return unity_SHBr;
                if (k == 4) return unity_SHBg;
                if (k == 5) return unity_SHBb;
                if (k == 6) return unity_SHC;
                return 0;
            }
            ENDCG
        }
    }
}
