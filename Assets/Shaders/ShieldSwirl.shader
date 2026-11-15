// Swirly, sparkly energy shield for URP. Fully procedural (no textures):
//  - Fresnel rim: edges glow brighter than the middle, which sells "bubble".
//  - Swirl: two sets of twisting bands rotating around the sphere at different speeds.
//  - Sparkles: random points on the surface that twinkle on and off.
// Additive blending, so it glows (and blooms) on top of whatever is behind it.
Shader "Custom/ShieldSwirl"
{
    Properties
    {
        _BaseColor ("Color (alpha = overall strength)", Color) = (0.5, 0.85, 1, 0.35)
        _RimColor ("Rim Color", Color) = (0.75, 0.95, 1, 1)
        _RimPower ("Rim Sharpness", Range(0.5, 8)) = 3
        _Intensity ("Glow Intensity", Float) = 2

        _SwirlSpeed ("Swirl Speed", Float) = 1.5
        _SwirlBands ("Swirl Bands", Float) = 6
        _SwirlTwist ("Swirl Twist", Float) = 4
        _SwirlStrength ("Swirl Strength", Range(0, 2)) = 0.9

        _SparkleDensity ("Sparkle Density", Float) = 28
        _SparkleAmount ("Sparkle Amount", Range(0, 1)) = 0.35
        _SparkleSpeed ("Sparkle Twinkle Speed", Float) = 6
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ShieldSwirl"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha One   // additive glow
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _RimColor;
                float _RimPower;
                float _Intensity;
                float _SwirlSpeed;
                float _SwirlBands;
                float _SwirlTwist;
                float _SwirlStrength;
                float _SparkleDensity;
                float _SparkleAmount;
                float _SparkleSpeed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 viewDirWS  : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewDirWS = GetWorldSpaceViewDir(positionWS);
                o.positionOS = v.positionOS.xyz;
                return o;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewDirWS);

                // 1) Fresnel rim
                float fresnel = pow(1.0 - saturate(dot(n, v)), _RimPower);

                // Position on the sphere as longitude/latitude, so patterns wrap around it.
                float3 p = normalize(i.positionOS);
                float lon = atan2(p.z, p.x);   // -PI..PI around the equator
                float lat = p.y;               // -1..1 bottom to top
                float t = _Time.y * _SwirlSpeed;

                // 2) Swirl: bands that twist as they go up, spinning over time.
                //    Whole-number band counts keep the pattern seamless where longitude wraps.
                float bands = round(_SwirlBands);
                float s1 = sin(lon * bands + lat * _SwirlTwist * PI + t * 2.0);
                float s2 = sin(lon * round(bands * 0.5) - lat * _SwirlTwist * 2.0 - t * 1.3);
                float swirl = pow(saturate(s1 * 0.5 + 0.5), 4.0) + 0.6 * pow(saturate(s2 * 0.5 + 0.5), 6.0);
                swirl *= _SwirlStrength;

                // 3) Sparkles: split the surface into a grid, give some cells a twinkling star.
                float2 uv = float2(lon / (2.0 * PI) + 0.5 + _Time.y * 0.03, lat * 0.5 + 0.5);
                float2 grid = uv * float2(_SparkleDensity * 2.0, _SparkleDensity);
                float2 cell = floor(grid);
                float2 local = frac(grid) - 0.5;
                float h = Hash21(cell);
                float hasStar = step(1.0 - _SparkleAmount, h);
                float twinkle = pow(saturate(sin(_Time.y * _SparkleSpeed * (0.5 + h) + h * 60.0)), 8.0);
                float star = hasStar * twinkle * smoothstep(0.25, 0.0, length(local));

                // Combine: tinted glow body + brighter rim + swirl, with white sparkles on top.
                float strength = _BaseColor.a;
                float glow = (0.15 + fresnel * 1.6 + swirl) * strength;
                float3 color = lerp(_BaseColor.rgb, _RimColor.rgb, saturate(fresnel + swirl * 0.5)) * glow * _Intensity;
                color += star * 3.0 * saturate(strength * 3.0);

                return half4(color, 1.0); // additive: color is what gets added
            }
            ENDHLSL
        }
    }
}
