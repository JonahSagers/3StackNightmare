// Blit shader used by PauseToggle: lerps a texture toward its luminance by _Amount, then scales by _Brightness.
// Lives in Resources so Shader.Find can locate it in builds.
Shader "Hidden/PauseDesaturate"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Amount ("Desaturation", Range(0, 1)) = 0
        _Brightness ("Brightness", Range(0, 1)) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Amount;
            float _Brightness;

            fixed4 frag (v2f_img i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float lum = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
                c.rgb = lerp(c.rgb, lum.xxx, _Amount) * _Brightness;
                return c;
            }
            ENDCG
        }
    }
}
