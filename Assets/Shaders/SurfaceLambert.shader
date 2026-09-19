// ObjViewer/SurfaceLambert — 顶点色 + 半兰伯特 + 边缘光，Built-in RP ForwardBase
Shader "ObjViewer/SurfaceLambert"
{
    Properties
    {
        _Color      ("Tint", Color) = (1,1,1,1)
        _Emission   ("Emission", Color) = (0,0,0,1)
        _RimColor   ("Rim Color", Color) = (0.3,0.8,1,1)
        _RimPower   ("Rim Power", Range(0.5,8)) = 3
        _SpecColor2 ("Specular", Range(0,1)) = 0.2
        _Smoothness ("Smoothness", Range(0,1)) = 0.4
        _AmbientBoost ("Ambient Boost", Range(0,2)) = 0.65
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Back

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos        : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                fixed4 color      : COLOR;
            };

            fixed4 _Color;
            fixed4 _Emission;
            fixed4 _RimColor;
            float  _RimPower;
            float  _SpecColor2;
            float  _Smoothness;
            float  _AmbientBoost;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos         = UnityObjectToClipPos(v.vertex);
                o.worldPos    = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.color       = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float3 v = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 h = normalize(l + v);

                float diff      = dot(n, l) * 0.5 + 0.5;                    // 半兰伯特，暗部不发黑
                float spec      = pow(saturate(dot(n, h)), _Smoothness * 120.0 + 8.0) * _SpecColor2;
                float rim       = pow(1.0 - saturate(dot(n, v)), _RimPower) * 0.6;
                float3 ambient  = UNITY_LIGHTMODEL_AMBIENT.rgb * _AmbientBoost;

                fixed4 albedo   = i.color * _Color;
                float3 lightCol = _LightColor0.rgb;

                float3 col = albedo.rgb * (ambient + lightCol * diff)
                           + lightCol * spec
                           + rim * _RimColor.rgb
                           + _Emission.rgb;

                // 轻微 ACES 风格压缩，避免过曝
                col = col / (col + 0.85) * 1.85;

                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }

    FallBack "Diffuse"
}
