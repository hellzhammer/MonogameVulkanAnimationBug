#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0

// Combine ALL variables into a single explicit cbuffer to prevent the creation of $Globals. 
// Keep the single cbuffer structure to satisfy the Vulkan compiler limit
cbuffer MainBuffer
{
    matrix World;
    matrix View;
    matrix Projection;
    
    // Remove row_major. MonoGame's SetValue automatically transposes this.
    matrix Bones[72];
};

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float3 Normal : NORMAL0;
    float2 TexCoord : TEXCOORD0;
    
    // Restore exact semantic indices for Vulkan mapping
    float4 BlendIndices : BLENDINDICES0;
    float4 BlendWeights : BLENDWEIGHT0;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
};

VertexShaderOutput MainVS(in VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput) 0;
    
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

float4 MainPS(VertexShaderOutput input) : SV_Target
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