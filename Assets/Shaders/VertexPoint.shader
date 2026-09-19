// ObjViewer/VertexPoint — 顶点云小方块用，颜色按世界空间高度渐变
// 配合 Graphics.DrawMeshInstanced 使用（支持 GPU Instancing）
Shader "ObjViewer/VertexPoint"
{
    Properties
    {
        _ColorA    ("Low Color",  Color) = (0.15,0.85,1,1)
        _ColorB    ("High Color", Color) = (1,0.45,0.15,1)
        _HeightMin ("Height Min", Float) = 0
        _HeightMax ("Height Max", Float) = 1
        _Glow      ("Glow", Range(0,2)) = 0.55
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos        : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                fixed4 color      : COLOR;
            };

            fixed4 _ColorA;
            fixed4 _ColorB;
            float  _HeightMin;
            float  _HeightMax;
            float  _Glow;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);

                float4 worldPos = mul(unity_ObjectToWorld, v.vertex);
                o.pos           = mul(UNITY_MATRIX_VP, worldPos);
                o.worldNormal   = UnityObjectToWorldNormal(v.normal);

                float h = saturate((worldPos.y - _HeightMin) / max(1e-4, _HeightMax - _HeightMin));
                o.color = lerp(_ColorA, _ColorB, h);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float  d = saturate(dot(n, l) * 0.6 + 0.55);
                float3 col = i.color.rgb * (d + _Glow);
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }

    FallBack "Diffuse"
}
