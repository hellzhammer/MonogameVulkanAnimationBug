#if OPENGL
#define SV_POSITION POSITION
#define SV_TARGET COLOR0
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
    // Fallback for DirectX/Vulkan profiles if you switch back
#define SV_POSITION SV_Position
#define SV_TARGET SV_Target
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

matrix World;
matrix View;
matrix Projection;

matrix Bones[72];
//float Time;

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float3 Normal : NORMAL0;
    float2 TexCoord : TEXCOORD0;
    
    // Catching the re-routed C# texture coordinate data
    float4 BlendIndices : TEXCOORD1;
    float4 BlendWeights : TEXCOORD2;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
};

VertexShaderOutput MainVS(in VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput) 0;
    
    // Cast float indices to integers for matrix array lookup
    int4 indices = (int4) input.BlendIndices;

    matrix skinTransform =
        Bones[indices.x] * input.BlendWeights.x +
        Bones[indices.y] * input.BlendWeights.y +
        Bones[indices.z] * input.BlendWeights.z +
        Bones[indices.w] * input.BlendWeights.w;

    float weightSum = input.BlendWeights.x + input.BlendWeights.y + input.BlendWeights.z + input.BlendWeights.w;
    
    float4 skinPosition = (weightSum > 0.001f) ? mul(input.Position, skinTransform) : input.Position;
    float3 skinNormal = (weightSum > 0.001f) ? mul(input.Normal, (float3x3) skinTransform) : input.Normal;
    skinNormal = normalize(skinNormal);

    float4 worldPosition = mul(skinPosition, World);
    float4 viewPosition = mul(worldPosition, View);
    output.Position = mul(viewPosition, Projection);

    float3 worldNormal = normalize(mul(skinNormal, (float3x3) World));
    float3 lightDirection = normalize(float3(-1.0f, -1.0f, 0.5f));
    float lightIntensity = max(0.25f, dot(worldNormal, -lightDirection));
    
    float3 baseColor = float3(0.7f, 0.7f, 0.7f);
    output.Color = float4(baseColor * lightIntensity, 1.0f);

    return output;
}

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
    return input.Color;
}

technique Skinning
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}